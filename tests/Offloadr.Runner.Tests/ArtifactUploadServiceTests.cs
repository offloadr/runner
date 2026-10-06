using Offloadr.Runner.V1;
using Offloadr.EditorRuntime.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;

namespace Offloadr.Runner.Tests;

public class ArtifactUploadServiceTests
{
    [Test]
    public async Task ActivateSession_WithCatalogPlaceholder_UploadsWhenLocalFileGetsRealContent()
    {
        var root = CreateTempDirectory();
        try
        {
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "ComfyUI_00003_.png";
            var outputPath = Path.Combine(outputDirectory, filename);
            var artifactClient = new FakeRunnerArtifactClient(
                new RunnerArtifactServiceListArtifactsResponse
                {
                    Artifacts =
                    {
                        new EditorArtifactMetadata
                        {
                            EditorSid = "editor-1",
                            Filename = filename,
                            Type = "output",
                            Subfolder = string.Empty,
                            SizeBytes = 1234,
                            CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-5))
                        }
                    }
                });

            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = root,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);

            Assert.That(File.Exists(outputPath), Is.True, "Expected zero-byte placeholder from remote catalog.");
            Assert.That(new FileInfo(outputPath).Length, Is.EqualTo(0));

            await File.WriteAllBytesAsync(outputPath, [1, 2, 3, 4, 5]);

            var upload = await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            Assert.That(upload.Metadata, Is.Not.Null);
            Assert.That(upload.Metadata!.Filename, Is.EqualTo(filename));
            Assert.That(upload.Metadata.Type, Is.EqualTo("output"));
            Assert.That(upload.Metadata.Subfolder, Is.EqualTo(string.Empty));
            Assert.That(upload.Metadata.SizeBytes, Is.EqualTo(5));
            Assert.That(upload.ChunkBytes, Is.EqualTo(5));
            Assert.That(artifactClient.LastListHeaders?.Any(h => h.Key == "x-runner-secret" && h.Value == "runner-secret-value"), Is.True);
            Assert.That(artifactClient.LastUploadHeaders?.Any(h => h.Key == "x-runner-secret" && h.Value == "runner-secret-value"), Is.True);
            Assert.That(artifactClient.LastListHeaders?.Any(h => h.Key == "x-runner-token"), Is.False);
            Assert.That(artifactClient.LastUploadHeaders?.Any(h => h.Key == "x-runner-token"), Is.False);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task SeedSessionAsync_WithInitialArtifacts_CreatesPlaceholderAndRefreshesCatalog()
    {
        var root = CreateTempDirectory();
        try
        {
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "ComfyUI_00004_.png";
            var outputPath = Path.Combine(outputDirectory, filename);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());

            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = root,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.SeedSessionAsync(
                "session-1",
                "editor-1",
                "owner-1",
                [
                    new EditorArtifactMetadata
                    {
                        EditorSid = "editor-1",
                        Filename = filename,
                        Type = "output",
                        Subfolder = string.Empty,
                        SizeBytes = 44,
                        CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1))
                    }
                ],
                CancellationToken.None);

            Assert.That(File.Exists(outputPath), Is.True);
            Assert.That(new FileInfo(outputPath).Length, Is.EqualTo(0));
            Assert.That(artifactClient.ListCallCount, Is.EqualTo(1));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task SeedSessionAsync_WithEmptyInitialArtifacts_FallsBackToCatalogFetch()
    {
        var root = CreateTempDirectory();
        try
        {
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "ComfyUI_00005_.png";
            var outputPath = Path.Combine(outputDirectory, filename);
            var artifactClient = new FakeRunnerArtifactClient(
                new RunnerArtifactServiceListArtifactsResponse
                {
                    Artifacts =
                    {
                        new EditorArtifactMetadata
                        {
                            EditorSid = "editor-1",
                            Filename = filename,
                            Type = "output",
                            Subfolder = string.Empty,
                            SizeBytes = 88,
                            CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-2))
                        }
                    }
                });

            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = root,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.SeedSessionAsync("session-1", "editor-1", "owner-1", [], CancellationToken.None);

            Assert.That(artifactClient.ListCallCount, Is.EqualTo(1));
            Assert.That(File.Exists(outputPath), Is.True);
            Assert.That(new FileInfo(outputPath).Length, Is.EqualTo(0));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task SeedSessionAsync_UsesActiveEditorSidForLazyArtifactFetch()
    {
        var root = CreateTempDirectory();
        try
        {
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "ComfyUI_00006_.png";
            var outputPath = Path.Combine(outputDirectory, filename);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = root,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.SeedSessionAsync(
                "session-1",
                "editor-current",
                "owner-1",
                [
                    new EditorArtifactMetadata
                    {
                        EditorSid = "editor-stale",
                        Filename = filename,
                        Type = "output",
                        Subfolder = string.Empty,
                        SizeBytes = 4,
                        CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1))
                    }
                ],
                CancellationToken.None);

            var available = await service.TryEnsureArtifactAvailableAsync(outputPath, highPriority: false, CancellationToken.None);

            Assert.That(available, Is.True);
            Assert.That(artifactClient.LastReadArtifactRequest, Is.Not.Null);
            Assert.That(artifactClient.LastReadArtifactRequest!.EditorSid, Is.EqualTo("editor-current"));
            Assert.That(new FileInfo(outputPath).Length, Is.EqualTo(4));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task SeedSessionAsync_WithInitialInputArtifact_CreatesPlaceholderAndLazyFetchesFromInputRoot()
    {
        var root = CreateTempDirectory();
        try
        {
            var inputDirectory = Path.Combine(root, "input");
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "uploaded-input.png";
            var inputPath = Path.Combine(inputDirectory, "imports", filename);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = inputDirectory,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.SeedSessionAsync(
                "session-1",
                "editor-current",
                "owner-1",
                [
                    new EditorArtifactMetadata
                    {
                        EditorSid = "editor-current",
                        Filename = filename,
                        Type = "input",
                        Subfolder = "imports",
                        SizeBytes = 4,
                        CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1))
                    }
                ],
                CancellationToken.None);

            Assert.That(File.Exists(inputPath), Is.True);
            Assert.That(new FileInfo(inputPath).Length, Is.EqualTo(0));

            var available = await service.TryEnsureArtifactAvailableAsync(inputPath, highPriority: false, CancellationToken.None);

            Assert.That(available, Is.True);
            Assert.That(artifactClient.LastReadArtifactRequest, Is.Not.Null);
            Assert.That(artifactClient.LastReadArtifactRequest!.EditorSid, Is.EqualTo("editor-current"));
            Assert.That(artifactClient.LastReadArtifactRequest.Type, Is.EqualTo("input"));
            Assert.That(artifactClient.LastReadArtifactRequest.Subfolder, Is.EqualTo("imports"));
            Assert.That(new FileInfo(inputPath).Length, Is.EqualTo(4));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task CapturedPromptRefreshCannotSelectAReplacementUploaderWithTheSameSessionId()
    {
        var root = CreateTempDirectory();
        try
        {
            var client = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());
            await using var service = new ArtifactUploadService("runner-secret-value", client, NullLogger<ArtifactUploadService>.Instance);
            SessionProcessManager.SessionPaths Paths(string suffix)
            {
                var path = Path.Combine(root, suffix);
                foreach (var directory in new[] { "input", "output", "temp" }) Directory.CreateDirectory(Path.Combine(path, directory));
                return new()
                {
                    HomeDirectory = path,
                    UserDirectory = path,
                    InputDirectory = Path.Combine(path, "input"),
                    OutputDirectory = Path.Combine(path, "output"),
                    TempDirectory = Path.Combine(path, "temp"),
                    CacheDirectory = path,
                    LogsDirectory = path
                };
            }
            await service.StartSessionAsync("session", Paths("old"), CancellationToken.None);
            await service.ActivateSessionAsync("session", "old-editor", "alice", CancellationToken.None);
            var oldRefresh = service.CaptureSessionRefresh("session");
            await service.StopSessionAsync("session");
            await service.StartSessionAsync("session", Paths("new"), CancellationToken.None);
            await service.ActivateSessionAsync("session", "new-editor", "alice", CancellationToken.None);
            var callsBeforeOldRefresh = client.ListCallCount;
            await oldRefresh();
            Assert.That(client.ListCallCount, Is.EqualTo(callsBeforeOldRefresh));
            await service.CaptureSessionRefresh("session")();
            Assert.That(client.ListCallCount, Is.EqualTo(callsBeforeOldRefresh + 1));
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task RefreshSessionAsync_ReloadsCatalogForInputArtifactsUploadedAfterActivation()
    {
        var root = CreateTempDirectory();
        try
        {
            var inputDirectory = Path.Combine(root, "input");
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "late-upload.png";
            var inputPath = Path.Combine(inputDirectory, filename);
            var listResponse = new RunnerArtifactServiceListArtifactsResponse();
            var artifactClient = new FakeRunnerArtifactClient(listResponse);
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = inputDirectory,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-current", "owner-1", CancellationToken.None);

            listResponse.Artifacts.Add(new EditorArtifactMetadata
            {
                EditorSid = "editor-current",
                Filename = filename,
                Type = "input",
                Subfolder = string.Empty,
                SizeBytes = 4,
                CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1))
            });

            await service.RefreshSessionAsync("session-1");

            Assert.That(artifactClient.ListCallCount, Is.EqualTo(2));
            Assert.That(File.Exists(inputPath), Is.True);
            Assert.That(new FileInfo(inputPath).Length, Is.EqualTo(0));

            var available = await service.TryEnsureArtifactAvailableAsync(inputPath, highPriority: false, CancellationToken.None);

            Assert.That(available, Is.True);
            Assert.That(artifactClient.LastReadArtifactRequest, Is.Not.Null);
            Assert.That(artifactClient.LastReadArtifactRequest!.Type, Is.EqualTo("input"));
            Assert.That(new FileInfo(inputPath).Length, Is.EqualTo(4));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RefreshSessionAsync_WhenInputArtifactReplacedWithSameSize_InvalidatesLocalBytes()
    {
        var root = CreateTempDirectory();
        try
        {
            var inputDirectory = Path.Combine(root, "input");
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "same-name.png";
            var inputPath = Path.Combine(inputDirectory, filename);
            var oldBytes = "old!"u8.ToArray();
            var newBytes = "new!"u8.ToArray();
            var oldCommittedUtc = DateTime.UtcNow.AddMinutes(-5);
            var newCommittedUtc = DateTime.UtcNow.AddMinutes(-1);
            var listResponse = new RunnerArtifactServiceListArtifactsResponse();
            listResponse.Artifacts.Add(new EditorArtifactMetadata
            {
                EditorSid = "editor-current",
                Filename = filename,
                Type = "input",
                Subfolder = string.Empty,
                SizeBytes = oldBytes.Length,
                CreatedUtc = Timestamp.FromDateTime(oldCommittedUtc)
            });
            var artifactClient = new FakeRunnerArtifactClient(listResponse)
            {
                ReadArtifactContent = oldBytes,
                ReadArtifactModifiedUtc = oldCommittedUtc
            };
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = inputDirectory,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-current", "owner-1", CancellationToken.None);
            Assert.That(await service.TryEnsureArtifactAvailableAsync(inputPath, highPriority: false, CancellationToken.None), Is.True);
            Assert.That(await File.ReadAllBytesAsync(inputPath), Is.EqualTo(oldBytes));

            listResponse.Artifacts.Clear();
            listResponse.Artifacts.Add(new EditorArtifactMetadata
            {
                EditorSid = "editor-current",
                Filename = filename,
                Type = "input",
                Subfolder = string.Empty,
                SizeBytes = newBytes.Length,
                CreatedUtc = Timestamp.FromDateTime(newCommittedUtc)
            });
            artifactClient.ReadArtifactContent = newBytes;
            artifactClient.ReadArtifactModifiedUtc = newCommittedUtc;

            await service.RefreshSessionAsync("session-1");

            Assert.That(new FileInfo(inputPath).Length, Is.EqualTo(0));
            Assert.That(await service.TryEnsureArtifactAvailableAsync(inputPath, highPriority: false, CancellationToken.None), Is.True);
            Assert.That(await File.ReadAllBytesAsync(inputPath), Is.EqualTo(newBytes));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task TryEnsureArtifactAvailableAsync_ReloadsCatalogForOutputAddedAfterInitialCatalog()
    {
        var root = CreateTempDirectory();
        try
        {
            var inputDirectory = Path.Combine(root, "input");
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "late-output.png";
            var outputPath = Path.Combine(outputDirectory, filename);
            var listResponse = new RunnerArtifactServiceListArtifactsResponse();
            var artifactClient = new FakeRunnerArtifactClient(listResponse)
            {
                ReadArtifactContent = "late"u8.ToArray()
            };
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = inputDirectory,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-current", "owner-1", CancellationToken.None);
            Assert.That(artifactClient.ListCallCount, Is.EqualTo(1));

            listResponse.Artifacts.Add(new EditorArtifactMetadata
            {
                EditorSid = "editor-current",
                Filename = filename,
                Type = "output",
                Subfolder = string.Empty,
                SizeBytes = 4,
                CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1))
            });

            var available = await service.TryEnsureArtifactAvailableAsync(outputPath, highPriority: true, CancellationToken.None);

            Assert.That(available, Is.True);
            Assert.That(artifactClient.ListCallCount, Is.EqualTo(2));
            Assert.That(artifactClient.LastReadArtifactRequest, Is.Not.Null);
            Assert.That(artifactClient.LastReadArtifactRequest!.Type, Is.EqualTo("output"));
            Assert.That(artifactClient.LastReadArtifactRequest.Filename, Is.EqualTo(filename));
            Assert.That(await File.ReadAllBytesAsync(outputPath), Is.EqualTo("late"u8.ToArray()));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task TryEnsureArtifactAvailableAsync_DerivesOutputReadWhenCatalogListIsStale()
    {
        var root = CreateTempDirectory();
        try
        {
            var inputDirectory = Path.Combine(root, "input");
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "ideogram_00031_.png";
            var outputPath = Path.Combine(outputDirectory, "nested", filename);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                ReadArtifactContent = "png-bytes"u8.ToArray()
            };
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = inputDirectory,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-current", "owner-1", CancellationToken.None);

            var available = await service.TryEnsureArtifactAvailableAsync(outputPath, highPriority: true, CancellationToken.None);

            Assert.That(available, Is.True);
            Assert.That(artifactClient.ListCallCount, Is.EqualTo(2));
            Assert.That(artifactClient.LastReadArtifactRequest, Is.Not.Null);
            Assert.That(artifactClient.LastReadArtifactRequest!.EditorSid, Is.EqualTo("editor-current"));
            Assert.That(artifactClient.LastReadArtifactRequest.Type, Is.EqualTo("output"));
            Assert.That(artifactClient.LastReadArtifactRequest.Subfolder, Is.EqualTo("nested"));
            Assert.That(artifactClient.LastReadArtifactRequest.Filename, Is.EqualTo(filename));
            Assert.That(await File.ReadAllBytesAsync(outputPath), Is.EqualTo("png-bytes"u8.ToArray()));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task TryEnsureArtifactAvailableAsync_PreservesExistingUncatalogedOutputFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var inputDirectory = Path.Combine(root, "input");
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "already-local.png";
            var outputPath = Path.Combine(outputDirectory, filename);
            var localBytes = "local-output"u8.ToArray();
            await File.WriteAllBytesAsync(outputPath, localBytes);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                ReadArtifactContent = "remote-output"u8.ToArray()
            };
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = inputDirectory,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-current", "owner-1", CancellationToken.None);

            var available = await service.TryEnsureArtifactAvailableAsync(outputPath, highPriority: true, CancellationToken.None);

            Assert.That(available, Is.True);
            Assert.That(artifactClient.LastReadArtifactRequest, Is.Null);
            Assert.That(await File.ReadAllBytesAsync(outputPath), Is.EqualTo(localBytes));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task TryEnsureArtifactAvailableAsync_PreservesExistingUncatalogedInputSeedAfterCatalogRefresh()
    {
        var root = CreateTempDirectory();
        try
        {
            var inputDirectory = Path.Combine(root, "input");
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "example-seed.png";
            var inputPath = Path.Combine(inputDirectory, filename);
            var seedBytes = "bundled-seed"u8.ToArray();
            await File.WriteAllBytesAsync(inputPath, seedBytes);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                ReadArtifactContent = "remote-input"u8.ToArray()
            };
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = inputDirectory,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-current", "owner-1", CancellationToken.None);

            var available = await service.TryEnsureArtifactAvailableAsync(inputPath, highPriority: true, CancellationToken.None);

            Assert.That(available, Is.True);
            Assert.That(artifactClient.ListCallCount, Is.EqualTo(2));
            Assert.That(artifactClient.LastReadArtifactRequest, Is.Null);
            Assert.That(await File.ReadAllBytesAsync(inputPath), Is.EqualTo(seedBytes));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RegisterInputSeeds_PreservesBundledInputSeedCopiedAfterSessionStart()
    {
        var root = CreateTempDirectory();
        try
        {
            var inputDirectory = Path.Combine(root, "input");
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                ReadArtifactContent = "remote-input"u8.ToArray()
            };
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = inputDirectory,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-current", "owner-1", CancellationToken.None);

            const string filename = "late-bundled-seed.png";
            var inputPath = Path.Combine(inputDirectory, filename);
            var seedBytes = "late-bundled-seed"u8.ToArray();
            await File.WriteAllBytesAsync(inputPath, seedBytes);
            service.RegisterInputSeeds("session-1");

            var available = await service.TryEnsureArtifactAvailableAsync(inputPath, highPriority: true, CancellationToken.None);

            Assert.That(available, Is.True);
            Assert.That(artifactClient.ListCallCount, Is.EqualTo(2));
            Assert.That(artifactClient.LastReadArtifactRequest, Is.Null);
            Assert.That(await File.ReadAllBytesAsync(inputPath), Is.EqualTo(seedBytes));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task TryEnsureArtifactAvailableAsync_FailsClosedForUncatalogedInputWhenCatalogRefreshFails()
    {
        var root = CreateTempDirectory();
        try
        {
            var inputDirectory = Path.Combine(root, "input");
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "stale-local-input.png";
            var inputPath = Path.Combine(inputDirectory, filename);
            var staleBytes = "stale-local"u8.ToArray();
            await File.WriteAllBytesAsync(inputPath, staleBytes);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                ListArtifactsException = new RpcException(new Status(StatusCode.Unavailable, "catalog unavailable")),
                ReadArtifactContent = "remote-input"u8.ToArray()
            };
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = inputDirectory,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-current", "owner-1", CancellationToken.None);

            var available = await service.TryEnsureArtifactAvailableAsync(inputPath, highPriority: true, CancellationToken.None);

            Assert.That(available, Is.False);
            Assert.That(artifactClient.ListCallCount, Is.GreaterThanOrEqualTo(2));
            Assert.That(artifactClient.LastReadArtifactRequest, Is.Null);
            Assert.That(await File.ReadAllBytesAsync(inputPath), Is.EqualTo(staleBytes));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task TryEnsureArtifactAvailableAsync_WhenInputRemovedFromCatalog_FailsClosedAndClearsLocalBytes()
    {
        var root = CreateTempDirectory();
        try
        {
            var inputDirectory = Path.Combine(root, "input");
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "deleted-input.png";
            var inputPath = Path.Combine(inputDirectory, filename);
            var oldBytes = "old-input"u8.ToArray();
            var listResponse = new RunnerArtifactServiceListArtifactsResponse();
            listResponse.Artifacts.Add(new EditorArtifactMetadata
            {
                EditorSid = "editor-current",
                Filename = filename,
                Type = "input",
                Subfolder = string.Empty,
                SizeBytes = oldBytes.Length,
                CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-2))
            });
            var artifactClient = new FakeRunnerArtifactClient(listResponse)
            {
                ReadArtifactContent = oldBytes
            };
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = inputDirectory,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-current", "owner-1", CancellationToken.None);
            Assert.That(await service.TryEnsureArtifactAvailableAsync(inputPath, highPriority: true, CancellationToken.None), Is.True);
            Assert.That(await File.ReadAllBytesAsync(inputPath), Is.EqualTo(oldBytes));

            listResponse.Artifacts.Clear();
            await service.RefreshSessionAsync("session-1");

            var available = await service.TryEnsureArtifactAvailableAsync(inputPath, highPriority: true, CancellationToken.None);

            Assert.That(available, Is.False);
            Assert.That(artifactClient.ReadArtifactCallCount, Is.EqualTo(1));
            Assert.That(new FileInfo(inputPath).Length, Is.EqualTo(0));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task TryEnsureArtifactAvailableAsync_FailsClosedForMissingInputArtifactPlaceholder()
    {
        var root = CreateTempDirectory();
        try
        {
            var inputDirectory = Path.Combine(root, "input");
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "uploaded-input.png";
            var inputPath = Path.Combine(inputDirectory, filename);
            await File.WriteAllBytesAsync(inputPath, []);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                ReadArtifactException = new RpcException(new Status(StatusCode.NotFound, "artifact not found"))
            };
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = inputDirectory,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-current", "owner-1", CancellationToken.None);

            var available = await service.TryEnsureArtifactAvailableAsync(inputPath, highPriority: true, CancellationToken.None);

            Assert.That(available, Is.False);
            Assert.That(artifactClient.LastReadArtifactRequest, Is.Null);
            Assert.That(new FileInfo(inputPath).Length, Is.EqualTo(0));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task TryEnsureArtifactAvailableAsync_RejectsArtifactStreamSizeMismatch()
    {
        var root = CreateTempDirectory();
        try
        {
            var inputDirectory = Path.Combine(root, "input");
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "wrong-size.png";
            var inputPath = Path.Combine(inputDirectory, filename);
            var listResponse = new RunnerArtifactServiceListArtifactsResponse();
            listResponse.Artifacts.Add(new EditorArtifactMetadata
            {
                EditorSid = "editor-current",
                Filename = filename,
                Type = "input",
                Subfolder = string.Empty,
                SizeBytes = 64,
                CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1))
            });
            var artifactClient = new FakeRunnerArtifactClient(listResponse)
            {
                ReadArtifactContent = "short"u8.ToArray(),
                ReadArtifactMetadataSizeBytes = 64
            };
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = inputDirectory,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-current", "owner-1", CancellationToken.None);

            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await service.TryEnsureArtifactAvailableAsync(inputPath, highPriority: true, CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain("Artifact stream size mismatch"));
            Assert.That(new FileInfo(inputPath).Length, Is.EqualTo(0));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task TryEnsureArtifactAvailableAsync_RejectsArtifactStreamShaMismatch()
    {
        var root = CreateTempDirectory();
        try
        {
            var inputDirectory = Path.Combine(root, "input");
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "wrong-sha.png";
            var inputPath = Path.Combine(inputDirectory, filename);
            var body = "valid-size"u8.ToArray();
            var listResponse = new RunnerArtifactServiceListArtifactsResponse();
            listResponse.Artifacts.Add(new EditorArtifactMetadata
            {
                EditorSid = "editor-current",
                Filename = filename,
                Type = "input",
                Subfolder = string.Empty,
                SizeBytes = body.Length,
                CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1))
            });
            var artifactClient = new FakeRunnerArtifactClient(listResponse)
            {
                ReadArtifactContent = body,
                ReadArtifactSha256 = new string('0', 64)
            };
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = inputDirectory,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-current", "owner-1", CancellationToken.None);

            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await service.TryEnsureArtifactAvailableAsync(inputPath, highPriority: true, CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain("SHA-256 mismatch"));
            Assert.That(new FileInfo(inputPath).Length, Is.EqualTo(0));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task TryEnsureArtifactAvailableAsync_HydratesExistingUncatalogedPlaceholder()
    {
        var root = CreateTempDirectory();
        try
        {
            var inputDirectory = Path.Combine(root, "input");
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "placeholder-output.png";
            var outputPath = Path.Combine(outputDirectory, filename);
            await File.WriteAllBytesAsync(outputPath, []);
            var remoteBytes = "remote-output"u8.ToArray();
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                ReadArtifactContent = remoteBytes
            };
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = inputDirectory,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-current", "owner-1", CancellationToken.None);

            var available = await service.TryEnsureArtifactAvailableAsync(outputPath, highPriority: true, CancellationToken.None);

            Assert.That(available, Is.True);
            Assert.That(artifactClient.LastReadArtifactRequest, Is.Not.Null);
            Assert.That(artifactClient.LastReadArtifactRequest!.Type, Is.EqualTo("output"));
            Assert.That(artifactClient.LastReadArtifactRequest.Filename, Is.EqualTo(filename));
            Assert.That(await File.ReadAllBytesAsync(outputPath), Is.EqualTo(remoteBytes));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task TryEnsureArtifactAvailableAsync_ReloadsCatalogBeforeTrustingExistingInputFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var inputDirectory = Path.Combine(root, "input");
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "uploaded-input.png";
            var inputPath = Path.Combine(inputDirectory, filename);
            await File.WriteAllBytesAsync(inputPath, "stale-input"u8.ToArray());
            var remoteBytes = "fresh-input"u8.ToArray();
            var listResponse = new RunnerArtifactServiceListArtifactsResponse();
            var artifactClient = new FakeRunnerArtifactClient(listResponse)
            {
                ReadArtifactContent = remoteBytes
            };
            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = inputDirectory,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-current", "owner-1", CancellationToken.None);
            Assert.That(artifactClient.ListCallCount, Is.EqualTo(1));

            listResponse.Artifacts.Add(new EditorArtifactMetadata
            {
                EditorSid = "editor-current",
                Filename = filename,
                Type = "input",
                Subfolder = string.Empty,
                SizeBytes = remoteBytes.Length,
                CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1))
            });

            var available = await service.TryEnsureArtifactAvailableAsync(inputPath, highPriority: true, CancellationToken.None);

            Assert.That(available, Is.True);
            Assert.That(artifactClient.ListCallCount, Is.EqualTo(2));
            Assert.That(artifactClient.LastReadArtifactRequest, Is.Not.Null);
            Assert.That(artifactClient.LastReadArtifactRequest!.Type, Is.EqualTo("input"));
            Assert.That(artifactClient.LastReadArtifactRequest.Filename, Is.EqualTo(filename));
            Assert.That(await File.ReadAllBytesAsync(inputPath), Is.EqualTo(remoteBytes));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task StopSessionAsync_CancelsBlockedArtifactRead()
    {
        var root = CreateTempDirectory();
        try
        {
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "ComfyUI_blocked_.png";
            var outputPath = Path.Combine(outputDirectory, filename);
            var blockingReader = new BlockingReadArtifactStreamReader();
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                BlockingReadArtifactReader = blockingReader
            };

            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = root,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.SeedSessionAsync(
                "session-1",
                "editor-1",
                "owner-1",
                [
                    new EditorArtifactMetadata
                    {
                        EditorSid = "editor-1",
                        Filename = filename,
                        Type = "output",
                        Subfolder = string.Empty,
                        SizeBytes = 4,
                        CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1))
                    }
                ],
                CancellationToken.None);

            var ensureTask = service.TryEnsureArtifactAvailableAsync(outputPath, highPriority: true, CancellationToken.None);
            await blockingReader.WaitUntilStartedAsync(TimeSpan.FromSeconds(3));

            var stopTask = service.StopSessionAsync("session-1");
            var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false);
            if (!ReferenceEquals(completed, stopTask))
            {
                Assert.Fail("Timed out waiting for StopSessionAsync to cancel a blocked artifact read.");
            }

            await stopTask.ConfigureAwait(false);
            Assert.That(await ensureTask.ConfigureAwait(false), Is.False);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task SeedSessionAsync_RefreshesCatalogForArtifactsMissingFromSnapshot()
    {
        var root = CreateTempDirectory();
        try
        {
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string snapshotFilename = "ComfyUI_00007_.png";
            const string catalogFilename = "ComfyUI_00008_.png";
            var catalogOutputPath = Path.Combine(outputDirectory, catalogFilename);
            var artifactClient = new FakeRunnerArtifactClient(
                new RunnerArtifactServiceListArtifactsResponse
                {
                    Artifacts =
                    {
                        new EditorArtifactMetadata
                        {
                            EditorSid = "editor-1",
                            Filename = catalogFilename,
                            Type = "output",
                            Subfolder = string.Empty,
                            SizeBytes = 4,
                            CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-2))
                        }
                    }
                });

            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = root,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.SeedSessionAsync(
                "session-1",
                "editor-1",
                "owner-1",
                [
                    new EditorArtifactMetadata
                    {
                        EditorSid = "editor-1",
                        Filename = snapshotFilename,
                        Type = "output",
                        Subfolder = string.Empty,
                        SizeBytes = 3,
                        CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1))
                    }
                ],
                CancellationToken.None);

            var available = await service.TryEnsureArtifactAvailableAsync(catalogOutputPath, highPriority: false, CancellationToken.None);

            Assert.That(artifactClient.ListCallCount, Is.EqualTo(1));
            Assert.That(available, Is.True);
            Assert.That(File.Exists(catalogOutputPath), Is.True);
            Assert.That(new FileInfo(catalogOutputPath).Length, Is.EqualTo(4));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task ActivateSession_UploadsTempArtifacts_WhenLocalFileAppears()
    {
        var root = CreateTempDirectory();
        try
        {
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            const string filename = "ComfyUI_temp_preview.png";
            var tempPath = Path.Combine(tempDirectory, filename);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());

            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                NullLogger<ArtifactUploadService>.Instance);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = root,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);

            await File.WriteAllBytesAsync(tempPath, [1, 2, 3, 4, 5, 6]);

            var upload = await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            Assert.That(upload.Metadata, Is.Not.Null);
            Assert.That(upload.Metadata!.Filename, Is.EqualTo(filename));
            Assert.That(upload.Metadata.Type, Is.EqualTo("temp"));
            Assert.That(upload.Metadata.Subfolder, Is.EqualTo(string.Empty));
            Assert.That(upload.Metadata.SizeBytes, Is.EqualTo(6));
            Assert.That(upload.ChunkBytes, Is.EqualTo(6));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task ActivateSession_UploadsTempArtifacts_WhenTempDirectoryIsRecreated()
    {
        var root = CreateTempDirectory();
        try
        {
            var outputDirectory = Path.Combine(root, "output");
            var tempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);

            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());
            var logger = new ListLogger<ArtifactUploadService>();
            const string filename = "ComfyUI_temp_recreated.png";
            var tempPath = Path.Combine(tempDirectory, filename);

            await using var service = new ArtifactUploadService(
                runnerSecret: "runner-secret-value",
                artifactClient,
                logger);

            var paths = new SessionProcessManager.SessionPaths
            {
                HomeDirectory = root,
                UserDirectory = root,
                OutputDirectory = outputDirectory,
                InputDirectory = root,
                TempDirectory = tempDirectory,
                CacheDirectory = root,
                LogsDirectory = root
            };

            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);

            Directory.Delete(tempDirectory, recursive: false);
            Directory.CreateDirectory(tempDirectory);
            await File.WriteAllBytesAsync(tempPath, [1, 2, 3, 4, 5, 6, 7]);

            var upload = await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            Assert.That(upload.Metadata, Is.Not.Null);
            Assert.That(upload.Metadata!.Filename, Is.EqualTo(filename));
            Assert.That(upload.Metadata.Type, Is.EqualTo("temp"));
            Assert.That(upload.ChunkBytes, Is.EqualTo(7));

            var logs = logger.Snapshot();

            Assert.That(
                logs.Any(entry =>
                    entry.Level == LogLevel.Information &&
                    entry.Message.Contains("Artifact home watcher registered", StringComparison.Ordinal) &&
                    entry.Message.Contains($"root={root}", StringComparison.Ordinal)),
                Is.True);

            Assert.That(
                logs.Count(entry =>
                    entry.Level == LogLevel.Information &&
                    entry.Message.Contains("Artifact watcher registered", StringComparison.Ordinal) &&
                    entry.Message.Contains("type=temp", StringComparison.Ordinal)),
                Is.GreaterThanOrEqualTo(1));

            Assert.That(
                logs.Any(entry =>
                    entry.Level == LogLevel.Information &&
                    entry.Message.Contains("Queued artifact upload", StringComparison.Ordinal) &&
                    entry.Message.Contains("type=temp", StringComparison.Ordinal) &&
                    entry.Message.Contains(filename, StringComparison.Ordinal)),
                Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(
                    logs.Any(entry =>
                        entry.Level == LogLevel.Debug &&
                        entry.Message.Contains("Artifact scan completed", StringComparison.Ordinal)),
                    Is.True);
                Assert.That(
                    logs.Any(entry =>
                        entry.Level == LogLevel.Information &&
                        entry.Message.Contains("Artifact scan completed", StringComparison.Ordinal)),
                    Is.False);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"artifact-upload-tests-{Guid.NewGuid():N}");
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
        }
        catch
        {
            // Best effort test cleanup.
        }
    }

    private sealed class FakeRunnerArtifactClient(RunnerArtifactServiceListArtifactsResponse listResponse) : RunnerArtifactService.RunnerArtifactServiceClient
    {
        private readonly RunnerArtifactServiceListArtifactsResponse _listResponse = listResponse;
        private readonly ConcurrentQueue<CapturedUpload> _uploads = new();
        private readonly Channel<CapturedUpload> _uploadSignals = Channel.CreateUnbounded<CapturedUpload>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
        public Metadata? LastListHeaders { get; private set; }
        public Metadata? LastUploadHeaders { get; private set; }
        public byte[] ReadArtifactContent { get; set; } = [1, 2, 3, 4];
        public DateTime ReadArtifactModifiedUtc { get; set; } = DateTime.UtcNow;
        public long? ReadArtifactMetadataSizeBytes { get; set; }
        public string? ReadArtifactSha256 { get; set; }
        public Exception? ListArtifactsException { get; init; }
        public RpcException? ReadArtifactException { get; init; }
        public BlockingReadArtifactStreamReader? BlockingReadArtifactReader { get; init; }
        public RunnerArtifactServiceReadArtifactRequest? LastReadArtifactRequest { get; private set; }
        public int ListCallCount { get; private set; }
        public int ReadArtifactCallCount { get; private set; }

        public override AsyncUnaryCall<RunnerArtifactServiceListArtifactsResponse> ListArtifactsAsync(RunnerArtifactServiceListArtifactsRequest request, CallOptions options)
        {
            ListCallCount++;
            LastListHeaders = options.Headers;
            var responseTask = ListArtifactsException is null
                ? Task.FromResult(_listResponse)
                : Task.FromException<RunnerArtifactServiceListArtifactsResponse>(ListArtifactsException);
            return new AsyncUnaryCall<RunnerArtifactServiceListArtifactsResponse>(
                responseTask,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public override AsyncClientStreamingCall<RunnerArtifactServiceUploadArtifactRequest, RunnerArtifactServiceUploadArtifactResponse> UploadArtifact(CallOptions options)
        {
            LastUploadHeaders = options.Headers;
            var response = new TaskCompletionSource<RunnerArtifactServiceUploadArtifactResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var writer = new CaptureUploadStreamWriter(captured =>
            {
                _uploads.Enqueue(captured);
                _uploadSignals.Writer.TryWrite(captured);
                response.TrySetResult(new RunnerArtifactServiceUploadArtifactResponse { Success = true });
            });

            return new AsyncClientStreamingCall<RunnerArtifactServiceUploadArtifactRequest, RunnerArtifactServiceUploadArtifactResponse>(
                writer,
                response.Task,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public override AsyncServerStreamingCall<RunnerArtifactServiceReadArtifactResponse> ReadArtifact(RunnerArtifactServiceReadArtifactRequest request, CallOptions options)
        {
            ReadArtifactCallCount++;
            LastReadArtifactRequest = request;
            if (ReadArtifactException is not null)
            {
                throw ReadArtifactException;
            }

            IAsyncStreamReader<RunnerArtifactServiceReadArtifactResponse> responseStream = BlockingReadArtifactReader is not null
                ? BlockingReadArtifactReader
                : new SequenceAsyncStreamReader<RunnerArtifactServiceReadArtifactResponse>(
                    [
                        new RunnerArtifactServiceReadArtifactResponse
                        {
                            Metadata = new RunnerReadStreamMetadata
                            {
                                SizeBytes = ReadArtifactMetadataSizeBytes ?? ReadArtifactContent.LongLength,
                                Sha256 = string.IsNullOrWhiteSpace(ReadArtifactSha256) ? null : ReadArtifactSha256,
                                ModifiedUtc = Timestamp.FromDateTime(ReadArtifactModifiedUtc)
                            }
                        },
                        new RunnerArtifactServiceReadArtifactResponse
                        {
                            Chunk = Google.Protobuf.ByteString.CopyFrom(ReadArtifactContent)
                        }
                    ]);
            return new AsyncServerStreamingCall<RunnerArtifactServiceReadArtifactResponse>(
                responseStream,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public async Task<CapturedUpload> WaitForUploadAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                return await _uploadSignals.Reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                var count = _uploads.Count;
                throw new TimeoutException($"Timed out waiting for artifact upload. Captured uploads: {count}.");
            }
        }
    }

    private sealed class CaptureUploadStreamWriter(Action<CapturedUpload> onComplete) : IClientStreamWriter<RunnerArtifactServiceUploadArtifactRequest>
    {
        private readonly Action<CapturedUpload> _onComplete = onComplete;
        private readonly object _gate = new();
        private readonly List<RunnerArtifactServiceUploadArtifactRequest> _messages = [];

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(RunnerArtifactServiceUploadArtifactRequest message)
        {
            lock (_gate)
            {
                _messages.Add(message.Clone());
            }

            return Task.CompletedTask;
        }

        public Task CompleteAsync()
        {
            RunnerArtifactServiceUploadArtifactRequest[] snapshot;
            lock (_gate)
            {
                snapshot = [.. _messages];
            }

            var metadata = snapshot.FirstOrDefault(m => m.PayloadCase == RunnerArtifactServiceUploadArtifactRequest.PayloadOneofCase.Metadata)?.Metadata;
            var chunkBytes = snapshot
                .Where(m => m.PayloadCase == RunnerArtifactServiceUploadArtifactRequest.PayloadOneofCase.Chunk)
                .Sum(m => (long)m.Chunk.Length);

            _onComplete(new CapturedUpload(metadata, chunkBytes));
            return Task.CompletedTask;
        }
    }

    private sealed record CapturedUpload(RunnerArtifactUploadMetadata? Metadata, long ChunkBytes);

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

    private sealed class BlockingReadArtifactStreamReader : IAsyncStreamReader<RunnerArtifactServiceReadArtifactResponse>
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _returnedMetadata;

        public RunnerArtifactServiceReadArtifactResponse Current { get; private set; } = default!;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_returnedMetadata)
            {
                _returnedMetadata = true;
                _started.TrySetResult();
                Current = new RunnerArtifactServiceReadArtifactResponse
                {
                    Metadata = new RunnerReadStreamMetadata
                    {
                        SizeBytes = 4,
                        ModifiedUtc = Timestamp.FromDateTime(DateTime.UtcNow)
                    }
                };
                return true;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return false;
        }

        public async Task WaitUntilStartedAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(_started.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (!ReferenceEquals(completed, _started.Task))
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:F1}s waiting for blocking artifact read.");
            }

            await _started.Task.ConfigureAwait(false);
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public ImmutableArray<LogEntry> Snapshot() => [.. _entries];

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);
}
