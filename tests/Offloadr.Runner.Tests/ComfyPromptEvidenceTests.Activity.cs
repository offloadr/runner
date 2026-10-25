using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public sealed partial class ComfyPromptEvidenceTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task NativeActivityPrecedesIncompleteOrSuppressedFrameHandling(bool completeSuppressedFrame)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var command = Command();
        var observed = new TaskCompletionSource<(string Session, RuntimeIdentity Runtime)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new EvidenceSink((_, _) => Task.CompletedTask);
        await using var relay = new ComfySessionEventRelay(
            (session, runtime) => observed.TrySetResult((session, runtime)),
            command.Target.RunnerId, "127.0.0.1", port, "test-process", _ => new SessionSealingStatus(), (_, _) => true);
        relay.AttachSink(sink);
        await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "activity-client", command.Target.GpuGeneration,
            command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, [], CancellationToken.None);
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        await sink.Authority.Activations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            var bytes = Encoding.UTF8.GetBytes(completeSuppressedFrame ? "{\"type\":\"feature_flags\",\"data\":{}}" : "{");
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, completeSuppressedFrame, CancellationToken.None);
            var activity = await observed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Multiple(() =>
            {
                Assert.That(activity.Session, Is.EqualTo(command.SessionId));
                Assert.That(activity.Runtime, Is.EqualTo(new RuntimeIdentity(command.Target.GpuGeneration,
                    command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId).Normalized()));
            });
        }
        finally
        {
            socket.Abort();
            await relay.StopSessionAsync(command.SessionId).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }
}
