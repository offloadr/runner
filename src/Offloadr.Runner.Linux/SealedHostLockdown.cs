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
    /// Directories on <paramref name="root"/>'s filesystem that anyone may write, at any depth,
    /// such as <c>/tmp</c>, <c>/var/tmp</c>, <c>/run/lock</c> or Ubuntu's <c>/var/crash</c>, and
    /// world-writable mount points there, such as a volume mounted into the container. Inside a
    /// world-writable directory every world-writable directory and regular file is reported too,
    /// since closing only the top would leave them usable. That includes filesystems mounted
    /// anywhere below one, which a session can still traverse into; read-only and memory-backed
    /// ones are not entered. Other filesystems are not entered, symlinks are not followed, each
    /// directory is visited once (bind mounts can repeat one) and sockets are left alone.
    /// </summary>
    public static IReadOnlyList<string> FindSharedWritableDirectories(string root = "/")
        => FindSharedWritableDirectories(
            root,
            SealedStorageProbe.ReadEntryStatus,
            static path => LinuxFileIdentityReader.TryRead(path, out var identity) ? identity : null,
            static (path, everything) => everything ? Directory.GetFileSystemEntries(path) : Directory.GetDirectories(path),
            ReadMounts());

    internal static IReadOnlyList<string> FindSharedWritableDirectories(
        string root,
        Func<string, LinuxEntryStatus?> statEntry,
        Func<string, LinuxFileIdentity?> identityOf,
        Func<string, bool, string[]> listChildren,
        IReadOnlyList<Mount> mounts)
    {
        var found = new List<string>();
        var shared = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<(ulong Device, ulong Inode)>();
        if (statEntry(root) is not { IsDirectory: true } || identityOf(root) is not { } rootIdentity)
        {
            return found;
        }

        visited.Add((rootIdentity.DeviceId, rootIdentity.Inode));
        Walk(root, rootIdentity.DeviceId, insideShared: false);

        // A mount below a shared directory is reachable although the walk above stayed on its own
        // filesystem. Parents sort before their children, so a mount nested in a walked mount is
        // judged after what that walk found.
        var prefix = root.EndsWith('/') ? root : root + "/";
        foreach (var mount in mounts
                     .Where(mount => mount.MountPoint.StartsWith(prefix, StringComparison.Ordinal))
                     .OrderBy(static mount => mount.MountPoint, StringComparer.Ordinal))
        {
            if (mount.ReadOnly || mount.Type is "tmpfs" or "ramfs" || !IsSharedOrBelowShared(mount.MountPoint) ||
                statEntry(mount.MountPoint) is not { IsDirectory: true, IsSymbolicLink: false } ||
                identityOf(mount.MountPoint) is not { } identity ||
                !visited.Add((identity.DeviceId, identity.Inode)))
            {
                continue;
            }

            Walk(mount.MountPoint, identity.DeviceId, insideShared: true);
        }

        return found;

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
                children = listChildren(directory, insideShared);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (var child in children)
            {
                if (statEntry(child) is not { IsSymbolicLink: false } entry || identityOf(child) is not { } identity)
                {
                    continue;
                }

                var worldWritable = (entry.Mode & UnixFileMode.OtherWrite) != 0;
                if (!entry.IsDirectory)
                {
                    if (insideShared && entry.IsRegularFile && worldWritable && shared.Add(child))
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

    /// <summary>One line of <c>/proc/self/mounts</c>.</summary>
    internal sealed record Mount(string MountPoint, string Type, bool ReadOnly);

    internal static IReadOnlyList<Mount> ReadMounts(string path = "/proc/self/mounts")
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
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
