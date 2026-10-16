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
    /// Directories near the top of <paramref name="root"/>'s filesystem that anyone may write,
    /// such as <c>/tmp</c>, <c>/var/tmp</c>, <c>/run/lock</c> or Ubuntu's <c>/var/crash</c>, and
    /// world-writable mount points there, such as a volume mounted into the container. Other
    /// filesystems are reported but never entered, and symlinks are not followed.
    /// </summary>
    public static IReadOnlyList<string> FindSharedWritableDirectories(string root = "/", int maxDepth = 3)
    {
        var found = new List<string>();
        if (SealedStorageProbe.ReadEntryStatus(root) is not { IsDirectory: true } ||
            !LinuxFileIdentityReader.TryRead(root, out var rootIdentity))
        {
            return found;
        }

        Walk(root, 0);
        return found;

        void Walk(string directory, int depth)
        {
            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (var child in children)
            {
                if (SealedStorageProbe.ReadEntryStatus(child) is not { IsDirectory: true, IsSymbolicLink: false } entry ||
                    !LinuxFileIdentityReader.TryRead(child, out var identity))
                {
                    continue;
                }

                if ((entry.Mode & UnixFileMode.OtherWrite) != 0)
                {
                    found.Add(child);
                    continue;
                }

                if (identity.DeviceId == rootIdentity.DeviceId && depth + 1 < maxDepth)
                {
                    Walk(child, depth + 1);
                }
            }
        }
    }

    /// <summary>Removes group and other write access from each disk-backed directory; returns the ones changed.</summary>
    public static IReadOnlyList<string> RemoveSharedWriteAccess(IEnumerable<string> directories)
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

    internal static IReadOnlyList<string> RemoveSharedWriteAccess(
        IEnumerable<string> directories,
        Func<string, LinuxEntryStatus?> statEntry,
        Func<string, LinuxFileSystemStats?> statFileSystem,
        Action<string, UnixFileMode> setMode)
    {
        ArgumentNullException.ThrowIfNull(directories);
        var changed = new List<string>();
        foreach (var directory in directories)
        {
            if (statEntry(directory) is not { IsDirectory: true, IsSymbolicLink: false } entry ||
                (entry.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0)
            {
                continue;
            }

            // Memory-backed scratch space is not where residue is a concern.
            if (statFileSystem(directory) is { Type: SealedStorageProbe.TmpfsMagic or SealedStorageProbe.RamfsMagic })
            {
                continue;
            }

            try
            {
                setMode(directory, entry.Mode & ~(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
                changed.Add(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A read-only mount cannot be changed, and nobody can write to it either.
                RunnerLog.Warning($"Sealed storage: could not close '{directory}' to session users: {ex.Message}");
            }
        }

        return changed;
    }
}
