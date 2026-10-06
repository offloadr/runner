using Offloadr.Runner.V1;
using Grpc.Core;
using System.Net.WebSockets;

namespace Offloadr.Runner.Tests;

public class ComfySessionEventRelayTests
{
    [Test]
    public async Task Enqueue_DropsOldestBufferedEvents_WhenQueueOverflows()
    {
        await using var relay = new ComfySessionEventRelay("runner-1", "127.0.0.1", 8188, queueCapacity: 2);
        var sink = new BlockingThenRecordingSink();
        relay.AttachSink(sink);

        var firstAccepted = relay.Enqueue(new SessionStreamEvent
        {
            RunnerId = "runner-1",
            SessionId = "session-1",
            ClientId = "client-1"
        });

        await WaitForAsync(sink.FirstInvocationStarted.Task, TimeSpan.FromSeconds(3));

        var secondAccepted = relay.Enqueue(new SessionStreamEvent
        {
            RunnerId = "runner-1",
            SessionId = "session-2",
            ClientId = "client-1"
        });

        var thirdAccepted = relay.Enqueue(new SessionStreamEvent
        {
            RunnerId = "runner-1",
            SessionId = "session-3",
            ClientId = "client-1"
        });

        var fourthAccepted = relay.Enqueue(new SessionStreamEvent
        {
            RunnerId = "runner-1",
            SessionId = "session-4",
            ClientId = "client-1"
        });

        sink.ReleaseFirstInvocation.TrySetResult(true);
        var deliveredSessions = await WaitForAsync(sink.DeliveredSessions.Task, TimeSpan.FromSeconds(3));

        Assert.Multiple(() =>
        {
            Assert.That(firstAccepted, Is.True);
            Assert.That(secondAccepted, Is.True);
            Assert.That(thirdAccepted, Is.True);
            Assert.That(fourthAccepted, Is.True);
            Assert.That(deliveredSessions, Is.EqualTo(new[] { "session-1", "session-3", "session-4" }));
        });
    }

    [Test]
    public async Task PermanentlyRejectedEvent_DoesNotBlockLaterValidEvents()
    {
        await using var relay = new ComfySessionEventRelay("runner-1", "127.0.0.1", 8188, queueCapacity: 8);
        var sink = new RejectStaleSessionSink();
        relay.AttachSink(sink);

        relay.Enqueue(new SessionStreamEvent
        {
            RunnerId = "runner-1",
            SessionId = "stale-session",
            ClientId = "client-1",
            EventType = "status"
        });
        relay.Enqueue(new SessionStreamEvent
        {
            RunnerId = "runner-1",
            SessionId = "live-session",
            ClientId = "client-1",
            EventType = "status"
        });

        var deliveredSessionId = await WaitForAsync(sink.FirstDeliveredSession.Task, TimeSpan.FromSeconds(3));

        Assert.Multiple(() =>
        {
            Assert.That(deliveredSessionId, Is.EqualTo("live-session"));
            Assert.That(sink.PermanentFailureCount, Is.GreaterThanOrEqualTo(1));
        });
    }

