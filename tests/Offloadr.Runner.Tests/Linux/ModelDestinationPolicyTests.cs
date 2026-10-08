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
                () => service.EnsureDownloadsAsync([download], CancellationToken.None, highPriority: false),
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

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);

    private sealed class NoopTransferBackend : TestModelTransferBackend
    {
    }
}
