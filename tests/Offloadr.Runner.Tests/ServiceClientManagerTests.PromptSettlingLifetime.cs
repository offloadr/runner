using System.Runtime.ExceptionServices;

namespace Offloadr.Runner.Tests;

public partial class ServiceClientManagerTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task PromptSettlingFailure_StillJoinsOriginalModelDemand(bool acceptedThenDequeued)
    {
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var original = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = WithPromptIdentity(new()
        {
            SessionId = PromptSessionId,
            SubmissionId = PromptSubmissionId,
            EditorSid = "editor-1",
            PromptJson = "{}"
        });
        CancellationTokenRegistration registration = default;
        Task? stop = null;
        Exception? bodyFailure = null;
        Exception? cleanupFailure = null;
        var callbacks = 0;
        try
        {
            Assert.That(work.Start(command, false, (_, physical, demand) => HandlePromptWithDemandAsync(
                token =>
                {
                    registration = token.Register(() =>
                    {
                        Interlocked.Increment(ref callbacks);
                        callbackRan.TrySetResult();
                        throw new InvalidOperationException("synthetic demand cancellation failure");
                    });
                    return original.Task;
                },
                acceptedThenDequeued ? 200 : 500,
                _ => { }, physical, demand,
                isPromptQueued: (_, _) => Task.FromResult<bool?>(false),
                nativeBody: "{\"prompt_id\":\"native-prompt-1\"}",
                holdPromptDemand: settling => work.HoldDemand(command.CommandId, settling))), Is.True);
            await callbackRan.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop = work.CancelAndWaitAsync(_ => true);
            await Task.Delay(50);
            TestContext.Out.WriteLine($"Original demand held={!original.Task.IsCompleted}; callback failed=True; prompt drain complete={stop.IsCompleted}");
            Assert.That(stop.IsCompleted, Is.False, "A failed withdrawal callback cannot stand in for the original model-demand completion.");
            original.SetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(callbacks, Is.EqualTo(1));
        }
        catch (Exception error) { bodyFailure = error; }
        finally
        {
            original.TrySetResult();
            try { await (stop ?? work.CancelAndWaitAsync(_ => true)).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { cleanupFailure = error; }
            registration.Dispose();
        }
        if (bodyFailure is not null) ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }
}
