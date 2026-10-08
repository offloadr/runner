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
    private const int OReadOnly = 0x0;
    private const int OWriteOnly = 0x1;
    private const int OReadWrite = 0x2;
    private const int OCreate = 0x40;
    private const int OExclusive = 0x80;
    private const int ONoControllingTerminal = 0x100;
    private const int ONonBlock = 0x800;
    private const int ODirectory = 0x10000;
    private const int ONoFollow = 0x20000;
    private const int OCloseOnExec = 0x80000;
    private const int OPath = 0x200000;
    private const int FDuplicateCloseOnExec = 1030;
    private const int ENoEntry = 2;
    private const int ENoDeviceOrAddress = 6;
    private const int EExists = 17;
    private const int ECrossDevice = 18;
    private const int ENotDirectory = 20;
    private const int EIsDirectory = 21;
    private const int EInvalidArgument = 22;
    private const int ETooManySymbolicLinks = 40;
    private const int AtEmptyPath = 0x1000;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x0001;
    private const uint StatxNlink = 0x0004;
    private const uint StatxMtime = 0x0040;
    private const uint StatxSize = 0x0200;
    private const int StatxBufferSize = 256;
    private const int StatxNlinkOffset = 16;
    private const int StatxModeOffset = 28;
    private const int StatxSizeOffset = 40;
    private const int StatxMtimeSecondsOffset = 112;
    private const int StatxMtimeNanosecondsOffset = 120;
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

    /// <summary>
    /// Creates an empty regular file, and any missing parent directories, unless a regular
    /// file already exists there. Returns true when this call created the file.
    /// </summary>
    public bool EnsurePlaceholder(string relativePath, DateTime? modifiedUtc)
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

                return true;
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

            return false;
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

    /// <summary>
    /// Describes <paramref name="relativePath"/> without following symlinks or
    /// opening it for I/O. Only a single-link regular file reached through real
    /// directories is reported as <see cref="SecureEntryKind.RegularFile"/>.
    /// </summary>
    public SecureFileStatus GetStatus(string relativePath)
    {
        var (kind, parent, leafName) = TryOpenExistingParent(relativePath);
        if (parent is null)
        {
            return new SecureFileStatus(kind, 0, default);
        }

        using (parent)
        {
            var descriptor = openat(
                GetDescriptor(parent),
                leafName,
                OPath | ONoFollow | OCloseOnExec);
            if (descriptor < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ENoEntry)
                {
                    return new SecureFileStatus(SecureEntryKind.Missing, 0, default);
                }

                throw CreateException($"inspecting '{relativePath}'", error);
            }

            using var entry = new SafeFileHandle((nint)descriptor, ownsHandle: true);
            return ReadStatus(entry, relativePath);
        }
    }

    /// <summary>
    /// Opens an existing single-link regular file for reading without following
    /// symlinks or blocking on FIFOs and devices. Returns null when it is missing.
    /// </summary>
    public FileStream? OpenRegularFileForRead(string relativePath)
    {
        var handle = OpenExistingRegularFile(relativePath, OReadOnly, out _);
        return handle is null
            ? null
            : new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
    }

    /// <summary>
    /// Truncates an existing single-link regular file to zero bytes and optionally
    /// sets its modification time. Returns false when it is missing.
    /// </summary>
    public bool TruncateRegularFile(string relativePath, DateTime? modifiedUtc)
    {
        using var handle = OpenExistingRegularFile(relativePath, OWriteOnly, out _);
        if (handle is null)
        {
            return false;
        }

        if (ftruncate(GetDescriptor(handle), 0) != 0)
        {
            throw CreateException($"truncating '{relativePath}'");
        }

        if (modifiedUtc.HasValue)
        {
            SetModifiedUtc(handle, modifiedUtc.Value);
        }

        return true;
    }

    /// <summary>
    /// Creates every directory component of <paramref name="relativeDirectory"/>
    /// beneath the root without following symlinks.
    /// </summary>
    public void EnsureDirectory(string relativeDirectory)
    {
        var parts = SplitRelativePath(relativeDirectory);
        var current = DuplicateDirectory(_rootHandle);
        try
        {
            foreach (var part in parts)
            {
                var next = OpenDirectory(current, part, createDirectory: true);
                current.Dispose();
                current = next;
            }
        }
        finally
        {
            current.Dispose();
        }
    }

    /// <summary>
    /// Reads the symbolic link at <paramref name="relativePath"/> without following
    /// any component. Returns <see cref="SecureLinkKind.Other"/> when the leaf is not
    /// a symbolic link or a directory component is not a real directory.
    /// </summary>
    public SecureLinkKind ReadSymbolicLink(string relativePath, out string? target)
    {
        target = null;
        var (kind, parent, leafName) = TryOpenExistingParent(relativePath);
        if (parent is null)
        {
            return kind == SecureEntryKind.Missing ? SecureLinkKind.Missing : SecureLinkKind.Other;
        }

        using (parent)
        {
            var buffer = new byte[4096];
            var length = readlinkat(GetDescriptor(parent), leafName, buffer, (nuint)buffer.Length);
            if (length >= 0)
            {
                if (length >= buffer.Length)
                {
                    return SecureLinkKind.Other;
                }

                target = System.Text.Encoding.UTF8.GetString(buffer, 0, (int)length);
                return SecureLinkKind.SymbolicLink;
            }

            var error = Marshal.GetLastPInvokeError();
            return error switch
            {
                ENoEntry => SecureLinkKind.Missing,
                EInvalidArgument => SecureLinkKind.Other,
                _ => throw CreateException($"reading link '{relativePath}'", error)
            };
        }
    }

    /// <summary>
    /// Creates a symbolic link at <paramref name="relativePath"/>, creating missing
    /// directory components beneath the root without following symlinks.
    /// </summary>
    public void CreateSymbolicLink(string relativePath, string target)
    {
        var (parent, leafName) = OpenParent(relativePath, createDirectories: true);
        using (parent)
        {
            if (symlinkat(target, GetDescriptor(parent), leafName) != 0)
            {
                throw CreateException($"creating link '{relativePath}'");
            }
        }
    }

    /// <summary>
    /// Removes <paramref name="relativePath"/> only when it is a symbolic link reached
    /// through real directories. Returns false when nothing was removed.
    /// </summary>
    public bool DeleteSymbolicLink(string relativePath)
    {
        var (_, parent, leafName) = TryOpenExistingParent(relativePath);
        if (parent is null)
        {
            return false;
        }

        using (parent)
        {
            var buffer = new byte[1];
            if (readlinkat(GetDescriptor(parent), leafName, buffer, (nuint)buffer.Length) < 0)
            {
                return false;
            }

            if (unlinkat(GetDescriptor(parent), leafName, 0) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ENoEntry)
                {
                    return false;
                }

                throw CreateException($"removing link '{relativePath}'", error);
            }

            return true;
        }
    }

    /// <summary>
    /// Moves the regular file at <paramref name="sourcePath"/> to <paramref name="relativePath"/>
    /// beneath this root, replacing what is there and creating missing directories. The source
    /// must be in a directory untrusted users cannot write. Files on another filesystem are copied.
    /// </summary>
    public void MoveFileInto(string sourcePath, string relativePath)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var sourceName = Path.GetFileName(fullSourcePath);
        var sourceDirectoryPath = Path.GetDirectoryName(fullSourcePath);
        if (string.IsNullOrEmpty(sourceName) || string.IsNullOrEmpty(sourceDirectoryPath))
        {
            throw new IOException($"Cannot move '{sourcePath}': it has no parent directory.");
        }

        var descriptor = open(sourceDirectoryPath, OPath | ODirectory | ONoFollow | OCloseOnExec);
        if (descriptor < 0)
        {
            throw CreateException($"opening source directory '{sourceDirectoryPath}'");
        }

        using var sourceDirectory = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        descriptor = openat(
            GetDescriptor(sourceDirectory),
            sourceName,
            OReadOnly | ONoFollow | ONonBlock | ONoControllingTerminal | OCloseOnExec);
        if (descriptor < 0)
        {
            throw CreateException($"opening source file '{fullSourcePath}'");
        }

        using var sourceFile = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        if (ReadStatus(sourceFile, fullSourcePath).Kind != SecureEntryKind.RegularFile)
        {
            throw NotRegularFile(fullSourcePath);
        }

        var (parent, leafName) = OpenParent(relativePath, createDirectories: true);
        using (parent)
        {
            if (renameat(GetDescriptor(sourceDirectory), sourceName, GetDescriptor(parent), leafName) == 0)
            {
                return;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error != ECrossDevice)
            {
                throw CreateException($"moving '{fullSourcePath}' to '{relativePath}'", error);
            }
        }

        using (var publication = CreatePublication(relativePath))
        {
            using (var input = new FileStream(sourceFile, FileAccess.Read, 1024 * 1024, isAsync: false))
            using (var output = publication.OpenWriteStream())
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            publication.Commit();
        }

        if (unlinkat(GetDescriptor(sourceDirectory), sourceName, 0) != 0)
        {
            throw CreateException($"removing moved source file '{fullSourcePath}'");
        }
    }

    public void Dispose() => _rootHandle.Dispose();

    private SafeFileHandle? OpenExistingRegularFile(
        string relativePath,
        int accessMode,
        out SecureFileStatus status)
    {
        status = default;
        var (kind, parent, leafName) = TryOpenExistingParent(relativePath);
        if (parent is null)
        {
            if (kind == SecureEntryKind.Missing)
            {
                return null;
            }

            throw NotRegularFile(relativePath);
        }

        using (parent)
        {
            var descriptor = openat(
                GetDescriptor(parent),
                leafName,
                accessMode | ONoFollow | ONonBlock | ONoControllingTerminal | OCloseOnExec);
            if (descriptor < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                return error switch
                {
                    ENoEntry => null,
                    ETooManySymbolicLinks or ENoDeviceOrAddress or EIsDirectory or ENotDirectory
                        => throw NotRegularFile(relativePath),
                    _ => throw CreateException($"opening '{relativePath}'", error)
                };
            }

            var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
            try
            {
                status = ReadStatus(handle, relativePath);
                if (status.Kind != SecureEntryKind.RegularFile)
                {
                    throw NotRegularFile(relativePath);
                }

                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
    }

    private (SecureEntryKind Kind, SafeFileHandle? Parent, string LeafName) TryOpenExistingParent(string relativePath)
    {
        var parts = SplitRelativePath(relativePath);
        var current = DuplicateDirectory(_rootHandle);
        try
        {
            for (var index = 0; index < parts.Length - 1; index++)
            {
                var descriptor = openat(
                    GetDescriptor(current),
                    parts[index],
                    OPath | ODirectory | ONoFollow | OCloseOnExec);
                if (descriptor < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    current.Dispose();
                    return error switch
                    {
                        ENoEntry => (SecureEntryKind.Missing, null, string.Empty),
                        ENotDirectory or ETooManySymbolicLinks => (SecureEntryKind.Other, null, string.Empty),
                        _ => throw CreateException($"opening directory component '{parts[index]}'", error)
                    };
                }

                current.Dispose();
                current = new SafeFileHandle((nint)descriptor, ownsHandle: true);
            }

            return (SecureEntryKind.RegularFile, current, parts[^1]);
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SecureFileStatus ReadStatus(SafeFileHandle file, string relativePath)
    {
        var buffer = Marshal.AllocHGlobal(StatxBufferSize);
        try
        {
            if (statx(
                    GetDescriptor(file),
                    string.Empty,
                    AtEmptyPath | AtSymlinkNoFollow,
                    StatxType | StatxNlink | StatxSize | StatxMtime,
                    buffer) != 0)
            {
                throw CreateException($"inspecting '{relativePath}'");
            }

            var mode = unchecked((ushort)Marshal.ReadInt16(buffer, StatxModeOffset));
            var links = unchecked((uint)Marshal.ReadInt32(buffer, StatxNlinkOffset));
            if ((mode & FileTypeMask) != RegularFileType || links > 1)
            {
                // Hard links can make a file outside the tree appear inside it.
                return new SecureFileStatus(SecureEntryKind.Other, 0, default);
            }

            var size = Marshal.ReadInt64(buffer, StatxSizeOffset);
            var seconds = Marshal.ReadInt64(buffer, StatxMtimeSecondsOffset);
            var nanoseconds = unchecked((uint)Marshal.ReadInt32(buffer, StatxMtimeNanosecondsOffset));
            var modifiedUtc = DateTime.UnixEpoch.AddTicks(
                (seconds * TimeSpan.TicksPerSecond) + (nanoseconds / 100));
            return new SecureFileStatus(SecureEntryKind.RegularFile, size, modifiedUtc);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static UnauthorizedAccessException NotRegularFile(string relativePath)
        => new($"Path is not a regular file beneath its trusted root: '{relativePath}'.");

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

    [DllImport("libc", SetLastError = true)]
    private static extern int ftruncate(int fileDescriptor, long length);

    [DllImport("libc", SetLastError = true)]
    private static extern nint readlinkat(int directoryFileDescriptor, string path, byte[] buffer, nuint bufferSize);

    [DllImport("libc", SetLastError = true)]
    private static extern int symlinkat(string target, int directoryFileDescriptor, string linkPath);
}

internal enum SecureLinkKind
{
    Missing,
    SymbolicLink,
    Other
}

internal enum SecureEntryKind
{
    Missing,
    RegularFile,
    Other
}

internal readonly record struct SecureFileStatus(SecureEntryKind Kind, long Length, DateTime LastWriteTimeUtc)
{
    public bool IsRegularFile => Kind == SecureEntryKind.RegularFile;
}
