using System.Diagnostics;

namespace Offloadr.Runner.Tests;

public class SealedHostLockdownLinuxTests
{
    private const UnixFileMode Mode0755 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void FindSharedWritableDirectories_FindsWorldWritableDirectoriesWithoutFollowingLinks()
    {
        LinuxTestPrerequisites.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), $"runner-lockdown-{Guid.NewGuid():N}");
        try
        {
            var shallow = Path.Combine(root, "tmp");
            var nested = Path.Combine(root, "var", "crash");
            var deep = Path.Combine(root, "a", "b", "c", "d");
            var inside = Path.Combine(shallow, "inside");
            var deepInside = Path.Combine(shallow, "x", "y", "z");
            foreach (var directory in new[] { shallow, nested, deep, inside, deepInside })
            {
                Directory.CreateDirectory(directory);
                File.SetUnixFileMode(directory, Mode0755 | UnixFileMode.OtherWrite);
            }

            var writableFile = Path.Combine(shallow, "x", "shared.log");
            File.WriteAllText(writableFile, string.Empty);
            File.SetUnixFileMode(writableFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
            File.WriteAllText(Path.Combine(shallow, "private.log"), string.Empty);
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), shallow);

            var found = SealedHostLockdown.FindSharedWritableDirectories(root);
            var mounts = SealedHostLockdown.FindSharedWritableDirectories("/");

            // Every world-writable directory counts, however deep below directories a session can
            // traverse; below one, world-writable files do too.
            Assert.That(found, Is.EquivalentTo(new[] { shallow, nested, deep, inside, deepInside, writableFile }));
            // /dev/shm lies on another filesystem below /dev, which is never entered.
            Assert.That(mounts, Has.None.EqualTo("/dev/shm"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void FindSharedWritableDirectories_FollowsMountsBelowSharedDirectories()
    {
        var shared = Mode0755 | UnixFileMode.OtherWrite;
        // /workspace is a world-writable volume with a writable folder and a nested disk mount
        // (not world-writable at its top) inside. /tmp/ram is memory-backed and /media read-only;
        // /proc is another filesystem below nothing shared. None of those three is entered.
        var entries = new Dictionary<string, (LinuxEntryStatus Status, ulong Device, string[] Children)>(StringComparer.Ordinal)
        {
            ["/"] = (new(true, false, 0, Mode0755), 1, ["/workspace", "/tmp", "/media", "/proc"]),
            ["/workspace"] = (new(true, false, 0, shared), 2, ["/workspace/jobs", "/workspace/mnt"]),
            ["/workspace/jobs"] = (new(true, false, 0, shared), 2, []),
            ["/workspace/mnt"] = (new(true, false, 0, Mode0755), 3, ["/workspace/mnt/deep"]),
            ["/workspace/mnt/deep"] = (new(true, false, 0, shared), 3, ["/workspace/mnt/deep/file"]),
            ["/workspace/mnt/deep/file"] = (new(false, false, 0, shared, IsRegularFile: true), 3, []),
            ["/tmp"] = (new(true, false, 0, shared), 1, ["/tmp/ram"]),
            ["/tmp/ram"] = (new(true, false, 0, Mode0755), 5, ["/tmp/ram/open"]),
            ["/tmp/ram/open"] = (new(true, false, 0, shared), 5, []),
            ["/media"] = (new(true, false, 0, shared), 6, ["/media/open"]),
            ["/media/open"] = (new(true, false, 0, shared), 6, []),
            ["/proc"] = (new(true, false, 0, Mode0755), 4, ["/proc/open"]),
            ["/proc/open"] = (new(true, false, 0, shared), 4, []),
        };
        var mounts = new SealedHostLockdown.Mount[]
        {
            new("/", "overlay", false),
            new("/workspace/mnt", "ext4", false),
            new("/workspace", "ext4", false),
            new("/tmp/ram", "tmpfs", false),
            new("/media", "ext4", true),
            new("/proc", "proc", false),
        };

        var found = SealedHostLockdown.FindSharedWritableDirectories(
            "/",
            path => entries.TryGetValue(path, out var entry) ? entry.Status : null,
            path => entries.TryGetValue(path, out var entry) ? new LinuxFileIdentity(entry.Device, InodeOf(path), 0) : null,
            path => entries[path].Children,
            mounts);

        Assert.That(found, Is.EquivalentTo(new[]
        {
            "/workspace", "/workspace/jobs", "/workspace/mnt/deep", "/workspace/mnt/deep/file", "/tmp", "/media",
        }));
    }

    [Test]
    public void FindSharedWritableDirectories_WalksEveryWritableDiskMount()
    {
        var shared = Mode0755 | UnixFileMode.OtherWrite;
        // /data is a 0755 volume, below nothing shared, with a scratch folder anyone may write
        // and a file anyone may write, which a session can reach by name.
        // /sys/fs/cgroup is a kernel filesystem and is not entered.
        var entries = new Dictionary<string, (LinuxEntryStatus Status, ulong Device, string[] Children)>(StringComparer.Ordinal)
        {
            ["/"] = (new(true, false, 0, Mode0755), 1, ["/data", "/sys"]),
            ["/data"] = (new(true, false, 0, Mode0755), 7, ["/data/scratch", "/data/models", "/data/open.log"]),
            ["/data/scratch"] = (new(true, false, 0, shared), 7, ["/data/scratch/cache.bin"]),
            ["/data/scratch/cache.bin"] = (new(false, false, 0, shared, IsRegularFile: true), 7, []),
            ["/data/models"] = (new(true, false, 0, Mode0755), 7, ["/data/models/deep"]),
            ["/data/models/deep"] = (new(true, false, 0, shared), 7, []),
            ["/data/open.log"] = (new(false, false, 0, shared, IsRegularFile: true), 7, []),
            ["/sys"] = (new(true, false, 0, Mode0755), 8, ["/sys/fs"]),
            ["/sys/fs"] = (new(true, false, 0, Mode0755), 8, ["/sys/fs/cgroup"]),
            ["/sys/fs/cgroup"] = (new(true, false, 0, Mode0755), 9, ["/sys/fs/cgroup/open"]),
            ["/sys/fs/cgroup/open"] = (new(true, false, 0, shared), 9, []),
        };
        var mounts = new SealedHostLockdown.Mount[]
        {
            new("/", "overlay", false),
            new("/data", "ext4", false),
            new("/sys", "sysfs", false),
            new("/sys/fs/cgroup", "cgroup2", false),
        };

        var found = SealedHostLockdown.FindSharedWritableDirectories(
            "/",
            path => entries.TryGetValue(path, out var entry) ? entry.Status : null,
            path => entries.TryGetValue(path, out var entry) ? new LinuxFileIdentity(entry.Device, InodeOf(path), 0) : null,
            path => entries[path].Children,
            mounts);

        Assert.That(found, Is.EquivalentTo(new[] { "/data/scratch", "/data/scratch/cache.bin", "/data/models/deep", "/data/open.log" }));
    }

    [Test]
    public void FindSharedWritableDirectories_VisitsADirectoryOnceThroughABindMountLoop()
    {
        var shared = Mode0755 | UnixFileMode.OtherWrite;
        // /data/loop is /data bind-mounted below itself: the same directory, reached again.
        var entries = new Dictionary<string, (LinuxEntryStatus Status, ulong Inode, string[] Children)>(StringComparer.Ordinal)
        {
            ["/"] = (new(true, false, 0, Mode0755), 1, ["/data"]),
            ["/data"] = (new(true, false, 0, Mode0755), 2, ["/data/loop", "/data/open"]),
            ["/data/loop"] = (new(true, false, 0, Mode0755), 2, ["/data/loop/loop", "/data/loop/open"]),
            ["/data/open"] = (new(true, false, 0, shared), 3, []),
            ["/data/loop/open"] = (new(true, false, 0, shared), 3, []),
            ["/data/loop/loop"] = (new(true, false, 0, Mode0755), 2, []),
        };

        var found = SealedHostLockdown.FindSharedWritableDirectories(
            "/",
            path => entries.TryGetValue(path, out var entry) ? entry.Status : null,
            path => entries.TryGetValue(path, out var entry) ? new LinuxFileIdentity(1, entry.Inode, 0) : null,
            path => entries[path].Children,
            []);

        Assert.That(found, Is.EqualTo(new[] { "/data/open" }));
    }

    [Test]
    public void FindSharedWritableDirectories_ReportsDirectoriesItCannotList()
    {
        var shared = Mode0755 | UnixFileMode.OtherWrite;
        // /tmp/fuse refuses to be listed; /tmp/gone disappeared after it was seen.
        var entries = new Dictionary<string, (LinuxEntryStatus Status, string[]? Children)>(StringComparer.Ordinal)
        {
            ["/"] = (new(true, false, 0, Mode0755), ["/tmp"]),
            ["/tmp"] = (new(true, false, 0, shared), ["/tmp/fuse", "/tmp/gone", "/tmp/open"]),
            ["/tmp/fuse"] = (new(true, false, 0, Mode0755), null),
            ["/tmp/gone"] = (new(true, false, 0, Mode0755), null),
            ["/tmp/open"] = (new(true, false, 0, shared), []),
        };
        var unlisted = new List<string>();

        var found = SealedHostLockdown.FindSharedWritableDirectories(
            "/",
            path => entries.TryGetValue(path, out var entry) ? entry.Status : null,
            path => entries.ContainsKey(path) ? new LinuxFileIdentity(1, InodeOf(path), 0) : null,
            path => entries[path].Children ?? throw (path == "/tmp/gone"
                ? new DirectoryNotFoundException(path)
                : new UnauthorizedAccessException(path)),
            [],
            unlisted);

        Assert.That(found, Is.EquivalentTo(new[] { "/tmp", "/tmp/open" }));
        Assert.That(unlisted, Is.EqualTo(new[] { "/tmp/fuse" }));
    }

    [Test]
    public void Scan_ReportsResidueOfReusableUidsAndAnUnreadableMountTable()
    {
        var shared = Mode0755 | UnixFileMode.OtherWrite;
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        // Under /tmp: residue of a removed session user (uid 1001, any mode), a live service's
        // folder (uid 999) and root's own. Outside shared directories ownership is not judged.
        var entries = new Dictionary<string, (LinuxEntryStatus Status, string[] Children)>(StringComparer.Ordinal)
        {
            ["/"] = (new(true, false, 0, Mode0755), ["/tmp", "/home"]),
            ["/tmp"] = (new(true, false, 0, shared), ["/tmp/old", "/tmp/service", "/tmp/agent"]),
            ["/tmp/old"] = (new(true, false, 1001, Private), ["/tmp/old/notes.txt"]),
            ["/tmp/old/notes.txt"] = (new(false, false, 1001, Private, IsRegularFile: true), []),
            ["/tmp/service"] = (new(true, false, 999, Private), []),
            ["/tmp/agent"] = (new(true, false, 0, Mode0755), []),
            ["/home"] = (new(true, false, 0, Mode0755), ["/home/old"]),
            ["/home/old"] = (new(true, false, 1001, Private), []),
        };

        var scan = SealedHostLockdown.Scan(
            "/",
            path => entries.TryGetValue(path, out var entry) ? entry.Status : null,
            path => entries.ContainsKey(path) ? new LinuxFileIdentity(1, InodeOf(path), 0) : null,
            path => entries[path].Children,
            mounts: null,
            isReusableOwner: userId => userId == 1001);

        Assert.That(scan.ReusablyOwned, Is.EqualTo(new[] { "/tmp/old", "/tmp/old/notes.txt" }));
        Assert.That(scan.SharedWritable, Is.EqualTo(new[] { "/tmp" }));
        Assert.That(scan.Unchecked, Is.EqualTo(new[] { "/proc/self/mounts" }), "mounted filesystems would go unseen");
    }

    [Test]
    public void Scan_CountsEntriesWritableThroughAReusableGroupAndUnreadableOnesAsUnchecked()
    {
        var groupWritable = Mode0755 | UnixFileMode.GroupWrite;
        // /opt/cache is root:4242 0775, 4242 being a group id useradd may hand out again; /opt/team
        // is the same for a live group. /opt/odd cannot be stat'ed although it is listed.
        var entries = new Dictionary<string, (LinuxEntryStatus Status, string[] Children)>(StringComparer.Ordinal)
        {
            ["/"] = (new(true, false, 0, Mode0755), ["/opt"]),
            ["/opt"] = (new(true, false, 0, Mode0755), ["/opt/cache", "/opt/team", "/opt/odd"]),
            ["/opt/cache"] = (new(true, false, 0, groupWritable, GroupId: 4242), ["/opt/cache/item.bin"]),
            ["/opt/cache/item.bin"] = (new(false, false, 0, Mode0755, IsRegularFile: true), []),
            ["/opt/team"] = (new(true, false, 0, groupWritable, GroupId: 100), []),
        };

        var scan = SealedHostLockdown.Scan(
            "/",
            path => entries.TryGetValue(path, out var entry) ? entry.Status : null,
            path => entries.ContainsKey(path) ? new LinuxFileIdentity(1, InodeOf(path), 0) : null,
            path => entries[path].Children,
            mounts: [],
            isReusableOwner: static _ => false,
            isReusableGroup: groupId => groupId == 4242);

        Assert.That(scan.SharedWritable, Is.EqualTo(new[] { "/opt/cache" }));
        // The fake /opt/odd does not exist on this machine, so it counts as gone, not unchecked.
        Assert.That(scan.Unchecked, Is.Empty);
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Scan_CountsAnExistingEntryWithoutMetadataAsUnchecked()
    {
        LinuxTestPrerequisites.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), $"runner-unreadable-{Guid.NewGuid():N}");
        try
        {
            var odd = Path.Combine(root, "odd");
            Directory.CreateDirectory(odd);

            var scan = SealedHostLockdown.Scan(
                root,
                path => path == odd ? null : SealedStorageProbe.ReadEntryStatus(path),
                path => LinuxFileIdentityReader.TryRead(path, out var identity) ? identity : null,
                Directory.GetFileSystemEntries,
                mounts: [],
                isReusableOwner: static _ => false);

            Assert.That(scan.Unchecked, Is.EqualTo(new[] { odd }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Scan_WalksABindAliasAgainBelowASharedDirectory()
    {
        var shared = Mode0755 | UnixFileMode.OtherWrite;
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        // /srv/cache (root-only path) is bind-mounted at /tmp/cache: the same directory, seen
        // first where sessions cannot reach it. Its residue counts once seen below /tmp.
        var entries = new Dictionary<string, (LinuxEntryStatus Status, ulong Inode, string[] Children)>(StringComparer.Ordinal)
        {
            ["/"] = (new(true, false, 0, Mode0755), 1, ["/srv", "/tmp"]),
            ["/srv"] = (new(true, false, 0, Private), 2, ["/srv/cache"]),
            ["/srv/cache"] = (new(true, false, 0, Mode0755), 3, ["/srv/cache/old"]),
            ["/srv/cache/old"] = (new(true, false, 1001, Private), 4, []),
            ["/tmp"] = (new(true, false, 0, shared), 5, ["/tmp/cache"]),
            ["/tmp/cache"] = (new(true, false, 0, Mode0755), 3, ["/tmp/cache/old"]),
            ["/tmp/cache/old"] = (new(true, false, 1001, Private), 4, []),
        };

        var scan = SealedHostLockdown.Scan(
            "/",
            path => entries.TryGetValue(path, out var entry) ? entry.Status : null,
            path => entries.TryGetValue(path, out var entry) ? new LinuxFileIdentity(1, entry.Inode, 0) : null,
            path => entries[path].Children,
            mounts: [],
            isReusableOwner: userId => userId == 1001);

        Assert.That(scan.ReusablyOwned, Is.EqualTo(new[] { "/tmp/cache/old" }));
    }

    [Test]
    public void Scan_ClosesTheMaskOfAnAclThatMayGrantWrite()
    {
        var groupWritable = Mode0755 | UnixFileMode.GroupWrite;
        // root:root, group write is the ACL's mask: a named entry may let any uid write.
        var entries = new Dictionary<string, (LinuxEntryStatus Status, string[] Children)>(StringComparer.Ordinal)
        {
            ["/"] = (new(true, false, 0, Mode0755), ["/opt"]),
            ["/opt"] = (new(true, false, 0, Mode0755), ["/opt/acl", "/opt/plain", "/opt/readonly"]),
            ["/opt/acl"] = (new(true, false, 0, groupWritable), []),
            ["/opt/plain"] = (new(true, false, 0, groupWritable), []),
            ["/opt/readonly"] = (new(true, false, 0, Mode0755), []),
        };

        var scan = SealedHostLockdown.Scan(
            "/",
            path => entries.TryGetValue(path, out var entry) ? entry.Status : null,
            path => entries.ContainsKey(path) ? new LinuxFileIdentity(1, InodeOf(path), 0) : null,
            path => entries[path].Children,
            mounts: [],
            isReusableOwner: static _ => false,
            hasAccessAcl: path => path is "/opt/acl" or "/opt/readonly");

        Assert.That(scan.SharedWritable, Is.EqualTo(new[] { "/opt/acl" }));
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void HasAccessAcl_IsFalseForAPlainFile()
    {
        LinuxTestPrerequisites.RequireLinux();

        var file = Path.Combine(Path.GetTempPath(), $"runner-acl-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(file, string.Empty);
            Assert.That(SealedHostLockdown.HasAccessAcl(file), Is.False);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Scan_FailsWithoutReadableAccountDatabases()
    {
        LinuxTestPrerequisites.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), $"runner-accounts-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var missingPasswd = Path.Combine(root, "passwd");
            var missingGroup = Path.Combine(root, "group");

            var scan = SealedHostLockdown.Scan(root, missingPasswd, missingGroup);

            Assert.That(scan.Unchecked, Is.EqualTo(new[] { missingPasswd, missingGroup }));
            Assert.That(scan.ReusablyOwned, Is.Empty, "no id is judged free without the databases");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Scan_CountsARootItCannotIdentifyAsUnchecked()
    {
        // statx blocked or unsupported: nothing could be checked, not "nothing found".
        var scan = SealedHostLockdown.Scan(
            "/",
            _ => new LinuxEntryStatus(true, false, 0, Mode0755),
            _ => null,
            _ => [],
            mounts: [],
            isReusableOwner: static _ => false);

        Assert.That(scan.Unchecked, Is.EqualTo(new[] { "/" }));
    }

    [Test]
    public void HandToRoot_GivesEntriesToRootAndReportsWhatStaysWithTheOldOwner()
    {
        var owners = new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["/tmp/old"] = 1001,
            ["/tmp/refuses"] = 1001,
            ["/tmp/root"] = 0,
        };

        var result = SealedHostLockdown.HandToRoot(
            owners.Keys,
            path => new LinuxEntryStatus(true, false, owners[path], Mode0755),
            path =>
            {
                if (path == "/tmp/refuses")
                {
                    throw new UnauthorizedAccessException(path);
                }

                owners[path] = 0;
            });

        Assert.That(result.Closed, Is.EqualTo(new[] { "/tmp/old" }));
        Assert.That(result.StillWritable, Is.EqualTo(new[] { "/tmp/refuses" }));
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void HandToRoot_ChangesTheRealOwner()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireRoot();

        var root = Path.Combine(Path.GetTempPath(), $"runner-handover-{Guid.NewGuid():N}");
        try
        {
            var file = Path.Combine(root, "left.txt");
            Directory.CreateDirectory(root);
            File.WriteAllText(file, string.Empty);
            var chown = Process.Start("/bin/chown", ["54321:54321", file])!;
            chown.WaitForExit();

            Assert.That(SealedStorageProbe.ReadEntryStatus(file)!.Value.OwnerId, Is.EqualTo(54321u));
            var result = SealedHostLockdown.HandToRoot([file]);

            Assert.That(result.Closed, Is.EqualTo(new[] { file }));
            Assert.That(SealedStorageProbe.ReadEntryStatus(file)!.Value.OwnerId, Is.Zero);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Summarize_NamesOnlyTopLevelDirectories()
    {
        var summary = SealedHostPreparation.Summarize(
        [
            "/tmp/portrait of alice.png",
            "/tmp/old/notes.txt",
            "/workspace/scratch",
            "/tmp",
            "/proc/self/mounts",
        ]);

        Assert.That(summary, Is.EqualTo("1 under /proc, 3 under /tmp, 1 under /workspace"));
    }

    private static ulong InodeOf(string path) => (ulong)(uint)StringComparer.Ordinal.GetHashCode(path);

    /// <summary>
    /// Inventory of the disk-backed places a fresh session user can write on the root filesystem
    /// of this image: every one of them must be something the lockdown closes in required mode.
    /// </summary>
    [Test]
    public async Task SessionUser_CanOnlyWriteDirectoriesTheLockdownCloses()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireRoot();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/find");

        await using var user = await LinuxTestUser.CreateAsync();
        var startInfo = new ProcessStartInfo("/usr/bin/find")
        {
            UserName = user.Name,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = "/",
        };
        foreach (var argument in new[] { "/", "-xdev", "-writable", "-prune", "-print" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var writable = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var closed = SealedHostLockdown.SharedTemporaryDirectories
            .Concat(SealedHostLockdown.FindSharedWritableDirectories())
            .ToHashSet(StringComparer.Ordinal);
        // Sockets and other non-directories the user may write cannot hold files. A link such as
        // /var/lock -> /run/lock is closed when its target is.
        var unexpected = writable
            .Where(path => Directory.Exists(path) && !closed.Contains(LinuxPathCanonicalizer.ResolveExistingPath(path)))
            .ToArray();

        Assert.That(unexpected, Is.Empty, "disk-backed directories a session user can write: " + string.Join(", ", unexpected));
    }
}
