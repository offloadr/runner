using System.Globalization;
using System.Runtime.InteropServices;

namespace Offloadr.Runner.Linux;

/// <summary>
/// Removes what a session user leaves behind outside its home. The uid is freed by userdel and
/// useradd hands it out again, so anything still owned by it would belong to the next session.
/// Nothing here follows symbolic links: a link is removed as a link.
/// </summary>
public static class LinuxSessionResidue
{
    // /dev/mqueue holds POSIX message queues, which outlive the process that made them.
    public static readonly IReadOnlyList<string> SharedTemporaryRoots = ["/tmp", "/var/tmp", "/dev/shm", "/dev/mqueue"];

    /// <summary>
    /// <see cref="SharedTemporaryRoots"/> and every other writable memory-backed mount, such as a
    /// memory-backed volume or a shared <c>/run</c>: a session may leave files there too. Not
    /// <c>/dev</c> itself (device nodes and terminals, which cannot be removed), nor anything
    /// under <c>/proc</c> or <c>/sys</c>.
    /// </summary>
    public static IReadOnlyList<string> CurrentResidueRoots()
    {
        var roots = new List<string>(SharedTemporaryRoots);
        foreach (var mount in SealedHostLockdown.ReadMounts() ?? [])
        {
            if (!mount.ReadOnly && mount.Type is "tmpfs" or "ramfs" &&
                mount.MountPoint is not ("/" or "/dev") &&
                !mount.MountPoint.StartsWith("/proc/", StringComparison.Ordinal) &&
                !mount.MountPoint.StartsWith("/sys/", StringComparison.Ordinal) &&
                !roots.Contains(mount.MountPoint, StringComparer.Ordinal))
            {
                roots.Add(mount.MountPoint);
            }
        }

        return roots;
    }

