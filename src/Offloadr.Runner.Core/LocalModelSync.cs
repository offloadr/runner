using Grpc.Core;
using Microsoft.Extensions.Logging;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Core;

/// <summary>
/// Keeps a runner's local model inventory current with Offloadr (<c>SyncLocalModels</c>): scans the folder
/// now and then, sends the inventory when it changed and a digest-only keepalive otherwise. A scan that
/// fails publishes nothing: the last inventory that was read stays published (its keepalives go on), and
/// with none read yet nothing is sent. Only a scan that read an empty folder publishes "no models".
/// </summary>
internal static class LocalModelSync
{
    /// <summary>How often the inventory is confirmed when it did not change; Offloadr forgets it after about three minutes.</summary>
    internal static TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromMinutes(1);

    internal static async Task RunAsync(
        Func<SyncLocalModelsRequest, CancellationToken, Task<SyncLocalModelsResponse>> syncLocalModelsAsync,
        CancellationToken shutdown,
        string runnerId,
        TimeSpan scanInterval,
        TimeSpan minBackoff,
        TimeSpan maxBackoff,
        Func<LocalModelSnapshot> snapshotProvider,
        Action<LocalModelSnapshot>? snapshotObserver,
        Task initialStatusApplied,
        ILogger logger,
        LocalModelSnapshot? initialSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(syncLocalModelsAsync);
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await initialStatusApplied.WaitAsync(shutdown).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return;
        }

        var attempt = 0;
        var scanFailures = 0;
        var rng = new Random();
        var lastSentDigest = string.Empty;
        var lastObservedDigest = initialSnapshot?.Digest ?? string.Empty;
        var hasSnapshot = initialSnapshot.HasValue;
        LocalModelSnapshot cachedSnapshot = initialSnapshot.GetValueOrDefault();
        var nextScanUtc = hasSnapshot ? DateTime.UtcNow + scanInterval : DateTime.MinValue;
        var nextKeepAliveUtc = DateTime.MinValue;
        var lastRequestWasFullSnapshot = false;

        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                if (!hasSnapshot || now >= nextScanUtc)
                {
                    LocalModelSnapshot scannedSnapshot;
                    try
                    {
                        scannedSnapshot = snapshotProvider();
                        scanFailures = 0;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Not read is not empty: what was read last stays published, and the scan is tried again soon.
                        scanFailures++;
                        now = DateTime.UtcNow;
                        logger.LogError(ex, "{Message}", $"Local model scan failed{(hasSnapshot ? "; the last inventory stays published" : "; nothing is published until a scan succeeds")}: {ex.Message}");
                        nextScanUtc = now + RegistrationPolicy.ComputeBackoff(scanFailures, minBackoff, maxBackoff, rng.NextDouble());
                        if (!hasSnapshot)
                        {
                            await Task.Delay(nextScanUtc - now, shutdown).ConfigureAwait(false);
                            continue;
                        }

                        scannedSnapshot = cachedSnapshot;
                    }

                    if (scanFailures == 0)
                    {
                        cachedSnapshot = scannedSnapshot;
                        hasSnapshot = true;
                        nextScanUtc = now + scanInterval;

                        if (snapshotObserver is not null)
                        {
                            try
                            {
                                snapshotObserver(scannedSnapshot);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "{Message}", $"Failed to reconcile local models into session roots: {ex.Message}");
                            }
                        }

                        if (!string.Equals(lastObservedDigest, scannedSnapshot.Digest, StringComparison.Ordinal))
                        {
                            logger.LogInformation("{Message}", Summary(scannedSnapshot));
                            lastObservedDigest = scannedSnapshot.Digest;
                        }
                    }
                }

                var snapshot = cachedSnapshot;
                var inventoryChanged = !string.Equals(lastSentDigest, snapshot.SelectionDigest, StringComparison.Ordinal);
                var fullSnapshot = inventoryChanged;
                var shouldSync = fullSnapshot || now >= nextKeepAliveUtc;

                if (shouldSync)
                {
                    lastRequestWasFullSnapshot = fullSnapshot;
                    var response = await syncLocalModelsAsync(
                        BuildRequest(runnerId, snapshot, fullSnapshot),
                        shutdown).ConfigureAwait(false);
                    if (inventoryChanged)
                    {
                        logger.LogInformation(
                            "{Message}",
                            $"Local model sync accepted={response.AcceptedCount} digest={snapshot.SelectionDigest[..Math.Min(12, snapshot.SelectionDigest.Length)]}");
                    }

                    lastSentDigest = snapshot.SelectionDigest;
                    nextKeepAliveUtc = DateTime.UtcNow + KeepAliveInterval;
                }

                attempt = 0;
                var delay = ComputeDelay(DateTime.UtcNow, nextScanUtc, nextKeepAliveUtc);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, shutdown).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ex is RpcException { StatusCode: StatusCode.InvalidArgument } && !lastRequestWasFullSnapshot)
                {
                    logger.LogWarning("{Message}", "Digest-only local model refresh was rejected; forcing a full local model resync.");
                    lastSentDigest = string.Empty;
                    nextKeepAliveUtc = DateTime.MinValue;
                    attempt = 0;
                    continue;
                }

                attempt++;
                if (ex is RpcException rpc)
                {
                    // The status code only: what a server or proxy wrote may carry signed URLs or credentials.
                    logger.LogError("{Message}", $"Local model sync RPC error attempt {attempt}: {rpc.StatusCode}");
                }
                else
                {
                    logger.LogError("{Message}", $"Local model sync failed ({ex.GetType().Name}).");
                }

                var delay = RegistrationPolicy.ComputeBackoff(attempt, minBackoff, maxBackoff, rng.NextDouble());
                try
                {
                    await Task.Delay(delay, shutdown).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    internal static TimeSpan ComputeDelay(DateTime nowUtc, DateTime nextScanUtc, DateTime nextKeepAliveUtc)
    {
        var nextWakeUtc = nextScanUtc <= nextKeepAliveUtc ? nextScanUtc : nextKeepAliveUtc;
        var delay = nextWakeUtc - nowUtc;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    internal static string Summary(LocalModelSnapshot snapshot)
    {
        if (snapshot.Models.Count == 0)
        {
            return "Local model scan summary: 0 models detected.";
        }

        var folderParts = snapshot.FolderStats
            .Select(stat => $"{stat.Folder}:{stat.ModelCount} ({FormatBytes(stat.TotalBytes)})");
        return $"Local model scan summary: {snapshot.Models.Count} models | {string.Join(", ", folderParts)}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var kb = bytes / 1024d;
        if (kb < 1024) return $"{kb:F1} KB";
        var mb = kb / 1024d;
        if (mb < 1024) return $"{mb:F1} MB";
        var gb = mb / 1024d;
        return $"{gb:F2} GB";
    }

    private static SyncLocalModelsRequest BuildRequest(string runnerId, LocalModelSnapshot snapshot, bool fullSnapshot)
    {
        var request = new SyncLocalModelsRequest
        {
            RunnerId = runnerId,
            SnapshotDigest = snapshot.SelectionDigest,
            FullSnapshot = fullSnapshot
        };
        if (fullSnapshot)
        {
            request.SelectionHashes.AddRange(snapshot.SelectionHashes);
        }

        return request;
    }
}
