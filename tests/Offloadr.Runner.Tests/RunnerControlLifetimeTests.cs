using System.Diagnostics;
using System.Runtime.CompilerServices;
using Offloadr.Runner.Agent;

namespace Offloadr.Runner.Tests;

[TestFixture]
public sealed class RunnerControlLifetimeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Disabled(CancellationToken _) => Task.CompletedTask;
    private static Task UntilShutdown(CancellationToken token) => Task.Delay(System.Threading.Timeout.Infinite, token);

    [Test]
    public async Task LaneFailure_CancelsPeersButJoinsTheirOriginalsBeforeStartingAllDrains()
    {
        var lifetime = new RunnerControlLifetime(CancellationToken.None);
        var status = Gate();
        var models = Gate();
        var commands = Gate();
        var started = Gate();
        var cancelled = Gate();
        var drainsStarted = Gate();
        var drains = new[] { Gate(), Gate(), Gate() };
        var drainCount = 0;
        var detached = false;
        var expected = new IOException("fixture lane failure");
        using var registration = lifetime.Token.Register(() => cancelled.TrySetResult());
        try
        {
            var run = Run(lifetime, status: _ => status.Task, models: _ => models.Task,
                commands: _ => { started.SetResult(); return commands.Task; },
                drainGpu: () => Drain(0), drainPrompts: () => Drain(1), drainStartup: () => Drain(2),
                detach: [() => detached = true]);
            await started.Task.WaitAsync(Timeout);
            models.SetException(expected);
            await cancelled.Task.WaitAsync(Timeout);
            Assert.That(drainCount, Is.Zero);
            Assert.That(run.IsCompleted, Is.False);
            status.SetResult();
            Assert.That(drainCount, Is.Zero);
            commands.SetResult();
            await drainsStarted.Task.WaitAsync(Timeout);
            var disposal = lifetime.DisposeAsync().AsTask();
            Assert.That(lifetime.DisposeAsync().AsTask(), Is.SameAs(disposal));
            foreach (var drain in drains)
            {
                Assert.That(run.IsCompleted, Is.False);
                Assert.That(disposal.IsCompleted, Is.False);
                Assert.That(detached, Is.False);
                drain.SetResult();
            }
            Assert.That(await FailureAsync(run), Is.SameAs(expected));
            Assert.That(await FailureAsync(disposal), Is.SameAs(expected));
            Assert.That(detached, Is.True);
        }
        finally
        {
            status.TrySetResult(); models.TrySetResult(); commands.TrySetResult();
            foreach (var drain in drains) drain.TrySetResult();
            await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask());
        }

        Task Drain(int index)
        {
            if (Interlocked.Increment(ref drainCount) == 3) drainsStarted.SetResult();
            return drains[index].Task;
        }
    }

    [Test]
    public async Task FactoryFailure_JoinsEarlierOriginalAndStillInvokesEveryCleanup()
    {
        var lifetime = new RunnerControlLifetime(CancellationToken.None);
        var original = Gate();
        var cancelled = Gate();
        var failure = new IOException("fixture factory failure");
        var laterFactories = 0;
        var drains = 0;
        var detachments = 0;
        using var registration = lifetime.Token.Register(() => cancelled.SetResult());
        try
        {
            var run = Run(lifetime, status: _ => original.Task, models: _ => throw failure,
                commands: _ => { laterFactories++; return Task.CompletedTask; },
                drainGpu: Drain, drainPrompts: Drain, drainStartup: Drain,
                detach: [() => detachments++, () => detachments++]);
            await cancelled.Task.WaitAsync(Timeout);
            Assert.That(drains, Is.Zero);
            Assert.That(detachments, Is.Zero);
            Assert.That(run.IsCompleted, Is.False);
            original.SetResult();
            Assert.That(await FailureAsync(run), Is.SameAs(failure));
            Assert.That(drains, Is.EqualTo(3));
            Assert.That(detachments, Is.EqualTo(2));
            Assert.That(laterFactories, Is.Zero);
        }
        finally
        {
            original.TrySetResult();
            await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask());
        }

        Task Drain() { drains++; return Task.CompletedTask; }
    }

    [Test]
    public async Task HostShutdown_JoinsAdmittedOriginalProgressAndRejectsLateCallbacks()
    {
        using var host = new CancellationTokenSource();
        var lifetime = new RunnerControlLifetime(host.Token);
        var started = Gate();
        var reportEntered = Gate();
        var reportCancelled = Gate();
        var release = Gate();
        var detached = false;
        var reportCalls = 0;
        try
        {
            var run = Run(lifetime, attach: () => started.SetResult(), detach: [() => detached = true]);
            await started.Task.WaitAsync(Timeout);
            var original = lifetime.ReportProgressAsync(async token =>
            {
                using var registration = token.Register(() => reportCancelled.SetResult());
                reportEntered.SetResult();
                await release.Task;
                reportCalls++;
            });
            await reportEntered.Task.WaitAsync(Timeout);
            await host.CancelAsync();
            await reportCancelled.Task.WaitAsync(Timeout);
            var disposal = lifetime.DisposeAsync().AsTask();
            await lifetime.ReportProgressAsync(_ => { reportCalls++; return Task.CompletedTask; });
            Assert.Multiple(() =>
            {
                Assert.That(original.IsCompleted, Is.False);
                Assert.That(run.IsCompleted, Is.False);
                Assert.That(disposal.IsCompleted, Is.False);
                Assert.That(detached, Is.False);
                Assert.That(reportCalls, Is.Zero);
            });
            release.SetResult();
            await run.WaitAsync(Timeout);
            await disposal.WaitAsync(Timeout);
            Assert.That(original.IsCompletedSuccessfully, Is.True);
            Assert.That(detached, Is.True);
            await lifetime.ReportProgressAsync(_ => { reportCalls++; return Task.CompletedTask; });
            Assert.That(reportCalls, Is.EqualTo(1));
        }
        finally
        {
            release.TrySetResult();
            await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask());
        }
    }

    [Test]
    public async Task AttachFailure_StillOwnsAProgressCallAlreadyAdmittedByPartialSetup()
    {
        var lifetime = new RunnerControlLifetime(CancellationToken.None);
        var release = Gate();
        var cancelled = Gate();
        var expected = new IOException("fixture partial attachment failure");
        Task? original = null;
        var detached = false;
        using var registration = lifetime.Token.Register(() => cancelled.SetResult());
        try
        {
            var run = Run(lifetime, attach: () =>
            {
                original = lifetime.ReportProgressAsync(_ => release.Task);
                throw expected;
            }, detach: [() => detached = true]);
            await cancelled.Task.WaitAsync(Timeout);
            Assert.That(original, Is.Not.Null);
            Assert.That(original!.IsCompleted, Is.False);
            Assert.That(run.IsCompleted, Is.False);
            Assert.That(detached, Is.False);
            release.SetResult();
            Assert.That(await FailureAsync(run), Is.SameAs(expected));
            Assert.That(original.IsCompletedSuccessfully, Is.True);
            Assert.That(detached, Is.True);
        }
        finally
        {
            release.TrySetResult();
            await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask());
        }
    }

    [Test]
    public async Task ProgressFailure_ClosesAdmissionAndRemainsAFailureAfterItsTaskCompleted()
    {
        var lifetime = new RunnerControlLifetime(CancellationToken.None);
        var failure = new IOException("fixture required progress cleanup failure");
        var calls = 0;
        try
        {
            var original = lifetime.ReportProgressAsync(_ => throw failure);
            Assert.That(await FailureAsync(original), Is.SameAs(failure));
            await lifetime.ReportProgressAsync(_ => { calls++; return Task.CompletedTask; });
            var run = Run(lifetime);
            Assert.That(await FailureAsync(run), Is.SameAs(failure));
            Assert.That(calls, Is.Zero);
            Assert.That(lifetime.Token.IsCancellationRequested, Is.True);
        }
        finally { await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask()); }
    }

    [Test]
    public async Task CleanupFailures_DoNotSkipOtherOriginalDrainsOrDetachments()
    {
        var lifetime = new RunnerControlLifetime(CancellationToken.None);
        var started = Gate();
        var release = Gate();
        var pendingDrainEntered = Gate();
        var gpuFailure = new IOException("fixture GPU drain setup failure");
        var promptFailure = new IOException("fixture prompt drain failure");
        var detachFailure = new IOException("fixture detach failure");
        var drains = 0;
        var detachments = 0;
        try
        {
            var run = Run(lifetime, attach: () => started.SetResult(),
                drainGpu: () => throw gpuFailure,
                drainPrompts: () => Task.FromException(promptFailure),
                drainStartup: () => { drains++; pendingDrainEntered.SetResult(); return release.Task; },
                detach: [() => throw detachFailure, () => detachments++]);
            await started.Task.WaitAsync(Timeout);
            await lifetime.RequestShutdownAsync();
            await pendingDrainEntered.Task.WaitAsync(Timeout);
            Assert.That(run.IsCompleted, Is.False);
            Assert.That(detachments, Is.Zero);
            release.SetResult();
            var failure = (AggregateException)await FailureAsync(run);
            Assert.That(failure.InnerExceptions, Is.EquivalentTo(new Exception[] { gpuFailure, promptFailure, detachFailure }));
            Assert.That(drains, Is.EqualTo(1));
            Assert.That(detachments, Is.EqualTo(1));
        }
        finally
        {
            release.TrySetResult();
            await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask());
        }
    }

    [Test]
    public async Task RepeatedCancellation_JoinsTheFirstOriginalCallbackEvenAfterProducersEnd()
    {
        var lifetime = new RunnerControlLifetime(CancellationToken.None);
        var started = Gate();
        var callbackEntered = Gate();
        var release = Gate();
        var detached = false;
        using var registration = lifetime.Token.Register(() =>
        {
            callbackEntered.SetResult();
            release.Task.GetAwaiter().GetResult();
        });
        try
        {
            var run = Run(lifetime, attach: () => started.SetResult(), detach: [() => detached = true]);
            await started.Task.WaitAsync(Timeout);
            var first = lifetime.RequestShutdownAsync();
            await callbackEntered.Task.WaitAsync(Timeout);
            Assert.That(lifetime.RequestShutdownAsync(), Is.SameAs(first));
            var disposal = lifetime.DisposeAsync().AsTask();
            Assert.That(first.IsCompleted, Is.False);
            Assert.That(run.IsCompleted, Is.False);
            Assert.That(disposal.IsCompleted, Is.False);
            Assert.That(detached, Is.False);
            release.SetResult();
            await run.WaitAsync(Timeout);
            await disposal.WaitAsync(Timeout);
            Assert.That(detached, Is.True);
        }
        finally
        {
            release.TrySetResult();
            await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask());
        }
    }

    [Test]
    public async Task DisposeWithoutRun_JoinsAnOriginalProgressCallThatIgnoresCancellation()
    {
        var lifetime = new RunnerControlLifetime(CancellationToken.None);
        var release = Gate();
        var entered = Gate();
        try
        {
            var original = lifetime.ReportProgressAsync(_ => { entered.SetResult(); return release.Task; });
            await entered.Task.WaitAsync(Timeout);
            var first = lifetime.DisposeAsync().AsTask();
            Assert.That(lifetime.DisposeAsync().AsTask(), Is.SameAs(first));
            Assert.That(first.IsCompleted, Is.False);
            Assert.That(original.IsCompleted, Is.False);
            release.SetResult();
            await first.WaitAsync(Timeout);
            Assert.That(original.IsCompletedSuccessfully, Is.True);
            Assert.Throws<ObjectDisposedException>(() => Run(lifetime));
        }
        finally
        {
            release.TrySetResult();
            await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask());
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task RequiredLaneEndingBeforeShutdown_IsFailure(bool statusEnds)
    {
        var lifetime = new RunnerControlLifetime(CancellationToken.None);
        try
        {
            var run = Run(lifetime, status: statusEnds ? Disabled : UntilShutdown,
                commands: statusEnds ? UntilShutdown : Disabled);
            Assert.That(await FailureAsync(run), Is.TypeOf<InvalidOperationException>());
            Assert.That(lifetime.Token.IsCancellationRequested, Is.True);
        }
        finally { await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask()); }
    }

    [Test]
    public async Task DisabledModelLane_DoesNotStopStatusOrCommands()
    {
        var lifetime = new RunnerControlLifetime(CancellationToken.None);
        var started = Gate();
        try
        {
            var run = Run(lifetime, commands: token => { started.SetResult(); return UntilShutdown(token); });
            await started.Task.WaitAsync(Timeout);
            Assert.That(lifetime.Token.IsCancellationRequested, Is.False);
            Assert.That(run.IsCompleted, Is.False);
            await lifetime.DisposeAsync().AsTask().WaitAsync(Timeout);
            await run.WaitAsync(Timeout);
        }
        finally { await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask()); }
    }

    [Test]
    public async Task SecondRun_IsRejectedWhileTheOriginalConnectionIsOwned()
    {
        var lifetime = new RunnerControlLifetime(CancellationToken.None);
        try
        {
            var original = Run(lifetime);
            Assert.Throws<InvalidOperationException>(() => Run(lifetime));
            await lifetime.DisposeAsync().AsTask().WaitAsync(Timeout);
            await original.WaitAsync(Timeout);
        }
        finally { await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask()); }
    }

    [Test]
    public async Task CompletedProgress_DoesNotKeepItsCapturedPayloadReachableWhileTheListenerIsIdle()
    {
        var lifetime = new RunnerControlLifetime(CancellationToken.None);
        try
        {
            var payload = await CompleteProgressWithPayloadAsync(lifetime);
            // Let the completing async state machines unwind before collecting.
            for (var attempt = 0; attempt < 20 && payload.IsAlive; attempt++)
            {
                await Task.Delay(10);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Assert.That(payload.IsAlive, Is.False);
            Assert.That(lifetime.Token.IsCancellationRequested, Is.False);
        }
        finally { await JoinAfterAssertionAsync(lifetime.DisposeAsync().AsTask()); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> CompleteProgressWithPayloadAsync(RunnerControlLifetime lifetime)
    {
        var content = new byte[4096];
        var reference = new WeakReference(content);
        await lifetime.ReportProgressAsync(async _ =>
        {
            await Task.Yield();
            GC.KeepAlive(content);
        });
        return reference;
    }

    [Test]
    public async Task ActualAgent_InvalidControlAddress_ExitsNonzeroWithOnlyAnErrorClass()
    {
        var assembly = typeof(RunnerAgentLifetime).Assembly.Location;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(assembly)!
        };
        start.ArgumentList.Add(assembly);
        foreach (var key in start.Environment.Keys.Where(key =>
                     key.StartsWith("RUNNER_", StringComparison.Ordinal) || key.StartsWith("COMFY_", StringComparison.Ordinal) ||
                     key.StartsWith("ARIA2_", StringComparison.Ordinal) || key.StartsWith("REG_", StringComparison.Ordinal) ||
                     key.StartsWith("EDITOR_", StringComparison.Ordinal) || key.StartsWith("SESSION_", StringComparison.Ordinal) ||
                     key.StartsWith("OFFLOADR_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
        start.Environment["OFFLOADR_API_GRPC"] = "https://[fixture-content-must-not-be-printed";
        start.Environment["RUNNER_SECRET"] = "fixture-runner-key-must-not-be-printed";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(Timeout);
            Assert.That(process.ExitCode, Is.EqualTo(1));
            Assert.That(await stdout, Is.Empty);
            Assert.That((await stderr).Trim(), Is.EqualTo("Runner agent failed; errorClass=UriFormatException."));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }

    private static Task Run(RunnerControlLifetime lifetime, Action? attach = null,
        Func<CancellationToken, Task>? status = null, Func<CancellationToken, Task>? models = null,
        Func<CancellationToken, Task>? commands = null, Func<Task>? drainGpu = null,
        Func<Task>? drainPrompts = null, Func<Task>? drainStartup = null, IReadOnlyList<Action>? detach = null)
        => lifetime.RunAsync(attach ?? (() => { }), status ?? UntilShutdown, models ?? Disabled, commands ?? UntilShutdown,
            drainGpu ?? (() => Task.CompletedTask), drainPrompts ?? (() => Task.CompletedTask),
            drainStartup ?? (() => Task.CompletedTask), detach ?? []);

    private static async Task<Exception> FailureAsync(Task original)
    {
        try { await original.WaitAsync(Timeout); }
        catch (Exception error) when (original.IsCompleted) { return error; }
        throw new AssertionException("The original task unexpectedly succeeded.");
    }

    private static async Task JoinAfterAssertionAsync(Task original)
    {
        try { await original.WaitAsync(Timeout); }
        catch (Exception) when (original.IsCompleted) { }
    }
}
