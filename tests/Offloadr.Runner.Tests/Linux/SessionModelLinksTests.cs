using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests.Linux;

public class SessionModelLinksTests
{
    private string _root = null!;
    private string _sessionRoot = null!;
    private string _cache = null!;
    private SessionProcessManager.SessionPaths _paths;

    [SetUp]
    public void SetUp()
    {
        LinuxTestPrerequisites.RequireLinux();
        _root = Path.Combine(Path.GetTempPath(), $"session-model-links-{Guid.NewGuid():N}");
        _sessionRoot = Path.Combine(_root, "sessions");
        _cache = Path.Combine(_root, "cache");
        var home = Path.Combine(_sessionRoot, "session-1");
        Directory.CreateDirectory(Path.Combine(home, "models"));
        Directory.CreateDirectory(_cache);
        _paths = new SessionProcessManager.SessionPaths
        {
            HomeDirectory = home,
            ModelsDirectory = Path.Combine(home, "models"),
            ModelCacheDirectory = _cache,
        };
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    [Test]
    public void Ensure_LinksEachModelFromTheCacheIntoTheSession()
    {
        var checkpoint = Model("Stable-diffusion/base.safetensors");
        var lora = Model("Lora/style.safetensors");

        new SessionModelLinks(_sessionRoot).Ensure(_paths, [checkpoint, lora]);

        Assert.Multiple(() =>
        {
            Assert.That(LinkTarget("Stable-diffusion/base.safetensors"), Is.EqualTo(checkpoint.DestinationPath));
            Assert.That(LinkTarget("Lora/style.safetensors"), Is.EqualTo(lora.DestinationPath));
            Assert.That(Directory.ResolveLinkTarget(Path.Combine(_paths.ModelsDirectory, "Lora"), returnFinalTarget: false), Is.Null,
                "The directories stay real ones, so Forge can still write its own files into them.");
        });
    }

    [Test]
    public void Ensure_IsRepeatableAndRepointsAStaleLink()
    {
        var model = Model("Stable-diffusion/base.safetensors");
        var linkPath = Path.Combine(_paths.ModelsDirectory, "Stable-diffusion", "base.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        File.CreateSymbolicLink(linkPath, Path.Combine(_root, "elsewhere.safetensors"));
        var links = new SessionModelLinks(_sessionRoot);

        links.Ensure(_paths, [model]);
        links.Ensure(_paths, [model]);

        Assert.That(LinkTarget("Stable-diffusion/base.safetensors"), Is.EqualTo(model.DestinationPath));
    }

    [Test]
    public void Ensure_LeavesAFileTheSessionPutThere()
    {
        var linkPath = Path.Combine(_paths.ModelsDirectory, "Stable-diffusion", "base.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        File.WriteAllText(linkPath, "the session's own");

        new SessionModelLinks(_sessionRoot).Ensure(_paths, [Model("Stable-diffusion/base.safetensors")]);

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(linkPath), Is.EqualTo("the session's own"));
            Assert.That(new FileInfo(linkPath).LinkTarget, Is.Null);
        });
    }

    [Test]
    public void Ensure_DoesNotFollowADirectoryTheSessionSwappedForALink()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(_paths.ModelsDirectory, "Stable-diffusion"), outside);

        new SessionModelLinks(_sessionRoot).Ensure(_paths, [Model("Stable-diffusion/base.safetensors")]);

        Assert.That(Directory.EnumerateFileSystemEntries(outside), Is.Empty, "Nothing is created through the session's link.");
    }

    [Test]
    public void Ensure_IgnoresModelsOutsideTheCacheAndSessionsWithoutOne()
    {
        var outside = new ModelDownloadRequest
        {
            Filename = "base.safetensors",
            RelativeModelPath = "Stable-diffusion/base.safetensors",
            DestinationPath = Path.Combine(_root, "other", "base.safetensors"),
        };
        var links = new SessionModelLinks(_sessionRoot);

        links.Ensure(_paths, [outside]);
        links.Ensure(_paths with { ModelCacheDirectory = null }, [Model("Lora/style.safetensors")]);

        Assert.That(Directory.EnumerateFileSystemEntries(_paths.ModelsDirectory), Is.Empty);
    }

    private ModelDownloadRequest Model(string relativePath) => new()
    {
        Filename = Path.GetFileName(relativePath),
        RelativeModelPath = relativePath,
        DestinationPath = Path.Combine(_cache, relativePath),
    };

    private string? LinkTarget(string relativePath)
        => new FileInfo(Path.Combine(_paths.ModelsDirectory, relativePath)).LinkTarget;
}
