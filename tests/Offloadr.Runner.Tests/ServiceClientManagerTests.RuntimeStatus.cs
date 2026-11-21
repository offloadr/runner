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

    private static Func<string, RuntimeIdentity, bool> IsTracked(RuntimeCommandHarness harness)
        => (session, runtime) => harness.Tracked(session) is { } tracked && tracked.SameRuntime(runtime);

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
    public async Task RuntimeStatus_TheOutcomeIsRecordedBeforeTheStartupSlotIsReleased()
    {
        // A command arriving between slot release and acknowledgement must see the outcome.
        var harness = new RuntimeCommandHarness();
        harness.Sink.FailWhen = request => request is AcknowledgeEditorRuntimeLaunchRequest;
        var launch = ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 3, InstanceA, revision: 1), harness.Deps);
        await WaitUntilAsync(() => harness.WorkState.GetActiveStartup() is null && harness.Calls.Any(call => call.StartsWith("start:", StringComparison.Ordinal)));

        Assert.That(harness.WorkState.StartupOutcome(CommandSessionId, new RuntimeIdentity(7, 3, InstanceA))?.Outcome.Ready, Is.True);
        await harness.Shutdown.CancelAsync();
        await launch.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void RuntimeStatus_ADelayedOutcomeOfAnOlderStartupDoesNotReplaceTheCurrentRuntimes()
    {
        var harness = RunningHarness(7, 4, InstanceB);
        harness.WorkState.RecordStartupOutcome(CommandSessionId, new RuntimeIdentity(7, 4, InstanceB),
            new ServiceClientManager.StartupOutcome(true, "", new SessionSealingStatus()), IsTracked(harness));
        // The superseded startup's continuation records its result afterwards.
        harness.WorkState.RecordStartupOutcome(CommandSessionId, new RuntimeIdentity(7, 3, InstanceA),
            new ServiceClientManager.StartupOutcome(false, "cancelled"), IsTracked(harness));

        var status = Status(harness);
        Assert.That(status.Phase, Is.EqualTo(RuntimePhase.Ready));
        AssertRuntime(status, 7, 4, InstanceB);
    }

    [Test]
    public void RuntimeStatus_ARefusedCommandForARunningReadyRuntimeKeepsItReady()
    {
        var harness = RunningHarness(7, 3, InstanceA);
        var runtime = new RuntimeIdentity(7, 3, InstanceA);
        harness.WorkState.RecordStartupOutcome(CommandSessionId, runtime, new ServiceClientManager.StartupOutcome(true, ""), IsTracked(harness));
        harness.WorkState.RecordStartupOutcome(CommandSessionId, runtime,
            new ServiceClientManager.StartupOutcome(false, "The session is already running with other sealing."), IsTracked(harness));

        Assert.That(Status(harness).Phase, Is.EqualTo(RuntimePhase.Ready));
    }

    [Test]
    public void RuntimeStatus_AFailureAfterTheReadyRuntimesProcessIsGoneIsReported()
    {
        var harness = RunningHarness(7, 3, InstanceA);
        var runtime = new RuntimeIdentity(7, 3, InstanceA);
        harness.WorkState.RecordStartupOutcome(CommandSessionId, runtime, new ServiceClientManager.StartupOutcome(true, ""), IsTracked(harness));
        // A retried launch of the same runtime failed and its cleanup took the process down.
        harness.Untrack(CommandSessionId);
        harness.WorkState.RecordStartupOutcome(CommandSessionId, runtime, new ServiceClientManager.StartupOutcome(false, "not ready"), IsTracked(harness));

        var status = Status(harness);
        Assert.Multiple(() =>
        {
            Assert.That(status.Phase, Is.EqualTo(RuntimePhase.Failed));
            Assert.That(status.Message, Is.EqualTo("not ready"));
        });
    }

    [Test]
    public void RuntimeStatus_ManyFailedNewerLaunchesNeverEvictTheRunningRuntimesOutcome()
    {
        var harness = RunningHarness(7, 3, InstanceA);
        harness.WorkState.RecordStartupOutcome(CommandSessionId, new RuntimeIdentity(7, 3, InstanceA),
            new ServiceClientManager.StartupOutcome(true, ""), IsTracked(harness));
        for (ulong epoch = 4; epoch < 20; epoch++)
            harness.WorkState.RecordStartupOutcome(CommandSessionId, new RuntimeIdentity(7, epoch, Guid.NewGuid().ToString("n")),
                new ServiceClientManager.StartupOutcome(false, "another runtime is running"), IsTracked(harness));

        Assert.That(Status(harness).Phase, Is.EqualTo(RuntimePhase.Ready));
    }

    [Test]
    public void RuntimeStatus_AnExactRetryAfterAFailureCanBecomeReady()
    {
        var harness = RunningHarness(7, 3, InstanceA);
        var runtime = new RuntimeIdentity(7, 3, InstanceA);
        harness.WorkState.RecordStartupOutcome(CommandSessionId, runtime, new ServiceClientManager.StartupOutcome(false, "not ready"), IsTracked(harness));
        Assert.That(Status(harness).Phase, Is.EqualTo(RuntimePhase.Failed));
        harness.WorkState.RecordStartupOutcome(CommandSessionId, runtime, new ServiceClientManager.StartupOutcome(true, ""), IsTracked(harness));
        Assert.That(Status(harness).Phase, Is.EqualTo(RuntimePhase.Ready));
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
