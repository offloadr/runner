using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public class DownloadCoordinatorTests
{
    [Test]
    public async Task SetActiveSession_ClearsRegistry_WhenSessionChanges()
    {
        var aria = new FakeAria2Client();
        var fileSystem = new FakeFileSystem(exists: ["/tmp/model.safetensors"]);
        var sut = new DownloadCoordinator(aria, fileSystem);
        ModelDownloadProgress? observed = null;
        sut.ProgressReporter = progress =>
        {
            observed = progress;
            return Task.CompletedTask;
        };

        sut.SetActiveSession("session-a");
        sut.RegisterDownloads("session-a", [new ModelDownloadRequest
        {
            ModelId = "model-a",
            Filename = "model.safetensors",
            DestinationPath = "/tmp/model.safetensors",
            SourceUrl = "https://example.com/a"
        }]);

        sut.SetActiveSession("session-b");

        await sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: false);

        Assert.That(observed, Is.Not.Null);
        Assert.That(observed!.Value.SessionId, Is.EqualTo("session-b"));
    }

    [Test]
    public async Task RegisterDownloads_OverwritesExistingEntry_ForSameNormalizedPath()
    {
        var aria = new FakeAria2Client();
        var fileSystem = new FakeFileSystem(exists: ["/tmp/model.safetensors"]);
        var sut = new DownloadCoordinator(aria, fileSystem);
        ModelDownloadProgress? observed = null;
        sut.ProgressReporter = progress =>
        {
            observed = progress;
            return Task.CompletedTask;
        };

        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "old",
            Filename = "model.safetensors",
            DestinationPath = "/tmp/model.safetensors"
        }]);

        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "new",
            Filename = "model.safetensors",
            DestinationPath = "/tmp/model.safetensors"
        }]);

        await sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: false);

        Assert.That(observed, Is.Not.Null);
        Assert.That(observed!.Value.ModelId, Is.EqualTo("new"));
    }

    [Test]
    public void EnsureDownloadedAsync_Throws_WhenDestinationMissing()
    {
        var sut = new DownloadCoordinator(new FakeAria2Client(), new FakeFileSystem());

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sut.EnsureDownloadedAsync("  ", CancellationToken.None, highPriority: false));
    }

    [Test]
    public async Task EnsureDownloadedAsync_CompletesImmediately_WhenFileAlreadyPresent()
    {
        var aria = new FakeAria2Client();
        var sut = new DownloadCoordinator(aria, new FakeFileSystem(exists: ["/tmp/model.safetensors"]));

        await sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: false);

        Assert.That(aria.AddUriCalls, Is.EqualTo(0));
        Assert.That(aria.AddMetalinkCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task RegisterDownloads_KeepsDestinationsThatDifferOnlyInCase()
    {
        var fileSystem = new FakeFileSystem();
        FakeAria2Client? aria = null;
        aria = new FakeAria2Client
        {
            OnAddUri = () => fileSystem.SetFileSize(aria!.LastCreateRequest!.DestinationPath, 8)
        };
        var sut = new DownloadCoordinator(aria, fileSystem);
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1",
        [
            new ModelDownloadRequest
            {
                ModelId = "model-upper",
                Filename = "Model.safetensors",
                DestinationPath = "/tmp/Model.safetensors",
                SizeBytes = 8,
                SourceUrl = "https://example.com/upper"
            },
            new ModelDownloadRequest
            {
                ModelId = "model-lower",
                Filename = "model.safetensors",
                DestinationPath = "/tmp/model.safetensors",
                SizeBytes = 8,
                SourceUrl = "https://example.com/lower"
            }
        ]);

        await sut.EnsureDownloadedAsync("/tmp/Model.safetensors", CancellationToken.None, highPriority: true);
        var upperSource = aria.LastCreateRequest!.SourceUris.Single();
        await sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: true);
        var lowerSource = aria.LastCreateRequest!.SourceUris.Single();

        Assert.Multiple(() =>
        {
            Assert.That(upperSource, Is.EqualTo("https://example.com/upper"));
            Assert.That(lowerSource, Is.EqualTo("https://example.com/lower"));
        });
    }

    [Test]
    public async Task EnsureDownloadedAsync_DownloadsAgain_WhenExistingFileIsLargerThanExpected()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.SetFileSize("/tmp/model.safetensors", 12);
        var aria = new FakeAria2Client
        {
            OnAddUri = () => fileSystem.SetFileSize("/tmp/model.safetensors", 8)
        };
        var sut = new DownloadCoordinator(aria, fileSystem);
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "model.safetensors",
            DestinationPath = "/tmp/model.safetensors",
            SizeBytes = 8,
            SourceUrl = "https://example.com/model"
        }]);

        await sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: true);

        Assert.That(aria.AddUriCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task EnsureDownloadedAsync_PassesSourceUriAndQueuePosition_ToBackend()
    {
        var fileSystem = new FakeFileSystem();
        var aria = new FakeAria2Client
        {
            OnAddUri = () => fileSystem.SetFileSize("/tmp/model.safetensors", 8)
        };
        var sut = new DownloadCoordinator(aria, fileSystem);
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "model.safetensors",
            DestinationPath = "/tmp/model.safetensors",
            SizeBytes = 8,
            SourceUrl = "https://example.com/model"
        }]);

        await sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: true);

        Assert.That(aria.LastCreateRequest, Is.Not.Null);
        Assert.That(aria.LastCreateRequest!.SourceUris, Is.EqualTo(["https://example.com/model"]));
        Assert.That(aria.LastCreateRequest.QueuePosition, Is.EqualTo(0));
        Assert.That(aria.LastCreateRequest.StartPaused, Is.False);
    }

    [Test]
    public async Task EnsureDownloadedAsync_StagedDestination_DownloadsToStagingAndPublishes()
    {
        const string destination = "/sessions/home/models/model.safetensors";
        const string staged = "/state/staging/key/model.safetensors";
        var fileSystem = new FakeFileSystem();
        var aria = new FakeAria2Client
        {
            OnAddUri = () => fileSystem.SetFileSize(staged, 8)
        };
        var staging = new FakeStaging(destination, staged, fileSystem);
        var sut = new DownloadCoordinator(
            aria,
            Aria2Settings.FromEnvironment(static _ => null, static () => "unused"),
            fileSystem,
            staging: staging);
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "model.safetensors",
            DestinationPath = destination,
            SizeBytes = 8,
            SourceUrl = "https://example.com/model"
        }]);

        await sut.EnsureDownloadedAsync(destination, CancellationToken.None, highPriority: false);

        Assert.Multiple(() =>
        {
            Assert.That(aria.LastCreateRequest!.DestinationPath, Is.EqualTo(staged));
            Assert.That(staging.Published, Is.EqualTo([(staged, destination)]));
            Assert.That(fileSystem.FileExists(destination), Is.True);
        });
    }

    [Test]
    public async Task EnsureDownloadedAsync_CompleteStagedFile_IsPublishedWithoutNewTransfer()
    {
        const string destination = "/sessions/home/models/model.safetensors";
        const string staged = "/state/staging/key/model.safetensors";
        var fileSystem = new FakeFileSystem();
        fileSystem.SetFileSize(staged, 8);
        var aria = new FakeAria2Client();
        var staging = new FakeStaging(destination, staged, fileSystem);
        var sut = new DownloadCoordinator(
            aria,
            Aria2Settings.FromEnvironment(static _ => null, static () => "unused"),
            fileSystem,
            staging: staging);
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "model.safetensors",
            DestinationPath = destination,
            SizeBytes = 8,
            SourceUrl = "https://example.com/model"
        }]);

        await sut.EnsureDownloadedAsync(destination, CancellationToken.None, highPriority: false);

        Assert.Multiple(() =>
        {
            Assert.That(aria.AddUriCalls, Is.EqualTo(0));
            Assert.That(staging.Published, Is.EqualTo([(staged, destination)]));
        });
    }

    private sealed class FakeStaging(string destination, string staged, FakeFileSystem fileSystem) : IModelDownloadStaging
    {
        public List<(string Staged, string Destination)> Published { get; } = [];

        public string? GetStagingPath(string destinationPath)
            => string.Equals(destinationPath, destination, StringComparison.Ordinal) ? staged : null;

        public void Publish(string stagingPath, string destinationPath)
        {
            Published.Add((stagingPath, destinationPath));
            fileSystem.SetFileSize(destinationPath, fileSystem.GetFileSize(stagingPath));
        }

        public void Discard(string destinationPath)
        {
        }
    }

    [Test]
    public async Task EnsureDownloadedAsync_PassesMetalinkContent_ToBackend()
    {
        var fileSystem = new FakeFileSystem();
        var aria = new FakeAria2Client
        {
            OnAddMetalink = () => fileSystem.SetFileSize("/tmp/model.safetensors", 8)
        };
        var sut = new DownloadCoordinator(aria, fileSystem);
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "model.safetensors",
            DestinationPath = "/tmp/model.safetensors",
            SizeBytes = 8,
            MetalinkXml = "<metalink />"
        }]);

        await sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: false);

        Assert.That(aria.LastCreateRequest, Is.Not.Null);
        Assert.That(aria.LastCreateRequest!.MetalinkContent, Is.EqualTo("<metalink />"u8.ToArray()));
        Assert.That(aria.LastCreateRequest.SourceUris, Is.Empty);
    }

    [Test]
    public async Task EnsureDownloadedAsync_DoesNotTreatFileAsComplete_WhenAriaSidecarExists()
    {
        var aria = new FakeAria2Client
        {
            StatusFactory = _ => new Aria2DownloadStatus("error", 0, 0, 0, "15", "download aborted")
        };
        var sut = new DownloadCoordinator(aria, new FakeFileSystem(exists: ["/tmp/model.safetensors", "/tmp/model.safetensors.aria2"]));
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "model.safetensors",
            DestinationPath = "/tmp/model.safetensors",
            SourceUrl = "https://example.com/model"
        }]);

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: false));

        Assert.That(aria.AddUriCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task EnsureDownloadedAsync_SwallowsReporterFailures()
    {
        var sut = new DownloadCoordinator(new FakeAria2Client(), new FakeFileSystem(exists: ["/tmp/model.safetensors"]));
        sut.SetActiveSession("session-1");
        sut.ProgressReporter = _ => throw new InvalidOperationException("boom");

        Assert.DoesNotThrowAsync(async () =>
            await sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: false));
    }

    [Test]
    public async Task EnsureDownloadedAsync_CreatesFallbackTrackedDownload_WhenNotRegistered()
    {
        var sut = new DownloadCoordinator(new FakeAria2Client(), new FakeFileSystem(exists: ["/tmp/new.bin"]));
        sut.SetActiveSession("session-z");
        ModelDownloadProgress? observed = null;
        sut.ProgressReporter = progress =>
        {
            observed = progress;
            return Task.CompletedTask;
        };

        await sut.EnsureDownloadedAsync("/tmp/new.bin", CancellationToken.None, highPriority: false);

        Assert.That(observed, Is.Not.Null);
        Assert.That(observed!.Value.SessionId, Is.EqualTo("session-z"));
        Assert.That(observed.Value.ModelId, Is.EqualTo(string.Empty));
        Assert.That(observed.Value.Filename, Is.EqualTo("new.bin"));
    }

    [Test]
    public async Task EnsureDownloadedAsync_DoesNotTreatEmptyUnknownSizeFileAsComplete()
    {
        var fileSystem = new FakeFileSystem(exists: ["/tmp/model.safetensors"], fileSizes: new Dictionary<string, long>
        {
            ["/tmp/model.safetensors"] = 0
        });
        var aria = new FakeAria2Client
        {
            OnAddUri = () => fileSystem.SetFileSize("/tmp/model.safetensors", 8)
        };
        var sut = new DownloadCoordinator(aria, fileSystem);
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "model.safetensors",
            DestinationPath = "/tmp/model.safetensors",
            SizeBytes = 0,
            SourceUrl = "https://example.com/model"
        }]);

        await sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: false);

        Assert.That(aria.AddUriCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task EnsureDownloadedAsync_ReportsFailed_WhenAriaStatusUnavailableIsRealError()
    {
        var aria = new FakeAria2Client
        {
            StatusFactory = _ => throw new ModelTransferStatusUnavailableException("Failed to open the file /comfyui/models/vae/ae.safetensors, cause: Read-only file system")
        };
        var fileSystem = new FakeFileSystem();
        var sut = new DownloadCoordinator(aria, fileSystem);
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "ae.safetensors",
            DestinationPath = "/comfyui/models/vae/ae.safetensors",
            SourceUrl = "https://example.com/model"
        }]);

        var progress = new List<ModelDownloadProgress>();
        sut.ProgressReporter = update =>
        {
            progress.Add(update);
            return Task.CompletedTask;
        };

        var ex = Assert.ThrowsAsync<ModelTransferStatusUnavailableException>(async () =>
            await sut.EnsureDownloadedAsync("/comfyui/models/vae/ae.safetensors", CancellationToken.None, highPriority: false));

        Assert.That(ex, Is.Not.Null);
        Assert.That(progress.Last().State, Is.EqualTo(ModelDownloadState.Failed));
        Assert.That(progress.Last().Message, Does.Contain("Read-only file system"));
    }

    [Test]
    public async Task EnsureDownloadedAsync_TreatsMissingAriaStatusAsCompleteWhenFileExists()
    {
        var aria = new FakeAria2Client
        {
            StatusFactory = _ => throw new ModelTransferStatusUnavailableException("GID gid-1 not found")
        };
        var fileSystem = new FakeFileSystem(exists: ["/tmp/model.safetensors"]);
        var sut = new DownloadCoordinator(aria, fileSystem);
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "model.safetensors",
            DestinationPath = "/tmp/model.safetensors",
            SourceUrl = "https://example.com/model"
        }]);

        ModelDownloadProgress? observed = null;
        sut.ProgressReporter = update =>
        {
            observed = update;
            return Task.CompletedTask;
        };

        await sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: false);

        Assert.That(observed, Is.Not.Null);
        Assert.That(observed!.Value.State, Is.EqualTo(ModelDownloadState.Complete));
    }

    [Test]
    public void EnsureDownloadedAsync_DoesNotTreatMissingAriaStatusAsComplete_WhenAriaSidecarExists()
    {
        var aria = new FakeAria2Client
        {
            StatusFactory = _ => throw new ModelTransferStatusUnavailableException("GID gid-1 not found")
        };
        var fileSystem = new FakeFileSystem(exists: ["/tmp/model.safetensors", "/tmp/model.safetensors.aria2"]);
        var sut = new DownloadCoordinator(
            aria,
            fileSystem,
            delay: new NoopDelay(),
            timeProvider: new FrozenTimeProvider(DateTimeOffset.UtcNow),
            downloadTimeout: TimeSpan.Zero);
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "model.safetensors",
            DestinationPath = "/tmp/model.safetensors",
            SourceUrl = "https://example.com/model"
        }]);

        Assert.ThrowsAsync<TimeoutException>(async () =>
            await sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: false));
    }

    [Test]
    public async Task EnsureDownloadedAsync_ReportsResumeSafeAverageSpeed_FromFirstAriaSample()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var delay = new AdvancingDelay(timeProvider);
        var fileSystem = new FakeFileSystem();
        var statuses = new Queue<Aria2DownloadStatus>([
            new Aria2DownloadStatus("active", 80, 100, 8, null, null),
            new Aria2DownloadStatus("active", 90, 100, 10, null, null),
            new Aria2DownloadStatus("complete", 100, 100, 0, null, null)
        ]);
        var aria = new FakeAria2Client
        {
            OnAddUri = () => fileSystem.SetFileSize("/tmp/model.safetensors", 100),
            StatusFactory = _ => statuses.Dequeue()
        };
        var sut = new DownloadCoordinator(aria, fileSystem, delay: delay, timeProvider: timeProvider);
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "model.safetensors",
            DestinationPath = "/tmp/model.safetensors",
            SizeBytes = 100,
            SourceUrl = "https://example.com/model"
        }]);
        var progress = new List<ModelDownloadProgress>();
        sut.ProgressReporter = update =>
        {
            progress.Add(update);
            return Task.CompletedTask;
        };

        await sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: false);

        var firstActive = progress.Single(update => update.State == ModelDownloadState.InProgress && update.BytesDownloaded == 80);
        Assert.That(firstActive.BytesPerSecond, Is.EqualTo(8));
        Assert.That(firstActive.AverageBytesPerSecond, Is.EqualTo(0));

        var secondActive = progress.Single(update =>
            update.State == ModelDownloadState.InProgress
            && update.BytesDownloaded == 90
            && update.BytesPerSecond > 0);
        Assert.That(secondActive.BytesPerSecond, Is.EqualTo(10));
        Assert.That(secondActive.AverageBytesPerSecond, Is.EqualTo(10));
    }

    [Test]
    public async Task EnsureDownloadedAsync_SumsCurrentSpeedAcrossActiveGids_AndClampsNegativeValues()
    {
        var fileSystem = new FakeFileSystem();
        var statuses = new Dictionary<string, Queue<Aria2DownloadStatus>>(StringComparer.Ordinal)
        {
            ["gid-1"] = new Queue<Aria2DownloadStatus>([
                new Aria2DownloadStatus("active", 10, 50, 12, null, null),
                new Aria2DownloadStatus("complete", 50, 50, 0, null, null)
            ]),
            ["gid-2"] = new Queue<Aria2DownloadStatus>([
                new Aria2DownloadStatus("active", 20, 50, -5, null, null),
                new Aria2DownloadStatus("complete", 50, 50, 0, null, null)
            ])
        };
        var aria = new FakeAria2Client
        {
            MetalinkGids = ["gid-1", "gid-2"],
            OnAddMetalink = () => fileSystem.SetFileSize("/tmp/model.safetensors", 100),
            StatusFactory = gid => statuses[gid].Dequeue()
        };
        var sut = new DownloadCoordinator(aria, fileSystem, delay: new NoopDelay());
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "model.safetensors",
            DestinationPath = "/tmp/model.safetensors",
            SizeBytes = 100,
            MetalinkXml = "<metalink />"
        }]);
        var progress = new List<ModelDownloadProgress>();
        sut.ProgressReporter = update =>
        {
            progress.Add(update);
            return Task.CompletedTask;
        };

        await sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: false);

        var active = progress.Single(update =>
            update.State == ModelDownloadState.InProgress
            && update.BytesDownloaded == 30
            && update.BytesPerSecond > 0);
        Assert.That(active.BytesPerSecond, Is.EqualTo(12));
    }

    [Test]
    public async Task EnsureDownloadedAsync_KeepsCompletedGidBytesInAverageSpeed()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var delay = new AdvancingDelay(timeProvider);
        var fileSystem = new FakeFileSystem();
        var statuses = new Dictionary<string, Queue<Aria2DownloadStatus>>(StringComparer.Ordinal)
        {
            ["gid-1"] = new Queue<Aria2DownloadStatus>([
                new Aria2DownloadStatus("active", 40, 50, 10, null, null),
                new Aria2DownloadStatus("complete", 50, 50, 0, null, null)
            ]),
            ["gid-2"] = new Queue<Aria2DownloadStatus>([
                new Aria2DownloadStatus("active", 10, 50, 10, null, null),
                new Aria2DownloadStatus("active", 20, 50, 10, null, null),
                new Aria2DownloadStatus("active", 30, 50, 10, null, null),
                new Aria2DownloadStatus("complete", 50, 50, 0, null, null)
            ])
        };
        var aria = new FakeAria2Client
        {
            MetalinkGids = ["gid-1", "gid-2"],
            OnAddMetalink = () => fileSystem.SetFileSize("/tmp/model.safetensors", 100),
            StatusFactory = gid => statuses[gid].Dequeue()
        };
        var sut = new DownloadCoordinator(aria, fileSystem, delay: delay, timeProvider: timeProvider);
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "model.safetensors",
            DestinationPath = "/tmp/model.safetensors",
            SizeBytes = 100,
            MetalinkXml = "<metalink />"
        }]);
        var progress = new List<ModelDownloadProgress>();
        sut.ProgressReporter = update =>
        {
            progress.Add(update);
            return Task.CompletedTask;
        };

        await sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: false);

        var afterOneGidCompleted = progress.Single(update =>
            update.State == ModelDownloadState.InProgress
            && update.BytesDownloaded == 80
            && update.BytesPerSecond > 0);
        Assert.That(afterOneGidCompleted.TotalBytes, Is.EqualTo(100));
        Assert.That(afterOneGidCompleted.BytesPerSecond, Is.EqualTo(10));
        Assert.That(afterOneGidCompleted.AverageBytesPerSecond, Is.EqualTo(15));
    }

    [TestCase(2)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(7)]
    [TestCase(19)]
    [TestCase(29)]
    public async Task EnsureDownloadedAsync_RetriesTransientAriaFailure_WithoutTrustingPreallocatedSize(int errorCode)
    {
        var fileSystem = new FakeFileSystem();
        var statuses = new Queue<Aria2DownloadStatus>([
            new Aria2DownloadStatus("error", 40, 100, 0, errorCode.ToString(), "transient problem"),
            new Aria2DownloadStatus("complete", 100, 100, 0, null, null)
        ]);
        var addCalls = 0;
        var aria = new FakeAria2Client
        {
            OnAddUri = () =>
            {
                addCalls++;
                if (addCalls == 1)
                {
                    // falloc can expose the final file length before aria2 has downloaded all bytes.
                    fileSystem.SetFileSize("/tmp/model.safetensors", 100);
                }
            },
            StatusFactory = _ => statuses.Dequeue()
        };
        var delay = new RecordingDelay();
        var sut = new DownloadCoordinator(aria, NewRetrySettings(), fileSystem, delay: delay);
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "model.safetensors",
            DestinationPath = "/tmp/model.safetensors",
            SizeBytes = 100,
            SourceUrl = "https://example.com/model"
        }]);
        var progress = new List<ModelDownloadProgress>();
        sut.ProgressReporter = update =>
        {
            progress.Add(update);
            return Task.CompletedTask;
        };

        await sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: false);

        Assert.That(aria.AddUriCalls, Is.EqualTo(2));
        Assert.That(delay.Delays, Is.EqualTo([TimeSpan.FromSeconds(2)]));
        var retrying = progress.Single(update => update.State == ModelDownloadState.Retrying);
        Assert.That(retrying.BytesDownloaded, Is.EqualTo(40));
        Assert.That(retrying.TotalBytes, Is.EqualTo(100));
        Assert.That(retrying.Percent, Is.EqualTo(40));
        Assert.That(retrying.BytesPerSecond, Is.EqualTo(0));
        Assert.That(retrying.Message, Does.Contain($"Retrying in 2s after aria2 error {errorCode}"));
        Assert.That(progress.Any(update => update.State == ModelDownloadState.Failed), Is.False);
    }

    [Test]
    public void EnsureDownloadedAsync_FailsImmediately_ForUnknownAriaError()
    {
        var aria = new FakeAria2Client
        {
            StatusFactory = _ => new Aria2DownloadStatus(
                "error",
                40,
                100,
                0,
                "1",
                "SSL/TLS handshake failure: unable to get local issuer certificate")
        };
        var delay = new RecordingDelay();
        var sut = new DownloadCoordinator(aria, NewRetrySettings(), new FakeFileSystem(), delay: delay);
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "model.safetensors",
            DestinationPath = "/tmp/model.safetensors",
            SizeBytes = 100,
            SourceUrl = "https://example.com/model"
        }]);
        var progress = new List<ModelDownloadProgress>();
        sut.ProgressReporter = update =>
        {
            progress.Add(update);
            return Task.CompletedTask;
        };

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: false));

        Assert.That(aria.AddUriCalls, Is.EqualTo(1));
        Assert.That(delay.Delays, Is.Empty);
        Assert.That(progress.Any(update => update.State == ModelDownloadState.Retrying), Is.False);
        var failed = progress.Single(update => update.State == ModelDownloadState.Failed);
        Assert.That(failed.BytesDownloaded, Is.EqualTo(40));
        Assert.That(failed.TotalBytes, Is.EqualTo(100));
        Assert.That(failed.Percent, Is.EqualTo(40));
        Assert.That(failed.Message, Does.Contain("aria2 reported error for gid-1: 1 SSL/TLS handshake failure"));
    }

    [Test]
    public async Task EnsureDownloadedAsync_CapsTransientRetryDelay_AndContinuesUntilSuccess()
    {
        var fileSystem = new FakeFileSystem();
        var statuses = new Queue<Aria2DownloadStatus>(Enumerable
            .Repeat(new Aria2DownloadStatus("error", 25, 100, 0, "29", "temporarily unavailable"), 7)
            .Append(new Aria2DownloadStatus("complete", 100, 100, 0, null, null)));
        var addCalls = 0;
        var aria = new FakeAria2Client
        {
            OnAddUri = () =>
            {
                addCalls++;
                if (addCalls == 8)
                {
                    fileSystem.SetFileSize("/tmp/model.safetensors", 100);
                }
            },
            StatusFactory = _ => statuses.Dequeue()
        };
        var delay = new RecordingDelay();
        var sut = new DownloadCoordinator(aria, NewRetrySettings(), fileSystem, delay: delay);
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "model.safetensors",
            DestinationPath = "/tmp/model.safetensors",
            SizeBytes = 100,
            SourceUrl = "https://example.com/model"
        }]);

        await sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: false);

        Assert.That(aria.AddUriCalls, Is.EqualTo(8));
        Assert.That(delay.Delays, Is.EqualTo(new[]
        {
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16),
            TimeSpan.FromSeconds(32),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(60)
        }));
    }

    [Test]
    public async Task EnsureDownloadsAsync_RetriesOnlyTransientlyFailedFiles()
    {
        var fileSystem = new FakeFileSystem();
        var attemptsByFile = new Dictionary<string, int>(StringComparer.Ordinal);
        var aria = new FakeAria2Client
        {
            AddUriGidFactory = options =>
            {
                var filename = (string)options["out"];
                var attempt = attemptsByFile.GetValueOrDefault(filename) + 1;
                attemptsByFile[filename] = attempt;
                if (filename.StartsWith("ready-", StringComparison.Ordinal) || attempt == 2)
                {
                    fileSystem.SetFileSize($"/tmp/{filename}", 100);
                }

                return $"{filename}:{attempt}";
            },
            StatusFactory = gid =>
                gid.StartsWith("retry-", StringComparison.Ordinal) && gid.EndsWith(":1", StringComparison.Ordinal)
                    ? new Aria2DownloadStatus("error", 50, 100, 0, "6", "network problem")
                    : new Aria2DownloadStatus("complete", 100, 100, 0, null, null)
        };
        var delay = new RecordingDelay();
        var sut = new DownloadCoordinator(aria, NewRetrySettings(), fileSystem, delay: delay);
        sut.SetActiveSession("session-1");
        var downloads = new[]
        {
            CreateDownloadRequest("ready-1"),
            CreateDownloadRequest("retry-1"),
            CreateDownloadRequest("ready-2"),
            CreateDownloadRequest("retry-2")
        };
        sut.RegisterDownloads("session-1", downloads);
        var progress = new List<ModelDownloadProgress>();
        sut.ProgressReporter = update =>
        {
            progress.Add(update);
            return Task.CompletedTask;
        };

        await sut.EnsureDownloadsAsync(downloads, CancellationToken.None, highPriority: false);

        Assert.That(attemptsByFile["ready-1.safetensors"], Is.EqualTo(1));
        Assert.That(attemptsByFile["ready-2.safetensors"], Is.EqualTo(1));
        Assert.That(attemptsByFile["retry-1.safetensors"], Is.EqualTo(2));
        Assert.That(attemptsByFile["retry-2.safetensors"], Is.EqualTo(2));
        Assert.That(delay.Delays, Is.EquivalentTo(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2) }));
        Assert.That(progress.Where(update => update.State == ModelDownloadState.Retrying).Select(update => update.ModelId),
            Is.EquivalentTo(new[] { "retry-1", "retry-2" }));
        Assert.That(progress.Count(update => update.State == ModelDownloadState.Complete), Is.EqualTo(4));
    }

    [Test]
    public async Task CancelSession_InterruptsTransientRetryDelay()
    {
        var aria = new FakeAria2Client
        {
            StatusFactory = _ => new Aria2DownloadStatus("error", 25, 100, 0, "6", "network problem")
        };
        var delay = new BlockingDelay();
        var sut = new DownloadCoordinator(aria, NewRetrySettings(), new FakeFileSystem(), delay: delay);
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "model.safetensors",
            DestinationPath = "/tmp/model.safetensors",
            SizeBytes = 100,
            SourceUrl = "https://example.com/model"
        }]);

        var downloadTask = sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: false);
        await delay.WaitUntilStartedAsync();

        sut.CancelSession("session-1");

        Assert.That(async () => await downloadTask, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(aria.AddUriCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task RegisterDownloads_ReplacementCancelsOmittedInFlightDownload()
    {
        var delay = new BlockingDelay();
        var aria = new FakeAria2Client
        {
            StatusFactory = _ => new Aria2DownloadStatus("active", 25, 100, 0, null, null)
        };
        var sut = new DownloadCoordinator(aria, new FakeFileSystem(), delay: delay);
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "omitted-model",
            Filename = "omitted.safetensors",
            DestinationPath = "/tmp/omitted.safetensors",
            SizeBytes = 100,
            SourceUrl = "https://example.com/omitted"
        }]);

        var downloadTask = sut.EnsureDownloadedAsync(
            "/tmp/omitted.safetensors",
            CancellationToken.None,
            highPriority: false);
        await delay.WaitUntilStartedAsync();

        sut.RegisterDownloads("session-1", [], replaceExisting: true);

        Assert.Multiple(() =>
        {
            Assert.That(async () => await downloadTask, Throws.InstanceOf<OperationCanceledException>());
            Assert.That(aria.ForceRemovedGids, Is.EquivalentTo(["gid-1"]));
        });
    }

    [Test]
    public async Task EnsureDownloadedAsync_ReportsZeroSpeeds_OnStartingCompleteAndFailedReports()
    {
        var completeFileSystem = new FakeFileSystem();
        var completeStatuses = new Queue<Aria2DownloadStatus>([
            new Aria2DownloadStatus("active", 50, 100, 100, null, null),
            new Aria2DownloadStatus("complete", 100, 100, 0, null, null)
        ]);
        var completeAria = new FakeAria2Client
        {
            OnAddUri = () => completeFileSystem.SetFileSize("/tmp/complete.safetensors", 100),
            StatusFactory = _ => completeStatuses.Dequeue()
        };
        var completeSut = new DownloadCoordinator(completeAria, completeFileSystem, delay: new NoopDelay());
        completeSut.SetActiveSession("session-1");
        completeSut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-complete",
            Filename = "complete.safetensors",
            DestinationPath = "/tmp/complete.safetensors",
            SizeBytes = 100,
            SourceUrl = "https://example.com/complete"
        }]);
        var completeProgress = new List<ModelDownloadProgress>();
        completeSut.ProgressReporter = update =>
        {
            completeProgress.Add(update);
            return Task.CompletedTask;
        };

        await completeSut.EnsureDownloadedAsync("/tmp/complete.safetensors", CancellationToken.None, highPriority: false);

        var starting = completeProgress.Single(update => update.Message == "starting download");
        Assert.That(starting.BytesPerSecond, Is.EqualTo(0));
        Assert.That(starting.AverageBytesPerSecond, Is.EqualTo(0));

        var complete = completeProgress.Single(update => update.State == ModelDownloadState.Complete);
        Assert.That(complete.BytesPerSecond, Is.EqualTo(0));
        Assert.That(complete.AverageBytesPerSecond, Is.EqualTo(0));

        var failedAria = new FakeAria2Client
        {
            StatusFactory = _ => new Aria2DownloadStatus("error", 0, 100, 50, "15", "download aborted")
        };
        var failedSut = new DownloadCoordinator(failedAria, new FakeFileSystem());
        failedSut.SetActiveSession("session-1");
        failedSut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-failed",
            Filename = "failed.safetensors",
            DestinationPath = "/tmp/failed.safetensors",
            SizeBytes = 100,
            SourceUrl = "https://example.com/failed"
        }]);
        var failedProgress = new List<ModelDownloadProgress>();
        failedSut.ProgressReporter = update =>
        {
            failedProgress.Add(update);
            return Task.CompletedTask;
        };

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await failedSut.EnsureDownloadedAsync("/tmp/failed.safetensors", CancellationToken.None, highPriority: false));

        var failed = failedProgress.Last(update => update.State == ModelDownloadState.Failed);
        Assert.That(failed.BytesPerSecond, Is.EqualTo(0));
        Assert.That(failed.AverageBytesPerSecond, Is.EqualTo(0));
    }

    [Test]
    public async Task CancelSession_CancelsTrackedDownload_AndForceRemovesActiveGids()
    {
        var delay = new BlockingDelay();
        var aria = new FakeAria2Client
        {
            StatusFactory = _ => new Aria2DownloadStatus("active", 25, 100, 0, null, null)
        };
        var sut = new DownloadCoordinator(aria, new FakeFileSystem(), delay: delay);
        sut.SetActiveSession("session-1");
        sut.RegisterDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "model.safetensors",
            DestinationPath = "/tmp/model.safetensors",
            SourceUrl = "https://example.com/model"
        }]);

        var downloadTask = sut.EnsureDownloadedAsync("/tmp/model.safetensors", CancellationToken.None, highPriority: false);
        await delay.WaitUntilStartedAsync();

        sut.CancelSession("session-1");

        Assert.That(async () => await downloadTask, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(aria.ForceRemovedGids, Is.EquivalentTo(["gid-1"]));
    }

    private sealed class FakeAria2Client : TestModelTransferBackend
    {
        public int AddUriCalls { get; private set; }
        public int AddMetalinkCalls { get; private set; }
        public Func<string, Aria2DownloadStatus>? StatusFactory { get; init; }
        public Action? OnAddUri { get; init; }
        public Action? OnAddMetalink { get; init; }
        public Func<IDictionary<string, object>, string?>? AddUriGidFactory { get; init; }
        public IReadOnlyList<string>? MetalinkGids { get; init; }
        public List<string> ForceRemovedGids { get; } = [];
        public ModelTransferCreateRequest? LastCreateRequest { get; private set; }

        public override Task<IReadOnlyList<ModelTransferHandle>> CreateAsync(
            ModelTransferCreateRequest request,
            CancellationToken cancellationToken)
        {
            LastCreateRequest = request;
            if (request.MetalinkContent is not null)
            {
                AddMetalinkCalls++;
                OnAddMetalink?.Invoke();
                IReadOnlyList<ModelTransferHandle> metalinkHandles = (MetalinkGids ?? ["gid-1"])
                    .Select(static gid => new ModelTransferHandle(gid))
                    .ToArray();
                return Task.FromResult(metalinkHandles);
            }

            AddUriCalls++;
            OnAddUri?.Invoke();
            var options = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["out"] = Path.GetFileName(request.DestinationPath),
                ["dir"] = Path.GetDirectoryName(request.DestinationPath) ?? string.Empty
            };
            var gid = AddUriGidFactory?.Invoke(options) ?? "gid-1";
            IReadOnlyList<ModelTransferHandle> handles = [new(gid)];
            return Task.FromResult(handles);
        }

        public override Task<ModelTransferSnapshot> GetStatusAsync(
            ModelTransferHandle handle,
            CancellationToken cancellationToken)
        {
            if (StatusFactory != null)
            {
                return Task.FromResult(StatusFactory(handle.ToString()).ToSnapshot(handle));
            }

            return Task.FromResult(Snapshot(handle, "complete", 1, 1));
        }

        public override Task RemoveAsync(ModelTransferHandle handle, CancellationToken cancellationToken)
        {
            ForceRemovedGids.Add(handle.ToString());
            return Task.CompletedTask;
        }
    }

    private sealed record Aria2DownloadStatus(
        string? Status,
        long CompletedLength,
        long TotalLength,
        long DownloadSpeed,
        string? ErrorCode,
        string? ErrorMessage)
    {
        public ModelTransferSnapshot ToSnapshot(ModelTransferHandle handle)
            => TestModelTransferBackend.Snapshot(
                handle,
                Status,
                CompletedLength,
                TotalLength,
                DownloadSpeed,
                ErrorCode,
                ErrorMessage);
    }

    private sealed class FakeFileSystem(IEnumerable<string>? exists = null, IReadOnlyDictionary<string, long>? fileSizes = null) : IFileSystem
    {
        private readonly HashSet<string> _existing = new(exists ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        private readonly Dictionary<string, long> _fileSizes = fileSizes is null
            ? new Dictionary<string, long>(StringComparer.Ordinal)
            : new Dictionary<string, long>(fileSizes, StringComparer.Ordinal);

        public string? NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return path.Trim();
        }

        public string? GetDirectoryName(string path) => Path.GetDirectoryName(path);

        public string? GetFileName(string path) => Path.GetFileName(path);

        public void CreateDirectory(string path)
        {
            // no-op
        }

        public bool FileExists(string path) => _existing.Contains(path);

        public void SetFileSize(string path, long size)
        {
            _existing.Add(path);
            _fileSizes[path] = size;
        }

        public long GetFileSize(string path)
            => _fileSizes.TryGetValue(path, out var size) ? size : 1024;
    }

    private sealed class BlockingDelay : IAsyncDelay
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task WaitUntilStartedAsync()
        {
            return _started.Task;
        }
    }

    private sealed class NoopDelay : IAsyncDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingDelay : IAsyncDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class AdvancingDelay(ManualTimeProvider timeProvider) : IAsyncDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            timeProvider.Advance(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public void Advance(TimeSpan delay)
        {
            _utcNow += delay;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class FrozenTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static Aria2Settings NewRetrySettings()
        => Aria2Settings.FromEnvironment(static _ => null, static () => "generated-secret");

    private static ModelDownloadRequest CreateDownloadRequest(string modelId)
        => new()
        {
            ModelId = modelId,
            Filename = $"{modelId}.safetensors",
            DestinationPath = $"/tmp/{modelId}.safetensors",
            SizeBytes = 100,
            SourceUrl = $"https://example.com/{modelId}"
        };
}
