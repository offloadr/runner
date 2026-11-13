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
    private readonly IModelTransferBackend _transferBackend;
    private readonly SessionModelDownloadStaging? _staging;
    private readonly bool _downloadsEnabled;

    /// <param name="downloadsEnabled">
    /// False when <c>RUNNER_MODEL_DOWNLOADS</c> is off: no remote model is registered, so nothing can start
    /// a transfer; each one is reported as failed, and a prompt that needs one is refused.
    /// </param>
    public ModelDownloadService(
        IModelTransferBackend transferBackend,
        Aria2Settings settings,
        ModelHydrationRuntimeCapabilities? capabilities = null,
        ModelDestinationPolicy? destinationPolicy = null,
        SessionModelDownloadStaging? staging = null,
        bool downloadsEnabled = true)
    {
        _downloadsEnabled = downloadsEnabled;
        _destinationPolicy = destinationPolicy;
        _transferBackend = transferBackend ?? throw new ArgumentNullException(nameof(transferBackend));
        _staging = staging;
        _fullDownloadCoordinator = new DownloadCoordinator(
            transferBackend,
            settings ?? throw new ArgumentNullException(nameof(settings)),
            staging: staging);
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

    /// <summary>
    /// Resolves a session's current runtime when downloads are registered, so progress
    /// stays labelled with the runtime it was started for.
    /// </summary>
    public Func<string, RuntimeIdentity?>? RuntimeResolver
    {
        get => _fullDownloadCoordinator.RuntimeResolver;
        set
        {
            _fullDownloadCoordinator.RuntimeResolver = value;
            if (_hydrationCoordinator is not null)
            {
                _hydrationCoordinator.RuntimeResolver = value;
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await DiscardStagedDownloadsAsync(cancellationToken).ConfigureAwait(false);
        if (_hydrationCoordinator is not null)
        {
            await _hydrationCoordinator.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Staged downloads belong to session homes, which an agent restart removes; drop the
    /// transfers aria2 restored for them and their partial files.
    /// </summary>
    private async Task DiscardStagedDownloadsAsync(CancellationToken cancellationToken)
    {
        if (_staging is null)
        {
            return;
        }

        var snapshots = await _transferBackend.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var snapshot in snapshots.Where(snapshot => snapshot.Files.Any(file => _staging.IsStagingPath(file.Path))))
        {
            try
            {
                await _transferBackend.RemoveAsync(snapshot.Handle, cancellationToken).ConfigureAwait(false);
            }
            catch (ModelTransferStatusUnavailableException)
            {
                // Already gone.
            }
        }

        _staging.DiscardAll();
    }

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
        if (!_downloadsEnabled)
        {
            ReportRefused(sessionId, snapshot);
            return;
        }

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
        if (!_downloadsEnabled)
        {
            ReportRefused(sessionId, snapshot);
            return;
        }

        ConfineDestinations(snapshot);
        var (managed, full) = PartitionDownloads(snapshot);
        _hydrationCoordinator?.RegisterDownloads(
            sessionId,
            managed,
            replaceExisting: true);
        _fullDownloadCoordinator.RegisterDownloads(sessionId, full, replaceExisting: true);
        SeedConventionalPlaceholders(sessionId, full, replaceExisting: true);
    }

    private string? CreateConventionalPlaceholder(string path)
    {
        if (_destinationPolicy is null || !OperatingSystem.IsLinux())
        {
            return ModelPlaceholderFiles.TryCreate(path, expectedLength: 0);
        }

        try
        {
            return _destinationPolicy.CreatePlaceholder(path);
        }
        catch (Exception ex)
        {
            RunnerLog.Error(nameof(ModelDownloadService), ex, $"Failed creating placeholder for model path '{path}': {ex.Message}");
            return null;
        }
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
                var created = CreateConventionalPlaceholder(path);
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
            if (_conventionalPathsBySession.Values.Any(paths => paths.Contains(path)))
            {
                continue;
            }

            // Transfers for the path were removed before this runs, so nothing writes the
            // staged file any more.
            _staging?.Discard(path);
            if (!_ownedConventionalPlaceholders.TryGetValue(path, out var ownedIdentity))
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

    /// <summary>
    /// Frees the paths that local models are about to be linked at from the agent's own downloads of
    /// them (see <see cref="DemandAwareModelHydrationCoordinator.ReleaseForLocalModelAsync"/>), and returns
    /// the requests to project now: all of them but the local ones whose download is in use, which keeps
    /// serving the editor until a later request finds it free.
    /// </summary>
    /// <param name="canLink">
    /// Whether the local model a request names can be linked now. A download is never given up for one
    /// that cannot: the local file may be gone from the folder since Offloadr chose it.
    /// </param>
    /// <param name="linkFreed">
    /// Links the local model of a request whose path was just freed, so the path is not left empty while
    /// the rest of the batch is released. The returned requests are projected again afterwards.
    /// </param>
    public async Task<IReadOnlyList<ModelDownloadRequest>> ReleaseForLocalModelsAsync(
        IEnumerable<ModelDownloadRequest> downloads,
        Func<ModelDownloadRequest, bool> canLink,
        CancellationToken cancellationToken,
        Action<ModelDownloadRequest>? linkFreed = null)
    {
        ArgumentNullException.ThrowIfNull(canLink);
        var requested = downloads.Where(static download => download is not null).ToArray();
        var inUse = new HashSet<string>(StringComparer.Ordinal);
        // A path depends only on a model's category and file name, so the download there may be another
        // model of another size. That one is never given up: it stays in the way, and the projector
        // refuses the local request, rather than deleting a model that was not asked to be replaced.
        var releasable = requested
            .Where(LocalModelProjector.IsLocalProjectionRequest)
            .Where(canLink)
            .Select(download => (Download: download, Path: NormalizeDestination(download.DestinationPath)))
            .Where(static entry => entry.Path is not null)
            .Where(entry => IsSameSize(entry.Download, ExpectedSizeOfDownloadAt(entry.Path!)))
            .DistinctBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        // All at once, so a held-up scheduler costs the command one start timeout, not one per model.
        // Once the command is cancelled no further download is given up, but those already freed are
        // still returned, so their local models get linked.
        var hydrationCoordinator = _hydrationCoordinator;
        var releases = await Task.WhenAll(releasable.Select(async entry =>
            {
                var release = cancellationToken.IsCancellationRequested
                    ? LocalModelRelease.InUse
                    : hydrationCoordinator?.IsRangeManaged(entry.Path!) == true
                        ? await hydrationCoordinator
                            .ReleaseForLocalModelAsync(entry.Path!, cancellationToken, () => canLink(entry.Download))
                            .ConfigureAwait(false)
                        : ReleaseFullDownloadForLocalModel(entry.Path!);
                if (release == LocalModelRelease.Released)
                {
                    LinkFreed(linkFreed, entry.Download);
                }

                return (entry.Download, Path: entry.Path!, Release: release);
            }))
            .ConfigureAwait(false);
        var keptSizes = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var (download, path, release) in releases)
        {
            // Only the copy the agent downloaded, still at its path, can stand in for the local model;
            // otherwise the request goes on to the projector.
            if (release != LocalModelRelease.InUse || !HasIntactCopyAt(path))
            {
                continue;
            }

            // The copy keeps serving only as the same model: one of another size stays in the way, and
            // the projector refuses the request as it did before, rather than the editor using other weights.
            var downloadedSize = ExpectedSizeOfDownloadAt(path);
            if (!IsSameSize(download, downloadedSize))
            {
                RunnerLog.Warning(
                    nameof(ModelDownloadService),
                    $"The downloaded copy at '{path}' is in use and is not the local model requested there.");
                continue;
            }

            RunnerLog.Info(
                nameof(ModelDownloadService),
                $"Keeping the downloaded copy at '{path}' while it is in use; the local model is linked there once it is free.");
            inUse.Add(path);
            keptSizes[path] = downloadedSize;
        }

        // Every request for a kept path is checked, not only the one that was released: another selection
        // of another size at the same path still reaches the projector.
        return inUse.Count == 0
            ? requested
            : requested
                .Where(download => !LocalModelProjector.IsLocalProjectionRequest(download) ||
                                   NormalizeDestination(download.DestinationPath) is not { } path ||
                                   !inUse.Contains(path) ||
                                   !IsSameSize(download, keptSizes[path]))
                .ToArray();
    }

    private static bool IsSameSize(ModelDownloadRequest download, long? downloadedSize)
        => download.SizeBytes <= 0 || downloadedSize is not { } size || size == download.SizeBytes;

    /// <summary>Whether the file at <paramref name="path"/> is still the one the agent downloaded there.</summary>
    private bool HasIntactCopyAt(string path)
        => _hydrationCoordinator?.IsRangeManaged(path) == true
            ? _hydrationCoordinator.HasIntactCopyAt(path)
            : File.Exists(path);

    /// <summary>The size the download at <paramref name="path"/> was asked for with, not how far it has got.</summary>
    private long? ExpectedSizeOfDownloadAt(string path)
        => _hydrationCoordinator?.ExpectedLengthAt(path);

    private static void LinkFreed(Action<ModelDownloadRequest>? linkFreed, ModelDownloadRequest download)
    {
        try
        {
            linkFreed?.Invoke(download);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Reported again when the whole request is projected.
            RunnerLog.Warning(nameof(ModelDownloadService), $"Could not link a local model at once ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Gives up a fully downloaded file to a local model, as the hydration coordinator does for its own:
    /// only in a model root no session can write (not a session home, whose downloads are staged), and
    /// only a regular file, which there only the agent's downloads are. One still downloading stays.
    /// </summary>
    private LocalModelRelease ReleaseFullDownloadForLocalModel(string path)
    {
        if (_destinationPolicy is null || _staging?.GetStagingPath(path) is not null)
        {
            return LocalModelRelease.NotManaged;
        }

        try
        {
            // Also refuses a path whose leaf is a link: the local model may be linked there already.
            _destinationPolicy.Confine(path);
        }
        catch (InvalidOperationException)
        {
            return LocalModelRelease.NotManaged;
        }

        lock (_conventionalPlaceholderGate)
        {
            if (_conventionalOperations.TryGetValue(path, out var operations) &&
                operations.Any(static operation => !operation.IsCompleted))
            {
                return LocalModelRelease.InUse;
            }

            var file = new FileInfo(path);
            if (!file.Exists || file.LinkTarget is not null)
            {
                return LocalModelRelease.NotManaged;
            }

            File.Delete(path);
            File.Delete($"{path}.aria2");
            _ownedConventionalPlaceholders.Remove(path);
            foreach (var sessionPaths in _conventionalPathsBySession.Values)
            {
                sessionPaths.Remove(path);
            }
        }

        RunnerLog.Info(
            nameof(ModelDownloadService),
            $"Removed the downloaded copy at '{path}': the same model is linked from the local models folder.");
        return LocalModelRelease.Released;
    }

    private void ReportRefused(string sessionId, IReadOnlyCollection<ModelDownloadRequest> downloads)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        if (downloads.Count == 0 || normalizedSessionId is null)
        {
            return;
        }

        RunnerLog.Info(
            nameof(ModelDownloadService),
            $"Not downloading {downloads.Count} model(s) for session '{normalizedSessionId}': RUNNER_MODEL_DOWNLOADS is off.");
        if (ProgressReporter is not { } reporter)
        {
            return;
        }

        var runtime = RuntimeResolver?.Invoke(normalizedSessionId);
        var reports = downloads.Select(download => new ModelDownloadProgress(
                normalizedSessionId,
                download.ModelId ?? string.Empty,
                download.Filename ?? Path.GetFileName(download.DestinationPath) ?? string.Empty,
                download.DestinationPath ?? string.Empty,
                BytesDownloaded: 0,
                TotalBytes: download.SizeBytes,
                Percent: 0,
                BytesPerSecond: 0,
                AverageBytesPerSecond: 0,
                ModelDownloadState.Failed,
                ModelDownloadsDisabledException.ReportMessage,
                runtime))
            .ToArray();
        _ = ReportRefusedAsync(reporter, reports);
    }

    private static async Task ReportRefusedAsync(Func<ModelDownloadProgress, Task> reporter, IEnumerable<ModelDownloadProgress> reports)
    {
        foreach (var report in reports)
        {
            try
            {
                await reporter(report).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RunnerLog.Warning(nameof(ModelDownloadService), $"Could not report a refused download ({ex.GetType().Name}).");
            }
        }
    }

    private static ModelDownloadRequest[] FilterRemoteDownloadRequests(IEnumerable<ModelDownloadRequest>? downloads)
        => downloads?
            .Where(static download => download is not null && !LocalModelProjector.IsLocalProjectionRequest(download))
            .ToArray()
           ?? [];

    /// <summary>
    /// Prepares a prompt's model dependencies before its native invocation and
    /// returns their hydration completion. Conventional downloads complete first
    /// because nothing gates their reads. Range-managed models are demanded in
    /// prompt order but not awaited: the VFS shim gates their reads and mappings,
    /// so a large first download cannot hold the native response past the editor's
    /// own request timeout. The demand lasts until its completion settles or
    /// <paramref name="demandCancellation"/> withdraws it.
    /// </summary>
    public async Task<Task> PreparePromptDownloadsAsync(
        IEnumerable<ModelDownloadRequest> downloads,
        CancellationToken demandCancellation,
        bool highPriority)
    {
        var snapshot = FilterRemoteDownloadRequests(downloads);
        if (!_downloadsEnabled && snapshot.Length > 0)
        {
            // Registration already reported each one; nothing was registered, so the editor would only fail later.
            throw new ModelDownloadsDisabledException(snapshot.Select(static download => download.Filename).ToArray());
        }

        ConfineDestinations(snapshot);
        var (managed, full) = PartitionDownloads(snapshot);
        Task conventional;
        lock (_conventionalPlaceholderGate)
        {
            conventional = EnsureConventionalDownloadsAsync(full, demandCancellation, highPriority);
            TrackConventionalOperation(full, conventional);
        }

        var demand = _hydrationCoordinator?.RequestPromptDownloadsAsync(
            _fullDownloadCoordinator.GetActiveSessionId(),
            managed,
            demandCancellation) ?? Task.FromResult(Task.CompletedTask);
        try
        {
            await Task.WhenAll(conventional, demand).ConfigureAwait(false);
        }
        catch
        {
            // The caller never receives this completion; observe a later fault.
            if (demand.IsCompletedSuccessfully)
            {
                _ = demand.Result.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            throw;
        }

        return demand.Result;
    }

    /// <summary>Fails the session's pending range-managed reads; see the coordinator.</summary>
    public void InterruptSessionReads(string? sessionId) => _hydrationCoordinator?.InterruptSessionReads(sessionId);

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
