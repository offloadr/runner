using Offloadr.Runner.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;

namespace Offloadr.Runner.Tests;

public class WorkspaceMirrorServiceTests
{
    [Test]
    public async Task SecureInputPublication_RemainsAnchoredWhenParentIsReplacedBySymlink()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Descriptor-relative workspace publication requires Linux.");
            return;
        }

        var tempRoot = CreateTempDirectory();
        try
        {
            var inputRoot = Path.Combine(tempRoot, "input");
            var originalParent = Path.Combine(inputRoot, "nested");
            var detachedParent = Path.Combine(inputRoot, "detached");
            var outsideParent = Path.Combine(tempRoot, "outside");
            Directory.CreateDirectory(originalParent);
            Directory.CreateDirectory(outsideParent);

            using var secureRoot = new LinuxSecureDirectoryRoot(inputRoot);
            using var publication = secureRoot.CreatePublication("nested/model.bin");
            await using (var destination = publication.OpenWriteStream())
            {
                await destination.WriteAsync("hydrated"u8.ToArray());
                await destination.FlushAsync();
            }

            Directory.Move(originalParent, detachedParent);
            Directory.CreateSymbolicLink(originalParent, outsideParent);
            publication.Commit();

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(Path.Combine(outsideParent, "model.bin")), Is.False);
                Assert.That(File.ReadAllText(Path.Combine(detachedParent, "model.bin")), Is.EqualTo("hydrated"));
            });
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task SecureInputPublication_IsReadableByTheSessionUser()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Descriptor-relative workspace publication requires Linux.");
            return;
        }

        var tempRoot = CreateTempDirectory();
        try
        {
            var inputRoot = Path.Combine(tempRoot, "input");
            Directory.CreateDirectory(inputRoot);

            using var secureRoot = new LinuxSecureDirectoryRoot(inputRoot);
            using var publication = secureRoot.CreatePublication("model.bin");
            await using (var destination = publication.OpenWriteStream())
            {
                await destination.WriteAsync("hydrated"u8.ToArray());
            }

            publication.Commit();

            Assert.That(
                File.GetUnixFileMode(Path.Combine(inputRoot, "model.bin")),
                Is.EqualTo(
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead |
                    UnixFileMode.OtherRead));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task PrepareSessionAsync_CleansPartialArchive_WhenDownloadFails()
    {
        const string sessionId = "session-archive-leak";
        var tempRoot = CreateTempDirectory();
        var pattern = $"workspace-seed-{sessionId}-*.zip";
        var before = Directory.EnumerateFiles(Path.GetTempPath(), pattern).OrderBy(static path => path, StringComparer.Ordinal).ToArray();

        try
        {
            var paths = CreateSessionPaths(tempRoot);
            var workspaceClient = new FakeRunnerWorkspaceClient
            {
                ThrowArchiveRead = true
            };
            await using var service = new WorkspaceMirrorService(
                workspaceClient,
                "runner-secret-value",
                NullLogger<WorkspaceMirrorService>.Instance);

            var ex = Assert.ThrowsAsync<IOException>(async () =>
                await service.PrepareSessionAsync(
                    sessionId,
                    paths,
                    "editor-1",
                    "comfyui",
                    [
                        new WorkspaceFileMetadata
                        {
                            Root = WorkspaceRoot.User,
                            RelativePath = "workflows/example.json",
                            SizeBytes = 4
                        }
                    ],
                    CancellationToken.None));

            Assert.That(ex, Is.Not.Null);

            var after = Directory.EnumerateFiles(Path.GetTempPath(), pattern).OrderBy(static path => path, StringComparer.Ordinal).ToArray();
            Assert.That(after, Is.EquivalentTo(before));
        }
        finally
        {
            TryDelete(tempRoot);
            foreach (var leftover in Directory.EnumerateFiles(Path.GetTempPath(), pattern))
            {
                TryDelete(leftover);
            }
        }
    }

    [Test]
    public async Task TryEnsureWorkspaceFileAvailableAsync_TreatsInputPathsAsCaseSensitive()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Case-sensitive mirrored input paths require a case-sensitive filesystem.");
        }

        const string sessionId = "session-case-sensitive";
        var tempRoot = CreateTempDirectory();

        try
        {
            var paths = CreateSessionPaths(tempRoot);
            var workspaceClient = new FakeRunnerWorkspaceClient
            {
                FileContentsByRelativePath = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Foo.png"] = "upper",
                    ["foo.png"] = "lower"
                }
            };
            await using var service = new WorkspaceMirrorService(
                workspaceClient,
                "runner-secret-value",
                NullLogger<WorkspaceMirrorService>.Instance);

            await service.PrepareSessionAsync(
                sessionId,
                paths,
                "editor-1",
                "comfyui",
                [
                    new WorkspaceFileMetadata
                    {
                        Root = WorkspaceRoot.Input,
                        RelativePath = "Foo.png",
                        SizeBytes = 5
                    },
                    new WorkspaceFileMetadata
                    {
                        Root = WorkspaceRoot.Input,
                        RelativePath = "foo.png",
                        SizeBytes = 5
                    }
                ],
                CancellationToken.None);

            var upperPath = Path.Combine(paths.InputDirectory, "Foo.png");
            var lowerPath = Path.Combine(paths.InputDirectory, "foo.png");

            Assert.That(await service.TryEnsureWorkspaceFileAvailableAsync(upperPath, highPriority: false, CancellationToken.None), Is.True);
            Assert.That(await service.TryEnsureWorkspaceFileAvailableAsync(lowerPath, highPriority: false, CancellationToken.None), Is.True);

            Assert.That(await File.ReadAllTextAsync(upperPath).ConfigureAwait(false), Is.EqualTo("upper"));
            Assert.That(await File.ReadAllTextAsync(lowerPath).ConfigureAwait(false), Is.EqualTo("lower"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task PrepareSessionAsync_HydratesArchiveBackedRoots_WithoutPerFileMetadata()
    {
        const string sessionId = "session-archive-roots";
        var tempRoot = CreateTempDirectory();

        try
        {
            var paths = CreateSessionPaths(tempRoot);
            var workspaceClient = new FakeRunnerWorkspaceClient();
            await using var service = new WorkspaceMirrorService(
                workspaceClient,
                "runner-secret-value",
                NullLogger<WorkspaceMirrorService>.Instance);

            await service.PrepareSessionAsync(
                sessionId,
                paths,
                "editor-1",
                "comfyui",
                [
                    new WorkspaceFileMetadata
                    {
                        Root = WorkspaceRoot.Input,
                        RelativePath = "input/example.png",
                        SizeBytes = 5
                    }
                ],
                CancellationToken.None);

            Assert.That(workspaceClient.SeedArchiveRequests, Is.EqualTo(new[] { WorkspaceRoot.User, WorkspaceRoot.CustomNodes }));
            Assert.That(File.Exists(Path.Combine(paths.InputDirectory, "input", "example.png")), Is.True);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task TryEnsureWorkspaceFileAvailableAsync_DownloadsInputFilesAddedAfterStartup()
    {
        const string sessionId = "session-live-input";
        var tempRoot = CreateTempDirectory();

        try
        {
            var paths = CreateSessionPaths(tempRoot);
            var workspaceClient = new FakeRunnerWorkspaceClient
            {
                FileContentsByRelativePath = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["late/audio.wav"] = "live-audio"
                }
            };
            await using var service = new WorkspaceMirrorService(
                workspaceClient,
                "runner-secret-value",
                NullLogger<WorkspaceMirrorService>.Instance);

            await service.PrepareSessionAsync(
                sessionId,
                paths,
                "editor-1",
                "comfyui",
                [],
                CancellationToken.None);

            var localPath = Path.Combine(paths.InputDirectory, "late", "audio.wav");
            Assert.That(await service.TryEnsureWorkspaceFileAvailableAsync(localPath, highPriority: false, CancellationToken.None), Is.True);

            Assert.That(await File.ReadAllTextAsync(localPath).ConfigureAwait(false), Is.EqualTo("live-audio"));
            Assert.That(workspaceClient.DownloadRequests.Select(static request => request.RelativePath), Is.EqualTo(new[] { "late/audio.wav" }));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task TryEnsureWorkspaceFileAvailableAsync_PreservesRemoteModifiedTimeAfterFlushing()
    {
        const string sessionId = "session-input-modified-time";
        var modifiedUtc = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var tempRoot = CreateTempDirectory();

        try
        {
            var paths = CreateSessionPaths(tempRoot);
            var workspaceClient = new FakeRunnerWorkspaceClient
            {
                FileContentsByRelativePath = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["dated/input.bin"] = "timestamped"
                },
                FileModifiedUtcByRelativePath = new Dictionary<string, DateTime>(StringComparer.Ordinal)
                {
                    ["dated/input.bin"] = modifiedUtc
                }
            };
            await using var service = new WorkspaceMirrorService(
                workspaceClient,
                "runner-secret-value",
                NullLogger<WorkspaceMirrorService>.Instance);

            await service.PrepareSessionAsync(
                sessionId,
                paths,
                "editor-1",
                "comfyui",
                [],
                CancellationToken.None);

            var localPath = Path.Combine(paths.InputDirectory, "dated", "input.bin");
            Assert.That(
                await service.TryEnsureWorkspaceFileAvailableAsync(
                    localPath,
                    highPriority: false,
                    CancellationToken.None),
                Is.True);

            Assert.That(File.GetLastWriteTimeUtc(localPath), Is.EqualTo(modifiedUtc));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task TryEnsureWorkspaceFileAvailableAsync_RejectsSymlinkedParentOutsideInputRoot()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Canonical symlink containment is enforced by the Linux runner.");
        }

        const string sessionId = "session-symlink-escape";
        var tempRoot = CreateTempDirectory();

        try
        {
            var paths = CreateSessionPaths(tempRoot);
            var outsideDirectory = Path.Combine(tempRoot, "outside");
            Directory.CreateDirectory(outsideDirectory);
            var linkedDirectory = Path.Combine(paths.InputDirectory, "link");
            Directory.CreateSymbolicLink(linkedDirectory, outsideDirectory);
            var workspaceClient = new FakeRunnerWorkspaceClient
            {
                FileContentsByRelativePath = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["link/escape.bin"] = "privileged-write"
                }
            };
            await using var service = new WorkspaceMirrorService(
                workspaceClient,
                "runner-secret-value",
                NullLogger<WorkspaceMirrorService>.Instance);

            await service.PrepareSessionAsync(
                sessionId,
                paths,
                "editor-1",
                "comfyui",
                [],
                CancellationToken.None);

            var linkedPath = Path.Combine(linkedDirectory, "escape.bin");
            var handled = await service.TryEnsureWorkspaceFileAvailableAsync(
                linkedPath,
                highPriority: false,
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(handled, Is.False);
                Assert.That(File.Exists(Path.Combine(outsideDirectory, "escape.bin")), Is.False);
                Assert.That(workspaceClient.DownloadRequests, Is.Empty);
            });
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task PrepareSessionAsync_RejectsSymlinkedInputPlaceholderParent()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Canonical symlink containment is enforced by the Linux runner.");
        }

        const string sessionId = "session-placeholder-symlink-escape";
        var tempRoot = CreateTempDirectory();

        try
        {
            var paths = CreateSessionPaths(tempRoot);
            var outsideDirectory = Path.Combine(tempRoot, "outside-placeholder");
            Directory.CreateDirectory(outsideDirectory);
            Directory.CreateSymbolicLink(
                Path.Combine(paths.InputDirectory, "link"),
                outsideDirectory);
            await using var service = new WorkspaceMirrorService(
                new FakeRunnerWorkspaceClient(),
                "runner-secret-value",
                NullLogger<WorkspaceMirrorService>.Instance);

            Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
                await service.PrepareSessionAsync(
                    sessionId,
                    paths,
                    "editor-1",
                    "comfyui",
                    [
                        new WorkspaceFileMetadata
                        {
                            Root = WorkspaceRoot.Input,
                            RelativePath = "link/escape.bin",
                            SizeBytes = 16
                        }
                    ],
                    CancellationToken.None));
            Assert.That(File.Exists(Path.Combine(outsideDirectory, "escape.bin")), Is.False);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task StopSessionAsync_CancelsInFlightWorkspaceDownloads()
    {
        const string sessionId = "session-cancel-download";
        var tempRoot = CreateTempDirectory();

        try
        {
            var paths = CreateSessionPaths(tempRoot);
            var blockingStream = new BlockingResponseStreamReader();
            var workspaceClient = new FakeRunnerWorkspaceClient
            {
                BlockingInputReader = blockingStream
            };
            await using var service = new WorkspaceMirrorService(
                workspaceClient,
                "runner-secret-value",
                NullLogger<WorkspaceMirrorService>.Instance);

            await service.PrepareSessionAsync(
                sessionId,
                paths,
                "editor-1",
                "comfyui",
                [
                    new WorkspaceFileMetadata
                    {
                        Root = WorkspaceRoot.Input,
                        RelativePath = "audio.wav",
                        SizeBytes = 5
                    }
                ],
                CancellationToken.None);

            var localPath = Path.Combine(paths.InputDirectory, "audio.wav");
            var ensureTask = service.TryEnsureWorkspaceFileAvailableAsync(localPath, highPriority: false, CancellationToken.None);

            await blockingStream.ReadStarted.Task.ConfigureAwait(false);
            await service.StopSessionAsync(sessionId).ConfigureAwait(false);

            Assert.That(await ensureTask.ConfigureAwait(false), Is.False);
            Assert.That(new FileInfo(localPath).Length, Is.EqualTo(0));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    private static SessionProcessManager.SessionPaths CreateSessionPaths(string root)
    {
        var paths = new SessionProcessManager.SessionPaths
        {
            HomeDirectory = root,
            UserDirectory = Path.Combine(root, "user"),
            CustomNodesDirectory = Path.Combine(root, "custom_nodes"),
            OutputDirectory = Path.Combine(root, "output"),
            InputDirectory = Path.Combine(root, "input"),
            TempDirectory = Path.Combine(root, "tmp"),
            CacheDirectory = Path.Combine(root, ".cache"),
            LogsDirectory = Path.Combine(root, "logs")
        };

        Directory.CreateDirectory(paths.UserDirectory);
        Directory.CreateDirectory(paths.CustomNodesDirectory);
        Directory.CreateDirectory(paths.OutputDirectory);
        Directory.CreateDirectory(paths.InputDirectory);
        Directory.CreateDirectory(paths.TempDirectory);
        Directory.CreateDirectory(paths.CacheDirectory);
        Directory.CreateDirectory(paths.LogsDirectory);
        return paths;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"workspace-mirror-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }

    private static byte[] CreateEmptyZipBytes()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
        }

        return stream.ToArray();
    }

    private sealed class FakeRunnerWorkspaceClient : RunnerWorkspaceService.RunnerWorkspaceServiceClient
    {
        public byte[] SeedArchiveBytes { get; init; } = CreateEmptyZipBytes();
        public IReadOnlyDictionary<string, string> FileContentsByRelativePath { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, DateTime> FileModifiedUtcByRelativePath { get; init; } = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        public bool ThrowArchiveRead { get; init; }
        public BlockingResponseStreamReader? BlockingInputReader { get; init; }
        public List<WorkspaceRoot> SeedArchiveRequests { get; } = [];
        public List<RunnerWorkspaceServiceReadWorkspaceFileRequest> DownloadRequests { get; } = [];

        public override AsyncServerStreamingCall<RunnerWorkspaceServiceReadWorkspaceSeedArchiveResponse> ReadWorkspaceSeedArchive(RunnerWorkspaceServiceReadWorkspaceSeedArchiveRequest request, CallOptions options)
        {
            SeedArchiveRequests.Add(request.Root);
            IAsyncStreamReader<RunnerWorkspaceServiceReadWorkspaceSeedArchiveResponse> responseStream = ThrowArchiveRead
                ? new ThrowingArchiveResponseStreamReader()
                : new SequenceAsyncStreamReader<RunnerWorkspaceServiceReadWorkspaceSeedArchiveResponse>(
                    [
                        new RunnerWorkspaceServiceReadWorkspaceSeedArchiveResponse
                        {
                            Metadata = new RunnerReadStreamMetadata
                            {
                                ContentType = "application/zip",
                                SizeBytes = SeedArchiveBytes.LongLength
                            }
                        },
                        new RunnerWorkspaceServiceReadWorkspaceSeedArchiveResponse
                        {
                            Chunk = Google.Protobuf.ByteString.CopyFrom(SeedArchiveBytes)
                        }
                    ]);
            return new AsyncServerStreamingCall<RunnerWorkspaceServiceReadWorkspaceSeedArchiveResponse>(
                responseStream,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public override AsyncServerStreamingCall<RunnerWorkspaceServiceReadWorkspaceFileResponse> ReadWorkspaceFile(RunnerWorkspaceServiceReadWorkspaceFileRequest request, CallOptions options)
        {
            DownloadRequests.Add(request);
            IAsyncStreamReader<RunnerWorkspaceServiceReadWorkspaceFileResponse> responseStream;
            if (BlockingInputReader is not null)
            {
                responseStream = BlockingInputReader;
            }
            else if (FileContentsByRelativePath.TryGetValue(request.RelativePath, out var content))
            {
                var metadata = new RunnerReadStreamMetadata
                {
                    SizeBytes = content.Length
                };
                if (FileModifiedUtcByRelativePath.TryGetValue(request.RelativePath, out var modifiedUtc))
                {
                    metadata.ModifiedUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(
                        modifiedUtc.ToUniversalTime());
                }

                responseStream = new SequenceAsyncStreamReader<RunnerWorkspaceServiceReadWorkspaceFileResponse>(
                    [
                        new RunnerWorkspaceServiceReadWorkspaceFileResponse
                        {
                            Metadata = metadata
                        },
                        new RunnerWorkspaceServiceReadWorkspaceFileResponse
                        {
                            Chunk = Google.Protobuf.ByteString.CopyFromUtf8(content)
                        }
                    ]);
            }
            else
            {
                responseStream = new SequenceAsyncStreamReader<RunnerWorkspaceServiceReadWorkspaceFileResponse>([]);
            }

            return new AsyncServerStreamingCall<RunnerWorkspaceServiceReadWorkspaceFileResponse>(
                responseStream,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }
    }

    private sealed class SequenceAsyncStreamReader<T>(IReadOnlyList<T> messages) : IAsyncStreamReader<T>
    {
        private int _index = -1;

        public T Current { get; private set; } = default!;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _index++;
            if (_index >= messages.Count)
            {
                return Task.FromResult(false);
            }

            Current = messages[_index];
            return Task.FromResult(true);
        }
    }

    private sealed class BlockingResponseStreamReader : IAsyncStreamReader<RunnerWorkspaceServiceReadWorkspaceFileResponse>
    {
        private int _index = -1;
        public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RunnerWorkspaceServiceReadWorkspaceFileResponse Current { get; private set; } = default!;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            _index++;
            if (_index == 0)
            {
                Current = new RunnerWorkspaceServiceReadWorkspaceFileResponse
                {
                    Metadata = new RunnerReadStreamMetadata
                    {
                        SizeBytes = 5
                    }
                };
                return true;
            }

            if (_index == 1)
            {
                ReadStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            return false;
        }
    }

    private sealed class ThrowingArchiveResponseStreamReader : IAsyncStreamReader<RunnerWorkspaceServiceReadWorkspaceSeedArchiveResponse>
    {
        private int _index = -1;

        public RunnerWorkspaceServiceReadWorkspaceSeedArchiveResponse Current { get; private set; } = default!;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _index++;
            switch (_index)
            {
                case 0:
                    Current = new RunnerWorkspaceServiceReadWorkspaceSeedArchiveResponse
                    {
                        Metadata = new RunnerReadStreamMetadata
                        {
                            ContentType = "application/zip",
                            SizeBytes = 4
                        }
                    };
                    return Task.FromResult(true);
                case 1:
                    Current = new RunnerWorkspaceServiceReadWorkspaceSeedArchiveResponse
                    {
                        Chunk = Google.Protobuf.ByteString.CopyFrom([0x50])
                    };
                    return Task.FromResult(true);
                case 2:
                    return Task.FromException<bool>(new IOException("simulated stream failure"));
                default:
                    return Task.FromResult(false);
            }
        }
    }
}
