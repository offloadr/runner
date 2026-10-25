using Grpc.Core;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public class RuntimeActivityScopeTests
{
    private static RuntimeActivityObservation Observation()
        => RuntimeActivityObservation.Capture("session-1", new(2, 3, "33333333-3333-4333-8333-333333333333"),
            new() { MemoryWorkspace = true, EncryptedUploads = true, EncryptedReadsRequired = true });

    [Test]
    public void CaptureRetainsOnlyOriginalIdentityAndFlags()
    {
        var profile = new SessionSealingStatus { MemoryWorkspace = true, EncryptedUploads = true, EncryptedReadsRequired = true };
        var runtime = new RuntimeIdentity(2, 3, "33333333-3333-4333-8333-333333333333");
        var captured = RuntimeActivityObservation.Capture("session-1", runtime, profile);
        profile.MemoryWorkspace = false;
        profile.EncryptedUploads = false;
        profile.EncryptedReadsRequired = false;
        runtime = new(9, 10, Guid.NewGuid().ToString("n"));
        Assert.That(captured, Is.EqualTo(Observation()));
        Assert.That(runtime, Is.Not.EqualTo(captured.Runtime));
    }

    [Test]
    public async Task UnobservedScopeCancelsWithoutCallingTransport()
    {
        var sink = new RecordingSink((_, _) => Task.FromResult(RuntimeActivityOutcome.Applied));
        var scope = new RuntimeActivityScope(Observation(), sink, TimeSpan.FromSeconds(5));
        Assert.That(scope.Completion.IsCompleted, Is.False);
        await scope.DisposeAsync();
        scope.Signal();
        Assert.Multiple(() =>
        {
            Assert.That(scope.Completion.Result, Is.EqualTo(RuntimeActivityDisposition.Cancelled));
            Assert.That(sink.Observations, Is.Empty);
        });
    }

    [TestCase(RuntimeActivityOutcome.Applied, "Accepted")]
    [TestCase(RuntimeActivityOutcome.AlreadyAppliedExactly, "Accepted")]
    [TestCase(RuntimeActivityOutcome.ObsoleteOrPreempted, "Retired")]
    [TestCase(RuntimeActivityOutcome.InvalidIdentity, "Rejected")]
    [TestCase(RuntimeActivityOutcome.ConflictingState, "Rejected")]
    [TestCase(RuntimeActivityOutcome.Unspecified, "Rejected")]
    [TestCase((RuntimeActivityOutcome)1234, "Rejected")]
    public async Task ConcurrentSignalsShareOneRequiredFact(RuntimeActivityOutcome outcome, string expected)
    {
        var sink = new RecordingSink((_, _) => Task.FromResult(outcome));
        await using var scope = new RuntimeActivityScope(Observation(), sink, TimeSpan.FromSeconds(5));
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(scope.Signal)));
        Assert.That((await scope.Completion.WaitAsync(TimeSpan.FromSeconds(5))).ToString(), Is.EqualTo(expected));
        Assert.That(sink.Observations, Is.EqualTo(new[] { Observation() }));
    }

    [TestCase(StatusCode.Unavailable)]
    [TestCase(StatusCode.DeadlineExceeded)]
    [TestCase(StatusCode.ResourceExhausted)]
    [TestCase(StatusCode.Cancelled)]
    [TestCase(StatusCode.Unknown)]
    [TestCase(StatusCode.Internal)]
    public async Task TransientFailureAndNotReadyRetryOriginalMetadata(StatusCode code)
    {
        var calls = 0;
        var sink = new RecordingSink((_, _) => ++calls switch
        {
            1 => Task.FromException<RuntimeActivityOutcome>(new RpcException(new(code, "synthetic confidential detail"))),
            2 => Task.FromResult(RuntimeActivityOutcome.NotReady),
            _ => Task.FromResult(RuntimeActivityOutcome.AlreadyAppliedExactly)
        });
        await using var scope = new RuntimeActivityScope(Observation(), sink, TimeSpan.FromSeconds(5), retryDelay: TimeSpan.FromMilliseconds(1));
        scope.Signal();
        Assert.That(await scope.Completion.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(RuntimeActivityDisposition.Accepted));
        Assert.That(sink.Observations, Is.EqualTo(Enumerable.Repeat(Observation(), 3)));
    }

    [TestCase(StatusCode.Unauthenticated)]
    [TestCase(StatusCode.PermissionDenied)]
    [TestCase(StatusCode.InvalidArgument)]
    [TestCase(StatusCode.Unimplemented)]
    [TestCase(StatusCode.NotFound)]
    public async Task PermanentFailureCannotBecomeSuccessfulActivity(StatusCode code)
    {
        var sink = new RecordingSink((_, _) => Task.FromException<RuntimeActivityOutcome>(new RpcException(new(code, "synthetic confidential detail"))));
        await using var scope = new RuntimeActivityScope(Observation(), sink, TimeSpan.FromSeconds(5));
        scope.Signal();
        Assert.That(await scope.Completion.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(RuntimeActivityDisposition.Rejected));
        Assert.That(sink.Observations, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task DisposalCancelsAndJoinsOriginalTransportEvenWhenItIgnoresCancellation()
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RuntimeActivityOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingSink(async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult(token);
            return await release.Task.ConfigureAwait(false);
        });
        var scope = new RuntimeActivityScope(Observation(), sink, TimeSpan.FromSeconds(5));
        scope.Signal();
        var originalToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = scope.DisposeAsync().AsTask();
        var duplicate = scope.DisposeAsync().AsTask();
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That(originalToken.IsCancellationRequested, Is.True);
                Assert.That(disposal.IsCompleted, Is.False);
                Assert.That(duplicate.IsCompleted, Is.False);
                Assert.That(scope.Completion.IsCompleted, Is.False);
            });
        }
        finally
        {
            release.TrySetResult(RuntimeActivityOutcome.Applied);
            await Task.WhenAll(disposal, duplicate).WaitAsync(TimeSpan.FromSeconds(5));
        }
        scope.Signal();
        Assert.That(await scope.Completion, Is.EqualTo(RuntimeActivityDisposition.Cancelled));
        Assert.That(sink.Observations, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AttemptTimeoutRetriesOnlyAfterOriginalAttemptHasEnded()
    {
        var calls = 0;
        var active = 0;
        var overlap = false;
        var sink = new RecordingSink(async (_, token) =>
        {
            if (Interlocked.Increment(ref active) != 1) overlap = true;
            try
            {
                if (Interlocked.Increment(ref calls) == 1)
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return RuntimeActivityOutcome.Applied;
            }
            finally { Interlocked.Decrement(ref active); }
        });
        await using var scope = new RuntimeActivityScope(Observation(), sink, TimeSpan.FromSeconds(5),
            attemptTimeout: TimeSpan.FromMilliseconds(20), retryDelay: TimeSpan.FromMilliseconds(1));
        scope.Signal();
        Assert.That(await scope.Completion.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(RuntimeActivityDisposition.Accepted));
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(overlap, Is.False);
            Assert.That(active, Is.Zero);
        });
    }

    [Test]
    public async Task DeliveryBudgetDoesNotRenewWhenServerKeepsReturningNotReady()
    {
        var sink = new RecordingSink((_, _) => Task.FromResult(RuntimeActivityOutcome.NotReady));
        await using var scope = new RuntimeActivityScope(Observation(), sink, TimeSpan.FromMilliseconds(50), retryDelay: TimeSpan.FromMilliseconds(1));
        scope.Signal();
        Assert.That(await scope.Completion.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(RuntimeActivityDisposition.Expired));
        var calls = sink.Observations.Count;
        scope.Signal();
        Assert.That(sink.Observations, Has.Count.EqualTo(calls));
        Assert.That(calls, Is.GreaterThan(0));
    }

    private sealed class RecordingSink(Func<RuntimeActivityObservation, CancellationToken, Task<RuntimeActivityOutcome>> report) : IRuntimeActivitySink
    {
        public List<RuntimeActivityObservation> Observations { get; } = [];

        public Task<RuntimeActivityOutcome> ReportAsync(RuntimeActivityObservation observation, CancellationToken cancellationToken)
        {
            Observations.Add(observation);
            return report(observation, cancellationToken);
        }
    }
}
