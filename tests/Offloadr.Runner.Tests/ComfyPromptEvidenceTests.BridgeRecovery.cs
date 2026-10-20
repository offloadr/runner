using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Offloadr.EditorRuntime.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public sealed partial class ComfyPromptEvidenceTests
{
    [Test]
    public async Task WaitingForSendLockCannotExtendAnAcceptedBridgeDeadline()
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start(); var port = ((IPEndPoint)portReservation.LocalEndpoint).Port; portReservation.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var command = Command();
        var activeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowActive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new EvidenceSink((_, _) => Task.CompletedTask);
        sink.Authority.BeforeReportResponse = async (request, ct) =>
        {
            if (request.State != EditorRuntimeBridgeState.Active) return;
            activeEntered.TrySetResult();
            await allowActive.Task.WaitAsync(ct);
        };
        sink.Authority.TransformReportResult = result =>
        {
            var now = DateTime.UtcNow;
            result.ServerNowUtc = Timestamp.FromDateTime(now);
            result.Lease.ExpiresUtc = Timestamp.FromDateTime(now.AddMilliseconds(600));
            responseReady.TrySetResult();
            return result;
        };
        await using var relay = new ComfySessionEventRelay(command.Target.RunnerId, "127.0.0.1", port,
            "test-process", _ => new SessionSealingStatus(), (_, _) => true);
        relay.AttachSink(sink);
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
            command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, "expired features"u8.ToArray(), CancellationToken.None);
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        await activeEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var bridge = BridgeForClient(relay, "client");
        var sendLock = BridgeField<SemaphoreSlim>(bridge, "_sendLock");
        await sendLock.WaitAsync();
        var held = true;
        try
        {
            var receive = socket.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), CancellationToken.None);
            allowActive.TrySetResult();
            await responseReady.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // Exercise the production lock wait after the response, without
            // relying on an OS socket buffer's backpressure threshold.
            await Task.Delay(900);
            sendLock.Release(); held = false;
            try
            {
                var received = await receive.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.That(received.MessageType, Is.EqualTo(WebSocketMessageType.Close), "Lock contention cannot authorize payload after lease expiry.");
            }
            catch (WebSocketException) { /* Definite no-payload retirement is also valid. */ }
        }
        finally
        {
            if (held) sendLock.Release();
            allowActive.TrySetResult();
            await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.That(BridgeCount(relay), Is.Zero);
    }

    [TestCase(StatusCode.Unavailable)]
    [TestCase(StatusCode.Cancelled)]
    public async Task LostHeartbeatResponseKeepsOnlyThePreviouslyAcceptedLeaseSendable(StatusCode failure)
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start(); var port = ((IPEndPoint)portReservation.LocalEndpoint).Port; portReservation.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var command = Command();
        var retryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new EvidenceSink((_, _) => Task.CompletedTask);
        var heartbeatCalls = 0;
        sink.Authority.BeforeReportResponse = async (request, ct) =>
        {
            if (request.State != EditorRuntimeBridgeState.Heartbeat) return;
            if (Interlocked.Increment(ref heartbeatCalls) == 1)
            {
                if (failure == StatusCode.Cancelled) await CancelBridgeRpcLikeGrpcAsync(ct);
                throw new RpcException(new(failure, "Controlled heartbeat response loss."));
            }
            retryEntered.TrySetResult();
            await allowRetry.Task.WaitAsync(ct);
        };
        await using var relay = new ComfySessionEventRelay(command.Target.RunnerId, "127.0.0.1", port,
            "test-process", _ => new SessionSealingStatus(), (_, _) => true);
        relay.AttachSink(sink);
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
            command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, "initial features"u8.ToArray(), CancellationToken.None);
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        var buffer = new byte[1024];
        await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        var active = await sink.Authority.Activations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        var bridge = BridgeForClient(relay, "client");
        var connection = BridgeField<object>(bridge, "_connection");
        var deadline = BridgeField<long>(connection, "Deadline");
        try
        {
            await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.That(Stopwatch.GetTimestamp(), Is.LessThan(deadline));
            Assert.That(BridgeField<long>(connection, "Deadline"), Is.EqualTo(deadline), "A lost renewal response cannot extend the accepted lease.");
            await relay.SendClientMessageAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
                command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, "during heartbeat retry"u8.ToArray(),
                active.BridgeConnectionId, active.LeaseRevision, CancellationToken.None);
            var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(System.Text.Encoding.UTF8.GetString(buffer, 0, received.Count), Is.EqualTo("during heartbeat retry"));
            var heartbeats = sink.Authority.ReportCalls.Where(request => request.State == EditorRuntimeBridgeState.Heartbeat).ToArray();
            Assert.That(heartbeats, Has.Length.EqualTo(2));
            Assert.That(heartbeats[1], Is.EqualTo(heartbeats[0]), "Retry must retain the same observation identity.");
        }
        finally
        {
            allowRetry.TrySetResult();
            await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Test]
    public async Task TimedOutReservationRetriesTheSamePhysicalIdentityBeforeNativeConnect()
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start(); var port = ((IPEndPoint)portReservation.LocalEndpoint).Port; portReservation.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var command = Command();
        var sink = new EvidenceSink((_, _) => Task.CompletedTask);
        var calls = 0;
        var timedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.Authority.BeforeReservationResponse = async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                try { await CancelBridgeRpcLikeGrpcAsync(ct); }
                finally { timedOut.TrySetResult(); }
            }
            retryEntered.TrySetResult();
            await allowRetry.Task.WaitAsync(ct);
        };
        await using var relay = new ComfySessionEventRelay(command.Target.RunnerId, "127.0.0.1", port,
            "test-process", _ => new SessionSealingStatus(), (_, _) => true);
        relay.AttachSink(sink);
        var accept = listener.GetContextAsync();
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
            command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, "after timeout"u8.ToArray(), CancellationToken.None);
        try
        {
            await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var claims = sink.Authority.ReserveCalls.ToArray();
            Assert.That(claims, Has.Length.EqualTo(2));
            Assert.That(claims[1], Is.EqualTo(claims[0]), "An internal gRPC timeout retries the original UUID and fence.");
            Assert.That(accept.IsCompleted, Is.False, "A timed-out response grants no permission for native connect.");
            allowRetry.TrySetResult();
            var context = await accept.WaitAsync(TimeSpan.FromSeconds(3));
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var buffer = new byte[1024];
            var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(System.Text.Encoding.UTF8.GetString(buffer, 0, received.Count), Is.EqualTo("after timeout"));
        }
        finally
        {
            allowRetry.TrySetResult();
            await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task CancelBridgeRpcLikeGrpcAsync(CancellationToken token)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new RpcException(new(StatusCode.Cancelled, "The local RPC cancellation token fired."));
        }
    }

    [TestCase(EditorRuntimeBridgeOutcome.Conflict)]
    [TestCase(EditorRuntimeBridgeOutcome.Preempted)]
    [TestCase(EditorRuntimeBridgeOutcome.Capacity)]
    [TestCase(EditorRuntimeBridgeOutcome.Exhausted)]
    public async Task TerminalReservationRejectionDoesNotConsumeCapacityForLaterClients(EditorRuntimeBridgeOutcome outcome)
    {
        var command = Command();
        var sink = new EvidenceSink((_, _) => Task.CompletedTask);
        sink.Authority.TransformReservationResult = result => { result.Outcome = outcome; return result; };
        await using var relay = new ComfySessionEventRelay(command.Target.RunnerId, "127.0.0.1", 1,
            "test-process", _ => new SessionSealingStatus(), (_, _) => true);
        relay.AttachSink(sink);
        try
        {
            for (var index = 0; index < 40; index++)
            {
                var client = "rejected-client-" + index;
                await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, client, command.Target.GpuGeneration,
                    command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, [], CancellationToken.None);
                var bridge = BridgeForClient(relay, client);
                await BridgeField<Task>(bridge, "_loopTask").WaitAsync(TimeSpan.FromSeconds(1));
                Assert.That(sink.Authority.ReserveCalls.Count(call => call.ClientId == client), Is.EqualTo(1));
                Assert.That(BridgeCount(relay), Is.LessThanOrEqualTo(32));
            }
        }
        finally { await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.That(BridgeCount(relay), Is.Zero);
    }

    [Test]
    public async Task StaleReservationRetriesAFreshPhysicalAttemptWhileTheRuntimeRemainsCurrent()
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start(); var port = ((IPEndPoint)portReservation.LocalEndpoint).Port; portReservation.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var command = Command();
        var sink = new EvidenceSink((_, _) => Task.CompletedTask);
        var responses = 0;
        sink.Authority.TransformReservationResult = result =>
        {
            if (Interlocked.Increment(ref responses) == 1) result.Outcome = EditorRuntimeBridgeOutcome.Stale;
            return result;
        };
        await using var relay = new ComfySessionEventRelay(command.Target.RunnerId, "127.0.0.1", port,
            "test-process", _ => new SessionSealingStatus(), (_, _) => true);
        relay.AttachSink(sink);
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
            command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, "winning features"u8.ToArray(), CancellationToken.None);
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        var buffer = new byte[1024];
        var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(System.Text.Encoding.UTF8.GetString(buffer, 0, received.Count), Is.EqualTo("winning features"));
        var claims = sink.Authority.ReserveCalls.ToArray();
        Assert.That(claims, Has.Length.EqualTo(2));
        Assert.That(claims[1].BridgeConnectionId, Is.Not.EqualTo(claims[0].BridgeConnectionId));
        Assert.That(claims[1].ExpectedActivationFence, Is.GreaterThan(claims[0].ExpectedActivationFence));
        Assert.That(sink.Authority.ReportCalls.Where(call => call.State == EditorRuntimeBridgeState.Active)
            .Select(call => call.BridgeConnectionId), Is.EqualTo(new[] { claims[1].BridgeConnectionId }));
        await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(BridgeCount(relay), Is.Zero);
    }

    [Test]
    public async Task UntrackedRuntimeBridgesReleaseCapacityAcrossDifferentClients()
    {
        var command = Command();
        var tracked = new RuntimeIdentity(command.Target.GpuGeneration, command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId);
        var sink = new EvidenceSink((_, _) => Task.CompletedTask);
        sink.Authority.BeforeReservationResponse = async (_, ct) => await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        await using var relay = new ComfySessionEventRelay(command.Target.RunnerId, "127.0.0.1", 1,
            "test-process", _ => new SessionSealingStatus(), (_, runtime) => runtime.SameRuntime(tracked));
        relay.AttachSink(sink);
        try
        {
            for (var index = 0; index < 40; index++)
            {
                tracked = new(command.Target.GpuGeneration, command.Target.RuntimeEpoch + (ulong)index, Guid.NewGuid().ToString("n"));
                await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "runtime-client-" + index,
                    tracked.LifecycleGeneration, tracked.RuntimeEpoch, tracked.RuntimeInstanceId, [], CancellationToken.None);
                Assert.That(BridgeCount(relay), Is.LessThanOrEqualTo(32));
            }
        }
        finally { await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.That(BridgeCount(relay), Is.Zero);
    }

    private static object BridgeForClient(ComfySessionEventRelay relay, string client)
        => ((IDictionary)typeof(ComfySessionEventRelay).GetField("_bridges", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(relay)!).Values.Cast<object>().Single(bridge => BridgeField<string>(bridge, "_clientId") == client);

    private static T BridgeField<T>(object instance, string name)
        => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(instance)!;
}
