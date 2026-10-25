using System.Runtime.ExceptionServices;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public partial class ServiceClientManagerTests
{
    [Test]
    public async Task TransientCompletion_JoinsOriginalCancellationCallbackBeforeAcknowledgement()
    {
        var harness = new TransientRequestHarness();
        var request = harness.Request("held-callback");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new TaskCompletionSource<AcknowledgeEditorRuntimeRequestRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        var acknowledgements = 0;
        Task? dispatch = null;
        Task? cancellation = null;
        Exception? bodyFailure = null;
        Exception? cleanupFailure = null;
        var response = new AcknowledgeEditorRuntimeRequestRequest
        {
            RequestId = request.RequestId,
            DeliveryId = request.DeliveryId,
            RunnerId = request.RunnerId,
            SessionId = request.SessionId,
            LifecycleGeneration = request.LifecycleGeneration,
            RuntimeEpoch = request.RuntimeEpoch,
            RuntimeInstanceId = request.RuntimeInstanceId,
            Kind = request.Kind,
            TransportSucceeded = true,
            StatusCode = 200
        };
        try
        {
            dispatch = ServiceClientManager.DispatchTransientEditorRuntimeRequest(
                request, CommandRunnerId, harness.Deliveries, harness.Cancellation,
                (_, token) =>
                {
                    registration = token.Register(() =>
                    {
                        callbackEntered.SetResult();
                        releaseCallback.Task.GetAwaiter().GetResult();
                    });
                    entered.SetResult();
                    return original.Task;
                },
                (_, _) =>
                {
                    Interlocked.Increment(ref acknowledgements);
                    return Task.CompletedTask;
                }, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation = Task.Run(harness.Cancellation.CancelAll);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            original.SetResult(response);
            await Task.Delay(100);
            TestContext.Out.WriteLine($"Original completed={original.Task.IsCompleted}; callback held={!releaseCallback.Task.IsCompleted}; dispatch complete={dispatch.IsCompleted}; acknowledgements={Volatile.Read(ref acknowledgements)}");
            Assert.Multiple(() =>
            {
                Assert.That(dispatch.IsCompleted, Is.False, "Request completion still owns the original cancellation callback.");
                Assert.That(Volatile.Read(ref acknowledgements), Is.Zero);
            });
            releaseCallback.SetResult();
            await Task.WhenAll(dispatch, cancellation).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(Volatile.Read(ref acknowledgements), Is.EqualTo(1));
        }
        catch (Exception error) { bodyFailure = error; }
        finally
        {
            original.TrySetResult(response);
            releaseCallback.TrySetResult();
            try
            {
                await Task.WhenAll(dispatch ?? Task.CompletedTask, cancellation ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error) { cleanupFailure = error; }
            registration.Dispose();
        }
        if (bodyFailure is not null) ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }
}
