namespace Offloadr.Runner.Tests;

public class LinuxSessionResidueLinuxTests
{
    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Cleanup_RemovesTheUsersSystemVObjects()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireRoot();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/ipcmk");
        LinuxTestPrerequisites.RequireCommand("/usr/bin/ipcrm");
        LinuxTestPrerequisites.RequireCommand("/usr/sbin/runuser");

        await using var user = await LinuxTestUser.CreateAsync();
        // Shared memory, a message queue and a semaphore set, left behind by an exited process.
        foreach (var kind in new[] { "-M 4096", "-Q", "-S 1" })
        {
            await VfsShimLinuxTests.RunProcessAsync("/usr/sbin/runuser", ["-u", user.Name, "--", "/bin/sh", "-c", "ipcmk " + kind], Path.GetTempPath());
        }

        Assert.That(LinuxSessionResidue.CountIpcObjectsOwnedBy(user.UserId), Is.EqualTo(3));
        var commandRunner = new LinuxCommandRunner();
        var strategy = new LinuxUserIsolationStrategy(
            commandRunner,
            static (userId, token) => LinuxProcessReaper.KillAllOwnedByAsync(userId, token),
            residueRoots: []);

        await strategy.CleanupAsync(new PreparedSessionIdentity(user.Name, string.Empty, CleanupIdentity: true, user.UserId), CancellationToken.None);

        Assert.That(LinuxSessionResidue.CountIpcObjectsOwnedBy(user.UserId), Is.Zero, "nothing the next user of the uid would own");
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void SetModeNoFollow_RefusesALinkAndLeavesItsTarget()
    {
        LinuxTestPrerequisites.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), $"runner-nofollow-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var target = Path.Combine(root, "target");
            File.WriteAllText(target, string.Empty);
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var link = Path.Combine(root, "swapped");
            File.CreateSymbolicLink(link, target);
            var open = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;

            Assert.Throws<IOException>(() => LinuxSecureDirectoryRoot.SetModeNoFollow(link, open));
            Assert.That(File.GetUnixFileMode(target), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));

            LinuxSecureDirectoryRoot.SetModeNoFollow(target, open);
            Assert.That(File.GetUnixFileMode(target), Is.EqualTo(open));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
