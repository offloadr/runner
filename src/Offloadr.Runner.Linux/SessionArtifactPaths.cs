namespace Offloadr.Runner.Linux;

/// <summary>
/// Validates artifact subfolders and filenames received from the control plane
/// before they are combined with a session artifact root.
/// </summary>
internal static class SessionArtifactPaths
{
    private static readonly char[] SubfolderSeparators = ['/', '\\'];

    /// <summary>
    /// Resolves <paramref name="subfolder"/> and <paramref name="filename"/> beneath
    /// <paramref name="root"/>. Rooted names, "." or ".." segments, separators in the
    /// filename and results outside the root are refused.
    /// </summary>
    public static bool TryResolve(
        string? root,
        string? subfolder,
        string? filename,
        out string fullPath,
        out string relativePath)
    {
        fullPath = string.Empty;
        relativePath = string.Empty;

        if (string.IsNullOrWhiteSpace(root) || !IsSafeFileName(filename))
        {
            return false;
        }

        var segments = Array.Empty<string>();
        if (!string.IsNullOrEmpty(subfolder))
        {
            if (subfolder.Contains('\0') ||
                subfolder[0] is '/' or '\\' ||
                Path.IsPathRooted(subfolder))
            {
                return false;
            }

            segments = subfolder.Split(SubfolderSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(static segment => segment is "." or ".."))
            {
                return false;
            }
        }

        string rootFullPath;
        string candidate;
        try
        {
            rootFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            candidate = rootFullPath;
            foreach (var segment in segments)
            {
                candidate = Path.Combine(candidate, segment);
            }

            candidate = Path.GetFullPath(Path.Combine(candidate, filename!));
        }
        catch
        {
            return false;
        }

        if (!candidate.StartsWith(rootFullPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        fullPath = candidate;
        relativePath = string.Join('/', segments.Append(filename!));
        return true;
    }

    /// <summary>
    /// Returns true for a single path component that names a file inside a directory.
    /// </summary>
    public static bool IsSafeFileName(string? filename)
        => !string.IsNullOrWhiteSpace(filename) &&
           filename is not "." and not ".." &&
           filename.IndexOfAny(['/', '\\', '\0']) < 0 &&
           !Path.IsPathRooted(filename);
}
