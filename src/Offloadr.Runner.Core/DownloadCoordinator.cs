using Offloadr.Runner.V1;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Offloadr.Runner.Core;

public interface IFileSystem
{
    string? NormalizePath(string? path);
    string? GetDirectoryName(string path);
    string? GetFileName(string path);
    void CreateDirectory(string path);
    bool FileExists(string path);
    long GetFileSize(string path);
}

public interface IAsyncDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// Routes downloads whose destination directory an untrusted user controls through a staging
/// file that only the downloader writes; the agent moves the completed file into place.
/// </summary>
public interface IModelDownloadStaging
{
    /// <summary>Returns where the downloader writes <paramref name="destinationPath"/>, or null to write it in place.</summary>
    string? GetStagingPath(string destinationPath);

    /// <summary>Moves the completed <paramref name="stagingPath"/> to <paramref name="destinationPath"/>.</summary>
    void Publish(string stagingPath, string destinationPath);

    /// <summary>Removes any staged data for <paramref name="destinationPath"/>.</summary>
    void Discard(string destinationPath);
}

public sealed class DownloadCoordinator
{
    private const double MinProgressDeltaPercent = 0.5;
    private static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultProgressInterval = TimeSpan.FromSeconds(3);

    private readonly IModelTransferBackend _transferBackend;
    private readonly IFileSystem _fileSystem;
    private readonly IAsyncDelay _delay;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _downloadTimeout;
    private readonly TimeSpan _progressInterval;
    private readonly Aria2Settings _settings;
    private readonly IModelDownloadStaging? _staging;
    private readonly ConcurrentDictionary<string, TrackedDownload> _registry = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sessionCancellations = new(StringComparer.Ordinal);
    private readonly object _pendingRemovalGate = new();
    private readonly Dictionary<string, Task<bool>> _pendingRemovals = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _removalShutdown = new();

    private string? _activeSessionId;

    public DownloadCoordinator(
        IModelTransferBackend transferBackend,
        IFileSystem? fileSystem = null,
        IAsyncDelay? delay = null,
        TimeProvider? timeProvider = null,
        TimeSpan? downloadTimeout = null,
        TimeSpan? progressInterval = null)
        : this(
            transferBackend,
            Aria2Settings.FromEnvironment(static _ => null, static () => "unused"),
            fileSystem,
            delay,
            timeProvider,
            downloadTimeout,
            progressInterval)
    {
    }

    public DownloadCoordinator(
        IModelTransferBackend transferBackend,
        Aria2Settings settings,
        IFileSystem? fileSystem = null,
        IAsyncDelay? delay = null,
        TimeProvider? timeProvider = null,
        TimeSpan? downloadTimeout = null,
        TimeSpan? progressInterval = null,
        IModelDownloadStaging? staging = null)
    {
        _staging = staging;
        _transferBackend = transferBackend ?? throw new ArgumentNullException(nameof(transferBackend));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _fileSystem = fileSystem ?? new SystemFileSystem();
        _delay = delay ?? new SystemAsyncDelay();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _downloadTimeout = downloadTimeout ?? DefaultDownloadTimeout;
        _progressInterval = progressInterval ?? DefaultProgressInterval;
    }

    public Func<ModelDownloadProgress, Task>? ProgressReporter { get; set; }

    public void SetActiveSession(string? sessionId)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        var previous = Volatile.Read(ref _activeSessionId);
        if (!string.IsNullOrWhiteSpace(previous)
            && !string.IsNullOrWhiteSpace(normalizedSessionId)
            && !string.Equals(previous, normalizedSessionId, StringComparison.Ordinal))
        {
            CancelSession(previous);
            _registry.Clear();
        }

        if (!string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            _sessionCancellations.GetOrAdd(normalizedSessionId, static _ => new CancellationTokenSource());
        }

