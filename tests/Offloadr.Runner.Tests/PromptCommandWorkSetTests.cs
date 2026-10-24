using System.Runtime.CompilerServices;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public sealed class PromptCommandWorkSetTests
{
    [Test]
    public async Task HeldModelDemandDoesNotRetainCompletedCommandOrPromptBody()
    {
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var settling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (command, body, demand) = StartHeldDemand(work, settling.Task);
        try
        {
            Assert.That(demand.IsCancellationRequested, Is.False);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.Multiple(() =>
            {
                Assert.That(command.TryGetTarget(out _), Is.False, "Settling model demand must release the completed command.");
                Assert.That(body.TryGetTarget(out _), Is.False, "Settling model demand must release the prompt body.");
            });
            work.WithdrawDemand(identity => identity.CommandId == "held-body");
            Assert.That(demand.IsCancellationRequested, Is.True, "Identity still permits terminal withdrawal.");
        }
        finally
        {
            settling.TrySetResult();
            await work.CancelAndWaitAsync(_ => true).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<SubmitPromptCommand>, WeakReference<string>, CancellationToken) StartHeldDemand(
        PromptCommandWorkSet work, Task settling)
    {
        WeakReference<SubmitPromptCommand>? command = null;
        WeakReference<string>? body = null;
        CancellationToken demand = default;
        Assert.That(work.Start(new() { CommandId = "held-body", SessionId = "synthetic-session", PromptJson = new string('x', 65536) }, false,
            (captured, _, token) =>
            {
                command = new(captured);
                body = new(captured.PromptJson);
                demand = token;
                work.HoldDemand(captured.CommandId, settling);
                return Task.CompletedTask;
            }), Is.True);
        return (command!, body!, demand);
    }

    [Test]
    public async Task HeldDemandWithdrawalPreservesTheOtherRuntime()
    {
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var tokens = new Dictionary<ulong, CancellationToken>();
        var instance = Guid.NewGuid().ToString("n");
        try
        {
            foreach (var epoch in new ulong[] { 1, 2 })
            {
                var command = new SubmitPromptCommand
                {
                    CommandId = "command-" + epoch,
                    SessionId = "session",
                    SubmissionId = "submission-" + epoch,
                    Target = new() { GpuGeneration = 1, RuntimeEpoch = epoch, RuntimeInstanceId = instance },
                };
                Assert.That(work.Start(command, false, (captured, _, demand) =>
                {
                    tokens[epoch] = demand;
                    work.HoldDemand(captured.CommandId, Task.Delay(Timeout.Infinite, demand).ContinueWith(_ => { }));
                    return Task.CompletedTask;
                }), Is.True);
                command.Target.RuntimeEpoch = 99;
            }

            await work.CancelAndWaitAsync(identity => identity.SessionId == "session"
                && identity.Runtime is { RuntimeEpoch: 1 }).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(tokens[1].IsCancellationRequested, Is.True);
            Assert.That(tokens[2].IsCancellationRequested, Is.False);
            work.WithdrawDemand(identity => identity.SubmissionId == "submission-2");
            Assert.That(tokens[2].IsCancellationRequested, Is.True);
        }
        finally
        {
            await work.CancelAndWaitAsync(_ => true).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    public async Task RepeatedDeliveryCannotStartASecondPhysicalRequest()
    {
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new SubmitPromptCommand { CommandId = Guid.NewGuid().ToString("n"), SessionId = "original" };
        var calls = 0;
        string? capturedSession = null;
        Assert.That(work.Start(command, false, async (captured, _) =>
        {
            calls++;
            started.SetResult();
            await release.Task;
            capturedSession = captured.SessionId;
        }), Is.True);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(work.Start(command, false, (_, _) => { calls++; return Task.CompletedTask; }), Is.False);
        command.SessionId = "replacement";
        release.SetResult();
        await work.CancelAndWaitAsync(_ => true);
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(capturedSession, Is.EqualTo("original"));
    }

    [TestCase("withdraw")]
    [TestCase("stop")]
    public async Task HeldDemandLetsRecoveryRunAndStaysWithdrawable(string withdrawal)
    {
        // An accepted prompt keeps only its model demand after its command ends, so a
        // recovery delivery of the same command must start instead of being refused.
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var command = new SubmitPromptCommand { CommandId = "accepted", SessionId = "session" };
        CancellationToken demandToken = default;
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commandEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.That(work.Start(command, false, (captured, _, demand) =>
        {
            demandToken = demand;
            var settling = Task.Delay(Timeout.Infinite, demand).ContinueWith(_ => settled.SetResult());
            work.HoldDemand(captured.CommandId, settling);
            commandEnded.SetResult();
            return Task.CompletedTask;
        }), Is.True);
        await commandEnded.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await WaitUntilStartedAsync(() => work.Start(command, false, (_, _) =>
        {
            recovered.TrySetResult();
            return Task.CompletedTask;
        }));
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(demandToken.IsCancellationRequested, Is.False, "Recovery leaves the held demand in place.");

        if (withdrawal == "withdraw")
            work.WithdrawDemand(prompt => prompt.CommandId == "accepted");
        else
            await work.CancelAndWaitAsync(prompt => prompt.SessionId == "session").WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(demandToken.IsCancellationRequested, Is.True);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task WaitUntilStartedAsync(Func<bool> start)
    {
        // The original work leaves the active set just after its body returns.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!start())
        {
            if (DateTime.UtcNow >= deadline) Assert.Fail("The recovery delivery was refused as a duplicate.");
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task InterruptLeavesModelDemandWhileStopAndTerminalEventWithdrawIt()
    {
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var tokens = new Dictionary<string, (CancellationToken Physical, CancellationToken Demand)>();
        var entered = new CountdownEvent(2);
        foreach (var name in new[] { "queued", "finished" })
            Assert.That(work.Start(new() { CommandId = name, SessionId = "session" }, false, async (_, physical, demand) =>
            {
                lock (tokens) tokens[name] = (physical, demand);
                entered.Signal();
                try { await Task.Delay(Timeout.Infinite, demand); }
                catch (OperationCanceledException) { }
            }), Is.True);
        Assert.That(entered.Wait(TimeSpan.FromSeconds(2)), Is.True);

        // A runtime interrupt stops only its executing prompt; queued ones keep their demand.
        work.Cancel(_ => true);
        Assert.Multiple(() =>
        {
            Assert.That(tokens["queued"].Physical.IsCancellationRequested, Is.True);
            Assert.That(tokens["queued"].Demand.IsCancellationRequested, Is.False);
        });

        // The interrupted prompt's terminal event withdraws only its own demand.
        work.WithdrawDemand(command => command.CommandId == "finished");
        Assert.Multiple(() =>
        {
            Assert.That(tokens["finished"].Demand.IsCancellationRequested, Is.True);
            Assert.That(tokens["queued"].Demand.IsCancellationRequested, Is.False);
        });

        var queuedDemand = tokens["queued"].Demand;
        await work.CancelAndWaitAsync(command => command.SessionId == "session").WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(queuedDemand.IsCancellationRequested, Is.True, "Stop withdraws every matching demand.");
    }

    [Test]
    public async Task StopJoinsEveryMatchingPhysicalRequestBeforeReplacement()
    {
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new CountdownEvent(2);
        var cancelled = new CountdownEvent(2);
        foreach (var name in new[] { "generation", "stream-reader" })
            Assert.That(work.Start(new() { CommandId = name, SessionId = "old" }, false, async (_, token) =>
            {
                entered.Signal();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.Signal(); }
                await release.Task;
            }), Is.True);
        Assert.That(entered.IsSet, Is.True);
        var stopping = work.CancelAndWaitAsync(command => command.SessionId == "old");
        Assert.That(cancelled.Wait(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(stopping.IsCompleted, Is.False);
        release.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task OldSessionCancellationDoesNotCancelAnotherTarget()
    {
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captured = default;
        work.Start(new() { CommandId = "current", SessionId = "new" }, false, async (_, token) => { captured = token; await release.Task; });
        await work.CancelAndWaitAsync(command => command.SessionId == "old");
        Assert.That(captured.IsCancellationRequested, Is.False);
        release.SetResult();
        await work.CancelAndWaitAsync(_ => true);
    }
}
