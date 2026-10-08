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
    public static readonly IReadOnlyList<string> SharedTemporaryRoots = ["/tmp", "/var/tmp", "/dev/shm"];

    private const int MaxDepth = 16;
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
    {
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
            // Never act through a link at the root itself.
            if (TryReadOwner(root, out _, out var isDirectory) && isDirectory)
            {
                removed += RemoveOwnedEntries(root, userId, depth: 0);
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
        foreach (var entry in EnumerateEntries(directory))
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

    private static int RemoveOwnedEntries(string directory, uint userId, int depth)
    {
        var removed = 0;
        foreach (var entry in EnumerateEntries(directory))
        {
            if (!TryReadOwner(entry, out var ownerId, out var isDirectory))
            {
                continue;
            }

            if (ownerId == userId)
            {
                if (TryRemove(entry, isDirectory))
                {
                    removed++;
                }
            }
            else if (isDirectory && depth < MaxDepth)
            {
                removed += RemoveOwnedEntries(entry, userId, depth + 1);
            }
        }

        return removed;
    }

    private static string[] EnumerateEntries(string directory)
    {
        try
        {
            return Directory.GetFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
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
