namespace Offloadr.Runner.Supervisor;

/// <summary>
/// Lets one supervisor at a time manage the runner container, so two copies of the app never both
/// adopt, restart or remove it. A held file handle, not a PID file, owns the lock, so a crash never
/// leaves it stale.
/// </summary>
public sealed class SupervisorInstanceLock(string path) : IDisposable
{
    private FileStream? _handle;

    /// <summary>True when the last attempt failed because another copy holds the lock.</summary>
    public bool HeldElsewhere { get; private set; }

    /// <summary>True when this instance now holds the lock (or already did).</summary>
    public bool TryAcquire() => TryAcquire(out _);

    /// <summary>
    /// True when this instance now holds the lock (or already did); otherwise why not: another copy
    /// holds it, or the app's data folder cannot be used.
    /// </summary>
    public bool TryAcquire(out string? reason)
    {
        reason = null;
        HeldElsewhere = false;
        if (_handle is not null)
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            _handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            reason = $"The app cannot use its data folder: {ex.Message}";
            return false;
        }
        catch (IOException)
        {
            HeldElsewhere = true;
            reason = "Another copy of the app is already running the runner. Use that one, or close it first.";
            return false;
        }
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}
