using Offloadr.Runner.V1;
using System.Collections.Concurrent;
using System.Linq;

namespace Offloadr.Runner.Linux;

internal sealed class ModelDownloadService : IAsyncDisposable
{
    private readonly DownloadCoordinator _fullDownloadCoordinator;
    private readonly DemandAwareModelHydrationCoordinator? _hydrationCoordinator;
    private readonly ConcurrentDictionary<string, DownloadOwnership> _destinationOwnership = new(StringComparer.Ordinal);
    private readonly object _conventionalPlaceholderGate = new();
    private readonly Dictionary<string, HashSet<string>> _conventionalPathsBySession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinuxFileIdentity> _ownedConventionalPlaceholders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<Task>> _conventionalOperations = new(StringComparer.Ordinal);
    private readonly ModelDestinationPolicy? _destinationPolicy;

    public ModelDownloadService(
        IModelTransferBackend transferBackend,
        Aria2Settings settings,
        ModelHydrationRuntimeCapabilities? capabilities = null,
        ModelDestinationPolicy? destinationPolicy = null)
    {
        _destinationPolicy = destinationPolicy;
        _fullDownloadCoordinator = new DownloadCoordinator(
            transferBackend ?? throw new ArgumentNullException(nameof(transferBackend)),
            settings ?? throw new ArgumentNullException(nameof(settings)));
        if (capabilities?.SupportsPersistentRangeHydration == true)
        {
            _hydrationCoordinator = new DemandAwareModelHydrationCoordinator(
                transferBackend,
                capabilities,
                settings.StateDirectory,
                settings.ShutdownTimeout);
        }
    }

    public Func<ModelDownloadProgress, Task>? ProgressReporter
    {
        get => _fullDownloadCoordinator.ProgressReporter;
        set
        {
            _fullDownloadCoordinator.ProgressReporter = value;
            if (_hydrationCoordinator is not null)
            {
                _hydrationCoordinator.ProgressReporter = value;
            }
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
        => _hydrationCoordinator?.InitializeAsync(cancellationToken) ?? Task.CompletedTask;

    public void SetActiveSession(string? sessionId)
    {
        _hydrationCoordinator?.ActivateSession(sessionId);
        _fullDownloadCoordinator.SetActiveSession(sessionId);
    }

    public void CancelSession(string? sessionId)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        _hydrationCoordinator?.CancelSession(normalizedSessionId);
        _fullDownloadCoordinator.CancelSession(normalizedSessionId);
        RemoveConventionalSession(normalizedSessionId);
        if (string.Equals(_fullDownloadCoordinator.GetActiveSessionId(), normalizedSessionId, StringComparison.Ordinal))
        {
            _fullDownloadCoordinator.SetActiveSession(null);
        }
    }

    public void RegisterDownloads(string sessionId, IEnumerable<ModelDownloadRequest> downloads)
    {
        var snapshot = FilterRemoteDownloadRequests(downloads);
        ConfineDestinations(snapshot);
        var (managed, full) = PartitionDownloads(snapshot);
        _hydrationCoordinator?.RegisterDownloads(
            sessionId,
            managed,
            replaceExisting: false);
        _fullDownloadCoordinator.RegisterDownloads(sessionId, full);
        SeedConventionalPlaceholders(sessionId, full, replaceExisting: false);
    }

    public void SeedDownloads(string sessionId, IEnumerable<ModelDownloadRequest> downloads)
    {
        var snapshot = FilterRemoteDownloadRequests(downloads);
        ConfineDestinations(snapshot);
        var (managed, full) = PartitionDownloads(snapshot);
        _hydrationCoordinator?.RegisterDownloads(
            sessionId,
            managed,
            replaceExisting: true);
        _fullDownloadCoordinator.RegisterDownloads(sessionId, full, replaceExisting: true);
        SeedConventionalPlaceholders(sessionId, full, replaceExisting: true);
    }

    private void SeedConventionalPlaceholders(
        string sessionId,
        IEnumerable<ModelDownloadRequest> downloads,
        bool replaceExisting)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        if (normalizedSessionId is null)
        {
            return;
        }

        string[] omitted;
        lock (_conventionalPlaceholderGate)
        {
            var paths = downloads
                .Select(static download => NormalizeDestination(download.DestinationPath))
                .Where(static path => path is not null)
                .Select(static path => path!)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var path in paths)
            {
                var created = ModelPlaceholderFiles.TryCreate(path, expectedLength: 0);
                if (created is not null && LinuxFileIdentityReader.TryRead(created, out var identity))
                {
                    _ownedConventionalPlaceholders[created] = identity;
                }
            }

            if (!_conventionalPathsBySession.TryGetValue(normalizedSessionId, out var registered))
            {
                registered = new HashSet<string>(StringComparer.Ordinal);
                _conventionalPathsBySession[normalizedSessionId] = registered;
            }

            omitted = [];
            if (replaceExisting)
            {
                omitted = registered.Except(paths, StringComparer.Ordinal).ToArray();
                registered.Clear();
            }

            registered.UnionWith(paths);
        }

