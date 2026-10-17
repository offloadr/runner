using System.Runtime.InteropServices;

namespace Offloadr.Runner.Linux;

/// <summary>
/// In required sealed mode, session processes must not find disk-backed scratch space they can
/// write: a library that ignores <c>TMPDIR</c> and writes to <c>/tmp</c> then fails instead of
/// leaving user data on disk. Root (the agent) keeps writing there; others can still traverse,
/// which the agent's socket under <c>/tmp</c> needs.
/// </summary>
public static class SealedHostLockdown
{
    public static readonly IReadOnlyList<string> SharedTemporaryDirectories = ["/tmp", "/var/tmp"];

    /// <summary>
    /// Directories anyone may write, at any depth, on <paramref name="root"/>'s filesystem and on
    /// every writable disk mounted below it (a volume a session can traverse into), such as
    /// <c>/tmp</c>, <c>/var/tmp</c>, <c>/run/lock</c>, Ubuntu's <c>/var/crash</c> or a scratch
    /// folder on a volume, and every world-writable regular file there. Inside a world-writable
    /// directory everything below is walked too, since closing only the top would leave it usable.
    /// Read-only, memory-backed and kernel filesystems are not entered, symlinks are not
    /// followed, each directory is visited once (bind mounts can repeat one) and sockets are left
    /// alone. A
    /// directory that cannot be listed (a FUSE or network mount refusing root) goes to
    /// <paramref name="unlisted"/>: what a session can write below it is unknown.
    /// </summary>
    public static IReadOnlyList<string> FindSharedWritableDirectories(string root = "/")
        => Scan(root).SharedWritable;

    /// <summary>
    /// What a lockdown must act on: world-writable places to close, entries inside shared
    /// directories owned by a uid a session user may get again (to hand to root) and what could
    /// not be checked.
    /// </summary>
    public sealed record ScanResult(IReadOnlyList<string> SharedWritable, IReadOnlyList<string> ReusablyOwned, IReadOnlyList<string> Unchecked);

    /// <summary>
    /// Scans as <see cref="FindSharedWritableDirectories(string)"/> describes. Inside a shared
    /// directory, a directory or regular file owned by a uid without an account, or by a session
    /// account, is reported too: useradd hands such a uid to the next session user, who could
    /// then write there through its owner bits. An unreadable mount table is reported as
    /// unchecked, since mounted filesystems would go unseen.
    /// </summary>
    public static ScanResult Scan(string root = "/", string passwdPath = "/etc/passwd")
    {
        var users = LinuxSessionResidue.ReadUsers(passwdPath);
        var liveUserIds = users.Select(static user => user.UserId).ToHashSet();
        var sessionUserIds = users
            .Where(static user => user.UserName.StartsWith("sess_", StringComparison.Ordinal))
            .Select(static user => user.UserId)
            .ToHashSet();
        return Scan(
            root,
            SealedStorageProbe.ReadEntryStatus,
            static path => LinuxFileIdentityReader.TryRead(path, out var identity) ? identity : null,
            static path => Directory.GetFileSystemEntries(path),
            ReadMounts(),
            userId => userId != 0 && (!liveUserIds.Contains(userId) || sessionUserIds.Contains(userId)));
    }

    internal static IReadOnlyList<string> FindSharedWritableDirectories(
        string root,
        Func<string, LinuxEntryStatus?> statEntry,
        Func<string, LinuxFileIdentity?> identityOf,
        Func<string, string[]> listChildren,
        IReadOnlyList<Mount> mounts,
        ICollection<string>? unlisted = null)
    {
        var scan = Scan(root, statEntry, identityOf, listChildren, mounts, static _ => false);
        foreach (var path in scan.Unchecked)
        {
            unlisted?.Add(path);
        }

        return scan.SharedWritable;
    }

