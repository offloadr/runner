namespace Offloadr.Runner.Linux;

/// <summary>
/// Keeps a sealed session within the memory-backed root's budget. When the budget (free space
/// and container memory headroom) falls under half the configured minimum, uploaded artifacts
/// are evicted until it is back at the minimum. Nothing is ever moved to disk: once nothing is
/// left to evict, writes fail with ENOSPC instead.
/// </summary>
public sealed class SealedStorageEvictor(
    SealedStorageOptions options,
    SealedStorageProbe probe,
    Func<string> activeSessionId,
    Func<string, long, TimeSpan, long> evictUploadedArtifacts,
    TimeSpan? interval = null,
    TimeSpan? minimumIdle = null)
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromSeconds(15);
    private readonly TimeSpan _minimumIdle = minimumIdle ?? TimeSpan.FromMinutes(2);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (options.Mode == SealedStorageMode.Off)
        {
            return;
        }

        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    RunOnce();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    RunnerLog.Warning($"Sealed storage eviction failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal long RunOnce()
    {
        var sessionId = activeSessionId();
        if (string.IsNullOrEmpty(sessionId))
        {
            return 0;
        }

        var status = probe.Probe(options);
        var bytesToFree = ComputeBytesToFree(status, options.MinimumBytes);
        if (bytesToFree <= 0)
        {
            return 0;
        }

        var released = evictUploadedArtifacts(sessionId, bytesToFree, _minimumIdle);
        if (released < bytesToFree)
        {
            RunnerLog.Warning(
                $"Sealed storage is low: budget {status.BudgetBytes} bytes, released {released} of {bytesToFree} bytes; " +
                "new session files fail once the root is full.");
        }

        return released;
    }

    internal static long ComputeBytesToFree(SealedStorageStatus status, long minimumBytes)
        => status.BudgetBytes is { } budget && budget < minimumBytes / 2
            ? minimumBytes - budget
            : 0;
}
