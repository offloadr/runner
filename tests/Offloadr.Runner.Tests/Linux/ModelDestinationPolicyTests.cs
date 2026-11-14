using System.Runtime.InteropServices;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public class ModelDestinationPolicyTests
{
    private static readonly Aria2Settings DefaultAria2Settings = Aria2Settings.FromEnvironment(static _ => null, static () => "generated-secret");

    private string _root = string.Empty;
    private string _modelsRoot = string.Empty;
    private string _outside = string.Empty;

    [SetUp]
    public void SetUp()
    {
        LinuxTestPrerequisites.RequireLinux();
        _root = Path.Combine(Path.GetTempPath(), $"model-destination-{Guid.NewGuid():N}");
        _modelsRoot = Directory.CreateDirectory(Path.Combine(_root, "models")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void Confine_AcceptsDestinationsBeneathAConfiguredRoot()
    {
        var policy = new ModelDestinationPolicy([_modelsRoot]);
        Directory.CreateDirectory(Path.Combine(_modelsRoot, "loras"));
        File.WriteAllText(Path.Combine(_modelsRoot, "loras", "existing.safetensors"), "x");

        Assert.Multiple(() =>
        {
            Assert.That(
                policy.Confine(Path.Combine(_modelsRoot, "checkpoints", "new", "model.safetensors")),
                Is.EqualTo(Path.Combine(_modelsRoot, "checkpoints", "new", "model.safetensors")));
            Assert.That(
                policy.Confine(Path.Combine(_modelsRoot, "loras", "existing.safetensors")),
                Is.EqualTo(Path.Combine(_modelsRoot, "loras", "existing.safetensors")));
        });
    }

    [Test]
    public void Confine_AcceptsDestinationsThroughACanonicalRootLink()
    {
        var linkedRoot = Path.Combine(_root, "linked-models");
        Directory.CreateSymbolicLink(linkedRoot, _modelsRoot);
        var policy = new ModelDestinationPolicy([linkedRoot]);

        Assert.That(
            policy.Confine(Path.Combine(linkedRoot, "vae", "ae.safetensors")),
            Is.EqualTo(Path.Combine(linkedRoot, "vae", "ae.safetensors")));
    }

    [Test]
    public void Confine_RejectsDestinationsOutsideTheRoots()
    {
        var policy = new ModelDestinationPolicy([_modelsRoot]);

        Assert.Multiple(() =>
        {
            Assert.That(() => policy.Confine("/etc/cron.d/model"), Throws.InvalidOperationException);
            Assert.That(() => policy.Confine(Path.Combine(_modelsRoot, "..", "outside", "x.bin")), Throws.InvalidOperationException);
            Assert.That(() => policy.Confine(_modelsRoot), Throws.InvalidOperationException);
            Assert.That(() => policy.Confine(_modelsRoot + "-sibling/x.bin"), Throws.InvalidOperationException);
            Assert.That(() => policy.Confine(string.Empty), Throws.InvalidOperationException);
        });
    }

    [Test]
    public void Confine_RejectsSymlinkedDirectoryResolvingOutsideTheRoots()
    {
        Directory.CreateSymbolicLink(Path.Combine(_modelsRoot, "loras"), _outside);
        var policy = new ModelDestinationPolicy([_modelsRoot]);

        Assert.That(
            () => policy.Confine(Path.Combine(_modelsRoot, "loras", "model.safetensors")),
            Throws.InvalidOperationException);
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Confine_RejectsSymlinkedDirectoryInASharedWritableParent()
    {
        var shared = Directory.CreateDirectory(Path.Combine(_modelsRoot, "shared")).FullName;
        File.SetUnixFileMode(shared, (UnixFileMode)Convert.ToInt32("777", 8));
        Directory.CreateDirectory(Path.Combine(_modelsRoot, "real"));
        Directory.CreateSymbolicLink(Path.Combine(shared, "loras"), Path.Combine(_modelsRoot, "real"));
        var policy = new ModelDestinationPolicy([_modelsRoot]);

        Assert.That(
            () => policy.Confine(Path.Combine(shared, "loras", "model.safetensors")),
            Throws.InvalidOperationException);
    }

    [Test]
    public void Confine_RejectsSymlinkFifoAndDirectoryLeaves()
    {
        var policy = new ModelDestinationPolicy([_modelsRoot]);
        var outsideFile = Path.Combine(_outside, "target.bin");
        File.WriteAllText(outsideFile, "x");
        File.CreateSymbolicLink(Path.Combine(_modelsRoot, "linked.bin"), outsideFile);
        Assert.That(mkfifo(Path.Combine(_modelsRoot, "pipe.bin"), Convert.ToUInt32("600", 8)), Is.EqualTo(0));
        Directory.CreateDirectory(Path.Combine(_modelsRoot, "dir.bin"));

        Assert.Multiple(() =>
        {
            Assert.That(() => policy.Confine(Path.Combine(_modelsRoot, "linked.bin")), Throws.InvalidOperationException);
            Assert.That(() => policy.Confine(Path.Combine(_modelsRoot, "pipe.bin")), Throws.InvalidOperationException);
            Assert.That(() => policy.Confine(Path.Combine(_modelsRoot, "dir.bin")), Throws.InvalidOperationException);
        });
    }

    [Test]
    public void ConfineToSessionRoot_RejectsAnySymlinkedComponent()
    {
        Directory.CreateDirectory(Path.Combine(_modelsRoot, "real"));
        Directory.CreateSymbolicLink(Path.Combine(_modelsRoot, "loras"), Path.Combine(_modelsRoot, "real"));
        var linkedRoot = Path.Combine(_root, "linked-models");
        Directory.CreateSymbolicLink(linkedRoot, _outside);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => ModelDestinationPolicy.ConfineToSessionRoot(_modelsRoot, Path.Combine(_modelsRoot, "loras", "m.safetensors")),
                Throws.InvalidOperationException);
            Assert.That(
                () => ModelDestinationPolicy.ConfineToSessionRoot(linkedRoot, Path.Combine(linkedRoot, "m.safetensors")),
                Throws.InvalidOperationException);
            Assert.That(
                () => ModelDestinationPolicy.ConfineToSessionRoot(_modelsRoot, Path.Combine(_modelsRoot, "real", "m.safetensors")),
                Throws.Nothing);
        });
    }

    [Test]
    public void MaterializeForgeDownloads_RejectsSymlinkedSessionModelDirectory()
    {
        Directory.CreateSymbolicLink(Path.Combine(_modelsRoot, "Lora"), _outside);
        var download = new ModelDownloadRequest
        {
            Filename = "model.safetensors",
            RelativeModelPath = "Lora/model.safetensors"
        };

        Assert.That(
            () => ServiceClientManager.MaterializeModelDownloadPathsForSession(
                [download],
                "forge-neo",
                new SessionProcessManager.SessionPaths { ModelsDirectory = _modelsRoot }),
            Throws.InvalidOperationException);
    }

    [Test]
    public async Task SeedDownloads_RefusesDestinationsOutsideTheConfiguredRoots()
    {
        await using var service = new ModelDownloadService(
            new NoopTransferBackend(),
            DefaultAria2Settings,
            capabilities: null,
            new ModelDestinationPolicy([_modelsRoot]));
        var outsidePath = Path.Combine(_outside, "escape.safetensors");
        var download = new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "escape.safetensors",
            DestinationPath = outsidePath,
            SizeBytes = 4,
            SourceUrl = "https://models.example.test/escape.safetensors"
        };

        Assert.Multiple(() =>
        {
            Assert.That(() => service.SeedDownloads("session-1", [download]), Throws.InvalidOperationException);
            Assert.That(() => service.RegisterDownloads("session-1", [download]), Throws.InvalidOperationException);
            Assert.That(
                () => service.PreparePromptDownloadsAsync([download], CancellationToken.None, highPriority: false),
                Throws.InvalidOperationException);
        });
        Assert.That(File.Exists(outsidePath), Is.False);
    }

    [Test]
    public async Task SeedDownloads_CreatesPlaceholdersBeneathTheConfiguredRoots()
    {
        await using var service = new ModelDownloadService(
            new NoopTransferBackend(),
            DefaultAria2Settings,
            capabilities: null,
            new ModelDestinationPolicy([_modelsRoot]));
        var destination = Path.Combine(_modelsRoot, "checkpoints", "model.safetensors");

        service.SeedDownloads(
            "session-1",
            [
                new ModelDownloadRequest
                {
                    ModelId = "model-1",
                    Filename = "model.safetensors",
                    DestinationPath = destination,
                    SizeBytes = 4,
                    SourceUrl = "https://models.example.test/model.safetensors"
                }
            ]);

        Assert.That(File.Exists(destination), Is.True);
    }

    [Test]
    public async Task ReleaseForLocalModels_DeletesAFullDownloadInAModelRoot()
    {
        await using var service = new ModelDownloadService(
            new NoopTransferBackend(),
            DefaultAria2Settings,
            capabilities: null,
            new ModelDestinationPolicy([_modelsRoot]));
        var destination = Path.Combine(_modelsRoot, "Stable-diffusion", "model.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "downloaded earlier");
        var local = LocalModel(destination, new FileInfo(destination).Length);

        var projected = await service.ReleaseForLocalModelsAsync([local], static _ => true, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(projected, Is.EqualTo(new[] { local }));
            Assert.That(File.Exists(destination), Is.False, "The local model is linked there instead; it is not kept twice.");
        });
    }

    [Test]
    public async Task ReleaseForLocalModels_NeverDeletesAFullDownloadOfAnotherOrUnknownSize()
    {
        // A fresh service, as after a restart: it registered nothing, and the files were there before it.
        await using var service = new ModelDownloadService(
            new NoopTransferBackend(),
            DefaultAria2Settings,
            capabilities: null,
            new ModelDestinationPolicy([_modelsRoot]));
        var otherSize = Path.Combine(_modelsRoot, "Lora", "x.safetensors");
        var unknownSize = Path.Combine(_modelsRoot, "Lora", "y.safetensors");
        var unfinished = Path.Combine(_modelsRoot, "Lora", "z.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(otherSize)!);
        File.WriteAllText(otherSize, "someone else's model");
        File.WriteAllText(unknownSize, "a model");
        File.WriteAllText(unfinished, "partly");
        File.WriteAllText($"{unfinished}.aria2", "control");

        var projected = await service.ReleaseForLocalModelsAsync(
            [LocalModel(otherSize, 999), LocalModel(unknownSize, 0), LocalModel(unfinished, new FileInfo(unfinished).Length)],
            static _ => true,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(projected, Has.Count.EqualTo(3), "All go on to the projector, which refuses the occupied paths.");
            Assert.That(File.ReadAllText(otherSize), Is.EqualTo("someone else's model"));
            Assert.That(File.Exists(unknownSize), Is.True);
            Assert.That(File.Exists(unfinished), Is.True);
        });
    }

    [Test]
    public async Task ReleaseForLocalModels_JudgesAFullDownloadByTheFileNotByWhatWasRegisteredThere()
    {
        await using var service = new ModelDownloadService(
            new NoopTransferBackend(),
            DefaultAria2Settings,
            capabilities: null,
            new ModelDestinationPolicy([_modelsRoot]));
        var destination = Path.Combine(_modelsRoot, "Lora", "x.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, new byte[8]);

        // A 4-byte model is registered there before its download has started.
        service.SeedDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-4",
            Filename = "x.safetensors",
            DestinationPath = destination,
            SizeBytes = 4,
            SourceUrl = "https://models.example.test/x.safetensors",
        }]);
        var projected = await service.ReleaseForLocalModelsAsync([LocalModel(destination, 4)], static _ => true, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(projected, Has.Count.EqualTo(1), "Handed to the projector, which refuses the occupied path.");
            Assert.That(new FileInfo(destination).Length, Is.EqualTo(8), "The 8-byte model is not deleted.");
        });
    }

    [Test]
    public async Task ReleaseForLocalModels_FreesAFinishedDownloadDespiteAnotherSizeRegisteredThereSince()
    {
        await using var service = new ModelDownloadService(
            new NoopTransferBackend(),
            DefaultAria2Settings,
            capabilities: null,
            new ModelDestinationPolicy([_modelsRoot]));
        var destination = Path.Combine(_modelsRoot, "Lora", "x.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, new byte[8]);

        // Another session registered a 4-byte model there and stopped before downloading it.
        service.SeedDownloads("session-1", [new ModelDownloadRequest
        {
            ModelId = "model-4",
            Filename = "x.safetensors",
            DestinationPath = destination,
            SizeBytes = 4,
            SourceUrl = "https://models.example.test/x.safetensors",
        }]);
        service.CancelSession("session-1");
        var local = LocalModel(destination, 8);
        var projected = await service.ReleaseForLocalModelsAsync([local], static _ => true, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(projected, Is.EqualTo(new[] { local }));
            Assert.That(File.Exists(destination), Is.False, "The finished 8-byte copy is the model asked for, so it is replaced.");
        });
    }

    [Test]
    public async Task ReleaseForLocalModels_KeepsAFullDownloadWhenTheLocalModelGoesMeanwhile()
    {
        await using var service = new ModelDownloadService(
            new NoopTransferBackend(),
            DefaultAria2Settings,
            capabilities: null,
            new ModelDestinationPolicy([_modelsRoot]));
        var destination = Path.Combine(_modelsRoot, "Stable-diffusion", "model.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "downloaded earlier");
        var local = LocalModel(destination, new FileInfo(destination).Length);

        // The local model can be linked when it is chosen, but no longer when its download would go.
        var asked = 0;
        var projected = await service.ReleaseForLocalModelsAsync([local], _ => Interlocked.Increment(ref asked) == 1, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(projected, Is.Empty, "The download keeps serving.");
            Assert.That(File.Exists(destination), Is.True);
        });
    }

    [Test]
    public async Task ReleaseForLocalModels_LeavesALinkAndPathsOutsideTheModelRoots()
    {
        await using var service = new ModelDownloadService(
            new NoopTransferBackend(),
            DefaultAria2Settings,
            capabilities: null,
            new ModelDestinationPolicy([_modelsRoot]));
        var linked = Path.Combine(_modelsRoot, "Lora", "linked.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(linked)!);
        File.WriteAllText(Path.Combine(_outside, "source.safetensors"), "local");
        File.CreateSymbolicLink(linked, Path.Combine(_outside, "source.safetensors"));
        var outsidePath = Path.Combine(_outside, "theirs.safetensors");
        File.WriteAllText(outsidePath, "not in a model root");

        var projected = await service.ReleaseForLocalModelsAsync([LocalModel(linked), LocalModel(outsidePath)], static _ => true, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(projected, Has.Count.EqualTo(2));
            Assert.That(new FileInfo(linked).LinkTarget, Is.Not.Null);
            Assert.That(File.ReadAllText(outsidePath), Is.EqualTo("not in a model root"));
        });
    }

    private static ModelDownloadRequest LocalModel(string destination, long sizeBytes = 0) => new()
    {
        ModelId = "model-1",
        Filename = Path.GetFileName(destination),
        DestinationPath = destination,
        SizeBytes = sizeBytes,
        LocalSelectionHash = new string('a', 64),
    };

    [Test]
    public void CreatePlaceholder_CreatesAnEmptyFileAndItsDirectoriesOnce()
    {
        var policy = new ModelDestinationPolicy([_modelsRoot]);
        var destination = Path.Combine(_modelsRoot, "checkpoints", "nested", "model.safetensors");

        var created = policy.CreatePlaceholder(destination);
        var again = policy.CreatePlaceholder(destination);

        Assert.Multiple(() =>
        {
            Assert.That(created, Is.EqualTo(destination));
            Assert.That(again, Is.Null);
            Assert.That(new FileInfo(destination).Length, Is.Zero);
        });
    }

    [Test]
    public void CreatePlaceholder_CreatesThroughATrustedLinkAtItsCanonicalTarget()
    {
        var realVae = Directory.CreateDirectory(Path.Combine(_modelsRoot, "real-vae")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(_modelsRoot, "vae"), realVae);
        var policy = new ModelDestinationPolicy([_modelsRoot]);

        var created = policy.CreatePlaceholder(Path.Combine(_modelsRoot, "vae", "ae.safetensors"));

        Assert.Multiple(() =>
        {
            Assert.That(created, Is.EqualTo(Path.Combine(_modelsRoot, "vae", "ae.safetensors")));
            Assert.That(File.Exists(Path.Combine(realVae, "ae.safetensors")), Is.True);
        });
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void CreatePlaceholder_RefusesALinkInASharedWritableDirectory()
    {
        var shared = Directory.CreateDirectory(Path.Combine(_modelsRoot, "shared")).FullName;
        File.SetUnixFileMode(shared, (UnixFileMode)Convert.ToInt32("777", 8));
        Directory.CreateSymbolicLink(Path.Combine(shared, "loras"), _outside);
        var policy = new ModelDestinationPolicy([_modelsRoot, _outside]);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => policy.CreatePlaceholder(Path.Combine(shared, "loras", "model.safetensors")),
                Throws.InvalidOperationException);
            Assert.That(File.Exists(Path.Combine(_outside, "model.safetensors")), Is.False);
        });
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);

    private sealed class NoopTransferBackend : TestModelTransferBackend
    {
    }
}
