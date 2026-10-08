using System.Runtime.InteropServices;
using Google.Protobuf.WellKnownTypes;
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
