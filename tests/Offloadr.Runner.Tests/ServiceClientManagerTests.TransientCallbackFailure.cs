namespace Offloadr.Runner.Tests;

public partial class ServiceClientManagerTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task TransientLifetime_CallbackFailureCannotReportSuccessfulCleanup(bool quiesce)
    {
        var harness = RunningHarness(7, 3, InstanceA);
        var request = harness.TransientCancellation.Acquire(CommandSessionId, 7, 3, InstanceA);
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
            command = quiesce
                ? ServiceClientManager.HandleQuiesceRuntimeCommandAsync(Quiesce(7, 3, InstanceA, revision: 1), harness.Deps)
                : ServiceClientManager.HandleStopSessionCommandAsync(Stop(7, 3, InstanceA), harness.Deps);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(command.IsCompleted, Is.False, "Cleanup must join the original request completion.");
            await request.DisposeAsync();
            if (quiesce)
            {
                await command.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(harness.Sink.QuiesceAcks.Single().Quiesced, Is.False);
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
            Assert.That(identity.SameRuntime(new RuntimeIdentity(7, 3, InstanceA)), Is.True);
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
