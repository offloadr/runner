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
        private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, RemoteArtifactState> _catalog = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Task> _downloadTasks = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _inputSeeds = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _watcherGate = new();
        private FileSystemWatcher? _homeWatcher;
        private FileSystemWatcher? _outputWatcher;
        private FileSystemWatcher? _tempWatcher;
        private Task? _worker;
        private bool _activated;
        private bool _catalogLoaded;

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

            var downloadTask = _downloadTasks.GetOrAdd(state.LocalPath, _ => DownloadArtifactAsync(state, highPriority, cancellationToken));
            try
            {
                await downloadTask.ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch
            {
                if (dynamicState)
                {
                    _catalog.TryRemove(normalized, out _);
                }

                throw;
            }
            finally
            {
                _downloadTasks.TryRemove(state.LocalPath, out _);
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
            if (string.Equals(normalized, NormalizePath(_paths.OutputDirectory), StringComparison.OrdinalIgnoreCase))
            {
                type = "output";
                return true;
            }

            if (string.Equals(normalized, NormalizePath(_paths.TempDirectory), StringComparison.OrdinalIgnoreCase))
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
                var currentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            var currentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

        private static void ClearUncatalogedInputFile(string type, string path)
        {
            if (!type.Equals("input", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                fs.SetLength(0);
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
            string? root = type.ToLowerInvariant() switch
            {
                "input" => _paths.InputDirectory,
                "output" => _paths.OutputDirectory,
                "temp" => _paths.TempDirectory,
                _ => null
            };

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
            var directory = Path.GetDirectoryName(state.LocalPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(state.LocalPath))
            {
                using var fs = new FileStream(state.LocalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                fs.SetLength(0);
            }
            else
            {
                var info = new FileInfo(state.LocalPath);
                if (info.Length > 0)
                {
                    if (ShouldKeepExistingArtifactFile(state, previousState, info))
                    {
                        state.MarkDownloaded();
                        return;
                    }
                }

                using var fs = new FileStream(state.LocalPath, FileMode.Open, FileAccess.Write, FileShare.Read);
                fs.SetLength(0);
            }

            try
            {
                File.SetLastWriteTimeUtc(state.LocalPath, state.CreatedUtc.UtcDateTime);
            }
            catch
            {
                // best effort
            }
        }

        private static bool ShouldKeepExistingArtifactFile(RemoteArtifactState state, RemoteArtifactState? previousState, FileInfo fileInfo)
        {
            if (!string.Equals(state.Type, "input", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return previousState is { Downloaded: true }
                   && previousState.HasSameCatalogGeneration(state)
                   && ExistingFileMatchesCatalogMetadata(state, fileInfo);
        }

        private static bool ExistingFileMatchesCatalogMetadata(RemoteArtifactState state, FileInfo fileInfo)
        {
            if (state.SizeBytes.HasValue && fileInfo.Length != state.SizeBytes.Value)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(state.Sha256))
            {
                try
                {
                    using var stream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
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
            var directory = Path.GetDirectoryName(state.LocalPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = Path.Combine(directory ?? Path.GetTempPath(), $".{Path.GetRandomFileName()}.part");
            try
            {
                long bytesWritten = 0;
                long? expectedSizeBytes = null;
                string? expectedSha256 = null;
                await using (var destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
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

                File.Move(tempPath, state.LocalPath, overwrite: true);
                try
                {
                    File.SetLastWriteTimeUtc(state.LocalPath, state.CreatedUtc.UtcDateTime);
                }
                catch
                {
                    // ignore timestamp failures
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // ignore cleanup failures
                }
            }
        }

        private void OnFileEvent(string type, string fullPath, string source)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return;
            try
            {
                if (!File.Exists(fullPath))
                {
                    return;
                }

                var fileInfo = new FileInfo(fullPath);
                if (!fileInfo.Exists)
                {
                    return;
                }

                if (TryGetCatalogEntry(fullPath, out var knownState) && ShouldSkipCatalogBackedUpload(fileInfo, knownState))
                {
                    return;
                }
                var attr = File.GetAttributes(fullPath);
                if ((attr & FileAttributes.Directory) != 0)
                {
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
                    fileInfo.Length);

                var request = new UploadRequest(type, fullPath, 0);
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

        private static bool ShouldSkipCatalogBackedUpload(FileInfo fileInfo, RemoteArtifactState? knownState)
        {
            if (knownState is null)
            {
                return false;
            }

            // Ignore catalog placeholders (0-byte files) and only upload once the local file has real content.
            if (fileInfo.Length <= 0)
            {
                return true;
            }

            if (!knownState.Downloaded)
            {
                return false;
            }

            var knownSize = knownState.SizeBytes;
            if (!knownSize.HasValue || knownSize.Value != fileInfo.Length)
            {
                return false;
            }

            // If size and mtime still match catalog metadata, nothing changed locally.
            return fileInfo.LastWriteTimeUtc <= knownState.CreatedUtc.UtcDateTime;
        }

        private async Task ProcessQueueAsync()
        {
            try
            {
                while (await _queue.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    while (_queue.Reader.TryRead(out var item))
                    {
                        try
                        {
                            await UploadAsync(item).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            if (item.Attempt < 3)
                            {
                                var retry = item with { Attempt = item.Attempt + 1 };
                                _logger.LogDebug(ex, "Retrying artifact upload {Path} attempt {Attempt}", item.FullPath, retry.Attempt);
                                await Task.Delay(TimeSpan.FromMilliseconds(200 * (item.Attempt + 1)), _cts.Token).ConfigureAwait(false);
                                _queue.Writer.TryWrite(retry);
                                continue;
                            }

                            _logger.LogWarning(ex, "Failed to upload artifact {Path} after retries", item.FullPath);
                        }
                        finally
                        {
                            _pending.TryRemove(item.FullPath, out _);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task UploadAsync(UploadRequest request)
        {
            var (type, fullPath, attempt) = request;
            var (root, relPath) = ResolveRelativePath(type, fullPath);
            if (root is null || relPath is null)
            {
                return;
            }

            await WaitForFileStableAsync(fullPath, _cts.Token).ConfigureAwait(false);

            var fileName = Path.GetFileName(relPath);
            var subfolder = Path.GetDirectoryName(relPath);
            subfolder = string.IsNullOrWhiteSpace(subfolder) ? string.Empty : subfolder.Replace(Path.DirectorySeparatorChar, '/');

            for (var tries = 0; tries < 5; tries++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                try
                {
                    if (string.IsNullOrWhiteSpace(_editorSid))
                    {
                        await Task.Delay(InitialRetryDelay * (tries + 1), _cts.Token).ConfigureAwait(false);
                        _queue.Writer.TryWrite(request with { Attempt = request.Attempt + 1 });
                        return;
                    }

                    var fileInfo = new FileInfo(fullPath);
                    if (!fileInfo.Exists)
                    {
                        return;
                    }

                    await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var contentType = ResolveContentType(fileName);

                    var metadata = new RunnerArtifactUploadMetadata
                    {
                        RunnerId = _runnerId,
                        EditorSid = _editorSid,
                        Filename = fileName,
                        Type = type,
                        Subfolder = subfolder,
                        ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType,
                        SizeBytes = stream.Length
                    };

                    if (!string.IsNullOrWhiteSpace(_owner))
                    {
                        metadata.Owner = _owner;
                    }
                    if (!string.IsNullOrWhiteSpace(_sessionId))
                    {
                        metadata.SessionId = _sessionId;
                    }
                    if (_runtimeIdentities?.TryGet(_sessionId, out var runtimeIdentity) == true)
                    {
                        metadata.LifecycleGeneration = runtimeIdentity.LifecycleGeneration;
                        metadata.RuntimeEpoch = runtimeIdentity.RuntimeEpoch;
                        metadata.RuntimeInstanceId = runtimeIdentity.RuntimeInstanceId;
                    }

                    _logger.LogInformation(
                        "Uploading artifact session={SessionId} editor={EditorSid} type={Type} path={Relative} bytes={Size}",
                        _sessionId,
                        _editorSid,
                        type,
                        relPath,
                        stream.Length);

                    var call = _artifactClient.UploadArtifact(headers: _authHeaders, cancellationToken: _cts.Token);
                    await call.RequestStream.WriteAsync(new RunnerArtifactServiceUploadArtifactRequest { Metadata = metadata }).ConfigureAwait(false);

                    var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                    try
                    {
                        int read;
                        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), _cts.Token).ConfigureAwait(false)) > 0)
                        {
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

                    var createdUtc = DateTimeOffset.UtcNow;
                    var localPath = NormalizePath(fullPath);
                    var remote = new RemoteArtifactState(_editorSid, type, subfolder, fileName, localPath, fileInfo.Length, null, createdUtc);
                    _catalog[remote.LocalPath] = remote;
                    remote.MarkDownloaded();

                    _logger.LogInformation("Uploaded artifact session={SessionId} type={Type} path={Relative}", _sessionId, type, relPath);
                    return;
                }
                catch (IOException ex) when (tries < 4)
                {
                    _logger.LogDebug(ex, "File locked during upload, retrying {Path}", fullPath);
                    await Task.Delay(InitialRetryDelay * (tries + 1), _cts.Token).ConfigureAwait(false);
                }
            }
        }

        private (string? Root, string? RelativePath) ResolveRelativePath(string type, string fullPath)
        {
            string? root = type.Equals("output", StringComparison.OrdinalIgnoreCase)
                ? _paths.OutputDirectory
                : type.Equals("temp", StringComparison.OrdinalIgnoreCase)
                    ? _paths.TempDirectory
                    : null;

            if (string.IsNullOrWhiteSpace(root)) return (null, null);

            try
            {
                var relative = Path.GetRelativePath(root, fullPath);
                if (relative.StartsWith("..")) return (null, null);

                return (root, relative);
            }
            catch
            {
                return (null, null);
            }
        }

        private static async Task WaitForFileStableAsync(string path, CancellationToken token)
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
                    var info = new FileInfo(path);
                    if (!info.Exists)
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

            if (_worker is not null)
            {
                try { await _worker.ConfigureAwait(false); } catch { }
            }

            var downloadTasks = _downloadTasks.Values.ToArray();
            if (downloadTasks.Length > 0)
            {
                try { await Task.WhenAll(downloadTasks).ConfigureAwait(false); } catch { }
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
