using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Offloadr.Runner.Linux;

/// <summary>
/// Performs privileged file publication relative to an already-open Linux
/// directory descriptor so path renames and symlink swaps cannot redirect it.
/// </summary>
internal sealed class LinuxSecureDirectoryRoot : IDisposable
{
    private const int OReadWrite = 0x2;
    private const int OCreate = 0x40;
    private const int OExclusive = 0x80;
    private const int ONonBlock = 0x800;
    private const int ODirectory = 0x10000;
    private const int ONoFollow = 0x20000;
    private const int OCloseOnExec = 0x80000;
    private const int OPath = 0x200000;
    private const int FDuplicateCloseOnExec = 1030;
    private const int ENoEntry = 2;
    private const int EExists = 17;
    private const int AtEmptyPath = 0x1000;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x0001;
    private const int StatxBufferSize = 256;
    private const ushort FileTypeMask = 0xF000;
    private const ushort RegularFileType = 0x8000;
    private const long UTimeOmit = (1L << 30) - 2;

    private readonly SafeFileHandle _rootHandle;

    public LinuxSecureDirectoryRoot(string rootPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Secure descriptor-relative publication requires Linux.");
        }

        var descriptor = open(
            Path.GetFullPath(rootPath),
            OPath | ODirectory | ONoFollow | OCloseOnExec);
        if (descriptor < 0)
        {
            throw CreateException($"opening trusted directory root '{rootPath}'");
        }

        _rootHandle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    public void EnsurePlaceholder(string relativePath, DateTime? modifiedUtc)
    {
        var (parent, leafName) = OpenParent(relativePath, createDirectories: true);
        using (parent)
        {
            var descriptor = openat(
                GetDescriptor(parent),
                leafName,
                OReadWrite | OCreate | OExclusive | ONonBlock | ONoFollow | OCloseOnExec,
                Convert.ToUInt32("644", 8));
            if (descriptor >= 0)
            {
                using var created = new SafeFileHandle((nint)descriptor, ownsHandle: true);
                if (modifiedUtc.HasValue)
                {
                    SetModifiedUtc(created, modifiedUtc.Value);
                }

                return;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error != EExists)
            {
                throw CreateException($"creating workspace placeholder '{relativePath}'", error);
            }

            descriptor = openat(
                GetDescriptor(parent),
                leafName,
                OPath | ONoFollow | OCloseOnExec);
            if (descriptor < 0)
            {
                throw CreateException($"opening existing workspace placeholder '{relativePath}'");
            }

            using var existing = new SafeFileHandle((nint)descriptor, ownsHandle: true);
            EnsureRegularFile(existing, relativePath);
            if (modifiedUtc.HasValue)
            {
                SetModifiedUtc(existing, modifiedUtc.Value, useEmptyPath: true);
            }
        }
    }

