using System.Net.Sockets;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public class VfsIpcPeerAuthenticationLinuxTests
{
    private static readonly Aria2Settings DefaultAria2Settings = Aria2Settings.FromEnvironment(static _ => null, static () => "generated-secret");

    [Test]
    public async Task Start_PassesKernelPeerCredentialsToAuthorizerAndHandler()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var root = CreateRoot();
        try
        {
            VfsIpcPeerCredentials? authorized = null;
            VfsIpcPeerCredentials? handled = null;
            await using var server = new VfsIpcServer(
                Path.Combine(root, "model-fetch.sock"),
                (_, peer, _) =>
                {
                    handled = peer;
                    return Task.FromResult(VfsIpcResponse.Success());
                },
                authorizePeer: peer =>
                {
                    authorized = peer;
                    return true;
                });
            server.Start();

            var response = await SendAsync(server.SocketPath, VfsIpcRequest.Release(5));

            Assert.Multiple(() =>
            {
                Assert.That(response.Status, Is.EqualTo(VfsIpcStatus.Success));
                Assert.That(authorized, Is.Not.Null);
                Assert.That(authorized!.Value.ProcessId, Is.EqualTo(Environment.ProcessId));
                Assert.That(authorized.Value.UserId, Is.EqualTo(VfsIpcPeerCredentials.CurrentEffectiveUserId));
                Assert.That(handled, Is.EqualTo(authorized));
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task Start_UnauthorizedPeer_GetsIoErrorWithoutReachingHandler()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var root = CreateRoot();
        try
        {
            var handlerCalls = 0;
            await using var server = new VfsIpcServer(
                Path.Combine(root, "model-fetch.sock"),
                (_, _) =>
                {
                    Interlocked.Increment(ref handlerCalls);
                    return Task.FromResult(VfsIpcResponse.Success());
                },
                authorizePeer: static _ => false);
            server.Start();

            var response = await SendAsync(server.SocketPath, VfsIpcRequest.Open("/models/any.bin", "session-1"));

            Assert.Multiple(() =>
            {
                Assert.That(response.Status, Is.EqualTo(VfsIpcStatus.IoError));
                Assert.That(Volatile.Read(ref handlerCalls), Is.Zero);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task DefaultPolicy_RejectsConnectionsFromAnotherUid()
    {
        LinuxTestPrerequisites.RequireRoot();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var root = CreateRoot();
        try
        {
            await using var user = await LinuxTestUser.CreateAsync();
            var client = await NativeVfsIpcClient.CompileAsync(root);
            var handlerCalls = 0;
            await using var server = new VfsIpcServer(
                Path.Combine(root, "model-fetch.sock"),
                (_, _) =>
                {
                    Interlocked.Increment(ref handlerCalls);
                    return Task.FromResult(VfsIpcResponse.Success());
                });
            server.Start();

            var fromOtherUser = await NativeVfsIpcClient.RunAsync(client, user.Name, server.SocketPath, "release", "9");
            var callsAfterOtherUser = Volatile.Read(ref handlerCalls);
            var fromRoot = await NativeVfsIpcClient.RunAsync(client, null, server.SocketPath, "release", "9");

            Assert.Multiple(() =>
            {
                Assert.That(fromOtherUser.Status, Is.EqualTo((byte)VfsIpcStatus.IoError));
                Assert.That(callsAfterOtherUser, Is.Zero);
                Assert.That(fromRoot.Status, Is.EqualTo((byte)VfsIpcStatus.Success));
                Assert.That(Volatile.Read(ref handlerCalls), Is.EqualTo(1));
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task ProvisionalLease_CannotBeAcknowledgedOrReleasedByAnotherUid()
    {
        LinuxTestPrerequisites.RequireRoot();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var root = CreateRoot();
        try
        {
            await using var owner = await LinuxTestUser.CreateAsync();
            await using var intruder = await LinuxTestUser.CreateAsync();
            var client = await NativeVfsIpcClient.CompileAsync(root);
            var released = new List<ulong>();
            await using var server = new VfsIpcServer(
                Path.Combine(root, "model-fetch.sock"),
                (request, _) =>
                {
                    if (request.Operation == VfsIpcOperation.Release)
                    {
                        lock (released)
                        {
                            released.Add(request.LeaseId);
                        }

                        return Task.FromResult(VfsIpcResponse.Success());
                    }

                    return Task.FromResult(new VfsIpcResponse(
                        VfsIpcStatus.Success,
                        VfsIpcOpenDisposition.RangeManaged,
                        LeaseId: 77,
                        TransferEpoch: 1,
                        ExpectedLength: 8));
                },
                openAcknowledgementTimeout: TimeSpan.FromSeconds(30),
                authorizePeer: static _ => true);
            server.Start();

            var opened = await NativeVfsIpcClient.RunAsync(client, owner.Name, server.SocketPath, "open", "/models/a.bin", "session-1");
            var intruderAck = await NativeVfsIpcClient.RunAsync(client, intruder.Name, server.SocketPath, "ack", "77");
            var ownerAck = await NativeVfsIpcClient.RunAsync(client, owner.Name, server.SocketPath, "ack", "77");

            Assert.Multiple(() =>
            {
                Assert.That(opened.Status, Is.EqualTo((byte)VfsIpcStatus.Success));
                Assert.That(opened.LeaseId, Is.EqualTo(77));
                Assert.That(intruderAck.Status, Is.EqualTo((byte)VfsIpcStatus.IoError));
                Assert.That(ownerAck.Status, Is.EqualTo((byte)VfsIpcStatus.Success));
                Assert.That(released, Is.Empty);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task ModelDownloadIpcServer_AcceptsOnlyTheActiveSessionUid()
    {
        LinuxTestPrerequisites.RequireRoot();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var root = CreateRoot();
        try
        {
            await using var sessionUser = await LinuxTestUser.CreateAsync();
            await using var otherUser = await LinuxTestUser.CreateAsync();
            var client = await NativeVfsIpcClient.CompileAsync(root);

            var modelPath = Path.Combine(root, "models", "model.bin");
            var backend = new CountingBackend(modelPath);
            var downloadService = new ModelDownloadService(backend, DefaultAria2Settings);
            downloadService.SetActiveSession("session-1");
            downloadService.RegisterDownloads(
                "session-1",
                [
                    new ModelDownloadRequest
                    {
                        ModelId = "model",
                        Filename = "model.bin",
                        DestinationPath = modelPath,
                        SizeBytes = 10,
                        SourceUrl = "https://models.example.test/model.bin"
                    }
                ]);
            await using var artifactService = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                new FakeRunnerArtifactClient(),
                NullLogger<ArtifactUploadService>.Instance);
            await using var workspaceMirrorService = new WorkspaceMirrorService(
                new FakeRunnerWorkspaceClient(),
                runnerSecret: "runner-secret-value",
                NullLogger<WorkspaceMirrorService>.Instance);
            var sessionUserIds = new Dictionary<string, uint> { ["session-1"] = sessionUser.UserId };
            await using var ipcServer = new ModelDownloadIpcServer(
                Path.Combine(root, "model-fetch.sock"),
                downloadService,
                artifactService,
                workspaceMirrorService,
                sessionId => sessionUserIds.TryGetValue(sessionId, out var uid) ? uid : null);
            ipcServer.Start();
            var socketPath = Path.Combine(root, "model-fetch.sock");

            var fromOtherUser = await NativeVfsIpcClient.RunAsync(client, otherUser.Name, socketPath, "open", modelPath, "session-1");
            var downloadsAfterOtherUser = backend.CreateCalls;
            var fromSessionUser = await NativeVfsIpcClient.RunAsync(client, sessionUser.Name, socketPath, "open", modelPath, "session-1");

            downloadService.SetActiveSession("session-2");
            var afterSessionChange = await NativeVfsIpcClient.RunAsync(client, sessionUser.Name, socketPath, "release", "1");

            Assert.Multiple(() =>
            {
                Assert.That(fromOtherUser.Status, Is.EqualTo((byte)VfsIpcStatus.IoError));
                Assert.That(downloadsAfterOtherUser, Is.Zero);
                Assert.That(fromSessionUser.Status, Is.EqualTo((byte)VfsIpcStatus.Success));
                Assert.That(backend.CreateCalls, Is.EqualTo(1));
                Assert.That(afterSessionChange.Status, Is.EqualTo((byte)VfsIpcStatus.IoError));
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<VfsIpcResponse> SendAsync(string socketPath, VfsIpcRequest request)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        await VfsIpcProtocol.WriteRequestAsync(socket, request, CancellationToken.None);
        var response = await VfsIpcProtocol
            .ReadResponseAsync(socket, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(response, Is.Not.Null);
        return response!.Value;
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "runneragent-peer-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class FakeRunnerArtifactClient : RunnerArtifactService.RunnerArtifactServiceClient
    {
        public override AsyncUnaryCall<RunnerArtifactServiceListArtifactsResponse> ListArtifactsAsync(RunnerArtifactServiceListArtifactsRequest request, CallOptions options)
            => new(
                Task.FromResult(new RunnerArtifactServiceListArtifactsResponse()),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
    }

    private sealed class FakeRunnerWorkspaceClient : RunnerWorkspaceService.RunnerWorkspaceServiceClient
    {
    }

    private sealed class CountingBackend(string destinationPath) : TestModelTransferBackend
    {
        private int _createCalls;

        public int CreateCalls => Volatile.Read(ref _createCalls);

        public override Task<IReadOnlyList<ModelTransferHandle>> CreateAsync(
            ModelTransferCreateRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _createCalls);
            File.WriteAllText(destinationPath, "downloaded");
            IReadOnlyList<ModelTransferHandle> handles = [new("gid-1")];
            return Task.FromResult(handles);
        }

        public override Task<ModelTransferSnapshot> GetStatusAsync(
            ModelTransferHandle handle,
            CancellationToken cancellationToken)
            => Task.FromResult(Snapshot(handle, "complete", 10, 10));
    }
}
