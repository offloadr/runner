namespace Offloadr.Runner.Tests;

public class LinuxSessionResidueLinuxTests
{
    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task RemoveEntriesOwnedBy_ReachesAFileAnyDepthDown()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireRoot();
        LinuxTestPrerequisites.RequireCommand("/bin/chown");

        var root = Path.Combine(Path.GetTempPath(), $"runner-residue-{Guid.NewGuid():N}");
        try
        {
            // Deeper than any fixed cutoff, through directories that belong to someone else.
            var deep = root;
            for (var level = 0; level < 40; level++)
            {
                deep = Path.Combine(deep, $"d{level}");
            }

            Directory.CreateDirectory(deep);
            var left = Path.Combine(deep, "left.png");
            await File.WriteAllTextAsync(left, "session data");
            await VfsShimLinuxTests.RunProcessAsync("/bin/chown", ["54321:54321", left], root);

            var removed = LinuxSessionResidue.RemoveEntriesOwnedBy(54321, [root], out var remaining);

            Assert.Multiple(() =>
            {
                Assert.That(removed, Is.EqualTo(1));
                Assert.That(remaining, Is.Zero);
                Assert.That(File.Exists(left), Is.False);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
