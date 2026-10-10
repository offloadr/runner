using Google.Protobuf;
using Grpc.Core;
using Offloadr.Common.V1;
using Offloadr.Runner.V1;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;

namespace Offloadr.Runner.Tests;

public sealed class ComfyPromptEvidenceTests
{
    [Test]
    public async Task TerminalEvidenceCaptureRetriesOriginalBytesAfterLostAcknowledgement()
    {
        var command = Command();
        var original = command.Clone();
        var frame = Frame(command);
        var payload = frame.Payload;
        await using var relay = new ComfySessionEventRelay(command.Target.RunnerId, "127.0.0.1", 8188);
        var observed = new List<ReportPromptEvidenceRequest>();
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new EvidenceSink((ReportPromptEvidenceRequest request, CancellationToken _) =>
            {
                observed.Add(request.Clone());
                if (observed.Count == 1)
                {
                    // Mutation after original delivery must not rebind its retry.
                    command.Target.RuntimeEpoch++;
                    command.SubmissionId = Guid.NewGuid().ToString("n");
                    frame.Payload = ByteString.Empty;
                    throw new RpcException(new(StatusCode.Unavailable, "acknowledgement response lost"));
                }
                retried.TrySetResult();
                return Task.CompletedTask;
            });
        relay.AttachSink(sink);
        var evidence = relay.CapturePromptEvidence(command, frame);
        Assert.That(evidence, Is.Not.Null);
        await relay.ReportRetainedPromptEvidenceAsync(evidence!);
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(observed, Has.Count.EqualTo(2));
        Assert.That(observed[1], Is.EqualTo(observed[0]));
        Assert.That(observed[1].SubmissionId, Is.EqualTo(original.SubmissionId));
        Assert.That(observed[1].Target, Is.EqualTo(original.Target));
        Assert.That(observed[1].Evidence.NativePayload, Is.EqualTo(payload));
    }

    [TestCase("prompt")]
    [TestCase("runtime")]
    [TestCase("generation")]
    [TestCase("submission")]
    [TestCase("nonterminal")]
    public async Task UnmatchedOrNonterminalFrameCannotCreateDurableEvidence(string mismatch)
    {
        var command = Command();
        var frame = Frame(command);
        if (mismatch == "prompt") frame.PromptId = Guid.NewGuid().ToString("D");
        if (mismatch == "runtime") frame.RuntimeInstanceId = Guid.NewGuid().ToString("n");
        if (mismatch == "generation") frame.LifecycleGeneration++;
        if (mismatch == "submission") frame.SubmissionId = Guid.NewGuid().ToString("n");
        if (mismatch == "nonterminal") frame.EventType = "status";
        await using var relay = new ComfySessionEventRelay(command.Target.RunnerId, "127.0.0.1", 8188);
        var sink = new EvidenceSink((_, _) => throw new AssertionException("Unmatched evidence reached the sink."));
        relay.AttachSink(sink);
        Assert.That(relay.CapturePromptEvidence(command, frame), Is.Null);
    }

