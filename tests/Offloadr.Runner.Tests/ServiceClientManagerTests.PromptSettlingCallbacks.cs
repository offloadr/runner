using Offloadr.Common.V1;
using Offloadr.Runner.V1;
using System.Runtime.ExceptionServices;

namespace Offloadr.Runner.Tests;

public partial class ServiceClientManagerTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task FailedPromptPreparation_JoinsWithdrawalCallbacks(bool callbackFails)
    {
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        AcknowledgePromptResultRequest? ack = null;
        Task? handled = null;
        Exception? bodyFailure = null;
        Exception? cleanupFailure = null;
        var nativeRequests = 0;
        try
        {
            handled = HandlePromptWithDemandAsync(_ => Task.CompletedTask, 200, result => ack = result,
                CancellationToken.None, prepare: token =>
                {
                    registration = token.Register(() =>
                    {
                        callbackEntered.TrySetResult();
                        releaseCallback.Task.GetAwaiter().GetResult();
                        if (callbackFails) throw new InvalidOperationException("synthetic preparation callback failure");
                    });
                    return Task.FromException<Task>(new IOException("synthetic preparation failure"));
                }, duringNativeRequest: () => Interlocked.Increment(ref nativeRequests));
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            Assert.Multiple(() =>
            {
                Assert.That(handled.IsCompleted, Is.False, "Preparation returned no demand, but its callback is still owned.");
                Assert.That(ack?.Result.Outcome, Is.EqualTo(PromptInvocationOutcome.NotExecuted));
                Assert.That(nativeRequests, Is.Zero);
            });
            releaseCallback.SetResult();
            await handled.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception error) { bodyFailure = error; }
        finally
        {
            releaseCallback.TrySetResult();
            try { await (handled ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { cleanupFailure = error; }
            registration.Dispose();
        }
        if (bodyFailure is not null) ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    [Test]
    public async Task CancelledPromptPreparation_JoinsOriginalFactoryAndCallbacks()
    {
        using var physical = new CancellationTokenSource();
        var factoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalFactory = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        AcknowledgePromptResultRequest? ack = null;
        Task? handled = null;
        Task? cancellation = null;
        Exception? bodyFailure = null;
        Exception? cleanupFailure = null;
        var nativeRequests = 0;
        try
        {
            handled = HandlePromptWithDemandAsync(_ => Task.CompletedTask, 200, result => ack = result,
                physical.Token, prepare: token =>
                {
                    registration = token.Register(() =>
                    {
                        callbackEntered.TrySetResult();
                        releaseCallback.Task.GetAwaiter().GetResult();
                    });
                    factoryEntered.TrySetResult();
                    return originalFactory.Task;
                }, duringNativeRequest: () => Interlocked.Increment(ref nativeRequests));
            await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation = physical.CancelAsync();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That(handled.IsCompleted, Is.False, "The original preparation factory still owns its completion.");
                Assert.That(ack, Is.Null);
            });
            originalFactory.SetResult(Task.CompletedTask);
            await WaitForConditionAsync(() => ack is not null, TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            Assert.Multiple(() =>
            {
                Assert.That(handled.IsCompleted, Is.False, "Factory completion cannot bypass its held withdrawal callback.");
                Assert.That(ack!.Result.Outcome, Is.EqualTo(PromptInvocationOutcome.NotExecuted));
                Assert.That(nativeRequests, Is.Zero);
            });
            releaseCallback.SetResult();
            await handled.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception error) { bodyFailure = error; }
        finally
        {
            originalFactory.TrySetResult(Task.CompletedTask);
            releaseCallback.TrySetResult();
            try { await Task.WhenAll(handled ?? Task.CompletedTask, cancellation ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { cleanupFailure = error; }
            registration.Dispose();
        }
        if (bodyFailure is not null) ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    [Test]
    public async Task WithdrawnPromptDemand_JoinsOriginalQueueProbeAfterDemandEnds()
    {
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var originalDemand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalProbe = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = WithPromptIdentity(new() { SessionId = PromptSessionId, SubmissionId = PromptSubmissionId });
        CancellationToken probeToken = default;
        Task? stop = null;
        Exception? bodyFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            Assert.That(work.Start(command, false, (_, physical, demand) => HandlePromptWithDemandAsync(
                _ => originalDemand.Task, 200, _ => { }, physical, demand,
                isPromptQueued: (_, token) =>
                {
                    probeToken = token;
                    probeEntered.TrySetResult();
                    return originalProbe.Task;
                }, nativeBody: "{\"prompt_id\":\"native-prompt-1\"}",
                holdPromptDemand: settling => work.HoldDemand(command.CommandId, settling))), Is.True);
            await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            originalDemand.SetResult();
            stop = work.CancelAndWaitAsync(_ => true);
            await WaitForConditionAsync(() => probeToken.IsCancellationRequested, TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            Assert.That(stop.IsCompleted, Is.False, "Cancellation is not completion of an original queue probe that ignores it.");
            originalProbe.SetResult(false);
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception error) { bodyFailure = error; }
        finally
        {
            originalDemand.TrySetResult();
            originalProbe.TrySetResult(false);
            try { await (stop ?? work.CancelAndWaitAsync(_ => true)).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { cleanupFailure = error; }
        }
        if (bodyFailure is not null) ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }
}
