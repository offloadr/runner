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

            setMode(directory, entry.Mode & ~(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
            changed.Add(directory);
        }

        return changed;
    }
}
