namespace Offloadr.Runner.Tests;

public class VfsShimNamingTests
{
    [Test]
    public void SourceAndDockerBuilds_UseOnlyEditorNeutralNames()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceDirectory = Path.Combine(repositoryRoot, "src", "Offloadr.Runner.Linux", "VfsShim");
        var dockerfiles = new[]
        {
            Path.Combine(repositoryRoot, "src", "Offloadr.Runner.Agent", "Dockerfile")
        };

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Combine(sourceDirectory, "model_vfs.c")), Is.True);
            Assert.That(
                File.Exists(Path.Combine(sourceDirectory, string.Concat("comfy", "_vfs.c"))),
                Is.False);
            foreach (var dockerfile in dockerfiles)
            {
                var content = File.ReadAllText(dockerfile);
                Assert.That(content, Does.Contain("VfsShim/model_vfs.c"));
                Assert.That(content, Does.Contain("liboffloadr_model_vfs.so"));
                Assert.That(content, Does.Not.Contain(string.Concat("comfy", "_vfs")));
                Assert.That(content, Does.Not.Contain(string.Concat("lib", "comfy", "vfs")));
            }
        });
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "runner.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
