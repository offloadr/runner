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
            (path, _) => entries[path].Children,
            mounts);

        Assert.That(found, Is.EquivalentTo(new[]
        {
            "/workspace", "/workspace/jobs", "/workspace/mnt/deep", "/workspace/mnt/deep/file", "/tmp", "/media",
        }));
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
            (path, _) => entries[path].Children,
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
            (path, _) => entries[path].Children ?? throw (path == "/tmp/gone"
                ? new DirectoryNotFoundException(path)
                : new UnauthorizedAccessException(path)),
            [],
            unlisted);

        Assert.That(found, Is.EquivalentTo(new[] { "/tmp", "/tmp/open" }));
        Assert.That(unlisted, Is.EqualTo(new[] { "/tmp/fuse" }));
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
