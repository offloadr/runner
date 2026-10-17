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
            foreach (var path in handed.Closed)
            {
                RunnerLog.Info($"Sealed storage: '{path}', left by an earlier session user, now belongs to root.");
            }

            foreach (var directory in lockdown.Closed)
            {
                RunnerLog.Info($"Sealed storage: '{directory}' is no longer writable by session users.");
            }

            var stillWritable = handed.StillWritable.Concat(lockdown.StillWritable).ToArray();
            if (stillWritable.Length > 0)
            {
                // Sessions could write their data to these disk-backed places.
                return "these disk-backed paths stay writable by session users: " + string.Join(", ", stillWritable);
            }

            if (scan.Unchecked.Count > 0)
            {
                // A session may still reach a writable place below these, which the scan could not see.
                return "these could not be read to check what session users can write: " + string.Join(", ", scan.Unchecked);
            }

            _applied = true;
            return null;
        }
    }
}
