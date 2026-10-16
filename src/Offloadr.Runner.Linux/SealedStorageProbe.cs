using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Offloadr.Runner.Linux;

/// <summary>Filesystem numbers from <c>statfs</c>.</summary>
public readonly record struct LinuxFileSystemStats(long Type, long BlockSize, long Blocks, long AvailableBlocks, bool ReadOnly = false);

/// <summary>Type, owner and permission bits of an entry itself (links are not followed).</summary>
public readonly record struct LinuxEntryStatus(bool IsDirectory, bool IsSymbolicLink, uint OwnerId, UnixFileMode Mode, bool IsRegularFile = false);

/// <summary>
/// Checks whether the sealed root can hold session data in memory: it must be on a tmpfs or
/// ramfs, belong to root, not be writable by anyone else, and leave at least the configured
/// minimum in both free space and container memory headroom. The probe never changes anything.
/// </summary>
public sealed class SealedStorageProbe
{
    internal const long TmpfsMagic = 0x01021994;
    internal const long RamfsMagic = 0x858458F6;

    private readonly Func<string, string?> _readFile;
    private readonly Func<string, LinuxFileSystemStats?> _statFileSystem;
    private readonly Func<string, LinuxEntryStatus?> _statEntry;
    private readonly Func<string, string> _resolvePath;

    public SealedStorageProbe()
        : this(ReadFileOrNull, ReadFileSystemStats, ReadEntryStatus, LinuxPathCanonicalizer.ResolveExistingPath)
    {
    }

    internal SealedStorageProbe(
        Func<string, string?> readFile,
        Func<string, LinuxFileSystemStats?> statFileSystem,
        Func<string, LinuxEntryStatus?> statEntry,
        Func<string, string> resolvePath)
    {
        _readFile = readFile ?? throw new ArgumentNullException(nameof(readFile));
        _statFileSystem = statFileSystem ?? throw new ArgumentNullException(nameof(statFileSystem));
        _statEntry = statEntry ?? throw new ArgumentNullException(nameof(statEntry));
        _resolvePath = resolvePath ?? throw new ArgumentNullException(nameof(resolvePath));
    }

    public SealedStorageStatus Probe(SealedStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var mode = SealedStorageOptions.FormatMode(options.Mode);
        var swapDevices = ReadSwapDeviceBytes();
        var swapLimit = ReadCgroupValue("memory.swap.max");
        if (options.Mode == SealedStorageMode.Off)
        {
            return Unavailable("sealed storage is off", new SealedStorageSwapStatus(swapDevices, swapLimit, TmpfsNoswap: false));
        }

        if (!IsNormalizedAbsolutePath(options.Root))
        {
            return Unavailable("RUNNER_SEALED_ROOT must be an absolute, normalized path", new SealedStorageSwapStatus(swapDevices, swapLimit, false));
        }

        // An existing root must be a real directory only root can change; a missing one is
        // created under its parent when the first sealed session starts.
        var target = options.Root;
        var root = _statEntry(options.Root);
        if (root is { } entry)
        {
            if (entry.IsSymbolicLink || !entry.IsDirectory)
            {
                return Unavailable("sealed root is not a directory", new SealedStorageSwapStatus(swapDevices, swapLimit, false));
            }

            if (entry.OwnerId != 0 || (entry.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                return Unavailable("sealed root must be owned by root and writable only by root", new SealedStorageSwapStatus(swapDevices, swapLimit, false));
            }
        }
        else
        {
            var separator = options.Root.LastIndexOf('/');
            var parent = separator == 0 ? "/" : options.Root[..separator];
            if (_statEntry(parent) is not { IsDirectory: true })
            {
                return Unavailable("sealed root's parent directory does not exist", new SealedStorageSwapStatus(swapDevices, swapLimit, false));
            }

            target = parent;
        }

        var noswap = MountHasOption(_resolvePath(target), "noswap");
        var swap = new SealedStorageSwapStatus(swapDevices, swapLimit, noswap);
        if (_statFileSystem(target) is not { } stats)
        {
            return Unavailable("could not read the sealed root's filesystem", swap);
        }

        if (stats.Type is not (TmpfsMagic or RamfsMagic))
        {
            return Unavailable("sealed root is not on a tmpfs or ramfs", swap);
        }

        if (stats.ReadOnly)
        {
            return Unavailable("sealed root is on a read-only mount", swap);
        }

        // ramfs reports no size; only the memory limit bounds it.
        long? capacity = stats.Type == RamfsMagic ? null : SafeMultiply(stats.Blocks, stats.BlockSize);
        long? free = stats.Type == RamfsMagic ? null : SafeMultiply(stats.AvailableBlocks, stats.BlockSize);
        var headroom = ReadMemoryHeadroomBytes();
        long? budget = (free, headroom) switch
        {
            (null, null) => null,
            ({ } f, null) => f,
            (null, { } h) => h,
            ({ } f, { } h) => Math.Min(f, h),
        };

        string? reason = budget switch
        {
            null => "could not determine how much memory the sealed root may use",
            { } b when b < options.MinimumBytes => $"sealed root budget {b} bytes is below the minimum of {options.MinimumBytes} bytes",
            _ => null,
        };

        return new SealedStorageStatus(mode, reason is null, reason, options.Root, capacity, free, headroom, budget, swap);

        SealedStorageStatus Unavailable(string why, SealedStorageSwapStatus swapStatus)
            => new(mode, false, why, options.Root, null, null, null, null, swapStatus);
    }

    /// <summary>
    /// True for a Linux path such as <c>/dev/shm/offloadr</c>: absolute, below <c>/</c>, without
    /// empty, <c>.</c> or <c>..</c> segments or a trailing slash.
    /// </summary>
    internal static bool IsNormalizedAbsolutePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.Length == 1 || path.EndsWith('/'))
        {
            return false;
        }

