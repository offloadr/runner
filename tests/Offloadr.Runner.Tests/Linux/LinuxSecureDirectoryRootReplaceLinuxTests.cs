namespace Offloadr.Runner.Tests;

public class LinuxSecureDirectoryRootReplaceLinuxTests
{
    [Test]
    public void CommitReplacing_ReplacesTheExpectedVersion()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.png"), "uploaded");
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            var version = secureRoot.GetVersion("a.png")!.Value;

            using (var placeholder = secureRoot.CreatePublication("a.png"))
            {
                Assert.That(placeholder.CommitReplacing(version), Is.True);
            }

            Assert.That(new FileInfo(Path.Combine(root, "a.png")).Length, Is.Zero);
            Assert.That(Directory.GetFiles(root), Has.Length.EqualTo(1), "the displaced file is gone");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void CommitReplacing_KeepsARewriteThatLandedAfterTheCheck(bool throughRename)
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "a.png");
            File.WriteAllText(path, "uploaded");
            var modified = File.GetLastWriteTimeUtc(path);
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            var version = secureRoot.GetVersion("a.png")!.Value;
            using var placeholder = secureRoot.CreatePublication("a.png");

            // The editor saves a new version of the same length and time, by an atomic rename
            // (new inode) or in place.
            if (throughRename)
            {
                var saved = Path.Combine(root, "a.png.new");
                File.WriteAllText(saved, "rewrite!");
                File.SetLastWriteTimeUtc(saved, modified);
                File.Move(saved, path, overwrite: true);
            }
            else
            {
                File.WriteAllText(path, "rewrite!");
                File.SetLastWriteTimeUtc(path, modified.AddSeconds(1));
            }

            Assert.That(placeholder.CommitReplacing(version), Is.False);
            Assert.That(File.ReadAllText(path), Is.EqualTo("rewrite!"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CommitReplacing_KeepsANewerRewriteThatLandsDuringTheRestore()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "a.png");
            File.WriteAllText(path, "uploaded");
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            var version = secureRoot.GetVersion("a.png")!.Value;
            using var placeholder = secureRoot.CreatePublication("a.png");

            // A first rewrite before the exchange, then a second one saved over the placeholder
            // right after it: the second is the newest and must survive.
            ReplaceAtomically(root, path, "rewrite-1");
            placeholder.AfterExchange = () => ReplaceAtomically(root, path, "rewrite-2");

            Assert.That(placeholder.CommitReplacing(version), Is.False);
            Assert.That(File.ReadAllText(path), Is.EqualTo("rewrite-2"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ReplaceAtomically(string root, string path, string content)
    {
        var staged = Path.Combine(root, $"staged-{Guid.NewGuid():N}");
        File.WriteAllText(staged, content);
        File.Move(staged, path, overwrite: true);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"runner-replace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
