using System.Runtime.ExceptionServices;
using Moq;

namespace Offloadr.Runner.Tests;

public sealed class GpuPowerCommandLifetimeTests
{
    [Test]
    public async Task Drain_JoinsAdmittedActivationUntilItsOriginalActionEnds()
    {
        var probe = new Mock<IRuntimeTelemetryProbe>(MockBehavior.Strict);
        var executor = new GpuPowerCommandExecutor(new GpuPowerLimitService(new RuntimeTelemetryService(probe.Object)));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? activation = null;
        Task? first = null;
        Task? second = null;
        Exception? bodyFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            activation = Task.Run(() => executor.ActivateSessionAsync(() =>
            {
                entered.SetResult();
                release.Task.GetAwaiter().GetResult();
            }, CancellationToken.None));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            first = executor.CancelAndDrainAsync();
            second = executor.CancelAndDrainAsync();
            TestContext.Out.WriteLine($"Activation held={!activation.IsCompleted}; action held={!release.Task.IsCompleted}; first drain complete={first.IsCompleted}; second drain complete={second.IsCompleted}");
            Assert.Multiple(() =>
            {
                Assert.That(first.IsCompleted, Is.False, "Drain still owns the admitted activation action.");
                Assert.That(second.IsCompleted, Is.False, "Repeated callers still own that same activation.");
            });
            release.SetResult();
            await Task.WhenAll(activation, first, second).WaitAsync(TimeSpan.FromSeconds(5));
            probe.VerifyNoOtherCalls();
        }
        catch (Exception error) { bodyFailure = error; }
        finally
        {
            release.TrySetResult();
            try
            {
                await Task.WhenAll(activation ?? Task.CompletedTask, first ?? Task.CompletedTask,
                    second ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error) { cleanupFailure = error; }
        }
        if (bodyFailure is not null) ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }
}
