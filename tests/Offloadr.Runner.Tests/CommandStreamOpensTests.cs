using System.Collections.Concurrent;
using System.Reflection;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

/// <summary>Each command stream open says which it is, and the highest Start order read before it.</summary>
public sealed class CommandStreamOpensTests
{
    [Test]
    public void EachOpen_RisesByOne_AndCarriesTheHighestStartOrderReadBefore()
    {
        var opens = new CommandStreamOpens();

        var first = opens.Next("runner", "1.0", "process");
        opens.Received(7);
        opens.Received(3);
        var second = opens.Next("runner", "1.0", "process");
        var third = opens.Next("runner", "1.0", "process");

        Assert.Multiple(() =>
        {
            Assert.That(first.CommandStreamSequence, Is.EqualTo(1UL), "0 is refused.");
            Assert.That(first.ReceivedRuntimeOrder, Is.Zero);
            Assert.That(first.ProtocolVersion, Is.EqualTo(RunnerProtocolVersion.Current));
            Assert.That(second.CommandStreamSequence, Is.EqualTo(2UL));
            Assert.That(second.ReceivedRuntimeOrder, Is.EqualTo(7UL), "The highest, not the last.");
            Assert.That(third.CommandStreamSequence, Is.EqualTo(3UL));
            Assert.That(third.ReceivedRuntimeOrder, Is.EqualTo(7UL), "Nothing was read on the second.");
        });
    }

