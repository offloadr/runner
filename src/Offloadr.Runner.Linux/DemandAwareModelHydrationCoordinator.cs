using Offloadr.Runner.V1;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Offloadr.Runner.Linux;

public enum ModelHydrationOpenDisposition
{
    Unmanaged,
    FullReady,
    RangeManaged
}

public readonly record struct ModelHydrationOpenResult(
    ModelHydrationOpenDisposition Disposition,
    ulong LeaseId,
    long TransferEpoch,
    long ExpectedLength,
    ulong DeviceId = 0,
    ulong Inode = 0)
{
    public static ModelHydrationOpenResult Unmanaged => new(ModelHydrationOpenDisposition.Unmanaged, 0, 0, 0);
    public static ModelHydrationOpenResult FullReady => new(ModelHydrationOpenDisposition.FullReady, 0, 0, 0);
}

public readonly record struct ModelHydrationEnsureResult(long TransferEpoch);

public sealed class ModelHydrationIOException : IOException
{
    public ModelHydrationIOException(
        string message,
        Exception? innerException = null,
        long transferEpoch = 0)
        : base(message, innerException)
    {
        TransferEpoch = transferEpoch;
    }

    public long TransferEpoch { get; }
}

public sealed class DemandAwareModelHydrationCoordinator : IAsyncDisposable
{
    private const long MaximumSafetensorsHeaderLength = 8L * 1024 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultShutdownCleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly IModelTransferBackend _backend;
    private readonly ModelHydrationRuntimeCapabilities _capabilities;
    private readonly ModelTransferIndex _index;
    private readonly object _sync = new();
    private readonly Dictionary<string, TransferState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, DescriptorLease> _descriptorLeases = [];
    private readonly ConcurrentQueue<ModelTransferIndexRecord> _orphanedRecords = new();
    private readonly ConcurrentQueue<ModelTransferIndexRecord> _conflictingRecords = new();
    private readonly List<LegacyTransferCandidate> _legacyCandidates = [];
    private readonly ConcurrentQueue<LegacyTransferCandidate> _legacyCleanup = new();
    private readonly HashSet<ModelTransferHandle> _legacyCleanupQueued = [];
    private readonly ConcurrentQueue<ModelTransferHandle> _restoredPauseRetries = new();
    private readonly HashSet<ModelTransferHandle> _preemptedExternalHandles = [];
    private readonly HashSet<string> _canceledSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sessionGenerations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _registrationCleanupGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _schedulerTask;
    private readonly TimeSpan _shutdownCleanupTimeout;

    private TransferState? _activeState;
    private long _nextIntentSequence;

