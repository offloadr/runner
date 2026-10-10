namespace Offloadr.Runner.Linux;

/// <summary>
/// Maps protocol session ids to per-session home directories. Session ids are
/// UUIDs; anything else is refused before it can become a path, a Linux user
/// or an argument to a privileged command.
/// </summary>
internal static class SessionHomePaths
{
    /// <summary>
    /// Returns true when <paramref name="sessionId"/> is a non-empty UUID in its
    /// hyphenated ("D") or compact ("N") form, without braces or whitespace.
    /// </summary>
    public static bool IsValidSessionId(string? sessionId)
    {
        // Guid parsing tolerates surrounding whitespace; the id is used verbatim as a
        // directory name, so only hex digits and hyphens are accepted.
        if (string.IsNullOrEmpty(sessionId) ||
            !sessionId.All(static character => char.IsAsciiHexDigit(character) || character == '-'))
        {
            return false;
        }

        return (Guid.TryParseExact(sessionId, "D", out var parsed) ||
                Guid.TryParseExact(sessionId, "N", out parsed)) &&
               parsed != Guid.Empty;
    }

    /// <summary>
    /// Builds the home directory for <paramref name="sessionId"/> and verifies it
    /// is a direct child of <paramref name="sessionRoot"/>.
    /// </summary>
    public static string ResolveHomeDirectory(string sessionRoot, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionRoot))
        {
            throw new ArgumentException("Session root is required.", nameof(sessionRoot));
        }

        if (!IsValidSessionId(sessionId))
        {
            throw new ArgumentException("Session id must be a non-empty UUID.", nameof(sessionId));
        }

        var homeDirectory = Path.GetFullPath(Path.Combine(sessionRoot, sessionId));
        if (!IsDirectChildOf(sessionRoot, homeDirectory))
        {
            throw new ArgumentException("Session home directory must be directly beneath the session root.", nameof(sessionId));
        }

        return homeDirectory;
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> normalizes to an immediate child
    /// of <paramref name="root"/>.
    /// </summary>
    public static bool IsDirectChildOf(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalizedRoot;
        string normalizedPath;
        try
        {
            normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return false;
        }

        var parent = Path.GetDirectoryName(normalizedPath);
        var name = Path.GetFileName(normalizedPath);
        return parent is not null &&
               string.Equals(Path.TrimEndingDirectorySeparator(parent), normalizedRoot, StringComparison.Ordinal) &&
               name.Length > 0 &&
               name is not "." and not "..";
    }
}