    [TestCase("execution_success")]
    [TestCase("execution_error")]
    [TestCase("execution_interrupted")]
    public async Task TerminalFrameSurvivesMissingUploaderAndDoesNotWaitForEvidenceDeliveryOrBlockStop(string eventType)
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var command = Command();
        var nativeFrame = Frame(command);
        var payload = nativeFrame.Payload.ToStringUtf8().Replace("execution_success", eventType);
        var reporting = new TaskCompletionSource<ReportPromptEvidenceRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowEvidence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveredEvidence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveredFrame = new TaskCompletionSource<SessionStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken evidenceToken = default;
        var refreshCaptureAttempts = 0;
        await using var relay = new ComfySessionEventRelay(command.Target.RunnerId, "127.0.0.1", port,
            captureSessionArtifactRefresh: _ =>
            {
                Interlocked.Increment(ref refreshCaptureAttempts);
                throw new InvalidOperationException("The artifact uploader was removed during Stop.");
            });
        var sink = new EvidenceSink(async (request, token) =>
        {
            evidenceToken = token;
            reporting.TrySetResult(request.Clone());
            await allowEvidence.Task.WaitAsync(token);
            deliveredEvidence.TrySetResult();
        }, request =>
        {
            foreach (var frame in request.Events)
                deliveredFrame.TrySetResult(frame.Clone());
            return Task.CompletedTask;
        });
        relay.AttachSink(sink);
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
            command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, [], CancellationToken.None);
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        relay.RegisterSubmission(command.SessionId, "client", command);
        var pending = command.Clone();
        pending.SubmissionId = Guid.NewGuid().ToString("n");
        pending.NativeIdentifiers.PromptId = Guid.NewGuid().ToString("D");
        relay.RegisterSubmission(command.SessionId, "client", pending);
        Assert.That(RetainedCommandCount(relay), Is.EqualTo(2));
        try
        {
            await socket.SendAsync(ByteString.CopyFromUtf8(payload).Memory, WebSocketMessageType.Text, true, CancellationToken.None);
            var captured = await reporting.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var live = await deliveredFrame.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Multiple(() =>
            {
                Assert.That(live.Payload.ToStringUtf8(), Is.EqualTo(payload));
                Assert.That(captured.Evidence.NativePayload, Is.EqualTo(live.Payload));
                Assert.That(captured.Target, Is.EqualTo(command.Target));
                Assert.That(RetainedCommandCount(relay), Is.EqualTo(1), "Only the unfinished command remains in the bridge.");
                Assert.That(deliveredEvidence.Task.IsCompleted, Is.False);
                Assert.That(refreshCaptureAttempts, Is.EqualTo(eventType == "execution_success" ? 1 : 0));
            });
            await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(evidenceToken.IsCancellationRequested, Is.False, "Stopping the bridge does not retire captured evidence.");
            allowEvidence.TrySetResult();
            await deliveredEvidence.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            allowEvidence.TrySetResult();
        }
    }

    private static int RetainedCommandCount(ComfySessionEventRelay relay)
    {
        // Inspect retained objects, not just a second report: duplicate report
        // suppression alone would not prove the large command was released.
        var bridges = (IDictionary)typeof(ComfySessionEventRelay)
            .GetField("_bridges", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(relay)!;
        return bridges.Values.Cast<object>().Sum(bridge => ((IDictionary)bridge.GetType()
            .GetField("_submissionCommands", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!).Count);
    }

    [Test]
    public async Task OversizedWebSocketMessageIsDroppedWithoutBlockingLaterMessages()
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var command = Command();
        var delivered = new System.Collections.Concurrent.ConcurrentQueue<SessionStreamEvent>();
        await using var relay = new ComfySessionEventRelay(command.Target.RunnerId, "127.0.0.1", port);
        relay.AttachSink(new EvidenceSink((_, _) => Task.CompletedTask, request =>
        {
            foreach (var frame in request.Events)
                delivered.Enqueue(frame.Clone());
            return Task.CompletedTask;
        }));
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
            command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, [], CancellationToken.None);
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;

        // One message just over the cap, sent in fragments, then a normal one.
        var fragment = new byte[1024 * 1024];
        var fragments = ComfySessionEventRelay.MaxRelayedMessageBytes / fragment.Length + 1;
        for (var index = 0; index < fragments; index++)
        {
            await socket.SendAsync(fragment, WebSocketMessageType.Binary, endOfMessage: index == fragments - 1, CancellationToken.None);
        }

        await socket.SendAsync("{\"type\":\"status\"}"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (delivered.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.That(delivered.Select(static frame => frame.Payload.ToStringUtf8()), Is.EqualTo(new[] { "{\"type\":\"status\"}" }));
    }

    [Test]
    public async Task ClientMessageIsSentOnceAndNotReplayedAfterAReconnect()
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var command = Command();
        await using var relay = new ComfySessionEventRelay(command.Target.RunnerId, "127.0.0.1", port);

        var send = relay.SendClientMessageAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
            command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, "{\"action\":\"once\"}"u8.ToArray(), CancellationToken.None);
        var first = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var firstSocket = (await first.AcceptWebSocketAsync(null)).WebSocket;
        await send.WaitAsync(TimeSpan.FromSeconds(5));
        var buffer = new byte[1024];
        var received = await firstSocket.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(System.Text.Encoding.UTF8.GetString(buffer, 0, received.Count), Is.EqualTo("{\"action\":\"once\"}"));

        // Drop the connection; the bridge reconnects but must not send the message again.
        firstSocket.Abort();
        firstSocket.Dispose();
        var second = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var secondSocket = (await second.AcceptWebSocketAsync(null)).WebSocket;
        using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        Assert.That(
            async () => await secondSocket.ReceiveAsync(buffer, quiet.Token),
            Throws.InstanceOf<OperationCanceledException>().Or.InstanceOf<WebSocketException>());
    }

    [Test]
    public async Task MissedTerminalFramesRetainOnlyBoundedBodyFreeEvidenceIdentities()
    {
        var command = Command();
        command.PromptJson = new string('x', 1024 * 1024);
        command.PreflightRequests.Add(new PromptHttpRequest { Body = command.PromptJson });
        command.WebsocketClientMessage = ByteString.CopyFromUtf8(command.PromptJson);
        await using var relay = new ComfySessionEventRelay(command.Target.RunnerId, "127.0.0.1", 1);
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
            command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, [], CancellationToken.None);
        var oldest = command.Clone();
        for (var index = 0; index < ComfySessionEventRelay.MaxRetainedPromptIdentities * 2; index++)
        {
            relay.RegisterSubmission(command.SessionId, "client", command);
            if (index + 1 == ComfySessionEventRelay.MaxRetainedPromptIdentities * 2) break;
            command.CommandId = Guid.NewGuid().ToString("n");
            command.SubmissionId = Guid.NewGuid().ToString("n");
            command.NativeIdentifiers.PromptId = Guid.NewGuid().ToString("D");
        }
        var bridges = (IDictionary)typeof(ComfySessionEventRelay)
            .GetField("_bridges", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(relay)!;
        var bridge = bridges.Values.Cast<object>().Single();
        var retained = (IDictionary)bridge.GetType().GetField("_submissionCommands", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        var order = (IEnumerable)bridge.GetType().GetField("_submissionOrder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        Assert.That(retained.Count, Is.EqualTo(ComfySessionEventRelay.MaxRetainedPromptIdentities));
        Assert.That(retained.Contains(oldest.NativeIdentifiers.PromptId), Is.False);
        var ordered = order.Cast<KeyValuePair<string, SubmitPromptCommand>>().ToArray();
        Assert.That(ordered, Has.Length.EqualTo(ComfySessionEventRelay.MaxRetainedPromptIdentities));
        foreach (var identity in retained.Values.Cast<SubmitPromptCommand>().Concat(ordered.Select(item => item.Value)))
        {
            Assert.That(identity.PromptJson, Is.Empty);
            Assert.That(identity.Downloads, Is.Empty);
            Assert.That(identity.PreflightRequests, Is.Empty);
            Assert.That(identity.WebsocketClientMessage, Is.Empty);
            Assert.That(identity.CalculateSize(), Is.LessThan(1024));
        }
        var newest = (SubmitPromptCommand)retained[command.NativeIdentifiers.PromptId]!;
        var captured = relay.CapturePromptEvidence(newest, Frame(command));
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.CommandId, Is.EqualTo(command.CommandId));
        Assert.That(captured.Target, Is.EqualTo(command.Target));
        // Eviction changes only transient correlation, never permission or replay.
        relay.UnregisterUninvokedSubmission(command.SessionId, "client", oldest);
        Assert.That(RetainedCommandCount(relay), Is.EqualTo(ComfySessionEventRelay.MaxRetainedPromptIdentities));
    }

    [Test]
    public async Task ExistingBridgeCannotBeReboundToAnotherRuntime()
    {
        var command = Command();
        await using var relay = new ComfySessionEventRelay(command.Target.RunnerId, "127.0.0.1", 1);
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
            command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, [], CancellationToken.None);
        Assert.ThrowsAsync<InvalidOperationException>(() => relay.EnsureBridgeAsync(command.SessionId, command.EditorSid,
            "client", command.Target.GpuGeneration, command.Target.RuntimeEpoch + 1, Guid.NewGuid().ToString("n"), [], CancellationToken.None));
    }

    [Test]
    public async Task BridgeIsRefusedUnlessTheExactRuntimeIsTracked()
    {
        var command = Command();
        var checkedRuntimes = new List<(string Session, RuntimeIdentity Runtime)>();
        await using var relay = new ComfySessionEventRelay(command.Target.RunnerId, "127.0.0.1", 1,
            isTrackedRuntime: (session, runtime) =>
            {
                checkedRuntimes.Add((session, runtime));
                return false;
            });

        Assert.ThrowsAsync<InvalidOperationException>(() => relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client",
            command.Target.GpuGeneration, command.Target.RuntimeEpoch, Guid.Parse(command.Target.RuntimeInstanceId).ToString("D"), [], CancellationToken.None));
        Assert.That(BridgeCount(relay), Is.Zero);
        Assert.That(checkedRuntimes, Is.EqualTo(new[]
        {
            (command.SessionId, new RuntimeIdentity(command.Target.GpuGeneration, command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId)),
        }));
    }

    [Test]
    public async Task BridgeLookupForAnotherRuntimeDoesNotReachTheBridge()
    {
        var command = Command();
        await using var relay = new ComfySessionEventRelay(command.Target.RunnerId, "127.0.0.1", 1, isTrackedRuntime: (_, _) => true);
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
            command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, [], CancellationToken.None);

        var other = command.Clone();
        other.Target.RuntimeEpoch++;
        relay.RegisterSubmission(command.SessionId, "client", other);
        Assert.That(RetainedCommandCount(relay), Is.Zero);

        relay.RegisterSubmission(command.SessionId, "client", command);
        Assert.That(RetainedCommandCount(relay), Is.EqualTo(1));
    }

    [Test]
    public async Task TrackedReplacementRuntimeRetiresThePreviousRuntimesBridge()
    {
        var command = Command();
        var tracked = new RuntimeIdentity(command.Target.GpuGeneration, command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId);
        await using var relay = new ComfySessionEventRelay(command.Target.RunnerId, "127.0.0.1", 1,
            isTrackedRuntime: (_, runtime) => runtime.SameRuntime(tracked));
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
            command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, [], CancellationToken.None);

        var replacement = command.Clone();
        replacement.Target.RuntimeEpoch++;
        replacement.Target.RuntimeInstanceId = Guid.NewGuid().ToString("n");
        tracked = new RuntimeIdentity(replacement.Target.GpuGeneration, replacement.Target.RuntimeEpoch, replacement.Target.RuntimeInstanceId);
        await relay.EnsureBridgeAsync(replacement.SessionId, replacement.EditorSid, "client", replacement.Target.GpuGeneration,
            replacement.Target.RuntimeEpoch, replacement.Target.RuntimeInstanceId, [], CancellationToken.None);

        Assert.That(BridgeCount(relay), Is.EqualTo(1));
        relay.RegisterSubmission(command.SessionId, "client", command);
        Assert.That(RetainedCommandCount(relay), Is.Zero, "The retired runtime's bridge no longer accepts submissions.");
        relay.RegisterSubmission(replacement.SessionId, "client", replacement);
        Assert.That(RetainedCommandCount(relay), Is.EqualTo(1));
    }

    private static int BridgeCount(ComfySessionEventRelay relay)
        => ((IDictionary)typeof(ComfySessionEventRelay)
            .GetField("_bridges", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(relay)!).Count;

    [Test]
    public async Task UninvokedPromptReleasesOnlyItsExactRetainedCommand()
    {
        var command = Command();
        await using var relay = new ComfySessionEventRelay(command.Target.RunnerId, "127.0.0.1", 1);
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
            command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, [], CancellationToken.None);
        relay.RegisterSubmission(command.SessionId, "client", command);
        Assert.That(RetainedCommandCount(relay), Is.EqualTo(1));
        var differentDelivery = command.Clone();
        differentDelivery.CommandId = Guid.NewGuid().ToString("n");
        relay.UnregisterUninvokedSubmission(command.SessionId, "client", differentDelivery);
        Assert.That(RetainedCommandCount(relay), Is.EqualTo(1));
        relay.UnregisterUninvokedSubmission(command.SessionId, "client", command);
        Assert.That(RetainedCommandCount(relay), Is.Zero);
    }

    private sealed class EvidenceSink(Func<ReportPromptEvidenceRequest, CancellationToken, Task> report,
        Func<ReportSessionEventsRequest, Task>? reportEvents = null) : IRunnerSessionSink
    {
        public Task ReportPromptEvidenceAsync(ReportPromptEvidenceRequest request, CancellationToken token) => report(request, token);
        public Task<RegisterRunnerResponse> RegisterRunnerAsync(RegisterRunnerRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<SyncLocalModelsResponse> SyncLocalModelsAsync(SyncLocalModelsRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task AckStartSessionAsync(AcknowledgeSessionStartRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task AckStopSessionAsync(AcknowledgeSessionStopRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<GrantGpuPowerExecutionResponse> GrantGpuPowerExecutionAsync(GrantGpuPowerExecutionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("Power execution is not part of this fixture");

        public Task AckGpuPowerLimitAsync(AcknowledgeGpuPowerLimitRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<GrantPromptExecutionResponse> GrantPromptExecutionAsync(GrantPromptExecutionRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task AckSubmitEditorActionAsync(AcknowledgePromptResultRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task ReportModelDownloadAsync(ReportModelDownloadRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task ReportSessionEventsAsync(ReportSessionEventsRequest request, CancellationToken token) => reportEvents?.Invoke(request) ?? Task.CompletedTask;
        public Task ReportSessionLogsAsync(ReportSessionLogsRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task ReportRunnerLogsAsync(ReportRunnerLogsRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task ReportRuntimeTelemetryAsync(ReportRuntimeTelemetryRequest request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static SubmitPromptCommand Command()
    {
        var id = Guid.NewGuid().ToString("n");
        var session = Guid.NewGuid().ToString("n");
        return new()
        {
            SubmissionId = id,
            CommandId = Guid.NewGuid().ToString("n"),
            SessionId = session,
            EditorSid = "editor",
            Owner = "alice",
            NativeIdentifiers = new() { PromptId = Guid.Parse(id).ToString("D") },
            Target = new()
            {
                EditorLifecycleId = "cpu",
                EditorGeneration = 1,
                CpuRuntimeGeneration = 1,
                CpuRuntimeInstanceId = Guid.NewGuid().ToString("n"),
                GpuLifecycleId = "gpu",
                GpuGeneration = 7,
                AllocationId = "allocation",
                RuntimeEpoch = 11,
                RuntimeInstanceId = Guid.NewGuid().ToString("n"),
                RunnerId = Guid.NewGuid().ToString("n"),
                RunnerSessionId = session,
            },
        };
    }

    private static SessionStreamEvent Frame(SubmitPromptCommand command) => new()
    {
        SubmissionId = command.SubmissionId,
        PromptId = command.NativeIdentifiers.PromptId,
        RunnerId = command.Target.RunnerId,
        SessionId = command.SessionId,
        EditorSid = command.EditorSid,
        LifecycleGeneration = command.Target.GpuGeneration,
        RuntimeEpoch = command.Target.RuntimeEpoch,
        RuntimeInstanceId = command.Target.RuntimeInstanceId,
        FrameType = SessionStreamFrameType.Text,
        EventType = "execution_success",
        Payload = ByteString.CopyFromUtf8("{\"type\":\"execution_success\",\"data\":{\"prompt_id\":\"" + command.NativeIdentifiers.PromptId + "\"}}"),
    };
}
