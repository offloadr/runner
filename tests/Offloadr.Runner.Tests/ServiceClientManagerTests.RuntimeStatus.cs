namespace Offloadr.Runner.Tests;

using Offloadr.Runner.V1;

public partial class ServiceClientManagerTests
{
    private static RuntimeStatus Status(RuntimeCommandHarness harness, bool stopping = false)
        => ServiceClientManager.DescribeRuntimeStatus(
            harness.Logical,
            harness.WorkState,
            _ => stopping,
            (session, runtime) => harness.Tracked(session) is { } tracked && tracked.SameRuntime(runtime));

    private static void AssertRuntime(RuntimeStatus status, ulong generation, ulong epoch, string instance)
        => Assert.That(
            new RuntimeIdentity(status.LifecycleGeneration, status.RuntimeEpoch, status.RuntimeInstanceId),
            Is.EqualTo(new RuntimeIdentity(generation, epoch, instance)));

    [Test]
    public void RuntimeStatus_WithoutASession_IsNone()
    {
        var status = Status(new RuntimeCommandHarness());
        Assert.Multiple(() =>
        {
            Assert.That(status.SessionId, Is.Empty);
            Assert.That(status.Phase, Is.EqualTo(RuntimePhase.None));
            Assert.That(status.LifecycleGeneration, Is.Zero);
        });
    }

    [Test]
    public async Task RuntimeStatus_IsStartingDuringALaunchAndReadyOnlyOnceItsOutcomeIsKnown()
    {
        var harness = new RuntimeCommandHarness { SealingStatus = new SessionSealingStatus { MemoryWorkspace = true } };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeStart = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };

        var launch = ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 3, InstanceA, revision: 1), harness.Deps);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var starting = Status(harness);
        Assert.Multiple(() =>
        {
            Assert.That(starting.SessionId, Is.EqualTo(CommandSessionId));
            Assert.That(starting.Phase, Is.EqualTo(RuntimePhase.Starting));
            Assert.That(starting.Sealing, Is.Null);
        });
        AssertRuntime(starting, 7, 3, InstanceA);

        release.TrySetResult();
        await launch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That((await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1)).Ready, Is.True);
        var ready = Status(harness);
        Assert.Multiple(() =>
        {
            Assert.That(ready.Phase, Is.EqualTo(RuntimePhase.Ready));
            Assert.That(ready.Sealing, Is.EqualTo(new SessionSealingStatus { MemoryWorkspace = true }));
            Assert.That(ready.Message, Is.Empty);
        });
        AssertRuntime(ready, 7, 3, InstanceA);
    }

    [Test]
    public async Task RuntimeStatus_IsReadyBeforeTheAcknowledgementIsDelivered()
    {
        // The heartbeat is the second way a ready runtime reaches the platform, so a
        // lost acknowledgement must not hold READY back.
        var harness = new RuntimeCommandHarness();
        harness.Sink.FailWhen = request => request is AcknowledgeEditorRuntimeLaunchRequest;

        var launch = ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 3, InstanceA, revision: 1), harness.Deps);
        await WaitUntilAsync(() => harness.Sink.Attempts.OfType<AcknowledgeEditorRuntimeLaunchRequest>().Any());

        Assert.That(Status(harness).Phase, Is.EqualTo(RuntimePhase.Ready));
        await harness.Shutdown.CancelAsync();
        await launch.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task RuntimeStatus_AfterAFailedLaunchThatKeepsItsSession_IsFailedWithItsMessage()
    {
        var harness = new RuntimeCommandHarness
        {
            BeforeStart = (_, _) => throw new TimeoutException("not ready"),
            BeforeStop = (_, _) => throw new IOException("signal failed"),
        };

        await ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 3, InstanceA, revision: 1), harness.Deps);
        var ack = await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1);

        var status = Status(harness);
        Assert.Multiple(() =>
        {
            Assert.That(status.Phase, Is.EqualTo(RuntimePhase.Failed));
            Assert.That(status.Message, Is.EqualTo(ack.Message));
            Assert.That(status.Message, Is.Not.Empty);
            Assert.That(status.Sealing, Is.Null);
        });
        AssertRuntime(status, 7, 3, InstanceA);
    }

    [Test]
    public async Task RuntimeStatus_AfterQuiesce_KeepsTheSessionWithNoRuntimePhase()
    {
        var harness = new RuntimeCommandHarness();
        await ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 3, InstanceA, revision: 1), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1);
        Assert.That(Status(harness).Phase, Is.EqualTo(RuntimePhase.Ready));

        await ServiceClientManager.HandleQuiesceRuntimeCommandAsync(Quiesce(7, 3, InstanceA, revision: 1), harness.Deps);
        Assert.That((await harness.Sink.WaitForAsync(harness.Sink.QuiesceAcks, 1)).Quiesced, Is.True);

        var status = Status(harness);
        Assert.Multiple(() =>
        {
            Assert.That(status.SessionId, Is.EqualTo(CommandSessionId));
            Assert.That(status.Phase, Is.EqualTo(RuntimePhase.None));
        });
    }

    [Test]
    public void RuntimeStatus_WhileTheSessionStops_IsStopping()
    {
        var harness = RunningHarness(7, 3, InstanceA);
        var status = Status(harness, stopping: true);
        Assert.That(status.Phase, Is.EqualTo(RuntimePhase.Stopping));
        AssertRuntime(status, 7, 3, InstanceA);
    }

    [Test]
    public void RuntimeStatus_ForATrackedRuntimeWithoutAnOwnedOutcome_IsNone()
    {
        // A runtime this process did not start (or whose outcome it never decided)
        // is never reported ready.
        var status = Status(RunningHarness(7, 3, InstanceA));
        Assert.That(status.Phase, Is.EqualTo(RuntimePhase.None));
    }

    [Test]
    public async Task RuntimeStatus_ForAnOutcomeOfAnOlderRuntime_IsNone()
    {
        var harness = new RuntimeCommandHarness();
        await ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 3, InstanceA, revision: 1), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1);
        harness.Logical.SetActiveRuntime(CommandSessionId, 7, 4, InstanceB);
        harness.Track(CommandSessionId, new RuntimeIdentity(7, 4, InstanceB));

        Assert.That(Status(harness).Phase, Is.EqualTo(RuntimePhase.None));
    }

    [Test]
    public void RuntimeStatusMessage_IsCutAtAWholeCharacterWithinItsLimit()
    {
        const string shortMessage = "ééé not ready";
        // 'a' then two-byte characters: byte 512 falls inside a character.
        var longMessage = "a" + new string('é', 400);
        var cut = ServiceClientManager.Truncate(longMessage, ServiceClientManager.MaxRuntimeStatusMessageBytes);
        Assert.Multiple(() =>
        {
            Assert.That(ServiceClientManager.Truncate(shortMessage, ServiceClientManager.MaxRuntimeStatusMessageBytes), Is.EqualTo(shortMessage));
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(cut), Is.EqualTo(511));
            Assert.That(longMessage, Does.StartWith(cut));
        });
    }
}
