using System.Runtime.CompilerServices;
using Moq;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public sealed class GpuPowerShutdownTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task RepeatedDrain_JoinsOriginalGrantCallback(bool callbackFails)
    {
        var executor = Executor();
        var started = Gate();
        var callbackEntered = Gate();
        var releaseCallback = Gate();
        var response = new TaskCompletionSource<GrantGpuPowerExecutionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        Task? drain = null;
        var work = executor.HandleAsync(Command("held"), "runner", () => "session", () => 1,
            (_, token) =>
            {
                registration = token.Register(() =>
                {
                    callbackEntered.TrySetResult();
                    releaseCallback.Task.GetAwaiter().GetResult();
                    if (callbackFails) throw new InvalidOperationException("synthetic callback failure");
                });
                started.TrySetResult();
                return response.Task;
            }, (_, _) => throw new AssertionException("A denied grant has no acknowledgement."), default);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            drain = executor.CancelAndDrainAsync();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            response.SetResult(new() { MayExecute = false });
            await Task.Delay(50);
            Assert.Multiple(() =>
            {
                Assert.That(executor.CancelAndDrainAsync(), Is.SameAs(drain));
                Assert.That(work.IsCompleted, Is.False, "Delivery still owns its original callback task.");
                Assert.That(drain.IsCompleted, Is.False);
            });
            await executor.HandleAsync(Command("late"), "runner", () => "session", () => 1,
                (_, _) => throw new AssertionException("Closed admission must not request another grant."),
                (_, _) => throw new AssertionException("Closed admission must not acknowledge."), default);
            Assert.CatchAsync<OperationCanceledException>(() => executor.ActivateSessionAsync(
                () => Assert.Fail("Closed admission must not activate."), default));
            releaseCallback.SetResult();
            await Task.WhenAll(work, drain).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            response.TrySetResult(new() { MayExecute = false });
            releaseCallback.TrySetResult();
            await Task.WhenAll(work, drain ?? executor.CancelAndDrainAsync()).WaitAsync(TimeSpan.FromSeconds(5));
            registration.Dispose();
        }
    }

    [Test]
    public async Task CallbackFailure_CannotSkipCancellationOrJoinsOfAnotherDelivery()
    {
        var executor = Executor();
        var firstStarted = Gate();
        var peerStarted = Gate();
        var peerCancelled = Gate();
        var firstResponse = new TaskCompletionSource<GrantGpuPowerExecutionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerResponse = new TaskCompletionSource<GrantGpuPowerExecutionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration firstRegistration = default;
        CancellationTokenRegistration peerRegistration = default;
        Task? drain = null;
        var first = executor.HandleAsync(Command("first"), "runner", () => "session", () => 1,
            (_, token) =>
            {
                firstRegistration = token.Register(() => throw new InvalidOperationException("synthetic callback failure"));
                firstStarted.TrySetResult();
                return firstResponse.Task;
            }, (_, _) => throw new AssertionException("A denied grant has no acknowledgement."), default);
        var peer = executor.HandleAsync(Command("peer"), "runner", () => "session", () => 1,
            (_, token) =>
            {
                peerRegistration = token.Register(() => peerCancelled.TrySetResult());
                peerStarted.TrySetResult();
                return peerResponse.Task;
            }, (_, _) => throw new AssertionException("A denied grant has no acknowledgement."), default);
        try
        {
            await Task.WhenAll(firstStarted.Task, peerStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            drain = executor.CancelAndDrainAsync();
            await peerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(drain.IsCompleted, Is.False);
            firstResponse.SetResult(new() { MayExecute = false });
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(drain.IsCompleted, Is.False, "The peer's original grant response is still held.");
            peerResponse.SetResult(new() { MayExecute = false });
            await Task.WhenAll(first, peer, drain).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            firstResponse.TrySetResult(new() { MayExecute = false });
            peerResponse.TrySetResult(new() { MayExecute = false });
            await Task.WhenAll(first, peer, drain ?? executor.CancelAndDrainAsync()).WaitAsync(TimeSpan.FromSeconds(5));
            firstRegistration.Dispose(); peerRegistration.Dispose();
        }
    }

    [Test]
    public async Task CallerShutdown_StillJoinsTheOriginalCallbackBeforeDeliveryEnds()
    {
        using var host = new CancellationTokenSource();
        var executor = Executor();
        var started = Gate();
        var callbackEntered = Gate();
        var releaseCallback = Gate();
        var response = new TaskCompletionSource<GrantGpuPowerExecutionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        Task? cancellation = null;
        Task? drain = null;
        var work = executor.HandleAsync(Command("host"), "runner", () => "session", () => 1,
            (_, token) =>
            {
                registration = token.Register(() =>
                {
                    callbackEntered.TrySetResult();
                    releaseCallback.Task.GetAwaiter().GetResult();
                });
                started.TrySetResult();
                return response.Task;
            }, (_, _) => throw new AssertionException("A denied grant has no acknowledgement."), host.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation = host.CancelAsync();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            response.SetResult(new() { MayExecute = false });
            drain = executor.CancelAndDrainAsync();
            await Task.Delay(50);
            Assert.Multiple(() =>
            {
                Assert.That(work.IsCompleted, Is.False);
                Assert.That(drain.IsCompleted, Is.False);
            });
            releaseCallback.SetResult();
            await Task.WhenAll(work, cancellation, drain).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            response.TrySetResult(new() { MayExecute = false }); releaseCallback.TrySetResult();
            await Task.WhenAll(work, cancellation ?? Task.CompletedTask,
                drain ?? executor.CancelAndDrainAsync()).WaitAsync(TimeSpan.FromSeconds(5));
            registration.Dispose();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CompletedActivation_DoesNotRetainContentInIdleOwnershipRecords(bool fails)
    {
        var executor = Executor();
        try
        {
            var payload = await CompletePayloadActionAsync(executor, fails);
            for (var attempt = 0; attempt < 20 && payload.IsAlive; attempt++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                await Task.Delay(25);
            }
            Assert.That(payload.IsAlive, Is.False, "Idle ownership records must not retain completed action or exception data.");
        }
        finally
        {
            if (fails)
            {
                var error = Assert.ThrowsAsync<InvalidOperationException>(() => executor.CancelAndDrainAsync())!;
                Assert.That(error.InnerException, Is.Null);
                Assert.That(error.Message, Is.EqualTo("GPU work failed during shutdown."));
            }
            else await executor.CancelAndDrainAsync();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> CompletePayloadActionAsync(GpuPowerCommandExecutor executor, bool fails)
    {
        var payload = new byte[64 * 1024];
        var reference = new WeakReference(payload);
        try
        {
            await executor.ActivateSessionAsync(() =>
            {
                GC.KeepAlive(payload);
                if (!fails) return;
                var error = new InvalidOperationException("synthetic activation failure");
                error.Data["payload"] = payload;
                throw error;
            }, default);
        }
        catch (InvalidOperationException) when (fails) { }
        return reference;
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static SetGpuPowerLimitCommand Command(string id) => new() { CommandId = id, SessionId = "session" };
    private static GpuPowerCommandExecutor Executor()
        => new(new GpuPowerLimitService(new RuntimeTelemetryService(new Mock<IRuntimeTelemetryProbe>(MockBehavior.Strict).Object)));
}
