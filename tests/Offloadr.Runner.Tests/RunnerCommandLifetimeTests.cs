using System.Runtime.CompilerServices;

namespace Offloadr.Runner.Tests;

public sealed class RunnerCommandLifetimeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Stop_JoinsEveryOriginalAndRejectsLateAdmission()
    {
        var lifetime = Create();
        var first = Gate();
        var second = Gate();
        try
        {
            var firstRoot = lifetime.Start(() => first.Task);
            var secondRoot = lifetime.Start(() => second.Task);
            var stop = lifetime.StopAsync();
            var late = 0;
            await lifetime.Start(() => { late++; return Task.CompletedTask; });
            Assert.That(late, Is.Zero);
            Assert.That(stop.IsCompleted, Is.False);
            Assert.That(lifetime.DisposeAsync().AsTask(), Is.SameAs(stop));
            first.SetResult();
            await firstRoot.WaitAsync(Timeout);
            Assert.That(stop.IsCompleted, Is.False);
            second.SetResult();
            await Task.WhenAll(firstRoot, secondRoot, stop).WaitAsync(Timeout);
        }
        finally { first.TrySetResult(); second.TrySetResult(); await JoinAsync(lifetime.StopAsync()); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FactoryFailure_CancelsParentButStillJoinsEarlierOriginal(bool returnsNull)
    {
        var parentRequested = Gate();
        var lifetime = new RunnerCommandLifetime(CancellationToken.None, () => { parentRequested.TrySetResult(); return Task.CompletedTask; });
        var original = Gate();
        try
        {
            _ = lifetime.Start(() => original.Task);
            await lifetime.Start(() => returnsNull ? null! : throw new IOException("synthetic private error"));
            await parentRequested.Task.WaitAsync(Timeout);
            Assert.That(lifetime.Token.IsCancellationRequested, Is.True);
            var stop = lifetime.StopAsync();
            Assert.That(stop.IsCompleted, Is.False);
            original.SetResult();
            var failure = Assert.ThrowsAsync<InvalidOperationException>(async () => await stop.WaitAsync(Timeout));
            Assert.That(failure!.InnerException, Is.Null);
            Assert.That(failure.Message, Does.Not.Contain("synthetic private error"));
        }
        finally { original.TrySetResult(); await JoinAsync(lifetime.StopAsync()); }
    }

    [Test]
    public async Task StopDuringSynchronousFactory_JoinsItsLaterPublishedOriginal()
    {
        var lifetime = Create();
        var entered = Gate();
        using var releaseFactory = new ManualResetEventSlim();
        var original = Gate();
        var admission = Task.Factory.StartNew(() => lifetime.Start(() =>
        {
            entered.SetResult();
            releaseFactory.Wait();
            return original.Task;
        }), CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(Timeout);
            var stop = lifetime.StopAsync();
            Assert.That(stop.IsCompleted, Is.False);
            releaseFactory.Set();
            var root = await admission.WaitAsync(Timeout);
            Assert.Multiple(() =>
            {
                Assert.That(root.IsCompleted, Is.False);
                Assert.That(stop.IsCompleted, Is.False);
            });
            original.SetResult();
            await Task.WhenAll(root, stop).WaitAsync(Timeout);
        }
        finally
        {
            releaseFactory.Set(); original.TrySetResult();
            await admission.Unwrap().WaitAsync(Timeout);
            await JoinAsync(lifetime.StopAsync());
        }
    }

    [Test]
    public async Task HostShutdown_JoinsOriginalCallbackAfterCommandWorkEnds()
    {
        using var host = new CancellationTokenSource();
        var lifetime = Create(host.Token);
        var entered = Gate();
        var releaseCallback = Gate();
        var original = Gate();
        using var registration = lifetime.Token.Register(() =>
        {
            entered.TrySetResult();
            releaseCallback.Task.GetAwaiter().GetResult();
        });
        try
        {
            var root = lifetime.Start(() => original.Task);
            var hostCancellation = host.CancelAsync();
            await entered.Task.WaitAsync(Timeout);
            var stop = lifetime.StopAsync();
            original.SetResult();
            await root.WaitAsync(Timeout);
            Assert.That(stop.IsCompleted, Is.False);
            Assert.That(lifetime.StopAsync(), Is.SameAs(stop));
            releaseCallback.SetResult();
            await Task.WhenAll(stop, hostCancellation).WaitAsync(Timeout);
        }
        finally { releaseCallback.TrySetResult(); original.TrySetResult(); await JoinAsync(lifetime.StopAsync()); }
    }

    [Test]
    public async Task CallbackFailure_CannotSkipPeerCancellationOrOriginalJoins()
    {
        var lifetime = Create();
        var peerCancelled = Gate();
        var first = Gate();
        var second = Gate();
        using var firstRegistration = lifetime.Token.Register(() => throw new IOException("synthetic callback content"));
        using var secondRegistration = lifetime.Token.Register(() => peerCancelled.TrySetResult());
        try
        {
            _ = lifetime.Start(() => first.Task);
            _ = lifetime.Start(() => second.Task);
            var stop = lifetime.StopAsync();
            await peerCancelled.Task.WaitAsync(Timeout);
            Assert.That(stop.IsCompleted, Is.False);
            first.SetResult();
            Assert.That(stop.IsCompleted, Is.False);
            second.SetResult();
            var failure = Assert.ThrowsAsync<InvalidOperationException>(async () => await stop.WaitAsync(Timeout));
            Assert.Multiple(() =>
            {
                Assert.That(failure!.InnerException, Is.Null);
                Assert.That(failure.Message, Does.Not.Contain("synthetic callback content"));
            });
        }
        finally { first.TrySetResult(); second.TrySetResult(); await JoinAsync(lifetime.StopAsync()); }
    }

    [Test]
    public async Task UnexpectedCancelledOriginal_IsACommandFailure()
    {
        var lifetime = Create();
        await lifetime.Start(() => Task.FromCanceled(new CancellationToken(canceled: true)));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await lifetime.StopAsync().WaitAsync(Timeout));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ClosedAdmission_DoesNotInvokeFactories(bool hostShutdown)
    {
        using var host = new CancellationTokenSource();
        var lifetime = Create(host.Token);
        try
        {
            if (hostShutdown) await host.CancelAsync();
            else await lifetime.StopAsync();
            var calls = 0;
            await lifetime.Start(() => { calls++; return Task.CompletedTask; });
            Assert.That(calls, Is.Zero);
        }
        finally { await lifetime.StopAsync().WaitAsync(Timeout); }
    }

    [Test]
    public async Task CommandFailure_CancelsControlPeersBeforeItsHeldOriginalEnds()
    {
        var parent = new RunnerControlLifetime(CancellationToken.None);
        var lifetime = new RunnerCommandLifetime(parent.Token, parent.RequestShutdownAsync);
        var original = Gate();
        var peerCancelled = Gate();
        var run = parent.RunAsync(() => { }, async token =>
        {
            using var registration = token.Register(() => peerCancelled.TrySetResult());
            await Task.Delay(System.Threading.Timeout.Infinite, token);
        }, _ => Task.CompletedTask, async commandCancellation =>
        {
            _ = lifetime.Start(() => original.Task);
            await lifetime.Start(() => Task.FromException(new IOException("synthetic child failure")));
            await lifetime.StopAsync();
        }, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, []);
        try
        {
            await peerCancelled.Task.WaitAsync(Timeout);
            Assert.Multiple(() =>
            {
                Assert.That(original.Task.IsCompleted, Is.False);
                Assert.That(run.IsCompleted, Is.False);
            });
            original.SetResult();
            Assert.ThrowsAsync<InvalidOperationException>(async () => await run.WaitAsync(Timeout));
        }
        finally
        {
            original.TrySetResult();
            await JoinAsync(run);
            await JoinAsync(lifetime.StopAsync());
            await JoinAsync(parent.DisposeAsync().AsTask());
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CompletedContentCanCollectWhileRegistryIsIdle(bool faulted)
    {
        var lifetime = Create();
        var (reference, original) = StartWithPayload(lifetime, faulted);
        try
        {
            await original.WaitAsync(Timeout);
            for (var attempt = 0; attempt < 30 && reference.IsAlive; attempt++)
            {
                await Task.Delay(10);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            Assert.That(reference.IsAlive, Is.False);
        }
        finally { await JoinAsync(lifetime.StopAsync()); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference, Task) StartWithPayload(RunnerCommandLifetime lifetime, bool faulted)
    {
        var payload = new byte[65536];
        var reference = new WeakReference(payload);
        var error = new IOException("synthetic private error");
        error.Data["synthetic-body"] = payload;
        var original = lifetime.Start(() =>
        {
            GC.KeepAlive(payload);
            return faulted ? Task.FromException(error) : Task.CompletedTask;
        });
        return (reference, original);
    }

    private static RunnerCommandLifetime Create(CancellationToken host = default)
        => new(host, () => Task.CompletedTask);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task JoinAsync(Task task)
    {
        try { await task.WaitAsync(Timeout); }
        catch (InvalidOperationException) { /* Expected failure facts are asserted by their owning case. */ }
    }
}
