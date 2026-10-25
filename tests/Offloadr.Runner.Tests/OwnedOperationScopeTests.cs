using System.Runtime.CompilerServices;
using Offloadr.Runner.Core;

namespace Offloadr.Runner.Tests;

public sealed class OwnedOperationScopeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Test]
    public async Task Closing_JoinsOriginalAndSynchronousBorrowerAndRejectsLateAdmission()
    {
        var scope = new OwnedOperationScope();
        var entered = Gate();
        var original = Gate();
        var lease = scope.TryEnter();
        var operation = scope.Run(_ => { entered.SetResult(); return original.Task; });
        try
        {
            await entered.Task.WaitAsync(Timeout);
            var stop = scope.CompleteAsync();
            Assert.That(scope.CompleteAsync(), Is.SameAs(stop));
            Assert.That(scope.TryEnter(), Is.Null);
            Assert.CatchAsync<OperationCanceledException>(() => scope.Run(_ => throw new AssertionException("Closed admission executed.")));
            original.SetResult();
            await operation.WaitAsync(Timeout);
            Assert.That(stop.IsCompleted, Is.False, "An admitted filesystem borrower remains held.");
            lease!.Dispose();
            await stop.WaitAsync(Timeout);
        }
        finally { original.TrySetResult(); lease?.Dispose(); await scope.CompleteAsync().WaitAsync(Timeout); }
    }

    [Test]
    public async Task Closing_DuringSynchronousFactory_JoinsItsLaterOriginal()
    {
        var scope = new OwnedOperationScope();
        var entered = Gate();
        using var releaseFactory = new ManualResetEventSlim();
        var original = Gate();
        var operation = scope.Run(_ =>
        {
            entered.SetResult();
            releaseFactory.Wait();
            return original.Task;
        });
        try
        {
            await entered.Task.WaitAsync(Timeout);
            var stop = scope.CompleteAsync();
            Assert.That(stop.IsCompleted, Is.False);
            releaseFactory.Set();
            await Task.Delay(50);
            Assert.That(stop.IsCompleted, Is.False);
            original.SetResult();
            await Task.WhenAll(operation, stop).WaitAsync(Timeout);
        }
        finally { releaseFactory.Set(); original.TrySetResult(); await scope.CompleteAsync().WaitAsync(Timeout); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CallerCancellation_JoinsOriginalCallbacksAfterExecutionEnds(bool callbackFails)
    {
        var scope = new OwnedOperationScope();
        using var caller = new CancellationTokenSource();
        var started = Gate();
        var callbackEntered = Gate();
        var releaseCallback = Gate();
        var original = Gate();
        CancellationTokenRegistration registration = default;
        var operation = scope.Run(token =>
        {
            registration = token.Register(() =>
            {
                callbackEntered.TrySetResult();
                releaseCallback.Task.GetAwaiter().GetResult();
                if (callbackFails) throw new InvalidOperationException("synthetic callback detail");
            });
            started.SetResult();
            return original.Task;
        }, caller.Token);
        Task? stop = null;
        try
        {
            await started.Task.WaitAsync(Timeout);
            caller.Cancel();
            await callbackEntered.Task.WaitAsync(Timeout);
            original.SetResult();
            stop = scope.CompleteAsync();
            await Task.Delay(50);
            Assert.That(operation.IsCompleted, Is.False);
            Assert.That(stop.IsCompleted, Is.False);
            releaseCallback.SetResult();
            await operation.WaitAsync(Timeout);
            if (callbackFails)
            {
                var error = Assert.ThrowsAsync<InvalidOperationException>(() => stop.WaitAsync(Timeout))!;
                Assert.That(error.InnerException, Is.Null);
                Assert.That(error.Message, Does.Not.Contain("synthetic callback detail"));
            }
            else await stop.WaitAsync(Timeout);
        }
        finally
        {
            original.TrySetResult(); releaseCallback.TrySetResult();
            try { await Task.WhenAll(operation, stop ?? scope.CompleteAsync()).WaitAsync(Timeout); }
            catch (InvalidOperationException) when (callbackFails) { }
            registration.Dispose();
        }
    }

    [Test]
    public async Task CallbackFailure_CannotSkipPeerCancellationOrOriginalJoin()
    {
        var scope = new OwnedOperationScope();
        var firstStarted = Gate();
        var peerStarted = Gate();
        var peerCancelled = Gate();
        var firstOriginal = Gate();
        var peerOriginal = Gate();
        CancellationTokenRegistration firstRegistration = default;
        CancellationTokenRegistration peerRegistration = default;
        var first = scope.Run(token =>
        {
            firstRegistration = token.Register(() => throw new InvalidOperationException("synthetic private detail"));
            firstStarted.SetResult(); return firstOriginal.Task;
        });
        var peer = scope.Run(token =>
        {
            peerRegistration = token.Register(() => peerCancelled.TrySetResult());
            peerStarted.SetResult(); return peerOriginal.Task;
        });
        Task? stop = null;
        try
        {
            await Task.WhenAll(firstStarted.Task, peerStarted.Task).WaitAsync(Timeout);
            stop = scope.CompleteAsync();
            await peerCancelled.Task.WaitAsync(Timeout);
            firstOriginal.SetResult();
            await first.WaitAsync(Timeout);
            Assert.That(stop.IsCompleted, Is.False);
            peerOriginal.SetResult();
            await peer.WaitAsync(Timeout);
            Assert.ThrowsAsync<InvalidOperationException>(() => stop.WaitAsync(Timeout));
        }
        finally
        {
            firstOriginal.TrySetResult(); peerOriginal.TrySetResult();
            try { await Task.WhenAll(first, peer, stop ?? scope.CompleteAsync()).WaitAsync(Timeout); }
            catch (InvalidOperationException) { }
            firstRegistration.Dispose(); peerRegistration.Dispose();
        }
    }

    [Test]
    public async Task OperationFailure_RemainsTheCallersOriginalFailureWithoutPoisoningCleanup()
    {
        var scope = new OwnedOperationScope();
        var original = new IOException("synthetic body detail");
        var failure = Assert.ThrowsAsync<IOException>(() => scope.Run(_ => throw original));
        Assert.That(failure, Is.SameAs(original));
        await scope.CompleteAsync().WaitAsync(Timeout);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task IdleOwner_DropsCompletedResultAndExceptionPayload(bool fail)
    {
        var scope = new OwnedOperationScope();
        var payload = await RunPayloadAsync(scope, fail);
        for (var attempt = 0; attempt < 20 && payload.IsAlive; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Task.Delay(20);
        }
        Assert.That(payload.IsAlive, Is.False, "Successful observation records must not retain arbitrary result or error data.");
        GC.KeepAlive(scope);
        await scope.CompleteAsync().WaitAsync(Timeout);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RunPayloadAsync(OwnedOperationScope scope, bool fail)
    {
        var payload = new byte[64 * 1024];
        var reference = new WeakReference(payload);
        Task<byte[]>? work = scope.Run(_ =>
        {
            if (!fail) return Task.FromResult(payload);
            var error = new IOException("synthetic payload");
            error.Data["payload"] = payload;
            return Task.FromException<byte[]>(error);
        });
        try { await work; }
        catch (IOException) when (fail) { }
        work = null;
        payload = null!;
        return reference;
    }
}
