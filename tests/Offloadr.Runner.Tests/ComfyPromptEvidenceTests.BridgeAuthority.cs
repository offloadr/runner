using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Offloadr.EditorRuntime.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public sealed partial class ComfyPromptEvidenceTests
{
    // A controlled protocol peer for production-loop tests. Database ordering
    // belongs to the service's real-persistence tests, not this fixture.
    private sealed class BridgeAuthority
    {
        private readonly object gate = new();
        private ulong fence;
        private readonly Dictionary<string, EditorRuntimeBridgeResult> reservations = [];
        private readonly Dictionary<string, EditorRuntimeBridgeResult> observations = [];
        private readonly Dictionary<string, Timestamp> firstActive = [];
        internal readonly ConcurrentQueue<ReserveEditorRuntimeBridgeRequest> ReserveCalls = new();
        internal readonly ConcurrentQueue<ReportEditorRuntimeBridgeStateRequest> ReportCalls = new();
        internal readonly Channel<EditorRuntimeBridgeLease> Activations = Channel.CreateUnbounded<EditorRuntimeBridgeLease>();
        internal Func<ReserveEditorRuntimeBridgeRequest, CancellationToken, Task>? BeforeReservationResponse;
        internal Func<ReportEditorRuntimeBridgeStateRequest, CancellationToken, Task>? BeforeReportResponse;
        internal EditorRuntimeBridgeOutcome ActiveOutcome = EditorRuntimeBridgeOutcome.Accepted;
        internal Func<EditorRuntimeBridgeResult, EditorRuntimeBridgeResult>? TransformReportResult;
        internal Func<EditorRuntimeBridgeResult, EditorRuntimeBridgeResult>? TransformReservationResult;

        internal Task<ReadEditorRuntimeBridgeCursorResponse> CursorAsync(ReadEditorRuntimeBridgeCursorRequest request, CancellationToken token)
        {
            lock (gate)
                return Task.FromResult(new ReadEditorRuntimeBridgeCursorResponse
                {
                    Result = new() { Outcome = EditorRuntimeBridgeOutcome.Accepted, ActivationFence = fence, ServerNowUtc = Timestamp.FromDateTime(DateTime.UtcNow) },
                });
        }

        internal async Task<ReserveEditorRuntimeBridgeResponse> ReserveAsync(ReserveEditorRuntimeBridgeRequest request, CancellationToken token)
        {
            ReserveCalls.Enqueue(request.Clone());
            EditorRuntimeBridgeResult result;
            lock (gate)
            {
                var now = DateTime.UtcNow;
                if (reservations.TryGetValue(request.BridgeConnectionId, out var retained))
                {
                    result = retained.Clone();
                    result.Outcome = EditorRuntimeBridgeOutcome.Duplicate;
                    result.ServerNowUtc = Timestamp.FromDateTime(now);
                }
                else
                {
                    if (request.ExpectedActivationFence != fence)
                        return new() { Result = new() { Outcome = EditorRuntimeBridgeOutcome.Stale, ServerNowUtc = Timestamp.FromDateTime(now) } };
                    result = new()
                    {
                        Outcome = EditorRuntimeBridgeOutcome.Accepted,
                        ActivationFence = ++fence,
                        ServerNowUtc = Timestamp.FromDateTime(now),
                        Lease = new()
                        {
                            Target = request.Target.Clone(),
                            ClientId = request.ClientId,
                            BridgeConnectionId = request.BridgeConnectionId,
                            LeaseRevision = fence,
                            ReservedUtc = Timestamp.FromDateTime(now),
                            ExpiresUtc = Timestamp.FromDateTime(now.AddSeconds(30)),
                        },
                    };
                    reservations.Add(request.BridgeConnectionId, result.Clone());
                }
            }
            if (BeforeReservationResponse is { } before) await before(request, token);
            return new() { Result = TransformReservationResult?.Invoke(result) ?? result };
        }

        internal async Task<ReportEditorRuntimeBridgeStateResponse> ReportAsync(ReportEditorRuntimeBridgeStateRequest request, CancellationToken token)
        {
            ReportCalls.Enqueue(request.Clone());
            EditorRuntimeBridgeResult result;
            lock (gate)
            {
                var key = request.BridgeConnectionId + ":" + request.ReportSequence;
                var now = DateTime.UtcNow;
                if (observations.TryGetValue(key, out var retained))
                {
                    result = retained.Clone(); result.Outcome = EditorRuntimeBridgeOutcome.Duplicate;
                    result.ServerNowUtc = Timestamp.FromDateTime(now);
                }
                else
                {
                    result = reservations[request.BridgeConnectionId].Clone();
                    result.Outcome = request.State == EditorRuntimeBridgeState.Active ? ActiveOutcome : EditorRuntimeBridgeOutcome.Accepted;
                    result.ServerNowUtc = Timestamp.FromDateTime(now);
                    result.Lease.ReportSequence = request.ReportSequence;
                    result.Lease.LastObservedUtc = Timestamp.FromDateTime(now);
                    result.Lease.ExpiresUtc = Timestamp.FromDateTime(now.AddSeconds(30));
                    if (request.State == EditorRuntimeBridgeState.Active) result.Lease.FirstActiveUtc = Timestamp.FromDateTime(now);
                    else if (firstActive.TryGetValue(request.BridgeConnectionId, out var activated)) result.Lease.FirstActiveUtc = activated.Clone();
                    observations.Add(key, result.Clone());
                    if (request.State == EditorRuntimeBridgeState.Active && result.Outcome == EditorRuntimeBridgeOutcome.Accepted)
                    {
                        firstActive[request.BridgeConnectionId] = result.Lease.FirstActiveUtc.Clone();
                        Activations.Writer.TryWrite(result.Lease.Clone());
                    }
                }
            }
            if (BeforeReportResponse is { } before) await before(request, token);
            return new() { Result = TransformReportResult?.Invoke(result) ?? result };
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task PhysicalBridgeReservesBeforeConnectAndSendsCapturedFeaturesOnlyAfterAcceptedActive(bool loseReservation, bool loseActive)
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start(); var port = ((IPEndPoint)portReservation.LocalEndpoint).Port; portReservation.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var command = Command();
        var reserveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReserve = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowActive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new EvidenceSink((_, _) => Task.CompletedTask);
        var reservationResponses = 0; var activeResponses = 0;
        sink.Authority.BeforeReservationResponse = async (_, ct) =>
        {
            reserveEntered.TrySetResult(); await allowReserve.Task.WaitAsync(ct);
            if (Interlocked.Increment(ref reservationResponses) == 1 && loseReservation)
                throw new RpcException(new(StatusCode.Unavailable, "Reservation response lost after commit."));
        };
        sink.Authority.BeforeReportResponse = async (request, ct) =>
        {
            if (request.State != EditorRuntimeBridgeState.Active) return;
            activeEntered.TrySetResult(); await allowActive.Task.WaitAsync(ct);
            if (Interlocked.Increment(ref activeResponses) == 1 && loseActive)
                throw new RpcException(new(StatusCode.Unavailable, "Active response lost after commit."));
        };
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, command.Target.RunnerId, "127.0.0.1", port,
            "test-process", _ => new SessionSealingStatus(), (_, _) => true);
        relay.AttachSink(sink);
        var nativeConnect = listener.GetContextAsync();
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
            command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, "original features"u8.ToArray(), CancellationToken.None);
        await reserveEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(100);
        Assert.That(nativeConnect.IsCompleted, Is.False, "Native connect preceded the reservation response.");
        allowReserve.TrySetResult();
        var context = await nativeConnect.WaitAsync(TimeSpan.FromSeconds(3));
        using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        await activeEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var buffer = new byte[1024];
        var receive = socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        // Updating the retained handshake during this attempt cannot alter the
        // bytes captured before its physical connection was reserved.
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
            command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, "replacement features"u8.ToArray(), CancellationToken.None);
        await Task.Delay(100);
        Assert.That(receive.IsCompleted, Is.False, "Feature bytes preceded the accepted Active response.");
        allowActive.TrySetResult();
        var received = await receive.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(System.Text.Encoding.UTF8.GetString(buffer, 0, received.Count), Is.EqualTo("original features"));
        Assert.That(sink.Authority.ReserveCalls.Count, Is.EqualTo(loseReservation ? 2 : 1));
        Assert.That(sink.Authority.ReserveCalls.Distinct().ToArray(), Has.Length.EqualTo(1), "Reservation retry changed the captured request.");
        var active = sink.Authority.ReportCalls.Where(call => call.State == EditorRuntimeBridgeState.Active).ToArray();
        Assert.That(active, Has.Length.EqualTo(loseActive ? 2 : 1));
        Assert.That(active.Distinct().ToArray(), Has.Length.EqualTo(1), "Active retry changed UUID, sequence, profile or runtime.");
        await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(BridgeCount(relay), Is.Zero);
        var inactive = sink.Authority.ReportCalls.Last();
        Assert.That(inactive.State, Is.EqualTo(EditorRuntimeBridgeState.Inactive));
        Assert.That(inactive.BridgeConnectionId, Is.EqualTo(active[0].BridgeConnectionId));
        Assert.That(inactive.ReportSequence, Is.EqualTo(active[0].ReportSequence + 1));
    }

    [TestCase(false, "accept")]
    [TestCase(true, "accept")]
    [TestCase(false, "cancel")]
    [TestCase(false, "stop")]
    [TestCase(false, "reject")]
    [TestCase(false, "timeout")]
    public async Task ClientMessageWaitsForItsCapturedActivationWithoutRetryOrRebinding(bool hyphenatedSession, string outcome)
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start(); var port = ((IPEndPoint)portReservation.LocalEndpoint).Port; portReservation.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var command = Command();
        if (hyphenatedSession)
        {
            command.SessionId = Guid.Parse(command.SessionId).ToString("D");
            command.Target.RunnerSessionId = command.SessionId;
        }
        var tracked = new Dictionary<string, RuntimeIdentity>(StringComparer.Ordinal)
        {
            [command.SessionId] = new(command.Target.GpuGeneration, command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId),
        };
        var sealings = new Dictionary<string, SessionSealingStatus>(StringComparer.Ordinal) { [command.SessionId] = new() };
        var allowActive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new EvidenceSink((_, _) => Task.CompletedTask);
        sink.Authority.BeforeReportResponse = async (request, ct) =>
        {
            if (request.State == EditorRuntimeBridgeState.Active) await allowActive.Task.WaitAsync(ct);
        };
        if (outcome == "reject")
            sink.Authority.TransformReportResult = result => { result.Outcome = EditorRuntimeBridgeOutcome.Preempted; return result; };
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, command.Target.RunnerId, "127.0.0.1", port,
            "test-process", session => sealings.GetValueOrDefault(session),
            (session, runtime) => tracked.TryGetValue(session, out var current) && current.SameRuntime(runtime));
        relay.AttachSink(sink);
        using var cancelSend = new CancellationTokenSource();
        WebSocket? socket = null;
        Task? send = null;
        Task<WebSocketReceiveResult>? receive = null;
        try
        {
            await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
                command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, "initial features"u8.ToArray(), CancellationToken.None);
            var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(3));
            socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            // The protocol peer has committed Active but deliberately holds its response.
            // Production may deliver a command carrying this exact authority already.
            var accepted = await sink.Authority.Activations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            var buffer = new byte[1024];
            receive = socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            send = relay.SendClientMessageAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
                command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, "one client message"u8.ToArray(),
                accepted.BridgeConnectionId, accepted.LeaseRevision, cancelSend.Token);
            await Task.Delay(100);
            Assert.That(send.IsCompleted, Is.False, "A matching command must wait for its captured activation, without caller retry.");
            Assert.That(receive.IsCompleted, Is.False, "Native bytes must wait for the accepted Active response.");
            if (outcome == "accept")
            {
                allowActive.TrySetResult();
                await send.WaitAsync(TimeSpan.FromSeconds(3));
                var first = await receive.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.That(System.Text.Encoding.UTF8.GetString(buffer, 0, first.Count), Is.EqualTo("initial features"));
                var second = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.That(System.Text.Encoding.UTF8.GetString(buffer, 0, second.Count), Is.EqualTo("one client message"));
                Assert.That(sink.Authority.ReserveCalls.Single().Target.SessionId, Is.EqualTo(command.SessionId));
            }
            else if (outcome == "cancel")
            {
                cancelSend.Cancel();
                Assert.That(async () => await send.WaitAsync(TimeSpan.FromSeconds(3)), Throws.InstanceOf<OperationCanceledException>());
            }
            else if (outcome == "timeout")
            {
                Assert.That(async () => await send.WaitAsync(TimeSpan.FromSeconds(6)), Throws.InstanceOf<OperationCanceledException>());
            }
            else if (outcome == "stop")
            {
                await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.That(async () => await send.WaitAsync(TimeSpan.FromSeconds(3)),
                    Throws.InstanceOf<OperationCanceledException>().Or.InstanceOf<InvalidOperationException>());
            }
            else
            {
                allowActive.TrySetResult();
                Assert.That(async () => await send.WaitAsync(TimeSpan.FromSeconds(3)), Throws.InstanceOf<InvalidOperationException>());
            }
        }
        finally
        {
            allowActive.TrySetResult();
            await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
            socket?.Abort(); socket?.Dispose();
            if (send is not null)
            {
                try { await send.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { }
            }
            if (receive is not null)
            {
                try { await receive.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
            }
        }
        Assert.That(BridgeCount(relay), Is.Zero);
    }

    [Test]
    public async Task ClientIdentifierContainingSeparatorDoesNotRetireAnotherClientsBridge()
    {
        var command = Command();
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, command.Target.RunnerId, "127.0.0.1", 1,
            "test-process", _ => new SessionSealingStatus(), (_, _) => true);
        foreach (var client in new[] { "client", "client|nested" })
            await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, client, command.Target.GpuGeneration,
                command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, [], CancellationToken.None);
        Assert.That(BridgeCount(relay), Is.EqualTo(2));
        await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestCase("rejected")]
    [TestCase("connection")]
    [TestCase("revision")]
    [TestCase("process")]
    public async Task RejectedOrMismatchedActivationNeverSendsFeatures(string mismatch)
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start(); var port = ((IPEndPoint)portReservation.LocalEndpoint).Port; portReservation.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var command = Command();
        var sink = new EvidenceSink((_, _) => Task.CompletedTask);
        sink.Authority.TransformReportResult = result =>
        {
            if (mismatch == "rejected") result.Outcome = EditorRuntimeBridgeOutcome.Preempted;
            if (mismatch == "connection") result.Lease.BridgeConnectionId = Guid.NewGuid().ToString("n");
            if (mismatch == "revision") result.Lease.LeaseRevision++;
            if (mismatch == "process") result.Lease.Target.InstanceId = "retired-process";
            return result;
        };
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, command.Target.RunnerId, "127.0.0.1", port,
            "test-process", _ => new SessionSealingStatus(), (_, _) => true);
        relay.AttachSink(sink);
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
            command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, "forbidden features"u8.ToArray(), CancellationToken.None);
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        var buffer = new byte[1024];
        try
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Close), "Rejected authority emitted native payload bytes.");
        }
        catch (WebSocketException) { /* Abrupt retirement is also a definite no-payload result at this peer. */ }
        await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(BridgeCount(relay), Is.Zero);
    }

    [Test]
    public async Task ConcurrentAdmissionCountsConnectingSlotsAndStopJoinsEveryOwnedBridge()
    {
        var command = Command();
        var sink = new EvidenceSink((_, _) => Task.CompletedTask);
        sink.Authority.BeforeReservationResponse = async (_, token) => await Task.Delay(Timeout.InfiniteTimeSpan, token);
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, command.Target.RunnerId, "127.0.0.1", 1,
            "test-process", _ => new SessionSealingStatus(), (_, _) => true);
        relay.AttachSink(sink);
        var accepted = await Task.WhenAll(Enumerable.Range(0, 64).Select(async index =>
        {
            try
            {
                await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client|" + index,
                    command.Target.GpuGeneration, command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, [], CancellationToken.None);
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }));
        Assert.That(accepted.Count(value => value), Is.EqualTo(32));
        Assert.That(BridgeCount(relay), Is.EqualTo(32));
        await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(BridgeCount(relay), Is.Zero);
    }

    [TestCase(30, 2, 28)]
    [TestCase(3, 4, 0)]
    [TestCase(0, 0, 0)]
    [TestCase(31, 0, 0)]
    [TestCase(30, -1, 0)]
    public void BridgeBudgetSubtractsWholeRpcDurationWithoutComparingHostUtc(int lifetime, int elapsed, int expected)
    {
        var server = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = new EditorRuntimeBridgeResult
        {
            ServerNowUtc = Timestamp.FromDateTime(server),
            Lease = new() { ExpiresUtc = Timestamp.FromDateTime(server.AddSeconds(lifetime)) },
        };
        Assert.That(ComfySessionEventRelay.RemainingBridgeBudget(result, TimeSpan.FromSeconds(elapsed)), Is.EqualTo(TimeSpan.FromSeconds(expected)));
    }
}
