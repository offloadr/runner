using System.Runtime.CompilerServices;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public sealed partial class PromptCommandWorkSetTests
{
    [Test]
    public async Task JoinedFaultedDemand_DoesNotRetainExceptionPayloadWhileIdle()
    {
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var payload = StartFaultedDemandWithPayload(work);
        try
        {
            await work.JoinCurrentOperationsAsync().WaitAsync(TimeSpan.FromSeconds(5));
            // Observe completion without cancellation, retirement or pruning.
            for (var attempt = 0; attempt < 30 && payload.IsAlive; attempt++)
            {
                await Task.Delay(10);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            Assert.That(payload.IsAlive, Is.False);
        }
        finally { await work.CancelAndWaitAsync(_ => true); }
    }

    [Test]
    public async Task SettledFaultedDemand_DoesNotRetainExceptionPayloadWhileIdle()
    {
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var payload = StartFaultedDemandWithPayload(work);
        try
        {
            for (var attempt = 0; attempt < 30 && payload.IsAlive; attempt++)
            {
                await Task.Delay(10);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            Assert.That(payload.IsAlive, Is.False, "Finished settling-task exception data must not survive until another command or shutdown.");
        }
        finally { await work.CancelAndWaitAsync(_ => true); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference StartFaultedDemandWithPayload(PromptCommandWorkSet work)
    {
        var settling = Gate();
        var content = new byte[65536];
        var reference = new WeakReference(content);
        var failure = new InvalidOperationException("fixture settling failure");
        failure.Data["synthetic-payload"] = content;
        Assert.That(work.Start(new SubmitPromptCommand { CommandId = "faulted-demand", SessionId = "session" }, false,
            (command, _, _) =>
            {
                work.HoldDemand(command.CommandId, settling.Task);
                return Task.CompletedTask;
            }), Is.True);
        settling.SetException(failure);
        return reference;
    }

    [Test]
    public async Task RepeatedStop_JoinsTheOriginalCancellationCallbackAfterExecutionEnds()
    {
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var execution = Gate();
        var callbackEntered = Gate();
        var releaseCallback = Gate();
        CancellationTokenRegistration registration = default;
        var callbackCalls = 0;
        try
        {
            Assert.That(work.Start(new() { CommandId = "held-callback", SessionId = "session" }, false, (_, token) =>
            {
                registration = token.Register(() =>
                {
                    Interlocked.Increment(ref callbackCalls);
                    callbackEntered.SetResult();
                    releaseCallback.Task.GetAwaiter().GetResult();
                });
                return execution.Task;
            }), Is.True);
            var first = work.CancelAndWaitAsync(_ => true);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = work.CancelAndWaitAsync(_ => true);
            execution.SetResult();
            await Task.Delay(50);
            Assert.Multiple(() =>
            {
                Assert.That(first.IsCompleted, Is.False);
                Assert.That(second.IsCompleted, Is.False);
                Assert.That(callbackCalls, Is.EqualTo(1));
            });
            releaseCallback.SetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            execution.TrySetResult(); releaseCallback.TrySetResult();
            await work.CancelAndWaitAsync(_ => true);
            registration.Dispose();
        }
    }

    [Test]
    public async Task StopBlocksMatchingAdmissionWhileOtherSessionsCanStillStart()
    {
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var release = Gate();
        try
        {
            Assert.That(work.Start(new() { CommandId = "original", SessionId = "closing" }, false,
                (_, _) => release.Task), Is.True);
            var stop = work.CancelAndWaitAsync(identity => identity.SessionId == "closing");
            Assert.That(work.Start(new() { CommandId = "late", SessionId = "closing" }, false,
                (_, _) => Task.CompletedTask), Is.False);
            Assert.That(work.Start(new() { CommandId = "other", SessionId = "unrelated" }, false,
                (_, _) => Task.CompletedTask), Is.True);
            Assert.That(stop.IsCompleted, Is.False);
            release.SetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(work.Start(new() { CommandId = "replacement", SessionId = "closing" }, false,
                (_, _) => Task.CompletedTask), Is.True);
        }
        finally
        {
            release.TrySetResult();
            await work.CancelAndWaitAsync(_ => true);
        }
    }

    [Test]
    public async Task HostShutdown_RejectsNewPhysicalWork()
    {
        using var shutdown = new CancellationTokenSource();
        var work = new PromptCommandWorkSet(shutdown.Token);
        await shutdown.CancelAsync();
        var calls = 0;
        Assert.That(work.Start(new() { CommandId = "late", SessionId = "session" }, false,
            (_, _) => { calls++; return Task.CompletedTask; }), Is.False);
        await work.CancelAndWaitAsync(_ => true);
        Assert.That(calls, Is.Zero);
    }

    [Test]
    public async Task Stop_JoinsDemandReplacedByARecoveryDelivery()
    {
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var oldSettling = Gate();
        var newSettling = Gate();
        CancellationToken oldDemand = default;
        try
        {
            Assert.That(work.Start(new() { CommandId = "recovered", SessionId = "session" }, false,
                (command, _, demand) =>
                {
                    oldDemand = demand;
                    work.HoldDemand(command.CommandId, oldSettling.Task);
                    return Task.CompletedTask;
                }), Is.True);
            Assert.That(work.Start(new() { CommandId = "recovered", SessionId = "session" }, false,
                (command, _, _) =>
                {
                    work.HoldDemand(command.CommandId, newSettling.Task);
                    return Task.CompletedTask;
                }), Is.True);
            Assert.That(oldDemand.IsCancellationRequested, Is.True);
            var stop = work.CancelAndWaitAsync(_ => true);
            newSettling.SetResult();
            await Task.Delay(50);
            Assert.That(stop.IsCompleted, Is.False, "The old original settling task still belongs to this session.");
            oldSettling.SetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            oldSettling.TrySetResult(); newSettling.TrySetResult();
            await work.CancelAndWaitAsync(_ => true);
        }
    }

    [Test]
    public async Task StopDuringExecution_JoinsItsLaterDemandHandoff()
    {
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var execution = Gate();
        var settling = Gate();
        try
        {
            Assert.That(work.Start(new() { CommandId = "handoff", SessionId = "session" }, false,
                (command, _, _) =>
                {
                    work.HoldDemand(command.CommandId, settling.Task);
                    return execution.Task;
                }), Is.True);
            var stop = work.CancelAndWaitAsync(_ => true);
            execution.SetResult();
            await Task.Delay(50);
            Assert.That(stop.IsCompleted, Is.False);
            Assert.That(settling.Task.IsCompleted, Is.False);
            settling.SetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            execution.TrySetResult(); settling.TrySetResult();
            await work.CancelAndWaitAsync(_ => true);
        }
    }

    [Test]
    public async Task CompletedPromptContentCanCollectWhileItsCancellationCallbackIsHeld()
    {
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var execution = Gate();
        var callbackEntered = Gate();
        var releaseCallback = Gate();
        var (command, body, registration) = StartPayloadWithHeldCancellation(work, execution.Task, callbackEntered, releaseCallback.Task);
        try
        {
            var stop = work.CancelAndWaitAsync(_ => true);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            execution.SetResult();
            for (var attempt = 0; attempt < 30 && (command.IsAlive || body.IsAlive); attempt++)
            {
                await Task.Delay(10);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            Assert.Multiple(() =>
            {
                Assert.That(command.IsAlive, Is.False);
                Assert.That(body.IsAlive, Is.False);
                Assert.That(stop.IsCompleted, Is.False);
            });
            releaseCallback.SetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            execution.TrySetResult(); releaseCallback.TrySetResult();
            await work.CancelAndWaitAsync(_ => true);
            registration.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Command, WeakReference Body, CancellationTokenRegistration Registration) StartPayloadWithHeldCancellation(
        PromptCommandWorkSet work, Task execution, TaskCompletionSource entered, Task release)
    {
        WeakReference? command = null;
        WeakReference? body = null;
        CancellationTokenRegistration registration = default;
        Assert.That(work.Start(new SubmitPromptCommand { CommandId = "payload", SessionId = "session", PromptJson = new string('x', 65536) }, false,
            (captured, token) =>
            {
                command = new(captured);
                body = new(captured.PromptJson);
                registration = token.Register(() =>
                {
                    entered.SetResult();
                    release.GetAwaiter().GetResult();
                });
                return execution;
            }), Is.True);
        return (command!, body!, registration);
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Test]
    public async Task CancellationCallbackFailure_CannotEscapeBeforeBothOriginalExecutionsEnd()
    {
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFailure = new InvalidOperationException("fixture cancellation callback failure");
        Task? firstOriginal = null;
        Task? secondOriginal = null;
        Task? stopping = null;
        var secondToken = CancellationToken.None;
        try
        {
            Assert.That(work.Start(new() { CommandId = "first", SessionId = "original" }, false,
                (_, token) => firstOriginal = FirstAsync(token)), Is.True);
            Assert.That(work.Start(new() { CommandId = "second", SessionId = "original" }, false,
                (_, token) =>
                {
                    secondToken = token;
                    secondEntered.SetResult();
                    return secondOriginal = secondRelease.Task;
                }), Is.True);
            await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Exception? synchronousFailure = null;
            try { stopping = work.CancelAndWaitAsync(_ => true); }
            catch (Exception error) { synchronousFailure = error; }
            TestContext.Out.WriteLine(
                $"Original executions held before release: first={firstOriginal!.IsCompleted}, second={secondOriginal!.IsCompleted}; " +
                $"synchronousCancellationFailure={synchronousFailure?.GetType().Name ?? "none"}.");
            Assert.Multiple(() =>
            {
                Assert.That(firstOriginal.IsCompleted, Is.False);
                Assert.That(secondOriginal.IsCompleted, Is.False);
                Assert.That(synchronousFailure, Is.Null, "Cancellation failure must be reported by the pending original join, after executions end.");
                Assert.That(stopping, Is.Not.Null);
            });
            Assert.That(stopping!.IsCompleted, Is.False);
            Assert.That(secondToken.IsCancellationRequested, Is.True, "A failed callback cannot skip its peer's cancellation.");
            firstRelease.SetResult();
            await firstOriginal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(stopping.IsCompleted, Is.False);
            secondRelease.SetResult();
            await secondOriginal.WaitAsync(TimeSpan.FromSeconds(5));
            var failure = Assert.ThrowsAsync<AggregateException>(() => stopping.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(failure!.Flatten().InnerExceptions, Does.Contain(callbackFailure));
        }
        finally
        {
            firstRelease.TrySetResult();
            secondRelease.TrySetResult();
            if (firstOriginal is not null) await firstOriginal;
            if (secondOriginal is not null) await secondOriginal;
            // Preserve an assertion failure rather than replacing it with the
            // expected callback error during owning fixture cleanup.
            if (stopping is not null)
            {
                try { await stopping; }
                catch (AggregateException error) when (error.Flatten().InnerExceptions.Contains(callbackFailure)) { }
            }
            try { await work.CancelAndWaitAsync(_ => true); }
            catch (AggregateException error) when (error.Flatten().InnerExceptions.Contains(callbackFailure)) { }
        }

        async Task FirstAsync(CancellationToken token)
        {
            using var registration = token.Register(() => throw callbackFailure);
            firstEntered.SetResult();
            await firstRelease.Task;
        }
    }
}
