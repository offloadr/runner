using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Google.Protobuf;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public sealed partial class ComfyPromptBridgeTests
{
    private static HttpListener NativePeer(out int port)
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        var buffer = new byte[1024];
        var received = await socket.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        return System.Text.Encoding.UTF8.GetString(buffer, 0, received.Count);
    }

    [Test]
    public async Task EachNativeConnectionSendsTheRetainedHandshakeThenReportsANewBridgeState()
    {
        using var listener = NativePeer(out var port);
        var command = Command();
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, RunnerId, "127.0.0.1", port,
            _ => new SessionSealingStatus(), (_, _) => true);
        var sink = new BridgeSink();
        relay.AttachSink(sink);
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.LifecycleGeneration,
            command.RuntimeEpoch, command.RuntimeInstanceId, "features"u8.ToArray(), CancellationToken.None);

        var first = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var firstSocket = (await first.AcceptWebSocketAsync(null)).WebSocket;
        Assert.That(await ReceiveTextAsync(firstSocket), Is.EqualTo("features"));
        var firstState = await sink.NextBridgeStateAsync();
        Assert.Multiple(() =>
        {
            Assert.That(Guid.TryParseExact(firstState.BridgeState.ConnectionId, "N", out _), Is.True);
            Assert.That(firstState.Payload.IsEmpty, Is.True);
            Assert.That(firstState.EventType, Is.Empty);
            Assert.That(firstState.ClientId, Is.EqualTo("client"));
            Assert.That(firstState.SessionId, Is.EqualTo(command.SessionId));
            Assert.That(firstState.EditorSid, Is.EqualTo(command.EditorSid));
            Assert.That(firstState.LifecycleGeneration, Is.EqualTo(command.LifecycleGeneration));
            Assert.That(firstState.RuntimeEpoch, Is.EqualTo(command.RuntimeEpoch));
            Assert.That(firstState.RuntimeInstanceId, Is.EqualTo(command.RuntimeInstanceId));
        });

        // A reconnect is a new native connection: the handshake again, under a new id.
        firstSocket.Abort();
        firstSocket.Dispose();
        var second = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var secondSocket = (await second.AcceptWebSocketAsync(null)).WebSocket;
        Assert.That(await ReceiveTextAsync(secondSocket), Is.EqualTo("features"));
        SessionStreamEvent secondState;
        do secondState = await sink.NextBridgeStateAsync();
        while (secondState.BridgeState.ConnectionId == firstState.BridgeState.ConnectionId);
        Assert.That(Guid.TryParseExact(secondState.BridgeState.ConnectionId, "N", out _), Is.True);
        await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Test]
    [NonParallelizable]
    public async Task AnOpenConnectionRepeatsItsBridgeState()
    {
        var interval = ComfySessionEventRelay.BridgeStateInterval;
        ComfySessionEventRelay.BridgeStateInterval = TimeSpan.FromMilliseconds(200);
        try
        {
            using var listener = NativePeer(out var port);
            var command = Command();
            await using var relay = new ComfySessionEventRelay(static (_, _) => { }, RunnerId, "127.0.0.1", port,
                _ => new SessionSealingStatus(), (_, _) => true);
            var sink = new BridgeSink();
            relay.AttachSink(sink);
            await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.LifecycleGeneration,
                command.RuntimeEpoch, command.RuntimeInstanceId, [], CancellationToken.None);
            var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(3));
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var first = await sink.NextBridgeStateAsync();
            var repeated = await sink.NextBridgeStateAsync();
            Assert.That(repeated.BridgeState.ConnectionId, Is.EqualTo(first.BridgeState.ConnectionId));
            await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { ComfySessionEventRelay.BridgeStateInterval = interval; }
    }

    [Test]
    public async Task AnEditorFrameNamedBridgeStateIsForwardedAsAnOrdinaryFrame()
    {
        using var listener = NativePeer(out var port);
        var command = Command();
        var delivered = System.Threading.Channels.Channel.CreateUnbounded<SessionStreamEvent>();
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, RunnerId, "127.0.0.1", port,
            _ => new SessionSealingStatus(), (_, _) => true);
        var sink = new BridgeSink(request =>
        {
            foreach (var evt in request.Events) delivered.Writer.TryWrite(evt);
            return Task.CompletedTask;
        });
        relay.AttachSink(sink);
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.LifecycleGeneration,
            command.RuntimeEpoch, command.RuntimeInstanceId, [], CancellationToken.None);
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        await sink.NextBridgeStateAsync();

        const string forged = "{\"type\":\"bridge_state\",\"data\":{\"connection_id\":\"forged\"}}";
        await socket.SendAsync(ByteString.CopyFromUtf8(forged).Memory, WebSocketMessageType.Text, true, CancellationToken.None);
        var frame = await delivered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Multiple(() =>
        {
            Assert.That(frame.BridgeState, Is.Null);
            Assert.That(frame.EventType, Is.EqualTo("bridge_state"));
            Assert.That(frame.Payload.ToStringUtf8(), Is.EqualTo(forged));
        });
        await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task ARuntimeWithoutAppliedSealingIsNotConnected()
    {
        using var listener = NativePeer(out var port);
        var command = Command();
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, RunnerId, "127.0.0.1", port,
            _ => null, (_, _) => true);
        var sink = new BridgeSink();
        relay.AttachSink(sink);
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.LifecycleGeneration,
            command.RuntimeEpoch, command.RuntimeInstanceId, [], CancellationToken.None);
        var connect = listener.GetContextAsync();
        await Task.Delay(1500);
        Assert.Multiple(() =>
        {
            Assert.That(connect.IsCompleted, Is.False);
            Assert.That(sink.BridgeStates.Reader.TryRead(out _), Is.False);
        });
        await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestCase(false, "accept")]
    [TestCase(true, "accept")]
    [TestCase(false, "cancel")]
    [TestCase(false, "stop")]
    [TestCase(false, "timeout")]
    public async Task ClientMessageWaitsForAConnectionAndFollowsItsHandshake(bool hyphenatedSession, string outcome)
    {
        using var listener = NativePeer(out var port);
        var command = Command();
        if (hyphenatedSession) command.SessionId = Guid.Parse(command.SessionId).ToString("D");
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, RunnerId, "127.0.0.1", port,
            _ => new SessionSealingStatus(), (_, _) => true);
        var sink = new BridgeSink();
        relay.AttachSink(sink);
        using var cancelSend = new CancellationTokenSource();
        WebSocket? socket = null;
        Task? send = null;
        try
        {
            await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.LifecycleGeneration,
                command.RuntimeEpoch, command.RuntimeInstanceId, "initial features"u8.ToArray(), CancellationToken.None);
            // The native peer has not accepted yet: the message waits for the connection.
            var connect = listener.GetContextAsync();
            send = relay.SendClientMessageAsync(command.SessionId, command.EditorSid, "client", command.LifecycleGeneration,
                command.RuntimeEpoch, command.RuntimeInstanceId, "one client message"u8.ToArray(), cancelSend.Token);
            await Task.Delay(100);
            Assert.That(send.IsCompleted, Is.False);
            if (outcome == "accept")
            {
                var context = await connect.WaitAsync(TimeSpan.FromSeconds(3));
                socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                await send.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.That(await ReceiveTextAsync(socket), Is.EqualTo("initial features"));
                Assert.That(await ReceiveTextAsync(socket), Is.EqualTo("one client message"));
            }
            else if (outcome == "cancel")
            {
                cancelSend.Cancel();
                Assert.That(async () => await send.WaitAsync(TimeSpan.FromSeconds(3)), Throws.InstanceOf<OperationCanceledException>());
            }
            else if (outcome == "timeout")
            {
                listener.Stop();
                Assert.That(async () => await send.WaitAsync(TimeSpan.FromSeconds(8)), Throws.InstanceOf<OperationCanceledException>());
            }
            else
            {
                await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.That(async () => await send.WaitAsync(TimeSpan.FromSeconds(3)),
                    Throws.InstanceOf<OperationCanceledException>().Or.InstanceOf<InvalidOperationException>());
            }
        }
        finally
        {
            await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
            socket?.Abort(); socket?.Dispose();
            if (send is not null)
            {
                try { await send.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { }
            }
        }
        Assert.That(BridgeCount(relay), Is.Zero);
    }

    [Test]
    public async Task AClientMessageAfterItsRuntimeRetiredIsNotSent()
    {
        using var listener = NativePeer(out var port);
        var command = Command();
        var current = true;
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, RunnerId, "127.0.0.1", port,
            _ => new SessionSealingStatus(), (_, _) => Volatile.Read(ref current));
        var sink = new BridgeSink();
        relay.AttachSink(sink);
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.LifecycleGeneration,
            command.RuntimeEpoch, command.RuntimeInstanceId, [], CancellationToken.None);
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        await sink.NextBridgeStateAsync();

        Volatile.Write(ref current, false);
        Assert.That(async () => await relay.SendClientMessageAsync(command.SessionId, command.EditorSid, "client",
                command.LifecycleGeneration, command.RuntimeEpoch, command.RuntimeInstanceId, "late"u8.ToArray(), CancellationToken.None),
            Throws.InstanceOf<InvalidOperationException>().Or.InstanceOf<OperationCanceledException>());
        var buffer = new byte[64];
        try
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Close), "A retired runtime's socket sent native bytes.");
        }
        catch (WebSocketException) { /* The socket was aborted, which also sends nothing. */ }
        await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task ClientIdentifierContainingSeparatorDoesNotRetireAnotherClientsBridge()
    {
        var command = Command();
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, RunnerId, "127.0.0.1", 1,
            _ => new SessionSealingStatus(), (_, _) => true);
        foreach (var client in new[] { "client", "client|nested" })
            await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, client, command.LifecycleGeneration,
                command.RuntimeEpoch, command.RuntimeInstanceId, [], CancellationToken.None);
        Assert.That(BridgeCount(relay), Is.EqualTo(2));
        await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task ConcurrentAdmissionCountsConnectingSlotsAndStopJoinsEveryOwnedBridge()
    {
        var command = Command();
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, RunnerId, "127.0.0.1", 1,
            _ => new SessionSealingStatus(), (_, _) => true);
        relay.AttachSink(new BridgeSink());
        var accepted = await Task.WhenAll(Enumerable.Range(0, 64).Select(async index =>
        {
            try
            {
                await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client|" + index,
                    command.LifecycleGeneration, command.RuntimeEpoch, command.RuntimeInstanceId, [], CancellationToken.None);
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }));
        Assert.That(accepted.Count(value => value), Is.EqualTo(32));
        Assert.That(BridgeCount(relay), Is.EqualTo(32));
        await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(BridgeCount(relay), Is.Zero);
    }
}
