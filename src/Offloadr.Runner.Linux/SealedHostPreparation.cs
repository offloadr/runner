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

            var unlisted = new List<string>();
            var lockdown = SealedHostLockdown.RemoveSharedWriteAccess(
                SealedHostLockdown.SharedTemporaryDirectories
                    .Concat(SealedHostLockdown.FindSharedWritableDirectories(unlisted: unlisted))
                    .Distinct(StringComparer.Ordinal));
            foreach (var directory in lockdown.Closed)
            {
                RunnerLog.Info($"Sealed storage: '{directory}' is no longer writable by session users.");
            }

            if (lockdown.StillWritable.Count > 0)
            {
                // Sessions could write their data to these disk-backed places.
                return "these disk-backed paths stay writable by session users: " + string.Join(", ", lockdown.StillWritable);
            }

            if (unlisted.Count > 0)
            {
                // A session may still reach a writable place below these, which the scan could not see.
                return "these directories could not be listed to check what session users can write below them: " +
                    string.Join(", ", unlisted);
            }

            _applied = true;
            return null;
        }
    }
}
