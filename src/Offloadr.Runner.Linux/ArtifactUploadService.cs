using Offloadr.Runner.V1;
using Offloadr.EditorRuntime.V1;
using Offloadr.Runner.Core.Sealing;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Offloadr.Runner.Linux;

/// <summary>Artifacts a session lost; when <paramref name="Incomplete"/> the runner could not count them all.</summary>
internal readonly record struct UnsavedUploads(int Count, bool Incomplete)
{
    public UnsavedUploads Add(UnsavedUploads other) => new(Count + other.Count, Incomplete || other.Incomplete);
}

internal sealed class ArtifactUploadService : IAsyncDisposable
{
    /// <summary>Largest session file the uploader will send; larger files are skipped.</summary>
    internal const long MaxUploadBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Most session files waiting for upload at once; more wait for the next scan.</summary>
    internal static int MaxPendingUploads { get; set; } = 4096;

    private static readonly TimeSpan DropWarningInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Every file below an artifact root, failing on a directory it cannot read rather than
    /// skipping it; replaceable so tests can make it fail.
    /// </summary>
    internal static Func<string, IEnumerable<string>> EnumerateArtifactFiles { get; set; } = static root =>
        Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        });

    /// <summary>How long Stop waits for cancelled upload and download work before moving on.</summary>
    internal static TimeSpan StopWaitTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a session's stop may take before its lost artifacts are recorded without waiting
    /// further. Under the caller's own bound, so the count is fixed before the session's data is
    /// removed: a stop that finishes after that would count none.
    /// </summary>
    internal static TimeSpan StopRecordTimeout { get; set; } = TimeSpan.FromSeconds(25);

    private readonly string? _runnerSecret;
    private readonly RunnerArtifactService.RunnerArtifactServiceClient _artifactClient;
    private readonly Metadata? _authHeaders;
    private readonly ILogger<ArtifactUploadService> _logger;
    private readonly RuntimeIdentityRegistry? _runtimeIdentities;
    private readonly string _runnerId;
    private readonly ArtifactUploadPolicy _uploadPolicy;
    private readonly ConcurrentDictionary<string, SessionUploader> _sessions = new(StringComparer.Ordinal);
    // Unsaved artifacts per logical session, summed over its runtimes: a restart removes the old
    // runtime's data too, and the session's Stop must still report what was lost then.
    // Keyed by lifecycle generation too: a session id is reused by a newer lifecycle, whose Stop
    // must not report an older one's losses.
    private readonly ConcurrentDictionary<(string SessionId, ulong Generation), UnsavedUploads> _unsavedBySession = new();
    private readonly object _stopGate = new();
    private readonly Dictionary<string, StopInFlight> _stopping = new(StringComparer.Ordinal);

    /// <summary>A session's stop that has not finished; its count is recorded once, by whichever comes first.</summary>
    private sealed class StopInFlight(SessionUploader uploader)
    {
        public SessionUploader Uploader { get; } = uploader;
        public Task<int> Task { get; set; } = System.Threading.Tasks.Task.FromResult(0);
        public int? RecordedCount { get; set; }
    }

    private readonly RunnerReadRequesterIdentity _requester;

    public ArtifactUploadService(string? runnerSecret, RunnerArtifactService.RunnerArtifactServiceClient artifactClient, ILogger<ArtifactUploadService> logger, RuntimeIdentityRegistry? runtimeIdentities = null, string? runnerId = null, string? instanceId = null, ArtifactUploadPolicy? uploadPolicy = null)
    {
        _requester = new RunnerReadRequesterIdentity(runnerId, instanceId, runtimeIdentities);
        _runnerSecret = string.IsNullOrWhiteSpace(runnerSecret) ? null : runnerSecret.Trim();
        _artifactClient = artifactClient ?? throw new ArgumentNullException(nameof(artifactClient));
        _authHeaders = BuildAuthHeaders(_runnerSecret);
        _logger = logger;
        _runtimeIdentities = runtimeIdentities;
        _runnerId = runnerId?.Trim() ?? string.Empty;
        _uploadPolicy = uploadPolicy ?? new ArtifactUploadPolicy();
    }

    private static Metadata? BuildAuthHeaders(string? runnerSecret)
    {
        if (runnerSecret is null)
        {
            return null;
        }

        var headers = new Metadata();
        if (!string.IsNullOrWhiteSpace(runnerSecret))
        {
            headers.Add("x-runner-secret", runnerSecret);
        }

        return headers;
    }

    public async Task StartSessionAsync(string sessionId, SessionProcessManager.SessionPaths paths, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            // A repeated start of the same runtime keeps its uploader. One left by an earlier
            // runtime (it exited while a relaunch was already admitted, so nothing stopped it) is
            // stopped, which records what it lost, and replaced: its reads name that runtime.
            var current = CurrentRuntime(sessionId);
            if (existing.CreatedFor is { } createdFor ? current is { } now && createdFor.SameRuntime(now) : current is null)
            {
                return;
            }

            await StopSessionAsync(sessionId).ConfigureAwait(false);
        }

        // A sealed session's artifact names stay out of the console, which reaches the host's disk.
        ILogger logger = paths.IsSealed ? new SealedSessionLogger(_logger) : _logger;
        var uploader = new SessionUploader(_runnerSecret, _artifactClient, _authHeaders, sessionId, paths, logger, _runtimeIdentities, _runnerId, _requester, _uploadPolicy);
        if (!_sessions.TryAdd(sessionId, uploader))
        {
            await uploader.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            await uploader.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SeedSessionAsync(
        string sessionId,
        string editorSid,
        string? owner,
        IEnumerable<EditorArtifactMetadata>? initialArtifacts,
        CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(sessionId, out var uploader))
        {
            await uploader.SeedSnapshotAsync(editorSid, owner, initialArtifacts, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Re-reads the file versions recorded for the session's artifacts after something other than
    /// a rewrite moved their change time, such as the ownership pass before the editor starts.
    /// </summary>
    public void RefreshRecordedVersions(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var uploader))
        {
            uploader.RefreshRecordedVersions();
        }
    }

    /// <summary>
    /// Takes the artifacts the session's lifecycle <paramref name="lifecycleGeneration"/> lost
    /// across all its runtimes, for its stop acknowledgement, and forgets them along with what
    /// older lifecycles of the session left unreported.
    /// </summary>
    public int TakeUnsavedUploadCount(string sessionId, ulong lifecycleGeneration = 0)
        => TakeUnsavedUploads(sessionId, lifecycleGeneration).Count;

    /// <inheritdoc cref="TakeUnsavedUploadCount"/>
    public UnsavedUploads TakeUnsavedUploads(string sessionId, ulong lifecycleGeneration = 0)
    {
        lock (_stopGate)
        {
            var taken = TakeRecordedUnsavedUploads(sessionId, lifecycleGeneration);
            // The Stop of a lifecycle whose data a restart removed reports that nobody counted
            // it; so does an unfenced Stop, or any when the generation was not recorded. Other
            // lifecycles' marks stay for their own Stops, which may still come.
            var incomplete = false;
            foreach (var mark in _lossUncounted.Keys.Where(mark => mark.SessionId == sessionId).ToArray())
            {
                if (lifecycleGeneration == 0 || mark.Generation == 0 || mark.Generation == lifecycleGeneration)
                {
                    _lossUncounted.TryRemove(mark, out _);
                    incomplete = true;
                }
            }

            return incomplete || Volatile.Read(ref _everyLossUncounted) ? taken with { Incomplete = true } : taken;
        }
    }

    private readonly ConcurrentDictionary<(string SessionId, ulong Generation), byte> _lossUncounted = new();

    /// <summary>
    /// Marks a session lifecycle (0 when unknown) whose data was removed without counting what it
    /// lost, such as sealed data an earlier runner process left: that lifecycle's Stop reports
    /// the count as unknown.
    /// </summary>
    /// <summary>
    /// Marks every lifecycle's loss as uncounted, for good: which lifecycles an earlier runner
    /// process left data of is not known (their notes could not be read).
    /// </summary>
    public void MarkEveryLossUncounted() => Volatile.Write(ref _everyLossUncounted, true);

    private bool _everyLossUncounted;

    public void MarkLossUncounted(string sessionId, ulong lifecycleGeneration = 0)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _lossUncounted[(sessionId, lifecycleGeneration)] = 0;
        }
    }

    private UnsavedUploads TakeRecordedUnsavedUploads(string sessionId, ulong lifecycleGeneration)
    {
        lock (_stopGate)
        {
            // An unfenced Stop (no generation) is for whatever lifecycle the session has.
            var unfenced = lifecycleGeneration == 0;

            // A stop still running has not counted yet: what it would find is not known.
            if (_stopping.TryGetValue(sessionId, out var inFlight) && !inFlight.Task.IsCompleted &&
                (unfenced || inFlight.Uploader.LifecycleGeneration == lifecycleGeneration))
            {
                RecordWithoutWaiting(sessionId, inFlight);
            }

            if (unfenced)
            {
                UnsavedUploads all = default;
                foreach (var key in _unsavedBySession.Keys.Where(key => key.SessionId == sessionId).ToArray())
                {
                    if (_unsavedBySession.TryRemove(key, out var lost))
                    {
                        all = all.Add(lost);
                    }
                }

                return all;
            }

            // Only this lifecycle's: an older one's Stop may still come, after a newer one's.
            return _unsavedBySession.TryRemove((sessionId, lifecycleGeneration), out var unsaved) ? unsaved : default;
        }
    }

    private RuntimeIdentity? CurrentRuntime(string sessionId)
        => _runtimeIdentities?.TryGet(sessionId, out var identity) == true ? identity : null;

    private void Record(string sessionId, ulong lifecycleGeneration, UnsavedUploads unsaved)
        => _unsavedBySession.AddOrUpdate((sessionId, lifecycleGeneration), unsaved, (_, earlier) => earlier.Add(unsaved));

    internal int GetPendingUploadCount(string sessionId)
        => _sessions.TryGetValue(sessionId, out var uploader) ? uploader.PendingUploadCount : 0;

    /// <summary>
    /// Frees memory on a sealed session's root by turning uploaded artifacts back into empty
    /// catalog placeholders, oldest upload first, until at least <paramref name="bytesToFree"/>
    /// bytes are released. Only artifacts the control plane accepted, unchanged since, idle for
    /// <paramref name="minimumIdle"/> and readable again through the VFS are evicted; opening one
    /// later downloads it again. Returns the bytes released (0 for an unsealed session).
    /// </summary>
    /// <summary>Sets <see cref="SessionUploader.BeforeEvictionCandidate"/> for a session (tests only).</summary>
    internal void SetBeforeEvictionCandidate(string sessionId, Action<string>? hook)
    {
        if (_sessions.TryGetValue(sessionId, out var uploader))
        {
            uploader.BeforeEvictionCandidate = hook;
        }
    }

    public long EvictUploadedArtifacts(string sessionId, long bytesToFree, TimeSpan minimumIdle)
        => _sessions.TryGetValue(sessionId, out var uploader) && uploader.IsSealed
            ? uploader.EvictUploadedArtifacts(bytesToFree, minimumIdle, DateTimeOffset.UtcNow)
            : 0;

    public void RegisterInputSeeds(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var uploader))
        {
            uploader.CaptureInputSeeds();
        }
    }

    public async Task ActivateSessionAsync(string sessionId, string editorSid, string? owner, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(sessionId, out var uploader))
        {
            await uploader.ActivateAsync(editorSid, owner, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> TryEnsureArtifactAvailableAsync(string path, bool highPriority, CancellationToken cancellationToken)
    {
        foreach (var uploader in _sessions.Values)
        {
            try
            {
                if (await uploader.TryEnsureArtifactAsync(path, highPriority, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (uploader.OwnsArtifactPath(path))
                {
                    throw;
                }

                _logger.LogDebug(ex, "Failed ensuring artifact availability via uploader for path {Path}", path);
            }
        }

        return false;
    }

    public bool OwnsArtifactPath(string path)
        => _sessions.Values.Any(uploader => uploader.OwnsArtifactPath(path));

    internal Func<Task> CaptureSessionRefresh(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var captured))
            throw new InvalidOperationException("The captured artifact uploader is not active.");
        // Delayed prompt sweeps belong to this uploader instance. A child restart
        // can reuse the logical session ID but must never inherit an old callback.
        return () => _sessions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, captured)
            ? captured.RefreshAsync() : Task.CompletedTask;
    }

    public async Task RefreshSessionAsync(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var uploader))
        {
            await uploader.RefreshAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops the session's uploads after a short drain. Returns how many artifacts were not
    /// uploaded: they failed or were still pending, and are lost once the session's data is removed.
    /// </summary>
    public Task<int> StopSessionAsync(string sessionId)
    {
        // A stop that overlaps another one for the session (an exit cleanup still draining when
        // the Stop arrives) waits for it, so the lost artifacts are recorded before anyone takes them.
        lock (_stopGate)
        {
            if (!_sessions.TryRemove(sessionId, out var uploader))
            {
                return _stopping.TryGetValue(sessionId, out var inFlight) ? inFlight.Task : Task.FromResult(0);
            }

            var stopping = new StopInFlight(uploader);
            stopping.Task = StopAndRecordAsync(sessionId, stopping);
            _stopping[sessionId] = stopping;
            _ = stopping.Task.ContinueWith(
                _ =>
                {
                    lock (_stopGate)
                    {
                        ((ICollection<KeyValuePair<string, StopInFlight>>)_stopping).Remove(new(sessionId, stopping));
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return stopping.Task;
        }
    }

    private async Task<int> StopAndRecordAsync(string sessionId, StopInFlight stopping)
    {
        var stop = stopping.Uploader.StopAsync();
        if (await Task.WhenAny(stop, Task.Delay(StopRecordTimeout)).ConfigureAwait(false) != stop)
        {
            _ = stop.ContinueWith(static failed => _ = failed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            lock (_stopGate)
            {
                return RecordWithoutWaiting(sessionId, stopping);
            }
        }

        await stop.ConfigureAwait(false);
        var unsaved = stopping.Uploader.UnsavedUploadCount;
        lock (_stopGate)
        {
            if (stopping.RecordedCount is { } recorded)
            {
                // Counted already, without waiting; a count taken now may follow removed data.
                return recorded;
            }

            stopping.RecordedCount = unsaved;
            if (unsaved > 0 || stopping.Uploader.UnsavedCountIncomplete)
            {
                Record(sessionId, stopping.Uploader.LifecycleGeneration, new UnsavedUploads(unsaved, stopping.Uploader.UnsavedCountIncomplete));
            }
        }

        if (unsaved > 0)
        {
            _logger.LogWarning(
                "Session stopped with artifacts that were not uploaded session={SessionId} count={Count}",
                sessionId,
                unsaved);
        }

        return unsaved;
    }

    /// <summary>
    /// Records a stop that has not finished as losing every pending upload, and at least one, and
    /// marks the count incomplete: what the uploader would have found is not known, and the
    /// session must not be reported as fully saved. Called under <see cref="_stopGate"/>.
    /// </summary>
    private int RecordWithoutWaiting(string sessionId, StopInFlight stopping)
    {
        if (stopping.RecordedCount is { } recorded)
        {
            return recorded;
        }

        var unsaved = Math.Max(1, stopping.Uploader.PendingUploadCount);
        stopping.RecordedCount = unsaved;
        Record(sessionId, stopping.Uploader.LifecycleGeneration, new UnsavedUploads(unsaved, Incomplete: true));
        _logger.LogWarning(
            "Artifact uploads of session={SessionId} did not stop in time; counting {Count} as not uploaded",
            sessionId,
            unsaved);
        return unsaved;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _sessions)
        {
            try
            {
                await kvp.Value.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose artifact uploader for session {SessionId}", kvp.Key);
            }
        }
        _sessions.Clear();
    }

    private sealed class SessionUploader : IAsyncDisposable
    {
        private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan StopDrainGracePeriod = TimeSpan.FromSeconds(5);
        private static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".bmp"] = "image/bmp",
            [".tif"] = "image/tiff",
            [".tiff"] = "image/tiff",
            [".mp4"] = "video/mp4",
            [".mov"] = "video/quicktime",
            [".mkv"] = "video/x-matroska",
            [".json"] = "application/json",
            [".txt"] = "text/plain",
            [".zip"] = "application/zip",
            [".tar"] = "application/x-tar"
        };

        private readonly string? _runnerSecret;
        private readonly RunnerArtifactService.RunnerArtifactServiceClient _artifactClient;
        private readonly Metadata? _authHeaders;
        private readonly string _sessionId;
        private string? _editorSid;
        private string? _owner;
        private readonly SessionProcessManager.SessionPaths _paths;
        private readonly ILogger _logger;
        private readonly RuntimeIdentityRegistry? _runtimeIdentities;
        private readonly string _runnerId;
        private readonly Channel<UploadRequest> _queue;
        private readonly ArtifactUploadOperationIdentity _uploadOperationIdentity = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<string, ArtifactUploadBudget> _pending = new(StringComparer.Ordinal);
        private readonly ArtifactUploadPolicy _uploadPolicy;
        private readonly ConcurrentDictionary<string, RemoteArtifactState> _catalog = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Task> _downloadTasks = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _inputSeeds = new(StringComparer.Ordinal);
        private readonly object _watcherGate = new();
        private FileSystemWatcher? _homeWatcher;
        private FileSystemWatcher? _outputWatcher;
        private FileSystemWatcher? _tempWatcher;
        private Task? _worker;
        private bool _activated;
        private bool _catalogLoaded;
        private long _droppedUploads;
        private int _rescanOwed;
        private long _lastDropWarningTicks = long.MinValue / 2;

        private readonly RunnerReadRequester _readRequester;

        public SessionUploader(string? runnerSecret, RunnerArtifactService.RunnerArtifactServiceClient artifactClient, Metadata? authHeaders, string sessionId, SessionProcessManager.SessionPaths paths, ILogger logger, RuntimeIdentityRegistry? runtimeIdentities = null, string? runnerId = null, RunnerReadRequesterIdentity? requester = null, ArtifactUploadPolicy? uploadPolicy = null)
        {
            _readRequester = (requester ?? new RunnerReadRequesterIdentity(runnerId, null, runtimeIdentities)).For(sessionId);
            CreatedFor = runtimeIdentities?.TryGet(sessionId, out var createdFor) == true ? createdFor : null;
            _runnerSecret = runnerSecret;
            _artifactClient = artifactClient;
            _authHeaders = authHeaders;
            _sessionId = sessionId;
            _paths = paths;
            _logger = logger;
            _runtimeIdentities = runtimeIdentities;
            LifecycleGeneration = _readRequester.LifecycleGeneration;
            _runnerId = runnerId?.Trim() ?? string.Empty;
            _uploadPolicy = uploadPolicy ?? new ArtifactUploadPolicy();
            _queue = Channel.CreateUnbounded<UploadRequest>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        }

        /// <summary>
        /// The runtime registered for the session when this uploader was created, if any, as the
        /// control plane spelled it. Every upload names it: a newer runtime registered later must
        /// never be credited with this one's output.
        /// </summary>
        public RuntimeIdentity? CreatedFor { get; }

        /// <summary>The lifecycle this uploader's runtime belongs to (0 without a registry).</summary>
        public ulong LifecycleGeneration { get; }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureInputSeeds();
            _worker = Task.Run(ProcessQueueAsync, _cts.Token);
            return Task.CompletedTask;
        }

        public async Task ActivateAsync(string editorSid, string? owner, CancellationToken cancellationToken)
        {
            await SeedSnapshotAsync(editorSid, owner, initialArtifacts: null, cancellationToken).ConfigureAwait(false);
        }

        public async Task RefreshAsync()
        {
            if (!_activated)
            {
                return;
            }

            await EnsureCatalogAsync(CancellationToken.None, forceReload: true).ConfigureAwait(false);
            EnqueueExistingFiles(_paths.OutputDirectory, "output");
            EnqueueExistingFiles(_paths.TempDirectory, "temp");
        }

        public async Task SeedSnapshotAsync(
            string editorSid,
            string? owner,
            IEnumerable<EditorArtifactMetadata>? initialArtifacts,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(editorSid))
            {
                _editorSid = editorSid;
            }

            if (!string.IsNullOrWhiteSpace(owner))
            {
                _owner = owner;
            }

            var seededCatalog = SeedInitialCatalog(editorSid, initialArtifacts);

            if (_activated)
            {
                await EnsureCatalogAsync(cancellationToken, forceReload: true, pruneMissing: !seededCatalog).ConfigureAwait(false);
                return;
            }

            await EnsureCatalogAsync(cancellationToken, pruneMissing: !seededCatalog).ConfigureAwait(false);
            _activated = true;
            RegisterArtifactWatcher(_paths.OutputDirectory, "output");
            RegisterArtifactWatcher(_paths.TempDirectory, "temp");
            // Watches the directory holding the output and temp roots, which a sealed session keeps outside its home.
            RegisterHomeWatcher(Path.GetDirectoryName(_paths.OutputDirectory));
            EnqueueExistingFiles(_paths.OutputDirectory, "output");
            EnqueueExistingFiles(_paths.TempDirectory, "temp");
            _logger.LogInformation(
                "Artifact uploader activated session={SessionId} editor={EditorSid} outputRoot={OutputRoot} tempRoot={TempRoot}",
                _sessionId,
                _editorSid,
                _paths.OutputDirectory,
                _paths.TempDirectory);

            if (!string.IsNullOrWhiteSpace(_runnerSecret))
            {
                _logger.LogInformation("Artifact auth mode session={SessionId} mode=runner-secret", _sessionId);
            }
        }

        public async Task<bool> TryEnsureArtifactAsync(string path, bool highPriority, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var normalized = NormalizePath(path);
            await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);

            var dynamicState = false;
            if (!TryGetCatalogEntry(normalized, out var state) || state is null)
            {
                if (!TryResolveArtifactPath(normalized, out var type, out var subfolder, out var filename))
                {
                    return false;
                }

                if (ShouldPreserveUncatalogedLocalFileBeforeCatalogRefresh(type, normalized))
                {
                    return true;
                }

                var refreshSucceeded = await EnsureCatalogAsync(cancellationToken, forceReload: true).ConfigureAwait(false);

                if (!TryGetCatalogEntry(normalized, out state) || state is null)
                {
                    if (refreshSucceeded && ShouldPreserveUncatalogedLocalFileAfterCatalogRefresh(type, normalized))
                    {
                        return true;
                    }

                    if (refreshSucceeded)
                    {
                        ClearUncatalogedInputFile(type, normalized);
                        if (type.Equals("input", StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }
                    }

                    if (!refreshSucceeded)
                    {
                        return false;
                    }

                    var editorSid = _editorSid;
                    if (string.IsNullOrWhiteSpace(editorSid))
                    {
                        return false;
                    }

                    state = new RemoteArtifactState(
                        editorSid,
                        type,
                        subfolder,
                        filename,
                        normalized,
                        sizeBytes: null,
                        sha256: null,
                        DateTimeOffset.UtcNow);
                    dynamicState = true;
                    _catalog[normalized] = state;
                }
            }

            // The shim opens the file itself once this returns: eviction must not replace it in
            // between, so serving marks it as in use under the eviction gate.
            lock (_evictionGate)
            {
                if (state.Downloaded && File.Exists(state.LocalPath))
                {
                    state.MarkServed(DateTimeOffset.UtcNow);
                    return true;
                }
            }

            // The fetch is shared by every caller for this artifact and runs for the uploader's
            // lifetime, not under any one caller's token: a client that hangs up stops waiting
            // without failing the others.
            var downloadTask = GetOrStartArtifactDownload(state, highPriority);
            try
            {
                await downloadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception) when (downloadTask.IsFaulted)
            {
                if (dynamicState)
                {
                    ((ICollection<KeyValuePair<string, RemoteArtifactState>>)_catalog).Remove(new(normalized, state));
                }

                throw;
            }
        }

        private Task GetOrStartArtifactDownload(RemoteArtifactState state, bool highPriority)
        {
            lock (_downloadTasks)
            {
                if (_downloadTasks.TryGetValue(state.LocalPath, out var existing))
                {
                    return existing;
                }

                var download = Task.Run(() => DownloadArtifactAsync(state, highPriority, CancellationToken.None));
                _downloadTasks[state.LocalPath] = download;
                // Registered after the add, so an already finished download is still removed.
                _ = download.ContinueWith(
                    completed => ((ICollection<KeyValuePair<string, Task>>)_downloadTasks).Remove(new(state.LocalPath, completed)),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return download;
            }
        }

        public bool OwnsArtifactPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return TryResolveArtifactPath(NormalizePath(path), out _, out _, out _);
        }

        private void RegisterHomeWatcher(string? root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                _logger.LogWarning(
                    "Artifact home watcher not registered session={SessionId} root={Root} reason=missing-directory",
                    _sessionId,
                    root);
                return;
            }

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.DirectoryName
            };

            FileSystemEventHandler createdHandler = (sender, args) => OnHomeWatcherEvent(root, args.ChangeType, args.FullPath);
            FileSystemEventHandler deletedHandler = (sender, args) => OnHomeWatcherEvent(root, args.ChangeType, args.FullPath);
            RenamedEventHandler renameHandler = (sender, args) => OnHomeWatcherEvent(root, WatcherChangeTypes.Renamed, args.FullPath, args.OldFullPath);
            watcher.Created += createdHandler;
            watcher.Deleted += deletedHandler;
            watcher.Renamed += renameHandler;
            watcher.Error += (_, ex) => _logger.LogWarning(
                ex.GetException() ?? new Exception("unknown"),
                "Artifact home watcher error session={SessionId} root={Root}",
                _sessionId,
                root);

            lock (_watcherGate)
            {
                ReplaceWatcher(ref _homeWatcher, watcher);
            }

            _logger.LogInformation(
                "Artifact home watcher registered session={SessionId} root={Root} outputRoot={OutputRoot} tempRoot={TempRoot}",
                _sessionId,
                root,
                _paths.OutputDirectory,
                _paths.TempDirectory);
        }

        private void RegisterArtifactWatcher(string? root, string type)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                _logger.LogWarning(
                    "Artifact watcher not registered session={SessionId} type={Type} root={Root} reason=missing-directory",
                    _sessionId,
                    type,
                    root);
                return;
            }

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                // Folders too: one made or moved in brings files no event names.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
            };

            FileSystemEventHandler handler = (sender, args) => OnArtifactWatcherEvent(type, root, args.ChangeType, args.FullPath);
            RenamedEventHandler renameHandler = (sender, args) => OnArtifactWatcherEvent(type, root, WatcherChangeTypes.Renamed, args.FullPath, args.OldFullPath);
            watcher.Created += handler;
            watcher.Changed += handler;
            watcher.Renamed += renameHandler;
            watcher.Error += (_, ex) => _logger.LogWarning(
                ex.GetException() ?? new Exception("unknown"),
                "Artifact watcher error session={SessionId} type={Type} root={Root}",
                _sessionId,
                type,
                root);

            lock (_watcherGate)
            {
                if (type.Equals("output", StringComparison.OrdinalIgnoreCase))
                {
                    ReplaceWatcher(ref _outputWatcher, watcher);
                }
                else if (type.Equals("temp", StringComparison.OrdinalIgnoreCase))
                {
                    ReplaceWatcher(ref _tempWatcher, watcher);
                }
                else
                {
                    watcher.Dispose();
                    throw new InvalidOperationException($"Unknown artifact watcher type '{type}'.");
                }
            }

            _logger.LogInformation(
                "Artifact watcher registered session={SessionId} type={Type} root={Root}",
                _sessionId,
                type,
                root);
        }

        private void OnHomeWatcherEvent(string root, WatcherChangeTypes changeType, string fullPath, string? oldFullPath = null)
        {
            try
            {
                if (changeType == WatcherChangeTypes.Deleted)
                {
                    if (TryGetArtifactRootType(fullPath, out var deletedType))
                    {
                        if (!ArtifactRootExists(deletedType))
                        {
                            UnregisterArtifactWatcher(deletedType, "root-deleted");
                        }
                    }
                    return;
                }

                if (changeType == WatcherChangeTypes.Created)
                {
                    if (TryGetArtifactRootType(fullPath, out var createdType))
                    {
                        RegisterArtifactWatcher(GetArtifactRootPath(createdType), createdType);
                        EnqueueExistingFiles(GetArtifactRootPath(createdType), createdType);
                    }
                    return;
                }

                if (changeType == WatcherChangeTypes.Renamed)
                {
                    if (TryGetArtifactRootType(oldFullPath, out var oldType))
                    {
                        if (!ArtifactRootExists(oldType))
                        {
                            UnregisterArtifactWatcher(oldType, "root-moved-or-deleted");
                        }
                    }

                    if (TryGetArtifactRootType(fullPath, out var newType))
                    {
                        RegisterArtifactWatcher(GetArtifactRootPath(newType), newType);
                        EnqueueExistingFiles(GetArtifactRootPath(newType), newType);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to handle home watcher event for session {SessionId}", _sessionId);
            }
        }

        private void UnregisterArtifactWatcher(string type, string reason)
        {
            FileSystemWatcher? removed = null;

            lock (_watcherGate)
            {
                if (type.Equals("output", StringComparison.OrdinalIgnoreCase))
                {
                    removed = _outputWatcher;
                    _outputWatcher = null;
                }
                else if (type.Equals("temp", StringComparison.OrdinalIgnoreCase))
                {
                    removed = _tempWatcher;
                    _tempWatcher = null;
                }
            }

            if (removed is null)
            {
                return;
            }

            try
            {
                removed.EnableRaisingEvents = false;
                removed.Dispose();
            }
            catch
            {
            }

            _logger.LogInformation(
                "Artifact watcher removed session={SessionId} type={Type} root={Root} reason={Reason}",
                _sessionId,
                type,
                GetArtifactRootPath(type),
                reason);
        }

        private void EnqueueExistingFiles(string? root, string type)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                _logger.LogInformation(
                    "Artifact scan skipped session={SessionId} type={Type} root={Root} reason=missing-directory",
                    _sessionId,
                    type,
                    root);
                return;
            }

            try
            {
                var discovered = 0;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    discovered++;
                    OnFileEvent(type, file, "scan");
                }

                _logger.LogDebug(
                    "Artifact scan completed session={SessionId} type={Type} root={Root} files={Count}",
                    _sessionId,
                    type,
                    root,
                    discovered);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to enumerate existing artifacts for session {SessionId}", _sessionId);
            }
        }

        private void OnArtifactWatcherEvent(string type, string root, WatcherChangeTypes changeType, string fullPath, string? oldFullPath = null)
        {
            try
            {
                long? bytes = null;
                var exists = File.Exists(fullPath);
                if (exists)
                {
                    var info = new FileInfo(fullPath);
                    if (info.Exists)
                    {
                        bytes = info.Length;
                    }
                }

                _logger.LogDebug(
                    "Artifact watcher event session={SessionId} type={Type} root={Root} event={Event} path={Path} oldPath={OldPath} exists={Exists} bytes={Bytes}",
                    _sessionId,
                    type,
                    root,
                    changeType,
                    fullPath,
                    oldFullPath,
                    exists,
                    bytes);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to inspect watcher event for session {SessionId}", _sessionId);
            }

            if (changeType is WatcherChangeTypes.Created or WatcherChangeTypes.Renamed &&
                SealedStorageProbe.ReadEntryStatus(fullPath) is { IsDirectory: true, IsSymbolicLink: false })
            {
                // A new or renamed-in folder: files written in it before the watcher took it up
                // (or that came with it) raise no event of their own.
                QueueFilesBelow(type, fullPath);
                return;
            }

            OnFileEvent(type, fullPath, $"watcher:{changeType}");
        }

        private void QueueFilesBelow(string type, string directory)
        {
            try
            {
                foreach (var file in EnumerateArtifactFiles(directory))
                {
                    OnFileEvent(type, file, "watcher:new-folder");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // What could not be listed is left to the next refresh scan.
                _logger.LogDebug(ex, "Could not list a new artifact folder session={SessionId} type={Type}", _sessionId, type);
            }
        }

        private bool TryGetArtifactRootType(string? path, out string type)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                type = string.Empty;
                return false;
            }

            var normalized = NormalizePath(path ?? string.Empty);
            if (string.Equals(normalized, NormalizePath(_paths.OutputDirectory), StringComparison.Ordinal))
            {
                type = "output";
                return true;
            }

            if (string.Equals(normalized, NormalizePath(_paths.TempDirectory), StringComparison.Ordinal))
            {
                type = "temp";
                return true;
            }

            type = string.Empty;
            return false;
        }

        private string? GetArtifactRootPath(string type)
        {
            if (type.Equals("output", StringComparison.OrdinalIgnoreCase))
            {
                return _paths.OutputDirectory;
            }

            if (type.Equals("temp", StringComparison.OrdinalIgnoreCase))
            {
                return _paths.TempDirectory;
            }

            return null;
        }

        private bool ArtifactRootExists(string type)
        {
            var root = GetArtifactRootPath(type);
            return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);
        }

        private static void ReplaceWatcher(ref FileSystemWatcher? slot, FileSystemWatcher watcher)
        {
            var previous = slot;
            slot = watcher;

            if (previous is null)
            {
                return;
            }

            try
            {
                previous.EnableRaisingEvents = false;
                previous.Dispose();
            }
            catch
            {
            }
        }

        private async Task<bool> EnsureCatalogAsync(CancellationToken cancellationToken, bool forceReload = false, bool pruneMissing = true)
        {
            if (_catalogLoaded && !forceReload)
            {
                return true;
            }

            var editorSid = _editorSid;
            if (string.IsNullOrWhiteSpace(editorSid))
            {
                return false;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            var token = linked.Token;

            try
            {
                var request = new RunnerArtifactServiceListArtifactsRequest
                {
                    EditorSid = editorSid,
                    Requester = _readRequester.Clone()
                };

                var response = await _artifactClient.ListArtifactsAsync(request, headers: _authHeaders, cancellationToken: token).ConfigureAwait(false);
                var currentPaths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in response.Artifacts)
                {
                    var appliedPath = ApplyCatalogEntry(item, editorSid);
                    if (!string.IsNullOrWhiteSpace(appliedPath))
                    {
                        currentPaths.Add(appliedPath);
                    }
                }

                if (pruneMissing)
                {
                    PruneCatalogEntries(currentPaths);
                }
                _catalogLoaded = true;
                return true;
            }
            catch (RpcException ex)
            {
                if (ex.StatusCode == StatusCode.PermissionDenied)
                {
                    _logger.LogWarning("Artifact catalog unauthorized for session {SessionId}", _sessionId);
                }
                else
                {
                    _logger.LogDebug(ex, "Failed to fetch artifact catalog for session {SessionId}", _sessionId);
                }

                return false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unexpected error fetching artifact catalog for session {SessionId}", _sessionId);
                return false;
            }
        }

        private bool SeedInitialCatalog(string editorSid, IEnumerable<EditorArtifactMetadata>? artifacts)
        {
            if (artifacts is null)
            {
                return false;
            }

            var currentPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in artifacts)
            {
                var appliedPath = ApplyCatalogEntry(item, editorSid);
                if (!string.IsNullOrWhiteSpace(appliedPath))
                {
                    currentPaths.Add(appliedPath);
                }
            }

            if (currentPaths.Count == 0)
            {
                return false;
            }

            PruneCatalogEntries(currentPaths);
            return true;
        }

        private string? ApplyCatalogEntry(EditorArtifactMetadata item, string activeEditorSid)
        {
            var localPath = ResolveLocalPath(item.Type, item.Subfolder, item.Filename);
            if (localPath is null)
            {
                return null;
            }

            var normalized = NormalizePath(localPath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (string.Equals(item.Type, "input", StringComparison.OrdinalIgnoreCase))
            {
                _inputSeeds.TryRemove(normalized, out _);
            }

            var createdUtc = ToDateTimeOffset(item.CreatedUtc);
            // Under the eviction gate: a pass must not replace the file between this entry judging
            // it downloaded and taking the place of the one the pass is evicting.
            lock (_evictionGate)
            {
                _catalog.TryGetValue(normalized, out var previousState);
                var state = new RemoteArtifactState(
                    activeEditorSid,
                    item.Type,
                    item.Subfolder,
                    item.Filename,
                    normalized,
                    item.SizeBytes,
                    item.Sha256,
                    createdUtc);
                _catalog[normalized] = state;

                try
                {
                    EnsurePlaceholder(state, previousState);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to prepare placeholder for artifact {Path}", normalized);
                }
            }

            return normalized;
        }

        private void PruneCatalogEntries(HashSet<string> currentPaths)
        {
            // Under the eviction gate, as every catalog replacement: a pass must not empty a file
            // whose entry, the only way to download it again, is being removed.
            lock (_evictionGate)
            {
                foreach (var (path, _) in _catalog)
                {
                    if (!currentPaths.Contains(path))
                    {
                        _catalog.TryRemove(path, out _);
                    }
                }
            }
        }

        private bool TryGetCatalogEntry(string path, out RemoteArtifactState? state)
        {
            var normalized = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                state = null;
                return false;
            }

            return _catalog.TryGetValue(normalized, out state);
        }

        private bool TryResolveArtifactPath(string path, out string type, out string subfolder, out string filename)
        {
            var candidates = new[]
            {
                ("input", _paths.InputDirectory),
                ("output", _paths.OutputDirectory),
                ("temp", _paths.TempDirectory)
            }
                .Where(static candidate => !string.IsNullOrWhiteSpace(candidate.Item2))
                .OrderByDescending(static candidate => NormalizePath(candidate.Item2!).Length);

            foreach (var (candidateType, root) in candidates)
            {
                if (TryResolveArtifactPath(path, root, out subfolder, out filename))
                {
                    type = candidateType;
                    return true;
                }
            }

            type = string.Empty;
            subfolder = string.Empty;
            filename = string.Empty;
            return false;
        }

        private static bool ShouldPreserveUncatalogedLocalFileBeforeCatalogRefresh(string type, string path)
        {
            return !type.Equals("input", StringComparison.OrdinalIgnoreCase)
                   && HasMaterializedLocalFile(path);
        }

        private bool ShouldPreserveUncatalogedLocalFileAfterCatalogRefresh(string type, string path)
            => (!type.Equals("input", StringComparison.OrdinalIgnoreCase) || _inputSeeds.ContainsKey(NormalizePath(path)))
               && HasMaterializedLocalFile(path);

        private void ClearUncatalogedInputFile(string type, string path)
        {
            if (!type.Equals("input", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                if (!TryResolveSecureLocation(type, path, out var root, out var relativePath))
                {
                    return;
                }

                using var secureRoot = new LinuxSecureDirectoryRoot(root);
                secureRoot.TruncateRegularFile(relativePath, modifiedUtc: null);
            }
            catch
            {
                // The VFS caller still receives a failed ensure and will not read stale bytes.
            }
        }

        private static bool HasMaterializedLocalFile(string path)
        {
            try
            {
                var fileInfo = new FileInfo(path);
                return fileInfo.Exists && fileInfo.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public void CaptureInputSeeds()
        {
            var root = _paths.InputDirectory;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (HasMaterializedLocalFile(file))
                    {
                        _inputSeeds.TryAdd(NormalizePath(file), 0);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to enumerate input seed files for session {SessionId}", _sessionId);
            }
        }

        private static bool TryResolveArtifactPath(string path, string? root, out string subfolder, out string filename)
        {
            subfolder = string.Empty;
            filename = string.Empty;

            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            string relative;
            try
            {
                relative = Path.GetRelativePath(NormalizePath(root), NormalizePath(path));
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(relative) || relative == "." || Path.IsPathFullyQualified(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            {
                return false;
            }

            var normalizedRelative = relative.Replace('\\', '/');
            var parts = normalizedRelative.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || parts.Any(static part => part is "." or ".."))
            {
                return false;
            }

            filename = parts[^1];
            if (string.IsNullOrWhiteSpace(filename))
            {
                return false;
            }

            subfolder = parts.Length == 1 ? string.Empty : string.Join('/', parts[..^1]);
            return true;
        }

        private string? GetArtifactTypeRoot(string type)
            => type.ToLowerInvariant() switch
            {
                "input" => _paths.InputDirectory,
                "output" => _paths.OutputDirectory,
                "temp" => _paths.TempDirectory,
                _ => null
            };

        /// <summary>
        /// Maps a session artifact path to its type root and a relative path so file
        /// operations can run through <see cref="LinuxSecureDirectoryRoot"/>.
        /// </summary>
        private bool TryResolveSecureLocation(string type, string path, out string root, out string relativePath)
        {
            root = GetArtifactTypeRoot(type) ?? string.Empty;
            relativePath = string.Empty;
            if (!TryResolveArtifactPath(path, root, out var subfolder, out var filename))
            {
                return false;
            }

            relativePath = subfolder.Length == 0 ? filename : $"{subfolder}/{filename}";
            return true;
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private static DateTimeOffset ToDateTimeOffset(Timestamp? timestamp)
        {
            if (timestamp is null || (timestamp.Seconds == 0 && timestamp.Nanos == 0))
            {
                return DateTimeOffset.UtcNow;
            }

            return timestamp.ToDateTimeOffset();
        }

        private string? ResolveLocalPath(string type, string? subfolder, string filename)
        {
            var root = GetArtifactTypeRoot(type);

            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(filename))
            {
                return null;
            }

            if (!SessionArtifactPaths.TryResolve(root, subfolder, filename, out var localPath, out _))
            {
                _logger.LogWarning(
                    "Ignoring artifact with an unsafe name session={SessionId} type={Type} subfolder={Subfolder} filename={Filename}",
                    _sessionId,
                    type,
                    subfolder,
                    filename);
                return null;
            }

            return localPath;
        }

        private void EnsurePlaceholder(RemoteArtifactState state, RemoteArtifactState? previousState)
        {
            if (!TryResolveSecureLocation(state.Type, state.LocalPath, out var root, out var relativePath))
            {
                throw new InvalidOperationException($"Artifact path is outside its session root: '{state.LocalPath}'.");
            }

            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            var status = secureRoot.GetStatus(relativePath);
            switch (status.Kind)
            {
                case SecureEntryKind.Missing:
                    secureRoot.EnsurePlaceholder(relativePath, state.CreatedUtc.UtcDateTime);
                    state.MarkPlaceholder(secureRoot.GetVersion(relativePath));
                    return;
                case SecureEntryKind.Other:
                    throw new UnauthorizedAccessException($"Artifact path is not a regular file: '{state.LocalPath}'.");
            }

            if (status.Length > 0 && ShouldKeepExistingArtifactFile(state, previousState, status.Length, secureRoot, relativePath))
            {
                // A kept output may hold newer bytes than the catalog's: only a file known to equal
                // the remote artifact (the version this uploader sent or fetched, or the catalog's
                // digest) counts as saved or may be evicted.
                var version = secureRoot.GetVersion(relativePath);
                var knownRemote = version is { } current &&
                                  (previousState?.MatchesLocalFile(current) == true ||
                                   (!string.IsNullOrWhiteSpace(state.Sha256) &&
                                    ExistingFileMatchesCatalogMetadata(state, status.Length, secureRoot, relativePath)));
                state.MarkDownloaded(knownRemote ? version : null);
                return;
            }

            secureRoot.TruncateRegularFile(relativePath, state.CreatedUtc.UtcDateTime);
            state.MarkPlaceholder(secureRoot.GetVersion(relativePath));
        }

        private static bool ShouldKeepExistingArtifactFile(
            RemoteArtifactState state,
            RemoteArtifactState? previousState,
            long length,
            LinuxSecureDirectoryRoot secureRoot,
            string relativePath)
        {
            if (!string.Equals(state.Type, "input", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return previousState is { Downloaded: true }
                   && previousState.HasSameCatalogGeneration(state)
                   && ExistingFileMatchesCatalogMetadata(state, length, secureRoot, relativePath);
        }

        private static bool ExistingFileMatchesCatalogMetadata(
            RemoteArtifactState state,
            long length,
            LinuxSecureDirectoryRoot secureRoot,
            string relativePath)
        {
            if (state.SizeBytes.HasValue && length != state.SizeBytes.Value)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(state.Sha256))
            {
                try
                {
                    using var stream = secureRoot.OpenRegularFileForRead(relativePath);
                    if (stream is null)
                    {
                        return false;
                    }

                    var actualSha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                    return string.Equals(actualSha, state.Sha256, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        private async Task DownloadArtifactAsync(RemoteArtifactState state, bool highPriority, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            var token = linked.Token;
            _ = highPriority;

            try
            {
                using var call = _artifactClient.ReadArtifact(
                    new RunnerArtifactServiceReadArtifactRequest
                    {
                        EditorSid = state.EditorSid,
                        Filename = state.Filename,
                        Type = state.Type,
                        Subfolder = state.Subfolder,
                        ReaderKey = _paths.TransferSealing?.ReaderKeyMessage,
                        Requester = _readRequester.Clone(),
                    },
                    headers: _authHeaders,
                    cancellationToken: token);

                var published = await FetchToFileAsync(state, call.ResponseStream, token).ConfigureAwait(false);
                lock (_evictionGate)
                {
                    state.MarkDownloaded(published);
                    state.MarkServed(DateTimeOffset.UtcNow);
                }
            }
            catch (RpcException ex) when (!token.IsCancellationRequested)
            {
                throw new InvalidOperationException($"Artifact download request failed: {ex.Status.Detail}", ex);
            }
        }

        /// <summary>Downloads and publishes the artifact; returns the published file's version.</summary>
        private async Task<SecureFileVersion?> FetchToFileAsync(RemoteArtifactState state, IAsyncStreamReader<RunnerArtifactServiceReadArtifactResponse> responseStream, CancellationToken cancellationToken)
        {
            if (!TryResolveSecureLocation(state.Type, state.LocalPath, out var root, out var relativePath))
            {
                throw new InvalidOperationException($"Artifact path is outside its session root: '{state.LocalPath}'.");
            }

            // Write and publish relative to a pinned directory descriptor so that a
            // swapped symlink in the session tree cannot redirect the root-owned write.
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            // The placeholder (or whatever) the download is to replace, as it is now: a write by
            // the session while it runs is newer than the remote bytes and must stay.
            var replacing = secureRoot.GetVersion(relativePath);
            using (var publication = secureRoot.CreatePublication(relativePath))
            {
                CarryOwnership(publication, state.LocalPath);
                long bytesWritten = 0;
                long? expectedSizeBytes = null;
                string? expectedSha256 = null;
                using var sinkSlot = new SealedReadSinkSlot();
                await using (var destination = publication.OpenWriteStream())
                using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var sawMetadata = false;
                    while (await responseStream.MoveNext(cancellationToken).ConfigureAwait(false))
                    {
                        var response = responseStream.Current;
                        switch (response.PayloadCase)
                        {
                            case RunnerArtifactServiceReadArtifactResponse.PayloadOneofCase.Metadata:
                                sawMetadata = true;
                                var metadata = response.Metadata;
                                expectedSizeBytes = metadata.SizeBytes;
                                if (expectedSizeBytes > SealedReadSink.WireLimit(metadata.Envelope, MaxUploadBytes))
                                {
                                    throw new InvalidOperationException(
                                        $"Artifact declares {expectedSizeBytes} bytes, above the {MaxUploadBytes} byte limit.");
                                }

                                expectedSha256 = string.IsNullOrWhiteSpace(metadata.Sha256) ? null : metadata.Sha256;
                                var sink = sinkSlot.Begin(SealedReadSink.Open(
                                    destination,
                                    metadata.Envelope,
                                    _paths.TransferSealing,
                                    () => new SealedArtifactIdentity(_sessionId, state.EditorSid, state.Type, state.Subfolder, state.Filename)));
                                // The envelope format caps this too; the artifact limit is checked
                                // here so it never depends on that.
                                if (sink.SealedPlaintextSize > MaxUploadBytes)
                                {
                                    throw new InvalidOperationException(
                                        $"Artifact declares {sink.SealedPlaintextSize} bytes, above the {MaxUploadBytes} byte limit.");
                                }
                                // The catalog records the local file's size: the plaintext a sealed
                                // header declares, not its ciphertext on the wire.
                                state.UpdateMetadata(
                                    sink.SealedPlaintextSize ?? expectedSizeBytes,
                                    sink.IsSealed ? null : expectedSha256,
                                    ToDateTimeOffset(metadata.ModifiedUtc));
                                break;
                            case RunnerArtifactServiceReadArtifactResponse.PayloadOneofCase.Chunk:
                                if (!sawMetadata)
                                {
                                    throw new InvalidOperationException("Artifact stream missing metadata frame.");
                                }

                                // Bound the write as it happens, so a stream larger than declared
                                // (or unbounded, when no size is declared) cannot fill the disk.
                                var limit = expectedSizeBytes ?? sinkSlot.Current.SealedCiphertextSize ?? MaxUploadBytes;
                                if (bytesWritten + response.Chunk.Length > limit)
                                {
                                    throw new InvalidOperationException(
                                        $"Artifact stream exceeds its {(expectedSizeBytes.HasValue ? "declared size" : "size limit")} of {limit} bytes.");
                                }

                                await sinkSlot.Current.WriteAsync(response.Chunk.Memory, cancellationToken).ConfigureAwait(false);
                                hasher.AppendData(response.Chunk.Memory.Span);
                                bytesWritten += response.Chunk.Length;
                                break;
                        }
                    }

                    if (!sawMetadata)
                    {
                        throw new InvalidOperationException("Artifact stream completed without metadata.");
                    }

                    if (expectedSizeBytes.HasValue && bytesWritten != expectedSizeBytes.Value)
                    {
                        throw new InvalidOperationException($"Artifact stream size mismatch: expected {expectedSizeBytes.Value} bytes but received {bytesWritten} bytes.");
                    }

                    if (!string.IsNullOrWhiteSpace(expectedSha256))
                    {
                        var actualSha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException("Artifact stream SHA-256 mismatch.");
                        }
                    }

                    // A sealed payload is usable only once its final chunk has verified.
                    await sinkSlot.Current.CompleteAsync(cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    publication.SetModifiedUtc(state.CreatedUtc.UtcDateTime);
                }
                catch
                {
                    // ignore timestamp failures
                }

                // Disposing an uncommitted publication removes its temporary file. The placeholder is
                // replaced only while no process has it open: a writer that opened it before its
                // first write would otherwise go on with an inode nobody can reach. The shim asks
                // for the download before it opens the file itself.
                if (!(replacing is { } expected ? publication.CommitReplacing(expected, whileUnopened: true) : publication.CommitIfAbsent()))
                {
                    // Still the placeholder, only open elsewhere: not downloaded, so a later open
                    // asks again.
                    if (replacing is { } placeholder && secureRoot.GetVersion(relativePath) is { } current && current.IsSameFileAs(placeholder))
                    {
                        throw new PlaceholderInUseException();
                    }

                    // The session wrote or created the file meanwhile: it stays, and its watcher
                    // uploads it.
                    return null;
                }
            }

            return secureRoot.GetVersion(relativePath);
        }

        private void OnFileEvent(string type, string fullPath, string source)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return;
            try
            {
                if (!TryResolveSecureLocation(type, fullPath, out var root, out var relativePath))
                {
                    return;
                }

                // Inspect without following symlinks: only plain regular files in the
                // session tree are uploaded; links, FIFOs, devices and directories are not.
                SecureFileStatus status;
                using (var secureRoot = new LinuxSecureDirectoryRoot(root))
                {
                    status = secureRoot.GetStatus(relativePath);
                }

                if (status.Kind == SecureEntryKind.Missing)
                {
                    return;
                }

                if (!status.IsRegularFile)
                {
                    _logger.LogDebug(
                        "Skipping non-regular artifact path session={SessionId} type={Type} path={Path}",
                        _sessionId,
                        type,
                        fullPath);
                    return;
                }

                if (TryGetCatalogEntry(fullPath, out var knownState) && ShouldSkipCatalogBackedUpload(status, knownState))
                {
                    return;
                }

                if (status.Length > MaxUploadBytes)
                {
                    _logger.LogWarning(
                        "Skipping artifact larger than the upload limit session={SessionId} type={Type} path={Path} bytes={Bytes} limit={Limit}",
                        _sessionId,
                        type,
                        fullPath,
                        status.Length,
                        MaxUploadBytes);
                    return;
                }

                // Session code decides how many files appear; past the cap new files wait
                // for the next refresh scan instead of growing the agent's memory.
                if (_pending.Count >= MaxPendingUploads)
                {
                    NoteDroppedUpload(fullPath);
                    return;
                }

                if (!_pending.TryAdd(fullPath, new ArtifactUploadBudget(_uploadPolicy)))
                {
                    return;
                }

                _logger.LogInformation(
                    "Queued artifact upload session={SessionId} type={Type} source={Source} path={Path} bytes={Bytes}",
                    _sessionId,
                    type,
                    source,
                    fullPath,
                    status.Length);

                var request = new UploadRequest(type, fullPath, 0, CreatedFor);
                if (!_queue.Writer.TryWrite(request))
                {
                    _pending.TryRemove(fullPath, out _);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to queue artifact for session {SessionId}", _sessionId);
            }
        }

        internal int PendingUploadCount => _pending.Count;

        /// <summary>
        /// Artifacts on disk that matched no uploaded version when the uploader stopped, whether their
        /// upload failed, was still pending or never queued (a full queue drops files).
        /// </summary>
        public int UnsavedUploadCount { get; private set; }

        /// <summary>Whether an artifact folder could not be read, so <see cref="UnsavedUploadCount"/> may be short.</summary>
        public bool UnsavedCountIncomplete { get; private set; }

        private int CountUnsavedArtifacts()
        {
            var unsaved = 0;
            var unsendable = new HashSet<(ulong Device, ulong Inode)>();
            foreach (var (type, root) in new[] { ("output", _paths.OutputDirectory), ("temp", _paths.TempDirectory) })
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    continue;
                }

                try
                {
                    using var secureRoot = new LinuxSecureDirectoryRoot(root);
                    foreach (var file in EnumerateArtifactFiles(root))
                    {
                        if (!TryResolveSecureLocation(type, file, out _, out var relativePath))
                        {
                            continue;
                        }

                        if (secureRoot.GetVersion(relativePath) is not { } version)
                        {
                            // A regular file the uploader refuses (a second hard link, say) still
                            // holds session data the control plane never got; once per inode.
                            if (SealedStorageProbe.ReadEntryStatus(file) is { IsRegularFile: true, IsSymbolicLink: false } &&
                                LinuxFileIdentityReader.TryRead(file, out var identity) &&
                                unsendable.Add((identity.DeviceId, identity.Inode)))
                            {
                                unsaved++;
                            }

                            continue;
                        }

                        // Saved means the control plane holds this exact file, or it is the empty
                        // placeholder the agent left for a remote artifact. An empty rewrite by the
                        // editor is neither.
                        if (!_catalog.TryGetValue(NormalizePath(file), out var state) || !state.AccountsFor(version))
                        {
                            unsaved++;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Files past this point went uncounted: the session must not be reported as
                    // fully saved on a partial count.
                    unsaved++;
                    UnsavedCountIncomplete = true;
                    _logger.LogWarning(ex, "Could not count unsaved artifacts session={SessionId} type={Type}", _sessionId, type);
                }
            }

            return unsaved;
        }

        public bool IsSealed => _paths.IsSealed;

        internal void RefreshRecordedVersions()
        {
            lock (_evictionGate)
            {
                foreach (var state in _catalog.Values)
                {
                    if ((state.LocalVersion is null && state.PlaceholderVersion is null) ||
                        !TryResolveSecureLocation(state.Type, state.LocalPath, out var root, out var relativePath))
                    {
                        continue;
                    }

                    try
                    {
                        using var secureRoot = new LinuxSecureDirectoryRoot(root);
                        state.RefreshVersion(secureRoot.GetVersion(relativePath));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        state.RefreshVersion(null);
                    }
                }
            }
        }

        private readonly object _evictionGate = new();

        private readonly struct EvictionGateScope : IDisposable
        {
            private readonly object _gate;

            public EvictionGateScope(object gate)
            {
                _gate = gate;
                Monitor.Enter(gate);
            }

            public void Dispose() => Monitor.Exit(_gate);
        }

        /// <summary>
        /// Gives a publication the owner of the file it replaces when that is not root: the
        /// session's own output stays writable to it after an eviction or a download again, since
        /// the shim does not intercept opens for writing.
        /// </summary>
        private static void CarryOwnership(LinuxSecureDirectoryRoot.PendingPublication publication, string path)
        {
            if (SealedStorageProbe.ReadEntryStatus(path) is { IsRegularFile: true, IsSymbolicLink: false, OwnerId: not 0 } current)
            {
                publication.SetOwner(current.OwnerId, current.GroupId);
            }
        }

        /// <summary>Runs before each eviction candidate takes the gate; lets tests change the catalog then.</summary>
        internal Action<string>? BeforeEvictionCandidate { get; set; }

        internal long EvictUploadedArtifacts(long bytesToFree, TimeSpan minimumIdle, DateTimeOffset now)
        {
            long released = 0;
            // The pass stops on the space that actually came back, not on the lengths it replaced.
            var freeBefore = ReadSealedFreeBytes();
            var candidates = _catalog.Values
                .Where(state => state.LocalVersion is { Status.Length: > 0 } &&
                                state.IdleSince(now) >= minimumIdle &&
                                RunnerVfsEnvironmentBuilder.IsArtifactReadbackPath(state.LocalPath))
                .OrderBy(static state => state.CreatedUtc)
                .ToArray();
            foreach (var state in candidates)
            {
                if (_cts.IsCancellationRequested)
                {
                    break;
                }

                if (_pending.ContainsKey(state.LocalPath) ||
                    !TryResolveSecureLocation(state.Type, state.LocalPath, out var root, out var relativePath))
                {
                    continue;
                }

                try
                {
                    BeforeEvictionCandidate?.Invoke(state.LocalPath);
                    // Excludes the VFS serving this file, and catalog updates, while it is checked
                    // and replaced.
                    using var gate = new EvictionGateScope(_evictionGate);
                    // A refresh or upload may have replaced this entry since the pass began: the
                    // current one then decides, and judged the file itself.
                    if (!_catalog.TryGetValue(state.LocalPath, out var current) || !ReferenceEquals(current, state) ||
                        state.IdleSince(DateTimeOffset.UtcNow) < minimumIdle)
                    {
                        continue;
                    }

                    using var secureRoot = new LinuxSecureDirectoryRoot(root);
                    // A rewrite of the same length is a different version: only the one the control
                    // plane holds may be replaced, since opening the placeholder downloads that one.
                    if (secureRoot.GetVersion(relativePath) is not { } version || !state.MatchesLocalFile(version))
                    {
                        continue;
                    }

                    var status = version.Status;

                    using var placeholder = secureRoot.CreatePublication(relativePath);
                    placeholder.SetModifiedUtc(status.LastWriteTimeUtc);
                    CarryOwnership(placeholder, state.LocalPath);
                    // Swapped in atomically and kept only if it displaced exactly that version,
                    // which no process had open, mapped or opened meanwhile: one holding it, a
                    // writer above all, would go on with an inode nobody can reach again.
                    if (!placeholder.CommitReplacing(version, whileUnopened: true))
                    {
                        continue;
                    }

                    state.MarkEvicted(secureRoot.GetVersion(relativePath));
                    released += status.Length;
                    var freed = freeBefore is { } before && ReadSealedFreeBytes() is { } after ? after - before : released;
                    if (freed >= bytesToFree)
                    {
                        break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Could not evict artifact session={SessionId} type={Type}", _sessionId, state.Type);
                }
            }

            if (released > 0)
            {
                _logger.LogInformation("Evicted uploaded artifacts session={SessionId} bytes={Bytes}", _sessionId, released);
            }

            return released;
        }

        private long? ReadSealedFreeBytes()
            => _paths.SealedDirectory is { } directory && SealedStorageProbe.ReadFileSystemStats(directory) is { } stats
                ? stats.AvailableBlocks * stats.BlockSize
                : null;

        /// <summary>
        /// Rescans the artifact folders once the queue has drained to half its cap after
        /// files were dropped, so files that arrived while it was full are still uploaded.
        /// </summary>
        private void RescanIfOwed()
        {
            if (Volatile.Read(ref _rescanOwed) == 0 ||
                _pending.Count > MaxPendingUploads / 2 ||
                Interlocked.Exchange(ref _rescanOwed, 0) == 0)
            {
                return;
            }

            _logger.LogInformation(
                "Rescanning artifacts after the upload queue drained session={SessionId}",
                _sessionId);
            EnqueueExistingFiles(_paths.OutputDirectory, "output");
            EnqueueExistingFiles(_paths.TempDirectory, "temp");
        }

        private void NoteDroppedUpload(string fullPath)
        {
            // Dropped files may get no further filesystem event, so the queue owes a rescan.
            Volatile.Write(ref _rescanOwed, 1);
            var dropped = Interlocked.Increment(ref _droppedUploads);
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastDropWarningTicks);
            if (now - last < DropWarningInterval.TotalMilliseconds ||
                Interlocked.CompareExchange(ref _lastDropWarningTicks, now, last) != last)
            {
                return;
            }

            _logger.LogWarning(
                "Artifact upload queue is full session={SessionId} limit={Limit} dropped={Dropped} latest={Path}; dropped files are retried on the next scan",
                _sessionId,
                MaxPendingUploads,
                dropped,
                fullPath);
        }

        private static bool ShouldSkipCatalogBackedUpload(SecureFileStatus status, RemoteArtifactState? knownState)
        {
            if (knownState is null)
            {
                return false;
            }

            // Ignore catalog placeholders (0-byte files) and only upload once the local file has real content.
            if (status.Length <= 0)
            {
                return true;
            }

            if (!knownState.Downloaded)
            {
                return false;
            }

            var knownSize = knownState.SizeBytes;
            if (!knownSize.HasValue || knownSize.Value != status.Length)
            {
                return false;
            }

            // If size and mtime still match catalog metadata, nothing changed locally.
            return status.LastWriteTimeUtc <= knownState.CreatedUtc.UtcDateTime;
        }

        private async Task ProcessQueueAsync()
        {
            try
            {
                while (await _queue.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    while (_queue.Reader.TryRead(out var item))
                    {
                        // A retried item keeps its pending entry, so the queue never holds
                        // more items than the pending cap admits.
                        UploadRequest? retry = null;
                        try
                        {
                            retry = await UploadAsync(item).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            if (item.Attempt < 3 && !_pending[item.FullPath].IsExpired)
                            {
                                retry = item with { Attempt = item.Attempt + 1 };
                                _logger.LogDebug(ex, "Retrying artifact upload {Path} attempt {Attempt}", item.FullPath, item.Attempt + 1);
                                await Task.Delay(TimeSpan.FromMilliseconds(200 * (item.Attempt + 1)), _cts.Token).ConfigureAwait(false);
                            }
                            else
                            {
                                _logger.LogWarning(ex, "Failed to upload artifact {Path} after retries", item.FullPath);
                            }
                        }
                        finally
                        {
                            if (retry is not { } next || !_queue.Writer.TryWrite(next))
                            {
                                _pending.TryRemove(item.FullPath, out _);
                            }
                        }

                        RescanIfOwed();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task<UploadRequest?> UploadAsync(UploadRequest request)
        {
            using var budget = _pending[request.FullPath].BeginAttempt(_cts.Token);
            try
            {
                return await UploadWithinBudgetAsync(request, budget).ConfigureAwait(false);
            }
            catch (Exception error) when (!_cts.IsCancellationRequested && budget.IsTimedOut)
            {
                // An expired observation cannot tell whether the server committed. A later
                // retry/rescan keeps the unchanged snapshot ID and Stop still counts it.
                throw new TimeoutException($"Artifact upload exceeded its {budget.ExpiredAllowance} allowance.", error);
            }
        }

        private async Task<UploadRequest?> UploadWithinBudgetAsync(UploadRequest request, ArtifactUploadAttempt budget)
        {
            var (type, fullPath, attempt, runtime) = request;
            if (!type.Equals("output", StringComparison.OrdinalIgnoreCase) &&
                !type.Equals("temp", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!TryResolveSecureLocation(type, fullPath, out var root, out var relPath))
            {
                return null;
            }

            await WaitForFileStableAsync(root, relPath, budget.Token).ConfigureAwait(false);

            var separatorIndex = relPath.LastIndexOf('/');
            var fileName = separatorIndex < 0 ? relPath : relPath[(separatorIndex + 1)..];
            var subfolder = separatorIndex < 0 ? string.Empty : relPath[..separatorIndex];

            for (var tries = 0; tries < 5; tries++)
            {
                budget.Token.ThrowIfCancellationRequested();
                budget.BeginMetadata();
                try
                {
                    if (string.IsNullOrWhiteSpace(_editorSid))
                    {
                        await Task.Delay(InitialRetryDelay * (tries + 1), budget.Token).ConfigureAwait(false);
                        return request.Attempt < 3 ? request with { Attempt = request.Attempt + 1 } : null;
                    }

                    // Open without following symlinks or blocking on FIFOs; the agent
                    // runs as root and must only read plain files from the session tree.
                    FileStream? opened;
                    using (var secureRoot = new LinuxSecureDirectoryRoot(root))
                    {
                        opened = secureRoot.OpenRegularFileForRead(relPath);
                    }

                    if (opened is null)
                    {
                        return null;
                    }

                    await using var stream = opened;
                    var sizeBytes = stream.Length;
                    var snapshotWriteUtc = LinuxSecureDirectoryRoot.GetOpenFileStatus(stream.SafeFileHandle, relPath).LastWriteTimeUtc;
                    var snapshotVersion = LinuxSecureDirectoryRoot.GetOpenFileVersion(stream.SafeFileHandle, relPath)
                        ?? throw new IOException("Cannot capture the opened artifact file version.");
                    if (sizeBytes > MaxUploadBytes)
                    {
                        _logger.LogWarning(
                            "Skipping artifact larger than the upload limit session={SessionId} type={Type} path={Relative} bytes={Bytes} limit={Limit}",
                            _sessionId,
                            type,
                            relPath,
                            sizeBytes,
                            MaxUploadBytes);
                        return null;
                    }

                    var contentType = ResolveContentType(fileName);

                    var metadata = new RunnerArtifactUploadMetadata
                    {
                        RunnerId = _readRequester.RunnerId,
                        InstanceId = _readRequester.InstanceId,
                        EditorSid = _editorSid,
                        Filename = fileName,
                        Type = type,
                        Subfolder = subfolder,
                        ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType,
                        SizeBytes = sizeBytes
                    };

                    if (!string.IsNullOrWhiteSpace(_owner))
                    {
                        metadata.Owner = _owner;
                    }
                    if (!string.IsNullOrWhiteSpace(_sessionId))
                    {
                        metadata.SessionId = _sessionId;
                    }
                    // The runtime captured when the file was queued, never the session's
                    // current one: that may since have exited or been replaced.
                    if (runtime is { IsValid: true } queuedFor)
                    {
                        metadata.LifecycleGeneration = queuedFor.LifecycleGeneration;
                        metadata.RuntimeEpoch = queuedFor.RuntimeEpoch;
                        metadata.RuntimeInstanceId = queuedFor.RuntimeInstanceId;
                    }

                    // Stable through transport/I/O retries and later rescans of this same
                    // secure file version, even after the queue's retry budget is exhausted.
                    metadata.UploadId = _uploadOperationIdentity.ForSnapshot(metadata, snapshotVersion);

                    // Every attempt seals afresh; a fresh envelope is still the same logical
                    // snapshot. No plaintext hash is exposed as its operation identity.
                    using var writer = _paths.TransferSealing?.OutputRecipient is { } recipient
                        ? SealedEnvelopeWriter.Create(
                            recipient,
                            new SealedArtifactIdentity(_sessionId, _editorSid, type, subfolder, fileName),
                            sizeBytes)
                        : null;
                    if (writer is not null)
                    {
                        metadata.Envelope = SessionTransferSealing.ToMessage(writer.Envelope);
                    }
                    budget.BindSnapshot(writer?.CiphertextSize ?? sizeBytes);

                    _logger.LogInformation(
                        "Uploading artifact session={SessionId} editor={EditorSid} type={Type} path={Relative} bytes={Size}",
                        _sessionId,
                        _editorSid,
                        type,
                        relPath,
                        sizeBytes);

                    // Disposed on every path, so an upload that fails midway (a sealed file that shrank) cancels its RPC.
                    using var call = _artifactClient.UploadArtifact(headers: _authHeaders, cancellationToken: budget.Token);
                    await call.RequestStream.WriteAsync(new RunnerArtifactServiceUploadArtifactRequest { Metadata = metadata }).ConfigureAwait(false);
                    budget.BeginBody();

                    if (writer is not null)
                    {
                        // Seal exactly the size declared in the header, even if the file grows.
                        await using var limited = new LengthLimitedReadStream(stream, sizeBytes);
                        await foreach (var sealedChunk in writer.EncryptChunksAsync(limited, budget.Token).ConfigureAwait(false))
                        {
                            await call.RequestStream.WriteAsync(new RunnerArtifactServiceUploadArtifactRequest
                            {
                                Chunk = Google.Protobuf.ByteString.CopyFrom(sealedChunk.Span)
                            }).ConfigureAwait(false);
                            budget.BodyWritten(sealedChunk.Length);
                        }
                    }

                    var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                    try
                    {
                        // Send at most the size declared in the metadata, even if the file grows.
                        var remaining = writer is null ? sizeBytes : 0;
                        int read;
                        while (remaining > 0 &&
                               (read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), budget.Token).ConfigureAwait(false)) > 0)
                        {
                            remaining -= read;
                            await call.RequestStream.WriteAsync(new RunnerArtifactServiceUploadArtifactRequest
                            {
                                Chunk = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read)
                            }).ConfigureAwait(false);
                            budget.BodyWritten(read);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    budget.BeginReceipt();
                    await call.RequestStream.CompleteAsync().ConfigureAwait(false);
                    var response = await call.ResponseAsync.ConfigureAwait(false);
                    budget.Token.ThrowIfCancellationRequested();

                    if (response.Outcome == RunnerArtifactUploadOutcome.RejectedStaleRuntime)
                    {
                        // Rejection never proves that these bytes reached durable storage.
                        return null;
                    }
                    // Catalog keys ignore case and can retain an older version's spelling.
                    // The echoed upload ID still binds the result to this exact snapshot.
                    if (response.Outcome != RunnerArtifactUploadOutcome.Committed || response.UploadId != metadata.UploadId
                        || response.Artifact is null
                        || response.Artifact.EditorSid != _editorSid
                        || !string.Equals(response.Artifact.Type, type, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(response.Artifact.Subfolder, subfolder, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(response.Artifact.Filename, fileName, StringComparison.OrdinalIgnoreCase)
                        || response.Artifact.SizeBytes != sizeBytes)
                    {
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.Message) ? "upload failed" : response.Message);
                    }

                    // A rewrite during the upload is dropped by the pending set, so compare the
                    // file with the snapshot that was sent before recording it as uploaded.
                    SecureFileStatus after;
                    SecureFileVersion? afterVersion;
                    using (var secureRoot = new LinuxSecureDirectoryRoot(root))
                    {
                        after = secureRoot.GetStatus(relPath);
                        afterVersion = secureRoot.GetVersion(relPath);
                    }

                    var changedDuringUpload = !after.IsRegularFile ||
                                              after.Length != sizeBytes ||
                                              after.LastWriteTimeUtc != snapshotWriteUtc ||
                                              afterVersion is null ||
                                              afterVersion != snapshotVersion;

                    // When it changed, record the snapshot's time so a later scan sees a newer file.
                    var createdUtc = changedDuringUpload
                        ? new DateTimeOffset(snapshotWriteUtc, TimeSpan.Zero)
                        : DateTimeOffset.UtcNow;
                    var localPath = NormalizePath(fullPath);
                    var remote = new RemoteArtifactState(_editorSid, type, subfolder, fileName, localPath, sizeBytes, null, createdUtc);
                    lock (_evictionGate)
                    {
                        _catalog[remote.LocalPath] = remote;
                        // A file that changed while it was sent matches no uploaded version yet.
                        remote.MarkDownloaded(changedDuringUpload ? null : afterVersion);
                    }

                    _logger.LogInformation("Uploaded artifact session={SessionId} type={Type} path={Relative}", _sessionId, type, relPath);
                    if (changedDuringUpload && after.IsRegularFile && request.Attempt < 3)
                    {
                        _logger.LogInformation(
                            "Artifact changed during upload; uploading it again session={SessionId} type={Type} path={Relative}",
                            _sessionId,
                            type,
                            relPath);
                        return request with { Attempt = request.Attempt + 1 };
                    }

                    return null;
                }
                catch (UnauthorizedAccessException ex)
                {
                    // The path became a symlink, FIFO or other non-regular file; never retry it.
                    _logger.LogWarning(ex, "Skipping non-regular artifact path session={SessionId} type={Type} path={Relative}", _sessionId, type, relPath);
                    return null;
                }
                catch (IOException ex) when (tries < 4)
                {
                    _logger.LogDebug(ex, "File locked during upload, retrying {Path}", fullPath);
                    await Task.Delay(InitialRetryDelay * (tries + 1), budget.Token).ConfigureAwait(false);
                }
            }

            return null;
        }

        private static async Task WaitForFileStableAsync(string root, string relativePath, CancellationToken token)
        {
            const int MaxChecks = 10;
            const int DelayMs = 150;

            long lastSize = -1;
            DateTime lastWrite = DateTime.MinValue;

            for (var i = 0; i < MaxChecks; i++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    SecureFileStatus info;
                    using (var secureRoot = new LinuxSecureDirectoryRoot(root))
                    {
                        info = secureRoot.GetStatus(relativePath);
                    }

                    if (!info.IsRegularFile)
                    {
                        await Task.Delay(DelayMs, token).ConfigureAwait(false);
                        continue;
                    }

                    var currentSize = info.Length;
                    var currentWrite = info.LastWriteTimeUtc;

                    if (currentSize == lastSize && currentWrite == lastWrite && currentSize > 0)
                    {
                        return;
                    }

                    lastSize = currentSize;
                    lastWrite = currentWrite;
                }
                catch
                {
                    // ignore and retry
                }

                await Task.Delay(DelayMs, token).ConfigureAwait(false);
            }
        }

        private static string? ResolveContentType(string fileName)
        {
            var ext = Path.GetExtension(fileName);
            if (!string.IsNullOrWhiteSpace(ext) && ContentTypes.TryGetValue(ext, out var contentType))
            {
                return contentType;
            }
            return "application/octet-stream";
        }

        private sealed class RemoteArtifactState
        {
            public RemoteArtifactState(string editorSid, string type, string subfolder, string filename, string localPath, long? sizeBytes, string? sha256, DateTimeOffset createdUtc)
            {
                EditorSid = editorSid;
                Type = type ?? string.Empty;
                Subfolder = subfolder ?? string.Empty;
                Filename = filename ?? string.Empty;
                LocalPath = localPath;
                SizeBytes = sizeBytes;
                Sha256 = sha256;
                CreatedUtc = createdUtc;
            }

            public string EditorSid { get; }
            public string Type { get; }
            public string Subfolder { get; }
            public string Filename { get; }
            public string LocalPath { get; }
            public long? SizeBytes { get; private set; }
            public string? Sha256 { get; private set; }
            public DateTimeOffset CreatedUtc { get; private set; }
            public bool Downloaded { get; private set; }

            public void UpdateMetadata(long? sizeBytes, string? sha256, DateTimeOffset createdUtc)
            {
                SizeBytes = sizeBytes;
                Sha256 = sha256;
                CreatedUtc = createdUtc;
            }

            /// <summary>The local file that equals the remote artifact, when there is one.</summary>
            public SecureFileVersion? LocalVersion { get; private set; }

            /// <summary>The empty placeholder the agent left for the remote artifact, when there is one.</summary>
            public SecureFileVersion? PlaceholderVersion { get; private set; }

            public void MarkDownloaded(SecureFileVersion? localVersion = null)
            {
                Downloaded = true;
                LocalVersion = localVersion;
                PlaceholderVersion = null;
            }

            public void MarkPlaceholder(SecureFileVersion? placeholder)
            {
                LocalVersion = null;
                PlaceholderVersion = placeholder is { Status.Length: 0 } ? placeholder : null;
            }

            public void MarkEvicted(SecureFileVersion? placeholder)
            {
                Downloaded = false;
                MarkPlaceholder(placeholder);
            }

            /// <summary>
            /// Takes on <paramref name="current"/> when it is the recorded file with only its change
            /// time moved; a record whose file changed in any other way no longer stands for it.
            /// </summary>
            public void RefreshVersion(SecureFileVersion? current)
            {
                if (LocalVersion is { } local)
                {
                    LocalVersion = current is { } now && now.IsSameFileAs(local) ? now : null;
                }

                if (PlaceholderVersion is { } placeholder)
                {
                    PlaceholderVersion = current is { } now && now.IsSameFileAs(placeholder) ? now : null;
                }
            }

            /// <summary>
            /// True when the control plane holds what <paramref name="version"/> stands for: the file
            /// itself, or the remote artifact behind the agent's own empty placeholder.
            /// </summary>
            public bool AccountsFor(SecureFileVersion version)
                => MatchesLocalFile(version) || PlaceholderVersion == version;

            /// <summary>When the VFS last told a reader the local file is ready.</summary>
            public DateTimeOffset LastServedUtc { get; private set; }

            public void MarkServed(DateTimeOffset when) => LastServedUtc = when;

            /// <summary>How long since the artifact was last uploaded, downloaded or served.</summary>
            public TimeSpan IdleSince(DateTimeOffset now)
                => now - (LastServedUtc > CreatedUtc ? LastServedUtc : CreatedUtc);

            /// <summary>True when <paramref name="version"/> is exactly the file the control plane holds.</summary>
            public bool MatchesLocalFile(SecureFileVersion version)
                => Downloaded && LocalVersion == version;

            public bool HasSameCatalogGeneration(RemoteArtifactState other)
                => SizeBytes == other.SizeBytes
                   && string.Equals(Sha256 ?? string.Empty, other.Sha256 ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                   && CreatedUtc == other.CreatedUtc;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsyncCore(cancelOutstandingWork: true).ConfigureAwait(false);
        }

        public async Task StopAsync()
        {
            await StopAsyncCore(cancelOutstandingWork: false).ConfigureAwait(false);
        }

        private async Task StopAsyncCore(bool cancelOutstandingWork)
        {
            StopWatchers();
            _queue.Writer.TryComplete();

            if (cancelOutstandingWork)
            {
                CancelOutstandingTransfers();
            }
            else
            {
                await WaitForWorkerDrainAsync().ConfigureAwait(false);
                CancelOutstandingTransfers();
            }

            // Outstanding work has been cancelled; bound the wait so a worker stuck in a
            // non-cancellable operation cannot keep Stop from completing.
            if (_worker is not null)
            {
                await WaitAfterCancellationAsync(_worker, "upload worker").ConfigureAwait(false);
            }

            var downloadTasks = _downloadTasks.Values.ToArray();
            if (downloadTasks.Length > 0)
            {
                await WaitAfterCancellationAsync(Task.WhenAll(downloadTasks), "artifact downloads").ConfigureAwait(false);
            }

            // Uploads have stopped: whatever is not on the control plane now is lost with the session's data.
            UnsavedUploadCount = CountUnsavedArtifacts();
        }

        private async Task WaitAfterCancellationAsync(Task task, string description)
        {
            var timeout = StopWaitTimeout;
            try
            {
                await task.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException) when (!task.IsCompleted)
            {
                _logger.LogWarning(
                    "Artifact {Description} did not stop within {TimeoutSeconds}s session={SessionId}; continuing",
                    description,
                    timeout.TotalSeconds,
                    _sessionId);
            }
            catch
            {
                // Failures of cancelled work are observed by their callers.
            }
        }

        private async Task WaitForWorkerDrainAsync()
        {
            if (_worker is null)
            {
                return;
            }

            try
            {
                await Task.WhenAny(_worker, Task.Delay(StopDrainGracePeriod)).ConfigureAwait(false);
            }
            catch
            {
                // Best effort before forced cancellation.
            }
        }

        private void CancelOutstandingTransfers()
        {
            try
            {
                _cts.Cancel();
            }
            catch
            {
            }
        }

        private void StopWatchers()
        {
            List<FileSystemWatcher> watchersToDispose = [];

            lock (_watcherGate)
            {
                if (_homeWatcher is not null)
                {
                    watchersToDispose.Add(_homeWatcher);
                    _homeWatcher = null;
                }

                if (_outputWatcher is not null)
                {
                    watchersToDispose.Add(_outputWatcher);
                    _outputWatcher = null;
                }

                if (_tempWatcher is not null)
                {
                    watchersToDispose.Add(_tempWatcher);
                    _tempWatcher = null;
                }
            }

            foreach (var watcher in watchersToDispose)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch
                {
                }
            }
        }

    }
}
