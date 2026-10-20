using System.Diagnostics;
using System.Net.WebSockets;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Offloadr.EditorRuntime.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Linux;

internal sealed partial class ComfySessionEventRelay
{
    private static readonly TimeSpan BridgeRpcTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReservationTimeout = TimeSpan.FromSeconds(30);

    internal static TimeSpan RemainingBridgeBudget(EditorRuntimeBridgeResult? result, TimeSpan rpcDuration)
    {
        if (result?.ServerNowUtc is null || result.Lease?.ExpiresUtc is null || rpcDuration < TimeSpan.Zero)
            return TimeSpan.Zero;
        try
        {
            var lifetime = result.Lease.ExpiresUtc.ToDateTime() - result.ServerNowUtc.ToDateTime();
            if (lifetime <= TimeSpan.Zero || lifetime > ReservationTimeout) return TimeSpan.Zero;
            return lifetime > rpcDuration ? lifetime - rpcDuration : TimeSpan.Zero;
        }
        catch (InvalidOperationException) { return TimeSpan.Zero; }
    }

    private sealed partial class SessionBridge
    {
        private sealed class BridgeRetiredException(string message) : Exception(message)
        {
        }

        public bool IsRetired => !RuntimeIsCurrent() || _loopTask is { IsCompleted: true };

        private sealed class PhysicalConnection(Guid id, EditorRuntimeBridgeTarget target, SessionSealingStatus profile, ClientWebSocket socket)
        {
            internal readonly Guid Id = id;
            internal readonly EditorRuntimeBridgeTarget Target = target;
            internal readonly SessionSealingStatus Profile = profile;
            internal readonly ClientWebSocket Socket = socket;
            internal ulong Revision;
            internal ulong Sequence;
            internal long Deadline;
            internal bool Active;
            internal readonly TaskCompletionSource<bool> SendReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private bool RuntimeIsCurrent()
            => !_cts.IsCancellationRequested && _owner._isTrackedRuntime(_sessionId,
                new RuntimeIdentity(_lifecycleGeneration, _runtimeEpoch, _runtimeInstanceId));

        private static bool Accepted(EditorRuntimeBridgeResult? result)
            => result?.Outcome is EditorRuntimeBridgeOutcome.Accepted or EditorRuntimeBridgeOutcome.Duplicate;

        private bool Matches(PhysicalConnection connection, EditorRuntimeBridgeResult result)
            => result.Lease is { Target: { } target } lease && target.Equals(connection.Target)
                && lease.ClientId == _clientId && Guid.TryParse(lease.BridgeConnectionId, out var id) && id == connection.Id
                && lease.LeaseRevision == connection.Revision && lease.ReportSequence == connection.Sequence;

        private async Task<T> CallAsync<T>(Func<IRunnerSessionSink, CancellationToken, Task<T>> operation, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(BridgeRpcTimeout);
            return await operation(_owner.BridgeSink(), timeout.Token).ConfigureAwait(false);
        }

        private static bool Transient(Exception exception, CancellationToken token)
            => !token.IsCancellationRequested && (exception is OperationCanceledException
                || exception is RpcException { StatusCode: StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Internal });

