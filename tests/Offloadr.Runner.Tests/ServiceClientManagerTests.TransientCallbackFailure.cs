namespace Offloadr.Runner.Tests;

public partial class ServiceClientManagerTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task TransientLifetime_CallbackFailureCannotReportSuccessfulCleanup(bool relaunch)
    {
        var harness = RunningHarness(InstanceA, 73);
        var request = harness.TransientCancellation.Acquire(CommandSessionId, Rt(InstanceA, 73));
        var cancelled = TransientLifetimeGate();
        var physicalCleanup = false;
        using var registration = request.Token.Register(() =>
        {
            cancelled.TrySetResult();
            throw new InvalidOperationException("synthetic private callback detail");
        });
        harness.BeforeStop = (_, _) =>
        {
            physicalCleanup = true;
            return Task.CompletedTask;
        };
        Task? command = null;
        try
        {
            command = relaunch
                ? ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 74), harness.Deps)
                : ServiceClientManager.HandleStopSessionCommandAsync(Stop(73), harness.Deps);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(command.IsCompleted, Is.False, "Cleanup must join the original request completion.");
            await request.DisposeAsync();
            if (relaunch)
            {
                await command.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(harness.Sink.StartAcks.Single().Ready, Is.False);
            }
            else
            {
                var error = Assert.ThrowsAsync<InvalidOperationException>(
                    () => command.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.That(error!.Message, Is.EqualTo("Transient request cleanup failed."));
                Assert.That(harness.Sink.StopAcks, Is.Empty);
            }
            Assert.That(physicalCleanup, Is.False);
            Assert.That(harness.Identities.TryGet(CommandSessionId, out var identity), Is.True);
            Assert.That(identity.SameRuntime(new RuntimeIdentity(InstanceA, 73)), Is.True);
        }
        finally
        {
            await request.DisposeAsync();
            if (command is not null)
            {
                try { await command.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (InvalidOperationException error) when (error.Message == "Transient request cleanup failed.") { }
            }
            try { await harness.TransientCancellation.DisposeAsync(); }
            catch (InvalidOperationException error) when (error.Message == "Transient request cleanup failed.") { }
        }
    }
}