    internal static ScanResult Scan(
        string root,
        Func<string, LinuxEntryStatus?> statEntry,
        Func<string, LinuxFileIdentity?> identityOf,
        Func<string, string[]> listChildren,
        IReadOnlyList<Mount>? mounts,
        Func<uint, bool> isReusableOwner)
    {
        var found = new List<string>();
        var reusablyOwned = new List<string>();
        var unlisted = new List<string>();
        var result = new ScanResult(found, reusablyOwned, unlisted);
        if (mounts is null)
        {
            unlisted.Add(MountTablePath);
            mounts = [];
        }

        var shared = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<(ulong Device, ulong Inode)>();
        if (statEntry(root) is not { IsDirectory: true } || identityOf(root) is not { } rootIdentity)
        {
            return result;
        }

        visited.Add((rootIdentity.DeviceId, rootIdentity.Inode));
        Walk(root, rootIdentity.DeviceId, insideShared: false);

        // Each other filesystem a session can traverse into is walked on its own: inside, as a
        // shared directory, when it lies below one. Parents sort before their children, so a mount
        // nested in a walked mount is judged after what that walk found.
        var prefix = root.EndsWith('/') ? root : root + "/";
        foreach (var mount in mounts
                     .Where(mount => mount.MountPoint.StartsWith(prefix, StringComparison.Ordinal))
                     .OrderBy(static mount => mount.MountPoint, StringComparer.Ordinal))
        {
            if (mount.ReadOnly || NotDiskBackedTypes.Contains(mount.Type) ||
                statEntry(mount.MountPoint) is not { IsDirectory: true, IsSymbolicLink: false } ||
                identityOf(mount.MountPoint) is not { } identity ||
                !visited.Add((identity.DeviceId, identity.Inode)))
            {
                continue;
            }

            Walk(mount.MountPoint, identity.DeviceId, insideShared: IsSharedOrBelowShared(mount.MountPoint));
        }

        return result;

        bool IsSharedOrBelowShared(string path)
        {
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                if (shared.Contains(current))
                {
                    return true;
                }
            }

            return false;
        }