        Volatile.Write(ref _activeSessionId, normalizedSessionId);
    }

    public void CancelSession(string? sessionId)
    {
        var normalizedSessionId = sessionId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return;
        }

        if (_sessionCancellations.TryRemove(normalizedSessionId, out var cts))
        {
            try
            {
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }
            }
            finally
            {
                cts.Dispose();
            }
        }

        foreach (var entry in _registry)
        {
            if (string.Equals(entry.Value.SessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                _registry.TryRemove(entry.Key, out _);
            }
        }
    }

    public void RegisterDownloads(
        string sessionId,
        IEnumerable<ModelDownloadRequest> downloads,
        bool replaceExisting = false)
    {
        if (downloads == null)
        {
            return;
        }

        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? sessionId : sessionId.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            _sessionCancellations.GetOrAdd(normalizedSessionId, static _ => new CancellationTokenSource());
        }

        var registrations = new List<TrackedDownload>();
        foreach (var download in downloads)
        {
            if (download == null)
            {
                continue;
            }

            var normalized = _fileSystem.NormalizePath(download.DestinationPath);
            if (normalized == null)
            {
                continue;
            }

            registrations.Add(new TrackedDownload(normalizedSessionId, download, normalized));
        }

        if (replaceExisting)
        {
            var registeredPaths = registrations
                .Select(static download => download.DestinationPath)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var entry in _registry)
            {
                if (string.Equals(entry.Value.SessionId, normalizedSessionId, StringComparison.Ordinal) &&
                    !registeredPaths.Contains(entry.Key))
                {
                    if (_registry.TryRemove(entry.Key, out var omitted))
                    {
                        omitted.CancelRegistration();
                    }
                }
            }
        }

        foreach (var registration in registrations)
        {
            _registry[registration.DestinationPath] = registration;
        }
    }

    public Task EnsureDownloadsAsync(IEnumerable<ModelDownloadRequest> downloads, CancellationToken cancellationToken, bool highPriority)
    {
        if (downloads == null)
        {
            return Task.CompletedTask;
        }

        var tasks = new List<Task>();

        foreach (var download in downloads)
        {
            if (download == null)
            {
                continue;
            }

            var normalized = _fileSystem.NormalizePath(download.DestinationPath);
            TrackedDownload tracked;

            if (normalized != null && _registry.TryGetValue(normalized, out var existing))
            {
                tracked = existing;
            }
            else
            {
                var session = GetActiveSessionId();
                var destination = normalized ?? download.DestinationPath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(destination))
                {
                    throw new InvalidOperationException("Download destination path is required.");
                }

                tracked = new TrackedDownload(session, download, destination);
                if (normalized != null)
                {
                    _registry[normalized] = tracked;
                }
            }

            tasks.Add(EnsureDownloadedAsync(tracked, cancellationToken, highPriority));
        }

        return Task.WhenAll(tasks);
    }

    public Task EnsureDownloadedAsync(string destinationPath, CancellationToken cancellationToken, bool highPriority)
    {
        var normalized = _fileSystem.NormalizePath(destinationPath);
        if (normalized == null)
        {
            return Task.FromException(new InvalidOperationException("Destination path is required."));
        }

        if (!_registry.TryGetValue(normalized, out var tracked))
        {
            var fallback = new ModelDownloadRequest
            {
                ModelId = string.Empty,
                Filename = _fileSystem.GetFileName(normalized) ?? normalized,
                DestinationPath = normalized
            };

            tracked = new TrackedDownload(GetActiveSessionId(), fallback, normalized);
            _registry[normalized] = tracked;
        }

        return EnsureDownloadedAsync(tracked, cancellationToken, highPriority);
    }

    public async Task<bool> TryEnsureRegisteredDownloadedAsync(string destinationPath, CancellationToken cancellationToken, bool highPriority)
    {
        var normalized = _fileSystem.NormalizePath(destinationPath);
        if (normalized == null)
        {
            return false;
        }

        if (!_registry.TryGetValue(normalized, out var tracked))
        {
            return false;
        }

        await EnsureDownloadedAsync(tracked, cancellationToken, highPriority).ConfigureAwait(false);
        return true;
    }

    public string? GetActiveSessionId() => Volatile.Read(ref _activeSessionId);

    private async Task EnsureDownloadedAsync(TrackedDownload tracked, CancellationToken cancellationToken, bool highPriority)
    {
        var sessionCancellation = GetSessionCancellationToken(tracked.SessionId);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            sessionCancellation,
            tracked.RegistrationCancellation);
        var effectiveCancellationToken = linkedCancellation.Token;

        var normalized = tracked.DestinationPath;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Download request missing destination_path.");
        }

        // A staged download is written where only the downloader can write and published
        // into its destination once complete.
        var stagingPath = _staging?.GetStagingPath(normalized);
        var transferPath = stagingPath ?? normalized;
        var directory = _fileSystem.GetDirectoryName(transferPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException($"Download destination '{normalized}' does not contain a directory.");
        }

        _fileSystem.CreateDirectory(directory);

        var gate = _locks.GetOrAdd(normalized, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(effectiveCancellationToken).ConfigureAwait(false);

        try
        {
            if (!await WaitForPendingTransferRemovalsAsync(
                    [normalized],
                    effectiveCancellationToken).ConfigureAwait(false))
            {
                throw new OperationCanceledException(
                    $"Transfer cleanup for '{normalized}' stopped before removal was confirmed.",
                    _removalShutdown.Token);
            }

            var retryDelay = _settings.ModelDownloadRetryInitialDelay;
            var latestProgress = DownloadProgressSnapshot.Empty(tracked.Request.SizeBytes);

            if (IsFileSatisfied(normalized, tracked.Request.SizeBytes))
            {
                await ReportProgressAsync(tracked, ModelDownloadState.Complete, tracked.Request.SizeBytes, tracked.Request.SizeBytes, 100, "already present").ConfigureAwait(false);
                return;
            }

            if (stagingPath is not null && IsFileSatisfied(stagingPath, tracked.Request.SizeBytes))
            {
                _staging!.Publish(stagingPath, normalized);
                await ReportProgressAsync(tracked, ModelDownloadState.Complete, tracked.Request.SizeBytes, tracked.Request.SizeBytes, 100, "complete").ConfigureAwait(false);
                return;
            }

            while (true)
            {
                effectiveCancellationToken.ThrowIfCancellationRequested();

                var startingMessage = latestProgress.BytesDownloaded > 0 ? "resuming download" : "starting download";
                await ReportProgressAsync(
                    tracked,
                    ModelDownloadState.InProgress,
                    latestProgress.BytesDownloaded,
                    latestProgress.TotalBytes,
                    latestProgress.Percent,
                    startingMessage).ConfigureAwait(false);

                var sourceUrl = tracked.Request.SourceUrl?.Trim();
                IReadOnlyList<ModelTransferHandle> handles;

                try
                {
                    var metalinkBytes = string.IsNullOrWhiteSpace(tracked.Request.MetalinkXml)
                        ? null
                        : Encoding.UTF8.GetBytes(tracked.Request.MetalinkXml);
                    if (string.IsNullOrWhiteSpace(sourceUrl) && metalinkBytes is null)
                    {
                        throw new InvalidOperationException($"Download request for model {tracked.Request.ModelId} is missing source_url and metalink_xml.");
                    }

                    handles = await _transferBackend
                        .CreateAsync(
                            new ModelTransferCreateRequest(
                                transferPath,
                                tracked.Request.SizeBytes,
                                string.IsNullOrWhiteSpace(sourceUrl) ? [] : [sourceUrl],
                                metalinkBytes,
                                PreferredIdentifier: null,
                                StartPaused: false,
                                QueuePosition: highPriority ? 0 : null),
                            effectiveCancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (effectiveCancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await ReportProgressAsync(
                        tracked,
                        ModelDownloadState.Failed,
                        latestProgress.BytesDownloaded,
                        latestProgress.TotalBytes,
                        latestProgress.Percent,
                        ex.Message).ConfigureAwait(false);
                    throw new InvalidOperationException($"Failed to enqueue download for model {tracked.Request.ModelId}: {ex.Message}", ex);
                }

                try
                {
                    latestProgress = await WaitForHandlesAsync(tracked, handles, tracked.Request.SizeBytes, effectiveCancellationToken).ConfigureAwait(false);
                    await WaitForDownloadAsync(transferPath, tracked.Request.SizeBytes, effectiveCancellationToken).ConfigureAwait(false);
                    if (stagingPath is not null)
                    {
                        _staging!.Publish(stagingPath, normalized);
                    }

                    await ReportProgressAsync(tracked, ModelDownloadState.Complete, tracked.Request.SizeBytes, tracked.Request.SizeBytes, 100, "complete").ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (effectiveCancellationToken.IsCancellationRequested)
                {
                    ScheduleTransferRemoval(normalized, handles);
                    throw;
                }
                catch (Exception ex) when (effectiveCancellationToken.IsCancellationRequested)
                {
                    ScheduleTransferRemoval(normalized, handles);
                    throw new OperationCanceledException(
                        "Model download cancellation interrupted transfer cleanup.",
                        ex,
                        effectiveCancellationToken);
                }
                catch (Aria2DownloadFailedException ex) when (IsRetryableAria2Error(ex.ErrorCode))
                {
                    latestProgress = ex.Progress;
                    await CancelTransfersUntilSessionCancellationAsync(
                            normalized,
                            handles,
                            logFailures: false,
                            effectiveCancellationToken)
                        .ConfigureAwait(false);

                    var message = $"Retrying in {retryDelay.TotalSeconds:0}s after aria2 error {ex.ErrorCode}: {ex.ErrorMessage}";
                    RunnerLog.Warning<DownloadCoordinator>($"Model download {tracked.Request.ModelId} failed transiently. {message}");
                    await ReportProgressAsync(
                        tracked,
                        ModelDownloadState.Retrying,
                        latestProgress.BytesDownloaded,
                        latestProgress.TotalBytes,
                        latestProgress.Percent,
                        message).ConfigureAwait(false);

                    await _delay.DelayAsync(retryDelay, effectiveCancellationToken).ConfigureAwait(false);
                    retryDelay = ComputeNextRetryDelay(retryDelay, _settings.ModelDownloadRetryMaxDelay);
                }
                catch (Exception ex)
                {
                    if (ex is Aria2DownloadFailedException downloadFailed)
                    {
                        latestProgress = downloadFailed.Progress;
                    }

                    await CancelTransfersUntilSessionCancellationAsync(
                            normalized,
                            handles,
                            logFailures: false,
                            effectiveCancellationToken)
                        .ConfigureAwait(false);
                    await ReportProgressAsync(
                        tracked,
                        ModelDownloadState.Failed,
                        latestProgress.BytesDownloaded,
                        latestProgress.TotalBytes,
                        latestProgress.Percent,
                        ex.Message).ConfigureAwait(false);

                    if (ex is Aria2DownloadFailedException)
                    {
                        throw new InvalidOperationException(ex.Message, ex);
                    }

                    throw;
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<DownloadProgressSnapshot> WaitForHandlesAsync(
        TrackedDownload tracked,
        IReadOnlyList<ModelTransferHandle> handles,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        if (handles == null || handles.Count == 0)
        {
            return DownloadProgressSnapshot.Empty(expectedSize);
        }

        var pending = new HashSet<ModelTransferHandle>(handles);
        if (pending.Count == 0)
        {
            return DownloadProgressSnapshot.Empty(expectedSize);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var lastLoggedPercent = -1d;
        var lastLogTime = now;
        var lastBytes = 0L;
        var lastTotal = expectedSize;
        long? baselineCompletedBytes = null;
        DateTime? baselineObservedAt = null;
        var completedBytesByHandle = new Dictionary<ModelTransferHandle, long>();
        var completedTotalByHandle = new Dictionary<ModelTransferHandle, long>();
        var lastObservedBytesByHandle = new Dictionary<ModelTransferHandle, long>();
        var lastObservedTotalByHandle = new Dictionary<ModelTransferHandle, long>();
        var latestProgress = DownloadProgressSnapshot.Empty(expectedSize);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var completed = new List<ModelTransferHandle>();
            var activeCompleted = 0L;
            var activeTotal = 0L;
            var aggregatedDownloadSpeed = 0L;
            var observedStatus = false;

            foreach (var handle in pending)
            {
                try
                {
                    var status = await _transferBackend.GetStatusAsync(handle, cancellationToken).ConfigureAwait(false);
                    var state = status.Status?.ToLowerInvariant();
                    observedStatus = true;
                    var completedLength = Math.Max(0, status.CompletedLength);
                    var totalLength = Math.Max(0, status.TotalLength);
                    lastObservedBytesByHandle[handle] = completedLength;
                    lastObservedTotalByHandle[handle] = totalLength;

                    if (state == "complete")
                    {
                        completedBytesByHandle[handle] = completedLength;
                        completedTotalByHandle[handle] = totalLength;
                        completed.Add(handle);
                    }
                    else if (state == "removed")
                    {
                        if (!string.IsNullOrWhiteSpace(status.ErrorCode) && status.ErrorCode != "0")
                        {
                            throw CreateDownloadFailedException(
                                handle,
                                status,
                                lastObservedBytesByHandle,
                                lastObservedTotalByHandle,
                                expectedSize,
                                "removed download");
                        }

                        completedBytesByHandle[handle] = completedLength;
                        completedTotalByHandle[handle] = totalLength;
                        completed.Add(handle);
                    }
                    else if (state == "error")
                    {
                        throw CreateDownloadFailedException(
                            handle,
                            status,
                            lastObservedBytesByHandle,
                            lastObservedTotalByHandle,
                            expectedSize,
                            "reported error for");
                    }
                    else
                    {
                        activeCompleted += completedLength;
                        activeTotal += totalLength;
                        aggregatedDownloadSpeed += Math.Max(0, status.DownloadSpeed);
                    }
                }
                catch (ModelTransferStatusUnavailableException ex) when (ShouldTreatUnavailableStatusAsComplete(ex))
                {
                    completedBytesByHandle[handle] = lastObservedBytesByHandle.GetValueOrDefault(handle, 0L);
                    completedTotalByHandle[handle] = lastObservedTotalByHandle.GetValueOrDefault(handle, 0L);
                    completed.Add(handle);
                }
            }

            foreach (var handle in completed)
            {
                pending.Remove(handle);
            }

            var aggregatedCompleted = completedBytesByHandle.Values.Sum() + activeCompleted;
            var aggregatedTotal = completedTotalByHandle.Values.Sum() + activeTotal;

            if (aggregatedTotal == 0 && expectedSize > 0)
            {
                aggregatedTotal = expectedSize;
            }

            var aggregatePercent = aggregatedTotal > 0
                ? Math.Clamp((double)aggregatedCompleted * 100d / aggregatedTotal, 0d, 100d)
                : 0d;
            latestProgress = new DownloadProgressSnapshot(aggregatedCompleted, aggregatedTotal, aggregatePercent);

            if (pending.Count == 0)
            {
                break;
            }

            if (aggregatedTotal > 0)
            {
                var percent = aggregatePercent;
                now = _timeProvider.GetUtcNow().UtcDateTime;
                if (observedStatus && baselineCompletedBytes is null)
                {
                    baselineCompletedBytes = aggregatedCompleted;
                    baselineObservedAt = now;
                }

                var averageBytesPerSecond = 0L;
                if (baselineCompletedBytes is { } baselineBytes && baselineObservedAt is { } baselineAt)
                {
                    var elapsedSeconds = Math.Max(0d, (now - baselineAt).TotalSeconds);
                    if (elapsedSeconds > 0)
                    {
                        averageBytesPerSecond = (long)Math.Max(0d, (aggregatedCompleted - baselineBytes) / elapsedSeconds);
                    }
                }

                if (Math.Abs(percent - lastLoggedPercent) >= MinProgressDeltaPercent || now - lastLogTime >= _progressInterval)
                {
                    lastLoggedPercent = percent;
                    lastLogTime = now;
                    lastBytes = aggregatedCompleted;
                    lastTotal = aggregatedTotal;

                    RunnerLog.Info<DownloadCoordinator>($"[aria2-progress] {percent:F1}% ({FormatBytes(aggregatedCompleted)}/{FormatBytes(aggregatedTotal)})");
                    await ReportProgressAsync(
                        tracked,
                        ModelDownloadState.InProgress,
                        aggregatedCompleted,
                        aggregatedTotal,
                        percent,
                        null,
                        aggregatedDownloadSpeed,
                        averageBytesPerSecond).ConfigureAwait(false);
                }
            }

            await _delay.DelayAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        if (lastLoggedPercent < 100 && lastTotal > 0)
        {
            await ReportProgressAsync(tracked, ModelDownloadState.InProgress, lastBytes, lastTotal, Math.Min(99.9, lastLoggedPercent < 0 ? 0 : lastLoggedPercent), null).ConfigureAwait(false);
        }

        return latestProgress;
    }

    private async Task WaitForDownloadAsync(string destination, long expectedSize, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow().UtcDateTime + _downloadTimeout;
        while (_timeProvider.GetUtcNow().UtcDateTime < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsFileSatisfied(destination, expectedSize))
            {
                return;
            }

            await _delay.DelayAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for download '{destination}'");
    }

    private static bool ShouldTreatUnavailableStatusAsComplete(ModelTransferStatusUnavailableException ex)
    {
        var message = ex.Message?.ToLowerInvariant() ?? string.Empty;
        return message.Contains("not found", StringComparison.Ordinal)
            || message.Contains("cannot find", StringComparison.Ordinal)
            || message.Contains("does not exist", StringComparison.Ordinal)
            || message.Contains("invalid gid", StringComparison.Ordinal)
            || message.Contains("gid", StringComparison.Ordinal) && message.Contains("not exist", StringComparison.Ordinal);
    }

    private static Aria2DownloadFailedException CreateDownloadFailedException(
        ModelTransferHandle handle,
        ModelTransferSnapshot status,
        IReadOnlyDictionary<ModelTransferHandle, long> observedBytesByHandle,
        IReadOnlyDictionary<ModelTransferHandle, long> observedTotalByHandle,
        long expectedSize,
        string action)
    {
        var bytesDownloaded = observedBytesByHandle.Values.Sum();
        var totalBytes = observedTotalByHandle.Values.Sum();
        if (totalBytes <= 0 && expectedSize > 0)
        {
            totalBytes = expectedSize;
        }

        var percent = totalBytes > 0
            ? Math.Clamp((double)bytesDownloaded * 100d / totalBytes, 0d, 100d)
            : 0d;
        var errorCode = int.TryParse(status.ErrorCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedErrorCode)
            ? parsedErrorCode
            : 1;
        var errorMessage = string.IsNullOrWhiteSpace(status.ErrorMessage) ? "unknown error" : status.ErrorMessage.Trim();

        return new Aria2DownloadFailedException(
            errorCode,
            errorMessage,
            new DownloadProgressSnapshot(bytesDownloaded, totalBytes, percent),
            $"aria2 {action} {handle}: {errorCode} {errorMessage}");
    }

    private static bool IsRetryableAria2Error(int errorCode)
        => errorCode is 2 or 5 or 6 or 7 or 19 or 29;

    private static TimeSpan ComputeNextRetryDelay(TimeSpan current, TimeSpan maximum)
    {
        if (current >= maximum)
        {
            return maximum;
        }

        var doubledTicks = current.Ticks > maximum.Ticks / 2
            ? maximum.Ticks
            : current.Ticks * 2;
        return TimeSpan.FromTicks(Math.Min(doubledTicks, maximum.Ticks));
    }

    private static string FormatBytes(long value)
    {
        if (value <= 0)
        {
            return "0B";
        }

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var size = (double)value;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##}{units[unit]}";
    }

    private async Task ReportProgressAsync(
        TrackedDownload tracked,
        ModelDownloadState state,
        long bytesDownloaded,
        long totalBytes,
        double percent,
        string? message,
        long bytesPerSecond = 0,
        long averageBytesPerSecond = 0)
    {
        var reporter = ProgressReporter;
        if (reporter == null)
        {
            return;
        }

        var sessionId = tracked.SessionId ?? GetActiveSessionId();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var progress = new ModelDownloadProgress(
            sessionId,
            tracked.Request.ModelId ?? string.Empty,
            tracked.Request.Filename ?? _fileSystem.GetFileName(tracked.DestinationPath) ?? tracked.DestinationPath,
            tracked.DestinationPath,
            bytesDownloaded,
            totalBytes,
            percent,
            bytesPerSecond,
            averageBytesPerSecond,
            state,
            message);

        try
        {
            await reporter(progress).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunnerLog.Error<DownloadCoordinator>(ex, $"Failed to report download progress for {tracked.Request.ModelId}: {ex.Message}");
        }
    }

    private bool IsFileSatisfied(string path, long expectedSize)
    {
        if (!_fileSystem.FileExists(path))
        {
            return false;
        }

        // aria2 leaves a sibling control file while a download is incomplete/resumable.
        // Treat that as authoritative evidence that the payload is not ready yet even if
        // the target file exists at full size (for example due to sparse/preallocated output).
        if (_fileSystem.FileExists($"{path}.aria2"))
        {
            return false;
        }

        if (expectedSize <= 0)
        {
            try
            {
                return _fileSystem.GetFileSize(path) > 0;
            }
            catch
            {
                return false;
            }
        }

        try
        {
            // A larger file is stale or wrong, not complete: the catalog size is the total size.
            return _fileSystem.GetFileSize(path) == expectedSize;
        }
        catch
        {
            return false;
        }
    }

    private CancellationToken GetSessionCancellationToken(string? sessionId)
    {
        var normalizedSessionId = sessionId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return CancellationToken.None;
        }

        var cts = _sessionCancellations.GetOrAdd(normalizedSessionId, static _ => new CancellationTokenSource());
        return cts.Token;
    }

    private async Task CancelTransfersAsync(
        IEnumerable<ModelTransferHandle> handles,
        bool logFailures = true,
        CancellationToken cancellationToken = default)
    {
        foreach (var handle in handles.Distinct())
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _transferBackend
                        .RemoveAsync(handle, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (ModelTransferStatusUnavailableException)
                {
                    // Already gone is the same confirmed terminal state cleanup needs.
                    break;
                }
                catch (Exception ex)
                {
                    if (logFailures)
                    {
                        RunnerLog.Error<DownloadCoordinator>(
                            ex,
                            $"Failed to cancel model transfer {handle}; retrying: {ex.Message}");
                    }

                    await _delay
                        .DelayAsync(_settings.ModelDownloadRetryInitialDelay, cancellationToken)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
            }
        }
    }

    private async Task CancelTransfersUntilSessionCancellationAsync(
        string destination,
        IReadOnlyCollection<ModelTransferHandle> handles,
        bool logFailures,
        CancellationToken cancellationToken)
    {
        try
        {
            await CancelTransfersAsync(handles, logFailures, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ScheduleTransferRemoval(destination, handles);
            throw;
        }
    }

    private void ScheduleTransferRemoval(
        string destination,
        IEnumerable<ModelTransferHandle> handles)
    {
        var snapshot = handles.Distinct().ToArray();
        if (snapshot.Length == 0)
        {
            return;
        }

        Task<bool> cleanup;
        lock (_pendingRemovalGate)
        {
            _pendingRemovals.TryGetValue(destination, out var previous);
            cleanup = RemoveTransfersAfterAsync(previous, snapshot, _removalShutdown.Token);
            _pendingRemovals[destination] = cleanup;
        }

        _ = cleanup.ContinueWith(
            completed =>
            {
                if (completed.Status != TaskStatus.RanToCompletion || !completed.Result)
                {
                    return;
                }

                lock (_pendingRemovalGate)
                {
                    if (_pendingRemovals.GetValueOrDefault(destination) == completed)
                    {
                        _pendingRemovals.Remove(destination);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<bool> RemoveTransfersAfterAsync(
        Task<bool>? previous,
        IReadOnlyCollection<ModelTransferHandle> handles,
        CancellationToken cancellationToken)
    {
        if (previous is not null && !await previous.ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await CancelTransfersAsync(handles, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<bool> WaitForPendingTransferRemovalsAsync(
        IEnumerable<string> destinations,
        CancellationToken cancellationToken = default)
    {
        Task<bool>[] pending;
        lock (_pendingRemovalGate)
        {
            pending = destinations
                .Select(_fileSystem.NormalizePath)
                .Where(static destination => destination is not null)
                .Select(destination => _pendingRemovals.GetValueOrDefault(destination!))
                .Where(static cleanup => cleanup is not null)
                .Select(static cleanup => cleanup!)
                .Distinct()
                .ToArray();
        }

        var results = await Task.WhenAll(pending)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return results.All(static confirmed => confirmed);
    }

    public void StopBackgroundTransferRemovals()
    {
        if (!_removalShutdown.IsCancellationRequested)
        {
            _removalShutdown.Cancel();
        }
    }

    private sealed class Aria2DownloadFailedException(
        int errorCode,
        string errorMessage,
        DownloadProgressSnapshot progress,
        string message) : InvalidOperationException(message)
    {
        public int ErrorCode { get; } = errorCode;
        public string ErrorMessage { get; } = errorMessage;
        public DownloadProgressSnapshot Progress { get; } = progress;
    }

    private readonly record struct DownloadProgressSnapshot(long BytesDownloaded, long TotalBytes, double Percent)
    {
        public static DownloadProgressSnapshot Empty(long expectedSize)
            => new(0, Math.Max(0, expectedSize), 0);
    }

    private sealed class TrackedDownload(
        string? sessionId,
        ModelDownloadRequest request,
        string destinationPath)
    {
        private readonly CancellationTokenSource _registrationCancellation = new();

        public string? SessionId { get; } = sessionId;
        public ModelDownloadRequest Request { get; } = request;
        public string DestinationPath { get; } = destinationPath;
        public CancellationToken RegistrationCancellation => _registrationCancellation.Token;

        public void CancelRegistration()
        {
            if (!_registrationCancellation.IsCancellationRequested)
            {
                _registrationCancellation.Cancel();
            }
        }
    }
}

public readonly record struct ModelDownloadProgress(
    string SessionId,
    string ModelId,
    string Filename,
    string DestinationPath,
    long BytesDownloaded,
    long TotalBytes,
    double Percent,
    long BytesPerSecond,
    long AverageBytesPerSecond,
    ModelDownloadState State,
    string? Message);

public sealed class SystemFileSystem : IFileSystem
{
    public string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    public string? GetDirectoryName(string path) => Path.GetDirectoryName(path);

    public string? GetFileName(string path) => Path.GetFileName(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool FileExists(string path) => File.Exists(path);

    public long GetFileSize(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return info.Length;
    }
}

public sealed class SystemAsyncDelay : IAsyncDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return Task.Delay(delay, cancellationToken);
    }
}
