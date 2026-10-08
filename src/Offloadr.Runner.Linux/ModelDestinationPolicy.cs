using System.Runtime.InteropServices;

namespace Offloadr.Runner.Linux;

/// <summary>
/// Confines model download destinations to the configured model roots. The agent
/// runs as root and creates placeholders there, so a destination must stay beneath
/// a root after canonical resolution and must not pass through a symbolic link that
/// a non-root user, including aria2's account, could have planted.
/// </summary>
internal sealed class ModelDestinationPolicy
{
    private const int AtFdcwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const int ENoEntry = 2;
    private const int ENotDirectory = 20;
    private const uint StatxType = 0x0001;
    private const uint StatxMode = 0x0002;
    private const uint StatxUid = 0x0008;
    private const int StatxBufferSize = 256;
    private const int StatxUidOffset = 20;
    private const int StatxModeOffset = 28;
    private const ushort FileTypeMask = 0xF000;
    private const ushort DirectoryType = 0x4000;
    private const ushort RegularFileType = 0x8000;
    private const ushort SymbolicLinkType = 0xA000;
    private const ushort GroupOrOtherWritable = 0x12; // S_IWGRP | S_IWOTH

    private readonly (string Lexical, string Canonical)[] _roots;

    public ModelDestinationPolicy(IEnumerable<string?> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots = roots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(static root => NormalizePath(root!))
            .Distinct(StringComparer.Ordinal)
            .Select(static root => (root, NormalizePath(LinuxPathCanonicalizer.ResolveExistingPath(root))))
            .ToArray();
    }

    public IReadOnlyList<string> Roots => _roots.Select(static root => root.Lexical).ToArray();

    /// <summary>
    /// Validates <paramref name="destinationPath"/> and returns its normalized form.
    /// Throws <see cref="InvalidOperationException"/> when it is not confined.
    /// </summary>
    public string Confine(string? destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new InvalidOperationException("Model download destination is required.");
        }

        var normalized = NormalizePath(destinationPath);
        var root = _roots
            .Select(root => IsStrictlyBeneath(normalized, root.Lexical) ? root.Lexical
                : IsStrictlyBeneath(normalized, root.Canonical) ? root.Canonical
                : null)
            .Where(static root => root is not null)
            .OrderByDescending(static root => root!.Length)
            .FirstOrDefault();
        if (root is null)
        {
            throw new InvalidOperationException(
                $"Model download destination '{normalized}' is outside the configured model roots.");
        }

        ValidateComponents(root, normalized, trustRootOwnedLinks: true);

        var canonical = NormalizePath(LinuxPathCanonicalizer.ResolveExistingPath(DeepestExistingPath(normalized)));
        if (!_roots.Any(candidate =>
                canonical == candidate.Canonical || IsStrictlyBeneath(canonical, candidate.Canonical)))
        {
            throw new InvalidOperationException(
                $"Model download destination '{normalized}' resolves outside the configured model roots.");
        }

        return normalized;
    }

    /// <summary>
    /// Validates a destination beneath a session-owned root: the root itself and every
    /// existing component below it must be a real directory, and an existing leaf must
    /// be a regular file.
    /// </summary>
    public static void ConfineToSessionRoot(string sessionRoot, string destinationPath)
    {
        var root = NormalizePath(sessionRoot);
        var normalized = NormalizePath(destinationPath);
        if (!IsStrictlyBeneath(normalized, root))
        {
            throw new InvalidOperationException(
                $"Model download destination '{normalized}' escapes the session model root.");
        }

        switch (ReadEntry(root))
        {
            case (EntryState.Missing, _, _):
            case (EntryState.Present, DirectoryType, _):
                break;
            default:
                throw new InvalidOperationException(
                    $"Session model root '{root}' is not a real directory.");
        }

        ValidateComponents(root, normalized, trustRootOwnedLinks: false);
    }

    private static void ValidateComponents(string root, string destination, bool trustRootOwnedLinks)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var relative = Path.GetRelativePath(root, destination);
        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            var parent = current;
            current = Path.Combine(current, segments[index]);
            var isLeaf = index == segments.Length - 1;
            var (state, type, _) = ReadEntry(current);
            if (state == EntryState.Missing)
            {
                return;
            }

            if (type == SymbolicLinkType)
            {
                // Links in directories only root can write were placed by the image or
                // operator; links anywhere else could redirect root-owned writes.
                if (isLeaf || !trustRootOwnedLinks || !IsRootOwnedAndNotShared(parent))
                {
                    throw new InvalidOperationException(
                        $"Model download destination '{destination}' passes through symbolic link '{current}'.");
                }

                continue;
            }

            if (isLeaf ? type != RegularFileType : type != DirectoryType)
            {
                throw new InvalidOperationException(
                    $"Model download destination '{destination}' has an unexpected file type at '{current}'.");
            }
        }
    }

    private static bool IsRootOwnedAndNotShared(string directory)
    {
        var (state, type, mode) = ReadEntry(directory, out var uid);
        return state == EntryState.Present &&
               type == DirectoryType &&
               uid == 0 &&
               (mode & GroupOrOtherWritable) == 0;
    }

    private static string DeepestExistingPath(string path)
    {
        var current = path;
        while (ReadEntry(current).State == EntryState.Missing)
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
            {
                return current;
            }

            current = parent;
        }

        return current;
    }

    private static (EntryState State, ushort Type, ushort Mode) ReadEntry(string path)
        => ReadEntry(path, out _);

    private static (EntryState State, ushort Type, ushort Mode) ReadEntry(string path, out uint uid)
    {
        uid = uint.MaxValue;
        if (!OperatingSystem.IsLinux())
        {
            if (!Path.Exists(path))
            {
                return (EntryState.Missing, 0, 0);
            }

            return (EntryState.Present, Directory.Exists(path) ? DirectoryType : RegularFileType, 0);
        }

        var buffer = Marshal.AllocHGlobal(StatxBufferSize);
        try
        {
            if (statx(AtFdcwd, path, AtSymlinkNoFollow, StatxType | StatxMode | StatxUid, buffer) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is ENoEntry or ENotDirectory)
                {
                    return (EntryState.Missing, 0, 0);
                }

                throw new IOException($"Could not inspect model destination component '{path}' (errno={error}).");
            }

            uid = unchecked((uint)Marshal.ReadInt32(buffer, StatxUidOffset));
            var mode = unchecked((ushort)Marshal.ReadInt16(buffer, StatxModeOffset));
            return (EntryState.Present, (ushort)(mode & FileTypeMask), (ushort)(mode & 0x0FFF));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsStrictlyBeneath(string path, string root)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private enum EntryState
    {
        Missing,
        Present
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "statx")]
    private static extern int statx(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        nint buffer);
}
