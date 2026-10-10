using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public class ModelDownloadServiceTests
{
    private static readonly Aria2Settings DefaultAria2Settings = Aria2Settings.FromEnvironment(static _ => null, static () => "generated-secret");

    [Test]
    public async Task SeedDownloads_RangeCapableRuntime_CreatesSparsePlaceholderAndSupportsRangeRead()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "diffusion_models", "sdxl.safetensors");
            var aria = new CompletingAria2Client(destinationPath, expectedBytes: 8);
            await using var service = CreateRangeManagedService(aria, root);
            await service.InitializeAsync(CancellationToken.None);

            service.SetActiveSession("session-1");
            service.SeedDownloads("session-1", [
                new ModelDownloadRequest
                {
                    ModelId = "model-1",
                    Filename = "sdxl.safetensors",
                    DestinationPath = destinationPath,
                    SizeBytes = 8,
                    SourceUrl = "https://models.example.test/sdxl.safetensors"
                }
            ]);

            Assert.That(File.Exists(destinationPath), Is.True);
            Assert.That(new FileInfo(destinationPath).Length, Is.EqualTo(8));

            var opened = await service.OpenRangeManagedAsync(
                destinationPath,
                "session-1",
                CancellationToken.None);
            await service.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                offset: 0,
                length: 8,
                CancellationToken.None);
            await service.ReleaseAsync(opened.LeaseId);

            Assert.That(aria.AddUriCallCount, Is.EqualTo(1));
            Assert.That(new FileInfo(destinationPath).Length, Is.EqualTo(8));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task InitializeAsync_RemovesTransfersAndFilesStagedByAnEarlierRun()
    {
        var root = CreateTempDirectory();
        try
        {
            var stagingDirectory = Path.Combine(root, "staging");
            var staging = new SessionModelDownloadStaging(Path.Combine(root, "sessions"), stagingDirectory);
            var staged = staging.GetStagingPath(Path.Combine(root, "sessions", "home", "models", "a.bin"))!;
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            File.WriteAllBytes(staged, [1, 2]);
            var backend = new ListingBackend(
            [
                TestModelTransferBackend.Snapshot(new("0000000000000001"), "active", 1, 2, files: [new(staged, 2, 1, [])]),
                TestModelTransferBackend.Snapshot(new("0000000000000002"), "active", 1, 2, files: [new(Path.Combine(root, "models", "b.bin"), 2, 1, [])])
            ]);
            await using var service = new ModelDownloadService(backend, DefaultAria2Settings, staging: staging);

            await service.InitializeAsync(CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(backend.Removed, Is.EqualTo(new[] { "0000000000000001" }));
                Assert.That(Directory.EnumerateFileSystemEntries(stagingDirectory), Is.Empty);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    private sealed class ListingBackend(IReadOnlyList<ModelTransferSnapshot> snapshots) : TestModelTransferBackend
    {
        public List<string> Removed { get; } = [];

        public override Task<IReadOnlyList<ModelTransferSnapshot>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult(snapshots);

        public override Task RemoveAsync(ModelTransferHandle handle, CancellationToken cancellationToken)
        {
            Removed.Add(handle.ToString());
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task SeedDownloads_ConventionalDestination_RemainsConventionalWithRicherMetadata()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "diffusion_models", "promoted.safetensors");
            var aria = new CompletingAria2Client(destinationPath, expectedBytes: 8);
            await using var service = CreateRangeManagedService(aria, root);
            await service.InitializeAsync(CancellationToken.None);

            service.SetActiveSession("session-1");
            service.SeedDownloads("session-1", [
                new ModelDownloadRequest
                {
                    ModelId = string.Empty,
                    Filename = "promoted.safetensors",
                    DestinationPath = destinationPath,
                    SizeBytes = 0,
                    SourceUrl = "https://models.example.test/promoted.safetensors"
                }
            ]);
            await service.EnsureDownloadedAsync(
                destinationPath,
                CancellationToken.None,
                highPriority: false);

            var enrichedRequest = new ModelDownloadRequest
            {
                ModelId = "model-promoted",
                Filename = "promoted.safetensors",
                DestinationPath = destinationPath,
                SizeBytes = 8,
                SourceUrl = "https://models.example.test/promoted.safetensors"
            };
            service.SeedDownloads("session-1", [enrichedRequest]);
            await service.EnsureDownloadsAsync(
                [enrichedRequest],
                CancellationToken.None,
                highPriority: true);

            var opened = await service.OpenRangeManagedAsync(
                destinationPath,
                "session-1",
                CancellationToken.None);
            var handled = await service.TryEnsureRegisteredDownloadAsync(
                destinationPath,
                CancellationToken.None,
                highPriority: false);

            Assert.Multiple(() =>
            {
                Assert.That(opened.Disposition, Is.EqualTo(ModelHydrationOpenDisposition.Unmanaged));
                Assert.That(handled, Is.True);
                Assert.That(aria.AddUriCallCount, Is.EqualTo(1));
                Assert.That(new FileInfo(destinationPath).Length, Is.EqualTo(8));
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task SeedDownloads_FullDownloadModel_CreatesZeroLengthPlaceholder()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "diffusion_models", "unknown.safetensors");
            var aria = new CompletingAria2Client(destinationPath, expectedBytes: 8);
            var service = new ModelDownloadService(aria, DefaultAria2Settings);

            service.SetActiveSession("session-1");
            service.SeedDownloads("session-1", [
                new ModelDownloadRequest
                {
                    ModelId = "model-unknown",
                    Filename = "unknown.safetensors",
                    DestinationPath = destinationPath,
                    SizeBytes = 0,
                    SourceUrl = "https://models.example.test/unknown.safetensors"
                }
            ]);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(destinationPath), Is.True);
                Assert.That(new FileInfo(destinationPath).Length, Is.Zero);
            });

            await service.EnsureDownloadedAsync(destinationPath, CancellationToken.None, highPriority: false);

            Assert.That(aria.AddUriCallCount, Is.EqualTo(1));
            Assert.That(new FileInfo(destinationPath).Length, Is.EqualTo(8));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task CancelSession_RangeCapableRuntime_PreservesPersistentPlaceholder()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "diffusion_models", "stale.safetensors");
            await using var service = CreateRangeManagedService(
                new CompletingAria2Client(destinationPath, expectedBytes: 8),
                root);
            await service.InitializeAsync(CancellationToken.None);

            service.SetActiveSession("session-1");
            service.SeedDownloads("session-1", [
                new ModelDownloadRequest
                {
                    ModelId = "model-1",
                    Filename = "stale.safetensors",
                    DestinationPath = destinationPath,
                    SizeBytes = 8,
                    SourceUrl = "https://models.example.test/stale.safetensors"
                }
            ]);

            Assert.That(File.Exists(destinationPath), Is.True);
            Assert.That(new FileInfo(destinationPath).Length, Is.EqualTo(8));

            service.CancelSession("session-1");

            Assert.That(File.Exists(destinationPath), Is.True);
            Assert.That(new FileInfo(destinationPath).Length, Is.EqualTo(8));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task SetActiveSession_ReactivatesRangeHydrationForSameLogicalSession()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "diffusion_models", "restart.safetensors");
            await using var service = CreateRangeManagedService(
                new CompletingAria2Client(destinationPath, expectedBytes: 8),
                root);
            await service.InitializeAsync(CancellationToken.None);

            service.SetActiveSession("session-1");
            service.SeedDownloads("session-1", [
                new ModelDownloadRequest
                {
                    ModelId = "model-1",
                    Filename = "restart.safetensors",
                    DestinationPath = destinationPath,
                    SizeBytes = 8,
                    SourceUrl = "https://models.example.test/restart.safetensors"
                }
            ]);

            service.CancelSession("session-1");
            service.SetActiveSession("session-1");

            var opened = await service.OpenRangeManagedAsync(
                destinationPath,
                "session-1",
                CancellationToken.None);
            await service.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                8,
                CancellationToken.None);
            await service.ReleaseAsync(opened.LeaseId);

            Assert.That(File.Exists(destinationPath), Is.True);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task CancelSession_ConventionalModel_RemovesUntouchedOwnedPlaceholder()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "diffusion_models", "cancelled.bin");
            await using var service = new ModelDownloadService(
                new CompletingAria2Client(destinationPath, expectedBytes: 8),
                DefaultAria2Settings);

            service.SetActiveSession("session-1");
            service.SeedDownloads("session-1", [
                new ModelDownloadRequest
                {
                    ModelId = "model-1",
                    Filename = "cancelled.bin",
                    DestinationPath = destinationPath,
                    SourceUrl = "https://models.example.test/cancelled.bin"
                }
            ]);

            Assert.That(File.Exists(destinationPath), Is.True);

            service.CancelSession("session-1");

            Assert.That(File.Exists(destinationPath), Is.False);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task CancelSession_ConventionalModel_PreservesMaterializedPayload()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "diffusion_models", "materialized.bin");
            await using var service = new ModelDownloadService(
                new CompletingAria2Client(destinationPath, expectedBytes: 8),
                DefaultAria2Settings);

            service.SetActiveSession("session-1");
            service.SeedDownloads("session-1", [
                new ModelDownloadRequest
                {
                    ModelId = "model-1",
                    Filename = "materialized.bin",
                    DestinationPath = destinationPath,
                    SourceUrl = "https://models.example.test/materialized.bin"
                }
            ]);
            await service.EnsureDownloadedAsync(destinationPath, CancellationToken.None, highPriority: false);

            service.CancelSession("session-1");

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(destinationPath), Is.True);
                Assert.That(new FileInfo(destinationPath).Length, Is.EqualTo(8));
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task EnsureDownloads_MixedBatchFailure_PreservesEachCompletedPayloadDuringCleanup()
    {
        var root = CreateTempDirectory();
        try
        {
            var completedPath = Path.Combine(root, "diffusion_models", "completed.bin");
            var failedPath = Path.Combine(root, "diffusion_models", "failed.bin");
            var backend = new MixedBatchBackend(completedPath, failedPath, expectedBytes: 8);
            await using var service = new ModelDownloadService(backend, DefaultAria2Settings);
            var downloads = new[]
            {
                new ModelDownloadRequest
                {
                    ModelId = "completed-model",
                    Filename = "completed.bin",
                    DestinationPath = completedPath,
                    SizeBytes = 8,
                    SourceUrl = "https://models.example.test/completed.bin"
                },
                new ModelDownloadRequest
                {
                    ModelId = "failed-model",
                    Filename = "failed.bin",
                    DestinationPath = failedPath,
                    SizeBytes = 8,
                    SourceUrl = "https://models.example.test/failed.bin"
                }
            };

            service.SetActiveSession("session-1");
            service.SeedDownloads("session-1", downloads);

            Assert.That(
                async () => await service.EnsureDownloadsAsync(
                    downloads,
                    CancellationToken.None,
                    highPriority: true),
                Throws.InstanceOf<InvalidOperationException>());

            service.SeedDownloads("session-1", []);
            await WaitUntilAsync(() => !File.Exists(failedPath), TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(completedPath), Is.True);
                Assert.That(new FileInfo(completedPath).Length, Is.EqualTo(8));
                Assert.That(File.Exists(failedPath), Is.False);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task CancelSession_ConventionalModel_WaitsForTransferRemovalThenDeletesPartialOwnedTarget()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "diffusion_models", "partial.bin");
            var backend = new BlockingPartialTransferBackend(destinationPath);
            await using var service = new ModelDownloadService(backend, DefaultAria2Settings);

            service.SetActiveSession("session-1");
            service.SeedDownloads("session-1", [
                new ModelDownloadRequest
                {
                    ModelId = "model-1",
                    Filename = "partial.bin",
                    DestinationPath = destinationPath,
                    SizeBytes = 8,
                    SourceUrl = "https://models.example.test/partial.bin"
                }
            ]);

            var download = service.EnsureDownloadedAsync(
                destinationPath,
                CancellationToken.None,
                highPriority: false);
            await backend.StatusRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

            service.CancelSession("session-1");
            await backend.RemovalRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(File.Exists(destinationPath), Is.True);

            backend.AllowRemoval.TrySetResult();
            Assert.That(async () => await download, Throws.InstanceOf<OperationCanceledException>());
            // The target and then its control file are deleted; wait for both.
            await WaitUntilAsync(
                () => !File.Exists(destinationPath) && !File.Exists($"{destinationPath}.aria2"),
                TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(destinationPath), Is.False);
                Assert.That(File.Exists($"{destinationPath}.aria2"), Is.False);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task CancelSession_ConventionalModel_RetriesRemovalBeforeDeletingPartialTarget()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "diffusion_models", "retry-partial.bin");
            var backend = new BlockingPartialTransferBackend(destinationPath)
            {
                RemovalFailuresRemaining = 1
            };
            await using var service = new ModelDownloadService(backend, DefaultAria2Settings);

            service.SetActiveSession("session-1");
            service.SeedDownloads("session-1", [
                new ModelDownloadRequest
                {
                    ModelId = "model-1",
                    Filename = "retry-partial.bin",
                    DestinationPath = destinationPath,
                    SizeBytes = 8,
                    SourceUrl = "https://models.example.test/retry-partial.bin"
                }
            ]);

            var download = service.EnsureDownloadedAsync(
                destinationPath,
                CancellationToken.None,
                highPriority: false);
            await backend.StatusRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

            service.CancelSession("session-1");
            await backend.RemovalRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(
                async () => await download.WaitAsync(TimeSpan.FromSeconds(1)),
                Throws.InstanceOf<OperationCanceledException>());
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(destinationPath), Is.True);
                Assert.That(File.Exists($"{destinationPath}.aria2"), Is.True);
                Assert.That(backend.RemovalAttempts, Is.EqualTo(1));
            });

            await backend.RemovalRetried.Task.WaitAsync(TimeSpan.FromSeconds(5));
            backend.AllowRemoval.TrySetResult();
            await WaitUntilAsync(() => !File.Exists(destinationPath), TimeSpan.FromSeconds(5));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task CancelSession_ConventionalModel_BoundsAlreadyRunningRemovalRetry()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "diffusion_models", "cancel-removal-retry.bin");
            var backend = new BlockingPartialTransferBackend(destinationPath)
            {
                ReturnFailedStatus = true,
                RemovalFailuresRemaining = 1
            };
            await using var service = new ModelDownloadService(backend, DefaultAria2Settings);

            service.SetActiveSession("session-1");
            service.SeedDownloads("session-1", [
                new ModelDownloadRequest
                {
                    ModelId = "model-1",
                    Filename = "cancel-removal-retry.bin",
                    DestinationPath = destinationPath,
                    SizeBytes = 8,
                    SourceUrl = "https://models.example.test/cancel-removal-retry.bin"
                }
            ]);

            var download = service.EnsureDownloadedAsync(
                destinationPath,
                CancellationToken.None,
                highPriority: false);
            await backend.RemovalRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

            service.CancelSession("session-1");
            Assert.That(
                async () => await download.WaitAsync(TimeSpan.FromSeconds(1)),
                Throws.InstanceOf<OperationCanceledException>());
            await backend.RemovalRetried.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(destinationPath), Is.True);
                Assert.That(File.Exists($"{destinationPath}.aria2"), Is.True);
                Assert.That(backend.RemovalAttempts, Is.EqualTo(2));
            });

            backend.AllowRemoval.TrySetResult();
            await WaitUntilAsync(() => !File.Exists(destinationPath), TimeSpan.FromSeconds(5));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task ReplacementSession_ConventionalModel_WaitsForPriorRemovalFenceBeforeCreating()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "diffusion_models", "replacement-fence.bin");
            var backend = new BlockingPartialTransferBackend(destinationPath)
            {
                RemovalFailuresRemaining = 1
            };
            await using var service = new ModelDownloadService(backend, DefaultAria2Settings);

            service.SetActiveSession("session-1");
            service.SeedDownloads("session-1", [CreateDownloadRequest("session-1-model")]);
            var firstDownload = service.EnsureDownloadedAsync(
                destinationPath,
                CancellationToken.None,
                highPriority: false);
            await backend.StatusRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

            service.CancelSession("session-1");
            await backend.RemovalRetried.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(
                async () => await firstDownload.WaitAsync(TimeSpan.FromSeconds(1)),
                Throws.InstanceOf<OperationCanceledException>());

            service.SetActiveSession("session-2");
            service.SeedDownloads("session-2", [CreateDownloadRequest("session-2-model")]);
            using var replacementCancellation = new CancellationTokenSource();
            var replacementDownload = service.EnsureDownloadedAsync(
                destinationPath,
                replacementCancellation.Token,
                highPriority: false);

            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Assert.That(backend.CreateAttempts, Is.EqualTo(1));

            backend.AllowRemoval.TrySetResult();
            await WaitUntilAsync(() => backend.CreateAttempts == 2, TimeSpan.FromSeconds(5));
            replacementCancellation.Cancel();
            Assert.That(
                async () => await replacementDownload.WaitAsync(TimeSpan.FromSeconds(1)),
                Throws.InstanceOf<OperationCanceledException>());

            ModelDownloadRequest CreateDownloadRequest(string modelId) => new()
            {
                ModelId = modelId,
                Filename = "replacement-fence.bin",
                DestinationPath = destinationPath,
                SizeBytes = 8,
                SourceUrl = "https://models.example.test/replacement-fence.bin"
            };
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task SeedDownloads_ConventionalAuthoritativeOmission_RemovesUntouchedOwnedPlaceholder()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "diffusion_models", "omitted.bin");
            await using var service = new ModelDownloadService(
                new CompletingAria2Client(destinationPath, expectedBytes: 8),
                DefaultAria2Settings);

            service.SetActiveSession("session-1");
            service.SeedDownloads("session-1", [
                new ModelDownloadRequest
                {
                    ModelId = "model-1",
                    Filename = "omitted.bin",
                    DestinationPath = destinationPath,
                    SourceUrl = "https://models.example.test/omitted.bin"
                }
            ]);

            service.SeedDownloads("session-1", []);
            var stillRegistered = await service.TryEnsureRegisteredDownloadAsync(
                destinationPath,
                CancellationToken.None,
                highPriority: false);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(destinationPath), Is.False);
                Assert.That(stillRegistered, Is.False);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task CancelSession_ConventionalModel_PreservesPreexistingEmptyFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "diffusion_models", "preexisting.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, []);
            await using var service = new ModelDownloadService(
                new CompletingAria2Client(destinationPath, expectedBytes: 8),
                DefaultAria2Settings);

            service.SetActiveSession("session-1");
            service.SeedDownloads("session-1", [
                new ModelDownloadRequest
                {
                    ModelId = "model-1",
                    Filename = "preexisting.bin",
                    DestinationPath = destinationPath,
                    SourceUrl = "https://models.example.test/preexisting.bin"
                }
            ]);

            service.CancelSession("session-1");

            Assert.That(File.Exists(destinationPath), Is.True);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task CancelSession_KeepsMaterializedModelFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "diffusion_models", "real.safetensors");
            var aria = new CompletingAria2Client(destinationPath, expectedBytes: 8);
            await using var service = CreateRangeManagedService(aria, root);
            await service.InitializeAsync(CancellationToken.None);

            service.SetActiveSession("session-1");
            service.SeedDownloads("session-1", [
                new ModelDownloadRequest
                {
                    ModelId = "model-1",
                    Filename = "real.safetensors",
                    DestinationPath = destinationPath,
                    SizeBytes = 8,
                    SourceUrl = "https://models.example.test/real.safetensors"
                }
            ]);

            await service.EnsureDownloadedAsync(destinationPath, CancellationToken.None, highPriority: false);

            service.CancelSession("session-1");

            Assert.That(File.Exists(destinationPath), Is.True);
            Assert.That(new FileInfo(destinationPath).Length, Is.EqualTo(8));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task SetActiveSession_NullPreservesSeededPlaceholdersAndRegisteredDownloads()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "diffusion_models", "reconnect.safetensors");
            var aria = new CompletingAria2Client(destinationPath, expectedBytes: 8);
            await using var service = CreateRangeManagedService(aria, root);
            await service.InitializeAsync(CancellationToken.None);

            service.SetActiveSession("session-1");
            service.SeedDownloads("session-1", [
                new ModelDownloadRequest
                {
                    ModelId = "model-1",
                    Filename = "reconnect.safetensors",
                    DestinationPath = destinationPath,
                    SizeBytes = 8,
                    SourceUrl = "https://models.example.test/reconnect.safetensors"
                }
            ]);

            service.SetActiveSession(null);

            Assert.That(File.Exists(destinationPath), Is.True);
            Assert.That(new FileInfo(destinationPath).Length, Is.EqualTo(8));

            var opened = await service.OpenRangeManagedAsync(
                destinationPath,
                "session-1",
                CancellationToken.None);
            await service.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                offset: 0,
                length: 8,
                CancellationToken.None);
            await service.ReleaseAsync(opened.LeaseId);

            Assert.That(aria.AddUriCallCount, Is.EqualTo(1));
            Assert.That(new FileInfo(destinationPath).Length, Is.EqualTo(8));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task TryEnsureRegisteredDownloadAsync_WhenPathRegistered_HydratesFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "diffusion_models", "registered.safetensors");
            var aria = new CompletingAria2Client(destinationPath, expectedBytes: 8);
            var service = new ModelDownloadService(aria, DefaultAria2Settings);

            service.SetActiveSession("session-1");
            service.RegisterDownloads("session-1", [
                new ModelDownloadRequest
                {
                    ModelId = "model-1",
                    Filename = "registered.safetensors",
                    DestinationPath = destinationPath,
                    SizeBytes = 8,
                    SourceUrl = "https://models.example.test/registered.safetensors"
                }
            ]);

            var handled = await service.TryEnsureRegisteredDownloadAsync(destinationPath, CancellationToken.None, highPriority: false);

            Assert.That(handled, Is.True);
            Assert.That(aria.AddUriCallCount, Is.EqualTo(1));
            Assert.That(new FileInfo(destinationPath).Length, Is.EqualTo(8));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task TryEnsureRegisteredDownloadAsync_WhenPathUnregistered_ReturnsFalseWithoutTrustingExistingFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "metadata", "optional.json");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllTextAsync(destinationPath, "local-sidecar");
            var aria = new CompletingAria2Client(destinationPath, expectedBytes: 8);
            var service = new ModelDownloadService(aria, DefaultAria2Settings);

            var handled = await service.TryEnsureRegisteredDownloadAsync(destinationPath, CancellationToken.None, highPriority: false);

            Assert.That(handled, Is.False);
            Assert.That(aria.AddUriCallCount, Is.EqualTo(0));
            Assert.That(await File.ReadAllTextAsync(destinationPath), Is.EqualTo("local-sidecar"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"model-download-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static ModelDownloadService CreateRangeManagedService(
        IModelTransferBackend backend,
        string root)
    {
        var settings = Aria2Settings.FromEnvironment(name => name switch
        {
            "ARIA2_DOWNLOAD_DIR" => root,
            "ARIA2_STATE_DIR" => Path.Combine(root, "aria2-state"),
            _ => null
        }, () => "generated-secret");
        var capabilities = new ModelHydrationRuntimeCapabilities(
            "synthetic-editor",
            SupportsPersistentRangeHydration: true,
            PersistentModelRoots: [root]);
        return new ModelDownloadService(backend, settings, capabilities);
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the expected condition.");
            }

            await Task.Delay(20);
        }
    }

    private sealed class BlockingPartialTransferBackend(string destinationPath) : TestModelTransferBackend
    {
        public TaskCompletionSource StatusRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RemovalRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RemovalRetried { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowRemoval { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReturnFailedStatus { get; init; }
        public int RemovalFailuresRemaining { get; init; }
        public int CreateAttempts { get; private set; }
        public int RemovalAttempts { get; private set; }

        public override Task<IReadOnlyList<ModelTransferHandle>> CreateAsync(
            ModelTransferCreateRequest request,
            CancellationToken cancellationToken)
        {
            CreateAttempts++;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.WriteAllBytes(destinationPath, [1, 2, 3, 4]);
            File.WriteAllBytes($"{destinationPath}.aria2", [1]);
            return Task.FromResult<IReadOnlyList<ModelTransferHandle>>([new ModelTransferHandle("partial-gid")]);
        }

        public override async Task<ModelTransferSnapshot> GetStatusAsync(
            ModelTransferHandle handle,
            CancellationToken cancellationToken)
        {
            StatusRequested.TrySetResult();
            if (ReturnFailedStatus)
            {
                return Snapshot(
                    handle,
                    "error",
                    completedLength: 4,
                    totalLength: 8,
                    errorCode: "3",
                    errorMessage: "simulated terminal transfer error");
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The status wait should be cancelled.");
        }

        public override async Task RemoveAsync(
            ModelTransferHandle handle,
            CancellationToken cancellationToken)
        {
            RemovalAttempts++;
            RemovalRequested.TrySetResult();
            if (RemovalAttempts <= RemovalFailuresRemaining)
            {
                throw new IOException("simulated transient removal failure");
            }

            RemovalRetried.TrySetResult();
            await AllowRemoval.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class CompletingAria2Client(string destinationPath, int expectedBytes) : TestModelTransferBackend
    {
        private readonly HashSet<ModelTransferHandle> _createdHandles = [];

        public int AddUriCallCount { get; private set; }

        public override Task<IReadOnlyList<ModelTransferHandle>> CreateAsync(
            ModelTransferCreateRequest request,
            CancellationToken cancellationToken)
        {
            if (request.SourceUris.Count > 0)
            {
                AddUriCallCount++;
            }

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(destinationPath, Enumerable.Repeat((byte)1, expectedBytes).ToArray());
            var handle = new ModelTransferHandle(request.PreferredIdentifier ?? "gid-1");
            _createdHandles.Add(handle);
            IReadOnlyList<ModelTransferHandle> handles = [handle];
            return Task.FromResult(handles);
        }

        public override Task<ModelTransferSnapshot> GetStatusAsync(
            ModelTransferHandle handle,
            CancellationToken cancellationToken)
        {
            if (!_createdHandles.Contains(handle))
            {
                throw new ModelTransferStatusUnavailableException($"Transfer '{handle}' is unavailable.");
            }

            return Task.FromResult(Snapshot(
                handle,
                "complete",
                expectedBytes,
                expectedBytes,
                files:
                [
                    new ModelTransferFileSnapshot(
                        destinationPath,
                        expectedBytes,
                        expectedBytes,
                        ["https://models.example.test/model"])
                ]));
        }
    }

    private sealed class MixedBatchBackend(
        string completedPath,
        string failedPath,
        int expectedBytes) : TestModelTransferBackend
    {
        private readonly ModelTransferHandle _completedHandle = new("completed-gid");
        private readonly TaskCompletionSource _completedStatusReturned =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<IReadOnlyList<ModelTransferHandle>> CreateAsync(
            ModelTransferCreateRequest request,
            CancellationToken cancellationToken)
        {
            if (string.Equals(request.DestinationPath, failedPath, StringComparison.Ordinal))
            {
                await _completedStatusReturned.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("intentional batch failure");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(completedPath)!);
            await File.WriteAllBytesAsync(
                completedPath,
                Enumerable.Repeat((byte)1, expectedBytes).ToArray(),
                cancellationToken);
            return [_completedHandle];
        }

        public override Task<ModelTransferSnapshot> GetStatusAsync(
            ModelTransferHandle handle,
            CancellationToken cancellationToken)
        {
            _completedStatusReturned.TrySetResult();
            return Task.FromResult(Snapshot(
                handle,
                "complete",
                expectedBytes,
                expectedBytes,
                files:
                [
                    new ModelTransferFileSnapshot(
                        completedPath,
                        expectedBytes,
                        expectedBytes,
                        ["https://models.example.test/completed.bin"])
                ]));
        }
    }
}
