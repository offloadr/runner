using System.Runtime.InteropServices;

namespace Offloadr.Runner.Linux;

public readonly record struct LinuxFileIdentity(ulong DeviceId, ulong Inode, long Length);

public static class LinuxFileIdentityReader
{
    private const int AtFdcwd = -100;
    private const int AtSymlinkNofollow = 0x100;
    private const uint StatxBasicStats = 0x07ff;
    private const uint StatxMountId = 0x1000;
    private const int StatxBufferSize = 256;

    public static bool TryRead(string path, out LinuxFileIdentity identity)
    {
        identity = default;
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(StatxBufferSize);
        try
        {
            if (statx(AtFdcwd, path, AtSymlinkNofollow, StatxBasicStats, buffer) != 0)
            {
                return false;
            }

            var inode = unchecked((ulong)Marshal.ReadInt64(buffer, 32));
            var length = Marshal.ReadInt64(buffer, 40);
            var deviceMajor = unchecked((uint)Marshal.ReadInt32(buffer, 136));
            var deviceMinor = unchecked((uint)Marshal.ReadInt32(buffer, 140));
            identity = new LinuxFileIdentity(
                ((ulong)deviceMajor << 32) | deviceMinor,
                inode,
                length);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Reads which mount <paramref name="path"/> itself (not a link's target) is on: its device and, where the
    /// kernel reports one, its mount id. Two directories of one filesystem bind-mounted apart share a device
    /// but not a mount, and a file cannot be renamed from one into the other.
    /// </summary>
    public static bool TryReadMount(string path, out (ulong DeviceId, ulong? MountId) mount)
    {
        mount = default;
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(StatxBufferSize);
        try
        {
            if (statx(AtFdcwd, path, AtSymlinkNofollow, StatxBasicStats | StatxMountId, buffer) != 0)
            {
                return false;
            }

            var returned = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            var deviceMajor = unchecked((uint)Marshal.ReadInt32(buffer, 136));
            var deviceMinor = unchecked((uint)Marshal.ReadInt32(buffer, 140));
            mount = (
                ((ulong)deviceMajor << 32) | deviceMinor,
                (returned & StatxMountId) != 0 ? unchecked((ulong)Marshal.ReadInt64(buffer, 144)) : null);
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

public static class LinuxPathCanonicalizer
{
    public static string ResolveExistingPath(string path)
    {
        var normalized = Path.GetFullPath(path);
        if (!OperatingSystem.IsLinux())
        {
            return normalized;
        }

        var resolvedPointer = realpath(normalized, nint.Zero);
        if (resolvedPointer == nint.Zero)
        {
            return normalized;
        }

        try
        {
            return Marshal.PtrToStringUTF8(resolvedPointer) ?? normalized;
        }
        finally
        {
            free(resolvedPointer);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern nint realpath(string path, nint resolvedPath);

    [DllImport("libc")]
    private static extern void free(nint pointer);
}
