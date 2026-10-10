using System.Diagnostics;
using System.Runtime.InteropServices;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.EditorRuntime.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public partial class ArtifactUploadServiceTests
{
    private static readonly byte[] SecretBytes = "outside-the-session"u8.ToArray();

    // Artifact file operations go through descriptor-relative Linux syscalls.
    [SetUp]
    public void RequireLinuxArtifactFileOperations() => LinuxTestPrerequisites.RequireLinux();

    [Test]
    public async Task TryEnsureArtifactAvailableAsync_DoesNotPublishThroughSymlinkedParentDirectory()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateSessionPaths(root);
            var outsideDirectory = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            var listResponse = new RunnerArtifactServiceListArtifactsResponse();
            listResponse.Artifacts.Add(CreateArtifact("input", "imports", "image.png"));
            var artifactClient = new FakeRunnerArtifactClient(listResponse) { ReadArtifactContent = [1, 2, 3, 4] };
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            var importsDirectory = Path.Combine(paths.InputDirectory, "imports");
            Assert.That(File.Exists(Path.Combine(importsDirectory, "image.png")), Is.True);

            Directory.Delete(importsDirectory, recursive: true);
            Directory.CreateSymbolicLink(importsDirectory, outsideDirectory);

            Assert.That(
                async () => await service.TryEnsureArtifactAvailableAsync(
                    Path.Combine(importsDirectory, "image.png"),
                    highPriority: true,
                    CancellationToken.None),
                Throws.Exception);
            Assert.That(Directory.EnumerateFileSystemEntries(outsideDirectory), Is.Empty);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task SeedSessionAsync_DoesNotTruncateThroughSymlinkedInputFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateSessionPaths(root);
            var secretPath = WriteOutsideSecret(root);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            File.CreateSymbolicLink(Path.Combine(paths.InputDirectory, "linked.png"), secretPath);
            await service.SeedSessionAsync(
                "session-1",
                "editor-1",
                "owner-1",
                [CreateArtifact("input", string.Empty, "linked.png")],
                CancellationToken.None);

            Assert.That(await File.ReadAllBytesAsync(secretPath), Is.EqualTo(SecretBytes));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task TryEnsureArtifactAvailableAsync_DoesNotClearUncatalogedInputThroughSymlink()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateSessionPaths(root);
            var secretPath = WriteOutsideSecret(root);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            var linkPath = Path.Combine(paths.InputDirectory, "stale.png");
            File.CreateSymbolicLink(linkPath, secretPath);

            var available = await service.TryEnsureArtifactAvailableAsync(linkPath, highPriority: true, CancellationToken.None);

            Assert.That(available, Is.False);
            Assert.That(await File.ReadAllBytesAsync(secretPath), Is.EqualTo(SecretBytes));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task ActivateSession_UploadsOnlyPlainRegularFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateSessionPaths(root);
            var secretPath = WriteOutsideSecret(root);
            File.CreateSymbolicLink(Path.Combine(paths.OutputDirectory, "linked.png"), secretPath);
            Directory.CreateSymbolicLink(Path.Combine(paths.OutputDirectory, "linked-dir"), Path.GetDirectoryName(secretPath)!);
            CreateHardLink(secretPath, Path.Combine(paths.OutputDirectory, "hardlinked.png"));
            CreateFifo(Path.Combine(paths.OutputDirectory, "pipe.png"));
            CreateFifo(Path.Combine(paths.TempDirectory, "pipe.png"));
            using (var sparse = new FileStream(Path.Combine(paths.OutputDirectory, "huge.png"), FileMode.CreateNew))
            {
                sparse.SetLength(ArtifactUploadService.MaxUploadBytes + 1);
            }

            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "real.png"), [1, 2, 3]);

            var upload = await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(10));
            Assert.That(upload.Metadata!.Filename, Is.EqualTo("real.png"));
            Assert.That(upload.ChunkBytes, Is.EqualTo(3));

            // A rescan must still skip every non-regular or oversized entry.
            await service.RefreshSessionAsync("session-1");
            var laterUploads = new List<string>();
            while (true)
            {
                try
                {
                    var later = await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(2));
                    laterUploads.Add(later.Metadata!.Filename);
                }
                catch (TimeoutException)
                {
                    break;
                }
            }

            Assert.That(laterUploads, Is.All.EqualTo("real.png"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task StopSessionAsync_CompletesWhenUploadWorkerIgnoresCancellation()
    {
        var originalTimeout = ArtifactUploadService.StopWaitTimeout;
        ArtifactUploadService.StopWaitTimeout = TimeSpan.FromMilliseconds(200);
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateSessionPaths(root);
            var artifactClient = new HangingUploadArtifactClient();
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "stuck.png"), [1, 2, 3]);
            await artifactClient.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var stopwatch = Stopwatch.StartNew();
            await service.StopSessionAsync("session-1").WaitAsync(TimeSpan.FromSeconds(20));

            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(15)));
        }
        finally
        {
            ArtifactUploadService.StopWaitTimeout = originalTimeout;
            TryDelete(root);
        }
    }

    [Test]
    public async Task SeedSessionAsync_IgnoresArtifactNamesThatEscapeTheirRoot()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateSessionPaths(root);
            var outsidePath = Path.Combine(root, "outside.png");
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.SeedSessionAsync(
                "session-1",
                "editor-1",
                "owner-1",
                [
                    CreateArtifact("output", "..", "outside.png"),
                    CreateArtifact("output", "nested/../..", "outside.png"),
                    CreateArtifact("input", string.Empty, "../outside.png"),
                    CreateArtifact("temp", string.Empty, outsidePath),
                    CreateArtifact("output", outsidePath, "x.png"),
                    CreateArtifact("output", "kept", "kept.png")
                ],
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(outsidePath), Is.False);
                Assert.That(File.Exists(Path.Combine(paths.OutputDirectory, "kept", "kept.png")), Is.True);
                Assert.That(Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName),
                    Is.EquivalentTo(new[] { "input", "output", "temp" }));
            });
            Assert.That(
                await service.TryEnsureArtifactAvailableAsync(outsidePath, highPriority: false, CancellationToken.None),
                Is.False);
            Assert.That(artifactClient.ReadArtifactCallCount, Is.EqualTo(0));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task TryEnsureArtifactAvailableAsync_CallerCancellationDoesNotFailOtherWaiters()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateSessionPaths(root);
            var gatedReader = new GatedReadArtifactStreamReader([1, 2, 3, 4]);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                BlockingReadArtifactReader = gatedReader
            };
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.SeedSessionAsync(
                "session-1",
                "editor-1",
                "owner-1",
                [CreateArtifact("output", string.Empty, "shared.png")],
                CancellationToken.None);
            var outputPath = Path.Combine(paths.OutputDirectory, "shared.png");

            using var firstCallerCancellation = new CancellationTokenSource();
            var first = service.TryEnsureArtifactAvailableAsync(outputPath, highPriority: true, firstCallerCancellation.Token);
            await gatedReader.WaitUntilStartedAsync(TimeSpan.FromSeconds(5));
            var second = service.TryEnsureArtifactAvailableAsync(outputPath, highPriority: true, CancellationToken.None);

            await firstCallerCancellation.CancelAsync();
            Assert.That(async () => await first, Throws.InstanceOf<OperationCanceledException>());
            Assert.That(second.IsCompleted, Is.False);

            gatedReader.Release();

            Assert.That(await second.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllBytes(outputPath), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
                Assert.That(artifactClient.ReadArtifactCallCount, Is.EqualTo(1));
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task PendingUploads_StayWithinTheCapWhileTheArtifactApiStalls()
    {
        var root = CreateTempDirectory();
        var previousCap = ArtifactUploadService.MaxPendingUploads;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            ArtifactUploadService.MaxPendingUploads = 3;
            var paths = CreateSessionPaths(root);
            for (var index = 0; index < 10; index++)
            {
                File.WriteAllBytes(Path.Combine(paths.OutputDirectory, $"image-{index}.png"), [1, 2, 3]);
            }

            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                UploadGate = gate.Task
            };
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);

            Assert.That(service.GetPendingUploadCount("session-1"), Is.EqualTo(3));
            gate.TrySetResult();

            // Files dropped at the cap are found again by the rescan once the queue drains.
            var uploaded = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < 10; index++)
            {
                var upload = await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(10));
                uploaded.Add(upload.Metadata!.Filename);
            }

            Assert.That(uploaded, Has.Count.EqualTo(10));
        }
        finally
        {
            ArtifactUploadService.MaxPendingUploads = previousCap;
            gate.TrySetResult();
            TryDelete(root);
        }
    }

    [Test]
    public async Task Uploads_CarryTheRuntimeTheyWereQueuedUnder()
    {
        var root = CreateTempDirectory();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var paths = CreateSessionPaths(root);
            File.WriteAllBytes(Path.Combine(paths.OutputDirectory, "first.png"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(paths.OutputDirectory, "second.png"), [4, 5, 6]);
            var identities = new RuntimeIdentityRegistry();
            identities.Set("session-1", 7, 3, "aaaaaaaaaaaa4aaa8aaaaaaaaaaaaaaa");
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                UploadGate = gate.Task
            };
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance,
                identities);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);

            // Both files are queued; the session is replaced while the first upload stalls.
            var first = await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(10));
            identities.Set("session-1", 7, 4, "bbbbbbbbbbbb4bbb8bbbbbbbbbbbbbbb");
            gate.TrySetResult();
            var second = await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(10));

            Assert.Multiple(() =>
            {
                Assert.That(first.Metadata!.RuntimeEpoch, Is.EqualTo(3));
                Assert.That(second.Metadata!.RuntimeEpoch, Is.EqualTo(3));
                Assert.That(second.Metadata.RuntimeInstanceId, Is.EqualTo("aaaaaaaaaaaa4aaa8aaaaaaaaaaaaaaa"));
            });
        }
        finally
        {
            gate.TrySetResult();
            TryDelete(root);
        }
    }

    [Test]
    public async Task Upload_IsRepeatedWhenTheFileChangesWhileItIsSent()
    {
        var root = CreateTempDirectory();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var paths = CreateSessionPaths(root);
            var output = Path.Combine(paths.OutputDirectory, "image.png");
            File.WriteAllBytes(output, [1, 2, 3, 4]);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                UploadGate = gate.Task
            };
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);

            var first = await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(10));
            // Same size, new content and time, while the first upload awaits its response.
            File.WriteAllBytes(output, [5, 6, 7, 8]);
            File.SetLastWriteTimeUtc(output, DateTime.UtcNow.AddMinutes(1));
            gate.TrySetResult();
            var second = await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(10));

            Assert.Multiple(() =>
            {
                Assert.That(first.Metadata!.Filename, Is.EqualTo("image.png"));
                Assert.That(second.Metadata!.Filename, Is.EqualTo("image.png"));
            });
        }
        finally
        {
            gate.TrySetResult();
            TryDelete(root);
        }
    }

    private static SessionProcessManager.SessionPaths CreateSessionPaths(string root)
    {
        var paths = new SessionProcessManager.SessionPaths
        {
            HomeDirectory = root,
            UserDirectory = root,
            OutputDirectory = Path.Combine(root, "output"),
            InputDirectory = Path.Combine(root, "input"),
            TempDirectory = Path.Combine(root, "temp"),
            CacheDirectory = root,
            LogsDirectory = root
        };
        Directory.CreateDirectory(paths.OutputDirectory);
        Directory.CreateDirectory(paths.InputDirectory);
        Directory.CreateDirectory(paths.TempDirectory);
        return paths;
    }

    private static string WriteOutsideSecret(string root)
    {
        var outsideDirectory = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        var secretPath = Path.Combine(outsideDirectory, "secret.txt");
        File.WriteAllBytes(secretPath, SecretBytes);
        return secretPath;
    }

    private static void CreateFifo(string path)
    {
        if (mkfifo(path, Convert.ToUInt32("600", 8)) != 0)
        {
            throw new IOException($"mkfifo failed for '{path}' (errno={Marshal.GetLastPInvokeError()}).");
        }
    }

    private static void CreateHardLink(string existingPath, string linkPath)
    {
        if (link(existingPath, linkPath) != 0)
        {
            throw new IOException($"link failed for '{linkPath}' (errno={Marshal.GetLastPInvokeError()}).");
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string existingPath, string newPath);

    private sealed class HangingUploadArtifactClient : RunnerArtifactService.RunnerArtifactServiceClient
    {
        public TaskCompletionSource UploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override AsyncUnaryCall<RunnerArtifactServiceListArtifactsResponse> ListArtifactsAsync(
            RunnerArtifactServiceListArtifactsRequest request,
            CallOptions options)
            => new(
                Task.FromResult(new RunnerArtifactServiceListArtifactsResponse()),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });

        public override AsyncClientStreamingCall<RunnerArtifactServiceUploadArtifactRequest, RunnerArtifactServiceUploadArtifactResponse> UploadArtifact(
            CallOptions options)
        {
            UploadStarted.TrySetResult();
            return new AsyncClientStreamingCall<RunnerArtifactServiceUploadArtifactRequest, RunnerArtifactServiceUploadArtifactResponse>(
                new HangingStreamWriter(),
                new TaskCompletionSource<RunnerArtifactServiceUploadArtifactResponse>().Task,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }
    }

    // Simulates a transport write that never observes cancellation.
    private sealed class HangingStreamWriter : IClientStreamWriter<RunnerArtifactServiceUploadArtifactRequest>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(RunnerArtifactServiceUploadArtifactRequest message)
            => new TaskCompletionSource().Task;

        public Task CompleteAsync()
            => new TaskCompletionSource().Task;
    }

    /// <summary>Returns the metadata frame, then holds the content until released.</summary>
    private sealed class GatedReadArtifactStreamReader(byte[] content) : IAsyncStreamReader<RunnerArtifactServiceReadArtifactResponse>
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _frame;

        public RunnerArtifactServiceReadArtifactResponse Current { get; private set; } = default!;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            switch (_frame++)
            {
                case 0:
                    _started.TrySetResult();
                    Current = new RunnerArtifactServiceReadArtifactResponse
                    {
                        Metadata = new RunnerReadStreamMetadata
                        {
                            SizeBytes = content.LongLength,
                            ModifiedUtc = Timestamp.FromDateTime(DateTime.UtcNow)
                        }
                    };
                    return true;
                case 1:
                    await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    Current = new RunnerArtifactServiceReadArtifactResponse
                    {
                        Chunk = Google.Protobuf.ByteString.CopyFrom(content)
                    };
                    return true;
                default:
                    return false;
            }
        }

        public Task WaitUntilStartedAsync(TimeSpan timeout) => _started.Task.WaitAsync(timeout);

        public void Release() => _released.TrySetResult();
    }

    private static EditorArtifactMetadata CreateArtifact(string type, string subfolder, string filename, long sizeBytes = 4)
        => new()
        {
            EditorSid = "editor-1",
            Filename = filename,
            Type = type,
            Subfolder = subfolder,
            SizeBytes = sizeBytes,
            CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1))
        };
}
