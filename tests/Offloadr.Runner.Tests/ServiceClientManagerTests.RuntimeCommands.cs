namespace Offloadr.Runner.Tests;

using System.Collections.Concurrent;
using Grpc.Core;
using Offloadr.Runner.V1;

public partial class ServiceClientManagerTests
{
    private const string CommandRunnerId = "11111111111141118111111111111111";
    private const string CommandSessionId = "22222222222242228222222222222222";
    private const string InstanceA = "aaaaaaaaaaaa4aaa8aaaaaaaaaaaaaaa";
    private const string InstanceB = "bbbbbbbbbbbb4bbb8bbbbbbbbbbbbbbb";
    private const string InstanceC = "cccccccccccc4ccc8ccccccccccccccc";

    [Test]
    public async Task Start_AcknowledgesReadyAndRetainsIdentity()
    {
        var harness = new RuntimeCommandHarness();

        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.Ready, Is.True);
            Assert.That(ack.Runtime, Is.EqualTo(Rt(InstanceA, 3).ToRef()));
            Assert.That(ack.InstanceId, Is.EqualTo("process-1"));
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(Rt(InstanceA, 3)));
            Assert.That(harness.Logical.GetActiveSessionId(), Is.EqualTo(CommandSessionId));
            Assert.That(harness.Identities.TryGet(CommandSessionId, out var identity), Is.True);
            Assert.That(identity, Is.EqualTo(Rt(InstanceA, 3)));
            // Kept only to match GPU power commands to the session.
            Assert.That(harness.Identities.LifecycleGeneration(CommandSessionId), Is.EqualTo(7));
        });
    }

    [Test]
    public async Task Stop_WhenStopFails_KeepsStateAndDoesNotAcknowledge()
    {
        var harness = RunningHarness(InstanceA, 3);
        harness.BeforeStop = (_, _) => throw new IOException("signal failed");
        var stop = Stop(3);

        await ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Sink.StopAcks, Is.Empty);
            Assert.That(harness.Logical.GetActiveSessionId(), Is.EqualTo(CommandSessionId));
            Assert.That(harness.Identities.TryGet(CommandSessionId, out _), Is.True);
            Assert.That(harness.Tracked(CommandSessionId), Is.Not.Null);
        });

        harness.BeforeStop = (_, _) => Task.CompletedTask;
        await ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);
        var ack = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.CommandId, Is.EqualTo(stop.CommandId));
            Assert.That(harness.Calls.Count(call => call.StartsWith("stop:", StringComparison.Ordinal)), Is.EqualTo(2));
            Assert.That(harness.Tracked(CommandSessionId), Is.Null);
        });
    }

    [Test]
    public async Task FailedStop_StillFencesTheSessionsOlderStarts()
    {
        var harness = RunningHarness(InstanceA, 3);
        harness.BeforeStop = (_, _) => throw new IOException("signal failed");

        await ServiceClientManager.HandleStopSessionCommandAsync(Stop(5), harness.Deps);

        Assert.That(
            harness.Logical.AdmitStart(CommandSessionId, Rt(InstanceB, 4)),
            Is.EqualTo(ServiceClientManager.StartAdmission.Stale));
    }

    [Test]
    public async Task FailedStart_WhenCleanupStopFails_KeepsStateUntilTheProcessExits()
    {
        var harness = new RuntimeCommandHarness
        {
            BeforeStart = (command, _) => throw new TimeoutException("not ready"),
            BeforeStop = (_, _) => throw new IOException("signal failed"),
        };

        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.Ready, Is.False);
            Assert.That(harness.Logical.GetActiveSessionId(), Is.EqualTo(CommandSessionId));
            Assert.That(harness.Identities.TryGet(CommandSessionId, out _), Is.True);
        });
    }

    [Test]
    public async Task NewerStartDuringAStartup_CancelsAndAwaitsThePreviousStartupBeforeStarting()
    {
        var harness = new RuntimeCommandHarness();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeStart = async (command, token) =>
        {
            if (command.Runtime.RuntimeOrder == 3)
            {
                firstEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };

        var first = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 4), harness.Deps);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        var calls = harness.Calls.ToList();
        Assert.Multiple(() =>
        {
            Assert.That(harness.Sink.StartAcks.Single(ack => ack.Runtime.RuntimeOrder == 3).Ready, Is.False);
            Assert.That(harness.Sink.StartAcks.Single(ack => ack.Runtime.RuntimeOrder == 4).Ready, Is.True);
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(Rt(InstanceB, 4)));
            Assert.That(harness.Logical.TryGetRuntime(CommandSessionId, out var logical), Is.True);
            Assert.That(logical, Is.EqualTo(Rt(InstanceB, 4)));
            Assert.That(harness.Identities.TryGet(CommandSessionId, out var identity), Is.True);
            Assert.That(identity, Is.EqualTo(Rt(InstanceB, 4)));
            // The superseded startup finished its cleanup before the newer one started.
            Assert.That(calls.IndexOf("start:4"), Is.GreaterThan(calls.IndexOf("stop:" + CommandSessionId)));
            Assert.That(calls.IndexOf("stop:" + CommandSessionId), Is.GreaterThan(calls.IndexOf("start:3")));
        });
    }

    [Test]
    public async Task NewerStartForTheSameSession_StopsTheOldChildThenLaunches()
    {
        var harness = RunningHarness(InstanceA, 3);
        var oldPrompt = harness.StartPrompt(InstanceA, 3);
        await using var oldRequest = harness.TransientCancellation.Acquire(CommandSessionId, Rt(InstanceA, 3));

        var relaunch = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 4), harness.Deps);
        await oldPrompt.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => oldRequest.Token.IsCancellationRequested);
        await Task.Delay(50);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Calls, Is.Empty, "The old child is stopped only once its relayed requests drained.");
            Assert.That(relaunch.IsCompleted, Is.False);
        });

        await oldRequest.DisposeAsync();
        await relaunch.WaitAsync(TimeSpan.FromSeconds(5));

        var calls = harness.Calls.ToList();
        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.Ready, Is.True);
            Assert.That(ack.Runtime, Is.EqualTo(Rt(InstanceB, 4).ToRef()));
            Assert.That(calls.IndexOf("stop:" + CommandSessionId), Is.GreaterThanOrEqualTo(0));
            Assert.That(calls.IndexOf("start:4"), Is.GreaterThan(calls.IndexOf("stop:" + CommandSessionId)));
            Assert.That(calls, Does.Contain("uploads:" + CommandSessionId), "The old runtime's session is cleaned up.");
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(Rt(InstanceB, 4)));
            Assert.That(harness.Logical.TryGetRuntime(CommandSessionId, out var logical), Is.True);
            Assert.That(logical, Is.EqualTo(Rt(InstanceB, 4)));
            Assert.That(harness.Identities.TryGet(CommandSessionId, out var identity), Is.True);
            Assert.That(identity, Is.EqualTo(Rt(InstanceB, 4)));
        });
    }

    [Test]
    public async Task NewerStartForTheSameSession_KeepsPromptsForItsOwnRuntime()
    {
        var harness = RunningHarness(InstanceA, 3);
        var oldPrompt = harness.StartPrompt(InstanceA, 3);
        var newPrompt = harness.StartPrompt(InstanceB, 4);

        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 4), harness.Deps).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(oldPrompt.IsCompleted, Is.True);
            Assert.That(newPrompt.IsCompleted, Is.False);
        });
        await harness.Shutdown.CancelAsync();
    }

    [Test]
    public async Task NewerStart_WhenTheOldChildCannotBeStopped_FailsAndKeepsTheOldRuntime()
    {
        var harness = RunningHarness(InstanceA, 3);
        harness.BeforeStop = (_, _) => throw new IOException("signal failed");

        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 4), harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.Ready, Is.False);
            Assert.That(harness.Calls.Where(call => call.StartsWith("start:", StringComparison.Ordinal)), Is.Empty);
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(Rt(InstanceA, 3)));
            Assert.That(harness.Logical.TryGetRuntime(CommandSessionId, out var logical), Is.True);
            Assert.That(logical, Is.EqualTo(Rt(InstanceA, 3)), "The session stays visible under its running identity.");
            Assert.That(harness.Identities.TryGet(CommandSessionId, out var identity), Is.True);
            Assert.That(identity, Is.EqualTo(Rt(InstanceA, 3)));
        });
    }

    [Test]
    public async Task StaleStart_DoesNotReplaceTheNewerRuntimeOrItsIdentity()
    {
        var harness = RunningHarness(InstanceB, 4);

        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.Ready, Is.False);
            Assert.That(ack.Runtime, Is.EqualTo(Rt(InstanceA, 3).ToRef()));
            Assert.That(harness.Calls, Is.Empty);
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(Rt(InstanceB, 4)));
            Assert.That(harness.Logical.TryGetRuntime(CommandSessionId, out var logical), Is.True);
            Assert.That(logical, Is.EqualTo(Rt(InstanceB, 4)));
            Assert.That(harness.Identities.TryGet(CommandSessionId, out var identity), Is.True);
            Assert.That(identity, Is.EqualTo(Rt(InstanceB, 4)));
        });
    }

    [Test]
    public async Task StartOfAnotherRuntimeAtTheSameOrder_IsRefusedAsStale()
    {
        var harness = RunningHarness(InstanceA, 3);

        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 3), harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.Ready, Is.False);
            Assert.That(harness.Calls, Is.Empty);
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(Rt(InstanceA, 3)));
        });
    }

    [Test]
    public async Task StaleStart_DoesNotCancelTheNewerStartup()
    {
        var harness = new RuntimeCommandHarness();
        var newerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNewer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeStart = async (_, token) =>
        {
            newerEntered.TrySetResult();
            await releaseNewer.Task.WaitAsync(token);
        };

        var newer = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 4), harness.Deps);
        await newerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps);
        releaseNewer.TrySetResult();
        await newer.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(harness.Sink.StartAcks.Single(ack => ack.Runtime.RuntimeOrder == 3).Ready, Is.False);
            Assert.That(harness.Sink.StartAcks.Single(ack => ack.Runtime.RuntimeOrder == 4).Ready, Is.True);
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(Rt(InstanceB, 4)));
        });
    }

    [Test]
    public async Task RepeatedStartWhileLaunching_LaunchesOnceAndSharesTheOutcome()
    {
        var harness = new RuntimeCommandHarness();
        var starts = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeStart = async (_, token) =>
        {
            Interlocked.Increment(ref starts);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };

        var original = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 4), harness.Deps);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The same start again, its runtime id spelled another way, as a redelivery may carry it.
        var redelivery = Start(Guid.Parse(InstanceB).ToString("D"), 4);
        var duplicate = ServiceClientManager.HandleStartRuntimeCommand(redelivery, harness.Deps);
        release.TrySetResult();
        await Task.WhenAll(original, duplicate).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(starts, Is.EqualTo(1));
            Assert.That(harness.Sink.StartAcks, Has.Count.EqualTo(2));
            Assert.That(harness.Sink.StartAcks, Has.All.Property(nameof(AcknowledgeRuntimeStartRequest.Ready)).True);
            Assert.That(harness.Sink.StartAcks.Select(ack => ack.Message).Distinct(), Has.Exactly(1).Items);
            Assert.That(harness.Sink.StartAcks.Select(ack => ack.Runtime),
                Is.EquivalentTo(new[] { Rt(InstanceB, 4).ToRef(), redelivery.Runtime }), "Each delivery is answered as it named its runtime.");
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(Rt(InstanceB, 4)));
        });
    }

    [Test]
    public async Task RepeatedStartJoiningAFailedStartup_AcknowledgesTheSameFailure()
    {
        var harness = new RuntimeCommandHarness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeStart = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            throw new InvalidOperationException("editor failed to start");
        };

        var original = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 4), harness.Deps);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var joined = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 4), harness.Deps);
        release.TrySetResult();
        await Task.WhenAll(original, joined).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(harness.Sink.StartAcks, Has.Count.EqualTo(2));
            Assert.That(harness.Sink.StartAcks, Has.All.Property(nameof(AcknowledgeRuntimeStartRequest.Ready)).False);
            Assert.That(harness.Sink.StartAcks, Has.All.Property(nameof(AcknowledgeRuntimeStartRequest.Message)).EqualTo("editor failed to start"));
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task RepeatedStartAfterItFinished_ReplaysTheRecordedAcknowledgementWithoutRelaunching(bool ready)
    {
        var harness = new RuntimeCommandHarness();
        if (!ready)
        {
            harness.BeforeStart = (_, _) => throw new InvalidOperationException("editor failed to start");
        }

        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps).WaitAsync(TimeSpan.FromSeconds(5));
        var calls = harness.Calls.ToArray();
        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps).WaitAsync(TimeSpan.FromSeconds(5));

        var acks = harness.Sink.StartAcks.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(acks, Has.Length.EqualTo(2));
            Assert.That(acks[1], Is.EqualTo(acks[0]), "The same acknowledgement is sent again.");
            Assert.That(acks[0].Ready, Is.EqualTo(ready));
            Assert.That(harness.Calls, Is.EqualTo(calls), "Nothing is launched or cleaned up again.");
        });
    }

    [Test]
    public async Task StartForAnOlderAssignmentAfterANewerSessionsStart_IsRefusedStale()
    {
        const string olderSessionId = "33333333333343338333333333333333";
        var harness = new RuntimeCommandHarness();
        var prompt = harness.StartPrompt(InstanceB, 10);
        var newerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNewer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeStart = async (start, token) =>
        {
            if (start.SessionId == CommandSessionId)
            {
                newerEntered.TrySetResult();
                await releaseNewer.Task.WaitAsync(token);
            }
        };

        var newer = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 10), harness.Deps);
        await newerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 9, olderSessionId), harness.Deps);
        var olderAck = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);

        Assert.Multiple(() =>
        {
            Assert.That(olderAck.SessionId, Is.EqualTo(olderSessionId));
            Assert.That(olderAck.Ready, Is.False);
            Assert.That(prompt.IsCompleted, Is.False, "The newer session's prompt keeps running.");
        });

        releaseNewer.TrySetResult();
        await newer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(harness.Sink.StartAcks.Single(ack => ack.SessionId == CommandSessionId).Ready, Is.True);
            Assert.That(harness.Tracked(olderSessionId), Is.Null);
            Assert.That(harness.Logical.GetActiveSessionId(), Is.EqualTo(CommandSessionId));
        });
        await harness.Shutdown.CancelAsync();
    }

    [Test]
    public async Task NewerStartForAnotherSession_PreemptsTheRunningSession()
    {
        const string newerSessionId = "33333333333343338333333333333333";
        var harness = RunningHarness(InstanceA, 3);
        var prompt = harness.StartPrompt(InstanceA, 3);

        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 4, newerSessionId), harness.Deps).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(harness.Sink.StartAcks.Single().Ready, Is.True);
            Assert.That(prompt.IsCompleted, Is.True);
            Assert.That(harness.Calls, Does.Contain("stop:" + CommandSessionId));
            Assert.That(harness.Tracked(CommandSessionId), Is.Null);
            Assert.That(harness.Identities.TryGet(CommandSessionId, out _), Is.False);
            Assert.That(harness.Tracked(newerSessionId), Is.EqualTo(Rt(InstanceB, 4)));
            Assert.That(harness.Logical.GetActiveSessionId(), Is.EqualTo(newerSessionId));
        });
    }

    [Test]
    public async Task StopBeforeAnOlderDelayedStart_RefusesTheStartAfterwards()
    {
        var harness = new RuntimeCommandHarness();

        await ServiceClientManager.HandleStopSessionCommandAsync(Stop(5), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);
        var callsAfterStop = harness.Calls.ToArray();
        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 4), harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.Ready, Is.False);
            Assert.That(harness.Calls, Is.EqualTo(callsAfterStop), "The delayed start launches nothing.");
            Assert.That(harness.Tracked(CommandSessionId), Is.Null);
            Assert.That(harness.Logical.GetActiveSessionId(), Is.Empty);
        });
    }

    [Test]
    public async Task OlderStopAfterANewerStart_LeavesTheNewerRuntimeRunning()
    {
        var harness = RunningHarness(InstanceB, 8);
        var prompt = harness.StartPrompt(InstanceB, 8);
        var stop = Stop(7);

        await ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.CommandId, Is.EqualTo(stop.CommandId));
            Assert.That(ack.UpToRuntimeOrder, Is.EqualTo(7));
            Assert.That(ack.CleanupDisposition, Is.EqualTo(RunnerCleanupDisposition.Superseded));
            Assert.That(ack.UnsavedArtifactsUnknown, Is.True);
            Assert.That(harness.Calls, Is.Empty);
            Assert.That(prompt.IsCompleted, Is.False);
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(Rt(InstanceB, 8)));
            Assert.That(harness.Logical.GetActiveSessionId(), Is.EqualTo(CommandSessionId));
            Assert.That(harness.Identities.TryGet(CommandSessionId, out _), Is.True);
            // A superseded Stop records no cutoff: the newer runtime's own start stays acceptable.
            Assert.That(harness.Logical.AdmitStart(CommandSessionId, Rt(InstanceB, 8)), Is.EqualTo(ServiceClientManager.StartAdmission.Same));
        });
        await harness.Shutdown.CancelAsync();
    }

    [Test]
    public async Task StopCutoffEvictedFromTheMap_StillRefusesTheSessionsOldStart()
    {
        const string otherSessionId = "33333333333343338333333333333333";
        var harness = new RuntimeCommandHarness(maxStopCutoffs: 1);
        await ServiceClientManager.HandleStopSessionCommandAsync(Stop(10), harness.Deps);
        var otherStop = Stop(3);
        otherStop.SessionId = otherSessionId;
        await ServiceClientManager.HandleStopSessionCommandAsync(otherStop, harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 2);

        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 9), harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.Ready, Is.False);
            Assert.That(harness.Calls.Where(call => call.StartsWith("start:", StringComparison.Ordinal)), Is.Empty);
        });
    }

    [Test]
    public async Task StopDuringStartup_CancelsTheStartupAndClearsItsState()
    {
        var harness = new RuntimeCommandHarness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeStart = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        var start = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ServiceClientManager.HandleStopSessionCommandAsync(Stop(3), harness.Deps).WaitAsync(TimeSpan.FromSeconds(5));
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Sink.StartAcks.Single().Ready, Is.False);
            Assert.That(harness.Sink.StopAcks.Single().CleanupDisposition, Is.EqualTo(RunnerCleanupDisposition.Performed));
            Assert.That(harness.Tracked(CommandSessionId), Is.Null);
            Assert.That(harness.Logical.GetActiveSessionId(), Is.Empty);
            Assert.That(harness.Identities.TryGet(CommandSessionId, out _), Is.False);
            Assert.That(harness.WorkState.GetActiveStartup(), Is.Null);
        });

        // A redelivery of the stopped start is refused by the Stop's cutoff.
        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps);
        var redelivered = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 2);
        Assert.That(redelivered.Ready, Is.False);
        Assert.That(harness.Tracked(CommandSessionId), Is.Null);
    }

    [Test]
    public async Task OlderStop_DoesNotCancelTheNewerStartup()
    {
        var harness = new RuntimeCommandHarness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeStart = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };

        var start = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 8), harness.Deps);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ServiceClientManager.HandleStopSessionCommandAsync(Stop(7), harness.Deps);
        release.TrySetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(harness.Sink.StartAcks.Single().Ready, Is.True);
            Assert.That(harness.Sink.StopAcks.Single().CleanupDisposition, Is.EqualTo(RunnerCleanupDisposition.Superseded));
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(Rt(InstanceB, 8)));
        });
    }

    [Test]
    public async Task Stop_IsNotBlockedByStartupAcknowledgementRetries()
    {
        var harness = new RuntimeCommandHarness();
        harness.Sink.FailWhen = request => request is AcknowledgeRuntimeStartRequest;

        var start = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps);
        await WaitUntilAsync(() => harness.Sink.Attempts.OfType<AcknowledgeRuntimeStartRequest>().Any());

        await ServiceClientManager.HandleStopSessionCommandAsync(Stop(3), harness.Deps).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Multiple(() =>
        {
            Assert.That(start.IsCompleted, Is.False, "The start acknowledgement keeps retrying independently.");
            Assert.That(harness.Tracked(CommandSessionId), Is.Null);
        });
        await harness.Shutdown.CancelAsync();
        await start.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task StartAcknowledgementRetry_ResendsTheIdenticalResultWithoutRelaunching()
    {
        var harness = new RuntimeCommandHarness();
        harness.Sink.FailNextAcknowledgements(2);

        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var attempts = harness.Sink.Attempts.OfType<AcknowledgeRuntimeStartRequest>().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(attempts, Has.Count.EqualTo(3));
            Assert.That(attempts.Distinct(), Has.Exactly(1).Items);
            Assert.That(attempts[0].Ready, Is.True);
            Assert.That(harness.Sink.StartAcks, Has.Count.EqualTo(1));
            Assert.That(harness.Calls.Count(call => call.StartsWith("start:", StringComparison.Ordinal)), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task FailedStart_ClearsOnlyItsOwnIdentity()
    {
        var harness = new RuntimeCommandHarness { BeforeStart = (_, _) => throw new TimeoutException("not ready") };

        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps);

        Assert.That((await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1)).Ready, Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Calls, Does.Contain("stop:" + CommandSessionId));
            Assert.That(harness.Logical.GetActiveSessionId(), Is.Empty);
            Assert.That(harness.Identities.TryGet(CommandSessionId, out _), Is.False);
        });
    }

    [TestCase(0UL)]
    [TestCase(2UL)]
    public async Task StaleStop_IsAcknowledgedWithoutStoppingTheNewerRuntime(ulong upToRuntimeOrder)
    {
        var harness = RunningHarness(InstanceA, 3);
        var prompt = harness.StartPrompt(InstanceA, 3);
        var stop = Stop(upToRuntimeOrder);

        await ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.CommandId, Is.EqualTo(stop.CommandId));
            Assert.That(ack.UpToRuntimeOrder, Is.EqualTo(upToRuntimeOrder));
            Assert.That(ack.CleanupDisposition, Is.EqualTo(RunnerCleanupDisposition.Superseded));
            Assert.That(harness.Calls, Is.Empty);
            Assert.That(prompt.IsCompleted, Is.False);
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(Rt(InstanceA, 3)));
            Assert.That(harness.Logical.GetActiveSessionId(), Is.EqualTo(CommandSessionId));
            Assert.That(harness.Identities.TryGet(CommandSessionId, out _), Is.True);
        });
        await harness.Shutdown.CancelAsync();
    }

    [TestCase(3UL)]
    [TestCase(9UL)]
    public async Task CurrentOrNewerStop_StopsTheRuntimeAndEchoesItsCutoff(ulong upToRuntimeOrder)
    {
        var harness = RunningHarness(InstanceA, 3);
        var stop = Stop(upToRuntimeOrder);

        await ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.CommandId, Is.EqualTo(stop.CommandId));
            Assert.That(ack.SessionId, Is.EqualTo(CommandSessionId));
            Assert.That(ack.InstanceId, Is.EqualTo("process-1"));
            Assert.That(ack.UpToRuntimeOrder, Is.EqualTo(upToRuntimeOrder));
            Assert.That(ack.CleanupDisposition, Is.EqualTo(RunnerCleanupDisposition.Performed));
            Assert.That(harness.Tracked(CommandSessionId), Is.Null);
            Assert.That(harness.Logical.GetActiveSessionId(), Is.Empty);
            Assert.That(harness.Identities.TryGet(CommandSessionId, out _), Is.False);
        });
    }

    [Test]
    public async Task StopAcknowledgement_IsRetriedWithTheIdenticalRequest()
    {
        var harness = RunningHarness(InstanceA, 3);
        harness.Sink.FailNextAcknowledgements(2);
        var stop = Stop(3);

        await ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);

        var attempts = harness.Sink.Attempts.OfType<AcknowledgeSessionStopRequest>().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(attempts, Has.Count.EqualTo(3));
            Assert.That(attempts.Distinct(), Has.Exactly(1).Items);
            Assert.That(attempts[0].CommandId, Is.EqualTo(stop.CommandId));
            Assert.That(attempts[0].InstanceId, Is.EqualTo(harness.InstanceId));
            Assert.That(harness.Calls.Count(call => call.StartsWith("stop:", StringComparison.Ordinal)), Is.EqualTo(1));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CompletedStop_RedeliveryReplaysWithoutTouchingReplacementRuntime(bool normalizeRetryId)
    {
        var harness = RunningHarness(InstanceA, 3);
        var stop = Stop(3);
        stop.CommandId = Guid.NewGuid().ToString("D").ToUpperInvariant();
        await ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);
        var originalAck = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);
        var cleanupCalls = harness.Calls.ToArray();

        harness.Run(CommandSessionId, Rt(InstanceB, 8));
        var prompt = harness.StartPrompt(InstanceB, 8);
        if (normalizeRetryId)
        {
            stop.CommandId = Guid.Parse(stop.CommandId).ToString("n");
        }

        await ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);
        var replay = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 2);
        Assert.Multiple(() =>
        {
            Assert.That(replay, Is.EqualTo(originalAck));
            Assert.That(harness.Calls, Is.EqualTo(cleanupCalls));
            Assert.That(prompt.IsCompleted, Is.False);
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(Rt(InstanceB, 8)));
            Assert.That(harness.Logical.GetActiveSessionId(), Is.EqualTo(CommandSessionId));
            Assert.That(harness.Identities.TryGet(CommandSessionId, out var identity), Is.True);
            Assert.That(identity, Is.EqualTo(Rt(InstanceB, 8)));
        });
        await harness.Shutdown.CancelAsync();
    }

    [Test]
    public async Task ConcurrentDuplicateStops_ShareCleanupAndWaitUntilItCompletes()
    {
        var harness = RunningHarness(InstanceA, 3);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeStop = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var stop = Stop(3);
        var first = ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = ServiceClientManager.HandleStopSessionCommandAsync(stop.Clone(), harness.Deps);
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(duplicate.IsCompleted, Is.False);
                Assert.That(harness.Sink.StopAcks, Is.Empty);
                Assert.That(harness.Calls.Count(call => call.StartsWith("stop:", StringComparison.Ordinal)), Is.EqualTo(1));
            });
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, duplicate).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.That(harness.Calls.Count(call => call.StartsWith("stop:", StringComparison.Ordinal)), Is.EqualTo(1));
        Assert.That(harness.Sink.StopAcks, Is.Not.Empty);
    }

    [Test]
    public async Task StopRedelivery_DoesNotStartAnotherAcknowledgementRetryLoop()
    {
        var harness = RunningHarness(InstanceA, 3);
        harness.Sink.FailWhen = _ => true;
        var stop = Stop(3);
        await ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);
        var work = harness.WorkState.Stops[Guid.Parse(stop.CommandId)];
        var retryLoop = work.AcknowledgementTask;

        await ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);

        Assert.Multiple(() =>
        {
            Assert.That(work.AcknowledgementTask, Is.SameAs(retryLoop));
            Assert.That(harness.Calls.Count(call => call.StartsWith("stop:", StringComparison.Ordinal)), Is.EqualTo(1));
        });
        await harness.Shutdown.CancelAsync();
        await retryLoop!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestCase("session")]
    [TestCase("process")]
    [TestCase("cutoff")]
    public async Task StopNonceReuse_WithDifferentIdentityDoesNotCleanUpOrAcknowledge(string changedIdentity)
    {
        var harness = RunningHarness(InstanceA, 3);
        var stop = Stop(3);
        harness.BeforeStop = (_, _) => throw new IOException("signal failed");
        await ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);
        var cleanupCalls = harness.Calls.ToArray();
        harness.BeforeStop = (_, _) => Task.CompletedTask;
        switch (changedIdentity)
        {
            case "session": stop.SessionId = "another-session"; break;
            case "process": harness.InstanceId = "another-process"; break;
            case "cutoff": stop.UpToRuntimeOrder = 4; break;
        }

        await ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Calls, Is.EqualTo(cleanupCalls));
            Assert.That(harness.Sink.StopAcks, Is.Empty);
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(Rt(InstanceA, 3)));
        });
    }

    [TestCase("")]
    [TestCase("invalid")]
    [TestCase("00000000-0000-0000-0000-000000000000")]
    public async Task StopWithInvalidCommandIdentity_DoesNotCancelOrCleanUp(string commandId)
    {
        var harness = RunningHarness(InstanceA, 3);
        var prompt = harness.StartPrompt(InstanceA, 3);
        var stop = Stop(3);
        stop.CommandId = commandId;

        await ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Calls, Is.Empty);
            Assert.That(harness.Sink.StopAcks, Is.Empty);
            Assert.That(prompt.IsCompleted, Is.False);
            Assert.That(harness.Logical.GetActiveSessionId(), Is.EqualTo(CommandSessionId));
        });
        await harness.Shutdown.CancelAsync();
    }

    [TestCase("", "process-1")]
    [TestCase(" ", "process-1")]
    [TestCase(CommandSessionId, "")]
    [TestCase(CommandSessionId, " ")]
    public async Task StopWithoutSessionOrProcessIdentity_DoesNotCleanUpOrAcknowledge(string sessionId, string instanceId)
    {
        var harness = RunningHarness(InstanceA, 3);
        harness.InstanceId = instanceId;
        var stop = Stop(3);
        stop.SessionId = sessionId;

        await ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Calls, Is.Empty);
            Assert.That(harness.Sink.StopAcks, Is.Empty);
            Assert.That(harness.Logical.GetActiveSessionId(), Is.EqualTo(CommandSessionId));
        });
    }

    [TestCase("N")]
    [TestCase("D")]
    public async Task StopCapturesIdentityBeforeCleanup_AndAcknowledgesOnlyAfterward(string format)
    {
        var harness = RunningHarness(InstanceA, 3);
        harness.InstanceId = " process-1 ";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeStop = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var stop = Stop(3);
        var commandId = Guid.NewGuid();
        var deliveredCommandId = commandId.ToString(format).ToUpperInvariant();
        stop.CommandId = deliveredCommandId;
        var operation = ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(harness.Sink.StopAcks, Is.Empty);
            stop.SessionId = Guid.NewGuid().ToString("n");
            stop.CommandId = Guid.NewGuid().ToString("n");
            stop.User = "replacement";
            stop.UpToRuntimeOrder = 99;
            harness.InstanceId = "replacement-process";
        }
        finally
        {
            release.TrySetResult();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var ack = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.SessionId, Is.EqualTo(CommandSessionId));
            Assert.That(ack.CommandId, Is.EqualTo(deliveredCommandId));
            Assert.That(ack.InstanceId, Is.EqualTo("process-1"));
            Assert.That(ack.User, Is.EqualTo("alice"));
            Assert.That(ack.UpToRuntimeOrder, Is.EqualTo(3));
            Assert.That(harness.Logical.GetActiveSessionId(), Is.Empty);
            Assert.That(harness.Identities.TryGet(CommandSessionId, out _), Is.False);
        });
    }

    [Test]
    public async Task StopCancelledDuringCleanup_DoesNotAcknowledgeSuccess()
    {
        var harness = RunningHarness(InstanceA, 3);
        harness.BeforeStop = async (_, _) => await harness.Shutdown.CancelAsync();

        await ServiceClientManager.HandleStopSessionCommandAsync(Stop(3), harness.Deps);

        Assert.That(harness.Sink.StopAcks, Is.Empty);
    }

    [Test]
    public void StopAfterHostShutdown_DoesNotEnterCleanup()
    {
        var harness = RunningHarness(InstanceA, 3);
        harness.Shutdown.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() =>
            ServiceClientManager.HandleStopSessionCommandAsync(Stop(3), harness.Deps));
        Assert.Multiple(() =>
        {
            Assert.That(harness.Calls, Is.Empty);
            Assert.That(harness.Sink.StopAcks, Is.Empty);
        });
    }

    [Test]
    public async Task TransientCancellation_StopRacingRequestsNeverThrowsAndReleasesEveryEntry()
    {
        const int workerCount = 4;
        const int iterations = 5000;
        using var shutdown = new CancellationTokenSource();
        var registry = new ServiceClientManager.TransientCancellationRegistry(shutdown.Token);
        using var stopRacing = new CancellationTokenSource();
        using var ready = new CountdownEvent(workerCount + 1);
        using var start = new ManualResetEventSlim();

        // Bounded dedicated workers keep this synchronous race independent of
        // thread-pool timer scheduling. Every worker is joined during cleanup.
        Task RunWorker(Action action) => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            start.Wait();
            action();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var requests = Enumerable.Range(0, workerCount).Select(worker => RunWorker(() =>
        {
            for (var iteration = 0; iteration < iterations && !stopRacing.IsCancellationRequested; iteration++)
            {
                var lease = registry.Acquire(CommandSessionId, Rt(InstanceA, 3));
                try
                {
                    _ = lease.Token.IsCancellationRequested;
                    lease.Token.Register(static () => { }).Dispose();
                }
                finally { lease.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
        })).ToArray();
        var stops = RunWorker(() =>
        {
            for (var iteration = 0; iteration < iterations && !stopRacing.IsCancellationRequested; iteration++)
            {
                registry.CancelSession(CommandSessionId);
                registry.CancelRuntime(CommandSessionId, Rt(InstanceA, 3));
            }
        });

        var racing = Task.WhenAll(requests.Append(stops));
        try
        {
            Assert.That(ready.Wait(TimeSpan.FromSeconds(10)), Is.True, "Every race worker must start before the shared gate opens.");
            start.Set();
            await racing.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            stopRacing.Cancel();
            start.Set();
            await racing.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.That(registry.Count, Is.Zero, "Entries are removed with their last request.");
    }

    [Test]
    public async Task TransientCancellation_IsScopedToTheRuntimeAndRenewedAfterCancel()
    {
        using var shutdown = new CancellationTokenSource();
        var registry = new ServiceClientManager.TransientCancellationRegistry(shutdown.Token);
        await using var oldRuntime = registry.Acquire(CommandSessionId, Rt(InstanceA, 3));
        await using var sameRuntimeOtherSpelling = registry.Acquire(CommandSessionId, Rt(Guid.Parse(InstanceA).ToString("D"), 3));
        await using var newRuntime = registry.Acquire(CommandSessionId, Rt(InstanceB, 4));
        Assert.That(registry.Count, Is.EqualTo(2));

        registry.CancelRuntime(CommandSessionId, Rt(InstanceA, 3));
        Assert.Multiple(() =>
        {
            Assert.That(oldRuntime.Token.IsCancellationRequested, Is.True);
            Assert.That(sameRuntimeOtherSpelling.Token.IsCancellationRequested, Is.True);
            Assert.That(newRuntime.Token.IsCancellationRequested, Is.False);
        });

        await using var later = registry.Acquire(CommandSessionId, Rt(InstanceA, 3));
        Assert.That(later.Token.IsCancellationRequested, Is.False);

        registry.CancelSession(CommandSessionId);
        Assert.Multiple(() =>
        {
            Assert.That(newRuntime.Token.IsCancellationRequested, Is.True);
            Assert.That(later.Token.IsCancellationRequested, Is.True);
            Assert.That(registry.Count, Is.Zero);
        });
    }

    [Test]
    public async Task EditorRequestRedeliveryAfterAcknowledgement_ReplaysTheResultWithItsDeliveryId()
    {
        var harness = new TransientRequestHarness();
        var request = harness.Request("delivery-1");

        await harness.DispatchAsync(request);
        var redelivery = request.Clone();
        redelivery.DeliveryId = "delivery-2";
        redelivery.Attempt++;
        await harness.DispatchAsync(redelivery);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Executions, Is.EqualTo(1));
            Assert.That(harness.Acknowledgements.Select(ack => ack.DeliveryId), Is.EqualTo(new[] { "delivery-1", "delivery-2" }));
            Assert.That(harness.Acknowledgements[1].Body, Is.EqualTo(harness.Acknowledgements[0].Body));
            Assert.That(harness.Deliveries.InFlightCount, Is.Zero);
        });
    }

    [Test]
    public async Task EditorRequestRedeliveryWhileInFlight_AcknowledgesEveryDeliveryWithTheSameResult()
    {
        var harness = new TransientRequestHarness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeResult = release.Task;
        var request = harness.Request("delivery-1");

        var first = harness.DispatchAsync(request);
        var redelivery = request.Clone();
        redelivery.DeliveryId = "delivery-2";
        redelivery.Attempt++;
        await harness.DispatchAsync(redelivery);
        await harness.DispatchAsync(redelivery.Clone());
        Assert.That(harness.Acknowledgements, Is.Empty);

        release.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(harness.Executions, Is.EqualTo(1));
            Assert.That(harness.Acknowledgements.Select(ack => ack.DeliveryId).Order(), Is.EqualTo(new[] { "delivery-1", "delivery-2" }));
            Assert.That(harness.Acknowledgements.Select(ack => ack.StatusCode).Distinct(), Is.EqualTo(new[] { 200 }));
            Assert.That(harness.Deliveries.InFlightCount, Is.Zero);
            Assert.That(harness.Cancellation.Count, Is.Zero);
        });
    }

    [Test]
    public async Task EditorRequestRedeliveryAfterItsResultWasEvicted_IsNotExecutedAgain()
    {
        var harness = new TransientRequestHarness(retainedResults: 1);
        var request = harness.Request("delivery-1");

        await harness.DispatchAsync(request);
        // Another result pushes the first one out of the bounded result cache.
        await harness.DispatchAsync(harness.Request("delivery-other", requestId: "request-2"));
        var redelivery = request.Clone();
        redelivery.DeliveryId = "delivery-2";
        await harness.DispatchAsync(redelivery);

        var last = harness.Acknowledgements.Last();
        Assert.Multiple(() =>
        {
            Assert.That(harness.Executions, Is.EqualTo(2));
            Assert.That(last.DeliveryId, Is.EqualTo("delivery-2"));
            Assert.That(last.TransportSucceeded, Is.False);
            Assert.That(last.StatusCode, Is.EqualTo(409));
        });
    }

    [Test]
    public async Task ExpiredEditorRequest_IsAcknowledgedWithoutRunningAnything()
    {
        var harness = new TransientRequestHarness();
        var request = harness.Request("delivery-1");
        request.ExpiresUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1));

        await harness.DispatchAsync(request);

        var ack = harness.Acknowledgements.Single();
        Assert.Multiple(() =>
        {
            Assert.That(harness.Executions, Is.Zero);
            Assert.That(ack.TransportSucceeded, Is.False);
            Assert.That(ack.StatusCode, Is.EqualTo(504));
            Assert.That(harness.Deliveries.InFlightCount, Is.Zero);
        });
    }

    [Test]
    public async Task InterruptThatExpiresWhileQueued_DoesNotCancelPrompts()
    {
        using var ordinaryControlGate = new SemaphoreSlim(0, 1);
        using var readGate = new SemaphoreSlim(1, 1);
        var interruptsApplied = 0;
        var request = new RelayEditorRuntimeRequestCommand
        {
            RequestId = "interrupt-1",
            DeliveryId = "delivery-1",
            RunnerId = CommandRunnerId,
            SessionId = CommandSessionId,
            Runtime = Rt(InstanceA, 3).ToRef(),
            Kind = Offloadr.EditorRuntime.V1.EditorRuntimeRequestKind.Control,
            Method = "POST",
            Path = "interrupt",
            ExpiresUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow.AddMilliseconds(200)),
        };

        // Another control request holds the queue until after this one's deadline.
        var execution = ServiceClientManager.ExecuteTransientEditorRuntimeRequestAsync(
            request,
            CommandRunnerId,
            sessionManager: null!,
            sessionEventRelay: null!,
            ordinaryControlGate,
            readGate,
            () => Interlocked.Increment(ref interruptsApplied),
            CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        ordinaryControlGate.Release();
        var response = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(504));
            Assert.That(interruptsApplied, Is.Zero);
        });
    }

    [Test]
    public async Task EditorRequestForAnotherRunner_IsRefusedWithoutRunning()
    {
        var harness = new TransientRequestHarness();
        var request = harness.Request("delivery-1");
        request.RunnerId = "99999999999949998999999999999999";

        await harness.DispatchAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Executions, Is.Zero);
            Assert.That(harness.Acknowledgements.Single().StatusCode, Is.EqualTo(403));
            Assert.That(harness.Deliveries.InFlightCount, Is.Zero);
            Assert.That(harness.Deliveries.ExecutedMarkerCount, Is.Zero);
            Assert.That(harness.Cancellation.Count, Is.Zero);
        });

        // Refusing an unseen destination must not reserve its request identity.
        request.RunnerId = CommandRunnerId;
        request.DeliveryId = "correct-destination";
        await harness.DispatchAsync(request);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Executions, Is.EqualTo(1));
            Assert.That(harness.Acknowledgements.Single(ack => ack.DeliveryId == "correct-destination").StatusCode, Is.EqualTo(200));
        });
    }

    [Test]
    public async Task EditorRequestWithoutADeadline_IsRefusedWithoutRunning()
    {
        var harness = new TransientRequestHarness();
        var request = harness.Request("delivery-1");
        request.ExpiresUtc = null;

        await harness.DispatchAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Executions, Is.Zero);
            Assert.That(harness.Acknowledgements.Single().StatusCode, Is.EqualTo(504));
        });
    }

    [Test]
    public async Task EditorRequestsPastTheMarkerLimit_AreRefusedRatherThanForgettingExecutedOnes()
    {
        var previousLimit = ServiceClientManager.TransientRequestDeliveries.MaxExecutedMarkers;
        ServiceClientManager.TransientRequestDeliveries.MaxExecutedMarkers = 1;
        try
        {
            var harness = new TransientRequestHarness(retainedResults: 1);
            var first = harness.Request("delivery-1");
            await harness.DispatchAsync(first);

            await harness.DispatchAsync(harness.Request("delivery-other", requestId: "request-2"));
            var redelivery = first.Clone();
            redelivery.DeliveryId = "delivery-2";
            await harness.DispatchAsync(redelivery);

            var acknowledgements = harness.Acknowledgements;
            Assert.Multiple(() =>
            {
                Assert.That(harness.Executions, Is.EqualTo(1));
                Assert.That(acknowledgements.Single(ack => ack.DeliveryId == "delivery-other").StatusCode, Is.EqualTo(503));
                // The first request's result is still retained, so its redelivery replays it.
                Assert.That(acknowledgements.Single(ack => ack.DeliveryId == "delivery-2").StatusCode, Is.EqualTo(200));
            });
        }
        finally
        {
            ServiceClientManager.TransientRequestDeliveries.MaxExecutedMarkers = previousLimit;
        }
    }

    private sealed class TransientRequestHarness(int retainedResults = 256)
    {
        private int _executions;


        public ServiceClientManager.TransientRequestDeliveries Deliveries { get; } = new(new ServiceClientManager.TransientAcknowledgementCache(retainedResults));
        public ServiceClientManager.TransientCancellationRegistry Cancellation { get; } = new(CancellationToken.None);
        public ConcurrentQueue<AcknowledgeEditorRuntimeRequestRequest> AcknowledgementQueue { get; } = new();
        public List<AcknowledgeEditorRuntimeRequestRequest> Acknowledgements => [.. AcknowledgementQueue];
        public int Executions => Volatile.Read(ref _executions);
        public Task BeforeResult { get; set; } = Task.CompletedTask;

        public RelayEditorRuntimeRequestCommand Request(string deliveryId, string requestId = "request-1") => new()
        {
            RequestId = requestId,
            DeliveryId = deliveryId,
            RunnerId = CommandRunnerId,
            SessionId = CommandSessionId,
            Runtime = Rt(InstanceA, 3).ToRef(),
            Kind = Offloadr.EditorRuntime.V1.EditorRuntimeRequestKind.Read,
            ExpiresUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(1)),
        };

        public Task DispatchAsync(RelayEditorRuntimeRequestCommand request)
            => ServiceClientManager.DispatchTransientEditorRuntimeRequest(
                request,
                CommandRunnerId,
                Deliveries,
                Cancellation,
                async (command, token) =>
                {
                    var execution = Interlocked.Increment(ref _executions);
                    await BeforeResult.WaitAsync(token).ConfigureAwait(false);
                    return new AcknowledgeEditorRuntimeRequestRequest
                    {
                        RequestId = command.RequestId,
                        DeliveryId = command.DeliveryId,
                        RunnerId = CommandRunnerId,
                        SessionId = command.SessionId,
                        TransportSucceeded = true,
                        StatusCode = 200,
                        Body = Google.Protobuf.ByteString.CopyFromUtf8($"execution-{execution}"),
                    };
                },
                (ack, _) =>
                {
                    AcknowledgementQueue.Enqueue(ack.Clone());
                    return Task.CompletedTask;
                },
                CancellationToken.None);
    }

    [Test]
    public async Task RevokedReport_ForASupersededRuntime_LeavesTheNewerRuntimeAlone()
    {
        var harness = RunningHarness(InstanceB, 4);

        await RevokeAsync(harness, Rt(InstanceA, 3));

        Assert.Multiple(() =>
        {
            Assert.That(harness.Calls, Is.Empty);
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(Rt(InstanceB, 4)));
            Assert.That(harness.Logical.GetActiveSessionId(), Is.EqualTo(CommandSessionId));
            Assert.That(harness.Identities.TryGet(CommandSessionId, out _), Is.True);
        });
    }

    [Test]
    public async Task RevokedReport_ForTheCurrentRuntime_StopsItAndClearsItsState()
    {
        var harness = RunningHarness(InstanceA, 3);

        await RevokeAsync(harness, Rt(InstanceA, 3));

        Assert.Multiple(() =>
        {
            Assert.That(harness.Calls, Does.Contain("stop:" + CommandSessionId));
            Assert.That(harness.Tracked(CommandSessionId), Is.Null);
            Assert.That(harness.Logical.GetActiveSessionId(), Is.Empty);
            Assert.That(harness.Identities.TryGet(CommandSessionId, out _), Is.False);
        });
    }

    [Test]
    public async Task RevokedReport_WithoutIdentity_IsIgnoredOnceARuntimeWasAdopted()
    {
        var harness = RunningHarness(InstanceA, 3);

        await RevokeAsync(harness, reported: null);

        Assert.That(harness.Calls, Is.Empty);
        Assert.That(harness.Tracked(CommandSessionId), Is.Not.Null);
    }

    private static Task RevokeAsync(RuntimeCommandHarness harness, RuntimeIdentity? reported)
    {
        var deps = harness.Deps;
        return ServiceClientManager.HandleRevokedRuntimeReportAsync(
            CommandSessionId,
            reported,
            harness.Logical,
            harness.Identities,
            deps.GetActiveRuntimeSessionId,
            deps.StopRuntime,
            deps.StopRuntimeIfIdentityMatches,
            deps.StopSessionRelay,
            deps.StopArtifactUploads,
            deps.StopWorkspaceMirrors,
            deps.CancelSessionDownloads,
            deps.GetDownloadActiveSessionId,
            deps.SetDownloadActiveSession);
    }

    private static RuntimeIdentity Rt(string runtimeId, ulong runtimeOrder) => new(runtimeId, runtimeOrder);

    /// <summary>Makes the runtime the session's own, as a start admitted and adopted here would.</summary>
    private static void Adopt(ServiceClientManager.LogicalSessionState state, string sessionId, RuntimeIdentity runtime)
    {
        state.AdmitStart(sessionId, runtime);
        Assert.That(state.TryAdoptRuntime(sessionId, runtime), Is.True, "The test runtime must be adoptable.");
    }

    private static StopSessionCommand Stop(ulong upToRuntimeOrder)
        => new()
        {
            SessionId = CommandSessionId,
            User = "alice",
            CommandId = Guid.NewGuid().ToString("n"),
            UpToRuntimeOrder = upToRuntimeOrder,
        };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for condition.");
            }

            await Task.Delay(10);
        }
    }

    private static RuntimeCommandHarness RunningHarness(string runtimeId, ulong runtimeOrder)
    {
        var harness = new RuntimeCommandHarness();
        harness.Run(CommandSessionId, Rt(runtimeId, runtimeOrder));
        return harness;
    }

    private static StartRuntimeCommand Start(string runtimeId, ulong runtimeOrder, string sessionId = CommandSessionId)
        => new()
        {
            SessionId = sessionId,
            User = "alice",
            EditorSid = "editor",
            EditorRuntimeKind = "comfyui",
            LifecycleGeneration = 7,
            Runtime = new RuntimeRef { RuntimeId = runtimeId, RuntimeOrder = runtimeOrder },
        };

    /// <summary>
    /// Binds the runtime command handlers to an in-memory runtime table so that
    /// identity fencing can be observed without a child process.
    /// </summary>
    private sealed class RuntimeCommandHarness
    {
        private readonly ConcurrentDictionary<string, RuntimeIdentity> _tracked = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Guid> _processes = new(StringComparer.Ordinal);

        public RuntimeCommandHarness(int maxStopCutoffs = 16_384)
        {
            WorkState = new ServiceClientManager.RunnerCommandWorkState(Shutdown.Token);
            TransientCancellation = new ServiceClientManager.TransientCancellationRegistry(Shutdown.Token);
            Logical = new ServiceClientManager.LogicalSessionState(string.Empty, maxStopCutoffs);
        }

        public CancellationTokenSource Shutdown { get; } = new();
        public RuntimeCommandSink Sink { get; } = new();
        public ServiceClientManager.RunnerCommandWorkState WorkState { get; }
        public ServiceClientManager.LogicalSessionState Logical { get; }
        public RuntimeIdentityRegistry Identities { get; } = new();
        public ServiceClientManager.TransientCancellationRegistry TransientCancellation { get; }
        public ConcurrentQueue<string> Calls { get; } = new();
        public ConcurrentQueue<StartRuntimeCommand> Starts { get; } = new();
        public string InstanceId { get; set; } = "process-1";
        public SessionSealingStatus? SealingStatus { get; set; }

        public Func<StartRuntimeCommand, bool>? SealsStart { get; set; }

        /// <summary>Lost artifacts the next completed Stop takes, as the uploader keeps them.</summary>
        public int UnsavedArtifacts
        {
            get => Volatile.Read(ref _unsavedArtifacts);
            set => Volatile.Write(ref _unsavedArtifacts, value);
        }

        private int _unsavedArtifacts;

        /// <summary>The session each Stop took the lost artifacts of.</summary>
        public ConcurrentQueue<string> UnsavedTakenFor { get; } = new();

        /// <summary>Sessions whose durable sealed-lifecycle note was dropped.</summary>
        public ConcurrentQueue<string> ForgottenLifecycles { get; } = new();

        /// <summary>Whether the uploader could not count every lost artifact.</summary>
        public bool UnsavedArtifactsIncomplete { get; set; }

        /// <summary>Tracks a start's runtime before <see cref="BeforeStart"/>, as a child that launched and then failed would be.</summary>
        public bool TrackBeforeStart { get; set; }

        /// <summary>Runs before a start registers its runtime; may block or throw.</summary>
        public Func<StartRuntimeCommand, CancellationToken, Task> BeforeStart { get; set; } = (_, _) => Task.CompletedTask;

        /// <summary>Runs before a stop removes its runtime; may block or throw.</summary>
        public Func<string, CancellationToken, Task> BeforeStop { get; set; } = (_, _) => Task.CompletedTask;

        public RuntimeIdentity? Tracked(string sessionId)
            => _tracked.TryGetValue(sessionId, out var identity) ? identity : null;

        /// <summary>Tracks a new process of <paramref name="identity"/>, as a fresh launch would.</summary>
        public void Track(string sessionId, RuntimeIdentity identity)
        {
            _tracked[sessionId] = identity;
            _processes[sessionId] = Guid.NewGuid();
        }

        /// <summary>Makes the runtime the session's running one, as a finished start would.</summary>
        public void Run(string sessionId, RuntimeIdentity runtime)
        {
            Adopt(Logical, sessionId, runtime);
            Identities.Set(sessionId, runtime, lifecycleGeneration: 7);
            Track(sessionId, runtime);
        }

        public void Untrack(string sessionId)
        {
            _tracked.TryRemove(sessionId, out _);
            _processes.TryRemove(sessionId, out _);
        }

        public Guid? TrackedProcess(string sessionId, RuntimeIdentity runtime)
            => _tracked.TryGetValue(sessionId, out var tracked) && tracked.SameRuntime(runtime)
                && _processes.TryGetValue(sessionId, out var process) ? process : null;

        /// <summary>Starts prompt work for the runtime that runs until it is cancelled.</summary>
        public Task StartPrompt(string runtimeId, ulong runtimeOrder)
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var prompt = new SubmitPromptCommand
            {
                HandoffId = Guid.NewGuid().ToString("n"),
                SessionId = CommandSessionId,
                Runtime = new RuntimeRef { RuntimeId = runtimeId, RuntimeOrder = runtimeOrder },
            };
            WorkState.Prompts.Start(prompt, false, async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                }
                finally
                {
                    completed.TrySetResult();
                }
            });
            return completed.Task;
        }

        public ServiceClientManager.RuntimeCommandDependencies Deps => new()
        {
            RunnerId = CommandRunnerId,
            InstanceId = InstanceId,
            Sink = Sink,
            StartAcknowledgement = factory => factory(),
            WorkState = WorkState,
            LogicalSessionState = Logical,
            RuntimeIdentities = Identities,
            TransientCancellation = TransientCancellation,
            GetActiveRuntimeSessionId = () => _tracked.Keys.FirstOrDefault() ?? string.Empty,
            StartRuntimeAndWaitForReady = StartAsync,
            StopRuntime = StopAsync,
            StopRuntimeIfIdentityMatches = async (sessionId, expected, beforeCleanup, token) =>
            {
                if (_tracked.TryGetValue(sessionId, out var tracked) && !tracked.SameRuntime(expected))
                {
                    return false;
                }

                await StopAsync(sessionId, beforeCleanup, token).ConfigureAwait(false);
                return true;
            },
            GetTrackedRuntime = sessionId => Tracked(sessionId),
            GetTrackedProcess = TrackedProcess,
            StopSessionRelay = sessionId => Record("relay:" + sessionId),
            StopArtifactUploads = sessionId => Record("uploads:" + sessionId),
            StopWorkspaceMirrors = sessionId => Record("mirror:" + sessionId),
            CancelSessionDownloads = sessionId => Calls.Enqueue("downloads:" + sessionId),
            GetDownloadActiveSessionId = () => null,
            SetDownloadActiveSession = _ => { },
            ActivateSession = (activate, token) =>
            {
                token.ThrowIfCancellationRequested();
                activate();
                return Task.CompletedTask;
            },
            Shutdown = Shutdown.Token,
            GetSessionSealing = _ => SealingStatus,
            SealsStart = SealsStart,
            TakeUnsavedArtifacts = sessionId =>
            {
                UnsavedTakenFor.Enqueue(sessionId);
                return new UnsavedUploads(Interlocked.Exchange(ref _unsavedArtifacts, 0), UnsavedArtifactsIncomplete);
            },
            ForgetSealedLifecycle = ForgottenLifecycles.Enqueue,
        };

        private Task Record(string call)
        {
            Calls.Enqueue(call);
            return Task.CompletedTask;
        }

        private async Task StartAsync(StartRuntimeCommand command, CancellationToken token)
        {
            Calls.Enqueue($"start:{command.Runtime.RuntimeOrder}");
            Starts.Enqueue(command.Clone());
            var identity = RuntimeIdentity.From(command.Runtime);
            if (_tracked.TryGetValue(command.SessionId, out var existing) && existing != identity)
            {
                throw new InvalidOperationException("The tracked session is a different runtime.");
            }

            if (TrackBeforeStart)
            {
                Track(command.SessionId, identity);
            }

            await BeforeStart(command, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Track(command.SessionId, identity);
        }

        private async Task StopAsync(string sessionId, Func<string, CancellationToken, Task>? beforeCleanup, CancellationToken token)
        {
            Calls.Enqueue("stop:" + sessionId);
            await BeforeStop(sessionId, token).ConfigureAwait(false);
            Untrack(sessionId);
            if (beforeCleanup is not null)
            {
                await beforeCleanup(sessionId, token).ConfigureAwait(false);
            }
        }
    }

    private sealed class RuntimeCommandSink : IRunnerSessionSink
    {

        private int _failuresRemaining;

        public ConcurrentQueue<AcknowledgeRuntimeStartRequest> StartAcks { get; } = new();
        public ConcurrentQueue<AcknowledgeSessionStopRequest> StopAcks { get; } = new();
        public ConcurrentQueue<AcknowledgeEditorRuntimeRequestRequest> RequestAcks { get; } = new();
        public ConcurrentQueue<object> Attempts { get; } = new();

        /// <summary>Fails the next acknowledgement attempts with a retryable status.</summary>
        public void FailNextAcknowledgements(int count) => Volatile.Write(ref _failuresRemaining, count);

        /// <summary>Fails every matching acknowledgement attempt with a retryable status.</summary>
        public Func<object, bool> FailWhen { get; set; } = _ => false;

        public async Task<T> WaitForAsync<T>(ConcurrentQueue<T> queue, int count, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            while (queue.Count < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail($"Timed out waiting for {count} acknowledgement(s) of {typeof(T).Name}; saw {queue.Count}.");
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            return queue.ElementAt(count - 1);
        }

        private Task Record<T>(ConcurrentQueue<T> queue, T request) where T : Google.Protobuf.IMessage<T>
        {
            Attempts.Enqueue(request.Clone());
            if (FailWhen(request) || Interlocked.Decrement(ref _failuresRemaining) >= 0)
            {
                return Task.FromException(new RpcException(new Status(StatusCode.Unavailable, "controlled lost acknowledgement")));
            }

            queue.Enqueue(request.Clone());
            return Task.CompletedTask;
        }

        public Task AckRuntimeStartAsync(AcknowledgeRuntimeStartRequest request, CancellationToken cancellationToken) => Record(StartAcks, request);
        public Task AckStopSessionAsync(AcknowledgeSessionStopRequest request, CancellationToken cancellationToken) => Record(StopAcks, request);
        public Task AckEditorRuntimeRequestAsync(AcknowledgeEditorRuntimeRequestRequest request, CancellationToken cancellationToken) => Record(RequestAcks, request);

        public Task<RegisterRunnerResponse> RegisterRunnerAsync(RegisterRunnerRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new RegisterRunnerResponse { Accepted = true });
        public Task<SyncLocalModelsResponse> SyncLocalModelsAsync(SyncLocalModelsRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new SyncLocalModelsResponse());
        public Task<GrantGpuPowerExecutionResponse> GrantGpuPowerExecutionAsync(GrantGpuPowerExecutionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task AckGpuPowerLimitAsync(AcknowledgeGpuPowerLimitRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GrantPromptExecutionResponse> GrantPromptExecutionAsync(GrantPromptExecutionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task AckSubmitEditorActionAsync(AcknowledgePromptResultRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportModelDownloadAsync(ReportModelDownloadRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportSessionEventsAsync(ReportSessionEventsRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportSessionLogsAsync(ReportSessionLogsRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportRunnerLogsAsync(ReportRunnerLogsRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportRuntimeTelemetryAsync(ReportRuntimeTelemetryRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