        private async Task ReserveAsync(PhysicalConnection connection, CancellationToken token)
        {
            var cursor = await CallAsync((sink, ct) => sink.ReadEditorRuntimeBridgeCursorAsync(
                new() { Target = connection.Target.Clone() }, ct), token).ConfigureAwait(false);
            if (cursor.Result?.Outcome != EditorRuntimeBridgeOutcome.Accepted || cursor.Result.ActivationFence >= long.MaxValue)
                throw new BridgeRetiredException("The runtime cannot reserve another bridge connection.");
            var request = new ReserveEditorRuntimeBridgeRequest
            {
                Target = connection.Target.Clone(),
                ClientId = _clientId,
                BridgeConnectionId = connection.Id.ToString("n"),
                ExpectedActivationFence = cursor.Result.ActivationFence,
            };
            connection.Revision = request.ExpectedActivationFence + 1;
            var attemptStarted = Stopwatch.GetTimestamp();
            while (RuntimeIsCurrent() && Stopwatch.GetElapsedTime(attemptStarted) < ReservationTimeout)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var response = await CallAsync((sink, ct) => sink.ReserveEditorRuntimeBridgeAsync(request.Clone(), ct), token).ConfigureAwait(false);
                    var observed = Stopwatch.GetTimestamp();
                    var remaining = RemainingBridgeBudget(response.Result, Stopwatch.GetElapsedTime(started, observed));
                    if (!Accepted(response.Result))
                    {
                        // A shared-cursor race loses this physical attempt, not
                        // the current runtime's permission to make a fresh claim.
                        if (response.Result?.Outcome is EditorRuntimeBridgeOutcome.Stale)
                            throw new InvalidOperationException("The physical bridge reservation lost its claim.");
                        throw new BridgeRetiredException("The physical bridge reservation was rejected.");
                    }
                    if (!Matches(connection, response.Result))
                        throw new BridgeRetiredException("The physical bridge reservation has incompatible authority.");
                    if (remaining <= TimeSpan.Zero)
                        throw new InvalidOperationException("The physical bridge reservation expired.");
                    Interlocked.Exchange(ref connection.Deadline, observed + (long)(remaining.TotalSeconds * Stopwatch.Frequency));
                    return;
                }
                catch (Exception ex) when (Transient(ex, token))
                {
                    // A lost response retries exactly this UUID and fence, never
                    // a fresh claim that could preempt an intervening connection.
                    await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
                }
            }
            throw new InvalidOperationException("The physical bridge reservation timed out or retired.");
        }

