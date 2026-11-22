namespace Offloadr.Runner.Tests;

using Offloadr.Runner.V1;

public partial class ServiceClientManagerTests
{
    private static RuntimeStatus Status(RuntimeCommandHarness harness, bool stopping = false)
        => ServiceClientManager.DescribeRuntimeStatus(
            harness.Logical,
            harness.WorkState,
            _ => stopping,
            harness.TrackedProcess);

    private static Func<string, RuntimeIdentity, Guid?> IsTracked(RuntimeCommandHarness harness)
        => harness.TrackedProcess;

    private static void AssertRuntime(RuntimeStatus status, string runtimeId, ulong runtimeOrder)
        => Assert.That(status.Runtime, Is.EqualTo(Rt(runtimeId, runtimeOrder).ToRef()));

    [Test]
    public void RuntimeStatus_WithoutASession_IsNone()
    {
        var status = Status(new RuntimeCommandHarness());
        Assert.Multiple(() =>
        {
            Assert.That(status.SessionId, Is.Empty);
            Assert.That(status.Phase, Is.EqualTo(RuntimePhase.None));
            Assert.That(status.Runtime, Is.Null);
        });
    }

    [Test]
    public async Task RuntimeStatus_IsStartingDuringAStartAndReadyOnlyOnceItsOutcomeIsKnown()
    {
        var harness = new RuntimeCommandHarness { SealingStatus = new SessionSealingStatus { MemoryWorkspace = true } };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeStart = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };

        var launch = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var starting = Status(harness);
        Assert.Multiple(() =>
        {
            Assert.That(starting.SessionId, Is.EqualTo(CommandSessionId));
            Assert.That(starting.Phase, Is.EqualTo(RuntimePhase.Starting));
            Assert.That(starting.Sealing, Is.Null);
        });
        AssertRuntime(starting, InstanceA, 3);

