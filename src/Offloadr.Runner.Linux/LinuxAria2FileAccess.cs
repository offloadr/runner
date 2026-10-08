using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Offloadr.Runner.Linux;

/// <summary>
/// Runs aria2 as an unprivileged account. aria2 opens files by path, so a path component
/// swapped for a symlink after validation redirects its writes; as that account they can only
/// reach directories prepared here. Those are root-owned directories that only root and the
/// account's group may write: the state directory, the staging directory and the directories
/// beneath the configured model roots that hold a download. Destinations in directories an
/// untrusted user controls (session homes) are not among them; they are staged and published
/// by root (<see cref="SessionModelDownloadStaging"/>).
/// </summary>
internal sealed class LinuxAria2FileAccess : IAria2FileAccess
{
    private const int OReadOnly = 0x0;
    private const int ONoControllingTerminal = 0x100;
    private const int ONonBlock = 0x800;
    private const int ODirectory = 0x10000;
    private const int ONoFollow = 0x20000;
    private const int OCloseOnExec = 0x80000;
    private const int ENoEntry = 2;
    private const int ETooManySymbolicLinks = 40;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 0x0001;
    private const uint StatxMode = 0x0002;
    private const uint StatxUid = 0x0008;
    private const uint StatxGid = 0x0010;
    private const int StatxBufferSize = 256;
    private const int StatxUidOffset = 20;
    private const int StatxGidOffset = 24;
    private const int StatxModeOffset = 28;
    private const uint FileTypeMask = 0xF000;
    private const uint DirectoryType = 0x4000;
    private const uint RegularFileType = 0x8000;
    private const uint PermissionMask = 0xFFF;
    private const uint GroupAll = 0x38; // g+rwx
    private const uint KeepOwner = uint.MaxValue;

    // Sticky: aria2 can add files but cannot rename or remove what root keeps there
    // (the RPC configuration and the transfer index).
    internal const uint StateDirectoryMode = 0x3F8 | 0x200; // 01770
    internal const uint StagingDirectoryMode = 0x1F8; // 0770
    internal const uint PrivateFileMode = 0x180; // 0600

    private readonly string[] _writableRoots;
    private readonly string _stagingDirectory;

