using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Offloadr.EditorRuntime.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public sealed partial class ComfyPromptEvidenceTests
{
    [Test]
    public async Task DetachedControlSinkKeepsTheAcceptedConnectionUntilReattachment()
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start(); var port = ((IPEndPoint)portReservation.LocalEndpoint).Port; portReservation.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var command = Command();
        var heartbeatRetried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new EvidenceSink((_, _) => Task.CompletedTask);
        sink.Authority.BeforeReportResponse = (request, _) =>
        {
            if (request.State == EditorRuntimeBridgeState.Heartbeat) heartbeatRetried.TrySetResult();
            return Task.CompletedTask;
        };
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, command.Target.RunnerId, "127.0.0.1", port,
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
        await BridgeField<TaskCompletionSource<bool>>(connection, "SendReady").Task.WaitAsync(TimeSpan.FromSeconds(3));
        var deadline = BridgeField<long>(connection, "Deadline");
        try
        {
            relay.DetachSink(sink);
            // Keep the control lane detached across the production ten-second
            // heartbeat, while the previously accepted thirty-second lease lives.
            await Task.Delay(TimeSpan.FromSeconds(11));
            Assert.That(BridgeField<object>(bridge, "_connection"), Is.SameAs(connection));
            Assert.That(BridgeField<long>(connection, "Deadline"), Is.EqualTo(deadline));
            Assert.That(Stopwatch.GetTimestamp(), Is.LessThan(deadline));
            await relay.SendClientMessageAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
                command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, "during control reconnect"u8.ToArray(),
                active.BridgeConnectionId, active.LeaseRevision, CancellationToken.None);
            var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(System.Text.Encoding.UTF8.GetString(buffer, 0, received.Count), Is.EqualTo("during control reconnect"));
            relay.AttachSink(sink);
            await heartbeatRetried.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var heartbeat = sink.Authority.ReportCalls.Single(request => request.State == EditorRuntimeBridgeState.Heartbeat);
            Assert.Multiple(() =>
            {
                Assert.That(heartbeat.BridgeConnectionId, Is.EqualTo(active.BridgeConnectionId));
                Assert.That(heartbeat.LeaseRevision, Is.EqualTo(active.LeaseRevision));
                Assert.That(heartbeat.ReportSequence, Is.EqualTo(2));
                Assert.That(sink.Authority.ReserveCalls, Has.Count.EqualTo(1), "Reattachment must not open another physical connection.");
            });
        }
        finally
        {
            relay.AttachSink(sink);
            await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.That(BridgeCount(relay), Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RejectedHeartbeatRevokesQueuedClientSendsBeforeWaitingForTheSendLock(bool mismatched)
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start(); var port = ((IPEndPoint)portReservation.LocalEndpoint).Port; portReservation.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var command = Command();
        var heartbeatEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowHeartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new EvidenceSink((_, _) => Task.CompletedTask);
        sink.Authority.BeforeReportResponse = async (request, ct) =>
        {
            if (request.State != EditorRuntimeBridgeState.Heartbeat) return;
            heartbeatEntered.TrySetResult();
            await allowHeartbeat.Task.WaitAsync(ct);
        };
        sink.Authority.TransformReportResult = result =>
        {
            if (result.Lease.ReportSequence != 2) return result;
            if (mismatched) result.Lease.BridgeConnectionId = Guid.NewGuid().ToString("n");
            else result.Outcome = EditorRuntimeBridgeOutcome.Conflict;
            responseReady.TrySetResult();
            return result;
        };
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, command.Target.RunnerId, "127.0.0.1", port,
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
        await BridgeField<TaskCompletionSource<bool>>(connection, "SendReady").Task.WaitAsync(TimeSpan.FromSeconds(3));
        var sendLock = BridgeField<SemaphoreSlim>(bridge, "_sendLock");
        await sendLock.WaitAsync();
        var held = true;
        Task? send = null;
        try
        {
            await heartbeatEntered.Task.WaitAsync(TimeSpan.FromSeconds(12));
            var receive = socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            // Queue the client first. A definitive response must revoke it even
            // when the report continuation is behind it on the same semaphore.
            send = relay.SendClientMessageAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
                command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, "must never reach native"u8.ToArray(),
                active.BridgeConnectionId, active.LeaseRevision, CancellationToken.None);
            allowHeartbeat.TrySetResult();
            await responseReady.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(250);
            sendLock.Release(); held = false;
            Exception? sendFailure = null;
            try { await send.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) { sendFailure = ex; }
            WebSocketReceiveResult? nativeResult = null;
            try { nativeResult = await receive.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (WebSocketException) { }
            Assert.Multiple(() =>
            {
                Assert.That(sendFailure, Is.InstanceOf<InvalidOperationException>(), "A queued send must observe the received revocation.");
                Assert.That(nativeResult?.MessageType, Is.Not.EqualTo(WebSocketMessageType.Text), "No client payload may follow the received revocation.");
            });
        }
        finally
        {
            allowHeartbeat.TrySetResult();
            if (held) sendLock.Release();
            await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
            if (send is not null)
                try { await send; }
                catch (Exception) { }
        }
        Assert.That(BridgeCount(relay), Is.Zero);
    }
}