        release.TrySetResult();
        await launch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That((await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1)).Ready, Is.True);
        var ready = Status(harness);
        Assert.Multiple(() =>
        {
            Assert.That(ready.Phase, Is.EqualTo(RuntimePhase.Ready));
            Assert.That(ready.Sealing, Is.EqualTo(new SessionSealingStatus { MemoryWorkspace = true }));
            Assert.That(ready.Message, Is.Empty);
        });
        AssertRuntime(ready, InstanceA, 3);
    }

    [Test]
    public async Task RuntimeStatus_IsReadyBeforeTheAcknowledgementIsDelivered()
    {
        // The heartbeat is the second way a ready runtime reaches the platform, so a
        // lost acknowledgement must not hold READY back.
        var harness = new RuntimeCommandHarness();
        harness.Sink.FailWhen = request => request is AcknowledgeRuntimeStartRequest;

        var launch = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps);
        await WaitUntilAsync(() => harness.Sink.Attempts.OfType<AcknowledgeRuntimeStartRequest>().Any());

        Assert.That(Status(harness).Phase, Is.EqualTo(RuntimePhase.Ready));
        await harness.Shutdown.CancelAsync();
        await launch.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task RuntimeStatus_AfterAFailedStartThatKeepsItsSession_IsFailedWithItsMessage()
    {
        var harness = new RuntimeCommandHarness
        {
            BeforeStart = (_, _) => throw new TimeoutException("not ready"),
            BeforeStop = (_, _) => throw new IOException("signal failed"),
        };

        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps);
        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);

        var status = Status(harness);
        Assert.Multiple(() =>
        {
            Assert.That(status.Phase, Is.EqualTo(RuntimePhase.Failed));
            Assert.That(status.Message, Is.EqualTo(ack.Message));
            Assert.That(status.Message, Is.Not.Empty);
            Assert.That(status.Sealing, Is.Null);
        });
        AssertRuntime(status, InstanceA, 3);
    }

    [Test]
    public async Task RuntimeStatus_FollowsARelaunchFromStartingToReadyAndAStopToNoSession()
    {
        var harness = new RuntimeCommandHarness();
        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        Assert.That(Status(harness).Phase, Is.EqualTo(RuntimePhase.Ready));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeStart = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var relaunch = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 4), harness.Deps);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var starting = Status(harness);
        Assert.That(starting.Phase, Is.EqualTo(RuntimePhase.Starting));
        AssertRuntime(starting, InstanceB, 4);

        release.TrySetResult();
        await relaunch.WaitAsync(TimeSpan.FromSeconds(5));
        var ready = Status(harness);
        Assert.That(ready.Phase, Is.EqualTo(RuntimePhase.Ready));
        AssertRuntime(ready, InstanceB, 4);

        await ServiceClientManager.HandleStopSessionCommandAsync(Stop(4), harness.Deps);
        var stopped = Status(harness);
        Assert.Multiple(() =>
        {
            Assert.That(stopped.SessionId, Is.Empty);
            Assert.That(stopped.Phase, Is.EqualTo(RuntimePhase.None));
            Assert.That(stopped.Runtime, Is.Null);
        });
    }

    [Test]
    public async Task RuntimeStatus_AfterARelaunchThatFailsAndKeepsItsSession_IsFailedForTheNewRuntime()
    {
        var harness = new RuntimeCommandHarness();
        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        harness.BeforeStart = (_, _) => throw new TimeoutException("not ready");
        // The failed relaunch's own child cannot be stopped, so the session stays held.
        harness.BeforeStop = (_, _) => harness.Tracked(CommandSessionId) is { } tracked && tracked.SameRuntime(Rt(InstanceB, 4))
            ? throw new IOException("signal failed")
            : Task.CompletedTask;
        harness.TrackBeforeStart = true;

        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 4), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 2);

        var status = Status(harness);
        Assert.Multiple(() =>
        {
            Assert.That(status.SessionId, Is.EqualTo(CommandSessionId));
            Assert.That(status.Phase, Is.EqualTo(RuntimePhase.Failed));
            Assert.That(status.Message, Is.EqualTo("not ready"));
        });
        AssertRuntime(status, InstanceB, 4);
    }

    [Test]
    public void RuntimeStatus_WhileTheSessionStops_IsStopping()
    {
        var harness = RunningHarness(InstanceA, 73);
        var status = Status(harness, stopping: true);
        Assert.That(status.Phase, Is.EqualTo(RuntimePhase.Stopping));
        AssertRuntime(status, InstanceA, 73);
    }

    [Test]
    public void RuntimeStatus_ForATrackedRuntimeWithoutAnOwnedOutcome_IsNone()
    {
        // A runtime this process did not start (or whose outcome it never decided)
        // is never reported ready.
        var status = Status(RunningHarness(InstanceA, 73));
        Assert.That(status.Phase, Is.EqualTo(RuntimePhase.None));
    }

    [Test]
    public async Task RuntimeStatus_ForAnOutcomeOfAnOlderRuntime_IsNone()
    {
        var harness = new RuntimeCommandHarness();
        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 73), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        harness.Run(CommandSessionId, new RuntimeIdentity(InstanceB, 74));

        Assert.That(Status(harness).Phase, Is.EqualTo(RuntimePhase.None));
    }

    [Test]
    public async Task RuntimeStatus_TheOutcomeIsRecordedBeforeTheStartupSlotIsReleased()
    {
        // A command arriving between slot release and acknowledgement must see the outcome.
        var harness = new RuntimeCommandHarness();
        harness.Sink.FailWhen = request => request is AcknowledgeRuntimeStartRequest;
        var launch = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 73), harness.Deps);
        await WaitUntilAsync(() => harness.WorkState.GetActiveStartup() is null && harness.Calls.Any(call => call.StartsWith("start:", StringComparison.Ordinal)));

        Assert.That(harness.WorkState.StartupOutcome(CommandSessionId, new RuntimeIdentity(InstanceA, 73))?.Outcome.Ready, Is.True);
        await harness.Shutdown.CancelAsync();
        await launch.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void RuntimeStatus_AStartupThatBeginsDuringTheProcessCheckIsNeverReportedReady()
    {
        var harness = RunningHarness(InstanceA, 73);
        var runtime = new RuntimeIdentity(InstanceA, 73);
        harness.WorkState.RecordStartupOutcome(CommandSessionId, runtime, new ServiceClientManager.StartupOutcome(true, ""), IsTracked(harness));
        var relaunched = false;
        var status = ServiceClientManager.DescribeRuntimeStatus(harness.Logical, harness.WorkState, _ => false, (session, checkedRuntime) =>
        {
            // A same-runtime relaunch publishes its startup and a fresh process right now.
            if (!relaunched)
            {
                relaunched = true;
                harness.WorkState.ReplaceActiveStartup(new ServiceClientManager.TrackedCommandWork(CancellationToken.None)
                {
                    SessionId = CommandSessionId,
                    RuntimeIdentity = runtime,
                    Task = new TaskCompletionSource().Task,
                });
            }
            return harness.TrackedProcess(session, checkedRuntime);
        });

        Assert.That(status.Phase, Is.EqualTo(RuntimePhase.Starting));
    }

    [Test]
    public void RuntimeStatus_ARelaunchedProcessOfTheSameRuntimeIsNotReadyFromTheOldOutcome()
    {
        var harness = RunningHarness(InstanceA, 73);
        var runtime = new RuntimeIdentity(InstanceA, 73);
        harness.WorkState.RecordStartupOutcome(CommandSessionId, runtime, new ServiceClientManager.StartupOutcome(true, ""), IsTracked(harness));
        Assert.That(Status(harness).Phase, Is.EqualTo(RuntimePhase.Ready));

        // A new process of the same runtime is not ready yet, while the startup slot belongs
        // to another session's start.
        harness.Untrack(CommandSessionId);
        harness.Track(CommandSessionId, runtime);
        harness.WorkState.ReplaceActiveStartup(new ServiceClientManager.TrackedCommandWork(CancellationToken.None)
        {
            SessionId = "33333333333343338333333333333333",
            RuntimeIdentity = new RuntimeIdentity(InstanceB, 11),
            Task = new TaskCompletionSource().Task,
        });

        Assert.That(Status(harness).Phase, Is.EqualTo(RuntimePhase.None));
    }

    [Test]
    public void RuntimeStatus_ADelayedOutcomeOfAnOlderStartupDoesNotReplaceTheCurrentRuntimes()
    {
        var harness = RunningHarness(InstanceB, 74);
        harness.WorkState.RecordStartupOutcome(CommandSessionId, new RuntimeIdentity(InstanceB, 74),
            new ServiceClientManager.StartupOutcome(true, "", new SessionSealingStatus()), IsTracked(harness));
        // The superseded startup's continuation records its result afterwards.
        harness.WorkState.RecordStartupOutcome(CommandSessionId, new RuntimeIdentity(InstanceA, 73),
            new ServiceClientManager.StartupOutcome(false, "cancelled"), IsTracked(harness));

        var status = Status(harness);
        Assert.That(status.Phase, Is.EqualTo(RuntimePhase.Ready));
        AssertRuntime(status, InstanceB, 74);
    }

    [Test]
    public void RuntimeStatus_ARefusedCommandForARunningReadyRuntimeKeepsItReady()
    {
        var harness = RunningHarness(InstanceA, 73);
        var runtime = new RuntimeIdentity(InstanceA, 73);
        harness.WorkState.RecordStartupOutcome(CommandSessionId, runtime, new ServiceClientManager.StartupOutcome(true, ""), IsTracked(harness));
        harness.WorkState.RecordStartupOutcome(CommandSessionId, runtime,
            new ServiceClientManager.StartupOutcome(false, "The session is already running with other sealing."), IsTracked(harness));

        Assert.That(Status(harness).Phase, Is.EqualTo(RuntimePhase.Ready));
    }

    [Test]
    public void RuntimeStatus_AFailureAfterTheReadyRuntimesProcessIsGoneIsReported()
    {
        var harness = RunningHarness(InstanceA, 73);
        var runtime = new RuntimeIdentity(InstanceA, 73);
        harness.WorkState.RecordStartupOutcome(CommandSessionId, runtime, new ServiceClientManager.StartupOutcome(true, ""), IsTracked(harness));
        // A later failure of the same runtime came after its cleanup took the process down.
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
    public void RuntimeStatus_ManyFailedNewerStartsNeverEvictTheRunningRuntimesOutcome()
    {
        var harness = RunningHarness(InstanceA, 73);
        harness.WorkState.RecordStartupOutcome(CommandSessionId, new RuntimeIdentity(InstanceA, 73),
            new ServiceClientManager.StartupOutcome(true, ""), IsTracked(harness));
        for (ulong order = 74; order < 90; order++)
            harness.WorkState.RecordStartupOutcome(CommandSessionId, new RuntimeIdentity(Guid.NewGuid().ToString("n"), order),
                new ServiceClientManager.StartupOutcome(false, "another runtime is running"), IsTracked(harness));

        Assert.That(Status(harness).Phase, Is.EqualTo(RuntimePhase.Ready));
    }

    [Test]
    public void RuntimeStatus_AnExactRetryAfterAFailureCanBecomeReady()
    {
        var harness = RunningHarness(InstanceA, 73);
        var runtime = new RuntimeIdentity(InstanceA, 73);
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
