namespace Offloadr.Runner.Linux;

/// <summary>
/// Closes the disk-backed shared directories to session users before sealed data arrives (see
/// <see cref="SealedHostLockdown"/>). Required mode applies it at startup, auto mode before its
/// first sealed start; once applied it stays so until the runner exits, for every session.
/// </summary>
public sealed class SealedHostPreparation
{
    private readonly object _gate = new();
    private bool _applied;

    /// <summary>Applies the lockdown once it holds; returns why it cannot, or null when it does.</summary>
    public string? EnsureApplied()
    {
        lock (_gate)
        {
            if (_applied)
            {
                return null;
            }

            var scan = SealedHostLockdown.Scan();
            // Residue of an earlier session user first: its uid may come back.
            var handed = SealedHostLockdown.HandToRoot(scan.ReusablyOwned);
            var lockdown = SealedHostLockdown.RemoveSharedWriteAccess(
                SealedHostLockdown.SharedTemporaryDirectories
                    .Concat(scan.SharedWritable)
                    .Distinct(StringComparer.Ordinal));
            if (handed.Closed.Count > 0)
            {
                RunnerLog.Info($"Sealed storage: entries left by earlier session users now belong to root: {Summarize(handed.Closed)}.");
            }

            if (lockdown.Closed.Count > 0)
            {
                RunnerLog.Info($"Sealed storage: no longer writable by session users: {Summarize(lockdown.Closed)}.");
            }

            var stillWritable = handed.StillWritable.Concat(lockdown.StillWritable).ToArray();
            if (stillWritable.Length > 0)
            {
                // Sessions could write their data to these disk-backed places.
                return "disk-backed paths stay writable by session users: " + Summarize(stillWritable);
            }

            if (scan.Unchecked.Count > 0)
            {
                // A session may still reach a writable place below these, which the scan could not see.
                return "paths could not be read to check what session users can write: " + Summarize(scan.Unchecked);
            }

            _applied = true;
            return null;
        }
    }

    /// <summary>
    /// Counts paths by their top-level directory, such as "3 under /tmp, 1 under /workspace".
    /// Names below it may have been chosen by an earlier session and carry its data, and these
    /// lines reach the host's disk; the top level is the image's or a mount's.
    /// </summary>
    internal static string Summarize(IEnumerable<string> paths)
        => string.Join(
            ", ",
            paths
                .GroupBy(static path => TopLevel(path), StringComparer.Ordinal)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => $"{group.Count()} under {group.Key}"));

    private static string TopLevel(string path)
    {
        var separator = path.IndexOf('/', 1);
        return separator < 0 ? path : path[..separator];
    }
}