        void Walk(string directory, ulong walkDevice, bool insideShared)
        {
            string[] children;
            try
            {
                children = listChildren(directory);
            }
            catch (DirectoryNotFoundException)
            {
                // Removed since it was seen: nothing below it to write.
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unlisted.Add(directory);
                return;
            }

            foreach (var child in children)
            {
                if (statEntry(child) is not { IsSymbolicLink: false } entry || identityOf(child) is not { } identity)
                {
                    continue;
                }

                if (insideShared && entry.OwnerId != 0 && (entry.IsDirectory || entry.IsRegularFile) && isReusableOwner(entry.OwnerId))
                {
                    reusablyOwned.Add(child);
                }

                var worldWritable = (entry.Mode & UnixFileMode.OtherWrite) != 0;
                if (!entry.IsDirectory)
                {
                    // Anywhere: a session that knows the name can write it through any folder it can traverse.
                    if (entry.IsRegularFile && worldWritable && shared.Add(child))
                    {
                        found.Add(child);
                    }

                    continue;
                }

                if (worldWritable && shared.Add(child))
                {
                    found.Add(child);
                }

                // Another filesystem is walked from the mount table, if a session can reach it.
                if (identity.DeviceId != walkDevice || !visited.Add((identity.DeviceId, identity.Inode)))
                {
                    continue;
                }

                Walk(child, walkDevice, insideShared: worldWritable || insideShared);
            }
        }
    }

    /// <summary>Memory-backed and kernel filesystems: nothing written there reaches a disk.</summary>
    private static readonly HashSet<string> NotDiskBackedTypes = new(StringComparer.Ordinal)
    {
        "tmpfs", "ramfs", "proc", "sysfs", "devtmpfs", "devpts", "mqueue", "cgroup", "cgroup2",
        "debugfs", "tracefs", "securityfs", "pstore", "bpf", "hugetlbfs", "configfs", "fusectl",
        "binfmt_misc", "autofs", "nsfs", "efivarfs", "rpc_pipefs", "nfsd",
    };

    /// <summary>One line of <c>/proc/self/mounts</c>.</summary>
    internal sealed record Mount(string MountPoint, string Type, bool ReadOnly);

    private const string MountTablePath = "/proc/self/mounts";

    /// <summary>The mount table, or null when it cannot be read (it always lists at least the root).</summary>
    internal static IReadOnlyList<Mount>? ReadMounts(string path = MountTablePath)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var mounts = new List<Mount>();
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split(' ');
            if (parts.Length < 4)
            {
                continue;
            }

            mounts.Add(new Mount(
                SealedStorageProbe.DecodeMountField(parts[1]),
                parts[2],
                parts[3].Split(',').Contains("ro", StringComparer.Ordinal)));
        }

        return mounts;
    }

    /// <summary>
    /// Gives each directory or regular file to root (owner and group), so a session user that
    /// gets its old uid again cannot write it, then checks the owner. Links are not followed.
    /// </summary>
    public static Result HandToRoot(IEnumerable<string> paths)
        => HandToRoot(paths, SealedStorageProbe.ReadEntryStatus, static path =>
        {
            if (lchown(path, 0, 0) != 0)
            {
                throw new IOException($"lchown failed with errno {Marshal.GetLastPInvokeError()}.");
            }
        });

    internal static Result HandToRoot(IEnumerable<string> paths, Func<string, LinuxEntryStatus?> statEntry, Action<string> chownToRoot)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var changed = new List<string>();
        var stillOwned = new List<string>();
        foreach (var path in paths)
        {
            if (statEntry(path) is not { IsSymbolicLink: false } entry || entry.OwnerId == 0)
            {
                continue;
            }

            try
            {
                chownToRoot(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RunnerLog.Warning($"Sealed storage: could not give '{path}' to root: {ex.Message}");
            }

            if (statEntry(path) is { OwnerId: 0 })
            {
                changed.Add(path);
            }
            else
            {
                stillOwned.Add(path);
            }
        }

        return new Result(changed, stillOwned);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int lchown(string path, uint owner, uint group);

    /// <summary>What the lockdown closed, and what it could not close although others can still write it.</summary>
    public sealed record Result(IReadOnlyList<string> Closed, IReadOnlyList<string> StillWritable);

    /// <summary>
    /// Removes group and other write access from each disk-backed directory or regular file, then
    /// checks that it is gone. A read-only mount needs no change; anything else that keeps its write
    /// bits (a FUSE, network or bind mount refusing chmod) is reported as still writable.
    /// </summary>
    public static Result RemoveSharedWriteAccess(IEnumerable<string> directories)
        => RemoveSharedWriteAccess(
            directories,
            SealedStorageProbe.ReadEntryStatus,
            SealedStorageProbe.ReadFileSystemStats,
            static (path, mode) =>
            {
                if (OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(path, mode);
                }
            });

    internal static Result RemoveSharedWriteAccess(
        IEnumerable<string> directories,
        Func<string, LinuxEntryStatus?> statEntry,
        Func<string, LinuxFileSystemStats?> statFileSystem,
        Action<string, UnixFileMode> setMode)
    {
        ArgumentNullException.ThrowIfNull(directories);
        var changed = new List<string>();
        var stillWritable = new List<string>();
        foreach (var directory in directories)
        {
            if (statEntry(directory) is not { IsSymbolicLink: false } entry ||
                !(entry.IsDirectory || entry.IsRegularFile) ||
                (entry.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0)
            {
                continue;
            }

            // Memory-backed scratch space is not where residue is a concern, and nobody writes to a
            // read-only mount.
            var fileSystem = statFileSystem(directory);
            if (fileSystem is { Type: SealedStorageProbe.TmpfsMagic or SealedStorageProbe.RamfsMagic } or { ReadOnly: true })
            {
                continue;
            }

            try
            {
                setMode(directory, entry.Mode & ~(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RunnerLog.Warning($"Sealed storage: could not close '{directory}' to session users: {ex.Message}");
            }

            // Some filesystems accept chmod and ignore it; only the mode read back counts.
            if (statEntry(directory) is { } after && (after.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0)
            {
                changed.Add(directory);
            }
            else
            {
                stillWritable.Add(directory);
            }
        }

        return new Result(changed, stillWritable);
    }
}
