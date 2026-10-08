using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.EditorRuntime.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public partial class ArtifactUploadServiceTests
{
    [Test]
    public async Task SeedSessionAsync_IgnoresArtifactNamesThatEscapeTheirRoot()
    {
        LinuxTestPrerequisites.RequireLinux();

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
