using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public sealed class PromptCommandWorkSetTests
{
    [Test]
    public async Task RepeatedDeliveryCannotStartASecondPhysicalRequest()
    {
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new SubmitPromptCommand { CommandId = Guid.NewGuid().ToString("n"), SessionId = "original" };
        var calls = 0;
        string? capturedSession = null;
        Assert.That(work.Start(command, async (captured, _) =>
        {
            calls++;
            started.SetResult();
            await release.Task;
            capturedSession = captured.SessionId;
        }), Is.True);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(work.Start(command, (_, _) => { calls++; return Task.CompletedTask; }), Is.False);
        command.SessionId = "replacement";
        release.SetResult();
        await work.CancelAndWaitAsync(_ => true);
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(capturedSession, Is.EqualTo("original"));
    }

    [Test]
    public async Task InterruptLeavesModelDemandWhileStopAndTerminalEventWithdrawIt()
    {
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var tokens = new Dictionary<string, (CancellationToken Physical, CancellationToken Demand)>();
        var entered = new CountdownEvent(2);
        foreach (var name in new[] { "queued", "finished" })
            Assert.That(work.Start(new() { CommandId = name, SessionId = "session" }, async (_, physical, demand) =>
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
            Assert.That(work.Start(new() { CommandId = name, SessionId = "old" }, async (_, token) =>
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
        work.Start(new() { CommandId = "current", SessionId = "new" }, async (_, token) => { captured = token; await release.Task; });
        await work.CancelAndWaitAsync(command => command.SessionId == "old");
        Assert.That(captured.IsCancellationRequested, Is.False);
        release.SetResult();
        await work.CancelAndWaitAsync(_ => true);
    }
}
