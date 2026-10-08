using Offloadr.Common.V1;

namespace Offloadr.Runner.Tests;

public class SessionArtifactPathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "artifact-root");

    [TestCase("", "image.png", "image.png")]
    [TestCase(null, "image.png", "image.png")]
    [TestCase("nested", "image.png", "nested/image.png")]
    [TestCase("a/b", "image.png", "a/b/image.png")]
    [TestCase("a\\b", "image.png", "a/b/image.png")]
    [TestCase("a//b/", "image.png", "a/b/image.png")]
    [TestCase("", "..image.png", "..image.png")]
    public void TryResolve_AcceptsNamesBeneathRoot(string? subfolder, string filename, string expectedRelativePath)
    {
        var resolved = SessionArtifactPaths.TryResolve(Root, subfolder, filename, out var fullPath, out var relativePath);

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.True);
            Assert.That(relativePath, Is.EqualTo(expectedRelativePath));
            Assert.That(
                fullPath,
                Is.EqualTo(Path.GetFullPath(Path.Combine([Root, .. expectedRelativePath.Split('/')]))));
        });
    }

    [TestCase("..", "image.png")]
    [TestCase("../outside", "image.png")]
    [TestCase("nested/../../outside", "image.png")]
    [TestCase("nested/./x", "image.png")]
    [TestCase("..\\outside", "image.png")]
    [TestCase("/etc", "passwd")]
    [TestCase("\\etc", "passwd")]
    [TestCase("", "/etc/passwd")]
    [TestCase("", "../passwd")]
    [TestCase("", "nested/image.png")]
    [TestCase("", "nested\\image.png")]
    [TestCase("", "..")]
    [TestCase("", ".")]
    [TestCase("", "")]
    [TestCase("", " ")]
    [TestCase("", "bad\0name")]
    [TestCase("bad\0dir", "image.png")]
    public void TryResolve_RejectsEscapingOrMalformedNames(string? subfolder, string filename)
    {
        Assert.That(
            SessionArtifactPaths.TryResolve(Root, subfolder, filename, out _, out _),
            Is.False);
    }

    [Test]
    public void PrepareForgePromptJson_DoesNotCreateDirectoriesThroughSymlinkedSubfolder()
    {
        LinuxTestPrerequisites.RequireLinux();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"forge-reference-{Guid.NewGuid():N}");
        try
        {
            var paths = new SessionProcessManager.SessionPaths
            {
                TempDirectory = Path.Combine(tempRoot, "temp"),
                OutputDirectory = Path.Combine(tempRoot, "output")
            };
            var outside = Directory.CreateDirectory(Path.Combine(tempRoot, "outside")).FullName;
            Directory.CreateDirectory(paths.TempDirectory);
            Directory.CreateSymbolicLink(Path.Combine(paths.TempDirectory, "forge-queue"), outside);
            var references = new[]
            {
                new PromptArtifactReference
                {
                    Placeholder = "offloadr://forge-artifact/submission-1/0",
                    Filename = "image.png",
                    Type = "temp",
                    Subfolder = "forge-queue/submission-1"
                }
            };

            Assert.That(
                () => SessionProcessManager.PrepareForgePromptJson(
                    """{"path":"offloadr://forge-artifact/submission-1/0"}""",
                    references,
                    paths),
                Throws.InstanceOf<IOException>());
            Assert.That(Directory.EnumerateFileSystemEntries(outside), Is.Empty);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [TestCase("../outside", "image.png")]
    [TestCase("", "/etc/passwd")]
    [TestCase("", "../image.png")]
    public void PrepareForgePromptJson_RejectsEscapingArtifactReferences(string subfolder, string filename)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"forge-reference-{Guid.NewGuid():N}");
        try
        {
            var paths = new SessionProcessManager.SessionPaths
            {
                TempDirectory = Path.Combine(tempRoot, "temp"),
                OutputDirectory = Path.Combine(tempRoot, "output")
            };
            var references = new[]
            {
                new PromptArtifactReference
                {
                    Placeholder = "offloadr://forge-artifact/submission-1/0",
                    Filename = filename,
                    Type = "temp",
                    Subfolder = subfolder
                }
            };

            Assert.That(
                () => SessionProcessManager.PrepareForgePromptJson(
                    """{"path":"offloadr://forge-artifact/submission-1/0"}""",
                    references,
                    paths),
                Throws.InvalidOperationException);
            Assert.That(Directory.Exists(Path.Combine(tempRoot, "outside")), Is.False);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
