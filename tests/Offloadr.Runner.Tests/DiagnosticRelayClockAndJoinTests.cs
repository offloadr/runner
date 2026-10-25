using System.Collections.Concurrent;
using Google.Protobuf.WellKnownTypes;
using Offloadr.Runner.V1;
using Clock = Offloadr.Runner.Tests.DiagnosticRelayLifetimeTests.DiagnosticClock;

namespace Offloadr.Runner.Tests;

public sealed class DiagnosticRelayClockAndJoinTests
{
    [TestCase("monotonic")]
    [TestCase("utc")]
    [TestCase("utc-rollback")]
    [TestCase("monotonic-rollback")]
    public void EitherClockRetiresTheOriginalWindowPermanently(string cause)
    {
        var clock = new Clock();
        var deadline = new LogRelayDeadline(clock);
        var original = deadline.CapturedUtc;
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.That(deadline.Remaining, Is.EqualTo(TimeSpan.FromMinutes(4)));
        switch (cause)
        {
            case "monotonic": clock.Shift(TimeSpan.Zero, TimeSpan.FromMinutes(5)); break;
            case "utc": clock.Shift(TimeSpan.FromMinutes(5), TimeSpan.Zero); break;
            case "utc-rollback": clock.Shift(TimeSpan.FromSeconds(-1), TimeSpan.Zero); break;
            case "monotonic-rollback": clock.Shift(TimeSpan.Zero, TimeSpan.FromSeconds(-1)); break;
        }
        Assert.That(deadline.Remaining, Is.EqualTo(TimeSpan.Zero));
        clock.Shift(original - clock.GetUtcNow(), TimeSpan.FromTicks(-clock.GetTimestamp()));
        Assert.That(deadline.Remaining, Is.EqualTo(TimeSpan.Zero), "Returning clocks to their start must not revive retired content.");
        Assert.That(deadline.CapturedUtc, Is.EqualTo(original));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AttemptCancellationJoinsTheOriginalBeforeFreshDelivery(bool runner)
    {
        var clock = new Clock();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<string>();
        var active = 0;
        var maximum = 0;
        async Task Send(string message, CancellationToken token)
        {
            var count = Interlocked.Increment(ref active);
            lock (calls) maximum = Math.Max(maximum, count);
            calls.Enqueue(message);
            try
            {
                if (message == "original")
                {
                    entered.TrySetResult(token);
                    // Deliberately ignore cancellation until the original operation is released.
                    await release.Task;
                    token.ThrowIfCancellationRequested();
                }
                else fresh.TrySetResult();
            }
            finally { Interlocked.Decrement(ref active); }
        }
        var sink = new DiagnosticSink
        {
            Session = (request, token) => Send(request.Entries.Single().Message, token),
            Runner = (request, token) => Send(request.Entries.Single().Message, token)
        };
        var probe = new RelayProbe(runner, clock, sink);
        try
        {
            probe.Enqueue("original");
            var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            clock.Advance(LogRelayBounds.AttemptTimeout);
            Assert.That(token.IsCancellationRequested, Is.True);
            await Task.Delay(300);
            Assert.That(calls, Has.Count.EqualTo(1), "Cancellation must not replace an unfinished call.");
            Assert.That(active, Is.EqualTo(1));
            clock.Advance(LogRelayBounds.MaximumQueuedAge);
            probe.Enqueue("fresh");
            release.TrySetResult();
            await fresh.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(calls, Is.EqualTo(new[] { "original", "fresh" }), "Expired original content must not retry.");
            Assert.That(maximum, Is.EqualTo(1));
        }
        finally
        {
            release.TrySetResult();
            await probe.DisposeAsync();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task NearExpiryUploadUsesTheRemainingOriginalAge(bool runner)
    {
        var clock = new Clock();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task Send(CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                first.TrySetResult();
                await releaseFirst.Task;
                throw new InvalidOperationException("synthetic transient");
            }
            second.TrySetResult(token);
            await releaseSecond.Task;
            token.ThrowIfCancellationRequested();
        }
        var sink = new DiagnosticSink { Session = (_, token) => Send(token), Runner = (_, token) => Send(token) };
        var probe = new RelayProbe(runner, clock, sink);
        try
        {
            probe.Enqueue("original");
            await first.Task.WaitAsync(TimeSpan.FromSeconds(3));
            clock.Advance(LogRelayBounds.MaximumQueuedAge - TimeSpan.FromSeconds(1));
            releaseFirst.TrySetResult();
            var token = await second.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(token.IsCancellationRequested, Is.False);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.That(token.IsCancellationRequested, Is.True, "A fresh five-second attempt must not outlive the final second of content age.");
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
            await probe.DisposeAsync();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TransientRetryPreservesOriginalCaptureAndIdentity(bool runner)
    {
        var clock = new Clock();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<(Timestamp Created, ulong Sequence, string Identity)>();
        Task Send(Timestamp created, ulong sequence, string identity)
        {
            seen.Add((created.Clone(), sequence, identity));
            if (seen.Count == 1)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                return Task.FromException(new InvalidOperationException("synthetic transient"));
            }
            delivered.TrySetResult();
            return Task.CompletedTask;
        }
        var sink = new DiagnosticSink
        {
            Session = (request, _) => { var entry = request.Entries.Single(); return Send(entry.CreatedUtc, entry.Sequence, entry.RuntimeInstanceId); },
            Runner = (request, _) => { var entry = request.Entries.Single(); return Send(entry.CreatedUtc, entry.Sequence, entry.RelayInstanceId); }
        };
        await using var probe = new RelayProbe(runner, clock, sink);
        probe.Enqueue("original");
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(seen, Has.Count.EqualTo(2));
        Assert.That(seen[1], Is.EqualTo(seen[0]));
        Assert.That(seen[0].Created.ToDateTimeOffset(), Is.LessThan(clock.GetUtcNow()));
        Assert.That(seen[0].Identity, Is.Not.Empty);
    }

    [Test]
    public async Task ConnectionReplacementCannotAdoptALineStillBeingCaptured()
    {
        using var clock = new BlockingCaptureClock();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new ConcurrentQueue<string>();
        var old = new DiagnosticSink { Runner = (_, _) => throw new AssertionException("The old transport must not be used.") };
        var next = new DiagnosticSink
        {
            Runner = (request, _) =>
            {
                foreach (var entry in request.Entries)
                {
                    messages.Enqueue(entry.Message);
                    if (entry.Message == "barrier") received.TrySetResult();
                }
                return Task.CompletedTask;
            }
        };
        await using var relay = new RunnerLogRelay("runner", 8, clock);
        relay.AttachSink(old);
        var capture = Task.Run(() => relay.Enqueue("RunnerAgent", "old capture"));
        try
        {
            Assert.That(clock.Capturing.Wait(TimeSpan.FromSeconds(3)), Is.True);
            relay.DetachSink(old);
            relay.AttachSink(next);
            relay.Enqueue("RunnerAgent", "fresh");
            clock.Release.Set();
            await capture.WaitAsync(TimeSpan.FromSeconds(3));
            relay.Enqueue("RunnerAgent", "barrier");
            await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(messages, Is.EqualTo(new[] { "fresh", "barrier" }));
        }
        finally
        {
            clock.Release.Set();
            await capture;
        }
    }

    private sealed class RelayProbe : IAsyncDisposable
    {
        private readonly RunnerLogRelay? _runner;
        private readonly SessionProcessLogRelay? _session;
        public RelayProbe(bool runner, TimeProvider clock, IRunnerSessionSink sink)
        {
            if (runner) { _runner = new("runner", 8, clock); _runner.AttachSink(sink); }
            else { _session = new("runner", 8, clock: clock); _session.AttachSink(sink); }
        }
        public void Enqueue(string message)
        {
            if (_runner is not null) _runner.Enqueue("RunnerAgent", message);
            else _session!.Enqueue("session", SessionProcessLogStream.Stdout, message, new RuntimeIdentity(7, 1, "ddc2e6472be94ee0b9dd211119b30a45"));
        }
        public async ValueTask DisposeAsync()
        {
            if (_runner is not null) await _runner.DisposeAsync();
            else await _session!.DisposeAsync();
        }
    }

    private sealed class BlockingCaptureClock : TimeProvider, IDisposable
    {
        private int _reads;
        public ManualResetEventSlim Capturing { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                Capturing.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Capture was not released.");
            }
            return DateTimeOffset.UtcNow;
        }
        public void Dispose() { Capturing.Dispose(); Release.Dispose(); }
    }

    private sealed class DiagnosticSink : IRunnerSessionSink
    {
        public Func<ReportSessionLogsRequest, CancellationToken, Task> Session { get; init; } = (_, _) => throw new NotSupportedException();
        public Func<ReportRunnerLogsRequest, CancellationToken, Task> Runner { get; init; } = (_, _) => throw new NotSupportedException();
        public Task<RegisterRunnerResponse> RegisterRunnerAsync(RegisterRunnerRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SyncLocalModelsResponse> SyncLocalModelsAsync(SyncLocalModelsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AckStartSessionAsync(AcknowledgeSessionStartRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AckStopSessionAsync(AcknowledgeSessionStopRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GrantGpuPowerExecutionResponse> GrantGpuPowerExecutionAsync(GrantGpuPowerExecutionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AckGpuPowerLimitAsync(AcknowledgeGpuPowerLimitRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GrantPromptExecutionResponse> GrantPromptExecutionAsync(GrantPromptExecutionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReportPromptEvidenceAsync(ReportPromptEvidenceRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AckSubmitEditorActionAsync(AcknowledgePromptResultRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReportModelDownloadAsync(ReportModelDownloadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReportSessionEventsAsync(ReportSessionEventsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReportSessionLogsAsync(ReportSessionLogsRequest request, CancellationToken cancellationToken) => Session(request, cancellationToken);
        public Task ReportRunnerLogsAsync(ReportRunnerLogsRequest request, CancellationToken cancellationToken) => Runner(request, cancellationToken);
        public Task ReportRuntimeTelemetryAsync(ReportRuntimeTelemetryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReadEditorRuntimeBridgeCursorResponse> ReadEditorRuntimeBridgeCursorAsync(ReadEditorRuntimeBridgeCursorRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReserveEditorRuntimeBridgeResponse> ReserveEditorRuntimeBridgeAsync(ReserveEditorRuntimeBridgeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReportEditorRuntimeBridgeStateResponse> ReportEditorRuntimeBridgeStateAsync(ReportEditorRuntimeBridgeStateRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}
