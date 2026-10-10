using Offloadr.Common.V1;
using Offloadr.Runner.V1;
using Microsoft.Extensions.Logging;

namespace Offloadr.Runner.Tests;

public class LocalModelProjectorTests
{
    // Projection links are created through descriptor-relative Linux syscalls.
    [SetUp]
    public void RequireLinuxProjection() => LinuxTestPrerequisites.RequireLinux();

    [Test]
    public void SetRequestedModels_RefusesSymlinkedDirectoryBelowDestinationRoot()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "local");
            var destinationRoot = Path.Combine(root, "sessions");
            var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            Directory.CreateDirectory(Path.Combine(sourceRoot, "checkpoints"));
            var sourceFile = Path.Combine(sourceRoot, "checkpoints", "demo.safetensors");
            File.WriteAllText(sourceFile, "checkpoint");
            var home = Directory.CreateDirectory(Path.Combine(destinationRoot, "home")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(home, "models"), outside);
            var projected = Path.Combine(home, "models", "checkpoints", "demo.safetensors");

            var projector = new LocalModelProjector(sourceRoot, destinationRoot, "forge-neo");
            projector.UpdateSnapshot(CreateSnapshot(CreateModel(sourceFile, ModelCategory.Checkpoint)));

            Assert.That(
                () => projector.SetRequestedModels([CreateLocalProjectionRequest(sourceFile, ModelCategory.Checkpoint, projected)]),
                Throws.InvalidOperationException.Or.InstanceOf<IOException>());
            Assert.That(Directory.EnumerateFileSystemEntries(outside), Is.Empty);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void SetRequestedModels_DoesNotRemoveLinksThroughSymlinkedDirectory()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "local");
            var destinationRoot = Path.Combine(root, "sessions");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "checkpoints"));
            var sourceFile = Path.Combine(sourceRoot, "checkpoints", "demo.safetensors");
            File.WriteAllText(sourceFile, "checkpoint");
            var modelsDirectory = Path.Combine(destinationRoot, "home", "models");
            var projected = Path.Combine(modelsDirectory, "checkpoints", "demo.safetensors");

            var projector = new LocalModelProjector(sourceRoot, destinationRoot, "forge-neo");
            projector.UpdateSnapshot(CreateSnapshot(CreateModel(sourceFile, ModelCategory.Checkpoint)));
            projector.SetRequestedModels([CreateLocalProjectionRequest(sourceFile, ModelCategory.Checkpoint, projected)]);
            Assert.That(File.Exists(projected), Is.True);

            // Replace the session-owned directory with a link to a directory holding an
            // unrelated symlink at the same relative name.
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(Path.Combine(outside, "checkpoints"));
            var unrelatedLink = Path.Combine(outside, "checkpoints", "demo.safetensors");
            File.CreateSymbolicLink(unrelatedLink, sourceFile);
            Directory.Delete(modelsDirectory, recursive: true);
            Directory.CreateSymbolicLink(modelsDirectory, outside);

            projector.SetRequestedModels([]);

            Assert.That(new FileInfo(unrelatedLink).LinkTarget, Is.EqualTo(sourceFile));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void UpdateSnapshot_DoesNotProjectUnrequestedModels()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "local");
            var destinationRoot = Path.Combine(root, "comfy", "models");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "checkpoints"));
            var sourceFile = Path.Combine(sourceRoot, "checkpoints", "demo.safetensors");
            File.WriteAllText(sourceFile, "checkpoint");

            var projector = new LocalModelProjector(sourceRoot, destinationRoot);
            projector.UpdateSnapshot(CreateSnapshot(CreateModel(sourceFile, ModelCategory.Checkpoint)));

            Assert.That(Directory.Exists(destinationRoot), Is.False);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void SetRequestedModels_ProjectsRegisteredMatchingModel()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "local");
            var destinationRoot = Path.Combine(root, "comfy", "models");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "checkpoints"));
            var sourceFile = Path.Combine(sourceRoot, "checkpoints", "demo.safetensors");
            File.WriteAllText(sourceFile, "checkpoint");
            var projected = Path.Combine(destinationRoot, "checkpoints", "demo.safetensors");

            var projector = new LocalModelProjector(sourceRoot, destinationRoot);
            projector.UpdateSnapshot(CreateSnapshot(CreateModel(sourceFile, ModelCategory.Checkpoint)));
            projector.SetRequestedModels([CreateLocalProjectionRequest(sourceFile, ModelCategory.Checkpoint, projected)]);

            Assert.That(File.Exists(projected), Is.True);
            Assert.That(ReadFileAttributes(projected).HasFlag(FileAttributes.ReparsePoint), Is.True);
            Assert.That(File.ReadAllText(projected), Is.EqualTo("checkpoint"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void SetRequestedModels_RemovesStaleManagedLinks()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "local");
            var destinationRoot = Path.Combine(root, "comfy", "models");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "checkpoints"));
            var sourceFile = Path.Combine(sourceRoot, "checkpoints", "demo.safetensors");
            File.WriteAllText(sourceFile, "checkpoint");
            var projected = Path.Combine(destinationRoot, "checkpoints", "demo.safetensors");

            var projector = new LocalModelProjector(sourceRoot, destinationRoot);
            projector.UpdateSnapshot(CreateSnapshot(CreateModel(sourceFile, ModelCategory.Checkpoint)));
            projector.SetRequestedModels([CreateLocalProjectionRequest(sourceFile, ModelCategory.Checkpoint, projected)]);

            Assert.That(File.Exists(projected), Is.True);

            projector.SetRequestedModels([]);

            Assert.That(File.Exists(projected), Is.False);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestCase("checkpoints/Foo.safetensors", "checkpoints/foo.safetensors")]
    [TestCase("checkpoints/a/model.safetensors", "checkpoints/b/model.safetensors")]
    public void SetRequestedModels_RefusesAnAmbiguousSelectionHash(string first, string second)
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "local");
            var destinationRoot = Path.Combine(root, "comfy", "models");
            var firstPath = Path.Combine(sourceRoot, first);
            var secondPath = Path.Combine(sourceRoot, second);
            Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
            // Same name (ignoring case), size and category, different contents.
            File.WriteAllText(firstPath, "aaaa");
            File.WriteAllText(secondPath, "bbbb");
            var projected = Path.Combine(destinationRoot, "checkpoints", Path.GetFileName(secondPath));

            var projector = new LocalModelProjector(sourceRoot, destinationRoot);
            projector.UpdateSnapshot(CreateSnapshot(
                CreateModel(firstPath, ModelCategory.Checkpoint),
                CreateModel(secondPath, ModelCategory.Checkpoint)));

            Assert.That(
                () => projector.SetRequestedModels([CreateLocalProjectionRequest(secondPath, ModelCategory.Checkpoint, projected)]),
                Throws.InvalidOperationException);
            Assert.That(File.Exists(projected) || new FileInfo(projected).LinkTarget is not null, Is.False);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void UpdateSnapshot_RemovesAProjectionWhoseSourceWasSwappedForALink()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "local");
            var destinationRoot = Path.Combine(root, "comfy", "models");
            var outside = Directory.CreateDirectory(Path.Combine(root, "host")).FullName;
            Directory.CreateDirectory(Path.Combine(sourceRoot, "checkpoints"));
            var source = Path.Combine(sourceRoot, "checkpoints", "demo.safetensors");
            File.WriteAllText(source, "checkpoint");
            var projected = Path.Combine(destinationRoot, "checkpoints", "demo.safetensors");
            var model = CreateModel(source, ModelCategory.Checkpoint);

            var projector = new LocalModelProjector(sourceRoot, destinationRoot);
            projector.UpdateSnapshot(CreateSnapshot(model));
            projector.SetRequestedModels([CreateLocalProjectionRequest(source, ModelCategory.Checkpoint, projected)]);
            Assert.That(new FileInfo(projected).LinkTarget, Is.Not.Null);

            // The source is replaced by a link to a file outside the local model root.
            var hostFile = Path.Combine(outside, "secret.safetensors");
            File.WriteAllText(hostFile, "secret0000");
            File.Delete(source);
            File.CreateSymbolicLink(source, hostFile);

            Assert.That(() => projector.UpdateSnapshot(CreateSnapshot(model)), Throws.InvalidOperationException);
            Assert.That(File.Exists(projected) || new FileInfo(projected).LinkTarget is not null, Is.False);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void SetRequestedModels_RefusesAModelWhoseRealFileIsOutsideTheLocalRoot()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "local");
            var destinationRoot = Path.Combine(root, "comfy", "models");
            var outside = Directory.CreateDirectory(Path.Combine(root, "host")).FullName;
            Directory.CreateDirectory(Path.Combine(sourceRoot, "checkpoints"));
            var hostFile = Path.Combine(outside, "secret.safetensors");
            File.WriteAllText(hostFile, "secret");
            var aliased = Path.Combine(sourceRoot, "checkpoints", "aliased.safetensors");
            File.CreateSymbolicLink(aliased, hostFile);
            var projected = Path.Combine(destinationRoot, "checkpoints", "aliased.safetensors");

            var projector = new LocalModelProjector(sourceRoot, destinationRoot);
            projector.UpdateSnapshot(CreateSnapshot(CreateModel(aliased, ModelCategory.Checkpoint)));

            Assert.That(
                () => projector.SetRequestedModels([CreateLocalProjectionRequest(aliased, ModelCategory.Checkpoint, projected)]),
                Throws.InvalidOperationException);
            Assert.That(File.Exists(projected) || new FileInfo(projected).LinkTarget is not null, Is.False);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void SetRequestedModels_RemovesLinksLeftByAnEarlierProcess()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "local");
            var destinationRoot = Path.Combine(root, "comfy", "models");
            var imageModels = Path.Combine(root, "image-models");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "checkpoints"));
            Directory.CreateDirectory(Path.Combine(imageModels, "vae"));
            var sourceFile = Path.Combine(sourceRoot, "checkpoints", "demo.safetensors");
            File.WriteAllText(sourceFile, "checkpoint");
            var projected = Path.Combine(destinationRoot, "checkpoints", "demo.safetensors");

            var earlier = new LocalModelProjector(sourceRoot, destinationRoot);
            earlier.UpdateSnapshot(CreateSnapshot(CreateModel(sourceFile, ModelCategory.Checkpoint)));
            earlier.SetRequestedModels([CreateLocalProjectionRequest(sourceFile, ModelCategory.Checkpoint, projected)]);
            // A link the image itself provides, into a folder that is not the local model root.
            var imageLink = Path.Combine(destinationRoot, "vae");
            Directory.CreateSymbolicLink(imageLink, Path.Combine(imageModels, "vae"));
            Assert.That(File.Exists(projected), Is.True);

            // A new agent process starts with no memory of the links it made before.
            var restarted = new LocalModelProjector(sourceRoot, destinationRoot);
            restarted.UpdateSnapshot(CreateSnapshot(CreateModel(sourceFile, ModelCategory.Checkpoint)));
            restarted.SetRequestedModels([]);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(projected) || new FileInfo(projected).LinkTarget is not null, Is.False);
                Assert.That(Directory.Exists(imageLink), Is.True);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void AddRequestedModels_DoesNotDropExistingProjection()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "local");
            var destinationRoot = Path.Combine(root, "comfy", "models");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "checkpoints"));
            Directory.CreateDirectory(Path.Combine(sourceRoot, "loras"));
            var checkpoint = Path.Combine(sourceRoot, "checkpoints", "demo.safetensors");
            var lora = Path.Combine(sourceRoot, "loras", "style.safetensors");
            File.WriteAllText(checkpoint, "checkpoint");
            File.WriteAllText(lora, "lora");
            var projectedCheckpoint = Path.Combine(destinationRoot, "checkpoints", "demo.safetensors");
            var projectedLora = Path.Combine(destinationRoot, "loras", "style.safetensors");

            var projector = new LocalModelProjector(sourceRoot, destinationRoot);
            projector.UpdateSnapshot(CreateSnapshot(
                CreateModel(checkpoint, ModelCategory.Checkpoint),
                CreateModel(lora, ModelCategory.Lora)));
            projector.SetRequestedModels([CreateLocalProjectionRequest(checkpoint, ModelCategory.Checkpoint, projectedCheckpoint)]);
            projector.AddRequestedModels([CreateLocalProjectionRequest(lora, ModelCategory.Lora, projectedLora)]);

            Assert.That(File.Exists(projectedCheckpoint), Is.True);
            Assert.That(File.Exists(projectedLora), Is.True);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void SetRequestedModels_WhenForgeNeo_UsesExplicitDestinationPath()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "local");
            var destinationRoot = Path.Combine(root, "forge", "models");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "checkpoints"));
            var sourceFile = Path.Combine(sourceRoot, "checkpoints", "demo.safetensors");
            File.WriteAllText(sourceFile, "checkpoint");
            var projected = Path.Combine(destinationRoot, "Stable-diffusion", "demo.safetensors");

            var projector = new LocalModelProjector(sourceRoot, destinationRoot, "forge-neo");
            projector.UpdateSnapshot(CreateSnapshot(CreateModel(sourceFile, ModelCategory.Checkpoint)));
            projector.SetRequestedModels([CreateLocalProjectionRequest(sourceFile, ModelCategory.Checkpoint, projected)]);

            Assert.That(File.Exists(projected), Is.True);
            Assert.That(ReadFileAttributes(projected).HasFlag(FileAttributes.ReparsePoint), Is.True);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void SetRequestedModels_ThrowsWhenRegisteredModelNoLongerMatchesSnapshot()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "local");
            var destinationRoot = Path.Combine(root, "comfy", "models");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "checkpoints"));
            var sourceFile = Path.Combine(sourceRoot, "checkpoints", "demo.safetensors");
            File.WriteAllText(sourceFile, "checkpoint");
            var projected = Path.Combine(destinationRoot, "checkpoints", "demo.safetensors");

            var projector = new LocalModelProjector(sourceRoot, destinationRoot);
            projector.UpdateSnapshot(CreateSnapshot());

            Assert.That(
                () => projector.SetRequestedModels([CreateLocalProjectionRequest(sourceFile, ModelCategory.Checkpoint, projected)]),
                Throws.InvalidOperationException);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void SetRequestedModels_DoesNotOverwriteExistingNonLinkFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "local");
            var destinationRoot = Path.Combine(root, "comfy", "models");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "checkpoints"));
            Directory.CreateDirectory(Path.Combine(destinationRoot, "checkpoints"));

            var sourceFile = Path.Combine(sourceRoot, "checkpoints", "demo.safetensors");
            var destinationFile = Path.Combine(destinationRoot, "checkpoints", "demo.safetensors");
            File.WriteAllText(sourceFile, "local");
            File.WriteAllText(destinationFile, "catalog");

            var projector = new LocalModelProjector(sourceRoot, destinationRoot);
            projector.UpdateSnapshot(CreateSnapshot(CreateModel(sourceFile, ModelCategory.Checkpoint)));

            Assert.That(
                () => projector.SetRequestedModels([CreateLocalProjectionRequest(sourceFile, ModelCategory.Checkpoint, destinationFile)]),
                Throws.InvalidOperationException);
            Assert.That(File.ReadAllText(destinationFile), Is.EqualTo("catalog"));
            Assert.That(ReadFileAttributes(destinationFile).HasFlag(FileAttributes.ReparsePoint), Is.False);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void AddRequestedModels_WhenLocalFilenameMatchesButSizeDiffers_LogsCatalogFallback()
    {
        var root = CreateTempDirectory();
        var logs = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(new RecordingLoggerProvider(logs));
            builder.SetMinimumLevel(LogLevel.Information);
        });
        try
        {
            RunnerLog.Configure(loggerFactory);
            var sourceRoot = Path.Combine(root, "local");
            var destinationRoot = Path.Combine(root, "comfy", "models");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "vae"));
            var sourceFile = Path.Combine(sourceRoot, "vae", "demo.safetensors");
            File.WriteAllText(sourceFile, "local");
            var localModel = CreateModel(sourceFile, ModelCategory.Vae);
            var requestedSize = localModel.SizeBytes + 2_264;
            var projector = new LocalModelProjector(sourceRoot, destinationRoot);
            projector.UpdateSnapshot(CreateSnapshot(localModel));

            projector.AddRequestedModels(
            [
                new ModelDownloadRequest
                {
                    ModelId = "catalog-model",
                    Filename = localModel.Filename,
                    Category = ModelCategory.Vae,
                    SizeBytes = requestedSize,
                    DestinationPath = Path.Combine(destinationRoot, "vae", localModel.Filename),
                    SourceUrl = "https://models.example/catalog-model"
                }
            ]);

            Assert.That(
                logs.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message),
                Is.EqualTo(new[]
                {
                    $"Local model candidate 'demo.safetensors' was not selected for catalog model 'catalog-model': " +
                    $"size local={localModel.SizeBytes} catalog={requestedSize}. Downloading the catalog version instead."
                }));
        }
        finally
        {
            RunnerLog.Configure(null);
            TryDelete(root);
        }
    }

    [Test]
    public async Task ConcurrentScanAndCommandUpdates_AreSerialized()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "local");
            var destinationRoot = Path.Combine(root, "comfy", "models");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "loras"));
            var models = new List<ModelInfo>();
            var requests = new List<ModelDownloadRequest>();
            for (var i = 0; i < 16; i++)
            {
                var sourceFile = Path.Combine(sourceRoot, "loras", $"lora-{i:D2}.safetensors");
                File.WriteAllText(sourceFile, new string('l', i + 1));
                models.Add(CreateModel(sourceFile, ModelCategory.Lora));
                requests.Add(CreateLocalProjectionRequest(
                    sourceFile,
                    ModelCategory.Lora,
                    Path.Combine(destinationRoot, "loras", $"lora-{i:D2}.safetensors")));
            }

            var snapshot = CreateSnapshot([.. models]);
            var projector = new LocalModelProjector(sourceRoot, destinationRoot);
            projector.UpdateSnapshot(snapshot);

            using var start = new Barrier(3);
            const int iterations = 200;
            var scanLoop = Task.Run(() =>
            {
                start.SignalAndWait();
                for (var i = 0; i < iterations; i++)
                {
                    projector.UpdateSnapshot(snapshot);
                }
            });
            var startLoop = Task.Run(() =>
            {
                start.SignalAndWait();
                for (var i = 0; i < iterations; i++)
                {
                    projector.SetRequestedModels(requests.Take(1 + (i % requests.Count)));
                }
            });
            var promptLoop = Task.Run(() =>
            {
                start.SignalAndWait();
                for (var i = 0; i < iterations; i++)
                {
                    projector.AddRequestedModels([requests[(i * 7) % requests.Count]]);
                }
            });

            await Task.WhenAll(scanLoop, startLoop, promptLoop);

            projector.SetRequestedModels(requests);
            foreach (var request in requests)
            {
                Assert.That(File.Exists(request.DestinationPath), Is.True, request.DestinationPath);
            }

            projector.SetRequestedModels([]);
            Assert.That(Directory.EnumerateFileSystemEntries(Path.Combine(destinationRoot, "loras")), Is.Empty);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static ModelInfo CreateModel(string fullPath, ModelCategory category)
    {
        return new ModelInfo
        {
            Filename = Path.GetFileName(fullPath),
            LocalPath = Path.GetDirectoryName(fullPath) ?? string.Empty,
            Category = category,
            SizeBytes = new FileInfo(fullPath).Length
        };
    }

    private static ModelDownloadRequest CreateLocalProjectionRequest(string fullPath, ModelCategory category, string destinationPath)
    {
        var fileInfo = new FileInfo(fullPath);
        return new ModelDownloadRequest
        {
            Filename = Path.GetFileName(fullPath),
            Category = category,
            SizeBytes = fileInfo.Length,
            DestinationPath = destinationPath,
            LocalSelectionHash = LocalModelSelectionHash.Compute(Path.GetFileName(fullPath), fileInfo.Length, category)
        };
    }

    private static LocalModelSnapshot CreateSnapshot(params ModelInfo[] models)
    {
        var selectionHashes = models
            .Select(model => LocalModelSelectionHash.Compute(model.Filename, model.SizeBytes, model.Category))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        return new LocalModelSnapshot(
            models,
            "digest",
            [],
            selectionHashes,
            LocalModelSelectionHash.ComputeSnapshotDigest(selectionHashes));
    }

    private static FileAttributes ReadFileAttributes(string path)
    {
        return File.GetAttributes(path);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"local-model-projector-{Guid.NewGuid():N}");
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

    private sealed class RecordingLoggerProvider(List<(LogLevel Level, string Message)> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(logs);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(List<(LogLevel Level, string Message)> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => logs.Add((logLevel, formatter(state, exception)));
    }
}
