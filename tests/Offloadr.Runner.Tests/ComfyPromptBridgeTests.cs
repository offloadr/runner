using Google.Protobuf;
using Offloadr.Runner.V1;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;

namespace Offloadr.Runner.Tests;

public sealed partial class ComfyPromptBridgeTests
{
    [TestCase("execution_success")]
    [TestCase("execution_error")]
    [TestCase("execution_interrupted")]
    public async Task TerminalFrameCarriesTheHandoffMappedFromTheNativeResponseAndSurvivesMissingUploader(string eventType)
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var command = Command();
        var promptId = NativePromptId(command);
        var payload = Frame(command, promptId).Payload.ToStringUtf8().Replace("execution_success", eventType);
        var delivered = System.Threading.Channels.Channel.CreateUnbounded<SessionStreamEvent>();
        var refreshCaptureAttempts = 0;
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, RunnerId, "127.0.0.1", port, "test-process", _ => new SessionSealingStatus(), isTrackedRuntime: (_, _) => true,
            captureSessionArtifactRefresh: _ =>
            {
                Interlocked.Increment(ref refreshCaptureAttempts);
                throw new InvalidOperationException("The artifact uploader was removed during Stop.");
            });
        relay.AttachSink(new BridgeSink(request =>
        {
            foreach (var frame in request.Events)
                delivered.Writer.TryWrite(frame.Clone());
            return Task.CompletedTask;
        }));
        var finished = System.Threading.Channels.Channel.CreateUnbounded<ComfySessionEventRelay.PromptTerminalEvent>();
        relay.PromptFinished += prompt => finished.Writer.TryWrite(prompt);
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.LifecycleGeneration,
            command.RuntimeEpoch, command.RuntimeInstanceId, [], CancellationToken.None);
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        // The platform sends no native id; the bridge learns it from ComfyUI's response.
        relay.MapPrompt(command.SessionId, "client", command, promptId);
        Assert.That(MappedPromptCount(relay), Is.EqualTo(1));

        await socket.SendAsync(ByteString.CopyFromUtf8(payload).Memory, WebSocketMessageType.Text, true, CancellationToken.None);
        var live = await delivered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        var ended = await finished.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Multiple(() =>
        {
            Assert.That(live.HandoffId, Is.EqualTo(command.HandoffId));
            Assert.That(live.PromptId, Is.EqualTo(promptId));
            Assert.That(live.Payload.ToStringUtf8(), Is.EqualTo(payload));
            Assert.That(ended.PromptId, Is.EqualTo(promptId));
            Assert.That(ended.HandoffId, Is.EqualTo(command.HandoffId));
            Assert.That(ended.SessionId, Is.EqualTo(command.SessionId));
            Assert.That(ended.Runtime, Is.EqualTo(new Offloadr.Runner.Core.RuntimeIdentity(
                command.LifecycleGeneration, command.RuntimeEpoch, command.RuntimeInstanceId)));
            Assert.That(refreshCaptureAttempts, Is.EqualTo(eventType == "execution_success" ? 1 : 0));
        });

        // A prompt that was never mapped (another client's, or evicted) still reports its
        // terminal event, so its model demand can end; it names no handoff.
        var unmappedPromptId = Guid.NewGuid().ToString("D");
        await socket.SendAsync(ByteString.CopyFromUtf8(payload.Replace(promptId, unmappedPromptId)).Memory,
            WebSocketMessageType.Text, true, CancellationToken.None);
        var unmappedLive = await delivered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        var unmapped = await finished.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Multiple(() =>
        {
            Assert.That(unmappedLive.HandoffId, Is.Empty);
            Assert.That(unmapped.PromptId, Is.EqualTo(unmappedPromptId));
            Assert.That(unmapped.HandoffId, Is.Null);
        });
        await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static int MappedPromptCount(ComfySessionEventRelay relay)
    {
        var bridges = (IDictionary)typeof(ComfySessionEventRelay)
            .GetField("_bridges", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(relay)!;
        return bridges.Values.Cast<object>().Sum(bridge => ((IDictionary)bridge.GetType()
            .GetField("_promptMap", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!).Count);
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
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, RunnerId, "127.0.0.1", port, "test-process", _ => new SessionSealingStatus(), isTrackedRuntime: (_, _) => true);
        relay.AttachSink(new BridgeSink(request =>
        {
            foreach (var frame in request.Events)
                delivered.Enqueue(frame.Clone());
            return Task.CompletedTask;
        }));
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.LifecycleGeneration,
            command.RuntimeEpoch, command.RuntimeInstanceId, [], CancellationToken.None);
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
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, RunnerId, "127.0.0.1", port, "test-process", _ => new SessionSealingStatus(), isTrackedRuntime: (_, _) => true);

        var sink = new BridgeSink();
        relay.AttachSink(sink);
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.LifecycleGeneration,
            command.RuntimeEpoch, command.RuntimeInstanceId, [], CancellationToken.None);
        var first = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var firstSocket = (await first.AcceptWebSocketAsync(null)).WebSocket;
        var accepted = await sink.Authority.Activations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await relay.SendClientMessageAsync(command.SessionId, command.EditorSid, "client", command.LifecycleGeneration,
            command.RuntimeEpoch, command.RuntimeInstanceId, "{\"action\":\"once\"}"u8.ToArray(),
            accepted.BridgeConnectionId, accepted.LeaseRevision, CancellationToken.None);
        var buffer = new byte[1024];
        var received = await firstSocket.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(System.Text.Encoding.UTF8.GetString(buffer, 0, received.Count), Is.EqualTo("{\"action\":\"once\"}"));

        // Drop the connection; the bridge reconnects but must not send the message again.
        firstSocket.Abort();
        firstSocket.Dispose();
        var second = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var secondSocket = (await second.AcceptWebSocketAsync(null)).WebSocket;
        await sink.Authority.Activations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.ThrowsAsync<InvalidOperationException>(() => relay.SendClientMessageAsync(command.SessionId, command.EditorSid,
            "client", command.LifecycleGeneration, command.RuntimeEpoch, command.RuntimeInstanceId,
            "stale"u8.ToArray(), accepted.BridgeConnectionId, accepted.LeaseRevision, CancellationToken.None));
        using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        Assert.That(
            async () => await secondSocket.ReceiveAsync(buffer, quiet.Token),
            Throws.InstanceOf<OperationCanceledException>().Or.InstanceOf<WebSocketException>());
    }

    [Test]
    public async Task PromptMappingIsBoundedAndForgetsTheOldestPrompt()
    {
        var command = Command();
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, RunnerId, "127.0.0.1", 1, "test-process", _ => new SessionSealingStatus(), isTrackedRuntime: (_, _) => true);
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.LifecycleGeneration,
            command.RuntimeEpoch, command.RuntimeInstanceId, [], CancellationToken.None);
        var promptIds = new List<string>();
        for (var index = 0; index < ComfySessionEventRelay.MaxRetainedPromptIdentities * 2; index++)
        {
            command.HandoffId = Guid.NewGuid().ToString("n");
            promptIds.Add(Guid.NewGuid().ToString("D"));
            relay.MapPrompt(command.SessionId, "client", command, promptIds[^1]);
        }
        var bridges = (IDictionary)typeof(ComfySessionEventRelay)
            .GetField("_bridges", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(relay)!;
        var bridge = bridges.Values.Cast<object>().Single();
        var mapped = (IDictionary)bridge.GetType().GetField("_promptMap", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        Assert.Multiple(() =>
        {
            Assert.That(mapped.Count, Is.EqualTo(ComfySessionEventRelay.MaxRetainedPromptIdentities));
            Assert.That(mapped.Contains(promptIds[0]), Is.False);
            Assert.That(mapped[promptIds[^1]], Is.EqualTo(command.HandoffId));
        });
    }

    [Test]
    public async Task ExistingBridgeCannotBeReboundToAnotherRuntime()
    {
        var command = Command();
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, RunnerId, "127.0.0.1", 1, "test-process", _ => new SessionSealingStatus(), isTrackedRuntime: (_, runtime) => runtime.SameRuntime(new(command.LifecycleGeneration, command.RuntimeEpoch, command.RuntimeInstanceId)));
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.LifecycleGeneration,
            command.RuntimeEpoch, command.RuntimeInstanceId, [], CancellationToken.None);
        Assert.ThrowsAsync<InvalidOperationException>(() => relay.EnsureBridgeAsync(command.SessionId, command.EditorSid,
            "client", command.LifecycleGeneration, command.RuntimeEpoch + 1, Guid.NewGuid().ToString("n"), [], CancellationToken.None));
    }

    [Test]
    public async Task BridgeIsRefusedUnlessTheExactRuntimeIsTracked()
    {
        var command = Command();
        var checkedRuntimes = new List<(string Session, RuntimeIdentity Runtime)>();
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, RunnerId, "127.0.0.1", 1, "test-process", _ => new SessionSealingStatus(),
            isTrackedRuntime: (session, runtime) =>
            {
                checkedRuntimes.Add((session, runtime));
                return false;
            });

        Assert.ThrowsAsync<InvalidOperationException>(() => relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client",
            command.LifecycleGeneration, command.RuntimeEpoch, Guid.Parse(command.RuntimeInstanceId).ToString("D"), [], CancellationToken.None));
        Assert.That(BridgeCount(relay), Is.Zero);
        Assert.That(checkedRuntimes, Is.EqualTo(new[]
        {
            (command.SessionId, new RuntimeIdentity(command.LifecycleGeneration, command.RuntimeEpoch, command.RuntimeInstanceId)),
        }));
    }

    [Test]
    public async Task BridgeLookupForAnotherRuntimeDoesNotReachTheBridge()
    {
        var command = Command();
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, RunnerId, "127.0.0.1", 1, "test-process", _ => new SessionSealingStatus(), isTrackedRuntime: (_, _) => true);
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.LifecycleGeneration,
            command.RuntimeEpoch, command.RuntimeInstanceId, [], CancellationToken.None);

        var other = command.Clone();
        other.RuntimeEpoch++;
        relay.MapPrompt(command.SessionId, "client", other, NativePromptId(other));
        Assert.That(MappedPromptCount(relay), Is.Zero);

        var otherEditor = command.Clone();
        otherEditor.EditorSid = "another-editor";
        relay.MapPrompt(command.SessionId, "client", otherEditor, NativePromptId(otherEditor));
        Assert.That(MappedPromptCount(relay), Is.Zero);

        relay.MapPrompt(command.SessionId, "client", command, NativePromptId(command));
        Assert.That(MappedPromptCount(relay), Is.EqualTo(1));
    }

    [Test]
    public async Task TrackedReplacementRuntimeRetiresThePreviousRuntimesBridge()
    {
        var command = Command();
        var tracked = new RuntimeIdentity(command.LifecycleGeneration, command.RuntimeEpoch, command.RuntimeInstanceId);
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, RunnerId, "127.0.0.1", 1, "test-process", _ => new SessionSealingStatus(),
            isTrackedRuntime: (_, runtime) => runtime.SameRuntime(tracked));
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.LifecycleGeneration,
            command.RuntimeEpoch, command.RuntimeInstanceId, [], CancellationToken.None);

        var replacement = command.Clone();
        replacement.RuntimeEpoch++;
        replacement.RuntimeInstanceId = Guid.NewGuid().ToString("n");
        tracked = new RuntimeIdentity(replacement.LifecycleGeneration, replacement.RuntimeEpoch, replacement.RuntimeInstanceId);
        await relay.EnsureBridgeAsync(replacement.SessionId, replacement.EditorSid, "client", replacement.LifecycleGeneration,
            replacement.RuntimeEpoch, replacement.RuntimeInstanceId, [], CancellationToken.None);

        Assert.That(BridgeCount(relay), Is.EqualTo(1));
        relay.MapPrompt(command.SessionId, "client", command, NativePromptId(command));
        Assert.That(MappedPromptCount(relay), Is.Zero, "The retired runtime's bridge no longer maps prompts.");
        relay.MapPrompt(replacement.SessionId, "client", replacement, NativePromptId(replacement));
        Assert.That(MappedPromptCount(relay), Is.EqualTo(1));
    }

    private static int BridgeCount(ComfySessionEventRelay relay)
        => ((IDictionary)typeof(ComfySessionEventRelay)
            .GetField("_bridges", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(relay)!).Count;

    private static readonly string RunnerId = Guid.NewGuid().ToString("n");

    private sealed class BridgeSink(Func<ReportSessionEventsRequest, Task>? reportEvents = null) : IRunnerSessionSink
    {
        public Task<ReadEditorRuntimeBridgeCursorResponse> ReadEditorRuntimeBridgeCursorAsync(ReadEditorRuntimeBridgeCursorRequest request, CancellationToken token) => Authority.CursorAsync(request, token);
        public Task<ReserveEditorRuntimeBridgeResponse> ReserveEditorRuntimeBridgeAsync(ReserveEditorRuntimeBridgeRequest request, CancellationToken token) => Authority.ReserveAsync(request, token);
        public Task<ReportEditorRuntimeBridgeStateResponse> ReportEditorRuntimeBridgeStateAsync(ReportEditorRuntimeBridgeStateRequest request, CancellationToken token) => Authority.ReportAsync(request, token);
        public BridgeAuthority Authority { get; } = new();

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

    private static SubmitPromptCommand Command() => new()
    {
        HandoffId = Guid.NewGuid().ToString("n"),
        SessionId = Guid.NewGuid().ToString("n"),
        EditorSid = "editor",
        Owner = "alice",
        EditorRuntimeKind = "comfyui",
        LifecycleGeneration = 7,
        RuntimeEpoch = 11,
        RuntimeInstanceId = Guid.NewGuid().ToString("n"),
        ClientId = "client",
    };

    // ComfyUI chooses the prompt id; any value the bridge has not seen works for tests.
    private static string NativePromptId(SubmitPromptCommand command) => Guid.Parse(command.HandoffId).ToString("D");

    private static SessionStreamEvent Frame(SubmitPromptCommand command) => Frame(command, NativePromptId(command));

    private static SessionStreamEvent Frame(SubmitPromptCommand command, string promptId) => new()
    {
        HandoffId = command.HandoffId,
        PromptId = promptId,
        RunnerId = RunnerId,
        SessionId = command.SessionId,
        EditorSid = command.EditorSid,
        LifecycleGeneration = command.LifecycleGeneration,
        RuntimeEpoch = command.RuntimeEpoch,
        RuntimeInstanceId = command.RuntimeInstanceId,
        FrameType = SessionStreamFrameType.Text,
        EventType = "execution_success",
        Payload = ByteString.CopyFromUtf8("{\"type\":\"execution_success\",\"data\":{\"prompt_id\":\"" + promptId + "\"}}"),
    };
}