    public DemandAwareModelHydrationCoordinator(
        IModelTransferBackend backend,
        ModelHydrationRuntimeCapabilities capabilities,
        string stateDirectory,
        TimeSpan? shutdownCleanupTimeout = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _index = new ModelTransferIndex(stateDirectory);
        _shutdownCleanupTimeout = shutdownCleanupTimeout ?? DefaultShutdownCleanupTimeout;
        if (_shutdownCleanupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shutdownCleanupTimeout),
                "Shutdown cleanup timeout must be positive.");
        }

        _schedulerTask = Task.Run(() => SchedulerLoopAsync(_shutdown.Token));
    }

    public Func<ModelDownloadProgress, Task>? ProgressReporter { get; set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!_capabilities.SupportsPersistentRangeHydration)
        {
            return;
        }

        var indexedByHandle = _index.Snapshot()
            .GroupBy(static record => record.Handle, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var snapshots = await _backend.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var snapshot in snapshots)
        {
            var managedPath = snapshot.Files
                .Select(static file => file.Path)
                .FirstOrDefault(_capabilities.OwnsPersistentModelPath);
            if (managedPath is null)
            {
                continue;
            }

            try
            {
                if (!snapshot.IsStopped)
                {
                    await _backend.ForcePauseAsync(snapshot.Handle, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _restoredPauseRetries.Enqueue(snapshot.Handle);
                RunnerLog.Warning<DemandAwareModelHydrationCoordinator>(
                    $"Could not pause restored model transfer {snapshot.Handle}: {ex.Message}");
            }

            if (!indexedByHandle.TryGetValue(snapshot.Handle.Value, out var records) || records.Length != 1)
            {
                RunnerLog.Warning<DemandAwareModelHydrationCoordinator>(
                    $"Quarantining unverified legacy model transfer {snapshot.Handle} path='{managedPath}'.");
                lock (_sync)
                {
                    _legacyCandidates.Add(CaptureLegacyTransfer(snapshot));
                }

                continue;
            }

            var record = records[0];
            var matchingFiles = snapshot.Files
                .Where(file => string.Equals(
                    NormalizePath(file.Path),
                    NormalizePath(record.DestinationPath),
                    StringComparison.Ordinal))
                .ToArray();
            if (matchingFiles.Length != 1 ||
                matchingFiles[0].Length != 0 && matchingFiles[0].Length != record.ExpectedLength ||
                snapshot.TotalLength != 0 && snapshot.TotalLength != record.ExpectedLength)
            {
                LinuxFileIdentity? ownedTargetIdentity = null;
                LinuxFileIdentity? ownedControlIdentity = null;
                if (record.PlaceholderOwned &&
                    LinuxFileIdentityReader.TryRead(record.DestinationPath, out var targetIdentity) &&
                    targetIdentity.DeviceId == record.DeviceId &&
                    targetIdentity.Inode == record.Inode &&
                    targetIdentity.Length == record.ExpectedLength)
                {
                    ownedTargetIdentity = targetIdentity;
                    var controlPath = $"{record.DestinationPath}.aria2";
                    if (LinuxFileIdentityReader.TryRead(controlPath, out var controlIdentity))
                    {
                        ownedControlIdentity = controlIdentity;
                    }
                }

                RunnerLog.Warning<DemandAwareModelHydrationCoordinator>(
                    $"Quarantining identity-mismatched model transfer {snapshot.Handle} path='{managedPath}'.");
                await _backend.RemoveAsync(snapshot.Handle, cancellationToken).ConfigureAwait(false);
                DeleteIfIdentityMatches(record.DestinationPath, ownedTargetIdentity);
                DeleteIfIdentityMatches($"{record.DestinationPath}.aria2", ownedControlIdentity);
                await _index
                    .QuarantineAsync(record, "snapshot-identity-mismatch", cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await _backend.SaveSessionAsync(cancellationToken).ConfigureAwait(false);
        if (!_restoredPauseRetries.IsEmpty)
        {
            SignalScheduler();
        }
    }

    public void RegisterDownloads(
        string sessionId,
        IEnumerable<ModelDownloadRequest> downloads,
        bool replaceExisting)
    {
        _registrationCleanupGate.Wait();
        try
        {
            RegisterDownloadsCore(sessionId, downloads, replaceExisting);
        }
        finally
        {
            _registrationCleanupGate.Release();
        }
    }

    private void RegisterDownloadsCore(
        string sessionId,
        IEnumerable<ModelDownloadRequest> downloads,
        bool replaceExisting)
    {
        var registrations = downloads
            .Where(static download => download is not null)
            .Where(download => IsRangeManaged(download.DestinationPath))
            .Select(download => CreateRegistration(sessionId, download))
            .ToArray();
        var claimedDigests = registrations
            .Select(static registration => registration.Identity.Digest)
            .ToHashSet(StringComparer.Ordinal);
        var indexedRecords = _index.Snapshot();
        LegacyTransferCandidate[] claimedLegacyCandidates;
        lock (_sync)
        {
            claimedLegacyCandidates = _legacyCandidates
                .Where(candidate => candidate.Snapshot.Files.Any(file =>
                    registrations.Any(registration => string.Equals(
                        NormalizePath(file.Path),
                        registration.Identity.DestinationPath,
                        StringComparison.Ordinal))))
                .ToArray();
            foreach (var candidate in claimedLegacyCandidates)
            {
                QueueLegacyCleanupLocked(candidate);
            }
        }

        if (claimedLegacyCandidates.Length > 0)
        {
            SignalScheduler();
            throw new InvalidOperationException(
                $"Unverified legacy transfer {claimedLegacyCandidates[0].Snapshot.Handle} conflicts with the authoritative model registration snapshot.");
        }

        foreach (var registration in registrations)
        {
            var conflicts = indexedRecords
                .Where(record =>
                    !string.Equals(record.IdentityDigest, registration.Identity.Digest, StringComparison.Ordinal) &&
                    (string.Equals(
                         NormalizePath(record.DestinationPath),
                         registration.Identity.DestinationPath,
                         StringComparison.Ordinal) ||
                     string.Equals(
                         record.Handle,
                         registration.Identity.DeterministicIdentifier,
                         StringComparison.Ordinal)))
                .ToArray();
            if (conflicts.Length == 0)
            {
                continue;
            }

            foreach (var conflict in conflicts)
            {
                lock (_sync)
                {
                    if (_states.TryGetValue(
                        NormalizePath(conflict.DestinationPath),
                        out var conflictingState) &&
                        string.Equals(
                            conflictingState.Identity.Digest,
                            conflict.IdentityDigest,
                            StringComparison.Ordinal))
                    {
                        // Claim the state while admission is serialized by
                        // _sync. Existing leases may drain, but no later Open
                        // can reserve the destination before quarantine.
                        conflictingState.PendingConflictQuarantine = true;
                    }
                }

                _conflictingRecords.Enqueue(conflict);
            }

            SignalScheduler();
            throw new InvalidOperationException(
                $"Persistent transfer identity conflict for destination '{registration.Identity.DestinationPath}'.");
        }

        lock (_sync)
        {
            foreach (var registration in registrations)
            {
                if (_states.TryGetValue(registration.Identity.DestinationPath, out var existing) &&
                    !string.Equals(existing.Identity.Digest, registration.Identity.Digest, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Destination '{registration.Identity.DestinationPath}' is already registered to model '{existing.Identity.ModelId}'.");
                }

                if (existing is null)
                {
                    var placeholder = EnsureRegisteredPlaceholder(registration.Identity);
                    existing = new TransferState(
                        registration.SessionId,
                        registration.Request,
                        registration.Identity,
                        placeholder.Owned)
                    {
                        DeviceId = placeholder.DeviceId,
                        Inode = placeholder.Inode,
                        Handle = placeholder.PreviousHandle,
                        TransferEpoch = placeholder.TransferEpoch,
                        RestartRequired = placeholder.RestartRequired
                    };
                    _states.Add(registration.Identity.DestinationPath, existing);
                }
                else
                {
                    existing.SessionId = registration.SessionId;
                    existing.PendingOrphanCleanup = false;
                    existing.OrphanCleanupQueued = false;
                    existing.PendingConflictQuarantine = false;
                    var previousSourceRevision = GetSourceRevision(existing.Request);
                    existing.Request = registration.Request;
                    existing.UriRefreshRequired =
                        !string.Equals(
                            previousSourceRevision,
                            GetSourceRevision(registration.Request),
                            StringComparison.Ordinal);
                    if (existing.UriRefreshRequired)
                    {
                        existing.UriRefreshFailureLogged = false;
                    }
                }
            }

            if (replaceExisting)
            {
                var retainedDigests = new HashSet<string>(StringComparer.Ordinal);
                var queuedOrphanDigests = _states.Values
                    .Where(static state => state.OrphanCleanupQueued)
                    .Select(static state => state.Identity.Digest)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var legacyCandidate in _legacyCandidates)
                {
                    QueueLegacyCleanupLocked(legacyCandidate);
                }

                foreach (var state in _states.Values
                             .Where(state => !claimedDigests.Contains(state.Identity.Digest))
                             .ToArray())
                {
                    state.PendingOrphanCleanup = true;
                    if (state.DescriptorLeaseCount > 0 || state.OpenReservationCount > 0)
                    {
                        retainedDigests.Add(state.Identity.Digest);
                        continue;
                    }

                    if (QueueOrphanCleanupLocked(state))
                    {
                        queuedOrphanDigests.Add(state.Identity.Digest);
                    }
                }

                foreach (var record in _index.Snapshot().Where(record =>
                             !claimedDigests.Contains(record.IdentityDigest) &&
                             !retainedDigests.Contains(record.IdentityDigest) &&
                             !queuedOrphanDigests.Contains(record.IdentityDigest)))
                {
                    _orphanedRecords.Enqueue(record);
                }
            }
        }

        SignalScheduler();
    }

    public bool IsRangeManaged(string? path) => _capabilities.OwnsPersistentModelPath(path);

    public async Task<ModelHydrationOpenResult> OpenAsync(
        string path,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizePath(path);
        var requestedSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        TransferState? state;
        string? leaseSessionId = null;
        long leaseSessionGeneration = 0;
        lock (_sync)
        {
            _states.TryGetValue(normalized, out state);
            if (state is null)
            {
                var canonical = LinuxPathCanonicalizer.ResolveExistingPath(normalized);
                _states.TryGetValue(canonical, out state);
            }

            if (state?.PendingOrphanCleanup == true ||
                state?.PendingConflictQuarantine == true)
            {
                throw new ModelHydrationIOException(
                    $"Managed model '{normalized}' is pending destructive cleanup.",
                    transferEpoch: state.TransferEpoch);
            }

            if (state is not null)
            {
                leaseSessionId = requestedSessionId ?? state.SessionId;
                leaseSessionGeneration = CaptureSessionGenerationLocked(leaseSessionId, state);
                state.OpenReservationCount++;
            }
        }

        if (state is null)
        {
            return ModelHydrationOpenResult.Unmanaged;
        }

        var reservationActive = true;
        try
        {
            await EnsureTransferAttachedAsync(state, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                state.OpenReservationCount = Math.Max(0, state.OpenReservationCount - 1);
                reservationActive = false;
                if (state.PendingConflictQuarantine)
                {
                    throw new ModelHydrationIOException(
                        $"Managed model '{normalized}' entered identity-conflict quarantine while Open was in progress.",
                        transferEpoch: state.TransferEpoch);
                }

                ThrowIfSessionGenerationChangedLocked(
                    leaseSessionId!,
                    leaseSessionGeneration,
                    state);
                VerifyCurrentFileIdentity(state);

                var leaseId = CreateLeaseIdLocked();

                var lease = new DescriptorLease(
                    leaseId,
                    leaseSessionId!,
                    state);
                _descriptorLeases.Add(leaseId, lease);
                state.DescriptorLeaseCount++;
                return new ModelHydrationOpenResult(
                    ModelHydrationOpenDisposition.RangeManaged,
                    leaseId,
                    state.TransferEpoch,
                    state.Identity.ExpectedLength,
                    state.DeviceId,
                    state.Inode);
            }
        }
        catch
        {
            lock (_sync)
            {
                if (reservationActive)
                {
                    state.OpenReservationCount = Math.Max(0, state.OpenReservationCount - 1);
                }

                QueueOrphanCleanupLocked(state);
            }

            SignalScheduler();
            throw;
        }
    }

    public Task<ModelHydrationEnsureResult> EnsureRangeAsync(
        ulong leaseId,
        long observedEpoch,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || length < 0)
        {
            return Task.FromException<ModelHydrationEnsureResult>(
                new ModelHydrationIOException("Model range offset and length must be non-negative."));
        }

        DescriptorLease lease;
        lock (_sync)
        {
            if (!_descriptorLeases.TryGetValue(leaseId, out lease!))
            {
                return Task.FromException<ModelHydrationEnsureResult>(
                    new ModelHydrationIOException($"Descriptor lease '{leaseId}' is not active."));
            }

            if (observedEpoch > lease.State.TransferEpoch)
            {
                return Task.FromException<ModelHydrationEnsureResult>(
                    new ModelHydrationIOException(
                        $"Descriptor lease '{leaseId}' observed future transfer epoch {observedEpoch}; current epoch is {lease.State.TransferEpoch}.",
                        transferEpoch: lease.State.TransferEpoch));
            }

            VerifyCurrentFileIdentity(lease.State);
            if (length == 0 || offset >= lease.State.Identity.ExpectedLength || lease.State.Complete)
            {
                return Task.FromResult(new ModelHydrationEnsureResult(lease.State.TransferEpoch));
            }

            if (!IsSafetensors(lease.State.Identity.DestinationPath))
            {
                PromoteSticky(lease.State, "first_non_empty_read");
            }

            var waiter = new RangeWaiter(
                Interlocked.Increment(ref _nextIntentSequence),
                leaseId,
                offset,
                length,
                requiresComplete: false,
                observedEpoch);
            lease.State.RangeWaiters.Add(waiter);
            RegisterCancellation(lease.State, waiter, cancellationToken);
            SignalScheduler();
            return waiter.Completion.Task;
        }
    }

    private ulong CreateLeaseIdLocked()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        while (true)
        {
            RandomNumberGenerator.Fill(bytes);
            var leaseId = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            if (leaseId != 0 && !_descriptorLeases.ContainsKey(leaseId))
            {
                return leaseId;
            }
        }
    }

    public Task<ModelHydrationEnsureResult> EnsureCompleteAsync(
        ulong leaseId,
        long observedEpoch,
        string reason,
        CancellationToken cancellationToken)
    {
        DescriptorLease lease;
        lock (_sync)
        {
            if (!_descriptorLeases.TryGetValue(leaseId, out lease!))
            {
                return Task.FromException<ModelHydrationEnsureResult>(
                    new ModelHydrationIOException($"Descriptor lease '{leaseId}' is not active."));
            }

            if (observedEpoch > lease.State.TransferEpoch)
            {
                return Task.FromException<ModelHydrationEnsureResult>(
                    new ModelHydrationIOException(
                        $"Descriptor lease '{leaseId}' observed future transfer epoch {observedEpoch}; current epoch is {lease.State.TransferEpoch}.",
                        transferEpoch: lease.State.TransferEpoch));
            }

            VerifyCurrentFileIdentity(lease.State);
            if (lease.State.Complete)
            {
                return Task.FromResult(new ModelHydrationEnsureResult(lease.State.TransferEpoch));
            }

            PromoteSticky(lease.State, string.IsNullOrWhiteSpace(reason) ? "mapping" : reason);
            var waiter = new RangeWaiter(
                Interlocked.Increment(ref _nextIntentSequence),
                leaseId,
                0,
                lease.State.Identity.ExpectedLength,
                requiresComplete: true,
                observedEpoch);
            lease.State.RangeWaiters.Add(waiter);
            RegisterCancellation(lease.State, waiter, cancellationToken);
            SignalScheduler();
            return waiter.Completion.Task;
        }
    }

    public Task ReleaseAsync(ulong leaseId)
    {
        HydrationWaiter[] canceledWaiters;
        lock (_sync)
        {
            if (!_descriptorLeases.Remove(leaseId, out var lease))
            {
                return Task.CompletedTask;
            }

            lease.State.DescriptorLeaseCount = Math.Max(0, lease.State.DescriptorLeaseCount - 1);
            canceledWaiters = RemoveLeaseWaitersLocked(lease.State, leaseId);
            if (lease.State.DescriptorLeaseCount == 0 && lease.State.PromptWaiters.Count == 0)
            {
                lease.State.StickyPromotion = false;
            }

            QueueOrphanCleanupLocked(lease.State);
        }

        CancelWaiters(canceledWaiters);
        SignalScheduler();
        return Task.CompletedTask;
    }

    public async Task EnsurePromptDownloadsAsync(
        string? sessionId,
        IEnumerable<ModelDownloadRequest> downloads,
        CancellationToken cancellationToken)
    {
        var dependencies = new List<(TransferState State, int Ordinal)>();
        var ordinal = 0;
        foreach (var download in downloads.Where(static download => download is not null))
        {
            var normalized = NormalizePath(download.DestinationPath);
            TransferState? state;
            lock (_sync)
            {
                _states.TryGetValue(normalized, out state);
            }

            if (state is null)
            {
                continue;
            }

            dependencies.Add((state, ordinal++));
        }

        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        var observedSessionGenerations = new long[dependencies.Count];
        lock (_sync)
        {
            for (var index = 0; index < dependencies.Count; index++)
            {
                var dependency = dependencies[index];
                var waiterSessionId = normalizedSessionId ?? dependency.State.SessionId;
                observedSessionGenerations[index] =
                    CaptureSessionGenerationLocked(waiterSessionId, dependency.State);
            }
        }

        await Task.WhenAll(
                dependencies.Select(dependency =>
                    EnsureTransferAttachedAsync(dependency.State, cancellationToken)))
            .ConfigureAwait(false);

        var completionTasks = new List<Task<ModelHydrationEnsureResult>>(dependencies.Count);
        lock (_sync)
        {
            for (var index = 0; index < dependencies.Count; index++)
            {
                var dependency = dependencies[index];
                var waiterSessionId = normalizedSessionId ?? dependency.State.SessionId;
                ThrowIfSessionGenerationChangedLocked(
                    waiterSessionId,
                    observedSessionGenerations[index],
                    dependency.State);
            }

            foreach (var dependency in dependencies)
            {
                var waiterSessionId = normalizedSessionId ?? dependency.State.SessionId;
                if (dependency.State.Complete)
                {
                    completionTasks.Add(Task.FromResult(
                        new ModelHydrationEnsureResult(dependency.State.TransferEpoch)));
                    continue;
                }

                var waiter = new PromptWaiter(
                    Interlocked.Increment(ref _nextIntentSequence),
                    waiterSessionId,
                    dependency.Ordinal);
                dependency.State.PromptWaiters.Add(waiter);
                RegisterCancellation(dependency.State, waiter, cancellationToken);
                completionTasks.Add(waiter.Completion.Task);
            }
        }

        // Publish the complete ordered dependency set before allowing the scheduler
        // to choose a runnable prompt transfer.
        SignalScheduler();
        await Task.WhenAll(completionTasks).ConfigureAwait(false);
    }

    public void CancelSession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var normalized = sessionId.Trim();
        List<HydrationWaiter> canceledWaiters = [];
        lock (_sync)
        {
            _sessionGenerations[normalized] =
                _sessionGenerations.GetValueOrDefault(normalized) + 1;
            _canceledSessions.Add(normalized);
            foreach (var leaseId in _descriptorLeases
                         .Where(pair => string.Equals(pair.Value.SessionId, normalized, StringComparison.Ordinal))
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                if (_descriptorLeases.Remove(leaseId, out var lease))
                {
                    lease.State.DescriptorLeaseCount = Math.Max(0, lease.State.DescriptorLeaseCount - 1);
                    canceledWaiters.AddRange(RemoveLeaseWaitersLocked(lease.State, leaseId));
                }
            }

            foreach (var state in _states.Values)
            {
                foreach (var waiter in state.PromptWaiters
                             .Where(waiter => string.Equals(waiter.SessionId, normalized, StringComparison.Ordinal))
                             .ToArray())
                {
                    state.PromptWaiters.Remove(waiter);
                    canceledWaiters.Add(waiter);
                }

                if (state.DescriptorLeaseCount == 0 && state.PromptWaiters.Count == 0)
                {
                    state.StickyPromotion = false;
                }

                QueueOrphanCleanupLocked(state);
            }
        }

        CancelWaiters(canceledWaiters);
        SignalScheduler();
    }

    public void ActivateSession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_sync)
        {
            _canceledSessions.Remove(sessionId.Trim());
        }
    }

    private RegisteredPlaceholder EnsureRegisteredPlaceholder(ModelTransferIdentity identity)
    {
        var destination = identity.DestinationPath;
        var stagingPath = $"{destination}.{identity.DeterministicIdentifier}.offloadr-placeholder";
        ModelTransferIndexRecord? indexed = null;
        if (_index.TryGet(identity.Digest, out var existingRecord))
        {
            if (!string.Equals(existingRecord.ModelId, identity.ModelId, StringComparison.Ordinal) ||
                !string.Equals(
                    NormalizePath(existingRecord.DestinationPath),
                    identity.DestinationPath,
                    StringComparison.Ordinal) ||
                existingRecord.ExpectedLength != identity.ExpectedLength ||
                !string.Equals(
                    existingRecord.Handle,
                    identity.DeterministicIdentifier,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Persistent transfer index identity does not match registered model '{identity.ModelId}'.");
            }

            indexed = existingRecord;
        }

        if (File.Exists(destination))
        {
            if (!LinuxFileIdentityReader.TryRead(destination, out var current) ||
                current.Length != identity.ExpectedLength)
            {
                throw new InvalidOperationException(
                    $"Registered model target '{destination}' does not match its expected length.");
            }

            if (indexed is not null &&
                indexed.PlaceholderOwned &&
                indexed.Inode != 0 &&
                !indexed.RestartRequired &&
                (indexed.DeviceId != current.DeviceId || indexed.Inode != current.Inode))
            {
                throw new InvalidOperationException(
                    $"Registered model target '{destination}' replaced its indexed placeholder inode.");
            }

            if (indexed is not null &&
                indexed.PlaceholderOwned &&
                indexed.Inode == 0)
            {
                indexed = indexed with
                {
                    DeviceId = current.DeviceId,
                    Inode = current.Inode
                };
                _index.Upsert(indexed);
            }

            return new RegisteredPlaceholder(
                indexed?.PlaceholderOwned == true,
                current.DeviceId,
                current.Inode,
                indexed?.RestartRequired == true,
                indexed?.RestartRequired == true
                    ? new ModelTransferHandle(indexed.Handle)
                    : null,
                indexed?.TransferEpoch ?? 1);
        }

        var restartRequired = false;
        if (indexed is not null)
        {
            if (!indexed.PlaceholderOwned)
            {
                throw new InvalidOperationException(
                    $"Indexed model target '{destination}' is missing and cannot be recreated during registration.");
            }

            restartRequired = indexed.Complete || indexed.PieceLength > 0 || indexed.NumPieces > 0;
            if (restartRequired && File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
            else if (File.Exists(stagingPath))
            {
                if (!LinuxFileIdentityReader.TryRead(stagingPath, out var staged) ||
                    staged.Length != identity.ExpectedLength ||
                    indexed.Inode != 0 &&
                    (indexed.DeviceId != staged.DeviceId || indexed.Inode != staged.Inode))
                {
                    throw new InvalidOperationException(
                        $"Staged model placeholder '{stagingPath}' does not match its transfer index.");
                }

                File.Move(stagingPath, destination, overwrite: false);
                return new RegisteredPlaceholder(
                    true,
                    staged.DeviceId,
                    staged.Inode,
                    indexed.RestartRequired,
                    indexed.RestartRequired
                        ? new ModelTransferHandle(indexed.Handle)
                        : null,
                    TransferEpoch: indexed.TransferEpoch);
            }
        }
        else if (File.Exists(stagingPath))
        {
            // A crash may leave the runner-owned staging name before its index
            // record is committed. No authoritative record can reference it.
            File.Delete(stagingPath);
        }

        return CreateAndPublishPlaceholder(
            identity,
            stagingPath,
            indexed?.TransferEpoch ?? 1,
            restartRequired,
            restartRequired,
            restartRequired && indexed is not null
                ? new ModelTransferHandle(indexed.Handle)
                : null,
            overwriteDestination: false);
    }

    private RegisteredPlaceholder CreateAndPublishPlaceholder(
        ModelTransferIdentity identity,
        string stagingPath,
        long transferEpoch,
        bool publishRestartRequired,
        bool finalRestartRequired,
        ModelTransferHandle? previousHandle,
        bool overwriteDestination)
    {
        var directory = Path.GetDirectoryName(identity.DestinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (var stream = new FileStream(
                   stagingPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            stream.SetLength(identity.ExpectedLength);
            stream.Flush(flushToDisk: true);
        }

        if (!LinuxFileIdentityReader.TryRead(stagingPath, out var created))
        {
            throw new InvalidOperationException(
                $"Could not identify staged model placeholder '{stagingPath}'.");
        }

        var placeholderRecord = new ModelTransferIndexRecord
        {
            IdentityDigest = identity.Digest,
            ModelId = identity.ModelId,
            DestinationPath = identity.DestinationPath,
            ExpectedLength = identity.ExpectedLength,
            Handle = identity.DeterministicIdentifier,
            TransferEpoch = transferEpoch,
            DeviceId = created.DeviceId,
            Inode = created.Inode,
            PlaceholderOwned = true,
            Complete = false,
            RestartRequired = publishRestartRequired
        };
        _index.Upsert(placeholderRecord);
        File.Move(stagingPath, identity.DestinationPath, overwriteDestination);
        if (publishRestartRequired != finalRestartRequired)
        {
            _index.Upsert(placeholderRecord with { RestartRequired = finalRestartRequired });
        }

        return new RegisteredPlaceholder(
            true,
            created.DeviceId,
            created.Inode,
            finalRestartRequired,
            previousHandle,
            transferEpoch);
    }

    private async Task EnsureTransferAttachedAsync(
        TransferState state,
        CancellationToken cancellationToken,
        bool deferResetWhileOpenReserved = false)
    {
        await state.AttachGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (deferResetWhileOpenReserved)
            {
                lock (_sync)
                {
                    if (state.OpenReservationCount > 0)
                    {
                        return;
                    }
                }
            }

            var resumeMode = ModelTransferResumeMode.InitializeEmpty;
            if (state.RestartRequired)
            {
                await ResetFailedTransferAsync(state, cancellationToken).ConfigureAwait(false);
            }

            if (state.Complete || state.Handle is not null)
            {
                try
                {
                    VerifyCurrentFileIdentity(state);
                    return;
                }
                catch (ModelHydrationIOException) when (state.DescriptorLeaseCount == 0)
                {
                    lock (_sync)
                    {
                        state.RestartRequired = true;
                    }

                    await PersistStateAsync(state, cancellationToken).ConfigureAwait(false);
                    await ResetFailedTransferAsync(state, cancellationToken).ConfigureAwait(false);
                }
            }

            if (_index.TryGet(state.Identity.Digest, out var indexed))
            {
                ValidateIndexedIdentity(state, indexed);
                state.TransferEpoch = indexed.TransferEpoch;
                state.PieceLength = indexed.PieceLength;
                state.NumPieces = indexed.NumPieces;
                state.DeviceId = indexed.DeviceId;
                state.Inode = indexed.Inode;
                state.PlaceholderOwned = indexed.PlaceholderOwned;
                VerifyCurrentFileIdentity(state);
                var indexedHandle = new ModelTransferHandle(indexed.Handle);
                try
                {
                    var snapshot = await _backend.GetStatusAsync(indexedHandle, cancellationToken).ConfigureAwait(false);
                    ValidateSnapshotIdentity(state, snapshot);
                    state.Handle = indexedHandle;
                    state.Complete = indexed.Complete || snapshot.IsComplete;
                    if (snapshot.IsComplete && !indexed.Complete)
                    {
                        await PersistStateAsync(state, cancellationToken).ConfigureAwait(false);
                    }

                    await RefreshUriAsync(state, snapshot, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (ModelTransferStatusUnavailableException)
                {
                    if (indexed.Complete)
                    {
                        // aria2 may evict a completed result between runner processes. The
                        // indexed inode and length were verified above, so the completed file
                        // remains authoritative even when its old handle is no longer listed.
                        state.Complete = true;
                        return;
                    }

                    if (File.Exists($"{state.Identity.DestinationPath}.aria2"))
                    {
                        resumeMode = ModelTransferResumeMode.RequireExisting;
                    }
                    else if (indexed.PieceLength > 0 || indexed.NumPieces > 0)
                    {
                        if (state.DescriptorLeaseCount > 0)
                        {
                            throw new ModelHydrationIOException(
                                $"Transfer '{state.Identity.ModelId}' cannot restart without resumable control state while managed descriptors remain open.",
                                transferEpoch: state.TransferEpoch);
                        }

                        state.TransferEpoch++;
                        state.PieceLength = 0;
                        state.NumPieces = 0;
                        state.LoadedPieces.Clear();
                    }
                }
            }

            if (!state.PlaceholderOwned)
            {
                throw new ModelHydrationIOException(
                    $"Managed model target '{state.Identity.DestinationPath}' is not a runner-owned placeholder and has no verified transfer state.");
            }

            var expectedHandle = new ModelTransferHandle(state.Identity.DeterministicIdentifier);
            state.Handle = expectedHandle;
            CaptureFileIdentity(state);
            try
            {
                await PersistStateAsync(state, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                state.Handle = null;
            }

            var handles = await _backend
                .CreateAsync(
                    new ModelTransferCreateRequest(
                        state.Identity.DestinationPath,
                        state.Identity.ExpectedLength,
                        GetSourceUris(state.Request),
                        GetMetalinkBytes(state.Request),
                        state.Identity.DeterministicIdentifier,
                        StartPaused: true,
                        QueuePosition: null,
                        ResumeMode: resumeMode),
                    cancellationToken)
                .ConfigureAwait(false);
            if (handles.Count != 1)
            {
                foreach (var handle in handles)
                {
                    await _backend.RemoveAsync(handle, cancellationToken).ConfigureAwait(false);
                }

                throw new ModelHydrationIOException(
                    $"Range-managed model '{state.Identity.ModelId}' created {handles.Count} transfers; exactly one is required.");
            }

            if (handles[0] != expectedHandle)
            {
                await _backend.RemoveAsync(handles[0], cancellationToken).ConfigureAwait(false);
                throw new ModelHydrationIOException(
                    $"Range-managed model '{state.Identity.ModelId}' did not use its deterministic transfer handle.");
            }

            state.Handle = handles[0];
            CaptureFileIdentity(state);
            await PersistStateAsync(state, cancellationToken).ConfigureAwait(false);
            await _backend.SaveSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ModelHydrationIOException)
        {
            throw new ModelHydrationIOException(
                $"Failed attaching model transfer '{state.Identity.ModelId}': {ex.Message}",
                ex);
        }
        finally
        {
            state.AttachGate.Release();
        }
    }

    private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TransferState? operationState = null;
            try
            {
                await RetryRestoredPausesAsync(cancellationToken).ConfigureAwait(false);
                await QuarantineConflictsAsync(cancellationToken).ConfigureAwait(false);
                await CleanupLegacyTransfersAsync(cancellationToken).ConfigureAwait(false);
                await CleanupOrphansAsync(cancellationToken).ConfigureAwait(false);
                var candidate = SelectCandidate();
                if (candidate is null)
                {
                    if (!await PauseActiveAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await _wake.WaitAsync(PollInterval, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await ResumePreemptedExternalTransfersAsync(cancellationToken).ConfigureAwait(false);
                    if (!_conflictingRecords.IsEmpty ||
                        !_orphanedRecords.IsEmpty ||
                        !_legacyCleanup.IsEmpty ||
                        !_restoredPauseRetries.IsEmpty ||
                        HasPreemptedExternalTransfers())
                    {
                        await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                operationState = candidate;
                if (!await TryRefreshPendingUriAsync(candidate, cancellationToken).ConfigureAwait(false))
                {
                    await _wake.WaitAsync(PollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!await EnsureActiveAsync(candidate, cancellationToken).ConfigureAwait(false))
                {
                    await _wake.WaitAsync(PollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (candidate.Handle is not { } handle)
                {
                    FailState(candidate, new ModelHydrationIOException("Managed transfer has no active handle."));
                    continue;
                }

                var snapshot = await _backend.GetStatusAsync(handle, cancellationToken).ConfigureAwait(false);
                if (string.Equals(snapshot.Status, "paused", StringComparison.OrdinalIgnoreCase))
                {
                    await ReactivatePausedCandidateAsync(candidate, handle, cancellationToken).ConfigureAwait(false);
                }

                await ProcessSnapshotAsync(candidate, snapshot, cancellationToken).ConfigureAwait(false);

                await _wake.WaitAsync(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not ModelHydrationIOException and not ModelTransferStatusUnavailableException)
            {
                // An operational RPC failure does not prove that aria2 changed
                // piece geometry, replaced the target, or invalidated readable
                // bytes. Preserve the transfer and its waiters, then retry the
                // same scheduler intent on the next central poll.
                RunnerLog.Warning<DemandAwareModelHydrationCoordinator>(
                    $"Demand-aware model hydration scheduler will retry after a transient failure: {ex.Message}");
                try
                {
                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                RunnerLog.Error<DemandAwareModelHydrationCoordinator>(
                    ex,
                    $"Demand-aware model hydration scheduler failed: {ex.Message}");
                if (operationState?.Handle is { } failedHandle)
                {
                    try
                    {
                        await _backend.ForcePauseAsync(failedHandle, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception pauseException)
                    {
                        RunnerLog.Warning<DemandAwareModelHydrationCoordinator>(
                            $"Could not pause failed transfer {failedHandle}: {pauseException.Message}");
                    }
                }

                if (operationState is not null)
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_activeState, operationState))
                        {
                            _activeState = null;
                        }

                        operationState.RestartRequired = true;
                    }

                    FailState(operationState, new ModelHydrationIOException(ex.Message, ex));
                }

                try
                {
                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private TransferState? SelectCandidate()
    {
        lock (_sync)
        {
            return _states.Values
                .Where(static state => state.Handle is not null && !state.Complete)
                .Select(state => new
                {
                    State = state,
                    Priority = state.RangeWaiters.Count > 0
                        ? 0
                        : state.PromptWaiters.Count > 0
                            ? 1
                            : state.StickyPromotion
                                ? 2
                                : 3,
                    Ordinal = state.PromptWaiters.Count == 0
                        ? int.MaxValue
                        : state.PromptWaiters.Min(static waiter => waiter.Ordinal),
                    Sequence = state.RangeWaiters
                        .Cast<HydrationWaiter>()
                        .Concat(state.PromptWaiters)
                        .Select(static waiter => waiter.Sequence)
                        .DefaultIfEmpty(state.StickySequence)
                        .Min()
                })
                .Where(static candidate => candidate.Priority < 3)
                .OrderBy(static candidate => candidate.Priority)
                .ThenBy(static candidate => candidate.Ordinal)
                .ThenBy(static candidate => candidate.Sequence)
                .Select(static candidate => candidate.State)
                .FirstOrDefault();
        }
    }

    private async Task<bool> EnsureActiveAsync(
        TransferState candidate,
        CancellationToken cancellationToken)
    {
        TransferState? previous;
        bool alreadyActive;
        lock (_sync)
        {
            previous = _activeState;
            alreadyActive = ReferenceEquals(previous, candidate);
        }

        if (alreadyActive)
        {
            await PreemptExternalTransfersAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (previous?.Handle is { } previousHandle && !previous.Complete)
        {
            try
            {
                await _backend.ForcePauseAsync(previousHandle, cancellationToken).ConfigureAwait(false);
            }
            catch (ModelTransferStatusUnavailableException)
            {
                // An already-absent transfer cannot occupy aria2's active slot.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RunnerLog.Warning<DemandAwareModelHydrationCoordinator>(
                    $"Could not pause preempted transfer {previousHandle}: {ex.Message}");
                return false;
            }
        }

        lock (_sync)
        {
            if (ReferenceEquals(_activeState, previous))
            {
                _activeState = null;
            }
        }

        if (candidate.Handle is not { } handle)
        {
            return true;
        }

        await PreemptExternalTransfersAsync(cancellationToken).ConfigureAwait(false);
        await _backend.MoveToFrontAsync(handle, cancellationToken).ConfigureAwait(false);
        await _backend.UnpauseAsync(handle, cancellationToken).ConfigureAwait(false);
        await _backend.SaveSessionAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _activeState = candidate;
        }

        RunnerLog.Event<DemandAwareModelHydrationCoordinator>(
            "model_hydration_promoted",
            "Model hydration promoted model_id={ModelId} reason={Reason}",
            candidate.Identity.ModelId,
            GetPromotionReason(candidate));
        return true;
    }

    private async Task PreemptExternalTransfersAsync(CancellationToken cancellationToken)
    {
        HashSet<ModelTransferHandle> managedHandles;
        lock (_sync)
        {
            managedHandles = _states.Values
                .Where(static state => state.Handle is not null)
                .Select(static state => state.Handle!.Value)
                .ToHashSet();
        }

        var changed = false;
        var snapshots = await _backend.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var snapshot in snapshots.Where(static snapshot => string.Equals(
                     snapshot.Status,
                     "active",
                     StringComparison.OrdinalIgnoreCase)))
        {
            if (managedHandles.Contains(snapshot.Handle))
            {
                continue;
            }

            lock (_sync)
            {
                if (_preemptedExternalHandles.Contains(snapshot.Handle))
                {
                    continue;
                }
            }

            try
            {
                await _backend.ForcePauseAsync(snapshot.Handle, cancellationToken).ConfigureAwait(false);
            }
            catch (ModelTransferStatusUnavailableException)
            {
                // The external transfer disappeared after ListAsync observed it.
                // It cannot occupy aria2's active slot or require a later resume.
                continue;
            }

            lock (_sync)
            {
                _preemptedExternalHandles.Add(snapshot.Handle);
            }

            changed = true;
        }

        if (changed)
        {
            await _backend.SaveSessionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ResumePreemptedExternalTransfersAsync(CancellationToken cancellationToken)
    {
        ModelTransferHandle[] handles;
        lock (_sync)
        {
            handles = _preemptedExternalHandles.ToArray();
        }

        var changed = false;
        foreach (var handle in handles)
        {
            try
            {
                await _backend.UnpauseAsync(handle, cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    _preemptedExternalHandles.Remove(handle);
                }

                changed = true;
            }
            catch (ModelTransferStatusUnavailableException)
            {
                lock (_sync)
                {
                    _preemptedExternalHandles.Remove(handle);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    var availableHandles = await _backend
                        .ListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (availableHandles.All(snapshot => snapshot.Handle != handle))
                    {
                        lock (_sync)
                        {
                            _preemptedExternalHandles.Remove(handle);
                        }

                        changed = true;
                        continue;
                    }
                }
                catch (Exception reconciliationException)
                    when (reconciliationException is not OperationCanceledException)
                {
                    RunnerLog.Warning<DemandAwareModelHydrationCoordinator>(
                        $"Could not reconcile preempted conventional transfer {handle}: {reconciliationException.Message}");
                }

                RunnerLog.Warning<DemandAwareModelHydrationCoordinator>(
                    $"Could not resume preempted conventional transfer {handle}: {ex.Message}");
            }
        }

        if (changed)
        {
            await _backend.SaveSessionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool HasPreemptedExternalTransfers()
    {
        lock (_sync)
        {
            return _preemptedExternalHandles.Count > 0;
        }
    }

    private async Task<bool> PauseActiveAsync(CancellationToken cancellationToken)
    {
        TransferState? active;
        lock (_sync)
        {
            active = _activeState;
        }

        if (active?.Handle is not { } handle || active.Complete)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeState, active))
                {
                    _activeState = null;
                }
            }

            return true;
        }

        try
        {
            await _backend.ForcePauseAsync(handle, cancellationToken).ConfigureAwait(false);
            await _backend.SaveSessionAsync(cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                if (ReferenceEquals(_activeState, active))
                {
                    _activeState = null;
                }
            }

            return true;
        }
        catch (ModelTransferStatusUnavailableException)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeState, active))
                {
                    _activeState = null;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RunnerLog.Warning<DemandAwareModelHydrationCoordinator>(
                $"Could not pause idle model transfer {handle}: {ex.Message}");
            return false;
        }
    }

    private async Task ReactivatePausedCandidateAsync(
        TransferState candidate,
        ModelTransferHandle handle,
        CancellationToken cancellationToken)
    {
        await _backend.MoveToFrontAsync(handle, cancellationToken).ConfigureAwait(false);
        await _backend.UnpauseAsync(handle, cancellationToken).ConfigureAwait(false);
        await _backend.SaveSessionAsync(cancellationToken).ConfigureAwait(false);
        RunnerLog.Event<DemandAwareModelHydrationCoordinator>(
            "model_hydration_reactivated",
            "Model hydration reactivated model_id={ModelId} reason={Reason}",
            candidate.Identity.ModelId,
            GetPromotionReason(candidate));
    }

    private async Task ProcessSnapshotAsync(
        TransferState state,
        ModelTransferSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ValidateSnapshotIdentity(state, snapshot);
        if (string.Equals(snapshot.Status, "error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(snapshot.Status, "removed", StringComparison.OrdinalIgnoreCase))
        {
            throw new ModelHydrationIOException(
                $"aria2 transfer {snapshot.Handle} failed: {snapshot.ErrorCode} {snapshot.ErrorMessage}");
        }

        var persistState = UpdateGeometryAndIdentity(
            state,
            snapshot,
            out var restartRequired,
            out var processingDeferred);
        if (processingDeferred)
        {
            return;
        }

        if (restartRequired)
        {
            await PersistStateAsync(state, cancellationToken).ConfigureAwait(false);
            await EnsureTransferAttachedAsync(
                    state,
                    cancellationToken,
                    deferResetWhileOpenReserved: true)
                .ConfigureAwait(false);
            return;
        }

        HashSet<int> loadedPieces;
        lock (_sync)
        {
            loadedPieces = snapshot.PieceLength > 0 && snapshot.NumPieces > 0
                ? DecodeLoadedPieces(snapshot.Bitfield, snapshot.NumPieces)
                : [.. state.LoadedPieces];
        }

        lock (_sync)
        {
            if (!state.LoadedPieces.IsSubsetOf(loadedPieces))
            {
                throw new ModelHydrationIOException(
                    $"Transfer {snapshot.Handle} invalidated a piece already reported readable in epoch {state.TransferEpoch}.");
            }

            state.LoadedPieces = loadedPieces;
        }

        if (IsSafetensors(state.Identity.DestinationPath) && state.SafetensorsHeaderEnd is null &&
            IsRangeReady(state, 0, sizeof(long), snapshot.IsComplete))
        {
            ClassifySafetensorsHeaderLength(state);
        }

        if (state.SafetensorsHeaderEnd is > 0 and var headerEnd &&
            !state.SafetensorsHeaderValidated &&
            IsRangeReady(state, 0, headerEnd, snapshot.IsComplete))
        {
            ValidateSafetensorsHeader(state, headerEnd);
        }

        List<RangeWaiter> completedRanges = [];
        List<PromptWaiter> completedPrompts = [];
        if (snapshot.IsComplete)
        {
            await PersistStateAsync(
                    state,
                    cancellationToken,
                    completeOverride: true)
                .ConfigureAwait(false);
            await _backend.SaveSessionAsync(cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                state.Complete = true;
                state.StickyPromotion = false;
                completedPrompts.AddRange(state.PromptWaiters);
                completedRanges.AddRange(state.RangeWaiters);
                if (ReferenceEquals(_activeState, state))
                {
                    _activeState = null;
                }
            }
        }
        else
        {
            lock (_sync)
            {
                foreach (var waiter in state.RangeWaiters)
                {
                    ApplySafetensorsPromotion(state, waiter);
                    if (!waiter.RequiresComplete &&
                        IsRangeReady(state, waiter.Offset, waiter.Length, snapshot.IsComplete))
                    {
                        completedRanges.Add(waiter);
                    }
                }
            }
        }

        foreach (var waiter in completedRanges)
        {
            CompleteWaiter(state, waiter);
        }

        foreach (var waiter in completedPrompts)
        {
            CompleteWaiter(state, waiter);
        }

        await ReportProgressAsync(state, snapshot).ConfigureAwait(false);
        if (persistState && !snapshot.IsComplete)
        {
            await PersistStateAsync(state, cancellationToken).ConfigureAwait(false);
            await _backend.SaveSessionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool UpdateGeometryAndIdentity(
        TransferState state,
        ModelTransferSnapshot snapshot,
        out bool restartRequired,
        out bool processingDeferred)
    {
        var changed = false;
        restartRequired = false;
        processingDeferred = false;
        lock (_sync)
        {
            var snapshotHasGeometry = snapshot.PieceLength > 0 && snapshot.NumPieces > 0;
            if (!snapshotHasGeometry && !snapshot.IsComplete)
            {
                // An aria2 restart can briefly expose status before its piece
                // geometry and bitfield are available. Defer the whole snapshot
                // so an empty transient bitfield cannot invalidate loaded pieces.
                processingDeferred = true;
            }

            if (state.PieceLength > 0 &&
                snapshotHasGeometry &&
                (state.PieceLength != snapshot.PieceLength || state.NumPieces != snapshot.NumPieces))
            {
                if (state.DescriptorLeaseCount > 0)
                {
                    throw new ModelHydrationIOException(
                        $"Transfer {snapshot.Handle} changed piece geometry from " +
                        $"{state.PieceLength}/{state.NumPieces} to " +
                        $"{snapshot.PieceLength}/{snapshot.NumPieces} while managed descriptors were open.");
                }

                state.TransferEpoch++;
                state.LoadedPieces.Clear();
                state.SafetensorsHeaderEnd = null;
                state.SafetensorsHeaderValidated = false;
                changed = true;
            }

            if (snapshotHasGeometry &&
                (state.PieceLength != snapshot.PieceLength || state.NumPieces != snapshot.NumPieces))
            {
                state.PieceLength = snapshot.PieceLength;
                state.NumPieces = snapshot.NumPieces;
                changed = true;
            }

            if (LinuxFileIdentityReader.TryRead(state.Identity.DestinationPath, out var fileIdentity))
            {
                if (fileIdentity.Length != state.Identity.ExpectedLength)
                {
                    throw new ModelHydrationIOException(
                        $"Managed model '{state.Identity.DestinationPath}' length changed from {state.Identity.ExpectedLength} to {fileIdentity.Length}.");
                }

                if (state.Inode != 0 && (state.Inode != fileIdentity.Inode || state.DeviceId != fileIdentity.DeviceId))
                {
                    if (state.DescriptorLeaseCount > 0)
                    {
                        throw new ModelHydrationIOException(
                            $"Managed model '{state.Identity.DestinationPath}' was replaced while descriptors were open.");
                    }

                    if (state.OpenReservationCount > 0)
                    {
                        processingDeferred = true;
                        return changed;
                    }

                    state.RestartRequired = true;
                    restartRequired = true;
                    return changed;
                }

                if (state.Inode != fileIdentity.Inode || state.DeviceId != fileIdentity.DeviceId)
                {
                    state.Inode = fileIdentity.Inode;
                    state.DeviceId = fileIdentity.DeviceId;
                    changed = true;
                }
            }
        }

        return changed;
    }

    private void ClassifySafetensorsHeaderLength(TransferState state)
    {
        try
        {
            Span<byte> prefix = stackalloc byte[sizeof(long)];
            using var stream = new FileStream(
                state.Identity.DestinationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Read(prefix) != prefix.Length)
            {
                PromoteSticky(state, "safetensors_header_unavailable");
                state.SafetensorsHeaderEnd = -1;
                return;
            }

            var headerLength = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(prefix));
            var headerEnd = checked(sizeof(long) + headerLength);
            if (headerLength < 0 ||
                headerLength > MaximumSafetensorsHeaderLength ||
                headerEnd > state.Identity.ExpectedLength)
            {
                PromoteSticky(state, "safetensors_header_invalid");
                state.SafetensorsHeaderEnd = -1;
                return;
            }

            state.SafetensorsHeaderEnd = headerEnd;
        }
        catch (Exception)
        {
            PromoteSticky(state, "safetensors_header_unavailable");
            state.SafetensorsHeaderEnd = -1;
        }
    }

    private void ValidateSafetensorsHeader(TransferState state, long headerEnd)
    {
        try
        {
            var headerLength = checked((int)(headerEnd - sizeof(long)));
            var header = new byte[headerLength];
            using var stream = new FileStream(
                state.Identity.DestinationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Position = sizeof(long);
            stream.ReadExactly(header);
            using var document = JsonDocument.Parse(header);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Safetensors header root is not an object.");
            }

            state.SafetensorsHeaderValidated = true;
        }
        catch (Exception)
        {
            PromoteSticky(state, "safetensors_header_invalid");
            state.SafetensorsHeaderEnd = -1;
            state.SafetensorsHeaderValidated = false;
        }
    }

    private void ApplySafetensorsPromotion(TransferState state, RangeWaiter waiter)
    {
        if (!IsSafetensors(state.Identity.DestinationPath) || state.SafetensorsHeaderEnd is null)
        {
            return;
        }

        if (state.SafetensorsHeaderEnd < 0 ||
            SaturatingAdd(waiter.Offset, waiter.Length) > state.SafetensorsHeaderEnd)
        {
            PromoteSticky(state, "read_beyond_header");
        }
    }

    private static bool IsRangeReady(
        TransferState state,
        long offset,
        long length,
        bool transferComplete)
    {
        if (length == 0 || offset >= state.Identity.ExpectedLength || transferComplete)
        {
            return true;
        }

        if (state.PieceLength <= 0 || state.NumPieces <= 0)
        {
            return false;
        }

        var endExclusive = Math.Min(
            state.Identity.ExpectedLength,
            SaturatingAdd(offset, length));
        if (endExclusive <= offset)
        {
            return true;
        }

        var firstPiece = offset / state.PieceLength;
        var lastPiece = (endExclusive - 1) / state.PieceLength;
        for (var piece = firstPiece; piece <= lastPiece; piece++)
        {
            if (piece > int.MaxValue || !state.LoadedPieces.Contains((int)piece))
            {
                return false;
            }
        }

        return true;
    }

    public static HashSet<int> DecodeLoadedPieces(string? bitfield, long numPieces)
    {
        var result = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(bitfield) || numPieces <= 0)
        {
            return result;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(bitfield);
        }
        catch (FormatException)
        {
            return result;
        }

        var cappedPieces = Math.Min(numPieces, int.MaxValue);
        for (var piece = 0; piece < cappedPieces; piece++)
        {
            var byteIndex = piece / 8;
            if (byteIndex >= bytes.Length)
            {
                break;
            }

            var mask = 1 << (7 - (piece % 8));
            if ((bytes[byteIndex] & mask) != 0)
            {
                result.Add((int)piece);
            }
        }

        return result;
    }

    private async Task RefreshUriAsync(
        TransferState state,
        ModelTransferSnapshot snapshot,
        CancellationToken cancellationToken,
        bool saveSessionWhenAlreadyCurrent = false)
    {
        var replacementUris = GetSourceUris(state.Request);
        if (replacementUris.Count == 0)
        {
            return;
        }

        var currentUris = snapshot.Files.SelectMany(static file => file.Uris).Distinct(StringComparer.Ordinal).ToArray();
        if (currentUris.SequenceEqual(replacementUris, StringComparer.Ordinal))
        {
            if (saveSessionWhenAlreadyCurrent)
            {
                await _backend.SaveSessionAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await _backend
            .ChangeUriAsync(snapshot.Handle, currentUris, replacementUris, cancellationToken)
            .ConfigureAwait(false);
        RunnerLog.Event<DemandAwareModelHydrationCoordinator>(
            "model_transfer_uri_refreshed",
            "Model transfer URI refreshed model_id={ModelId}",
            state.Identity.ModelId);
        await _backend.SaveSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryRefreshPendingUriAsync(
        TransferState state,
        CancellationToken cancellationToken)
    {
        ModelTransferHandle? handle;
        string requestedSourceRevision;
        lock (_sync)
        {
            if (!state.UriRefreshRequired)
            {
                return true;
            }

            handle = state.Handle;
            requestedSourceRevision = GetSourceRevision(state.Request);
        }

        if (handle is null)
        {
            return true;
        }

        try
        {
            var snapshot = await _backend
                .GetStatusAsync(handle.Value, cancellationToken)
                .ConfigureAwait(false);
            ValidateSnapshotIdentity(state, snapshot);
            await RefreshUriAsync(
                    state,
                    snapshot,
                    cancellationToken,
                    saveSessionWhenAlreadyCurrent: true)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ModelHydrationIOException)
        {
            throw;
        }
        catch (ModelTransferStatusUnavailableException)
        {
            await ReattachUnavailableTransferAsync(state, handle.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            var shouldLog = false;
            lock (_sync)
            {
                if (!state.UriRefreshFailureLogged)
                {
                    state.UriRefreshFailureLogged = true;
                    shouldLog = true;
                }
            }

            if (shouldLog)
            {
                RunnerLog.Warning<DemandAwareModelHydrationCoordinator>(
                    $"Model transfer URI refresh will retry model_id={state.Identity.ModelId}");
            }

            return false;
        }

        lock (_sync)
        {
            state.UriRefreshFailureLogged = false;
            state.UriRefreshRequired = !string.Equals(
                requestedSourceRevision,
                GetSourceRevision(state.Request),
                StringComparison.Ordinal);
        }

        return true;
    }

    private async Task ReattachUnavailableTransferAsync(
        TransferState state,
        ModelTransferHandle unavailableHandle,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (state.Handle == unavailableHandle)
            {
                state.Handle = null;
            }

            if (ReferenceEquals(_activeState, state))
            {
                _activeState = null;
            }

            state.UriRefreshFailureLogged = false;
        }

        await EnsureTransferAttachedAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private async Task RetryRestoredPausesAsync(CancellationToken cancellationToken)
    {
        if (_restoredPauseRetries.IsEmpty)
        {
            return;
        }

        var availableSnapshots = await _backend.ListAsync(cancellationToken).ConfigureAwait(false);
        var availableByHandle = availableSnapshots.ToDictionary(static snapshot => snapshot.Handle);
        var changed = false;
        var handlesToProcess = _restoredPauseRetries.Count;
        for (var handleIndex = 0;
             handleIndex < handlesToProcess && _restoredPauseRetries.TryDequeue(out var handle);
             handleIndex++)
        {
            if (!availableByHandle.TryGetValue(handle, out var snapshot) || snapshot.IsStopped)
            {
                continue;
            }

            try
            {
                await _backend.ForcePauseAsync(handle, cancellationToken).ConfigureAwait(false);
                changed = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _restoredPauseRetries.Enqueue(handle);
                throw;
            }
            catch (ModelTransferStatusUnavailableException)
            {
                // A transfer that disappeared cannot occupy aria2's active slot.
            }
            catch (Exception ex)
            {
                _restoredPauseRetries.Enqueue(handle);
                RunnerLog.Warning<DemandAwareModelHydrationCoordinator>(
                    $"Could not pause restored model transfer {handle}: {ex.Message}");
            }
        }

        if (changed)
        {
            await _backend.SaveSessionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ThrowIfSessionCanceledLocked(string sessionId, TransferState state)
    {
        if (_canceledSessions.Contains(sessionId))
        {
            throw new ModelHydrationIOException(
                $"Session '{sessionId}' was canceled before model '{state.Identity.ModelId}' could publish its hydration lease.",
                transferEpoch: state.TransferEpoch);
        }
    }

    private long CaptureSessionGenerationLocked(string sessionId, TransferState state)
    {
        ThrowIfSessionCanceledLocked(sessionId, state);
        return _sessionGenerations.GetValueOrDefault(sessionId);
    }

    private void ThrowIfSessionGenerationChangedLocked(
        string sessionId,
        long observedGeneration,
        TransferState state)
    {
        ThrowIfSessionCanceledLocked(sessionId, state);
        var currentGeneration = _sessionGenerations.GetValueOrDefault(sessionId);
        if (currentGeneration != observedGeneration)
        {
            throw new ModelHydrationIOException(
                $"Session '{sessionId}' changed hydration generation before model '{state.Identity.ModelId}' could publish its hydration lease.",
                transferEpoch: state.TransferEpoch);
        }
    }

    private async Task CleanupOrphansAsync(CancellationToken cancellationToken)
    {
        var changed = false;
        var recordsToProcess = _orphanedRecords.Count;
        for (var recordIndex = 0;
             recordIndex < recordsToProcess && _orphanedRecords.TryDequeue(out var record);
             recordIndex++)
        {
            await _registrationCleanupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!TryGetCurrentUnclaimedRecord(record, out var currentRecord))
                {
                    continue;
                }

                record = currentRecord;

                var handle = new ModelTransferHandle(record.Handle);
                try
                {
                    await _backend.ForcePauseAsync(handle, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // The transfer may already be stopped or absent.
                }

                try
                {
                    await _backend.RemoveAsync(handle, cancellationToken).ConfigureAwait(false);
                    changed = true;
                }
                catch (ModelTransferStatusUnavailableException)
                {
                    // Already absent.
                }

                if (record.PlaceholderOwned && !record.Complete)
                {
                    var destinationExists = LinuxFileIdentityReader.TryRead(
                        record.DestinationPath,
                        out var identity);
                    var ownsDestination = destinationExists &&
                        identity.DeviceId == record.DeviceId &&
                        identity.Inode == record.Inode;
                    if (ownsDestination)
                    {
                        File.Delete(record.DestinationPath);
                    }

                    var controlPath = $"{record.DestinationPath}.aria2";
                    if ((ownsDestination || !destinationExists) && File.Exists(controlPath))
                    {
                        File.Delete(controlPath);
                    }
                }

                if (!record.Complete)
                {
                    await _index.RemoveAsync(record.IdentityDigest, cancellationToken).ConfigureAwait(false);
                    changed = true;
                }

                lock (_sync)
                {
                    if (_states.TryGetValue(
                            NormalizePath(record.DestinationPath),
                            out var orphanedState) &&
                        string.Equals(
                            orphanedState.Identity.Digest,
                            record.IdentityDigest,
                            StringComparison.Ordinal) &&
                        orphanedState.PendingOrphanCleanup &&
                        orphanedState.DescriptorLeaseCount == 0 &&
                        orphanedState.OpenReservationCount == 0)
                    {
                        _states.Remove(orphanedState.Identity.DestinationPath);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _orphanedRecords.Enqueue(record);
                throw;
            }
            catch (Exception ex)
            {
                _orphanedRecords.Enqueue(record);
                RunnerLog.Warning<DemandAwareModelHydrationCoordinator>(
                    $"Failed cleaning orphaned model transfer '{record.Handle}': {ex.Message}");
            }
            finally
            {
                _registrationCleanupGate.Release();
            }
        }

        if (changed)
        {
            await _backend.SaveSessionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private ConflictQuarantineEligibility GetConflictQuarantineEligibility(
        ModelTransferIndexRecord record)
    {
        lock (_sync)
        {
            var currentState = _states.Values.FirstOrDefault(state => string.Equals(
                state.Identity.Digest,
                record.IdentityDigest,
                StringComparison.Ordinal));
            if (currentState is not null)
            {
                if (!currentState.PendingConflictQuarantine)
                {
                    return ConflictQuarantineEligibility.Stale;
                }

                if (currentState.DescriptorLeaseCount > 0 ||
                    currentState.OpenReservationCount > 0 ||
                    currentState.PromptWaiters.Count > 0)
                {
                    return ConflictQuarantineEligibility.Deferred;
                }
            }
        }

        return _index.TryGet(record.IdentityDigest, out var currentRecord) &&
               currentRecord == record
            ? ConflictQuarantineEligibility.Eligible
            : ConflictQuarantineEligibility.Stale;
    }

    private bool TryGetCurrentUnclaimedRecord(
        ModelTransferIndexRecord queuedRecord,
        out ModelTransferIndexRecord currentRecord)
    {
        lock (_sync)
        {
            var currentState = _states.Values.FirstOrDefault(state => string.Equals(
                state.Identity.Digest,
                queuedRecord.IdentityDigest,
                StringComparison.Ordinal));
            if (currentState is not null &&
                (!currentState.PendingOrphanCleanup ||
                 currentState.DescriptorLeaseCount > 0 ||
                 currentState.OpenReservationCount > 0))
            {
                currentRecord = queuedRecord;
                return false;
            }
        }

        return _index.TryGet(queuedRecord.IdentityDigest, out currentRecord!);
    }

    private bool QueueOrphanCleanupLocked(TransferState state)
    {
        if (!state.PendingOrphanCleanup ||
            state.OrphanCleanupQueued ||
            state.DescriptorLeaseCount > 0 ||
            state.OpenReservationCount > 0 ||
            !_index.TryGet(state.Identity.Digest, out var record))
        {
            return false;
        }

        state.OrphanCleanupQueued = true;
        _orphanedRecords.Enqueue(record);
        return true;
    }

    private void QueueLegacyCleanupLocked(LegacyTransferCandidate candidate)
    {
        if (_legacyCleanupQueued.Add(candidate.Snapshot.Handle))
        {
            _legacyCleanup.Enqueue(candidate);
        }
    }

    private LegacyTransferCandidate CaptureLegacyTransfer(ModelTransferSnapshot snapshot)
    {
        var files = snapshot.Files
            .Where(file => _capabilities.OwnsPersistentModelPath(file.Path))
            .Select(static file =>
            {
                var path = NormalizePath(file.Path);
                LinuxFileIdentity? targetIdentity =
                    LinuxFileIdentityReader.TryRead(path, out var target) ? target : null;
                var controlPath = $"{path}.aria2";
                LinuxFileIdentity? controlIdentity =
                    LinuxFileIdentityReader.TryRead(controlPath, out var control) ? control : null;
                return new LegacyTransferFile(path, targetIdentity, controlIdentity);
            })
            .ToArray();
        return new LegacyTransferCandidate(snapshot, files);
    }

    private static void DeleteIfIdentityMatches(string path, LinuxFileIdentity? expectedIdentity)
    {
        if (expectedIdentity is not { } expected ||
            !LinuxFileIdentityReader.TryRead(path, out var current) ||
            current.DeviceId != expected.DeviceId ||
            current.Inode != expected.Inode)
        {
            return;
        }

        File.Delete(path);
    }

    private static bool IsPathConfirmedAbsent(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
    }

    private async Task CleanupLegacyTransfersAsync(CancellationToken cancellationToken)
    {
        var changed = false;
        var candidatesToProcess = _legacyCleanup.Count;
        for (var candidateIndex = 0;
             candidateIndex < candidatesToProcess && _legacyCleanup.TryDequeue(out var candidate);
             candidateIndex++)
        {
            var snapshot = candidate.Snapshot;
            await _registrationCleanupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                try
                {
                    await _backend.RemoveAsync(snapshot.Handle, cancellationToken).ConfigureAwait(false);
                }
                catch (ModelTransferStatusUnavailableException)
                {
                    // Already absent.
                }

                foreach (var file in candidate.Files)
                {
                    DeleteIfIdentityMatches(file.Path, file.TargetIdentity);
                    var controlPath = $"{file.Path}.aria2";
                    DeleteIfIdentityMatches(controlPath, file.ControlIdentity);
                }

                changed = true;
                lock (_sync)
                {
                    _legacyCandidates.RemoveAll(existing =>
                        existing.Snapshot.Handle == snapshot.Handle);
                    _legacyCleanupQueued.Remove(snapshot.Handle);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _legacyCleanup.Enqueue(candidate);
                throw;
            }
            catch (Exception ex)
            {
                _legacyCleanup.Enqueue(candidate);
                RunnerLog.Warning<DemandAwareModelHydrationCoordinator>(
                    $"Failed cleaning legacy model transfer '{snapshot.Handle}': {ex.Message}");
            }
            finally
            {
                _registrationCleanupGate.Release();
            }
        }

        if (changed)
        {
            await _backend.SaveSessionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task QuarantineConflictsAsync(CancellationToken cancellationToken)
    {
        var changed = false;
        var recordsToProcess = _conflictingRecords.Count;
        for (var recordIndex = 0;
             recordIndex < recordsToProcess && _conflictingRecords.TryDequeue(out var record);
             recordIndex++)
        {
            await _registrationCleanupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var eligibility = GetConflictQuarantineEligibility(record);
                if (eligibility == ConflictQuarantineEligibility.Deferred)
                {
                    _conflictingRecords.Enqueue(record);
                    continue;
                }

                if (eligibility != ConflictQuarantineEligibility.Eligible)
                {
                    continue;
                }

                var handle = new ModelTransferHandle(record.Handle);
                try
                {
                    await _backend.ForcePauseAsync(handle, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // The conflicting transfer may already be stopped or absent.
                }

                try
                {
                    await _backend.RemoveAsync(handle, cancellationToken).ConfigureAwait(false);
                }
                catch (ModelTransferStatusUnavailableException)
                {
                    // Already absent.
                }

                if (record.PlaceholderOwned)
                {
                    var targetExists = LinuxFileIdentityReader.TryRead(
                        record.DestinationPath,
                        out var identity);
                    var targetMatches = targetExists &&
                                        identity.DeviceId == record.DeviceId &&
                                        identity.Inode == record.Inode;
                    var targetAbsent = !targetExists &&
                                       IsPathConfirmedAbsent(record.DestinationPath);
                    if (targetMatches)
                    {
                        File.Delete(record.DestinationPath);
                    }

                    var controlPath = $"{record.DestinationPath}.aria2";
                    if ((targetMatches || targetAbsent) &&
                        File.Exists(controlPath))
                    {
                        File.Delete(controlPath);
                    }
                }

                lock (_sync)
                {
                    if (_states.TryGetValue(
                            NormalizePath(record.DestinationPath),
                            out var conflictingState) &&
                        string.Equals(
                            conflictingState.Identity.Digest,
                            record.IdentityDigest,
                            StringComparison.Ordinal) &&
                        conflictingState.PendingConflictQuarantine &&
                        conflictingState.DescriptorLeaseCount == 0 &&
                        conflictingState.OpenReservationCount == 0 &&
                        conflictingState.PromptWaiters.Count == 0)
                    {
                        _states.Remove(conflictingState.Identity.DestinationPath);
                    }
                }

                await _index
                    .QuarantineAsync(record, "identity-conflict", cancellationToken)
                    .ConfigureAwait(false);
                changed = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _conflictingRecords.Enqueue(record);
                throw;
            }
            catch (Exception ex)
            {
                _conflictingRecords.Enqueue(record);
                RunnerLog.Warning<DemandAwareModelHydrationCoordinator>(
                    $"Failed quarantining conflicting model transfer '{record.Handle}': {ex.Message}");
            }
            finally
            {
                _registrationCleanupGate.Release();
            }
        }

        if (changed)
        {
            await _backend.SaveSessionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PersistStateAsync(
        TransferState state,
        CancellationToken cancellationToken,
        bool? completeOverride = null)
    {
        if (state.Handle is not { } handle)
        {
            return;
        }

        ModelTransferIndexRecord record;
        lock (_sync)
        {
            record = new ModelTransferIndexRecord
            {
                IdentityDigest = state.Identity.Digest,
                ModelId = state.Identity.ModelId,
                DestinationPath = state.Identity.DestinationPath,
                ExpectedLength = state.Identity.ExpectedLength,
                Handle = handle.Value,
                TransferEpoch = state.TransferEpoch,
                PieceLength = state.PieceLength,
                NumPieces = state.NumPieces,
                DeviceId = state.DeviceId,
                Inode = state.Inode,
                PlaceholderOwned = state.PlaceholderOwned,
                Complete = completeOverride ?? state.Complete,
                RestartRequired = state.RestartRequired
            };
        }

        await _index.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private async Task ResetFailedTransferAsync(
        TransferState state,
        CancellationToken cancellationToken)
    {
        ModelTransferHandle? handle;
        lock (_sync)
        {
            if (!state.RestartRequired)
            {
                return;
            }

            if (state.DescriptorLeaseCount > 0)
            {
                throw new ModelHydrationIOException(
                    $"Transfer '{state.Identity.ModelId}' cannot restart while managed descriptors remain open.",
                    transferEpoch: state.TransferEpoch);
            }

            handle = state.Handle;
        }

        if (handle is { } failedHandle)
        {
            try
            {
                await _backend.ForcePauseAsync(failedHandle, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The failed transfer may no longer exist.
            }

            try
            {
                await _backend.RemoveAsync(failedHandle, cancellationToken).ConfigureAwait(false);
            }
            catch (ModelTransferStatusUnavailableException)
            {
                // Already absent.
            }
        }

        var controlPath = $"{state.Identity.DestinationPath}.aria2";
        if (File.Exists(controlPath))
        {
            File.Delete(controlPath);
        }

        lock (_sync)
        {
            state.Handle = null;
            state.TransferEpoch++;
            state.PieceLength = 0;
            state.NumPieces = 0;
            state.DeviceId = 0;
            state.Inode = 0;
            state.LoadedPieces.Clear();
            state.SafetensorsHeaderEnd = null;
            state.SafetensorsHeaderValidated = false;
            state.Complete = false;
            state.RestartRequired = true;
        }

        RecreatePlaceholderForRestart(state);
        await _backend.SaveSessionAsync(cancellationToken).ConfigureAwait(false);
        RunnerLog.Event<DemandAwareModelHydrationCoordinator>(
            "model_transfer_restarted",
            "Model transfer restarted model_id={ModelId} epoch={TransferEpoch}",
            state.Identity.ModelId,
            state.TransferEpoch);
    }

    private void RecreatePlaceholderForRestart(TransferState state)
    {
        var destination = state.Identity.DestinationPath;
        var stagingPath = $"{destination}.{state.Identity.DeterministicIdentifier}.offloadr-placeholder";
        if (File.Exists(stagingPath))
        {
            File.Delete(stagingPath);
        }

        var placeholder = CreateAndPublishPlaceholder(
            state.Identity,
            stagingPath,
            state.TransferEpoch,
            publishRestartRequired: true,
            finalRestartRequired: false,
            previousHandle: null,
            overwriteDestination: true);
        lock (_sync)
        {
            state.PlaceholderOwned = placeholder.Owned;
            state.DeviceId = placeholder.DeviceId;
            state.Inode = placeholder.Inode;
            state.RestartRequired = placeholder.RestartRequired;
        }
    }

    private async Task ReportProgressAsync(TransferState state, ModelTransferSnapshot snapshot)
    {
        var reporter = ProgressReporter;
        if (reporter is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        lock (_sync)
        {
            if (!snapshot.IsComplete && now - state.LastProgressReportUtc < ProgressInterval)
            {
                return;
            }

            state.LastProgressReportUtc = now;
        }

        var total = snapshot.TotalLength > 0 ? snapshot.TotalLength : state.Identity.ExpectedLength;
        var percent = total > 0
            ? Math.Clamp((double)snapshot.CompletedLength * 100d / total, 0, 100)
            : 0;
        try
        {
            await reporter(new ModelDownloadProgress(
                    state.SessionId,
                    state.Identity.ModelId,
                    state.Request.Filename,
                    state.Identity.DestinationPath,
                    snapshot.CompletedLength,
                    total,
                    percent,
                    snapshot.DownloadSpeed,
                    0,
                    snapshot.IsComplete ? ModelDownloadState.Complete : ModelDownloadState.InProgress,
                    snapshot.IsComplete ? "complete" : null))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunnerLog.Error<DemandAwareModelHydrationCoordinator>(
                ex,
                $"Failed to report model hydration progress for {state.Identity.ModelId}: {ex.Message}");
        }
    }

    private static void ValidateIndexedIdentity(TransferState state, ModelTransferIndexRecord indexed)
    {
        if (!string.Equals(indexed.IdentityDigest, state.Identity.Digest, StringComparison.Ordinal) ||
            !string.Equals(indexed.ModelId, state.Identity.ModelId, StringComparison.Ordinal) ||
            !string.Equals(indexed.DestinationPath, state.Identity.DestinationPath, StringComparison.Ordinal) ||
            indexed.ExpectedLength != state.Identity.ExpectedLength)
        {
            throw new ModelHydrationIOException(
                $"Persisted transfer identity for '{state.Identity.DestinationPath}' does not match its registration.");
        }
    }

    private static void ValidateSnapshotIdentity(TransferState state, ModelTransferSnapshot snapshot)
    {
        var matchingFiles = snapshot.Files
            .Where(file => string.Equals(NormalizePath(file.Path), state.Identity.DestinationPath, StringComparison.Ordinal))
            .ToArray();
        if (matchingFiles.Length != 1 ||
            matchingFiles[0].Length != 0 && matchingFiles[0].Length != state.Identity.ExpectedLength ||
            snapshot.TotalLength != 0 && snapshot.TotalLength != state.Identity.ExpectedLength)
        {
            throw new ModelHydrationIOException(
                $"Transfer {snapshot.Handle} does not match model '{state.Identity.ModelId}' destination and length.");
        }
    }

    private static void CaptureFileIdentity(TransferState state)
    {
        if (!LinuxFileIdentityReader.TryRead(state.Identity.DestinationPath, out var identity))
        {
            throw new ModelHydrationIOException(
                $"Could not identify managed model target '{state.Identity.DestinationPath}'.");
        }

        if (identity.Length != state.Identity.ExpectedLength)
        {
            throw new ModelHydrationIOException(
                $"Managed model target '{state.Identity.DestinationPath}' has length {identity.Length}; expected {state.Identity.ExpectedLength}.");
        }

        state.DeviceId = identity.DeviceId;
        state.Inode = identity.Inode;
    }

    private static void VerifyCurrentFileIdentity(TransferState state)
    {
        if (!LinuxFileIdentityReader.TryRead(state.Identity.DestinationPath, out var identity) ||
            identity.Length != state.Identity.ExpectedLength ||
            state.Inode != 0 &&
            (state.Inode != identity.Inode || state.DeviceId != identity.DeviceId))
        {
            throw new ModelHydrationIOException(
                $"Managed model target '{state.Identity.DestinationPath}' no longer matches transfer epoch {state.TransferEpoch}.",
                transferEpoch: state.TransferEpoch);
        }
    }

    private void RegisterCancellation(
        TransferState state,
        HydrationWaiter waiter,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return;
        }

        waiter.CancellationRegistration = cancellationToken.Register(() =>
        {
            lock (_sync)
            {
                if (waiter is RangeWaiter rangeWaiter)
                {
                    state.RangeWaiters.Remove(rangeWaiter);
                }

                if (waiter is PromptWaiter promptWaiter)
                {
                    state.PromptWaiters.Remove(promptWaiter);
                }

                waiter.Completion.TrySetCanceled(cancellationToken);
            }

            SignalScheduler();
        });
    }

    private void CompleteWaiter(TransferState state, HydrationWaiter waiter)
    {
        long transferEpoch;
        lock (_sync)
        {
            if (waiter is RangeWaiter rangeWaiter)
            {
                state.RangeWaiters.Remove(rangeWaiter);
            }

            if (waiter is PromptWaiter promptWaiter)
            {
                state.PromptWaiters.Remove(promptWaiter);
            }

            transferEpoch = state.TransferEpoch;
        }

        UnregisterWaiterCancellation(waiter);
        waiter.Completion.TrySetResult(new ModelHydrationEnsureResult(transferEpoch));
    }

    private static RangeWaiter[] RemoveLeaseWaitersLocked(TransferState state, ulong leaseId)
    {
        var waiters = state.RangeWaiters
            .Where(waiter => waiter.LeaseId == leaseId)
            .ToArray();
        foreach (var waiter in waiters)
        {
            state.RangeWaiters.Remove(waiter);
        }

        return waiters;
    }

    private void CancelWaiters(IEnumerable<HydrationWaiter> waiters)
    {
        foreach (var waiter in waiters)
        {
            UnregisterWaiterCancellation(waiter);
            waiter.Completion.TrySetCanceled();
        }
    }

    private void UnregisterWaiterCancellation(HydrationWaiter waiter)
    {
        if (Monitor.IsEntered(_sync))
        {
            throw new InvalidOperationException(
                "Waiter cancellation must be unregistered after releasing the coordinator lock.");
        }

        waiter.CancellationRegistration.Unregister();
    }

    private void FailState(TransferState state, ModelHydrationIOException exception)
    {
        if (exception.TransferEpoch == 0)
        {
            exception = new ModelHydrationIOException(
                exception.Message,
                exception,
                state.TransferEpoch);
        }

        HydrationWaiter[] waiters;
        lock (_sync)
        {
            waiters = state.RangeWaiters
                .Cast<HydrationWaiter>()
                .Concat(state.PromptWaiters)
                .ToArray();
            state.RangeWaiters.Clear();
            state.PromptWaiters.Clear();
            state.StickyPromotion = false;
        }

        foreach (var waiter in waiters)
        {
            UnregisterWaiterCancellation(waiter);
            waiter.Completion.TrySetException(exception);
        }
    }

    private void PromoteSticky(TransferState state, string reason)
    {
        lock (_sync)
        {
            if (state.StickyPromotion)
            {
                return;
            }

            state.StickyPromotion = true;
            state.StickySequence = Interlocked.Increment(ref _nextIntentSequence);
            state.StickyReason = reason;
        }
    }

    private string GetPromotionReason(TransferState state)
    {
        lock (_sync)
        {
            if (state.RangeWaiters.Any(static waiter => waiter.RequiresComplete))
            {
                return "mapping";
            }

            if (state.RangeWaiters.Count > 0)
            {
                return "range_waiter";
            }

            if (state.PromptWaiters.Count > 0)
            {
                return "prompt";
            }

            return state.StickyReason ?? "sticky_read";
        }
    }

    private Registration CreateRegistration(string sessionId, ModelDownloadRequest request)
    {
        var identity = ModelTransferIdentity.Create(
            request.ModelId,
            NormalizePath(request.DestinationPath),
            request.SizeBytes);
        return new Registration(
            string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId.Trim(),
            request.Clone(),
            identity);
    }

    private static IReadOnlyList<string> GetSourceUris(ModelDownloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MetalinkXml))
        {
            return string.IsNullOrWhiteSpace(request.SourceUrl) ? [] : [request.SourceUrl.Trim()];
        }

        try
        {
            return XDocument.Parse(request.MetalinkXml, LoadOptions.PreserveWhitespace)
                .Descendants()
                .Where(static element => string.Equals(
                    element.Name.LocalName,
                    "url",
                    StringComparison.OrdinalIgnoreCase))
                .Select(static element => element.Value.Trim())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException("Range-managed Metalink XML is malformed.", ex);
        }
    }

    private static string GetSourceRevision(ModelDownloadRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.MetalinkXml))
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(request.MetalinkXml));
            return $"metalink:{Convert.ToHexStringLower(digest)}";
        }

        return string.IsNullOrWhiteSpace(request.SourceUrl)
            ? string.Empty
            : $"uri:{request.SourceUrl.Trim()}";
    }

    private static byte[]? GetMetalinkBytes(ModelDownloadRequest request)
        => string.IsNullOrWhiteSpace(request.MetalinkXml)
            ? null
            : Encoding.UTF8.GetBytes(request.MetalinkXml);

    private static bool IsSafetensors(string path)
        => string.Equals(Path.GetExtension(path), ".safetensors", StringComparison.OrdinalIgnoreCase);

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path.GetFullPath(path.Trim());
    }

    private void SignalScheduler()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // One wake-up is sufficient.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        SignalScheduler();
        try
        {
            await _schedulerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }

        try
        {
            using var cleanupCancellation = new CancellationTokenSource(_shutdownCleanupTimeout);
            try
            {
                await PauseActiveAsync(cleanupCancellation.Token).ConfigureAwait(false);
                await ResumePreemptedExternalTransfersAsync(cleanupCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cleanupCancellation.IsCancellationRequested)
            {
                RunnerLog.Warning<DemandAwareModelHydrationCoordinator>(
                    "Timed out while pausing managed transfers and resuming preempted conventional transfers during shutdown.");
            }
        }
        finally
        {
            _registrationCleanupGate.Dispose();
            _wake.Dispose();
            _shutdown.Dispose();
        }
    }

    private sealed class TransferState(
        string sessionId,
        ModelDownloadRequest request,
        ModelTransferIdentity identity,
        bool placeholderOwned)
    {
        public string SessionId { get; set; } = sessionId;
        public ModelDownloadRequest Request { get; set; } = request;
        public ModelTransferIdentity Identity { get; } = identity;
        public bool PlaceholderOwned { get; set; } = placeholderOwned;
        public SemaphoreSlim AttachGate { get; } = new(1, 1);
        public ModelTransferHandle? Handle { get; set; }
        public long TransferEpoch { get; set; } = 1;
        public long PieceLength { get; set; }
        public long NumPieces { get; set; }
        public ulong DeviceId { get; set; }
        public ulong Inode { get; set; }
        public bool Complete { get; set; }
        public HashSet<int> LoadedPieces { get; set; } = [];
        public List<RangeWaiter> RangeWaiters { get; } = [];
        public List<PromptWaiter> PromptWaiters { get; } = [];
        public int DescriptorLeaseCount { get; set; }
        public int OpenReservationCount { get; set; }
        public bool StickyPromotion { get; set; }
        public long StickySequence { get; set; } = long.MaxValue;
        public string? StickyReason { get; set; }
        public long? SafetensorsHeaderEnd { get; set; }
        public bool SafetensorsHeaderValidated { get; set; }
        public DateTime LastProgressReportUtc { get; set; } = DateTime.MinValue;
        public bool RestartRequired { get; set; }
        public bool UriRefreshRequired { get; set; }
        public bool UriRefreshFailureLogged { get; set; }
        public bool PendingOrphanCleanup { get; set; }
        public bool OrphanCleanupQueued { get; set; }
        public bool PendingConflictQuarantine { get; set; }
    }

    private enum ConflictQuarantineEligibility
    {
        Stale,
        Deferred,
        Eligible
    }

    private abstract class HydrationWaiter(long sequence)
    {
        public long Sequence { get; } = sequence;
        public TaskCompletionSource<ModelHydrationEnsureResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration CancellationRegistration { get; set; }
    }

    private sealed class RangeWaiter(
        long sequence,
        ulong leaseId,
        long offset,
        long length,
        bool requiresComplete,
        long observedEpoch) : HydrationWaiter(sequence)
    {
        public ulong LeaseId { get; } = leaseId;
        public long Offset { get; } = offset;
        public long Length { get; } = length;
        public bool RequiresComplete { get; } = requiresComplete;
        public long ObservedEpoch { get; } = observedEpoch;
    }

    private sealed class PromptWaiter(
        long sequence,
        string sessionId,
        int ordinal) : HydrationWaiter(sequence)
    {
        public string SessionId { get; } = sessionId;
        public int Ordinal { get; } = ordinal;
    }

    private sealed record DescriptorLease(ulong LeaseId, string SessionId, TransferState State);

    private sealed record LegacyTransferCandidate(
        ModelTransferSnapshot Snapshot,
        IReadOnlyList<LegacyTransferFile> Files);

    private sealed record LegacyTransferFile(
        string Path,
        LinuxFileIdentity? TargetIdentity,
        LinuxFileIdentity? ControlIdentity);

    private readonly record struct RegisteredPlaceholder(
        bool Owned,
        ulong DeviceId,
        ulong Inode,
        bool RestartRequired = false,
        ModelTransferHandle? PreviousHandle = null,
        long TransferEpoch = 1);

    private sealed record Registration(
        string SessionId,
        ModelDownloadRequest Request,
        ModelTransferIdentity Identity);
}