    /// <param name="account">The account aria2 runs as, or null to leave ownership alone.</param>
    /// <param name="modelRoots">Roots beneath which aria2 may write downloads in place.</param>
    /// <param name="stagingDirectory">Where staged downloads are written.</param>
    /// <param name="sessionRoot">
    /// The directory holding session homes. Model roots at or beneath it are dropped: sessions
    /// can write there, so downloads into it must be staged.
    /// </param>
    public LinuxAria2FileAccess(
        Aria2ServiceAccount? account,
        IEnumerable<string?> modelRoots,
        string stagingDirectory,
        string? sessionRoot = null)
    {
        ArgumentNullException.ThrowIfNull(modelRoots);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        Account = account;
        _stagingDirectory = Normalize(stagingDirectory);
        var untrusted = string.IsNullOrWhiteSpace(sessionRoot) ? null : Normalize(sessionRoot);
        _writableRoots = modelRoots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(static root => Normalize(root!))
            .Where(root => untrusted is null || !IsSameOrBeneath(root, untrusted))
            .Append(_stagingDirectory)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public Aria2ServiceAccount? Account { get; }

    internal IReadOnlyList<string> WritableRoots => _writableRoots;

    /// <summary>
    /// Resolves the account aria2 runs as. Without the account, or when the agent is not root,
    /// aria2 runs as the agent's own user unless <see cref="Aria2Settings.RequireUser"/> is set.
    /// </summary>
    public static LinuxAria2FileAccess Create(
        Aria2Settings settings,
        IEnumerable<string?> modelRoots,
        string? sessionRoot,
        string passwdPath = "/etc/passwd",
        uint? effectiveUserId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var account = ResolveAccount(
            settings,
            passwdPath,
            effectiveUserId ?? VfsIpcPeerCredentials.CurrentEffectiveUserId ?? uint.MaxValue);
        return new LinuxAria2FileAccess(account, modelRoots, settings.StagingDirectory, sessionRoot);
    }

    internal static Aria2ServiceAccount? ResolveAccount(Aria2Settings settings, string passwdPath, uint effectiveUserId)
    {
        var account = TryReadAccount(settings.User, passwdPath);
        string? fallbackReason = null;
        if (account is null)
        {
            fallbackReason = $"aria2 account '{settings.User}' does not exist";
        }
        else if (effectiveUserId != 0)
        {
            fallbackReason = $"the agent is not root and cannot start aria2 as '{settings.User}'";
        }

        if (fallbackReason is null)
        {
            return account;
        }

        if (settings.RequireUser)
        {
            throw new InvalidOperationException($"Cannot run aria2 unprivileged: {fallbackReason}.");
        }

        RunnerLog.Warning(nameof(LinuxAria2FileAccess), $"{fallbackReason}; aria2 runs as the agent's own user.");
        return null;
    }

    /// <summary>Reads <paramref name="userName"/> from a passwd-format file.</summary>
    public static Aria2ServiceAccount? TryReadAccount(string? userName, string passwdPath = "/etc/passwd")
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(passwdPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var line in lines)
        {
            var fields = line.Split(':');
            if (fields.Length >= 4 &&
                string.Equals(fields[0], userName, StringComparison.Ordinal) &&
                uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var userId) &&
                uint.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var groupId))
            {
                return new Aria2ServiceAccount(userName, userId, groupId);
            }
        }

        return null;
    }

    public void PrepareStateDirectory(Aria2Settings settings, string rpcConfigPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var stateDirectory = Normalize(settings.StateDirectory);
        Directory.CreateDirectory(stateDirectory);
        Directory.CreateDirectory(_stagingDirectory);
        if (Account is not { } account)
        {
            return;
        }

        using (var state = OpenDirectory(stateDirectory))
        {
            SetOwnerAndMode(state, 0, account.GroupId, StateDirectoryMode, stateDirectory);
            ChownFile(state, Path.GetFileName(rpcConfigPath), account, PrivateFileMode, required: true);
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(settings.SessionFilePath)), stateDirectory, StringComparison.Ordinal))
            {
                ChownFile(state, Path.GetFileName(settings.SessionFilePath), account, mode: null, required: false);
            }
            else
            {
                RunnerLog.Warning(
                    nameof(LinuxAria2FileAccess),
                    $"aria2 session file '{settings.SessionFilePath}' is outside '{stateDirectory}'; " +
                    $"'{account.UserName}' needs write access to its directory to save the session.");
            }
        }

        using var staging = OpenDirectory(_stagingDirectory);
        SetOwnerAndMode(staging, 0, account.GroupId, StagingDirectoryMode, _stagingDirectory);
    }

    public void PrepareTransferTarget(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (Account is not { } account)
        {
            return;
        }

        var destination = Normalize(destinationPath);
        var parent = Path.GetDirectoryName(destination);
        var leafName = Path.GetFileName(destination);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leafName))
        {
            throw new InvalidOperationException($"Download destination '{destination}' has no parent directory.");
        }

        // Resolve links the agent already accepted (ModelDestinationPolicy), then walk the
        // canonical path without following any, so a link planted since cannot move this grant.
        var canonicalParent = Normalize(LinuxPathCanonicalizer.ResolveExistingPath(parent));
        var root = _writableRoots
            .Select(static root => Normalize(LinuxPathCanonicalizer.ResolveExistingPath(root)))
            .Where(root => IsSameOrBeneath(canonicalParent, root))
            .OrderByDescending(static root => root.Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"aria2 may not write '{destination}': its directory is not beneath a download root.");

        using var directory = OpenBeneath(root, Path.GetRelativePath(root, canonicalParent));
        GrantGroupWrite(directory, account.GroupId, canonicalParent);
        ChownFile(directory, leafName, account, mode: null, required: false);
        ChownFile(directory, $"{leafName}.aria2", account, mode: null, required: false);
    }

    private static void GrantGroupWrite(SafeFileHandle directory, uint groupId, string description)
    {
        var (_, _, groupOwner, mode) = ReadStatus(directory, description);
        if (groupOwner != groupId && fchown(GetDescriptor(directory), KeepOwner, groupId) != 0)
        {
            throw CreateException($"changing the group of '{description}'");
        }

        if ((mode & GroupAll) != GroupAll && fchmod(GetDescriptor(directory), mode | GroupAll) != 0)
        {
            throw CreateException($"granting group write on '{description}'");
        }
    }

    private static void SetOwnerAndMode(SafeFileHandle handle, uint userId, uint groupId, uint mode, string description)
    {
        var (_, owner, groupOwner, currentMode) = ReadStatus(handle, description);
        if ((owner != userId || groupOwner != groupId) && fchown(GetDescriptor(handle), userId, groupId) != 0)
        {
            throw CreateException($"changing the owner of '{description}'");
        }

        if (currentMode != mode && fchmod(GetDescriptor(handle), mode) != 0)
        {
            throw CreateException($"changing the mode of '{description}'");
        }
    }

    private static void ChownFile(SafeFileHandle directory, string name, Aria2ServiceAccount account, uint? mode, bool required)
    {
        var descriptor = openat(
            GetDescriptor(directory),
            name,
            OReadOnly | ONoFollow | ONonBlock | ONoControllingTerminal | OCloseOnExec);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ENoEntry && !required)
            {
                return;
            }

            throw error == ETooManySymbolicLinks
                ? new IOException($"aria2 file '{name}' is a symbolic link.")
                : CreateException($"opening aria2 file '{name}'", error);
        }

        using var file = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        var (type, owner, groupOwner, currentMode) = ReadStatus(file, name);
        if (type != RegularFileType)
        {
            throw new IOException($"aria2 file '{name}' is not a regular file.");
        }

        if ((owner != account.UserId || groupOwner != account.GroupId) &&
            fchown(GetDescriptor(file), account.UserId, account.GroupId) != 0)
        {
            throw CreateException($"changing the owner of aria2 file '{name}'");
        }

        if (mode is { } requiredMode && currentMode != requiredMode && fchmod(GetDescriptor(file), requiredMode) != 0)
        {
            throw CreateException($"changing the mode of aria2 file '{name}'");
        }
    }

    private static SafeFileHandle OpenBeneath(string root, string relativePath)
    {
        var current = OpenDirectory(root);
        try
        {
            if (relativePath == ".")
            {
                return current;
            }

            foreach (var part in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == "..")
                {
                    throw new InvalidOperationException($"Download directory '{relativePath}' escapes '{root}'.");
                }

                var descriptor = openat(
                    GetDescriptor(current),
                    part,
                    OReadOnly | ODirectory | ONoFollow | OCloseOnExec);
                if (descriptor < 0)
                {
                    throw CreateException($"opening download directory component '{part}' beneath '{root}'");
                }

                current.Dispose();
                current = new SafeFileHandle((nint)descriptor, ownsHandle: true);
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        var descriptor = open(path, OReadOnly | ODirectory | ONoFollow | OCloseOnExec);
        if (descriptor < 0)
        {
            throw CreateException($"opening directory '{path}'");
        }

        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static (uint Type, uint Owner, uint Group, uint Mode) ReadStatus(SafeFileHandle handle, string description)
    {
        var buffer = Marshal.AllocHGlobal(StatxBufferSize);
        try
        {
            if (statx(GetDescriptor(handle), string.Empty, AtEmptyPath, StatxType | StatxMode | StatxUid | StatxGid, buffer) != 0)
            {
                throw CreateException($"inspecting '{description}'");
            }

            var mode = unchecked((uint)(ushort)Marshal.ReadInt16(buffer, StatxModeOffset));
            return (
                mode & FileTypeMask,
                unchecked((uint)Marshal.ReadInt32(buffer, StatxUidOffset)),
                unchecked((uint)Marshal.ReadInt32(buffer, StatxGidOffset)),
                mode & PermissionMask);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsSameOrBeneath(string path, string root)
        => string.Equals(path, root, StringComparison.Ordinal) ||
           path.StartsWith(root.EndsWith('/') ? root : root + "/", StringComparison.Ordinal);

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private static int GetDescriptor(SafeFileHandle handle)
        => checked((int)handle.DangerousGetHandle());

    private static IOException CreateException(string operation, int? error = null)
    {
        var errorCode = error ?? Marshal.GetLastPInvokeError();
        return new IOException(
            $"Linux filesystem operation failed while {operation}: " +
            $"{new Win32Exception(errorCode).Message} (errno={errorCode}).");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int openat(int directoryFileDescriptor, string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchown(int fileDescriptor, uint owner, uint group);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchmod(int fileDescriptor, uint mode);

    [DllImport("libc", SetLastError = true, EntryPoint = "statx")]
    private static extern int statx(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        nint buffer);
}