    [TestCase("executed", true)]
    [TestCase("execution_cached", true)]
    [TestCase("execution_success", true)]
    [TestCase("executing", false)]
    [TestCase("progress", false)]
    [TestCase("status", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void ShouldRefreshArtifactsForEventType_MatchesExpectedTriggerSet(string? eventType, bool expected)
    {
        var result = ComfySessionEventRelay.ShouldRefreshArtifactsForEventType(eventType);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void ShouldSuppressSessionBridgeError_ReturnsTrue_ForPrematureWebSocketCloseDuringShutdown()
    {
        var ex = new WebSocketException(WebSocketError.ConnectionClosedPrematurely);

        var result = ComfySessionEventRelay.ShouldSuppressSessionBridgeError(ex, shutdownRequested: true);

        Assert.That(result, Is.True);
    }

    [Test]
    public void ShouldSuppressSessionBridgeError_ReturnsFalse_WithoutShutdownRequest()
    {
        var ex = new WebSocketException(WebSocketError.ConnectionClosedPrematurely);

        var result = ComfySessionEventRelay.ShouldSuppressSessionBridgeError(ex, shutdownRequested: false);

        Assert.That(result, Is.False);
    }

    [Test]
    public void ShouldSuppressSessionBridgeError_ReturnsFalse_ForOtherShutdownExceptions()
    {
        var ex = new WebSocketException(WebSocketError.Faulted);

        var result = ComfySessionEventRelay.ShouldSuppressSessionBridgeError(ex, shutdownRequested: true);

        Assert.That(result, Is.False);
    }

    [TestCase("bridge-1", "bridge-1", true)]
    [TestCase("bridge-1", "bridge-2", false)]
    [TestCase("", "bridge-1", false)]
    [TestCase("bridge-1", "", false)]
    public void IsBridgeConnectionActivated_IsScopedToCurrentConnection(
        string activatedConnectionId,
        string currentConnectionId,
        bool expected)
    {
        var result = ComfySessionEventRelay.IsBridgeConnectionActivated(
            activatedConnectionId,
            currentConnectionId);

        Assert.That(result, Is.EqualTo(expected));
    }

    [TestCase("feature_flags", true)]
    [TestCase("status", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void ShouldSuppressGpuServerFrame_MatchesOnlyFeatureMessageType(string? eventType, bool expected)
    {
        Assert.That(ComfySessionEventRelay.ShouldSuppressGpuServerFrame(eventType), Is.EqualTo(expected));
    }

    private static async Task<T> WaitForAsync<T>(Task<T> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, task))
        {
            Assert.Fail($"Timed out after {timeout.TotalSeconds:F1}s waiting for async signal.");
        }

        return await task.ConfigureAwait(false);
    }

    private sealed class RejectStaleSessionSink : IRunnerSessionSink
    {
        public int PermanentFailureCount { get; private set; }

        public TaskCompletionSource<string> FirstDeliveredSession { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RegisterRunnerResponse> RegisterRunnerAsync(RegisterRunnerRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new RegisterRunnerResponse { Accepted = true });

        public Task<SyncLocalModelsResponse> SyncLocalModelsAsync(SyncLocalModelsRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new SyncLocalModelsResponse());

        public Task AckStartSessionAsync(AcknowledgeSessionStartRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AckStopSessionAsync(AcknowledgeSessionStopRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GrantGpuPowerExecutionResponse> GrantGpuPowerExecutionAsync(GrantGpuPowerExecutionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("Power execution is not part of this fixture");

        public Task AckGpuPowerLimitAsync(AcknowledgeGpuPowerLimitRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<GrantPromptExecutionResponse> GrantPromptExecutionAsync(GrantPromptExecutionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("This fixture does not execute prompts.");
        public Task ReportPromptEvidenceAsync(ReportPromptEvidenceRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("This fixture does not observe prompts.");
        public Task AckSubmitEditorActionAsync(AcknowledgePromptResultRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportModelDownloadAsync(ReportModelDownloadRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportRunnerLogsAsync(ReportRunnerLogsRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportSessionLogsAsync(ReportSessionLogsRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportRuntimeTelemetryAsync(ReportRuntimeTelemetryRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportSessionEventsAsync(ReportSessionEventsRequest request, CancellationToken cancellationToken)
        {
            if (request.Events.Any(evt => evt.SessionId == "stale-session"))
            {
                PermanentFailureCount++;
                throw new RpcException(new Status(StatusCode.PermissionDenied, "stale session"));
            }

            FirstDeliveredSession.TrySetResult(request.Events[0].SessionId);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingThenRecordingSink : IRunnerSessionSink
    {
        private readonly List<string> _deliveredSessions = [];
        private int _callCount;

        public TaskCompletionSource<bool> FirstInvocationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseFirstInvocation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string[]> DeliveredSessions { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RegisterRunnerResponse> RegisterRunnerAsync(RegisterRunnerRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new RegisterRunnerResponse { Accepted = true });

        public Task<SyncLocalModelsResponse> SyncLocalModelsAsync(SyncLocalModelsRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new SyncLocalModelsResponse());

        public Task AckStartSessionAsync(AcknowledgeSessionStartRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AckStopSessionAsync(AcknowledgeSessionStopRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GrantGpuPowerExecutionResponse> GrantGpuPowerExecutionAsync(GrantGpuPowerExecutionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("Power execution is not part of this fixture");

        public Task AckGpuPowerLimitAsync(AcknowledgeGpuPowerLimitRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<GrantPromptExecutionResponse> GrantPromptExecutionAsync(GrantPromptExecutionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("This fixture does not execute prompts.");
        public Task ReportPromptEvidenceAsync(ReportPromptEvidenceRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("This fixture does not observe prompts.");
        public Task AckSubmitEditorActionAsync(AcknowledgePromptResultRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportModelDownloadAsync(ReportModelDownloadRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportRunnerLogsAsync(ReportRunnerLogsRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportSessionLogsAsync(ReportSessionLogsRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportRuntimeTelemetryAsync(ReportRuntimeTelemetryRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task ReportSessionEventsAsync(ReportSessionEventsRequest request, CancellationToken cancellationToken)
        {
            var callNumber = Interlocked.Increment(ref _callCount);
            if (callNumber == 1)
            {
                FirstInvocationStarted.TrySetResult(true);
                await ReleaseFirstInvocation.Task.ConfigureAwait(false);
            }

            _deliveredSessions.AddRange(request.Events.Select(evt => evt.SessionId));
            if (_deliveredSessions.Count >= 3)
            {
                DeliveredSessions.TrySetResult(_deliveredSessions.ToArray());
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