    [Test]
    public async Task AReconnect_OpensTheNextStream_WithTheStartOrderReadOnTheLastOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "runner-command-opens-" + Guid.NewGuid().ToString("n"));
        var opened = new ConcurrentQueue<CommandStreamRequest>();
        var secondOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The first stream delivers a Start with no session, which is dropped unread, and one the loop refuses as
        // stale (at or below its session's Stop cutoff), and ends; the second stays open.
        var invoker = new Mock<CallInvoker>(MockBehavior.Strict);
        invoker.Setup(peer => peer.AsyncServerStreamingCall(
                It.IsAny<Method<CommandStreamRequest, CommandStreamResponse>>(), It.IsAny<string?>(),
                It.IsAny<CallOptions>(), It.IsAny<CommandStreamRequest>()))
            .Returns((Method<CommandStreamRequest, CommandStreamResponse> _, string? _, CallOptions options, CommandStreamRequest request) =>
            {
                opened.Enqueue(request.Clone());
                IAsyncStreamReader<CommandStreamResponse> reader = opened.Count == 1
                    ? new Commands(
                        new CommandStreamResponse { StartRuntime = Start(9) },
                        new CommandStreamResponse { StartRuntime = Start(7, "synthetic-session") })
                    : new Commands();
                if (opened.Count >= 2)
                {
                    secondOpen.TrySetResult();
                }

                return new AsyncServerStreamingCall<CommandStreamResponse>(reader, Task.FromResult(new Metadata()),
                    () => Status.DefaultSuccess, () => new Metadata(), () => { });
            });
        var acknowledged = new ConcurrentQueue<AcknowledgeRuntimeStartRequest>();
        invoker.Setup(peer => peer.AsyncUnaryCall(
                It.IsAny<Method<AcknowledgeRuntimeStartRequest, AcknowledgeRuntimeStartResponse>>(),
                It.IsAny<string?>(), It.IsAny<CallOptions>(), It.IsAny<AcknowledgeRuntimeStartRequest>()))
            .Returns((Method<AcknowledgeRuntimeStartRequest, AcknowledgeRuntimeStartResponse> _, string? _, CallOptions _, AcknowledgeRuntimeStartRequest ack) =>
            {
                acknowledged.Enqueue(ack.Clone());
                return new AsyncUnaryCall<AcknowledgeRuntimeStartResponse>(Task.FromResult(new AcknowledgeRuntimeStartResponse()),
                    Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
            });
        var sessions = new ServiceClientManager.LogicalSessionState(string.Empty);
        sessions.RecordStopCutoff("synthetic-session", 7);
        using var host = new CancellationTokenSource();
        try
        {
            var isolation = new Mock<ISessionIsolationStrategy>(MockBehavior.Strict);
            var commands = new Mock<ILinuxCommandRunner>(MockBehavior.Strict);
            var options = SessionProcessOptions.FromEnvironment(name => name == "SESSION_ROOT" ? Path.Combine(root, "sessions") : null);
            await using var manager = new SessionProcessManager(TestRuntimeActivitySink.Accepted, options, isolation.Object,
                new RunnerVfsEnvironmentBuilder(), commands.Object);
            var settings = Aria2Settings.FromEnvironment(name => name switch
            {
                "ARIA2_STATE_DIR" => Path.Combine(root, "aria"),
                "ARIA2_DOWNLOAD_DIR" => Path.Combine(root, "models"),
                _ => null
            }, () => "fixture");
            await using var downloads = new ModelDownloadService(new IdleBackend(), settings);
            var client = new RunnerControlService.RunnerControlServiceClient(invoker.Object);
            await using var sink = new GrpcRunnerControlSink(client, client, client, client, TimeSpan.FromSeconds(5));
            await using var relay = new ComfySessionEventRelay((_, _) => throw new NotSupportedException(), "runner", "127.0.0.1", 8188,
                _ => null, (_, _) => false);
            await using var artifacts = new ArtifactUploadService(null, new RunnerArtifactService.RunnerArtifactServiceClient(invoker.Object),
                NullLogger<ArtifactUploadService>.Instance);
            await using var workspace = new WorkspaceMirrorService(new RunnerWorkspaceService.RunnerWorkspaceServiceClient(invoker.Object),
                null, NullLogger<WorkspaceMirrorService>.Instance);
            var gpu = new GpuPowerCommandExecutor(new GpuPowerLimitService(new RuntimeTelemetryService(new Mock<IRuntimeTelemetryProbe>(MockBehavior.Strict).Object)));

            var method = typeof(ServiceClientManager).GetMethod("RunCommandStreamLoopAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            var listener = (Task)method.Invoke(null,
            [
                client, sink, host.Token, "runner", "fixture-process", "fixture", TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(20), Task.CompletedTask, manager, downloads, relay, artifacts, workspace, gpu,
                null, new ServiceClientManager.RunnerCommandWorkState(host.Token), sessions,
                new RuntimeIdentityRegistry(), (Func<Task>)(() => host.CancelAsync())
            ])!;
            await secondOpen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var waited = 0; acknowledged.IsEmpty && waited < 500; waited++)
            {
                await Task.Delay(10);
            }

            await host.CancelAsync();
            await listener.WaitAsync(TimeSpan.FromSeconds(5));
            await gpu.CancelAndDrainAsync();
        }
        finally
        {
            await host.CancelAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        var requests = opened.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(requests[0].CommandStreamSequence, Is.EqualTo(1UL));
            Assert.That(requests[0].ReceivedRuntimeOrder, Is.Zero);
            Assert.That(requests[1].CommandStreamSequence, Is.EqualTo(2UL));
            Assert.That(requests[1].ReceivedRuntimeOrder, Is.EqualTo(7UL), "Refused on the stream before; the one with no session does not count.");
            Assert.That(acknowledged.Select(ack => ack.SessionId), Is.EqualTo(new[] { "synthetic-session" }), "Refused, and told so.");
            Assert.That(acknowledged.Single().Ready, Is.False);
            Assert.That(acknowledged.Single().Message, Does.Contain("superseded runtime"), "Refused as older work, not failed starting.");
        });
    }

    private static StartRuntimeCommand Start(ulong order, string sessionId = "", string runtimeId = "33333333333343338333333333333333") => new()
    {
        SessionId = sessionId,
        Runtime = new RuntimeRef { RuntimeId = runtimeId, RuntimeOrder = order },
    };

    private sealed class IdleBackend : TestModelTransferBackend;

    /// <summary>Delivers its commands, then ends the stream; with none, stays open until cancelled.</summary>
    private sealed class Commands(params CommandStreamResponse[] commands) : IAsyncStreamReader<CommandStreamResponse>
    {
        private int _next = -1;

        public CommandStreamResponse Current => commands[_next];

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (commands.Length == 0)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return ++_next < commands.Length;
        }
    }
}
