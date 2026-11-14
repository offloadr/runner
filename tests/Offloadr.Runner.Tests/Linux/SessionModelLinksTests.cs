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
    public void Ensure_RefusesTheModelWhenTheSessionPutAFileOfItsOwnThere_AndLinksTheRest()
    {
        var linkPath = Path.Combine(_paths.ModelsDirectory, "Stable-diffusion", "base.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        File.WriteAllText(linkPath, "the session's own");
        var other = Model("Lora/style.safetensors");

        Assert.That(
            () => new SessionModelLinks(_sessionRoot).Ensure(_paths, [Model("Stable-diffusion/base.safetensors"), other]),
            Throws.InvalidOperationException,
            "The editor must not run on weights other than those asked for.");
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(linkPath), Is.EqualTo("the session's own"), "The session's file is kept.");
            Assert.That(new FileInfo(linkPath).LinkTarget, Is.Null);
            Assert.That(LinkTarget("Lora/style.safetensors"), Is.EqualTo(other.DestinationPath), "The other model is linked all the same.");
        });
    }

    [Test]
    public void Ensure_DoesNotFollowADirectoryTheSessionSwappedForALink()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(_paths.ModelsDirectory, "Stable-diffusion"), outside);

        Assert.That(
            () => new SessionModelLinks(_sessionRoot).Ensure(_paths, [Model("Stable-diffusion/base.safetensors")]),
            Throws.InvalidOperationException);
        Assert.That(Directory.EnumerateFileSystemEntries(outside), Is.Empty, "Nothing is created through the session's link.");
    }

    [Test]
    public async Task Ensure_FromPromptsAtOnce_LinksTheSameNewModelWithoutFailingAny()
    {
        var links = new SessionModelLinks(_sessionRoot);
        var checkpoint = Model("Stable-diffusion/base.safetensors");
        var lora = Model("Lora/style.safetensors");

        var prompts = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => links.Ensure(_paths, [checkpoint, lora])))
            .ToArray();

        Assert.That(async () => await Task.WhenAll(prompts), Throws.Nothing);
        Assert.Multiple(() =>
        {
            Assert.That(LinkTarget("Stable-diffusion/base.safetensors"), Is.EqualTo(checkpoint.DestinationPath));
            Assert.That(LinkTarget("Lora/style.safetensors"), Is.EqualTo(lora.DestinationPath));
        });
    }

    [Test]
    public void Ensure_GivesTheFoldersItMakesToTheSessionUser()
    {
        LinuxTestPrerequisites.RequireRoot();
        // The session owns its models folder, as it does once its home has been handed over.
        RunChown("12345:12345", _paths.ModelsDirectory);

        new SessionModelLinks(_sessionRoot).Ensure(_paths, [Model("ESRGAN/upscaler.pth")]);

        Assert.Multiple(() =>
        {
            Assert.That(OwnerOf(Path.Combine(_paths.ModelsDirectory, "ESRGAN")), Is.EqualTo("12345:12345"),
                "Forge can still write the upscalers it fetches itself beside the link.");
            Assert.That(LinkTarget("ESRGAN/upscaler.pth"), Is.EqualTo(Path.Combine(_cache, "ESRGAN", "upscaler.pth")));
        });
    }

    private static void RunChown(string owner, string path)
    {
        using var chown = System.Diagnostics.Process.Start("/bin/chown", [owner, path])!;
        chown.WaitForExit();
        Assert.That(chown.ExitCode, Is.Zero);
    }

    private static string OwnerOf(string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/stat", ["-c", "%u:%g", path]) { RedirectStandardOutput = true };
        using var stat = System.Diagnostics.Process.Start(start)!;
        var owner = stat.StandardOutput.ReadToEnd().Trim();
        stat.WaitForExit();
        return owner;
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
