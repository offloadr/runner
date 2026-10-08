using Offloadr.Runner.V1;
using Offloadr.EditorRuntime.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Offloadr.Runner.Linux;

/// <summary>
/// Connects to the local ComfyUI websocket for a session/client combination and forwards frames to Offloadr API.
/// Batches outbound events and forwards them to the active runner control session.
/// </summary>
internal sealed partial class ComfySessionEventRelay : IAsyncDisposable
{
    private enum FlushBatchOutcome
    {
        Succeeded,
        Retry,
        Drop
    }

    private const int DefaultQueueCapacity = 4096;
    private const int MaxBatchSize = 32;
    internal const int MaxRetainedPromptIdentities = 256;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(150);

    private readonly string _runnerId;
    private readonly string _comfyHost;
    private readonly int _comfyPort;
    private readonly Channel<SessionStreamEvent> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, SessionBridge> _bridges = new(StringComparer.Ordinal);
    private readonly Task _senderTask;
    private readonly object _sinkLock = new();
    private readonly Func<string, Func<Task>>? _captureSessionArtifactRefresh;
    private readonly Func<string, RuntimeIdentity, bool>? _isTrackedRuntime;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastArtifactRefreshUtc = new(StringComparer.Ordinal);

    internal static TimeSpan ArtifactRefreshDebounceInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    internal static bool ShouldSuppressGpuServerFrame(string? eventType)
        => string.Equals(eventType, "feature_flags", StringComparison.Ordinal);

    private IRunnerSessionSink? _sink;

