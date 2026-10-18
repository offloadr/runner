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
    public void CountIpcObjectsOwnedBy_IsUnknownWhenATableIsMissing()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"runner-sysvipc-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "shm"), "key shmid perms size cpid lpid nattch uid gid\n0 7 600 4096 1 1 0 54321 54321\n");
            File.WriteAllText(Path.Combine(directory, "msg"), "key msqid perms cbytes qnum lspid lrpid uid gid\n");

            // The semaphore table is masked: what it would list is unknown.
            Assert.That(LinuxSessionResidue.CountIpcObjectsOwnedBy(54321, directory), Is.Null);

            File.WriteAllText(Path.Combine(directory, "sem"), "key semid perms nsems uid gid\n");
            Assert.That(LinuxSessionResidue.CountIpcObjectsOwnedBy(54321, directory), Is.EqualTo(1));
            var absent = Path.Combine(directory, "absent");
            Assert.That(LinuxSessionResidue.CountIpcObjectsOwnedBy(54321, absent, ipcUnavailable: () => true), Is.Zero, "a kernel without System V IPC");
            Assert.That(LinuxSessionResidue.CountIpcObjectsOwnedBy(54321, absent, ipcUnavailable: () => false), Is.Null, "a hidden directory");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void CurrentResidueRoots_AddWritableMemoryMountsButNotDev()
    {
        LinuxTestPrerequisites.RequireLinux();

        var roots = LinuxSessionResidue.CurrentResidueRoots();

        Assert.That(roots, Is.SupersetOf(LinuxSessionResidue.SharedTemporaryRoots));
        Assert.That(roots, Has.None.EqualTo("/dev"));
        Assert.That(roots, Has.None.StartsWith("/proc/").And.None.StartsWith("/sys/"));
        Assert.That(LinuxSessionResidue.CurrentResidueRoots(mounts: null), Is.Null, "an unreadable mount table");
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task RemoveEntriesOwnedBy_KeepsSomeoneElsesFilesInTheUsersDirectory()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireRoot();
        LinuxTestPrerequisites.RequireCommand("/bin/chown");

        var root = Path.Combine(Path.GetTempPath(), $"runner-foreign-{Guid.NewGuid():N}");
        try
        {
            // The session made the folder and a file in it; a root service added its own file.
            var folder = Path.Combine(root, "shared-cache");
            Directory.CreateDirectory(folder);
            var sessionFile = Path.Combine(folder, "session.bin");
            var serviceFile = Path.Combine(folder, "service.bin");
            await File.WriteAllTextAsync(sessionFile, "session data");
            await File.WriteAllTextAsync(serviceFile, "service data");
            await VfsShimLinuxTests.RunProcessAsync("/bin/chown", ["54321:54321", folder, sessionFile], root);

            LinuxSessionResidue.RemoveEntriesOwnedBy(54321, [root], out var remaining);

            Assert.Multiple(() =>
            {
                Assert.That(remaining, Is.Zero);
                Assert.That(File.Exists(sessionFile), Is.False);
                Assert.That(File.Exists(serviceFile), Is.True, "someone else's file stays");
                Assert.That(SealedStorageProbe.ReadEntryStatus(folder)!.Value.OwnerId, Is.Zero, "the folder is root's now");
                Assert.That(File.GetUnixFileMode(folder) & (UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute), Is.EqualTo((UnixFileMode)0), "and closed to later sessions");
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void RemoveEntriesOwnedBy_SkipsOnlyARootThatDoesNotExist()
    {
        LinuxTestPrerequisites.RequireLinux();

        var missing = Path.Combine(Path.GetTempPath(), $"runner-missing-{Guid.NewGuid():N}");
        LinuxSessionResidue.RemoveEntriesOwnedBy(54321, [missing], out var remaining);

        Assert.That(remaining, Is.Zero);
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
