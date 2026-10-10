using Offloadr.Runner.V1;
using Offloadr.EditorRuntime.V1;
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

internal sealed class ArtifactUploadService : IAsyncDisposable
{
    /// <summary>Largest session file the uploader will send; larger files are skipped.</summary>
    internal const long MaxUploadBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Most session files waiting for upload at once; more wait for the next scan.</summary>
    internal static int MaxPendingUploads { get; set; } = 4096;

    private static readonly TimeSpan DropWarningInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long Stop waits for cancelled upload and download work before moving on.</summary>
    internal static TimeSpan StopWaitTimeout { get; set; } = TimeSpan.FromSeconds(10);

    private readonly string? _runnerSecret;
    private readonly RunnerArtifactService.RunnerArtifactServiceClient _artifactClient;
    private readonly Metadata? _authHeaders;
    private readonly ILogger<ArtifactUploadService> _logger;
    private readonly RuntimeIdentityRegistry? _runtimeIdentities;
    private readonly string _runnerId;
    private readonly ConcurrentDictionary<string, SessionUploader> _sessions = new(StringComparer.Ordinal);

    public ArtifactUploadService(string? runnerSecret, RunnerArtifactService.RunnerArtifactServiceClient artifactClient, ILogger<ArtifactUploadService> logger, RuntimeIdentityRegistry? runtimeIdentities = null, string? runnerId = null)
    {
        _runnerSecret = string.IsNullOrWhiteSpace(runnerSecret) ? null : runnerSecret.Trim();
        _artifactClient = artifactClient ?? throw new ArgumentNullException(nameof(artifactClient));
        _authHeaders = BuildAuthHeaders(_runnerSecret);
        _logger = logger;
        _runtimeIdentities = runtimeIdentities;
        _runnerId = runnerId?.Trim() ?? string.Empty;
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
        if (_sessions.ContainsKey(sessionId))
        {
            return;
        }

        var uploader = new SessionUploader(_runnerSecret, _artifactClient, _authHeaders, sessionId, paths, _logger, _runtimeIdentities, _runnerId);
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

    internal int GetPendingUploadCount(string sessionId)
        => _sessions.TryGetValue(sessionId, out var uploader) ? uploader.PendingUploadCount : 0;

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

    public async Task StopSessionAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var uploader))
        {
            await uploader.StopAsync().ConfigureAwait(false);
        }
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
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
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

        public SessionUploader(string? runnerSecret, RunnerArtifactService.RunnerArtifactServiceClient artifactClient, Metadata? authHeaders, string sessionId, SessionProcessManager.SessionPaths paths, ILogger logger, RuntimeIdentityRegistry? runtimeIdentities = null, string? runnerId = null)
        {
            _runnerSecret = runnerSecret;
            _artifactClient = artifactClient;
            _authHeaders = authHeaders;
            _sessionId = sessionId;
            _paths = paths;
            _logger = logger;
            _runtimeIdentities = runtimeIdentities;
            _runnerId = runnerId?.Trim() ?? string.Empty;
            _queue = Channel.CreateUnbounded<UploadRequest>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        }

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
            RegisterHomeWatcher(_paths.HomeDirectory);
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

            if (state.Downloaded && File.Exists(state.LocalPath))
            {
                return true;
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
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
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

            OnFileEvent(type, fullPath, $"watcher:{changeType}");
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
                    EditorSid = editorSid
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

            return normalized;
        }

        private void PruneCatalogEntries(HashSet<string> currentPaths)
        {
            foreach (var (path, _) in _catalog)
            {
                if (!currentPaths.Contains(path))
                {
                    _catalog.TryRemove(path, out _);
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
                    return;
                case SecureEntryKind.Other:
                    throw new UnauthorizedAccessException($"Artifact path is not a regular file: '{state.LocalPath}'.");
            }

            if (status.Length > 0 && ShouldKeepExistingArtifactFile(state, previousState, status.Length, secureRoot, relativePath))
            {
                state.MarkDownloaded();
                return;
            }

            secureRoot.TruncateRegularFile(relativePath, state.CreatedUtc.UtcDateTime);
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
                        Subfolder = state.Subfolder
                    },
                    headers: _authHeaders,
                    cancellationToken: token);

                await FetchToFileAsync(state, call.ResponseStream, token).ConfigureAwait(false);
                state.MarkDownloaded();
            }
            catch (RpcException ex) when (!token.IsCancellationRequested)
            {
                throw new InvalidOperationException($"Artifact download request failed: {ex.Status.Detail}", ex);
            }
        }

        private async Task FetchToFileAsync(RemoteArtifactState state, IAsyncStreamReader<RunnerArtifactServiceReadArtifactResponse> responseStream, CancellationToken cancellationToken)
        {
            if (!TryResolveSecureLocation(state.Type, state.LocalPath, out var root, out var relativePath))
            {
                throw new InvalidOperationException($"Artifact path is outside its session root: '{state.LocalPath}'.");
            }

            // Write and publish relative to a pinned directory descriptor so that a
            // swapped symlink in the session tree cannot redirect the root-owned write.
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            using (var publication = secureRoot.CreatePublication(relativePath))
            {
                long bytesWritten = 0;
                long? expectedSizeBytes = null;
                string? expectedSha256 = null;
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
                                expectedSha256 = string.IsNullOrWhiteSpace(metadata.Sha256) ? null : metadata.Sha256;
                                state.UpdateMetadata(
                                    expectedSizeBytes,
                                    expectedSha256,
                                    ToDateTimeOffset(metadata.ModifiedUtc));
                                break;
                            case RunnerArtifactServiceReadArtifactResponse.PayloadOneofCase.Chunk:
                                if (!sawMetadata)
                                {
                                    throw new InvalidOperationException("Artifact stream missing metadata frame.");
                                }

                                await destination.WriteAsync(response.Chunk.Memory, cancellationToken).ConfigureAwait(false);
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
                }

                try
                {
                    publication.SetModifiedUtc(state.CreatedUtc.UtcDateTime);
                }
                catch
                {
                    // ignore timestamp failures
                }

                // Disposing an uncommitted publication removes its temporary file.
                publication.Commit();
            }
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

                if (!_pending.TryAdd(fullPath, 0))
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

                var request = new UploadRequest(type, fullPath, 0, CurrentRuntime());
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

        private RuntimeIdentity? CurrentRuntime()
            => _runtimeIdentities?.TryGet(_sessionId, out var identity) == true ? identity : null;

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
                            if (item.Attempt < 3)
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

            await WaitForFileStableAsync(root, relPath, _cts.Token).ConfigureAwait(false);

            var separatorIndex = relPath.LastIndexOf('/');
            var fileName = separatorIndex < 0 ? relPath : relPath[(separatorIndex + 1)..];
            var subfolder = separatorIndex < 0 ? string.Empty : relPath[..separatorIndex];

            for (var tries = 0; tries < 5; tries++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                try
                {
                    if (string.IsNullOrWhiteSpace(_editorSid))
                    {
                        await Task.Delay(InitialRetryDelay * (tries + 1), _cts.Token).ConfigureAwait(false);
                        return request with { Attempt = request.Attempt + 1 };
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
                        RunnerId = _runnerId,
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

                    _logger.LogInformation(
                        "Uploading artifact session={SessionId} editor={EditorSid} type={Type} path={Relative} bytes={Size}",
                        _sessionId,
                        _editorSid,
                        type,
                        relPath,
                        sizeBytes);

                    var call = _artifactClient.UploadArtifact(headers: _authHeaders, cancellationToken: _cts.Token);
                    await call.RequestStream.WriteAsync(new RunnerArtifactServiceUploadArtifactRequest { Metadata = metadata }).ConfigureAwait(false);

                    var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                    try
                    {
                        // Send at most the size declared in the metadata, even if the file grows.
                        var remaining = sizeBytes;
                        int read;
                        while (remaining > 0 &&
                               (read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), _cts.Token).ConfigureAwait(false)) > 0)
                        {
                            remaining -= read;
                            await call.RequestStream.WriteAsync(new RunnerArtifactServiceUploadArtifactRequest
                            {
                                Chunk = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read)
                            }).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    await call.RequestStream.CompleteAsync().ConfigureAwait(false);
                    var response = await call.ResponseAsync.ConfigureAwait(false);

                    if (!response.Success)
                    {
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.Message) ? "upload failed" : response.Message);
                    }

                    // A rewrite during the upload is dropped by the pending set, so compare the
                    // file with the snapshot that was sent before recording it as uploaded.
                    SecureFileStatus after;
                    using (var secureRoot = new LinuxSecureDirectoryRoot(root))
                    {
                        after = secureRoot.GetStatus(relPath);
                    }

                    var changedDuringUpload = !after.IsRegularFile ||
                                              after.Length != sizeBytes ||
                                              after.LastWriteTimeUtc != snapshotWriteUtc;

                    // When it changed, record the snapshot's time so a later scan sees a newer file.
                    var createdUtc = changedDuringUpload
                        ? new DateTimeOffset(snapshotWriteUtc, TimeSpan.Zero)
                        : DateTimeOffset.UtcNow;
                    var localPath = NormalizePath(fullPath);
                    var remote = new RemoteArtifactState(_editorSid, type, subfolder, fileName, localPath, sizeBytes, null, createdUtc);
                    _catalog[remote.LocalPath] = remote;
                    remote.MarkDownloaded();

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
                    await Task.Delay(InitialRetryDelay * (tries + 1), _cts.Token).ConfigureAwait(false);
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

            public void MarkDownloaded() => Downloaded = true;

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