    public ComfySessionEventRelay(
        string runnerId,
        string comfyHost,
        int comfyPort,
        Func<string, Func<Task>>? captureSessionArtifactRefresh = null,
        int queueCapacity = DefaultQueueCapacity,
        Func<string, RuntimeIdentity, bool>? isTrackedRuntime = null)
    {
        _runnerId = runnerId ?? throw new ArgumentNullException(nameof(runnerId));
        _comfyHost = string.IsNullOrWhiteSpace(comfyHost) ? "127.0.0.1" : comfyHost.Trim();
        _comfyPort = comfyPort;
        _captureSessionArtifactRefresh = captureSessionArtifactRefresh;
        _isTrackedRuntime = isTrackedRuntime;
        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity), "Queue capacity must be positive.");
        }

        _queue = Channel.CreateBounded<SessionStreamEvent>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _senderTask = Task.Run(SenderLoopAsync);
    }

    public void AttachSink(IRunnerSessionSink sink)
    {
        if (sink == null) throw new ArgumentNullException(nameof(sink));
        lock (_sinkLock)
        {
            _sink = sink;
        }
    }

    public void DetachSink(IRunnerSessionSink sink)
    {
        lock (_sinkLock)
        {
            if (ReferenceEquals(_sink, sink))
            {
                _sink = null;
            }
        }
    }

    public async Task EnsureBridgeAsync(
        string sessionId,
        string editorSid,
        string clientId,
        ulong lifecycleGeneration,
        ulong runtimeEpoch,
        string runtimeInstanceId,
        byte[] clientMessage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(editorSid)
            || lifecycleGeneration == 0 || runtimeEpoch == 0 || !Guid.TryParse(runtimeInstanceId, out var instance) || instance == Guid.Empty)
            throw new ArgumentException("A complete captured bridge identity is required.");

        var runtime = new RuntimeIdentity(lifecycleGeneration, runtimeEpoch, runtimeInstanceId).Normalized();

        // Only the session's tracked child may receive a bridge: a delayed relay
        // for an old runtime would otherwise reconnect to its replacement.
        if (_isTrackedRuntime is not null && !_isTrackedRuntime(sessionId.Trim(), runtime))
            throw new InvalidOperationException("The bridge identity is not the session's tracked runtime.");

        var key = BuildKey(sessionId, clientId, runtime);
        await RetireOtherRuntimeBridgesAsync(sessionId, clientId, key).ConfigureAwait(false);
        var bridge = _bridges.GetOrAdd(key, _ => new SessionBridge(this, _runnerId, sessionId, editorSid, clientId, lifecycleGeneration, runtimeEpoch, runtimeInstanceId, BuildWsUri(clientId)));
        bridge.UpdateConnectionData(editorSid, lifecycleGeneration, runtimeEpoch, runtimeInstanceId, clientMessage);
        await bridge.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RetireOtherRuntimeBridgesAsync(string sessionId, string clientId, string key)
    {
        var prefix = BuildClientPrefix(sessionId, clientId);
        foreach (var other in _bridges.Keys.Where(candidate =>
                     candidate.StartsWith(prefix, StringComparison.Ordinal) && !string.Equals(candidate, key, StringComparison.Ordinal)).ToArray())
        {
            // Without a runtime check this relay cannot tell which bridge is
            // current, so an existing bridge is never rebound.
            if (_isTrackedRuntime is null)
                throw new InvalidOperationException("An existing bridge cannot be rebound to another runtime.");

            if (_bridges.TryRemove(other, out var retired))
            {
                await retired.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task SendClientMessageAsync(
        string sessionId,
        string editorSid,
        string clientId,
        ulong lifecycleGeneration,
        ulong runtimeEpoch,
        string runtimeInstanceId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await EnsureBridgeAsync(sessionId, editorSid, clientId, lifecycleGeneration, runtimeEpoch, runtimeInstanceId, payload, cancellationToken).ConfigureAwait(false);
        if (TryGetBridge(sessionId, clientId, new RuntimeIdentity(lifecycleGeneration, runtimeEpoch, runtimeInstanceId), out var bridge))
        {
            await bridge.SendClientMessageAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    public void RegisterSubmission(string sessionId, string clientId, SubmitPromptCommand command)
    {
        if (TryGetBridge(sessionId, clientId, TargetRuntime(command), out var bridge))
        {
            bridge.RegisterSubmission(command);
        }
    }

    public void UnregisterUninvokedSubmission(string sessionId, string clientId, SubmitPromptCommand command)
    {
        if (TryGetBridge(sessionId, clientId, TargetRuntime(command), out var bridge))
        {
            bridge.UnregisterUninvokedSubmission(command);
        }
    }

    public void MapPrompt(string sessionId, string clientId, SubmitPromptCommand command, string promptId)
    {
        if (string.IsNullOrWhiteSpace(promptId) || string.IsNullOrWhiteSpace(command.SubmissionId)) return;
        if (TryGetBridge(sessionId, clientId, TargetRuntime(command), out var bridge))
        {
            bridge.MapPrompt(promptId, command.SubmissionId);
        }
    }

    private static RuntimeIdentity TargetRuntime(SubmitPromptCommand command)
        => command.Target is { } target
            ? new RuntimeIdentity(target.GpuGeneration, target.RuntimeEpoch, target.RuntimeInstanceId)
            : default;

    public async Task StopSessionAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        var prefix = sessionId.Trim() + "|";
        var matches = _bridges.Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(kvp => kvp.Key).ToList();
        foreach (var key in matches)
        {
            if (_bridges.TryRemove(key, out var bridge))
            {
                await bridge.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal bool Enqueue(SessionStreamEvent evt)
    {
        if (!_queue.Writer.TryWrite(evt))
        {
            RunnerLog.Error<ComfySessionEventRelay>(
                $"Session event queue overflow session={evt.SessionId ?? "-"} client={evt.ClientId ?? "-"}; dropping event.");
            return false;
        }

        return true;
    }

    private bool TryGetBridge(string sessionId, string clientId, RuntimeIdentity runtime, out SessionBridge bridge)
    {
        var key = BuildKey(sessionId, clientId, runtime.Normalized());
        return _bridges.TryGetValue(key, out bridge!);
    }

    private async Task<bool> ReportBridgeStateAsync(
        string sessionId,
        string editorSid,
        string clientId,
        ulong lifecycleGeneration,
        ulong runtimeEpoch,
        string runtimeInstanceId,
        string bridgeConnectionId,
        EditorRuntimeBridgeState state,
        CancellationToken cancellationToken)
    {
        IRunnerSessionSink? sink;
        lock (_sinkLock)
        {
            sink = _sink;
        }
        if (sink is null || string.IsNullOrWhiteSpace(editorSid) || lifecycleGeneration == 0)
        {
            return false;
        }

        await sink.ReportEditorRuntimeBridgeStateAsync(new ReportEditorRuntimeBridgeStateRequest
        {
            RunnerId = _runnerId,
            SessionId = sessionId,
            EditorSid = editorSid,
            ClientId = clientId,
            LifecycleGeneration = lifecycleGeneration,
            RuntimeEpoch = runtimeEpoch,
            RuntimeInstanceId = runtimeInstanceId,
            BridgeConnectionId = bridgeConnectionId,
            State = state,
            ObservedUtc = Timestamp.FromDateTime(DateTime.UtcNow)
        }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // Bridges are keyed by session, client and exact runtime, so a lookup made
    // for one runtime can never reach a bridge connected for another.
    private static string BuildKey(string sessionId, string clientId, RuntimeIdentity runtime)
        => string.Concat(
            BuildClientPrefix(sessionId, clientId),
            runtime.LifecycleGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture), "|",
            runtime.RuntimeEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture), "|",
            runtime.RuntimeInstanceId);

    private static string BuildClientPrefix(string sessionId, string clientId)
        => string.Concat(sessionId.Trim(), "|", clientId.Trim(), "|");

    private Uri BuildWsUri(string clientId)
    {
        var builder = new UriBuilder("ws", _comfyHost, _comfyPort, "/ws");
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            builder.Query = "clientId=" + Uri.EscapeDataString(clientId.Trim());
        }
        return builder.Uri;
    }

    private async Task SenderLoopAsync()
    {
        var buffer = new List<SessionStreamEvent>(MaxBatchSize);

        while (!_shutdown.IsCancellationRequested)
        {
            SessionStreamEvent evt;
            try
            {
                evt = await _queue.Reader.ReadAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            buffer.Add(evt);
            var flushDelay = Task.Delay(FlushInterval, _shutdown.Token);

            while (buffer.Count < MaxBatchSize)
            {
                if (_queue.Reader.TryRead(out var next))
                {
                    buffer.Add(next);
                    continue;
                }

                try
                {
                    await Task.WhenAny(flushDelay).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!flushDelay.IsCompleted)
                {
                    continue;
                }
                break;
            }

            await FlushBufferWithRetryAsync(buffer).ConfigureAwait(false);
            buffer.Clear();
        }

        // Drain remaining events best effort
        while (_queue.Reader.TryRead(out var remaining))
        {
            buffer.Add(remaining);
            if (buffer.Count >= MaxBatchSize)
            {
                await TryFlushBufferAsync(buffer).ConfigureAwait(false);
                buffer.Clear();
            }
        }
        if (buffer.Count > 0)
        {
            await TryFlushBufferAsync(buffer).ConfigureAwait(false);
        }
    }

    private async Task FlushBufferWithRetryAsync(List<SessionStreamEvent> events)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            var outcome = await TryFlushBufferAsync(events).ConfigureAwait(false);
            if (outcome == FlushBatchOutcome.Succeeded)
            {
                return;
            }

            if (outcome == FlushBatchOutcome.Drop)
            {
                await DropInvalidEventsAsync(events).ConfigureAwait(false);
                return;
            }

            try
            {
                await Task.Delay(FlushInterval, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task DropInvalidEventsAsync(List<SessionStreamEvent> events)
    {
        if (events.Count == 0 || _shutdown.IsCancellationRequested)
        {
            return;
        }

        if (events.Count == 1)
        {
            var evt = events[0];
            RunnerLog.Error<ComfySessionEventRelay>(
                $"Dropping permanently rejected session event session={evt.SessionId ?? "-"} client={evt.ClientId ?? "-"} type={evt.EventType ?? "-"}.");
            return;
        }

        var midpoint = events.Count / 2;
        var left = new List<SessionStreamEvent>(midpoint);
        left.AddRange(events.Take(midpoint));
        var right = new List<SessionStreamEvent>(events.Count - midpoint);
        right.AddRange(events.Skip(midpoint));
        await FlushBufferWithRetryAsync(left).ConfigureAwait(false);
        await FlushBufferWithRetryAsync(right).ConfigureAwait(false);
    }

    private async Task<FlushBatchOutcome> TryFlushBufferAsync(List<SessionStreamEvent> events)
    {
        if (events.Count == 0)
        {
            return FlushBatchOutcome.Succeeded;
        }

        if (_shutdown.IsCancellationRequested)
        {
            return FlushBatchOutcome.Retry;
        }

        IRunnerSessionSink? sink;
        lock (_sinkLock)
        {
            sink = _sink;
        }

        if (sink is null)
        {
            return FlushBatchOutcome.Retry;
        }

        var request = new ReportSessionEventsRequest();
        request.Events.AddRange(events);

        try
        {
            await sink.ReportSessionEventsAsync(request, _shutdown.Token).ConfigureAwait(false);
            return FlushBatchOutcome.Succeeded;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return FlushBatchOutcome.Retry;
        }
        catch (RpcException ex) when (IsPermanentBatchFailure(ex))
        {
            RunnerLog.Error<ComfySessionEventRelay>(
                ex,
                $"Dropping permanently rejected session events batch ({events.Count}): {ex.StatusCode} {ex.Status.Detail}");
            return FlushBatchOutcome.Drop;
        }
        catch (Exception ex)
        {
            RunnerLog.Error<ComfySessionEventRelay>(ex, $"Failed to report session events batch ({events.Count}): {ex.Message}");
            return FlushBatchOutcome.Retry;
        }
    }

    private static bool IsPermanentBatchFailure(RpcException ex)
        => ex.StatusCode is StatusCode.PermissionDenied
            or StatusCode.InvalidArgument
            or StatusCode.NotFound
            or StatusCode.FailedPrecondition;

    public async ValueTask DisposeAsync()
    {
        if (!_shutdown.IsCancellationRequested)
        {
            _shutdown.Cancel();
        }

        foreach (var kvp in _bridges.ToArray())
        {
            if (_bridges.TryRemove(kvp.Key, out var bridge))
            {
                await bridge.DisposeAsync().ConfigureAwait(false);
            }
        }

        _queue.Writer.TryComplete();

        try
        {
            await _senderTask.ConfigureAwait(false);
        }
        catch
        {
            // swallow on shutdown
        }

        _shutdown.Dispose();
    }

    internal static bool ShouldRefreshArtifactsForEventType(string? eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return false;
        }

        return eventType.Trim() switch
        {
            "executed" => true,
            "execution_cached" => true,
            "execution_success" => true,
            _ => false
        };
    }

    internal static bool ShouldSuppressSessionBridgeError(Exception exception, bool shutdownRequested)
    {
        if (!shutdownRequested)
        {
            return false;
        }

        return exception is WebSocketException webSocketException
            && webSocketException.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely;
    }

    internal static bool IsBridgeConnectionActivated(string? activatedConnectionId, string? currentConnectionId)
        => !string.IsNullOrWhiteSpace(currentConnectionId) &&
           string.Equals(activatedConnectionId, currentConnectionId, StringComparison.Ordinal);

    private void TriggerArtifactRefresh(string sessionId)
    {
        if (_captureSessionArtifactRefresh is null || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_lastArtifactRefreshUtc.TryGetValue(sessionId, out var lastRefreshUtc) &&
            now - lastRefreshUtc < ArtifactRefreshDebounceInterval)
        {
            return;
        }

        _lastArtifactRefreshUtc[sessionId] = now;
        Func<Task> refresh;
        try
        {
            refresh = _captureSessionArtifactRefresh(sessionId);
        }
        catch (Exception ex)
        {
            // Artifact refresh is ancillary to the live websocket frame and
            // its durable terminal evidence. Stop can remove the uploader first.
            RunnerLog.Warning(nameof(ComfySessionEventRelay),
                $"Skipping artifact refresh for session {sessionId}: {ex.GetType().Name}.");
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await refresh().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RunnerLog.Error<ComfySessionEventRelay>(ex, $"Failed to refresh artifacts for session {sessionId}: {ex.Message}");
                }
            },
            CancellationToken.None);
    }

    private sealed class SessionBridge : IAsyncDisposable
    {
        private readonly ComfySessionEventRelay _owner;
        private readonly string _runnerId;
        private readonly string _sessionId;
        private readonly string _editorSid;
        private readonly string _clientId;
        private readonly Uri _uri;
        private readonly ulong _lifecycleGeneration;
        private readonly ulong _runtimeEpoch;
        private readonly string _runtimeInstanceId;
        private string _bridgeConnectionId = string.Empty;
        private string _activatedBridgeConnectionId = string.Empty;
        private byte[] _clientMessage = [];
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<string, string> _promptMap = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, SubmitPromptCommand> _submissionCommands = new(StringComparer.Ordinal);
        private readonly Queue<KeyValuePair<string, SubmitPromptCommand>> _submissionOrder = new();
        private readonly object _submissionGate = new();
        private readonly SemaphoreSlim _startLock = new(1, 1);
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly object _socketGate = new();
        private ClientWebSocket? _activeSocket;
        private Task? _loopTask;
        private string? _latestSubmissionId;

        public SessionBridge(
            ComfySessionEventRelay owner,
            string runnerId,
            string sessionId,
            string editorSid,
            string clientId,
            ulong lifecycleGeneration,
            ulong runtimeEpoch,
            string runtimeInstanceId,
            Uri uri)
        {
            _owner = owner;
            _runnerId = runnerId;
            _sessionId = sessionId.Trim();
            _editorSid = editorSid?.Trim() ?? string.Empty;
            _clientId = clientId.Trim();
            _lifecycleGeneration = lifecycleGeneration;
            _runtimeEpoch = runtimeEpoch;
            _runtimeInstanceId = runtimeInstanceId?.Trim() ?? string.Empty;
            _uri = uri;
        }

        public void UpdateConnectionData(string editorSid, ulong lifecycleGeneration, ulong runtimeEpoch, string runtimeInstanceId, byte[] clientMessage)
        {
            if (editorSid != _editorSid || lifecycleGeneration != _lifecycleGeneration || runtimeEpoch != _runtimeEpoch
                || !Guid.TryParse(runtimeInstanceId, out var instance) || instance != Guid.Parse(_runtimeInstanceId))
                throw new InvalidOperationException("An existing bridge cannot be rebound to another runtime.");
            if (clientMessage is { Length: > 0 })
            {
                _clientMessage = clientMessage.ToArray();
            }
        }

        public async Task SendClientMessageAsync(byte[] payload, CancellationToken cancellationToken)
        {
            if (payload is not { Length: > 0 })
            {
                return;
            }
            _clientMessage = payload.ToArray();
            ClientWebSocket? socket;
            lock (_socketGate)
            {
                socket = _activeSocket;
            }
            if (socket is { State: WebSocketState.Open })
            {
                await SendPayloadAsync(socket, payload, cancellationToken).ConfigureAwait(false);
            }
        }

        public void RegisterSubmission(SubmitPromptCommand command)
        {
            _ = Offloadr.Common.V1.PromptRuntimeMarker.TargetFingerprint(command.Target);
            if (command.SessionId != _sessionId || command.EditorSid != _editorSid
                || command.Target.GpuGeneration != _lifecycleGeneration || command.Target.RuntimeEpoch != _runtimeEpoch
                || Guid.Parse(command.Target.RuntimeInstanceId) != Guid.Parse(_runtimeInstanceId))
                throw new InvalidOperationException("The prompt does not belong to this bridge.");
            _latestSubmissionId = command.SubmissionId;
            if (!string.IsNullOrWhiteSpace(command.NativeIdentifiers?.PromptId))
            {
                // This is a bounded live-evidence correlation cache, never an
                // execution/deduplication record. Missed terminal frames fall back
                // to durable history recovery; retain no prompt or download body.
                var identity = new SubmitPromptCommand
                {
                    CommandId = command.CommandId,
                    SubmissionId = command.SubmissionId,
                    SessionId = command.SessionId,
                    EditorSid = command.EditorSid,
                    Target = command.Target.Clone(),
                    NativeIdentifiers = command.NativeIdentifiers.Clone(),
                };
                var promptId = command.NativeIdentifiers.PromptId;
                lock (_submissionGate)
                {
                    if (_submissionCommands.TryAdd(promptId, identity))
                    {
                        _submissionOrder.Enqueue(new(promptId, identity));
                        _promptMap.TryAdd(promptId, command.SubmissionId);
                    }
                    while (_submissionOrder.Count > MaxRetainedPromptIdentities)
                    {
                        var oldest = _submissionOrder.Dequeue();
                        ((ICollection<KeyValuePair<string, SubmitPromptCommand>>)_submissionCommands).Remove(oldest);
                        ((ICollection<KeyValuePair<string, string>>)_promptMap).Remove(new(oldest.Key, oldest.Value.SubmissionId));
                    }
                }
            }
        }

        public void UnregisterUninvokedSubmission(SubmitPromptCommand command)
        {
            var promptId = command.NativeIdentifiers?.PromptId;
            if (string.IsNullOrEmpty(promptId)) return;
            if (_submissionCommands.TryGetValue(promptId, out var retained)
                && retained.CommandId == command.CommandId && retained.SubmissionId == command.SubmissionId
                && retained.Target.Equals(command.Target))
            {
                ((ICollection<KeyValuePair<string, SubmitPromptCommand>>)_submissionCommands)
                    .Remove(new(promptId, retained));
                ((ICollection<KeyValuePair<string, string>>)_promptMap)
                    .Remove(new(promptId, command.SubmissionId));
                if (_latestSubmissionId == command.SubmissionId) _latestSubmissionId = null;
            }
        }

        public void MapPrompt(string promptId, string submissionId)
        {
            if (string.IsNullOrWhiteSpace(promptId) || string.IsNullOrWhiteSpace(submissionId)) return;
            _promptMap[promptId.Trim()] = submissionId.Trim();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_loopTask is not null)
            {
                return;
            }

            await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_loopTask is null)
                {
                    _loopTask = Task.Run(() => RunAsync());
                }
            }
            finally
            {
                _startLock.Release();
            }
        }

        private async Task RunAsync()
        {
            var token = _cts.Token;
            var delay = TimeSpan.FromSeconds(1);

            while (!token.IsCancellationRequested)
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

                try
                {
                    await socket.ConnectAsync(_uri, token).ConfigureAwait(false);
                    _bridgeConnectionId = Guid.NewGuid().ToString("n");
                    RunnerLog.Info<SessionBridge>($"[{_sessionId}] Comfy websocket connected (client {_clientId})");
                    delay = TimeSpan.FromSeconds(1);
                    lock (_socketGate)
                    {
                        _activeSocket = socket;
                    }
                    if (_clientMessage.Length > 0)
                    {
                        await SendPayloadAsync(socket, _clientMessage, token).ConfigureAwait(false);
                    }
                    var bridgeConnectionId = Volatile.Read(ref _bridgeConnectionId);
                    if (await TryReportBridgeStateAsync(EditorRuntimeBridgeState.Active, token).ConfigureAwait(false))
                    {
                        Volatile.Write(ref _activatedBridgeConnectionId, bridgeConnectionId);
                    }
                    using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var heartbeatTask = RunHeartbeatAsync(heartbeatCts.Token);
                    try
                    {
                        await ReceiveLoopAsync(socket, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        await heartbeatCts.CancelAsync().ConfigureAwait(false);
                        try { await heartbeatTask.ConfigureAwait(false); } catch { }
                        lock (_socketGate)
                        {
                            if (ReferenceEquals(_activeSocket, socket))
                            {
                                _activeSocket = null;
                            }
                        }
                        await TryReportBridgeStateAsync(EditorRuntimeBridgeState.Inactive, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ShouldSuppressSessionBridgeError(ex, token.IsCancellationRequested))
                {
                    break;
                }
                catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                {
                    try
                    {
                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
                }
                catch (Exception ex)
                {
                    RunnerLog.Error<SessionBridge>(ex, $"[{_sessionId}] Websocket bridge error for client {_clientId}: {ex.Message}");
                    try
                    {
                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
                }
            }
        }

        private async Task SendPayloadAsync(ClientWebSocket socket, byte[] payload, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task RunHeartbeatAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    if (IsBridgeConnectionActivated(
                        Volatile.Read(ref _activatedBridgeConnectionId),
                        Volatile.Read(ref _bridgeConnectionId)))
                    {
                        await TryReportBridgeStateAsync(EditorRuntimeBridgeState.Heartbeat, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var bridgeConnectionId = Volatile.Read(ref _bridgeConnectionId);
                        if (await TryReportBridgeStateAsync(EditorRuntimeBridgeState.Active, cancellationToken).ConfigureAwait(false))
                        {
                            Volatile.Write(ref _activatedBridgeConnectionId, bridgeConnectionId);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        private async Task<bool> TryReportBridgeStateAsync(EditorRuntimeBridgeState state, CancellationToken cancellationToken)
        {
            try
            {
                return await _owner.ReportBridgeStateAsync(
                    _sessionId,
                    _editorSid,
                    _clientId,
                    _lifecycleGeneration,
                    _runtimeEpoch,
                    _runtimeInstanceId,
                    _bridgeConnectionId,
                    state,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RunnerLog.Error<SessionBridge>(ex, $"[{_sessionId}] Failed reporting bridge state {state}: {ex.Message}");
                return false;
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", token).ConfigureAwait(false);
                            return;
                        }
                        if (result.Count > 0)
                        {
                            ms.Write(buffer, 0, result.Count);
                        }
                    } while (!result.EndOfMessage);

                    var payload = ms.ToArray();
                    var frameType = result.MessageType switch
                    {
                        WebSocketMessageType.Text => SessionStreamFrameType.Text,
                        WebSocketMessageType.Binary => SessionStreamFrameType.Binary,
                        _ => SessionStreamFrameType.Unspecified
                    };

                    if (frameType == SessionStreamFrameType.Unspecified)
                    {
                        continue;
                    }

                    string? promptId = null;
                    string? eventType = null;
                    if (frameType == SessionStreamFrameType.Text)
                    {
                        (promptId, eventType) = ExtractMetadata(payload);
                        if (ShouldSuppressGpuServerFrame(eventType))
                        {
                            continue;
                        }
                    }

                    var evt = new SessionStreamEvent
                    {
                        RunnerId = _runnerId,
                        SessionId = _sessionId,
                        EditorSid = _editorSid,
                        ClientId = _clientId,
                        FrameType = frameType,
                        Payload = ByteString.CopyFrom(payload),
                        CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                        LifecycleGeneration = _lifecycleGeneration,
                        RuntimeEpoch = _runtimeEpoch,
                        RuntimeInstanceId = _runtimeInstanceId,
                    };

                    if (!string.IsNullOrWhiteSpace(eventType))
                    {
                        evt.EventType = eventType;
                        if (ShouldRefreshArtifactsForEventType(eventType))
                        {
                            _owner.TriggerArtifactRefresh(_sessionId);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(promptId))
                    {
                        evt.PromptId = promptId;
                        if (_promptMap.TryGetValue(promptId, out var submissionId) && !string.IsNullOrWhiteSpace(submissionId))
                        {
                            evt.SubmissionId = submissionId;
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(_latestSubmissionId))
                    {
                        evt.SubmissionId = _latestSubmissionId;
                    }

                    _owner.Enqueue(evt);
                    if (promptId is not null && _submissionCommands.TryGetValue(promptId, out var command)
                        && _owner.CapturePromptEvidence(command, evt) is { } evidence
                        && _submissionCommands.TryRemove(promptId, out _))
                    {
                        // Terminal evidence owns its immutable bytes and identity;
                        // releasing the full command does not depend on delivery.
                        _ = _owner.ReportRetainedPromptEvidenceAsync(evidence);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _cts.Cancel();
            }
            catch { }

            if (_loopTask is not null)
            {
                try
                {
                    await _loopTask.ConfigureAwait(false);
                }
                catch { }
            }

            _startLock.Dispose();
            _sendLock.Dispose();
            _cts.Dispose();
        }

        private static (string? PromptId, string? EventType) ExtractMetadata(byte[] payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                string? promptId = null;
                string? eventType = null;

                if (doc.RootElement.TryGetProperty("prompt_id", out var promptNode) && promptNode.ValueKind == JsonValueKind.String)
                {
                    promptId = promptNode.GetString();
                }

                if (doc.RootElement.TryGetProperty("type", out var typeNode) && typeNode.ValueKind == JsonValueKind.String)
                {
                    eventType = typeNode.GetString();
                }

                if (doc.RootElement.TryGetProperty("data", out var dataNode) && dataNode.ValueKind == JsonValueKind.Object)
                {
                    if (promptId is null && dataNode.TryGetProperty("prompt_id", out var nestedPromptNode) && nestedPromptNode.ValueKind == JsonValueKind.String)
                    {
                        promptId = nestedPromptNode.GetString();
                    }

                    if (eventType is null && dataNode.TryGetProperty("type", out var nestedTypeNode) && nestedTypeNode.ValueKind == JsonValueKind.String)
                    {
                        eventType = nestedTypeNode.GetString();
                    }
                }

                return (promptId, eventType);
            }
            catch (JsonException)
            {
                return (null, null);
            }
        }

    }
}
