namespace Offloadr.Runner.Tests;

public partial class ServiceClientManagerTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task TransientLifetime_CallbackFailureCannotSkipPeerCancellation(bool sameRuntime)
    {
        await using var registry = new ServiceClientManager.TransientCancellationRegistry(CancellationToken.None);
        await using var failed = registry.Acquire(CommandSessionId, 7, 3, InstanceA);
        await using var peer = registry.Acquire(CommandSessionId, 7, sameRuntime ? 3UL : 4UL, sameRuntime ? InstanceA : InstanceB);
        var peerCancelled = TransientLifetimeGate();
        using var failedRegistration = failed.Token.Register(() => throw new InvalidOperationException("synthetic callback failure"));
        using var peerRegistration = peer.Token.Register(() => peerCancelled.TrySetResult());
        registry.CancelAll();
        await peerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        registry.CancelAll();
        await Task.WhenAll(failed.DisposeAsync().AsTask(), peer.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(registry.Count, Is.Zero);
    }

    [Test]
    public async Task TransientLifetime_ShutdownJoinsRetiredCallbacksAndCoalesces()
    {
        var registry = new ServiceClientManager.TransientCancellationRegistry(CancellationToken.None);
        var retired = registry.Acquire(CommandSessionId, 7, 3, InstanceA);
        var entered = TransientLifetimeGate();
        var release = TransientLifetimeGate();
        Task? completion = null;
        Task? close = null;
        using var registration = retired.Token.Register(() =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        });
        try
        {
            registry.CancelAll();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(registry.Count, Is.Zero, "Retirement must not discard callback ownership.");
            completion = retired.DisposeAsync().AsTask();
            close = registry.DisposeAsync().AsTask();
            Assert.Multiple(() =>
            {
                Assert.That(retired.DisposeAsync().AsTask(), Is.SameAs(completion));
                Assert.That(registry.DisposeAsync().AsTask(), Is.SameAs(close));
                Assert.That(completion.IsCompleted, Is.False);
                Assert.That(close.IsCompleted, Is.False);
            });
            release.SetResult();
            await Task.WhenAll(completion, close).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(completion ?? retired.DisposeAsync().AsTask(), close ?? registry.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TransientLifetime_StopAndQuiesceFenceRequestsThroughPhysicalCleanup(bool quiesce)
    {
        var harness = RunningHarness(7, 3, InstanceA);
        var request = harness.TransientCancellation.Acquire(CommandSessionId, 7, 3, InstanceA);
        var cancelled = TransientLifetimeGate();
        var cleanupEntered = TransientLifetimeGate();
        var releaseCleanup = TransientLifetimeGate();
        using var registration = request.Token.Register(() => cancelled.TrySetResult());
        harness.BeforeStop = (_, _) =>
        {
            cleanupEntered.TrySetResult();
            return releaseCleanup.Task;
        };
        Task? command = null;
        try
        {
            command = quiesce
                ? ServiceClientManager.HandleQuiesceRuntimeCommandAsync(Quiesce(7, 3, InstanceA, revision: 1), harness.Deps)
                : ServiceClientManager.HandleStopSessionCommandAsync(Stop(7, 3, InstanceA), harness.Deps);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That(command.IsCompleted, Is.False);
                Assert.That(cleanupEntered.Task.IsCompleted, Is.False, "Physical cleanup waits for the original request.");
            });
            Assert.Throws<OperationCanceledException>(() => harness.TransientCancellation.Acquire(CommandSessionId, 7, 3, InstanceA));
            await using var unrelated = harness.TransientCancellation.Acquire("unrelated-session", 7, 3, InstanceA);
            Assert.That(unrelated.Token.IsCancellationRequested, Is.False);
            await request.DisposeAsync();
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<OperationCanceledException>(() => harness.TransientCancellation.Acquire(CommandSessionId, 7, 3, InstanceA));
            releaseCleanup.SetResult();
            await command.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(quiesce ? harness.Sink.QuiesceAcks.Count : harness.Sink.StopAcks.Count, Is.EqualTo(1));
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await request.DisposeAsync();
            if (command is not null) await command.WaitAsync(TimeSpan.FromSeconds(5));
            await harness.TransientCancellation.DisposeAsync();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TransientLifetime_ClosedRegistryRejectsAdmission(bool hostShutdown)
    {
        using var shutdown = new CancellationTokenSource();
        await using var registry = new ServiceClientManager.TransientCancellationRegistry(shutdown.Token);
        if (hostShutdown) await shutdown.CancelAsync();
        else await registry.DisposeAsync();
        Assert.Throws<OperationCanceledException>(() => registry.Acquire(CommandSessionId, 7, 3, InstanceA));
    }

    private static TaskCompletionSource TransientLifetimeGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