    public PendingPublication CreatePublication(string relativePath)
    {
        var (parent, leafName) = OpenParent(relativePath, createDirectories: true);
        try
        {
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var temporaryName = $".{Path.GetRandomFileName()}.part";
                var descriptor = openat(
                    GetDescriptor(parent),
                    temporaryName,
                    OReadWrite | OCreate | OExclusive | ONoFollow | OCloseOnExec,
                    Convert.ToUInt32("600", 8));
                if (descriptor >= 0)
                {
                    return new PendingPublication(
                        parent,
                        new SafeFileHandle((nint)descriptor, ownsHandle: true),
                        temporaryName,
                        leafName,
                        relativePath);
                }

                var error = Marshal.GetLastPInvokeError();
                if (error != EExists)
                {
                    throw CreateException($"creating temporary workspace file for '{relativePath}'", error);
                }
            }

            throw new IOException($"Could not allocate a unique temporary workspace file for '{relativePath}'.");
        }
        catch
        {
            parent.Dispose();
            throw;
        }
    }

    public void Dispose() => _rootHandle.Dispose();

    private (SafeFileHandle Parent, string LeafName) OpenParent(
        string relativePath,
        bool createDirectories)
    {
        var parts = SplitRelativePath(relativePath);
        var current = DuplicateDirectory(_rootHandle);
        try
        {
            for (var index = 0; index < parts.Length - 1; index++)
            {
                var next = OpenDirectory(current, parts[index], createDirectories);
                current.Dispose();
                current = next;
            }

            return (current, parts[^1]);
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle DuplicateDirectory(SafeFileHandle directory)
    {
        var descriptor = openat(
            GetDescriptor(directory),
            ".",
            OPath | ODirectory | ONoFollow | OCloseOnExec);
        if (descriptor < 0)
        {
            throw CreateException("duplicating trusted directory descriptor");
        }

        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static SafeFileHandle OpenDirectory(
        SafeFileHandle parent,
        string name,
        bool createDirectory)
    {
        var descriptor = openat(
            GetDescriptor(parent),
            name,
            OPath | ODirectory | ONoFollow | OCloseOnExec);
        if (descriptor >= 0)
        {
            return new SafeFileHandle((nint)descriptor, ownsHandle: true);
        }

        var error = Marshal.GetLastPInvokeError();
        if (!createDirectory || error != ENoEntry)
        {
            throw CreateException($"opening workspace directory component '{name}'", error);
        }

        if (mkdirat(GetDescriptor(parent), name, Convert.ToUInt32("755", 8)) != 0)
        {
            error = Marshal.GetLastPInvokeError();
            if (error != EExists)
            {
                throw CreateException($"creating workspace directory component '{name}'", error);
            }
        }

        descriptor = openat(
            GetDescriptor(parent),
            name,
            OPath | ODirectory | ONoFollow | OCloseOnExec);
        if (descriptor < 0)
        {
            throw CreateException($"opening newly-created workspace directory component '{name}'");
        }

        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static string[] SplitRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new UnauthorizedAccessException($"Workspace path must be relative: '{relativePath}'.");
        }

        var parts = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(static part => part is "." or ".." || part.Contains('/')))
        {
            throw new UnauthorizedAccessException($"Workspace path escapes its trusted root: '{relativePath}'.");
        }

        return parts;
    }

    private static void EnsureRegularFile(SafeFileHandle file, string relativePath)
    {
        var buffer = Marshal.AllocHGlobal(StatxBufferSize);
        try
        {
            if (statx(
                    GetDescriptor(file),
                    string.Empty,
                    AtEmptyPath | AtSymlinkNoFollow,
                    StatxType,
                    buffer) != 0)
            {
                throw CreateException($"validating workspace file '{relativePath}'");
            }

            var mode = unchecked((ushort)Marshal.ReadInt16(buffer, 28));
            if ((mode & FileTypeMask) != RegularFileType)
            {
                throw new UnauthorizedAccessException(
                    $"Workspace path is not a regular file: '{relativePath}'.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SetModifiedUtc(
        SafeFileHandle file,
        DateTime modifiedUtc,
        bool useEmptyPath = false)
    {
        var utc = modifiedUtc.ToUniversalTime();
        var unixSeconds = new DateTimeOffset(utc).ToUnixTimeSeconds();
        var nanoseconds = (utc.Ticks % TimeSpan.TicksPerSecond) * 100;
        var timestamps = new[]
        {
            new Timespec(0, UTimeOmit),
            new Timespec(unixSeconds, nanoseconds)
        };

        var result = useEmptyPath
            ? utimensat(GetDescriptor(file), string.Empty, timestamps, AtEmptyPath)
            : futimens(GetDescriptor(file), timestamps);
        if (result != 0)
        {
            throw CreateException("setting workspace file timestamp");
        }
    }

    private static int GetDescriptor(SafeFileHandle handle)
        => checked((int)handle.DangerousGetHandle());

    private static Exception CreateException(string operation, int? error = null)
    {
        var errorCode = error ?? Marshal.GetLastPInvokeError();
        return new IOException(
            $"Linux filesystem operation failed while {operation}: " +
            $"{new Win32Exception(errorCode).Message} (errno={errorCode}).");
    }

    internal sealed class PendingPublication : IDisposable
    {
        private readonly SafeFileHandle _parent;
        private readonly SafeFileHandle _file;
        private readonly string _temporaryName;
        private readonly string _destinationName;
        private readonly string _relativePath;
        private bool _committed;

        public PendingPublication(
            SafeFileHandle parent,
            SafeFileHandle file,
            string temporaryName,
            string destinationName,
            string relativePath)
        {
            _parent = parent;
            _file = file;
            _temporaryName = temporaryName;
            _destinationName = destinationName;
            _relativePath = relativePath;
        }

        public FileStream OpenWriteStream()
        {
            var descriptor = fcntl(
                GetDescriptor(_file),
                FDuplicateCloseOnExec,
                0);
            if (descriptor < 0)
            {
                throw CreateException($"duplicating temporary workspace file for '{_relativePath}'");
            }

            return new FileStream(
                new SafeFileHandle((nint)descriptor, ownsHandle: true),
                FileAccess.Write,
                64 * 1024,
                isAsync: false);
        }

        public void SetModifiedUtc(DateTime modifiedUtc)
            => LinuxSecureDirectoryRoot.SetModifiedUtc(_file, modifiedUtc);

        public void Commit()
        {
            if (fchmod(GetDescriptor(_file), Convert.ToUInt32("644", 8)) != 0)
            {
                throw CreateException($"setting published workspace file mode for '{_relativePath}'");
            }

            if (renameat(
                    GetDescriptor(_parent),
                    _temporaryName,
                    GetDescriptor(_parent),
                    _destinationName) != 0)
            {
                throw CreateException($"publishing workspace file '{_relativePath}'");
            }

            _committed = true;
        }

        public void Dispose()
        {
            _file.Dispose();
            if (!_committed)
            {
                _ = unlinkat(GetDescriptor(_parent), _temporaryName, 0);
            }

            _parent.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Timespec(long seconds, long nanoseconds)
    {
        public readonly long Seconds = seconds;
        public readonly long Nanoseconds = nanoseconds;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int openat(int directoryFileDescriptor, string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int openat(int directoryFileDescriptor, string path, int flags, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkdirat(int directoryFileDescriptor, string path, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchmod(int fileDescriptor, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fileDescriptor, int command, int argument);

    [DllImport("libc", SetLastError = true)]
    private static extern int renameat(
        int oldDirectoryFileDescriptor,
        string oldPath,
        int newDirectoryFileDescriptor,
        string newPath);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlinkat(int directoryFileDescriptor, string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int futimens(int fileDescriptor, Timespec[] times);

    [DllImport("libc", SetLastError = true)]
    private static extern int utimensat(
        int directoryFileDescriptor,
        string path,
        Timespec[] times,
        int flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "statx")]
    private static extern int statx(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        nint buffer);
}
