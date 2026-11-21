using System.Net.WebSockets;
using Google.Protobuf.WellKnownTypes;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Linux;

internal sealed partial class ComfySessionEventRelay
{
    /// <summary>How often an open native socket repeats its <see cref="BridgeState"/>.</summary>
    internal static TimeSpan BridgeStateInterval { get; set; } = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RuntimeCheckInterval = TimeSpan.FromMilliseconds(100);

    private sealed partial class SessionBridge
    {
        public bool IsRetired => !RuntimeIsCurrent() || _loopTask is { IsCompleted: true };

        // One native connection. A reconnect makes a new one with a new id, so the
        // platform learns that per-client editor state must be sent again.
        private sealed class PhysicalConnection(Guid id, ClientWebSocket socket)
        {
            internal readonly Guid Id = id;
            internal readonly ClientWebSocket Socket = socket;
            internal bool Active;
            internal readonly TaskCompletionSource<bool> SendReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private bool RuntimeIsCurrent()
            => !_cts.IsCancellationRequested && _owner._isTrackedRuntime(_sessionId,
                new RuntimeIdentity(_lifecycleGeneration, _runtimeEpoch, _runtimeInstanceId));

        private async Task RunAsync()
        {
            var token = _cts.Token;
            var delay = TimeSpan.FromSeconds(1);
            while (RuntimeIsCurrent())
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                PhysicalConnection? connection = null;
                try
                {
                    var features = _clientMessage.ToArray();
                    // A runtime is bridged only once its data protection was applied.
                    _ = _owner._getSessionSealing(_sessionId)
                        ?? throw new InvalidOperationException("The runtime has no applied sealing profile.");
                    using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        connect.CancelAfter(ClientMessageConnectWait);
                        await socket.ConnectAsync(_uri, connect.Token).ConfigureAwait(false);
                    }
                    if (!RuntimeIsCurrent()) throw new InvalidOperationException("The bridge runtime retired during connect.");
                    connection = new(Guid.NewGuid(), socket);
                    await _sendLock.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        lock (_socketGate) _connection = connection;
                        connection.Active = true;
                    }
                    finally { _sendLock.Release(); }
                    // The connection's handshake goes first, so a client message
                    // cannot reach the editor before the client is identified.
                    if (features.Length > 0) await SendPayloadAsync(connection, features, token).ConfigureAwait(false);
                    connection.SendReady.TrySetResult(true);
                    delay = TimeSpan.FromSeconds(1);
                    ReportBridgeState(connection);
                    using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var guardTask = GuardConnectionAsync(connection, guard.Token);
                    try { await ReceiveLoopAsync(socket, token).ConfigureAwait(false); }
                    finally
                    {
                        await guard.CancelAsync().ConfigureAwait(false);
                        try { await guardTask.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (guard.IsCancellationRequested) { }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) when (ShouldSuppressSessionBridgeError(ex, token.IsCancellationRequested)) { break; }
                catch (Exception ex)
                {
                    RunnerLog.Warning(nameof(SessionBridge), $"Bridge connection ended ({ex.GetType().Name}).");
                }
                finally
                {
                    await _sendLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (connection is not null)
                        {
                            connection.Active = false;
                            connection.SendReady.TrySetResult(false);
                        }
                        lock (_socketGate) if (ReferenceEquals(_connection, connection)) _connection = null;
                        socket.Abort();
                    }
                    finally { _sendLock.Release(); }
                }
                try { await Task.Delay(delay, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
            }
        }

        // Aborts the socket once its runtime retires, and repeats its bridge state
        // so a missed report or a listener that attached later still learns of it.
        private async Task GuardConnectionAsync(PhysicalConnection connection, CancellationToken token)
        {
            var nextReport = DateTime.UtcNow + BridgeStateInterval;
            while (!token.IsCancellationRequested)
            {
                if (!RuntimeIsCurrent())
                {
                    connection.Socket.Abort();
                    return;
                }
                if (DateTime.UtcNow >= nextReport)
                {
                    ReportBridgeState(connection);
                    nextReport = DateTime.UtcNow + BridgeStateInterval;
                }
                await Task.Delay(RuntimeCheckInterval, token).ConfigureAwait(false);
            }
        }

        private void ReportBridgeState(PhysicalConnection connection)
            => _owner.Enqueue(new SessionStreamEvent
            {
                RunnerId = _runnerId,
                SessionId = _sessionId,
                EditorSid = _editorSid,
                ClientId = _clientId,
                CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                LifecycleGeneration = _lifecycleGeneration,
                RuntimeEpoch = _runtimeEpoch,
                RuntimeInstanceId = _runtimeInstanceId,
                BridgeState = new BridgeState { ConnectionId = connection.Id.ToString("n") },
            });

        private async Task SendPayloadAsync(PhysicalConnection connection, byte[] payload, CancellationToken token)
        {
            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                lock (_socketGate)
                    if (!ReferenceEquals(_connection, connection)) throw new InvalidOperationException("The native connection has closed.");
                if (!connection.Active || !RuntimeIsCurrent() || connection.Socket.State != WebSocketState.Open)
                    throw new InvalidOperationException("The native connection cannot send.");
                using var send = CancellationTokenSource.CreateLinkedTokenSource(token, _cts.Token);
                send.CancelAfter(ClientMessageConnectWait);
                await connection.Socket.SendAsync(payload, WebSocketMessageType.Text, true, send.Token).ConfigureAwait(false);
            }
            finally { _sendLock.Release(); }
        }
    }
}