    /// <summary>
    /// How many System V shared memory segments, message queues and semaphore sets the user owns,
    /// as <c>/proc/sysvipc</c> lists them; null when a table cannot be read. They outlive their
    /// creator, and the next user of the uid would own them.
    /// </summary>
    public static int? CountIpcObjectsOwnedBy(uint userId, string sysvipcDirectory = "/proc/sysvipc")
    {
        // A kernel without System V IPC has no such directory; with one, every table is there,
        // and one that is missing (masked, say) hides what it would list.
        if (!Directory.Exists(sysvipcDirectory))
        {
            return 0;
        }

        var owned = 0;
        foreach (var table in new[] { "shm", "msg", "sem" })
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(Path.Combine(sysvipcDirectory, table));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            var header = lines.Length > 0 ? lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
            var uidColumn = Array.IndexOf(header, "uid");
            if (uidColumn < 0)
            {
                return null;
            }

            owned += lines.Skip(1).Count(line =>
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return fields.Length > uidColumn &&
                    uint.TryParse(fields[uidColumn], NumberStyles.None, CultureInfo.InvariantCulture, out var owner) &&
                    owner == userId;
            });
        }

        return owned;
    }

    private const int AtFdcwd = -100;
    private const int AtSymlinkNofollow = 0x100;
    private const uint StatxType = 0x0001;
    private const uint StatxMode = 0x0002;
    private const uint StatxUid = 0x0008;
    private const int StatxBufferSize = 256;
    private const uint FileTypeMask = 0xF000;
    private const uint DirectoryType = 0x4000;

    /// <summary>
    /// Deletes every entry under <paramref name="roots"/> owned by <paramref name="userId"/>,
    /// descending into directories owned by others. Tolerates entries that disappear meanwhile.
    /// </summary>
    /// <returns>How many entries were removed.</returns>
    public static int RemoveEntriesOwnedBy(uint userId, IEnumerable<string> roots)
        => RemoveEntriesOwnedBy(userId, roots, out _);

    /// <inheritdoc cref="RemoveEntriesOwnedBy(uint, IEnumerable{string})"/>
    /// <param name="remaining">Entries of the user that could not be removed.</param>
    public static int RemoveEntriesOwnedBy(uint userId, IEnumerable<string> roots, out int remaining)
    {
        remaining = 0;
        if (userId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId), "Refusing to remove files owned by root.");
        }

        if (!OperatingSystem.IsLinux())
        {
            return 0;
        }

        var removed = 0;
        foreach (var root in roots)
        {
            if (!TryReadOwner(root, out _, out var isDirectory))
            {
                // A root that does not exist holds nothing; one that cannot be read may.
                if (!SealedHostLockdown.IsGone(root))
                {
                    remaining++;
                }

                continue;
            }

            // Never act through a link at the root itself.
            if (isDirectory)
            {
                removed += RemoveOwnedEntries(root, userId, ref remaining);
            }
        }

        return removed;
    }

    /// <summary>
    /// Removes top-level entries of <paramref name="directory"/> owned by a non-root uid that
    /// <paramref name="isLiveUserId"/> does not recognise, such as homes of users already deleted.
    /// </summary>
    public static int RemoveEntriesOfUnknownOwners(string directory, Func<uint, bool> isLiveUserId)
    {
        ArgumentNullException.ThrowIfNull(isLiveUserId);
        if (!OperatingSystem.IsLinux() ||
            !TryReadOwner(directory, out _, out var isDirectory) ||
            !isDirectory)
        {
            return 0;
        }

        var removed = 0;
        _ = TryEnumerateEntries(directory, out var entries);
        foreach (var entry in entries)
        {
            if (TryReadOwner(entry, out var ownerId, out var entryIsDirectory) &&
                ownerId != 0 &&
                !isLiveUserId(ownerId) &&
                TryRemove(entry, entryIsDirectory))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Reads (name, uid) pairs from a passwd-format file.</summary>
    public static IReadOnlyList<(string UserName, uint UserId, string HomeDirectory)> ReadUsers(string passwdPath)
    {
        var users = new List<(string, uint, string)>();
        string[] lines;
        try
        {
            lines = File.ReadAllLines(passwdPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return users;
        }

        foreach (var line in lines)
        {
            var fields = line.Split(':');
            if (fields.Length >= 3 &&
                fields[0].Length > 0 &&
                uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var userId))
            {
                users.Add((fields[0], userId, fields.Length > 5 ? fields[5] : string.Empty));
            }
        }

        return users;
    }

    /// <summary>
    /// Walks <paramref name="root"/> at any depth, without recursion, and visits each directory
    /// once (a bind mount can show one again), so no subtree is skipped by a depth cutoff. Only
    /// the user's own entries go: a directory of the user's that holds someone else's files
    /// (root's, or another service's) is emptied of the user's entries and given to root, and
    /// those files stay.
    /// </summary>
    private static int RemoveOwnedEntries(string root, uint userId, ref int remaining)
    {
        var removed = 0;
        var visited = new HashSet<(ulong Device, ulong Inode)>();
        var pending = new Stack<string>();
        // The user's own directories, in the order found: children come after their parents.
        var ownedDirectories = new List<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            if (LinuxFileIdentityReader.TryRead(directory, out var identity) && !visited.Add((identity.DeviceId, identity.Inode)))
            {
                continue;
            }

            if (!TryEnumerateEntries(directory, out var entries))
            {
                // What lies below is unknown; some of it may be the user's.
                remaining++;
                continue;
            }

            foreach (var entry in entries)
            {
                if (!TryReadOwner(entry, out var ownerId, out var isDirectory))
                {
                    // Gone since the listing is fine; there but unreadable may still be the user's.
                    if (!SealedHostLockdown.IsGone(entry))
                    {
                        remaining++;
                    }

                    continue;
                }

                if (isDirectory)
                {
                    if (ownerId == userId)
                    {
                        ownedDirectories.Add(entry);
                    }

                    pending.Push(entry);
                }
                else if (ownerId == userId)
                {
                    if (TryRemove(entry, isDirectory: false))
                    {
                        removed++;
                    }
                    else
                    {
                        remaining++;
                    }
                }
            }
        }

        // Deepest first: a directory is empty of the user's entries by the time it is reached.
        for (var index = ownedDirectories.Count - 1; index >= 0; index--)
        {
            var directory = ownedDirectories[index];
            try
            {
                Directory.Delete(directory, recursive: false);
                removed++;
            }
            catch (IOException) when (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            {
                // Someone else's files are in it: it becomes root's alone, and they stay. Closed to
                // everyone else (with an ACL, its mask too): no later session may enter it.
                if (lchown(directory, 0, 0) != 0)
                {
                    remaining++;
                }
                else
                {
                    try
                    {
                        LinuxSecureDirectoryRoot.SetModeNoFollow(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        remaining++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                remaining++;
            }
        }

        return removed;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int lchown(string path, uint owner, uint group);

    private static bool TryEnumerateEntries(string directory, out string[] entries)
    {
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            entries = [];
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            entries = [];
            return false;
        }
    }

    private static bool TryRemove(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                // Recursive deletion unlinks symbolic links it meets instead of following them.
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Owner and type of the entry itself (a link is reported as a link, not its target).</summary>
    internal static bool TryReadOwner(string path, out uint ownerId, out bool isDirectory)
    {
        ownerId = 0;
        isDirectory = false;
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(StatxBufferSize);
        try
        {
            if (statx(AtFdcwd, path, AtSymlinkNofollow, StatxType | StatxMode | StatxUid, buffer) != 0)
            {
                return false;
            }

            // struct statx: stx_uid at offset 20, stx_mode (u16) at offset 28.
            ownerId = unchecked((uint)Marshal.ReadInt32(buffer, 20));
            var mode = unchecked((ushort)Marshal.ReadInt16(buffer, 28));
            isDirectory = (mode & FileTypeMask) == DirectoryType;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "statx")]
    private static extern int statx(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        nint buffer);
}