        private async Task<bool> ReportAsync(PhysicalConnection connection, EditorRuntimeBridgeState state, CancellationToken token, bool retry = true)
        {
            if (connection.Sequence >= long.MaxValue) return false;
            var sequence = ++connection.Sequence;
            var request = new ReportEditorRuntimeBridgeStateRequest
            {
                RunnerId = _runnerId,
                SessionId = _sessionId,
                EditorSid = _editorSid,
                ClientId = _clientId,
                LifecycleGeneration = _lifecycleGeneration,
                RuntimeEpoch = _runtimeEpoch,
                RuntimeInstanceId = _runtimeInstanceId,
                BridgeConnectionId = connection.Id.ToString("n"),
                InstanceId = _owner._instanceId,
                LeaseRevision = connection.Revision,
                ReportSequence = sequence,
                State = state,
                Sealing = connection.Profile.Clone(),
            };
            do
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var response = await CallAsync((sink, ct) => sink.ReportEditorRuntimeBridgeStateAsync(request.Clone(), ct), token).ConfigureAwait(false);
                    var observed = Stopwatch.GetTimestamp();
                    var remaining = RemainingBridgeBudget(response.Result, Stopwatch.GetElapsedTime(started, observed));
                    // Lock contention consumes this absolute budget; it cannot
                    // turn the received duration into a later lease deadline.
                    var deadline = observed + (long)(remaining.TotalSeconds * Stopwatch.Frequency);
                    await _sendLock.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        connection.Active = false;
                        if (!Accepted(response.Result))
                        {
                            if (response.Result?.Outcome is EditorRuntimeBridgeOutcome.Stale) return false;
                            throw new BridgeRetiredException("The physical bridge observation was rejected.");
                        }
                        if (!Matches(connection, response.Result))
                            throw new BridgeRetiredException("The physical bridge observation has incompatible authority.");
                        if (state == EditorRuntimeBridgeState.Inactive) return true;
                        if (!RuntimeIsCurrent() || remaining <= TimeSpan.Zero || Stopwatch.GetTimestamp() >= deadline
                            || connection.Socket.State != WebSocketState.Open || response.Result.Lease.FirstActiveUtc is null) return false;
                        Interlocked.Exchange(ref connection.Deadline, deadline);
                        connection.Active = true;
                        return true;
                    }
                    finally { _sendLock.Release(); }
                }
                catch (Exception ex) when (Transient(ex, token))
                {
                    // A transport failure neither revokes nor renews the last
                    // accepted lease. Send and expiry guards retain its deadline.
                    if (!retry) return false;
                    await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
                }
            } while (RuntimeIsCurrent() && Stopwatch.GetTimestamp() < connection.Deadline);
            return false;
        }

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
                    var profile = _owner._getSessionSealing(_sessionId)?.Clone()
                        ?? throw new InvalidOperationException("The runtime has no applied sealing profile.");
                    connection = new(Guid.NewGuid(), new()
                    {
                        RunnerId = _runnerId,
                        SessionId = _sessionId,
                        EditorSid = _editorSid,
                        LifecycleGeneration = _lifecycleGeneration,
                        RuntimeEpoch = _runtimeEpoch,
                        RuntimeInstanceId = _runtimeInstanceId,
                        InstanceId = _owner._instanceId,
                    }, profile, socket);
                    await ReserveAsync(connection, token).ConfigureAwait(false);
                    if (!RuntimeIsCurrent()) throw new InvalidOperationException("The bridge runtime retired before connect.");
                    using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        var remaining = TimeSpan.FromSeconds((connection.Deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                        if (remaining <= TimeSpan.Zero) throw new InvalidOperationException("The bridge reservation expired before connect.");
                        connect.CancelAfter(remaining < ClientMessageConnectWait ? remaining : ClientMessageConnectWait);
                        await socket.ConnectAsync(_uri, connect.Token).ConfigureAwait(false);
                    }
                    lock (_socketGate) _connection = connection;
                    if (!await ReportAsync(connection, EditorRuntimeBridgeState.Active, token).ConfigureAwait(false))
                        throw new InvalidOperationException("The bridge activation was not accepted.");
                    delay = TimeSpan.FromSeconds(1);
                    if (features.Length > 0) await SendPayloadAsync(connection, features, token).ConfigureAwait(false);
                    connection.SendReady.TrySetResult(true);
                    using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var heartbeatTask = RunHeartbeatAsync(connection, heartbeat.Token);
                    var expiryTask = GuardExpiryAsync(connection, heartbeat.Token);
                    try { await ReceiveLoopAsync(socket, token).ConfigureAwait(false); }
                    finally
                    {
                        await heartbeat.CancelAsync().ConfigureAwait(false);
                        try { await Task.WhenAll(heartbeatTask, expiryTask).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (heartbeat.IsCancellationRequested) { }
                    }
                }
                catch (BridgeRetiredException)
                {
                    RunnerLog.Warning(nameof(SessionBridge), "Bridge connection authority retired.");
                    break;
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
                    if (connection is { Revision: > 0 })
                    {
                        try { await ReportAsync(connection, EditorRuntimeBridgeState.Inactive, CancellationToken.None, retry: false).ConfigureAwait(false); }
                        catch (Exception ex) { RunnerLog.Warning(nameof(SessionBridge), $"Bridge retirement report failed ({ex.GetType().Name})."); }
                    }
                }
                try { await Task.Delay(delay, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
            }
        }

        private async Task RunHeartbeatAsync(PhysicalConnection connection, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                    if (!await ReportAsync(connection, EditorRuntimeBridgeState.Heartbeat, token).ConfigureAwait(false))
                    {
                        connection.Socket.Abort();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { connection.Socket.Abort(); throw; }
        }

        private async Task GuardExpiryAsync(PhysicalConnection connection, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (!RuntimeIsCurrent() || Stopwatch.GetTimestamp() >= Interlocked.Read(ref connection.Deadline))
                {
                    connection.Socket.Abort();
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
            }
        }

        private async Task SendPayloadAsync(PhysicalConnection connection, byte[] payload, CancellationToken token)
        {
            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                lock (_socketGate)
                    if (!ReferenceEquals(_connection, connection)) throw new InvalidOperationException("The physical bridge has retired.");
                if (!connection.Active || !RuntimeIsCurrent() || Stopwatch.GetTimestamp() >= connection.Deadline
                    || connection.Socket.State != WebSocketState.Open)
                    throw new InvalidOperationException("The physical bridge is not authorized to send.");
                using var send = CancellationTokenSource.CreateLinkedTokenSource(token, _cts.Token);
                var remaining = TimeSpan.FromSeconds((connection.Deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                if (remaining <= TimeSpan.Zero) throw new InvalidOperationException("The bridge expired before send.");
                send.CancelAfter(remaining < ClientMessageConnectWait ? remaining : ClientMessageConnectWait);
                await connection.Socket.SendAsync(payload, WebSocketMessageType.Text, true, send.Token).ConfigureAwait(false);
            }
            finally { _sendLock.Release(); }
        }
    }
}
