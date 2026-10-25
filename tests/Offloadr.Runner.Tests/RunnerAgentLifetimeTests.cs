using Offloadr.Runner.Agent;

namespace Offloadr.Runner.Tests;

[TestFixture]
public sealed class RunnerAgentLifetimeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Disabled(CancellationToken _) => Task.CompletedTask;
    private static Task UntilShutdown(CancellationToken token) => Task.Delay(System.Threading.Timeout.Infinite, token);

    [Test]
    public async Task FactoryFailure_JoinsTheAlreadyStartedOriginalBeforeStoppingSessions()
    {
        var lifetime = new RunnerAgentLifetime();
        var release = Gate();
        var cancelled = Gate();
        var expected = new InvalidOperationException("fixture setup failure");
        var stopped = false;
        var laterFactoryCalls = 0;
        Task? original = null;
        using var registration = lifetime.Token.Register(() => cancelled.TrySetResult());
        try
        {
            var run = lifetime.RunAsync(
                _ => original = release.Task,
                _ => throw expected,
                _ => { laterFactoryCalls++; return Task.CompletedTask; },
                Disabled,
                () => { stopped = true; return ValueTask.CompletedTask; });
            await cancelled.Task.WaitAsync(Timeout);
            var disposal = lifetime.DisposeAsync().AsTask();
            Assert.Multiple(() =>
            {
                Assert.That(original, Is.SameAs(release.Task));
                Assert.That(original!.IsCompleted, Is.False);
                Assert.That(run.IsCompleted, Is.False);
                Assert.That(disposal.IsCompleted, Is.False);
                Assert.That(stopped, Is.False);
                Assert.That(laterFactoryCalls, Is.Zero);
            });
            release.SetResult();
            Assert.That(await FailureAsync(run), Is.SameAs(expected));
            Assert.That(await FailureAsync(disposal), Is.SameAs(expected));
            Assert.That(stopped, Is.True);
        }
        finally
        {
            release.TrySetResult();
            await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask());
        }
    }

    [Test]
    public async Task StatusFactoryFailure_JoinsAllThreeStartedOriginalsBeforeCleanup()
    {
        var lifetime = new RunnerAgentLifetime();
        var originals = new[] { Gate(), Gate(), Gate() };
        var cancelled = Gate();
        var expected = new IOException("fixture status setup failure");
        var stops = 0;
        using var registration = lifetime.Token.Register(() => cancelled.TrySetResult());
        try
        {
            var run = lifetime.RunAsync(_ => originals[0].Task, _ => originals[1].Task, _ => originals[2].Task,
                _ => throw expected, () => { stops++; return ValueTask.CompletedTask; });
            await cancelled.Task.WaitAsync(Timeout);
            foreach (var original in originals)
            {
                Assert.That(stops, Is.Zero);
                Assert.That(run.IsCompleted, Is.False);
                original.SetResult();
            }
            Assert.That(await FailureAsync(run), Is.SameAs(expected));
            Assert.That(stops, Is.EqualTo(1));
        }
        finally
        {
            foreach (var original in originals) original.TrySetResult();
            await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask());
        }
    }

    [Test]
    public async Task DisposeDuringSynchronousFactory_OwnsItsLateOriginalAndRejectsLaterFactories()
    {
        var lifetime = new RunnerAgentLifetime();
        var enteredFactory = Gate();
        var releaseFactory = Gate();
        var original = Gate();
        var laterFactories = 0;
        var stops = 0;
        try
        {
            var run = lifetime.RunAsync(_ =>
            {
                enteredFactory.SetResult();
                releaseFactory.Task.GetAwaiter().GetResult();
                return original.Task;
            }, LaterFactory, LaterFactory, LaterFactory,
                () => { stops++; return ValueTask.CompletedTask; });
            await enteredFactory.Task.WaitAsync(Timeout);
            var disposal = lifetime.DisposeAsync().AsTask();
            await lifetime.RequestShutdownAsync().WaitAsync(Timeout);
            Assert.That(disposal.IsCompleted, Is.False);
            Assert.That(stops, Is.Zero);
            releaseFactory.SetResult();
            Assert.That(original.Task.IsCompleted, Is.False);
            Assert.That(disposal.IsCompleted, Is.False);
            original.SetResult();
            await run.WaitAsync(Timeout);
            await disposal.WaitAsync(Timeout);
            Assert.That(laterFactories, Is.Zero);
            Assert.That(stops, Is.EqualTo(1));
        }
        finally
        {
            releaseFactory.TrySetResult();
            original.TrySetResult();
            await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask());
        }

        Task LaterFactory(CancellationToken _)
        {
            laterFactories++;
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task SessionCleanup_RemainsOwnedBeforeCallerDisposesSidecars()
    {
        var lifetime = new RunnerAgentLifetime();
        var started = Gate();
        var cleanupEntered = Gate();
        var releaseCleanup = Gate();
        var sidecarsDisposed = false;
        try
        {
            var caller = RunCallerAsync();
            await started.Task.WaitAsync(Timeout);
            await lifetime.RequestShutdownAsync().WaitAsync(Timeout);
            await cleanupEntered.Task.WaitAsync(Timeout);
            var disposal = lifetime.DisposeAsync().AsTask();
            Assert.That(caller.IsCompleted, Is.False);
            Assert.That(disposal.IsCompleted, Is.False);
            Assert.That(sidecarsDisposed, Is.False);
            releaseCleanup.SetResult();
            await caller.WaitAsync(Timeout);
            await disposal.WaitAsync(Timeout);
            Assert.That(sidecarsDisposed, Is.True);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask());
        }

        async Task RunCallerAsync()
        {
            try
            {
                await lifetime.RunAsync(UntilShutdown, Disabled, Disabled,
                    _ => { started.SetResult(); return Task.CompletedTask; },
                    () => { cleanupEntered.SetResult(); return new ValueTask(releaseCleanup.Task); });
            }
            finally { sidecarsDisposed = true; }
        }
    }

    [Test]
    public async Task ProducerFailure_CancelsPeersAndJoinsEveryOriginalBeforeCleanup()
    {
        var lifetime = new RunnerAgentLifetime();
        var command = Gate();
        var telemetry = Gate();
        var eviction = Gate();
        var status = Gate();
        var started = Gate();
        var cancelled = Gate();
        var expected = new InvalidOperationException("fixture producer failure");
        var stopped = false;
        using var registration = lifetime.Token.Register(() => cancelled.TrySetResult());
        try
        {
            var run = lifetime.RunAsync(_ => command.Task, _ => telemetry.Task, _ => eviction.Task,
                _ => { started.SetResult(); return status.Task; },
                () => { stopped = true; return ValueTask.CompletedTask; });
            await started.Task.WaitAsync(Timeout);
            telemetry.SetException(expected);
            await cancelled.Task.WaitAsync(Timeout);
            var firstDisposal = lifetime.DisposeAsync().AsTask();
            var secondDisposal = lifetime.DisposeAsync().AsTask();
            Assert.That(secondDisposal, Is.SameAs(firstDisposal));
            Assert.That(stopped, Is.False);
            command.SetResult();
            eviction.SetResult();
            Assert.That(status.Task.IsCompleted, Is.False);
            Assert.That(run.IsCompleted, Is.False);
            Assert.That(firstDisposal.IsCompleted, Is.False);
            Assert.That(stopped, Is.False);
            status.SetResult();
            Assert.That(await FailureAsync(run), Is.SameAs(expected));
            Assert.That(await FailureAsync(firstDisposal), Is.SameAs(expected));
            Assert.That(stopped, Is.True);
        }
        finally
        {
            command.TrySetResult();
            telemetry.TrySetResult();
            eviction.TrySetResult();
            status.TrySetResult();
            await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask());
        }
    }

    [Test]
    public async Task RepeatedShutdown_JoinsOriginalCancellationCallbackBeforeCleanup()
    {
        var lifetime = new RunnerAgentLifetime();
        var started = Gate();
        var callbackEntered = Gate();
        var releaseCallback = Gate();
        var stopped = false;
        using var registration = lifetime.Token.Register(() =>
        {
            callbackEntered.SetResult();
            releaseCallback.Task.GetAwaiter().GetResult();
        });
        try
        {
            var run = lifetime.RunAsync(UntilShutdown, Disabled, Disabled,
                _ => { started.SetResult(); return Task.CompletedTask; },
                () => { stopped = true; return ValueTask.CompletedTask; });
            await started.Task.WaitAsync(Timeout);
            var firstCancellation = lifetime.RequestShutdownAsync();
            await callbackEntered.Task.WaitAsync(Timeout);
            var secondCancellation = lifetime.RequestShutdownAsync();
            var disposal = lifetime.DisposeAsync().AsTask();
            Assert.Multiple(() =>
            {
                Assert.That(secondCancellation, Is.SameAs(firstCancellation));
                Assert.That(firstCancellation.IsCompleted, Is.False);
                Assert.That(run.IsCompleted, Is.False);
                Assert.That(disposal.IsCompleted, Is.False);
                Assert.That(stopped, Is.False);
            });
            releaseCallback.SetResult();
            await run.WaitAsync(Timeout);
            await disposal.WaitAsync(Timeout);
            Assert.That(stopped, Is.True);
            Assert.That(lifetime.RequestShutdownAsync(), Is.SameAs(firstCancellation));
        }
        finally
        {
            releaseCallback.TrySetResult();
            await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask());
        }
    }

    [Test]
    public async Task CancellationCallbackFailure_StillJoinsProducersAndStopsSessions()
    {
        var lifetime = new RunnerAgentLifetime();
        var release = Gate();
        var started = Gate();
        var expected = new InvalidOperationException("fixture callback failure");
        var stopped = false;
        using var registration = lifetime.Token.Register(() => throw expected);
        try
        {
            var run = lifetime.RunAsync(_ => release.Task, Disabled, Disabled,
                _ => { started.SetResult(); return Task.CompletedTask; },
                () => { stopped = true; return ValueTask.CompletedTask; });
            await started.Task.WaitAsync(Timeout);
            var cancellationFailure = await FailureAsync(lifetime.RequestShutdownAsync());
            Assert.That(((AggregateException)cancellationFailure).InnerExceptions, Does.Contain(expected));
            Assert.That(run.IsCompleted, Is.False);
            Assert.That(stopped, Is.False);
            release.SetResult();
            Assert.That(await FailureAsync(run), Is.SameAs(cancellationFailure));
            Assert.That(stopped, Is.True);
        }
        finally
        {
            release.TrySetResult();
            await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask());
        }
    }

    [Test]
    public async Task SetupAndCleanupFailures_AreBothPreservedAfterOriginalJoins()
    {
        var lifetime = new RunnerAgentLifetime();
        var release = Gate();
        var cancelled = Gate();
        var setupFailure = new InvalidOperationException("fixture setup failure");
        var cleanupFailure = new IOException("fixture cleanup failure");
        var stopped = false;
        using var registration = lifetime.Token.Register(() => cancelled.SetResult());
        try
        {
            var run = lifetime.RunAsync(_ => release.Task, _ => throw setupFailure, Disabled, Disabled,
                () => { stopped = true; throw cleanupFailure; });
            await cancelled.Task.WaitAsync(Timeout);
            Assert.That(stopped, Is.False);
            release.SetResult();
            var failure = (AggregateException)await FailureAsync(run);
            Assert.That(failure.InnerExceptions, Is.EquivalentTo(new Exception[] { setupFailure, cleanupFailure }));
            Assert.That(stopped, Is.True);
        }
        finally
        {
            release.TrySetResult();
            await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask());
        }
    }

    [Test]
    public async Task NormalHostShutdown_JoinsCancelledProducersBeforeSessionCleanup()
    {
        var lifetime = new RunnerAgentLifetime();
        var started = Gate();
        var producerEnds = 0;
        var endsAtStop = -1;
        var stops = 0;
        try
        {
            var run = lifetime.RunAsync(Producer, Producer, Producer,
                token => { started.SetResult(); return Producer(token); },
                () => { endsAtStop = producerEnds; stops++; return ValueTask.CompletedTask; });
            await started.Task.WaitAsync(Timeout);
            await lifetime.RequestShutdownAsync().WaitAsync(Timeout);
            await run.WaitAsync(Timeout);
            await lifetime.DisposeAsync().AsTask().WaitAsync(Timeout);
            Assert.Multiple(() =>
            {
                Assert.That(producerEnds, Is.EqualTo(4));
                Assert.That(endsAtStop, Is.EqualTo(4));
                Assert.That(stops, Is.EqualTo(1));
            });
        }
        finally { await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask()); }

        async Task Producer(CancellationToken token)
        {
            try { await UntilShutdown(token); }
            finally { Interlocked.Increment(ref producerEnds); }
        }
    }

    [Test]
    public async Task DisabledOptionalProducers_DoNotStopTheCommandListener()
    {
        var lifetime = new RunnerAgentLifetime();
        var started = Gate();
        try
        {
            var run = lifetime.RunAsync(UntilShutdown, Disabled, Disabled,
                _ => { started.SetResult(); return Task.CompletedTask; },
                () => ValueTask.CompletedTask);
            await started.Task.WaitAsync(Timeout);
            Assert.That(lifetime.IsCancellationRequested, Is.False);
            Assert.That(run.IsCompleted, Is.False);
            await lifetime.DisposeAsync().AsTask().WaitAsync(Timeout);
            await run.WaitAsync(Timeout);
        }
        finally { await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask()); }
    }

    [Test]
    public async Task CommandListenerEndingBeforeShutdown_IsFailure()
    {
        var lifetime = new RunnerAgentLifetime();
        try
        {
            var run = lifetime.RunAsync(Disabled, Disabled, Disabled, Disabled, () => ValueTask.CompletedTask);
            Assert.That(await FailureAsync(run), Is.TypeOf<InvalidOperationException>());
            Assert.That(lifetime.IsCancellationRequested, Is.True);
        }
        finally { await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask()); }
    }

    [Test]
    public async Task UnexpectedProducerCancellation_IsFailureAndCancelsPeers()
    {
        var lifetime = new RunnerAgentLifetime();
        try
        {
            var run = lifetime.RunAsync(UntilShutdown, _ => Task.FromCanceled(new CancellationToken(true)),
                Disabled, Disabled, () => ValueTask.CompletedTask);
            Assert.That(await FailureAsync(run), Is.TypeOf<TaskCanceledException>());
            Assert.That(lifetime.IsCancellationRequested, Is.True);
        }
        finally { await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask()); }
    }

    [Test]
    public async Task ShutdownBeforeRun_DoesNotInvokeProducersButStillStopsSessions()
    {
        var lifetime = new RunnerAgentLifetime();
        var producers = 0;
        var stops = 0;
        try
        {
            await lifetime.RequestShutdownAsync();
            await lifetime.RunAsync(Producer, Producer, Producer, Producer,
                () => { stops++; return ValueTask.CompletedTask; }).WaitAsync(Timeout);
            Assert.That(producers, Is.Zero);
            Assert.That(stops, Is.EqualTo(1));
        }
        finally { await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask()); }

        Task Producer(CancellationToken _)
        {
            producers++;
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task DisposeBeforeRun_RejectsNewProducersAndCoalescesTheOriginalJoin()
    {
        var lifetime = new RunnerAgentLifetime();
        var first = lifetime.DisposeAsync().AsTask();
        var second = lifetime.DisposeAsync().AsTask();
        Assert.That(second, Is.SameAs(first));
        await first.WaitAsync(Timeout);
        Assert.Throws<ObjectDisposedException>(() => lifetime.RunAsync(Disabled, Disabled, Disabled, Disabled, () => ValueTask.CompletedTask));
    }

    [Test]
    public async Task SecondRun_IsRejectedWhileTheFirstOriginalIsOwned()
    {
        var lifetime = new RunnerAgentLifetime();
        try
        {
            var run = lifetime.RunAsync(UntilShutdown, Disabled, Disabled, Disabled, () => ValueTask.CompletedTask);
            Assert.Throws<InvalidOperationException>(() => lifetime.RunAsync(Disabled, Disabled, Disabled, Disabled, () => ValueTask.CompletedTask));
            await lifetime.DisposeAsync().AsTask().WaitAsync(Timeout);
            await run.WaitAsync(Timeout);
        }
        finally { await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask()); }
    }

    private static async Task<Exception> FailureAsync(Task original)
    {
        try { await original.WaitAsync(Timeout); }
        catch (Exception error) when (original.IsCompleted) { return error; }
        throw new AssertionException("The original task unexpectedly succeeded.");
    }

    private static async Task JoinAfterAssertionAsync(Task original)
    {
        // Cleanup must not replace a failed assertion with an expected task error.
        try { await original.WaitAsync(Timeout); }
        catch (Exception) when (original.IsCompleted) { }
    }
}