        ScheduleOwnedConventionalPlaceholderCleanup(omitted);
    }

    private void RemoveConventionalSession(string? normalizedSessionId)
    {
        if (normalizedSessionId is null)
        {
            return;
        }

        string[] candidates;
        lock (_conventionalPlaceholderGate)
        {
            if (!_conventionalPathsBySession.Remove(normalizedSessionId, out var paths))
            {
                return;
            }

            candidates = paths.ToArray();
        }

        ScheduleOwnedConventionalPlaceholderCleanup(candidates);
    }

    private void ScheduleOwnedConventionalPlaceholderCleanup(IReadOnlyCollection<string> candidates)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        _ = CleanupOwnedConventionalPlaceholdersAsync(candidates);
    }

    private async Task CleanupOwnedConventionalPlaceholdersAsync(IReadOnlyCollection<string> candidates)
    {
        await WaitForConventionalOperationsAsync(candidates).ConfigureAwait(false);
        if (!await _fullDownloadCoordinator
                .WaitForPendingTransferRemovalsAsync(candidates)
                .ConfigureAwait(false))
        {
            return;
        }

        lock (_conventionalPlaceholderGate)
        {
            CleanupOwnedConventionalPlaceholders(candidates);
        }
    }

    private async Task WaitForConventionalOperationsAsync(IEnumerable<string> candidates)
    {
        Task[] pending;
        lock (_conventionalPlaceholderGate)
        {
            pending = candidates
                .SelectMany(path => _conventionalOperations.GetValueOrDefault(path) ?? [])
                .Distinct()
                .ToArray();
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch
        {
            // The initiating caller observes download failures. Background
            // transfer-removal fences are awaited separately before cleanup.
        }
    }

    private void CleanupOwnedConventionalPlaceholders(IEnumerable<string> candidates)
    {
        foreach (var path in candidates)
        {
            if (_conventionalPathsBySession.Values.Any(paths => paths.Contains(path)) ||
                !_ownedConventionalPlaceholders.TryGetValue(path, out var ownedIdentity))
            {
                continue;
            }

            if (!LinuxFileIdentityReader.TryRead(path, out var currentIdentity))
            {
                try
                {
                    File.Delete($"{path}.aria2");
                    _ownedConventionalPlaceholders.Remove(path);
                }
                catch (Exception ex)
                {
                    RunnerLog.Error(
                        nameof(ModelDownloadService),
                        ex,
                        $"Failed removing conventional model resume state '{path}.aria2': {ex.Message}");
                }

                continue;
            }

            if (currentIdentity.DeviceId != ownedIdentity.DeviceId ||
                currentIdentity.Inode != ownedIdentity.Inode)
            {
                _ownedConventionalPlaceholders.Remove(path);
                continue;
            }

            try
            {
                File.Delete(path);
                File.Delete($"{path}.aria2");
                _ownedConventionalPlaceholders.Remove(path);
            }
            catch (Exception ex)
            {
                RunnerLog.Error(
                    nameof(ModelDownloadService),
                    ex,
                    $"Failed removing untouched conventional model placeholder '{path}': {ex.Message}");
            }
        }
    }

    private static string? NormalizeDestination(string? path)
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
            return null;
        }
    }

    /// <summary>
    /// Refuses the whole batch when any destination escapes the configured model
    /// roots, before placeholders are created or transfers registered.
    /// </summary>
    private void ConfineDestinations(IEnumerable<ModelDownloadRequest> downloads)
    {
        if (_destinationPolicy is null)
        {
            return;
        }

        foreach (var download in downloads)
        {
            _destinationPolicy.Confine(download.DestinationPath);
        }
    }

    private static ModelDownloadRequest[] FilterRemoteDownloadRequests(IEnumerable<ModelDownloadRequest>? downloads)
        => downloads?
            .Where(static download => download is not null && !LocalModelProjector.IsLocalProjectionRequest(download))
            .ToArray()
           ?? [];

    public Task EnsureDownloadsAsync(IEnumerable<ModelDownloadRequest> downloads, CancellationToken cancellationToken, bool highPriority)
    {
        var snapshot = FilterRemoteDownloadRequests(downloads);
        ConfineDestinations(snapshot);
        var (managed, full) = PartitionDownloads(snapshot);
        Task conventional;
        lock (_conventionalPlaceholderGate)
        {
            conventional = EnsureConventionalDownloadsAsync(full, cancellationToken, highPriority);
            TrackConventionalOperation(full, conventional);
        }

        return Task.WhenAll(
            conventional,
            _hydrationCoordinator?.EnsurePromptDownloadsAsync(
                _fullDownloadCoordinator.GetActiveSessionId(),
                managed,
                cancellationToken) ?? Task.CompletedTask);
    }

    public Task EnsureDownloadedAsync(string destinationPath, CancellationToken cancellationToken, bool highPriority)
    {
        _destinationPolicy?.Confine(destinationPath);
        lock (_conventionalPlaceholderGate)
        {
            var operation = EnsureConventionalDownloadAsync(destinationPath, cancellationToken, highPriority);
            TrackConventionalOperation([destinationPath], operation);
            return operation;
        }
    }

    public Task<bool> TryEnsureRegisteredDownloadAsync(string destinationPath, CancellationToken cancellationToken, bool highPriority)
    {
        lock (_conventionalPlaceholderGate)
        {
            var operation = TryEnsureConventionalDownloadAsync(destinationPath, cancellationToken, highPriority);
            TrackConventionalOperation([destinationPath], operation);
            return operation;
        }
    }

    private async Task EnsureConventionalDownloadsAsync(
        IReadOnlyCollection<ModelDownloadRequest> downloads,
        CancellationToken cancellationToken,
        bool highPriority)
    {
        await Task.WhenAll(downloads.Select(download =>
                EnsureConventionalDownloadAsync(download, cancellationToken, highPriority)))
            .ConfigureAwait(false);
    }

    private async Task EnsureConventionalDownloadAsync(
        ModelDownloadRequest download,
        CancellationToken cancellationToken,
        bool highPriority)
    {
        await _fullDownloadCoordinator
            .EnsureDownloadsAsync([download], cancellationToken, highPriority)
            .ConfigureAwait(false);
        MarkConventionalDownloadComplete(download.DestinationPath);
    }

    private async Task EnsureConventionalDownloadAsync(
        string destinationPath,
        CancellationToken cancellationToken,
        bool highPriority)
    {
        await _fullDownloadCoordinator
            .EnsureDownloadedAsync(destinationPath, cancellationToken, highPriority)
            .ConfigureAwait(false);
        MarkConventionalDownloadComplete(destinationPath);
    }

    private async Task<bool> TryEnsureConventionalDownloadAsync(
        string destinationPath,
        CancellationToken cancellationToken,
        bool highPriority)
    {
        var handled = await _fullDownloadCoordinator
            .TryEnsureRegisteredDownloadedAsync(destinationPath, cancellationToken, highPriority)
            .ConfigureAwait(false);
        if (handled)
        {
            MarkConventionalDownloadComplete(destinationPath);
        }

        return handled;
    }

    private void MarkConventionalDownloadComplete(string? destinationPath)
    {
        var normalized = NormalizeDestination(destinationPath);
        if (normalized is null)
        {
            return;
        }

        lock (_conventionalPlaceholderGate)
        {
            _ownedConventionalPlaceholders.Remove(normalized);
        }
    }

    private void TrackConventionalOperation(
        IEnumerable<ModelDownloadRequest> downloads,
        Task operation)
        => TrackConventionalOperation(downloads.Select(static download => download.DestinationPath), operation);

    private void TrackConventionalOperation(IEnumerable<string?> destinationPaths, Task operation)
    {
        var paths = destinationPaths
            .Select(NormalizeDestination)
            .Where(static path => path is not null)
            .Select(static path => path!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        lock (_conventionalPlaceholderGate)
        {
            foreach (var path in paths)
            {
                if (!_conventionalOperations.TryGetValue(path, out var operations))
                {
                    operations = [];
                    _conventionalOperations[path] = operations;
                }

                operations.Add(operation);
            }
        }

        _ = operation.ContinueWith(
            _ =>
            {
                lock (_conventionalPlaceholderGate)
                {
                    foreach (var path in paths)
                    {
                        if (_conventionalOperations.TryGetValue(path, out var operations))
                        {
                            operations.Remove(operation);
                            if (operations.Count == 0)
                            {
                                _conventionalOperations.Remove(path);
                            }
                        }
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public string? GetActiveSessionId()
    {
        return _fullDownloadCoordinator.GetActiveSessionId();
    }

    public Task<ModelHydrationOpenResult> OpenRangeManagedAsync(
        string destinationPath,
        string? sessionId,
        CancellationToken cancellationToken)
        => _hydrationCoordinator?.OpenAsync(destinationPath, sessionId, cancellationToken)
           ?? Task.FromResult(ModelHydrationOpenResult.Unmanaged);

    public Task<ModelHydrationEnsureResult> EnsureRangeAsync(
        ulong leaseId,
        long observedEpoch,
        long offset,
        long length,
        CancellationToken cancellationToken,
        string? requiredSessionId = null)
        => _hydrationCoordinator?.EnsureRangeAsync(
               leaseId,
               observedEpoch,
               offset,
               length,
               cancellationToken,
               requiredSessionId)
           ?? Task.FromException<ModelHydrationEnsureResult>(
               new ModelHydrationIOException("Range-managed hydration is disabled for this runtime."));

    public Task<ModelHydrationEnsureResult> EnsureCompleteAsync(
        ulong leaseId,
        long observedEpoch,
        string reason,
        CancellationToken cancellationToken,
        string? requiredSessionId = null)
        => _hydrationCoordinator?.EnsureCompleteAsync(
               leaseId,
               observedEpoch,
               reason,
               cancellationToken,
               requiredSessionId)
           ?? Task.FromException<ModelHydrationEnsureResult>(
               new ModelHydrationIOException("Range-managed hydration is disabled for this runtime."));

    public Task ReleaseAsync(ulong leaseId, string? requiredSessionId = null)
        => _hydrationCoordinator?.ReleaseAsync(leaseId, requiredSessionId) ?? Task.CompletedTask;

    private bool IsRangeManaged(string? path)
        => _hydrationCoordinator?.IsRangeManaged(path) == true;

    private bool CanRangeManage(ModelDownloadRequest download)
        => download.SizeBytes > 0
           && !string.IsNullOrWhiteSpace(download.ModelId)
           && IsRangeManaged(download.DestinationPath);

    private (ModelDownloadRequest[] Managed, ModelDownloadRequest[] Full) PartitionDownloads(
        IEnumerable<ModelDownloadRequest> downloads)
    {
        var managed = new List<ModelDownloadRequest>();
        var full = new List<ModelDownloadRequest>();
        foreach (var download in downloads)
        {
            var destination = string.IsNullOrWhiteSpace(download.DestinationPath)
                ? null
                : Path.GetFullPath(download.DestinationPath.Trim());
            if (destination is null)
            {
                full.Add(download);
                continue;
            }

            var canRangeManage = CanRangeManage(download);
            var ownership = _destinationOwnership.GetOrAdd(
                destination,
                canRangeManage ? DownloadOwnership.RangeManaged : DownloadOwnership.Conventional);
            if (ownership == DownloadOwnership.RangeManaged)
            {
                if (!canRangeManage)
                {
                    throw new InvalidOperationException(
                        $"Range-managed model destination '{destination}' was registered without stable model identity and size metadata.");
                }

                managed.Add(download);
                continue;
            }

            full.Add(download);
        }

        return (managed.ToArray(), full.ToArray());
    }

    private enum DownloadOwnership
    {
        Conventional,
        RangeManaged
    }

    public async ValueTask DisposeAsync()
    {
        _fullDownloadCoordinator.StopBackgroundTransferRemovals();
        if (_hydrationCoordinator is not null)
        {
            await _hydrationCoordinator.DisposeAsync().ConfigureAwait(false);
        }
    }
}
