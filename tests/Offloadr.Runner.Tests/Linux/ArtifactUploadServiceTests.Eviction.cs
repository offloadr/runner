using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public partial class ArtifactUploadServiceTests
{
    [Test]
    public async Task EvictUploadedArtifacts_ReplacesAnUploadedOutputAndDownloadsItAgainOnOpen()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true);
            var content = "uploaded-output-bytes"u8.ToArray();
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                ReadArtifactContent = content,
            };
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);

            var outputPath = Path.Combine(paths.OutputDirectory, "ComfyUI_00001_.png");
            await File.WriteAllBytesAsync(outputPath, content);
            await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await using var openBeforeEviction = new FileStream(outputPath, FileMode.Open, FileAccess.Read);

            var released = await EvictWhenRecordedAsync(service, "session-1");

            Assert.That(released, Is.EqualTo(content.Length));
            Assert.That(new FileInfo(outputPath).Length, Is.Zero);
            // A reader that had it open still sees the uploaded bytes.
            var stillOpen = new byte[content.Length];
            await openBeforeEviction.ReadExactlyAsync(stillOpen);
            Assert.That(stillOpen, Is.EqualTo(content));

            Assert.That(await service.TryEnsureArtifactAvailableAsync(outputPath, highPriority: true, CancellationToken.None), Is.True);
            Assert.That(await File.ReadAllBytesAsync(outputPath), Is.EqualTo(content));
            Assert.That(artifactClient.LastReadArtifactRequest!.Filename, Is.EqualTo("ComfyUI_00001_.png"));
            // Give the watcher time to see the placeholder and the download.
            await Task.Delay(500);
            Assert.That(artifactClient.UploadCount, Is.EqualTo(1), "neither the placeholder nor the download is uploaded");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task EvictUploadedArtifacts_LeavesUnsealedChangedAndRecentArtifacts()
    {
        var root = CreateTempDirectory();
        try
        {
            var unsealedPaths = CreateEvictionPaths(Path.Combine(root, "unsealed"), sealedSession: false);
            var sealedPaths = CreateEvictionPaths(Path.Combine(root, "sealed"), sealedSession: true);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("unsealed", unsealedPaths, CancellationToken.None);
            await service.ActivateSessionAsync("unsealed", "editor-1", "owner-1", CancellationToken.None);
            var unsealedOutput = Path.Combine(unsealedPaths.OutputDirectory, "a.png");
            await File.WriteAllBytesAsync(unsealedOutput, [1, 2, 3]);
            await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(200);

            Assert.That(service.EvictUploadedArtifacts("unsealed", long.MaxValue, TimeSpan.Zero), Is.Zero);
            Assert.That(new FileInfo(unsealedOutput).Length, Is.EqualTo(3));
            await service.StopSessionAsync("unsealed");

            await service.StartSessionAsync("sealed", sealedPaths, CancellationToken.None);
            await service.ActivateSessionAsync("sealed", "editor-2", "owner-1", CancellationToken.None);
            var sealedOutput = Path.Combine(sealedPaths.OutputDirectory, "b.png");
            await File.WriteAllBytesAsync(sealedOutput, [1, 2, 3]);
            await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(200);

            Assert.That(service.EvictUploadedArtifacts("sealed", long.MaxValue, TimeSpan.FromHours(1)), Is.Zero, "too recent");
            await File.WriteAllBytesAsync(sealedOutput, [1, 2, 3, 4]);
            Assert.That(service.EvictUploadedArtifacts("sealed", long.MaxValue, TimeSpan.Zero), Is.Zero, "changed since upload");
            Assert.That(new FileInfo(sealedOutput).Length, Is.EqualTo(4));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task StopSessionAsync_CountsAnUploadThatStopInterrupted()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true);
            var neverAccepted = new TaskCompletionSource();
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                UploadGate = neverAccepted.Task,
            };
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "late.png"), [1, 2, 3]);
            await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(5));

            var unsaved = await service.StopSessionAsync("session-1");

            Assert.That(unsaved, Is.EqualTo(1));
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero, "a repeated stop has nothing left");

            // A restart's new runtime stops with nothing pending: the session still lost one.
            var relaunched = CreateEvictionPaths(Path.Combine(root, "relaunched"), sealedSession: true);
            await service.StartSessionAsync("session-1", relaunched, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
            Assert.That(service.TakeUnsavedUploadCount("session-1"), Is.EqualTo(1));
            Assert.That(service.TakeUnsavedUploadCount("session-1"), Is.Zero, "taken once, for the session's stop");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task StopSessionAsync_CountsALossWhenItCannotListEveryArtifact()
    {
        var root = CreateTempDirectory();
        var enumerate = ArtifactUploadService.EnumerateArtifactFiles;
        // The listing breaks off, as on an unreadable subtree: what lies beyond is unknown.
        ArtifactUploadService.EnumerateArtifactFiles = static _ => throw new UnauthorizedAccessException("unreadable subtree");
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);

            Assert.That(await service.StopSessionAsync("session-1"), Is.GreaterThanOrEqualTo(1));
        }
        finally
        {
            ArtifactUploadService.EnumerateArtifactFiles = enumerate;
            TryDelete(root);
        }
    }

    [Test]
    public async Task UnsavedCount_BelongsToTheLifecycleThatLostTheArtifacts()
    {
        var root = CreateTempDirectory();
        try
        {
            var identities = new RuntimeIdentityRegistry();
            identities.Set("session-1", 1, 1, "instance-1");
            var paths = CreateEvictionPaths(root, sealedSession: true);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                UploadGate = new TaskCompletionSource().Task,
            };
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance, identities);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "late.png"), [1, 2, 3]);
            await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            // Lifecycle 1 exits with one artifact lost; its Stop never comes.
            Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(1));

            // A newer lifecycle of the same session id loses nothing.
            identities.Set("session-1", 2, 1, "instance-2");
            var relaunched = CreateEvictionPaths(Path.Combine(root, "next"), sealedSession: true);
            await service.StartSessionAsync("session-1", relaunched, CancellationToken.None);
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);

            Assert.That(service.TakeUnsavedUploadCount("session-1", 2), Is.Zero, "the older lifecycle's loss is not this one's");
            Assert.That(service.TakeUnsavedUploadCount("session-1", 1), Is.Zero, "and is dropped once a newer lifecycle reported");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task StopSessionAsync_ThatOverrunsCountsConservativelyAndOnlyOnce()
    {
        var root = CreateTempDirectory();
        var originalTimeout = ArtifactUploadService.StopRecordTimeout;
        var originalWait = ArtifactUploadService.StopWaitTimeout;
        ArtifactUploadService.StopRecordTimeout = TimeSpan.FromMilliseconds(100);
        ArtifactUploadService.StopWaitTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true);
            var neverAccepted = new TaskCompletionSource();
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                UploadGate = neverAccepted.Task,
            };
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "late.png"), [1, 2, 3]);
            await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(5));

            // The uploader is still draining when the stop's time is up.
            var stopped = service.StopSessionAsync("session-1");
            var finished = await Task.WhenAny(stopped, Task.Delay(TimeSpan.FromSeconds(3)));

            Assert.That(finished, Is.SameAs(stopped), "the stop does not wait for the drain");
            Assert.That(await stopped, Is.GreaterThanOrEqualTo(1));
            Assert.That(service.TakeUnsavedUploadCount("session-1"), Is.EqualTo(await stopped));

            // The uploader finishes later (after its 5 s drain and the wait for its worker); what it
            // counts then is not added again.
            await Task.Delay(TimeSpan.FromSeconds(6.5));
            Assert.That(service.TakeUnsavedUploadCount("session-1"), Is.Zero);
        }
        finally
        {
            ArtifactUploadService.StopRecordTimeout = originalTimeout;
            ArtifactUploadService.StopWaitTimeout = originalWait;
            TryDelete(root);
        }
    }

    [Test]
    public async Task SameLengthRewrite_IsNeitherEvictedNorCountedAsSaved()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            var outputPath = Path.Combine(paths.OutputDirectory, "same.png");
            await File.WriteAllBytesAsync(outputPath, "version-1"u8.ToArray());
            await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => service.EvictUploadedArtifacts("session-1", 0, TimeSpan.Zero) == 0 && artifactClient.UploadCount == 1);
            await Task.Delay(300);

            // The control plane refuses the rewrite, so it only holds version 1.
            artifactClient.RejectUploads = true;
            await File.WriteAllBytesAsync(outputPath, "version-2"u8.ToArray());
            File.SetLastWriteTimeUtc(outputPath, DateTime.UtcNow.AddSeconds(5));

            Assert.That(service.EvictUploadedArtifacts("session-1", long.MaxValue, TimeSpan.Zero), Is.Zero);
            Assert.That(await File.ReadAllBytesAsync(outputPath), Is.EqualTo("version-2"u8.ToArray()));
            Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(1), "the rewrite never reached the control plane");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task EvictUploadedArtifacts_LeavesAnArtifactTheVfsJustServed()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            var outputPath = Path.Combine(paths.OutputDirectory, "reused.png");
            await File.WriteAllBytesAsync(outputPath, [1, 2, 3]);
            await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            var idle = TimeSpan.FromMilliseconds(400);
            await Task.Delay(idle * 2);

            // The next workflow loads the output: the shim is told it is ready and opens it itself.
            Assert.That(await service.TryEnsureArtifactAvailableAsync(outputPath, highPriority: true, CancellationToken.None), Is.True);

            Assert.That(service.EvictUploadedArtifacts("session-1", long.MaxValue, idle), Is.Zero);
            Assert.That(new FileInfo(outputPath).Length, Is.EqualTo(3));
            await Task.Delay(idle * 2);
            Assert.That(service.EvictUploadedArtifacts("session-1", long.MaxValue, idle), Is.EqualTo(3));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task StopSessionAsync_CountsAnEmptyArtifactThatNeverUploaded()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse()) { RejectUploads = true };
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "empty.txt"), []);
            await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(5));

            Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(1));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    [Test]
    public async Task StopSessionAsync_ThatOverlapsADrainingStopWaitsForItsCount()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true);
            var neverAccepted = new TaskCompletionSource();
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                UploadGate = neverAccepted.Task,
            };
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "late.png"), [1, 2, 3]);
            await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(5));

            // The exit cleanup starts draining; the Stop arrives while it does.
            var exitCleanup = service.StopSessionAsync("session-1");
            var stop = service.StopSessionAsync("session-1");
            Assert.That(stop.IsCompleted, Is.False, "the overlapping stop waits for the drain");

            Assert.That(await stop, Is.EqualTo(1));
            Assert.That(await exitCleanup, Is.EqualTo(1));
            Assert.That(service.TakeUnsavedUploadCount("session-1"), Is.EqualTo(1), "recorded once, before the Stop takes it");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task KeptOutputWithNewerBytes_IsNotTakenAsTheCatalogsVersion()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true);
            var catalog = new RunnerArtifactServiceListArtifactsResponse();
            catalog.Artifacts.Add(new Offloadr.EditorRuntime.V1.EditorArtifactMetadata
            {
                EditorSid = "editor-1",
                Filename = "reused.png",
                Type = "output",
                Subfolder = string.Empty,
                SizeBytes = 3,
                CreatedUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow.AddHours(-1)),
            });
            // The control plane refuses the newer bytes, so it only holds its own older version.
            var artifactClient = new FakeRunnerArtifactClient(catalog) { RejectUploads = true };
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance);
            await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "reused.png"), "newer"u8.ToArray());
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(5));

            Assert.That(service.EvictUploadedArtifacts("session-1", long.MaxValue, TimeSpan.Zero), Is.Zero);
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(paths.OutputDirectory, "reused.png")), Is.EqualTo("newer"u8.ToArray()));
            Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(1));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task ReplacementWithTheSameLengthAndTime_IsNotTakenAsTheUploadedVersion()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            var outputPath = Path.Combine(paths.OutputDirectory, "copied.png");
            await File.WriteAllBytesAsync(outputPath, "version-1"u8.ToArray());
            await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(500);
            var uploadedTime = File.GetLastWriteTimeUtc(outputPath);

            // As cp -p and a rename would: other bytes, same length and modification time.
            artifactClient.RejectUploads = true;
            var staged = Path.Combine(root, "staged.png");
            await File.WriteAllBytesAsync(staged, "version-2"u8.ToArray());
            File.SetLastWriteTimeUtc(staged, uploadedTime);
            File.Move(staged, outputPath, overwrite: true);

            Assert.That(service.EvictUploadedArtifacts("session-1", long.MaxValue, TimeSpan.Zero), Is.Zero);
            Assert.That(await File.ReadAllBytesAsync(outputPath), Is.EqualTo("version-2"u8.ToArray()));
            Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(1));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task EmptyRewriteOfAnUploadedArtifact_CountsAsUnsavedWhenItsUploadFails()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            var outputPath = Path.Combine(paths.OutputDirectory, "cleared.txt");
            await File.WriteAllBytesAsync(outputPath, "content"u8.ToArray());
            await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(500);

            artifactClient.RejectUploads = true;
            await File.WriteAllBytesAsync(outputPath, []);

            Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(1));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task RecordedVersions_SurviveTheOwnershipPassBeforeTheEditorStarts()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true);
            var catalog = new RunnerArtifactServiceListArtifactsResponse();
            catalog.Artifacts.Add(new Offloadr.EditorRuntime.V1.EditorArtifactMetadata
            {
                EditorSid = "editor-1",
                Filename = "remote.png",
                Type = "output",
                SizeBytes = 10,
                CreatedUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow.AddHours(-1)),
            });
            var artifactClient = new FakeRunnerArtifactClient(catalog);
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            var placeholder = Path.Combine(paths.OutputDirectory, "remote.png");
            Assert.That(new FileInfo(placeholder).Length, Is.Zero);

            // What chown -R does to every file: only the change time moves.
            await Task.Delay(20);
            File.SetUnixFileMode(placeholder, File.GetUnixFileMode(placeholder));
            service.RefreshRecordedVersions("session-1");

            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero, "the placeholder still stands for the remote artifact");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<long> EvictWhenRecordedAsync(ArtifactUploadService service, string sessionId)
    {
        // The upload is recorded once the control plane's response arrives.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var released = service.EvictUploadedArtifacts(sessionId, long.MaxValue, TimeSpan.Zero);
            if (released > 0 || DateTime.UtcNow > deadline)
            {
                return released;
            }

            await Task.Delay(50);
        }
    }

    private static SessionProcessManager.SessionPaths CreateEvictionPaths(string root, bool sealedSession)
    {
        var data = sealedSession ? Path.Combine(root, "sealed") : root;
        var paths = new SessionProcessManager.SessionPaths
        {
            HomeDirectory = root,
            SealedDirectory = sealedSession ? data : null,
            UserDirectory = Path.Combine(data, "user"),
            OutputDirectory = Path.Combine(data, "output"),
            InputDirectory = Path.Combine(data, "input"),
            TempDirectory = Path.Combine(data, "temp"),
            CacheDirectory = Path.Combine(root, ".cache"),
            LogsDirectory = Path.Combine(root, "logs"),
        };
        Directory.CreateDirectory(paths.OutputDirectory);
        Directory.CreateDirectory(paths.InputDirectory);
        Directory.CreateDirectory(paths.TempDirectory);
        return paths;
    }
}
