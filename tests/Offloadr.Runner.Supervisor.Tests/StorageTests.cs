using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hosting;
using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor.Tests;

public class RunnerImagesTests
{
    private const string ComfyLatest = "ghcr.io/offloadr/runner-comfyui:latest";
    private const string ForgeNeo = "ghcr.io/offloadr/runner-forge-neo:neo";
    private const string ComfyRepository = "ghcr.io/offloadr/runner-comfyui";

    private static readonly LaunchTemplate Comfy = BuiltInLaunchPlanCatalog.Templates.Single(template => template.Image == ComfyLatest);

    private static StoredImage Running => new("sha256:aaa", [ComfyLatest], [ComfyRepository + "@sha256:da"], 12_000_000_000);

    private static StoredImage Forge => new("sha256:bbb", [ForgeNeo], ["ghcr.io/offloadr/runner-forge-neo@sha256:db"], 9_000_000_000);

    private static StoredImage Older => new("sha256:ccc", [], [ComfyRepository + "@sha256:dc"], 11_000_000_000);

    private static StoredImage SomeoneElses => new("sha256:ddd", ["ubuntu:24.04"], ["ubuntu@sha256:dd"], 80_000_000);

    private static SupervisorView RunningView => SupervisorView.Initial(DateTimeOffset.UtcNow) with
    {
        State = SupervisorState.Running,
        Plan = BuiltInLaunchPlanCatalog.Plan(),
        ActiveTemplate = Comfy,
        Image = new ImageInfo("sha256:aaa", ComfyRepository + "@sha256:da", [ComfyLatest]),
    };

    [Test]
    public void Classify_ListsOnlyRunnerImages_WhatEachIsFor_RunningFirst()
    {
        var images = RunnerImages.Classify([SomeoneElses, Older, Forge, Running], RunningView, new RunnerSettings());

        Assert.That(images.Select(image => (image.Id, image.Use, image.Name, image.CanRemove)), Is.EqualTo(new[]
        {
            ("sha256:aaa", RunnerImageUse.Running, ComfyLatest, false),
            ("sha256:bbb", RunnerImageUse.NotUsed, ForgeNeo, true),
            ("sha256:ccc", RunnerImageUse.OlderVersion, ComfyRepository, true),
        }));
    }

    [Test]
    public void Classify_KeepsTheChosenEditorsImage_WhileTheRunnerIsStopped()
    {
        var stopped = SupervisorView.Initial(DateTimeOffset.UtcNow) with { Image = RunningView.Image };

        var images = RunnerImages.Classify([Running, Forge], stopped, new RunnerSettings { SelectedEditorTemplate = "forge-neo:neo" });

        Assert.That(images.Select(image => (image.Id, image.Use)), Is.EqualTo(new[]
        {
            ("sha256:bbb", RunnerImageUse.Chosen),
            ("sha256:aaa", RunnerImageUse.NotUsed),
        }));
    }

    [Test]
    public void Classify_KeepsAnImageATemplatePinsByDigest()
    {
        var pinned = new LaunchTemplate("comfyui-pinned", "comfyui", ComfyRepository + "@sha256:dc", "ComfyUI pinned");
        var view = SupervisorView.Initial(DateTimeOffset.UtcNow) with
        {
            Plan = new LaunchPlan([pinned], "comfyui-pinned", TimeSpan.FromMinutes(5), LaunchPlanSource.ControlPlane),
        };

        var images = RunnerImages.Classify([Older], view, new RunnerSettings());

        Assert.That(images.Single().Use, Is.EqualTo(RunnerImageUse.Chosen));
    }

    [Test]
    public void Classify_NamesOnlyTheRunnersTags_OfAnImageSomeoneTaggedToo()
    {
        var tagged = Forge with { RepoTags = [ForgeNeo, "my-forge:keep"] };

        var image = RunnerImages.Classify([tagged], RunningView, new RunnerSettings()).Single();

        Assert.Multiple(() =>
        {
            Assert.That(image.Name, Is.EqualTo(ForgeNeo));
            Assert.That(image.Tags, Is.EqualTo(new[] { ForgeNeo }));
        });
    }

    [TestCase("ghcr.io/offloadr/runner-comfyui:latest", "ghcr.io/offloadr/runner-comfyui")]
    [TestCase("ghcr.io/offloadr/runner-comfyui@sha256:abc", "ghcr.io/offloadr/runner-comfyui")]
    [TestCase("ghcr.io/offloadr/runner-comfyui:latest@sha256:abc", "ghcr.io/offloadr/runner-comfyui")]
    [TestCase("localhost:5000/runner:dev", "localhost:5000/runner")]
    [TestCase("localhost:5000/runner", "localhost:5000/runner")]
    public void Repository_DropsTheTagAndDigest(string reference, string repository)
        => Assert.That(RunnerImages.Repository(reference), Is.EqualTo(repository));

