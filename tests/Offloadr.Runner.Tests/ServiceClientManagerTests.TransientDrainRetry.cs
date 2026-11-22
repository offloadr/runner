namespace Offloadr.Runner.Tests;

public partial class ServiceClientManagerTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task TransientLifetime_FailureIsScopedAndRetryCanReachPhysicalCleanup(bool relaunch, bool completedBeforeDrain)
    {
        var harness = RunningHarness(InstanceA, 73);
        var request = harness.TransientCancellation.Acquire(CommandSessionId, Rt(InstanceA, 73));
        var cancelled = TransientLifetimeGate();
        var physicalCleanup = 0;
        using var registration = request.Token.Register(() =>
        {
            cancelled.TrySetResult();
            throw new InvalidOperationException("synthetic callback detail");
        });
        harness.BeforeStop = (_, _) => { physicalCleanup++; return Task.CompletedTask; };
        var stop = Stop(73);
        Task? first = null;
        try
        {
            if (completedBeforeDrain)
            {
                harness.TransientCancellation.CancelSession(CommandSessionId);
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await request.DisposeAsync();
                using var unrelated = harness.TransientCancellation.BeginSessionDrain("unrelated-session");
                Assert.DoesNotThrowAsync(() => unrelated.Completion.WaitAsync(TimeSpan.FromSeconds(5)),
                    "A completed failure belongs to its captured session/runtime.");
            }
            first = relaunch
                ? ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 74), harness.Deps)
                : ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await request.DisposeAsync();
            if (relaunch)
            {
                await first.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(harness.Sink.StartAcks.Single().Ready, Is.False);
            }
            else
                Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(physicalCleanup, Is.Zero);
            Assert.That(harness.Identities.TryGet(CommandSessionId, out _), Is.True);

            using (var unrelated = harness.TransientCancellation.BeginSessionDrain("another-session"))
                Assert.DoesNotThrowAsync(() => unrelated.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.DoesNotThrowAsync(() => ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps)
                .WaitAsync(TimeSpan.FromSeconds(5)), "A later Stop must reach physical cleanup after observing the failure.");
            var acknowledgement = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);
            Assert.Multiple(() =>
            {
                Assert.That(physicalCleanup, Is.EqualTo(1));
                Assert.That(acknowledgement.CommandId, Is.EqualTo(stop.CommandId));
                Assert.That(harness.Sink.StopAcks.Count, Is.EqualTo(1));
                Assert.That(harness.Identities.TryGet(CommandSessionId, out _), Is.False);
            });
        }
        finally
        {
            await request.DisposeAsync();
            if (first is not null)
            {
                try { await first.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (InvalidOperationException error) when (error.Message == "Transient request cleanup failed.") { }
            }
            try { await harness.TransientCancellation.DisposeAsync(); }
            catch (InvalidOperationException error) when (error.Message == "Transient request cleanup failed.") { }
        }
    }
}