        return path[1..].Split('/').All(static segment => segment is not ("" or "." or ".."));
    }

    private long? ReadMemoryHeadroomBytes()
    {
        // cgroup v2 first, then v1; an unlimited cgroup is bounded by the machine's memory.
        var limit = ParseLimit(ReadCgroupValue("memory.max"))
            ?? ParseLimit(_readFile("/sys/fs/cgroup/memory/memory.limit_in_bytes")?.Trim());
        var usage = ParseLong(ReadCgroupValue("memory.current"))
            ?? ParseLong(_readFile("/sys/fs/cgroup/memory/memory.usage_in_bytes")?.Trim());
        var total = ReadMemTotalBytes();
        if (limit is null || (total is { } t && limit > t))
        {
            limit = total;
        }

        if (limit is not { } l)
        {
            return null;
        }

        return Math.Max(0, l - (usage ?? 0));
    }

    private long? ReadMemTotalBytes()
    {
        var meminfo = _readFile("/proc/meminfo");
        if (meminfo is null)
        {
            return null;
        }

        foreach (var line in meminfo.Split('\n'))
        {
            if (!line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line["MemTotal:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 && ParseLong(parts[0]) is { } kib ? SafeMultiply(kib, 1024) : null;
        }

        return null;
    }

    private long ReadSwapDeviceBytes()
    {
        var swaps = _readFile("/proc/swaps");
        if (swaps is null)
        {
            return 0;
        }

        long total = 0;
        foreach (var line in swaps.Split('\n').Skip(1))
        {
            // Filename Type Size(KiB) Used Priority
            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && ParseLong(parts[2]) is { } kib)
            {
                total += SafeMultiply(kib, 1024) ?? 0;
            }
        }

        return total;
    }

    private string? ReadCgroupValue(string name)
        => _readFile($"/sys/fs/cgroup/{name}")?.Trim();

    private bool MountHasOption(string path, string option)
    {
        var mounts = _readFile("/proc/self/mounts");
        if (mounts is null)
        {
            return false;
        }

        string? bestMountPoint = null;
        string[]? bestOptions = null;
        foreach (var line in mounts.Split('\n'))
        {
            var parts = line.Split(' ');
            if (parts.Length < 4)
            {
                continue;
            }

            var mountPoint = DecodeMountField(parts[1]);
            var contains = mountPoint == "/" ||
                string.Equals(path, mountPoint, StringComparison.Ordinal) ||
                path.StartsWith(mountPoint + "/", StringComparison.Ordinal);
            // The last matching entry wins on equal length: later mounts shadow earlier ones.
            if (contains && (bestMountPoint is null || mountPoint.Length >= bestMountPoint.Length))
            {
                bestMountPoint = mountPoint;
                bestOptions = parts[3].Split(',');
            }
        }

        return bestOptions?.Contains(option, StringComparer.Ordinal) == true;
    }

    /// <summary>Undoes the octal escapes (<c>\040</c> for a space) in <c>/proc/mounts</c> fields.</summary>
    internal static string DecodeMountField(string field)
    {
        if (!field.Contains('\\', StringComparison.Ordinal))
        {
            return field;
        }

        var builder = new StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && i + 3 < field.Length &&
                field[(i + 1)..(i + 4)].All(static c => c is >= '0' and <= '7'))
            {
                builder.Append((char)Convert.ToInt32(field.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                builder.Append(field[i]);
            }
        }

        return builder.ToString();
    }

    private static long? ParseLimit(string? value)
        => string.Equals(value, "max", StringComparison.Ordinal) ? null : ParseLong(value);

    private static long? ParseLong(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : null;

    private static long? SafeMultiply(long left, long right)
    {
        try
        {
            return checked(left * right);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static string? ReadFileOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private const int StatFsBufferSize = 128;
    private const int AtFdcwd = -100;
    private const int AtSymlinkNofollow = 0x100;
    private const uint StatxTypeModeUid = 0x0001 | 0x0002 | 0x0008;
    private const int StatxBufferSize = 256;
    private const int FileTypeMask = 0xF000;
    private const int DirectoryType = 0x4000;
    private const int SymbolicLinkType = 0xA000;
    private const int RegularFileType = 0x8000;

    internal static LinuxFileSystemStats? ReadFileSystemStats(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(StatFsBufferSize);
        try
        {
            if (statfs(path, buffer) != 0)
            {
                return null;
            }

            // 64-bit struct statfs: f_type, f_bsize, f_blocks, f_bfree, f_bavail, f_files,
            // f_ffree (8 bytes each), f_fsid (8), f_namelen, f_frsize.
            var type = Marshal.ReadInt64(buffer, 0) & 0xFFFFFFFF;
            var blockSize = Marshal.ReadInt64(buffer, 8);
            var fragmentSize = Marshal.ReadInt64(buffer, 72);
            // f_flags (offset 80) carries ST_RDONLY (1) for a read-only mount.
            return new LinuxFileSystemStats(
                type,
                fragmentSize > 0 ? fragmentSize : blockSize,
                Marshal.ReadInt64(buffer, 16),
                Marshal.ReadInt64(buffer, 32),
                (Marshal.ReadInt64(buffer, 80) & 1) != 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static LinuxEntryStatus? ReadEntryStatus(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(StatxBufferSize);
        try
        {
            if (statx(AtFdcwd, path, AtSymlinkNofollow, StatxTypeModeUid, buffer) != 0)
            {
                return null;
            }

            // struct statx: stx_uid at offset 20, stx_mode (u16) at offset 28.
            var ownerId = unchecked((uint)Marshal.ReadInt32(buffer, 20));
            var mode = unchecked((ushort)Marshal.ReadInt16(buffer, 28));
            return new LinuxEntryStatus(
                (mode & FileTypeMask) == DirectoryType,
                (mode & FileTypeMask) == SymbolicLinkType,
                ownerId,
                (UnixFileMode)(mode & 0xFFF),
                (mode & FileTypeMask) == RegularFileType);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "statfs")]
    private static extern int statfs(string path, nint buffer);

    [DllImport("libc", SetLastError = true, EntryPoint = "statx")]
    private static extern int statx(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        nint buffer);
}
