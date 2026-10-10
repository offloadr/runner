using System.Net.Sockets;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public class ModelDownloadIpcServerLinuxTests
{
    private static readonly Aria2Settings DefaultAria2Settings = Aria2Settings.FromEnvironment(static _ => null, static () => "generated-secret");

    [Test]
    public async Task Open_WhenWritableArtifactPath_ReturnsNotManagedWithoutHydration()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var root = Path.Combine(Path.GetTempPath(), "runneragent-ipc-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var inputDirectory = Path.Combine(root, "input");
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            var artifactClient = new FakeRunnerArtifactClient();
            await using var artifactService = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);
            await artifactService.StartSessionAsync(
                "session-1",
                new SessionProcessManager.SessionPaths
                {
                    HomeDirectory = root,
                    UserDirectory = root,
                    OutputDirectory = outputDirectory,
                    InputDirectory = inputDirectory,
                    TempDirectory = tempDirectory,
                    CacheDirectory = root,
                    LogsDirectory = root
                },
                CancellationToken.None);
            await artifactService.ActivateSessionAsync("session-1", "editor-current", "owner-1", CancellationToken.None);

            var outputPath = Path.Combine(outputDirectory, "new-output.png");
            var aria = new CountingAria2Client(outputPath);
            var downloadService = new ModelDownloadService(aria, DefaultAria2Settings);
            downloadService.SetActiveSession("session-1");
            await using var workspaceMirrorService = new WorkspaceMirrorService(
                new FakeRunnerWorkspaceClient(),
                runnerSecret: "runner-secret-value",
                NullLogger<WorkspaceMirrorService>.Instance);
            var socketPath = Path.Combine(root, "model-fetch.sock");
            await using var ipcServer = new ModelDownloadIpcServer(
                socketPath,
                downloadService,
                artifactService,
                workspaceMirrorService);
            ipcServer.Start();

            using var client = await ConnectAsync(socketPath);
            await VfsIpcProtocol.WriteRequestAsync(
                client,
                VfsIpcRequest.Open(outputPath, "session-1", writeAccess: true),
                CancellationToken.None);
            var response = await VfsIpcProtocol.ReadResponseAsync(client, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(response, Is.Not.Null);
                Assert.That(response!.Value.Status, Is.EqualTo(VfsIpcStatus.NotManaged));
                Assert.That(aria.AddUriCallCount, Is.EqualTo(0));
                Assert.That(File.Exists(outputPath), Is.False);
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task Open_WhenWritableRegisteredRangeModel_ReturnsReadOnlyAndReleasesLease()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var root = Path.Combine(Path.GetTempPath(), "runneragent-ipc-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            var modelPath = Path.Combine(modelsRoot, "protected.bin");
            var settings = Aria2Settings.FromEnvironment(
                name => name switch
                {
                    "ARIA2_DOWNLOAD_DIR" => modelsRoot,
                    "ARIA2_STATE_DIR" => Path.Combine(root, "state"),
                    "ARIA2_SESSION_FILE" => Path.Combine(root, "state", "session.txt"),
                    _ => null
                },
                static () => "generated-secret");
            await using var downloadService = new ModelDownloadService(
                new RangeManagedBackend(),
                settings,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [modelsRoot]));
            await downloadService.InitializeAsync(CancellationToken.None);
            downloadService.SetActiveSession("session-1");
            downloadService.SeedDownloads(
                "session-1",
                [
                    new ModelDownloadRequest
                    {
                        ModelId = "protected-model",
                        Filename = "protected.bin",
                        DestinationPath = modelPath,
                        SizeBytes = 8,
                        SourceUrl = "https://models.example.test/protected.bin"
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
            var socketPath = Path.Combine(root, "model-fetch.sock");
            await using var ipcServer = new ModelDownloadIpcServer(
                socketPath,
                downloadService,
                artifactService,
                workspaceMirrorService);
            ipcServer.Start();

            using var client = await ConnectAsync(socketPath);
            await VfsIpcProtocol.WriteRequestAsync(
                client,
                VfsIpcRequest.Open(modelPath, "session-1", writeAccess: true),
                CancellationToken.None);
            var response = await VfsIpcProtocol.ReadResponseAsync(client, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(response, Is.Not.Null);
                Assert.That(response!.Value.Status, Is.EqualTo(VfsIpcStatus.ReadOnly));
                Assert.That(new FileInfo(modelPath).Length, Is.EqualTo(8));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task EnsurePath_WhenArtifactPathUnhandled_ReturnsEnsureFailedWithoutModelFallback()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var root = Path.Combine(Path.GetTempPath(), "runneragent-ipc-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var inputDirectory = Path.Combine(root, "input");
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            var artifactClient = new FakeRunnerArtifactClient();
            await using var artifactService = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);
            await artifactService.StartSessionAsync(
                "session-1",
                new SessionProcessManager.SessionPaths
                {
                    HomeDirectory = root,
                    UserDirectory = root,
                    OutputDirectory = outputDirectory,
                    InputDirectory = inputDirectory,
                    TempDirectory = tempDirectory,
                    CacheDirectory = root,
                    LogsDirectory = root
                },
                CancellationToken.None);
            await artifactService.ActivateSessionAsync("session-1", "editor-current", "owner-1", CancellationToken.None);

            var staleInputPath = Path.Combine(inputDirectory, "deleted-input.png");
            await File.WriteAllTextAsync(staleInputPath, "stale-input");
            var aria = new CountingAria2Client(staleInputPath);
            var downloadService = new ModelDownloadService(aria, DefaultAria2Settings);
            downloadService.SetActiveSession("session-1");
            await using var workspaceMirrorService = new WorkspaceMirrorService(
                new FakeRunnerWorkspaceClient(),
                runnerSecret: "runner-secret-value",
                NullLogger<WorkspaceMirrorService>.Instance);
            var socketPath = Path.Combine(root, "model-fetch.sock");
            await using var ipcServer = new ModelDownloadIpcServer(socketPath, downloadService, artifactService, workspaceMirrorService);
            ipcServer.Start();

            using var client = await ConnectAsync(socketPath);
            await VfsIpcProtocol.WriteRequestAsync(
                client,
                VfsIpcRequest.Open(staleInputPath, "session-1"),
                CancellationToken.None);
            var response = await VfsIpcProtocol.ReadResponseAsync(client, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(response, Is.Not.Null);
                Assert.That(response!.Value.Status, Is.EqualTo(VfsIpcStatus.EnsureFailed));
                Assert.That(aria.AddUriCallCount, Is.EqualTo(0));
                Assert.That(new FileInfo(staleInputPath).Length, Is.EqualTo(0));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task Open_WhenSessionIdDoesNotMatchActiveSession_ReturnsIoErrorWithoutHydration()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var root = Path.Combine(Path.GetTempPath(), "runneragent-ipc-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelPath = Path.Combine(root, "models", "forged.bin");
            var aria = new CountingAria2Client(modelPath);
            var downloadService = new ModelDownloadService(aria, DefaultAria2Settings);
            downloadService.SetActiveSession("session-current");
            downloadService.RegisterDownloads(
                "session-current",
                [
                    new ModelDownloadRequest
                    {
                        ModelId = "forged-model",
                        Filename = "forged.bin",
                        DestinationPath = modelPath,
                        SizeBytes = 10,
                        SourceUrl = "https://models.example.test/forged.bin"
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
            var socketPath = Path.Combine(root, "model-fetch.sock");
            await using var ipcServer = new ModelDownloadIpcServer(
                socketPath,
                downloadService,
                artifactService,
                workspaceMirrorService);
            ipcServer.Start();

            using var client = await ConnectAsync(socketPath);
            await VfsIpcProtocol.WriteRequestAsync(
                client,
                VfsIpcRequest.Open(modelPath, "session-stale"),
                CancellationToken.None);
            var response = await VfsIpcProtocol.ReadResponseAsync(client, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(response, Is.Not.Null);
                Assert.That(response!.Value.Status, Is.EqualTo(VfsIpcStatus.IoError));
                Assert.That(aria.AddUriCallCount, Is.Zero);
                Assert.That(File.Exists(modelPath), Is.True);
                Assert.That(new FileInfo(modelPath).Length, Is.Zero);
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task Open_WhenRangeHydrationDoesNotManageRegisteredModel_UsesFullDownloadFallback()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var root = Path.Combine(Path.GetTempPath(), "runneragent-ipc-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelPath = Path.Combine(root, "models", "fallback.bin");
            var aria = new CountingAria2Client(modelPath);
            var downloadService = new ModelDownloadService(aria, DefaultAria2Settings);
            downloadService.SetActiveSession("session-1");
            downloadService.RegisterDownloads(
                "session-1",
                [
                    new ModelDownloadRequest
                    {
                        ModelId = "fallback-model",
                        Filename = "fallback.bin",
                        DestinationPath = modelPath,
                        SizeBytes = 10,
                        SourceUrl = "https://models.example.test/fallback.bin"
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
            var socketPath = Path.Combine(root, "model-fetch.sock");
            await using var ipcServer = new ModelDownloadIpcServer(
                socketPath,
                downloadService,
                artifactService,
                workspaceMirrorService);
            ipcServer.Start();

            using var client = await ConnectAsync(socketPath);
            await VfsIpcProtocol.WriteRequestAsync(
                client,
                VfsIpcRequest.Open(modelPath, "session-1"),
                CancellationToken.None);
            var response = await VfsIpcProtocol.ReadResponseAsync(client, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(response, Is.Not.Null);
                Assert.That(response!.Value.Status, Is.EqualTo(VfsIpcStatus.Success));
                Assert.That(response.Value.OpenDisposition, Is.EqualTo(VfsIpcOpenDisposition.FullReady));
                Assert.That(aria.AddUriCallCount, Is.EqualTo(1));
                Assert.That(File.ReadAllText(modelPath), Is.EqualTo("downloaded"));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<Socket> ConnectAsync(string socketPath)
    {
        Exception? last = null;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
                return socket;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(50);
            }
        }

        throw new InvalidOperationException($"Could not connect to socket '{socketPath}'.", last);
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

    private sealed class CountingAria2Client(string destinationPath) : TestModelTransferBackend
    {
        public int AddUriCallCount { get; private set; }

        public override Task<IReadOnlyList<ModelTransferHandle>> CreateAsync(
            ModelTransferCreateRequest request,
            CancellationToken cancellationToken)
        {
            if (request.SourceUris.Count > 0)
            {
                AddUriCallCount++;
            }

            File.WriteAllText(destinationPath, "downloaded");
            IReadOnlyList<ModelTransferHandle> handles = [new("gid-1")];
            return Task.FromResult(handles);
        }

        public override Task<ModelTransferSnapshot> GetStatusAsync(
            ModelTransferHandle handle,
            CancellationToken cancellationToken)
            => Task.FromResult(Snapshot(handle, "complete", 10, 10));
    }

    private sealed class RangeManagedBackend : TestModelTransferBackend
    {
        public override Task<ModelTransferSnapshot> GetStatusAsync(
            ModelTransferHandle handle,
            CancellationToken cancellationToken)
            => Task.FromException<ModelTransferSnapshot>(
                new ModelTransferStatusUnavailableException($"Transfer '{handle}' is not attached yet."));
    }
}
