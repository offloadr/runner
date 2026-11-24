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
            Assert.That(status.SettledRuntimeOrder, Is.Zero, "A new process has settled nothing.");
        });
    }

    [Test]
    public async Task RuntimeStatus_ReportsTheSettledOrder_AlsoOnceTheSessionIsGone()
    {
        var harness = new RuntimeCommandHarness();
        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That((await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1)).Ready, Is.True);
        var ready = Status(harness);
        harness.Logical.ClearIfMatches(CommandSessionId);
        var gone = Status(harness);

        Assert.Multiple(() =>
        {
            Assert.That(ready.SettledRuntimeOrder, Is.EqualTo(3));
            Assert.That(gone.SessionId, Is.Empty);
            Assert.That(gone.SettledRuntimeOrder, Is.EqualTo(3), "Sampled after the Start: the control plane may act on this report.");
        });
    }

    [Test]
    public async Task SettledRuntimeOrder_StaysBelowAStartThatIsAdmittedButNotAdopted_AndRisesForARefusedOne()
    {
        var harness = new RuntimeCommandHarness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeActivate = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };

        var launch = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 5), harness.Deps);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var admitted = Status(harness);

        // An older Start arrives meanwhile: refused as older work, and settled at its own order.
        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 3), harness.Deps).WaitAsync(TimeSpan.FromSeconds(5));
        var refused = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        var afterRefused = Status(harness);

        release.TrySetResult();
        await launch.WaitAsync(TimeSpan.FromSeconds(5));
        var adopted = Status(harness);

        Assert.Multiple(() =>
        {
            Assert.That(admitted.SettledRuntimeOrder, Is.Zero, "Admitted, not adopted: a status must not say it is dealt with.");
            Assert.That(refused.Ready, Is.False);
            Assert.That(afterRefused.SettledRuntimeOrder, Is.EqualTo(3), "A refused Start is settled before its acknowledgement.");
            Assert.That(adopted.SettledRuntimeOrder, Is.EqualTo(5));
            Assert.That(adopted.SessionId, Is.EqualTo(CommandSessionId), "The order and the session of one snapshot.");
        });
    }

    [Test]
    public async Task SettledRuntimeOrder_RisesWhenAStartupFailsBeforeAdoption_OnceItsOutcomeIsKnown()
    {
        var harness = new RuntimeCommandHarness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeActivate = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            throw new InvalidOperationException("The GPU could not be activated.");
        };

        var launch = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 4), harness.Deps);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var starting = Status(harness);
        release.TrySetResult();
        await launch.WaitAsync(TimeSpan.FromSeconds(5));
        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        var failed = Status(harness);

        Assert.Multiple(() =>
        {
            Assert.That(starting.SettledRuntimeOrder, Is.Zero, "Not before its outcome is known.");
            Assert.That(ack.Ready, Is.False);
            Assert.That(failed.SettledRuntimeOrder, Is.EqualTo(4), "Failed before the session named it: dealt with all the same.");
        });
    }

    [Test]
    public async Task SettledRuntimeOrder_RisesToAStopsCutoff_AndASupersededStopChangesNothing()
    {
        var harness = new RuntimeCommandHarness();

        // No session is held: the Stop records its cutoff all the same.
        await ServiceClientManager.HandleStopSessionCommandAsync(Stop(5), harness.Deps).WaitAsync(TimeSpan.FromSeconds(5));
        var afterStop = Status(harness);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeActivate = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var launch = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 9), harness.Deps);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // An older Stop arrives while the newer start is admitted, not yet adopted: superseded by its
        // startup, it records nothing, so the settled order stays below that start.
        await ServiceClientManager.HandleStopSessionCommandAsync(Stop(7), harness.Deps).WaitAsync(TimeSpan.FromSeconds(5));
        var afterSuperseded = Status(harness);

        release.TrySetResult();
        await launch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That((await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1)).Ready, Is.True, "The superseded Stop stopped nothing.");
        var adopted = Status(harness);

        Assert.Multiple(() =>
        {
            Assert.That(afterStop.SessionId, Is.Empty);
            Assert.That(afterStop.SettledRuntimeOrder, Is.EqualTo(5));
            Assert.That(afterSuperseded.SettledRuntimeOrder, Is.EqualTo(5));
            Assert.That(adopted.SettledRuntimeOrder, Is.EqualTo(9));
            Assert.That(adopted.SessionId, Is.EqualTo(CommandSessionId));
        });
    }

    [Test]
    public void SettledRuntimeOrder_IgnoresACutoffPastEveryValidOrder_AndAStartWithoutASession()
    {
        var state = new ServiceClientManager.LogicalSessionState(string.Empty);
        state.RecordStopCutoff("session-a", ulong.MaxValue);
        state.SettleRefusedStart(string.Empty, Rt(InstanceA, 40));

        Assert.Multiple(() =>
        {
            Assert.That(state.SettledRuntimeOrder, Is.Zero);
            Assert.That(state.AdmitStart("session-a", Rt(InstanceA, 3)), Is.EqualTo(ServiceClientManager.StartAdmission.Stale), "Its own session stays fenced.");
            Assert.That(state.AdmitStart("session-b", Rt(InstanceB, 3)), Is.EqualTo(ServiceClientManager.StartAdmission.Newer));
        });
    }

    [Test]
    public void SettledRuntimeOrder_IsRaisedOnlyOnceTheSessionNamesTheRuntime_AndNeverDecreases()
    {
        var state = new ServiceClientManager.LogicalSessionState(string.Empty);
        var first = Rt(InstanceA, 5);
        Assert.That(state.AdmitStart("session-a", first), Is.EqualTo(ServiceClientManager.StartAdmission.Newer));
        Assert.That(state.SettledRuntimeOrder, Is.Zero, "Admitted, not adopted: the session does not name it yet.");

        Assert.That(state.TryAdoptRuntime("session-a", first), Is.True);
        Assert.That(state.Snapshot(), Is.EqualTo(("session-a", (RuntimeIdentity?)first, 5UL)), "Read together with the session.");

        Assert.That(state.AdmitStart("session-b", Rt(InstanceB, 4)), Is.EqualTo(ServiceClientManager.StartAdmission.Stale));
        state.SettleRefusedStart("session-b", Rt(InstanceB, 4));
        state.ClearIfMatches("session-a");
        Assert.That(state.SettledRuntimeOrder, Is.EqualTo(5));
    }

    [Test]
    public void SettledRuntimeOrder_NeverReachesAStartThatMayStillBeAdopted()
    {
        var state = new ServiceClientManager.LogicalSessionState(string.Empty);
        var pending = Rt(InstanceA, 5);
        Assert.That(state.AdmitStart("session-b", pending), Is.EqualTo(ServiceClientManager.StartAdmission.Newer));

        // A Stop of another session records a cutoff above the start in flight: a status that said 9 is
        // settled would let the control plane act on "no session" just before this start is adopted.
        state.RecordStopCutoff("session-a", 9);
        Assert.That(state.SettledRuntimeOrder, Is.EqualTo(4));

        Assert.That(state.TryAdoptRuntime("session-b", pending), Is.True);
        Assert.That(state.SettledRuntimeOrder, Is.EqualTo(9), "The cutoff counts once that start is settled.");
    }

    [Test]
    public void WhatWaitedBehindAReplacedStart_CountsUpToTheStartThatReplacedIt()
    {
        var state = new ServiceClientManager.LogicalSessionState(string.Empty);
        Assert.That(state.AdmitStart("session-b", Rt(InstanceA, 5)), Is.EqualTo(ServiceClientManager.StartAdmission.Newer));
        state.RecordStopCutoff("session-a", 6);
        state.RecordStopCutoff("session-c", 9);
        Assert.That(state.SettledRuntimeOrder, Is.EqualTo(4));

        // The start at 5 can no longer be adopted: 6 is settled, 9 still waits behind the start at 7.
        Assert.That(state.AdmitStart("session-b", Rt(InstanceB, 7)), Is.EqualTo(ServiceClientManager.StartAdmission.Newer));
        Assert.That(state.TryAdoptRuntime("session-b", Rt(InstanceA, 5)), Is.False);
        Assert.That(state.SettledRuntimeOrder, Is.EqualTo(6));

        state.SettleStart(Rt(InstanceA, 5));
        Assert.That(state.SettledRuntimeOrder, Is.EqualTo(6), "The replaced start's own end changes nothing more.");

        Assert.That(state.TryAdoptRuntime("session-b", Rt(InstanceB, 7)), Is.True);
        Assert.That(state.SettledRuntimeOrder, Is.EqualTo(9));
    }

    [Test]
    public void ARefusedStart_NeverSettlesTheStartInFlight_WhateverItNames()
    {
        var state = new ServiceClientManager.LogicalSessionState(string.Empty);
        var pending = Rt(InstanceA, 5);
        Assert.That(state.AdmitStart("session-a", pending), Is.EqualTo(ServiceClientManager.StartAdmission.Newer));

        // The same runtime named for another session: refused, and the start in flight is still open.
        Assert.That(state.AdmitStart("session-b", pending), Is.EqualTo(ServiceClientManager.StartAdmission.Stale));
        state.SettleRefusedStart("session-b", pending);
        Assert.That(state.SettledRuntimeOrder, Is.Zero, "A conflicting identity settles nothing.");

        state.SettleStart(pending);
        Assert.That(state.SettledRuntimeOrder, Is.EqualTo(5), "Its own startup ended not ready.");
    }

    [Test]
    public void AStartAtOrBelowTheSettledOrder_IsRefused_WhateverItsSession()
    {
        var state = new ServiceClientManager.LogicalSessionState(string.Empty);
        state.RecordStopCutoff("session-a", 9);

        Assert.Multiple(() =>
        {
            Assert.That(state.AdmitStart("session-b", Rt(InstanceA, 8)), Is.EqualTo(ServiceClientManager.StartAdmission.Stale),
                "A status may have said that nothing at or below 9 starts here.");
            Assert.That(state.AdmitStart("session-b", Rt(InstanceB, 10)), Is.EqualTo(ServiceClientManager.StartAdmission.Newer));
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
    public void RuntimeStatus_WhenStartupsKeepChanging_StillNamesTheSessionsRuntime()
    {
        var harness = RunningHarness(InstanceA, 73);
        var runtime = new RuntimeIdentity(InstanceA, 73);
        harness.WorkState.RecordStartupOutcome(CommandSessionId, runtime, new ServiceClientManager.StartupOutcome(true, ""), IsTracked(harness));
        var status = ServiceClientManager.DescribeRuntimeStatus(harness.Logical, harness.WorkState, _ => false, (session, checkedRuntime) =>
        {
            // Every read is overtaken by another startup.
            harness.WorkState.ReplaceActiveStartup(new ServiceClientManager.TrackedCommandWork(CancellationToken.None)
            {
                SessionId = "session-other",
                RuntimeIdentity = new RuntimeIdentity(InstanceB, 80),
                Task = new TaskCompletionSource().Task,
            });
            harness.WorkState.ClearActiveStartup(harness.WorkState.GetActiveStartup()!);
            return harness.TrackedProcess(session, checkedRuntime);
        });

        Assert.Multiple(() =>
        {
            Assert.That(status.SessionId, Is.EqualTo(CommandSessionId));
            Assert.That(status.Phase, Is.EqualTo(RuntimePhase.Starting));
            Assert.That(status.Runtime?.RuntimeId, Is.EqualTo(InstanceA));
            Assert.That(status.Runtime?.RuntimeOrder, Is.EqualTo(73));
            Assert.That(status.SettledRuntimeOrder, Is.EqualTo(73));
        });
    }

    [Test]
    public void ARefusedStartThatReusesTheNewestRuntimeId_SettlesNothing()
    {
        var state = new ServiceClientManager.LogicalSessionState(string.Empty);
        Assert.That(state.AdmitStart("session-a", Rt(InstanceA, 5)), Is.EqualTo(ServiceClientManager.StartAdmission.Newer));
        Assert.That(state.TryAdoptRuntime("session-a", Rt(InstanceA, 5)), Is.True);

        Assert.That(state.AdmitStart("session-a", Rt(InstanceA, 9)), Is.EqualTo(ServiceClientManager.StartAdmission.Stale));
        state.SettleRefusedStart("session-a", Rt(InstanceA, 9));

        Assert.Multiple(() =>
        {
            Assert.That(state.SettledRuntimeOrder, Is.EqualTo(5));
            Assert.That(state.AdmitStart("session-b", Rt(InstanceB, 6)), Is.EqualTo(ServiceClientManager.StartAdmission.Newer));
        });
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
