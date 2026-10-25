using System.Runtime.ExceptionServices;

namespace Offloadr.Runner.Tests;

public partial class ServiceClientManagerTests
{
    [Test]
    public async Task HostShutdownOfStartup_JoinsOriginalCancellationCallbackBeforeCompletion()
    {
        var harness = new RuntimeCommandHarness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        Task? launch = null;
        Task? original = null;
        Task? shutdown = null;
        Exception? bodyFailure = null;
        Exception? cleanupFailure = null;
        harness.BeforeStart = (_, token) =>
        {
            registration = token.Register(() =>
            {
                callbackEntered.SetResult();
                releaseCallback.Task.GetAwaiter().GetResult();
            });
            entered.SetResult();
            return release.Task;
        };
        harness.BeforeStop = (_, _) => { cleanupEntered.TrySetResult(); return Task.CompletedTask; };
        try
        {
            launch = ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 3, InstanceA, revision: 1), harness.Deps);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var tracked = harness.WorkState.GetActiveStartup()!;
            original = tracked.Task!;
            shutdown = harness.Shutdown.CancelAsync();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.SetResult();
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            Assert.Multiple(() =>
            {
                Assert.That(original.IsCompleted, Is.False, "The original callback still belongs to physical startup.");
                Assert.That(launch.IsCompleted, Is.False);
                Assert.That(harness.WorkState.GetActiveStartup(), Is.SameAs(tracked));
                Assert.That(harness.Sink.LaunchAcks, Is.Empty, "Host shutdown never acknowledges a ready runtime.");
            });
            releaseCallback.SetResult();
            await Task.WhenAll(launch, original, shutdown).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That(harness.WorkState.GetActiveStartup(), Is.Null);
                Assert.That(harness.Sink.LaunchAcks, Is.Empty);
            });
        }
        catch (Exception error) { bodyFailure = error; }
        finally
        {
            release.TrySetResult(); releaseCallback.TrySetResult();
            try
            {
                await Task.WhenAll(launch ?? Task.CompletedTask, original ?? Task.CompletedTask,
                    shutdown ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error) { cleanupFailure = error; }
            registration.Dispose();
        }
        if (bodyFailure is not null) ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    [Test]
    public async Task StartupCancellationFailure_CannotAcknowledgeQuiesceBeforeOriginalStartupEnds()
    {
        var harness = new RuntimeCommandHarness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        Task? launch = null;
        Task? quiesce = null;
        Exception? bodyFailure = null;
        Exception? cleanupFailure = null;
        harness.BeforeStart = (_, token) =>
        {
            registration = token.Register(() => throw new InvalidOperationException("fixture cancellation failure"));
            entered.SetResult();
            return release.Task;
        };
        try
        {
            launch = ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 3, InstanceA, revision: 1), harness.Deps);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var original = harness.WorkState.GetActiveStartup()!.Task!;
            quiesce = ServiceClientManager.HandleQuiesceRuntimeCommandAsync(Quiesce(7, 3, InstanceA, revision: 1), harness.Deps);
            await Task.Delay(50);
            TestContext.Out.WriteLine($"Original startup pending={!original.IsCompleted}; producer held={!release.Task.IsCompleted}; quiesce complete={quiesce.IsCompleted}; acknowledgements={harness.Sink.QuiesceAcks.Count}");
            Assert.Multiple(() =>
            {
                Assert.That(original.IsCompleted, Is.False);
                Assert.That(release.Task.IsCompleted, Is.False);
                Assert.That(quiesce.IsCompleted, Is.False, "Cancellation failure must not escape the original startup join.");
                Assert.That(harness.Sink.QuiesceAcks, Is.Empty);
            });
            release.SetResult();
            await launch.WaitAsync(TimeSpan.FromSeconds(5));
            await quiesce.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(original.IsCompleted, Is.True);
        }
        catch (Exception error) { bodyFailure = error; }
        finally
        {
            release.TrySetResult();
            try
            {
                if (launch is not null) await launch.WaitAsync(TimeSpan.FromSeconds(5));
                if (quiesce is not null) await quiesce.WaitAsync(TimeSpan.FromSeconds(5));
                await harness.Shutdown.CancelAsync();
            }
            catch (Exception error) { cleanupFailure = error; }
            registration.Dispose();
        }
        // Cleanup cannot replace the ownership assertion's stack or diagnosis.
        if (bodyFailure is not null) ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }
}
