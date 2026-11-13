using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public class ModelDownloadsDisabledTests
{
    private static readonly Aria2Settings DefaultAria2Settings = Aria2Settings.FromEnvironment(static _ => null, static () => "generated-secret");

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("off", false)]
    [TestCase(" ON ", true)]
    [TestCase("on", true)]
    public void Options_AreOffUnlessTurnedOn(string? raw, bool enabled)
    {
        var options = ModelDownloadOptions.FromEnvironment(name => name == "RUNNER_MODEL_DOWNLOADS" ? raw : null);

        Assert.Multiple(() =>
        {
            Assert.That(options.Enabled, Is.EqualTo(enabled));
            Assert.That(options.InvalidValue, Is.Null);
        });
    }

    [Test]
    public void Options_ATypoKeepsThemOffAndIsReported()
    {
        var options = ModelDownloadOptions.FromEnvironment(name => name == "RUNNER_MODEL_DOWNLOADS" ? "yes" : null);

        Assert.Multiple(() =>
        {
            Assert.That(options.Enabled, Is.False);
            Assert.That(options.InvalidValue, Is.EqualTo("yes"));
        });
    }

    [Test]
    public void AgentOptions_ReadTheSetting()
    {
        var options = RunnerAgentOptions.FromEnvironment(
            name => name == "RUNNER_MODEL_DOWNLOADS" ? "on" : null,
            static _ => false);

        Assert.That(options.ModelDownloads.Enabled, Is.True);
    }

    [Test]
    public async Task Off_RegistersNothingAndReportsEachModelAsFailed()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "diffusion_models", "flux-dev.safetensors");
            var backend = new RecordingBackend();
            await using var service = new ModelDownloadService(backend, DefaultAria2Settings, downloadsEnabled: false);
            var reports = new List<ModelDownloadProgress>();
            var reported = new TaskCompletionSource();
            service.ProgressReporter = progress =>
            {
                lock (reports)
                {
                    reports.Add(progress);
                }

                reported.TrySetResult();
                return Task.CompletedTask;
            };

            service.SetActiveSession("session-1");
            service.SeedDownloads("session-1", [Remote(destinationPath), Local(Path.Combine(root, "loras", "mine.safetensors"))]);
            await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(destinationPath), Is.False, "No placeholder: the editor must not list a model it cannot load.");
                Assert.That(backend.Created, Is.Zero);
                Assert.That(reports, Has.Count.EqualTo(1), "Only the remote model is refused; a local one is projected elsewhere.");
                Assert.That(reports[0].State, Is.EqualTo(ModelDownloadState.Failed));
                Assert.That(reports[0].Filename, Is.EqualTo("flux-dev.safetensors"));
                Assert.That(reports[0].Message, Does.Contain("turned off"));
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task Off_RefusesAPromptThatNeedsADownload()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationPath = Path.Combine(root, "diffusion_models", "flux-dev.safetensors");
            var backend = new RecordingBackend();
            await using var service = new ModelDownloadService(backend, DefaultAria2Settings, downloadsEnabled: false);
            service.SetActiveSession("session-1");

            var refused = Assert.ThrowsAsync<ModelDownloadsDisabledException>(async () =>
                await service.PreparePromptDownloadsAsync([Remote(destinationPath)], CancellationToken.None, highPriority: true));

            Assert.Multiple(() =>
            {
                Assert.That(refused!.Message, Does.Contain("flux-dev.safetensors"));
                Assert.That(backend.Created, Is.Zero);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task Off_APromptWithOnlyLocalModelsGoesAhead()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new RecordingBackend();
            await using var service = new ModelDownloadService(backend, DefaultAria2Settings, downloadsEnabled: false);
            service.SetActiveSession("session-1");

            var demand = await service.PreparePromptDownloadsAsync(
                [Local(Path.Combine(root, "loras", "mine.safetensors"))],
                CancellationToken.None,
                highPriority: true);

            Assert.Multiple(() =>
            {
                Assert.That(demand.IsCompletedSuccessfully, Is.True);
                Assert.That(backend.Created, Is.Zero);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static ModelDownloadRequest Remote(string destinationPath) => new()
    {
        ModelId = "model-1",
        Filename = Path.GetFileName(destinationPath),
        DestinationPath = destinationPath,
        SizeBytes = 8,
        SourceUrl = "https://models.example.test/" + Path.GetFileName(destinationPath),
    };

    private static ModelDownloadRequest Local(string destinationPath) => new()
    {
        ModelId = "model-2",
        Filename = Path.GetFileName(destinationPath),
        DestinationPath = destinationPath,
        SizeBytes = 8,
        LocalSelectionHash = new string('a', 64),
    };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "model-downloads-off-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private sealed class RecordingBackend : TestModelTransferBackend
    {
        private int _created;

        public int Created => Volatile.Read(ref _created);

        public override Task<IReadOnlyList<ModelTransferHandle>> CreateAsync(ModelTransferCreateRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _created);
            return base.CreateAsync(request, cancellationToken);
        }
    }
}