    [Test]
    public async Task Remove_TakesTheRunnerTagsOff_AndTheEngineDropsTheImage()
    {
        var engine = new ImageStore(Running, Forge);
        var images = new RunnerImages(() => RunningView, engine, new FakeSettingsStore(new RunnerSettings()));

        var result = await images.RemoveAsync("sha256:bbb");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(ImageRemoval.Removed));
            Assert.That(engine.Removals, Is.EqualTo(new[] { ForgeNeo }), "By name, never by ID or force.");
            Assert.That(engine.Has("sha256:aaa"), Is.True);
        });
    }

    [Test]
    public async Task Remove_AnOlderVersion_GoesByItsDigest()
    {
        var engine = new ImageStore(Running, Older);
        var images = new RunnerImages(() => RunningView, engine, new FakeSettingsStore(new RunnerSettings()));

        Assert.Multiple(async () =>
        {
            Assert.That(await images.RemoveAsync("sha256:ccc"), Is.EqualTo(ImageRemoval.Removed));
            Assert.That(engine.Removals, Is.EqualTo(new[] { ComfyRepository + "@sha256:dc" }));
        });
    }

    [Test]
    public async Task Remove_ByTag_ThenTheDigest_WhereTheEngineKeepsAnUntaggedImage()
    {
        // The containerd image store keeps the image under its digest once its last tag is gone.
        var engine = new ImageStore(Running, Forge) { LastTagKeepsDigests = true };
        var images = new RunnerImages(() => RunningView, engine, new FakeSettingsStore(new RunnerSettings()));

        Assert.Multiple(async () =>
        {
            Assert.That(await images.RemoveAsync("sha256:bbb"), Is.EqualTo(ImageRemoval.Removed));
            Assert.That(engine.Removals, Is.EqualTo(new[] { ForgeNeo, "ghcr.io/offloadr/runner-forge-neo@sha256:db" }));
        });
    }

    [Test]
    public async Task Remove_LeavesAnImageSomeoneTaggedThemselves()
    {
        var engine = new ImageStore(Running, Forge with { RepoTags = [ForgeNeo, "my-forge:keep"] }) { LastTagKeepsDigests = true };
        var images = new RunnerImages(() => RunningView, engine, new FakeSettingsStore(new RunnerSettings()));

        Assert.Multiple(async () =>
        {
            Assert.That(await images.RemoveAsync("sha256:bbb"), Is.EqualTo(ImageRemoval.Kept));
            Assert.That(engine.Removals, Is.EqualTo(new[] { ForgeNeo }), "Their name, and so the image, stays.");
        });
    }

    [Test]
    public async Task Remove_ReportsAnImageTheEngineKept()
    {
        var engine = new ImageStore(Running, Forge) { KeepEverything = true };
        var images = new RunnerImages(() => RunningView, engine, new FakeSettingsStore(new RunnerSettings()));

        Assert.That(await images.RemoveAsync("sha256:bbb"), Is.EqualTo(ImageRemoval.Kept));
    }

    [Test]
    public async Task Remove_NeverTouchesTheRunningOrChosenImage()
    {
        var engine = new ImageStore(Running, Forge);
        var images = new RunnerImages(() => RunningView, engine, new FakeSettingsStore(new RunnerSettings { SelectedEditorTemplate = "forge-neo:neo" }));

        Assert.Multiple(async () =>
        {
            Assert.That(await images.RemoveAsync("sha256:aaa"), Is.EqualTo(ImageRemoval.NotAllowed));
            Assert.That(engine.Removals, Is.Empty);
        });
    }

    [Test]
    public async Task Remove_LooksAgain_SoAnEditorChosenSinceThePageWasDrawnKeepsItsImage()
    {
        var settings = new FakeSettingsStore(new RunnerSettings());
        var stopped = SupervisorView.Initial(DateTimeOffset.UtcNow);
        var engine = new ImageStore(Running, Forge);
        var images = new RunnerImages(() => stopped, engine, settings);
        Assert.That((await images.ListAsync()).Single(image => image.Id == "sha256:bbb").CanRemove, Is.True);

        await settings.SaveAsync(new RunnerSettings { SelectedEditorTemplate = "forge-neo:neo" });

        Assert.Multiple(async () =>
        {
            Assert.That(await images.RemoveAsync("sha256:bbb"), Is.EqualTo(ImageRemoval.NotAllowed));
            Assert.That(engine.Removals, Is.Empty);
        });
    }

    [TestCase(SupervisorState.Checking)]
    [TestCase(SupervisorState.ResolvingPlan)]
    [TestCase(SupervisorState.Pulling)]
    [TestCase(SupervisorState.Starting)]
    [TestCase(SupervisorState.Stopping)]
    public async Task Remove_WaitsWhileTheRunnerIsBusy(SupervisorState state)
    {
        var engine = new ImageStore(Running, Forge);
        var images = new RunnerImages(() => RunningView with { State = state }, engine, new FakeSettingsStore(new RunnerSettings()));

        Assert.Multiple(async () =>
        {
            Assert.That(await images.RemoveAsync("sha256:bbb"), Is.EqualTo(ImageRemoval.Busy));
            Assert.That(engine.Removals, Is.Empty);
        });
    }

    [Test]
    public async Task Remove_OfAnImageAlreadyGone_IsDone()
    {
        var engine = new ImageStore(Running);
        var images = new RunnerImages(() => RunningView, engine, new FakeSettingsStore(new RunnerSettings()));

        Assert.That(await images.RemoveAsync("sha256:bbb"), Is.EqualTo(ImageRemoval.Removed));
    }

    /// <summary>An engine that only holds images: tags and digests, removed one reference at a time, never by force.</summary>
    private sealed class ImageStore(params StoredImage[] images) : IContainerEngine
    {
        private readonly Dictionary<string, StoredImage> _images = images.ToDictionary(image => image.Id, StringComparer.Ordinal);

        /// <summary>Removing the last tag leaves the image under its digests, as the containerd image store does.</summary>
        public bool LastTagKeepsDigests { get; init; }

        /// <summary>Every removal is refused, as for an image a container uses.</summary>
        public bool KeepEverything { get; init; }

        public List<string> Removals { get; } = [];

        public bool Has(string id) => _images.ContainsKey(id);

        public Task<IReadOnlyList<StoredImage>> ListImagesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<StoredImage>>([.. _images.Values]);

        public Task<ImageInfo?> InspectImageAsync(string image, CancellationToken cancellationToken)
            => Task.FromResult(_images.TryGetValue(image, out var found)
                ? new ImageInfo(found.Id, found.RepoDigests.FirstOrDefault(), found.RepoTags)
                : null);

        public Task<bool> RemoveImageAsync(string reference, CancellationToken cancellationToken)
        {
            Removals.Add(reference);
            var image = _images.Values.FirstOrDefault(candidate => candidate.RepoTags.Contains(reference) || candidate.RepoDigests.Contains(reference));
            if (image is null || KeepEverything)
            {
                return Task.FromResult(false);
            }

            var left = image with
            {
                RepoTags = image.RepoTags.Where(tag => tag != reference).ToArray(),
                RepoDigests = image.RepoDigests.Where(digest => digest != reference).ToArray(),
            };
            if (left.RepoTags.Count > 0 || (LastTagKeepsDigests && left.RepoDigests.Count > 0))
            {
                _images[image.Id] = left;
            }
            else
            {
                _images.Remove(image.Id);
            }

            return Task.FromResult(true);
        }

        public Task<ContainerEngineInfo> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ContainerEnginePin?> CurrentContextAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PullAsync(string image, Action<string> onProgress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> RunDetachedAsync(ContainerSpec spec, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ContainerInfo?> InspectContainerAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> ReadEnvironmentVariableAsync(string container, string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> ReadFileAsync(string container, string path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task FollowLogsAsync(string container, int tail, Action<string> onLine, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task StopAsync(string container, TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RemoveAsync(string container, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<GpuAccessCheck> CheckGpuAccessAsync(string image, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

public class ListImagesTests
{
    private static readonly string Docker = Environment.ProcessPath!;

    private const string Inspect = """
        [
          {"Id": "sha256:aaa", "RepoTags": ["ghcr.io/offloadr/runner-comfyui:latest"], "RepoDigests": ["ghcr.io/offloadr/runner-comfyui@sha256:da"], "Size": 12000000000, "Created": "2026-11-01T10:00:00Z"},
          {"Id": "sha256:ccc", "RepoTags": [], "RepoDigests": ["ghcr.io/offloadr/runner-comfyui@sha256:dc"], "Size": 11000000000},
          {"Id": "sha256:eee", "RepoTags": ["<none>:<none>"], "RepoDigests": ["<none>@<none>"], "Size": 5}
        ]
        """;

    [Test]
    public void Json_ReadsNamesDigestsAndSize()
    {
        var images = DockerCliJson.Images(Inspect);

        Assert.Multiple(() =>
        {
            Assert.That(images.Select(image => image.Id), Is.EqualTo(new[] { "sha256:aaa", "sha256:ccc", "sha256:eee" }));
            Assert.That(images[0].RepoTags, Is.EqualTo(new[] { "ghcr.io/offloadr/runner-comfyui:latest" }));
            Assert.That(images[0].Size, Is.EqualTo(12_000_000_000));
            Assert.That(images[0].Created, Is.EqualTo(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero)));
            Assert.That(images[1].RepoTags, Is.Empty);
            Assert.That(images[2].RepoTags, Is.Empty, "<none> is no name.");
            Assert.That(images[2].RepoDigests, Is.Empty);
            Assert.That(DockerCliJson.Images("not json"), Is.Empty);
        });
    }

    [Test]
    public async Task List_InspectsEachImageOnce()
    {
        var processes = new FakeProcessRunner()
            .On("image ls", 0, stdout: "sha256:aaa\nsha256:ccc\nsha256:aaa\n")
            .On("image inspect", 0, stdout: Inspect);
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        var images = await engine.ListImagesAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(images, Has.Count.EqualTo(3));
            Assert.That(processes.Calls[0].Arguments, Is.EqualTo(new[] { "image", "ls", "--quiet", "--no-trunc" }));
            Assert.That(processes.Calls[1].Arguments, Is.EqualTo(new[] { "image", "inspect", "sha256:aaa", "sha256:ccc" }));
        });
    }

    [Test]
    public async Task List_InspectsManyImagesInParts()
    {
        var ids = Enumerable.Range(0, DockerCliContainerEngine.ImagesPerInspect * 2 + 5).Select(index => $"sha256:{index:x4}");
        var processes = new FakeProcessRunner().On("image ls", 0, stdout: string.Join('\n', ids)).On("image inspect", 0, stdout: "[]");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        await engine.ListImagesAsync(CancellationToken.None);

        Assert.That(processes.Calls.Count(call => call.Arguments[0] == "image" && call.Arguments[1] == "inspect"), Is.EqualTo(3));
    }

    [Test]
    public async Task List_SkipsAnImageRemovedSinceTheListing()
    {
        var processes = new FakeProcessRunner()
            .On("image ls", 0, stdout: "sha256:aaa\nsha256:gone")
            .On("image inspect", 1, stdout: Inspect, stderr: "Error response from daemon: No such image: sha256:gone");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        Assert.That(await engine.ListImagesAsync(CancellationToken.None), Has.Count.EqualTo(3));
    }

    [Test]
    public void List_ReportsAnEngineThatDoesNotAnswer()
    {
        var processes = new FakeProcessRunner().On("image ls", 1, stderr: "error during connect: the docker daemon is not running");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        Assert.That(async () => await engine.ListImagesAsync(CancellationToken.None), Throws.InstanceOf<ContainerEngineException>());
    }
}

public class ModelsFolderUsageTests
{
    private string _folder = null!;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "runner-models-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(_folder, "checkpoints"));
        Directory.CreateDirectory(Path.Combine(_folder, ModelsFolderUsage.StagingFolderName, "runner", "run"));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_folder, recursive: true);

    [Test]
    public void Measure_CountsModels_AndUnfinishedDownloadsApart()
    {
        File.WriteAllBytes(Path.Combine(_folder, "checkpoints", "a.safetensors"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(_folder, "b.safetensors"), new byte[24]);
        File.WriteAllBytes(Path.Combine(_folder, ModelsFolderUsage.StagingFolderName, "runner", "run", "c.part"), new byte[500]);

        Assert.That(ModelsFolderUsage.Measure(_folder), Is.EqualTo(new ModelsFolderUsage(2, 1024, 500)));
    }

    [Test]
    public void Measure_StopsWhenCancelled()
    {
        File.WriteAllBytes(Path.Combine(_folder, "b.safetensors"), new byte[24]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.That(() => ModelsFolderUsage.Measure(_folder, cancelled.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public void Measure_ReportsAMissingFolder()
        => Assert.That(() => ModelsFolderUsage.Measure(Path.Combine(_folder, "missing")), Throws.InstanceOf<DirectoryNotFoundException>());

    [TestCase(0L, "0 B")]
    [TestCase(1023L, "1023 B")]
    [TestCase(1536L, "1.5 KB")]
    [TestCase(12_884_901_888L, "12.0 GB")]
    [TestCase(214_748_364_800L, "200 GB")]
    public void Format_ReadsLikeWindows(long bytes, string text)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Assert.That(ModelsFolderUsage.Format(bytes), Is.EqualTo(text));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }
}
