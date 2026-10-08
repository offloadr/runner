using System.Collections.Concurrent;
using System.IO.Compression;
using Offloadr.Runner.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Offloadr.Runner.Linux;

internal sealed class WorkspaceMirrorService : IAsyncDisposable
{
    private const string ComfyUiRuntimeKind = "comfyui";
    // Input files outside the session's initial snapshot are fetched on demand, one control
    // plane read per distinct path; bound how many such entries a session can accumulate.
    internal const int DefaultMaxDynamicInputEntries = 4096;

    private readonly RunnerWorkspaceService.RunnerWorkspaceServiceClient _workspaceClient;
    private readonly Metadata? _authHeaders;
    private readonly ILogger<WorkspaceMirrorService> _logger;
    private readonly int _maxDynamicInputEntries;
    private readonly ConcurrentDictionary<string, SessionMirror> _sessions = new(StringComparer.Ordinal);

    public WorkspaceMirrorService(
        RunnerWorkspaceService.RunnerWorkspaceServiceClient workspaceClient,
        string? runnerSecret,
        ILogger<WorkspaceMirrorService> logger,
        int maxDynamicInputEntries = DefaultMaxDynamicInputEntries)
    {
        _workspaceClient = workspaceClient ?? throw new ArgumentNullException(nameof(workspaceClient));
        _authHeaders = BuildAuthHeaders(runnerSecret);
        _logger = logger;
        _maxDynamicInputEntries = maxDynamicInputEntries > 0
            ? maxDynamicInputEntries
            : throw new ArgumentOutOfRangeException(nameof(maxDynamicInputEntries));
    }

    public async Task PrepareSessionAsync(
        string sessionId,
        SessionProcessManager.SessionPaths paths,
        string? editorSid,
        string? runtimeKind,
        IEnumerable<WorkspaceFileMetadata>? initialWorkspaceFiles,
        CancellationToken cancellationToken)
    {
        await StopSessionAsync(sessionId).ConfigureAwait(false);

        var mirror = new SessionMirror(_workspaceClient, _authHeaders, sessionId, paths, _logger, _maxDynamicInputEntries);
        if (!_sessions.TryAdd(sessionId, mirror))
        {
            await mirror.DisposeAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await mirror.InitializeAsync(editorSid, runtimeKind, initialWorkspaceFiles, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _sessions.TryRemove(sessionId, out _);
            await mirror.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> TryEnsureWorkspaceFileAvailableAsync(string path, bool highPriority, CancellationToken cancellationToken)
    {
        foreach (var mirror in _sessions.Values)
        {
            if (await mirror.TryEnsureInputFileAsync(path, highPriority, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    public async Task StopSessionAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var mirror))
        {
            await mirror.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var mirror in _sessions.Values)
        {
            await mirror.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
    }

    private static Metadata? BuildAuthHeaders(string? runnerSecret)
    {
        if (string.IsNullOrWhiteSpace(runnerSecret))
        {
            return null;
        }

        var headers = new Metadata
        {
            { "x-runner-secret", runnerSecret.Trim() }
        };
        return headers;
    }

    private sealed class SessionMirror : IAsyncDisposable
    {
        private const long MaxArchiveBytes = 512L * 1024 * 1024;
        private const long MaxExpandedBytes = 1024L * 1024 * 1024;
        private const int MaxArchiveEntries = 50_000;

        private readonly RunnerWorkspaceService.RunnerWorkspaceServiceClient _workspaceClient;
        private readonly Metadata? _authHeaders;
        private readonly string _sessionId;
        private readonly SessionProcessManager.SessionPaths _paths;
        private readonly ILogger _logger;
        private readonly LinuxSecureDirectoryRoot _secureInputRoot;
        private readonly ConcurrentDictionary<string, WorkspaceFileState> _inputCatalog = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Task> _downloadTasks = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _disposeCts = new();
        private readonly object _dynamicInputGate = new();
        private readonly int _maxDynamicInputEntries;

        private string? _editorSid;
        private int _dynamicInputCount;

        public SessionMirror(
            RunnerWorkspaceService.RunnerWorkspaceServiceClient workspaceClient,
            Metadata? authHeaders,
            string sessionId,
            SessionProcessManager.SessionPaths paths,
            ILogger logger,
            int maxDynamicInputEntries)
        {
            _workspaceClient = workspaceClient;
            _authHeaders = authHeaders;
            _sessionId = sessionId;
            _paths = paths;
            _logger = logger;
            _maxDynamicInputEntries = maxDynamicInputEntries;
            _secureInputRoot = new LinuxSecureDirectoryRoot(paths.InputDirectory);
        }

        public async Task InitializeAsync(string? editorSid, string? runtimeKind, IEnumerable<WorkspaceFileMetadata>? initialWorkspaceFiles, CancellationToken cancellationToken)
        {
            _editorSid = string.IsNullOrWhiteSpace(editorSid) ? null : editorSid.Trim();
            if (string.IsNullOrWhiteSpace(_editorSid))
            {
                return;
            }

            var normalizedRuntime = string.IsNullOrWhiteSpace(runtimeKind) ? ComfyUiRuntimeKind : runtimeKind.Trim();
            if (!string.Equals(normalizedRuntime, ComfyUiRuntimeKind, StringComparison.Ordinal))
            {
                return;
            }

            var snapshot = initialWorkspaceFiles?.ToArray() ?? [];
            await HydrateArchiveAsync(WorkspaceRoot.User, _paths.UserDirectory, cancellationToken).ConfigureAwait(false);
            await HydrateArchiveAsync(WorkspaceRoot.CustomNodes, _paths.CustomNodesDirectory, cancellationToken).ConfigureAwait(false);

            SeedInputPlaceholders(snapshot.Where(static item => item.Root == WorkspaceRoot.Input));
        }

        public async Task<bool> TryEnsureInputFileAsync(string path, bool highPriority, CancellationToken cancellationToken)
        {
            if (_disposeCts.IsCancellationRequested)
            {
                return false;
            }

            var normalized = NormalizePath(path);
            if (!IsSafeInputHydrationPath(normalized))
            {
                return false;
            }

            if (!_inputCatalog.TryGetValue(normalized, out var state))
            {
                state = TryAddDynamicInputState(normalized);
                if (state is null)
                {
                    return false;
                }
            }

            if (state.Downloaded && File.Exists(state.LocalPath))
            {
                return true;
            }

            // The fetch is shared by every caller for this path and is not tied to any one
            // caller's token: a client that hangs up stops waiting without failing the others.
            var downloadTask = GetOrStartInputDownload(normalized, state, highPriority);
            try
            {
                await downloadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                return false;
            }
        }

        private Task GetOrStartInputDownload(string normalized, WorkspaceFileState state, bool highPriority)
        {
            lock (_dynamicInputGate)
            {
                if (_downloadTasks.TryGetValue(normalized, out var existing))
                {
                    return existing;
                }

                var download = Task.Run(() => DownloadInputFileAsync(state, highPriority, CancellationToken.None));
                _downloadTasks[normalized] = download;
                // Registered after the add, so an already finished download is still removed.
                _ = download.ContinueWith(
                    completed =>
                    {
                        ((ICollection<KeyValuePair<string, Task>>)_downloadTasks).Remove(new(normalized, completed));
                        if (!completed.IsCompletedSuccessfully && state.IsDynamic)
                        {
                            // Do not keep entries for paths the control plane could not serve.
                            RemoveDynamicInputState(normalized, state);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return download;
            }
        }

        private WorkspaceFileState? TryAddDynamicInputState(string normalized)
        {
            lock (_dynamicInputGate)
            {
                if (_inputCatalog.TryGetValue(normalized, out var existing))
                {
                    return existing;
                }

                if (_dynamicInputCount >= _maxDynamicInputEntries)
                {
                    throw new IOException(
                        $"Workspace input catalog for session '{_sessionId}' already tracks {_maxDynamicInputEntries} files fetched on demand.");
                }

                var dynamicState = TryCreateDynamicInputState(normalized);
                if (dynamicState is null)
                {
                    return null;
                }

                _inputCatalog[normalized] = dynamicState;
                _dynamicInputCount++;
                return dynamicState;
            }
        }

        private void RemoveDynamicInputState(string normalized, WorkspaceFileState state)
        {
            lock (_dynamicInputGate)
            {
                if (((ICollection<KeyValuePair<string, WorkspaceFileState>>)_inputCatalog).Remove(new(normalized, state)))
                {
                    _dynamicInputCount--;
                }
            }
        }

        private void SeedInputPlaceholders(IEnumerable<WorkspaceFileMetadata> files)
        {
            foreach (var file in files)
            {
                if (string.IsNullOrWhiteSpace(file.RelativePath))
                {
                    continue;
                }

                var localPath = ResolveWorkspacePath(_paths.InputDirectory, file.RelativePath);
                var state = new WorkspaceFileState(
                    _editorSid!,
                    file.Root,
                    file.RelativePath.Replace('\\', '/'),
                    localPath,
                    file.SizeBytes,
                    ToDateTime(file.ModifiedUtc));

                lock (_dynamicInputGate)
                {
                    if (_inputCatalog.TryGetValue(localPath, out var previous) && previous.IsDynamic)
                    {
                        _dynamicInputCount--;
                    }

                    _inputCatalog[localPath] = state;
                }

                EnsurePlaceholder(state);
            }
        }

        private WorkspaceFileState? TryCreateDynamicInputState(string localPath)
        {
            if (string.IsNullOrWhiteSpace(_editorSid))
            {
                return null;
            }

            if (!TryGetRelativeInputPath(localPath, out var relativePath))
            {
                return null;
            }

            return new WorkspaceFileState(
                _editorSid!,
                WorkspaceRoot.Input,
                relativePath,
                localPath,
                sizeBytes: null,
                modifiedUtc: null)
            {
                IsDynamic = true
            };
        }

        private async Task DownloadInputFileAsync(WorkspaceFileState state, bool highPriority, CancellationToken cancellationToken)
        {
            _ = highPriority;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            var linkedCancellationToken = linkedCts.Token;

            using var call = _workspaceClient.ReadWorkspaceFile(
                new RunnerWorkspaceServiceReadWorkspaceFileRequest
                {
                    EditorSid = state.EditorSid,
                    Root = state.Root,
                    RelativePath = state.RelativePath
                },
                headers: _authHeaders,
                cancellationToken: linkedCancellationToken);

            await FetchToFileAsync(state, call.ResponseStream, linkedCancellationToken).ConfigureAwait(false);
            state.Downloaded = true;
        }

        private async Task HydrateArchiveAsync(WorkspaceRoot root, string destinationRoot, CancellationToken cancellationToken)
        {
            using var call = _workspaceClient.ReadWorkspaceSeedArchive(
                new RunnerWorkspaceServiceReadWorkspaceSeedArchiveRequest
                {
                    EditorSid = _editorSid!,
                    Root = root
                },
                headers: _authHeaders,
                cancellationToken: cancellationToken);

            var tempPath = await DownloadArchiveAsync(call.ResponseStream, cancellationToken).ConfigureAwait(false);
            try
            {
                ExtractArchiveSafely(tempPath, destinationRoot);
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        private async Task<string> DownloadArchiveAsync(IAsyncStreamReader<RunnerWorkspaceServiceReadWorkspaceSeedArchiveResponse> responseStream, CancellationToken cancellationToken)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"workspace-seed-{_sessionId}-{Guid.NewGuid():N}.zip");
            long totalBytes = 0;
            bool sawMetadata = false;

            try
            {
                await using var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                while (await responseStream.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    var response = responseStream.Current;
                    switch (response.PayloadCase)
                    {
                        case RunnerWorkspaceServiceReadWorkspaceSeedArchiveResponse.PayloadOneofCase.Metadata:
                            sawMetadata = true;
                            if (response.Metadata.SizeBytes.HasValue && response.Metadata.SizeBytes.Value > MaxArchiveBytes)
                            {
                                throw new InvalidOperationException($"Workspace seed archive exceeds max allowed size ({MaxArchiveBytes} bytes).");
                            }

                            break;
                        case RunnerWorkspaceServiceReadWorkspaceSeedArchiveResponse.PayloadOneofCase.Chunk:
                            if (!sawMetadata)
                            {
                                throw new InvalidOperationException("Workspace seed archive stream missing metadata frame.");
                            }

                            totalBytes += response.Chunk.Length;
                            if (totalBytes > MaxArchiveBytes)
                            {
                                throw new InvalidOperationException($"Workspace seed archive exceeds max allowed size ({MaxArchiveBytes} bytes).");
                            }

                            await destination.WriteAsync(response.Chunk.Memory, cancellationToken).ConfigureAwait(false);
                            break;
                    }
                }

                if (!sawMetadata)
                {
                    throw new InvalidOperationException("Workspace seed archive stream completed without metadata.");
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                return tempPath;
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        private void ExtractArchiveSafely(string archivePath, string destinationRoot)
        {
            Directory.CreateDirectory(destinationRoot);

            using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read, leaveOpen: false);

            long expandedBytes = 0;
            var entryCount = 0;

            foreach (var entry in archive.Entries)
            {
                entryCount++;
                if (entryCount > MaxArchiveEntries)
                {
                    throw new InvalidOperationException($"Workspace seed archive exceeds max entry count ({MaxArchiveEntries}).");
                }

                var isDirectory = TryNormalizeZipEntryPath(entry.FullName, out var normalizedRelativePath);
                ValidateZipEntry(entry, isDirectory);

                var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, normalizedRelativePath));
                var destinationRootFullPath = Path.GetFullPath(destinationRoot);
                if (!destinationPath.StartsWith(destinationRootFullPath + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                    !string.Equals(destinationPath, destinationRootFullPath, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"ZIP entry escapes extraction root: '{entry.FullName}'.");
                }

                if (isDirectory)
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                expandedBytes += entry.Length;
                if (expandedBytes > MaxExpandedBytes)
                {
                    throw new InvalidOperationException($"Workspace seed archive exceeds max expanded size ({MaxExpandedBytes} bytes).");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                using var entryStream = entry.Open();
                using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
                entryStream.CopyTo(output);
                output.Flush(true);

                if (entry.LastWriteTime != default)
                {
                    File.SetLastWriteTimeUtc(destinationPath, entry.LastWriteTime.UtcDateTime);
                }
            }
        }

        private static bool TryNormalizeZipEntryPath(string entryName, out string normalizedRelativePath)
        {
            normalizedRelativePath = string.Empty;
            if (string.IsNullOrWhiteSpace(entryName))
            {
                throw new InvalidOperationException("ZIP entry name is empty.");
            }

            var normalized = entryName.Replace('\\', '/').Trim();
            if (normalized.Length == 0 || normalized.StartsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"ZIP entry has invalid path '{entryName}'.");
            }

            if (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
            {
                throw new InvalidOperationException($"ZIP entry has rooted path '{entryName}'.");
            }

            var isDirectory = normalized.EndsWith("/", StringComparison.Ordinal);
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || parts.Any(static part => part is "." or ".."))
            {
                throw new InvalidOperationException($"ZIP entry escapes extraction root: '{entryName}'.");
            }

            normalizedRelativePath = Path.Combine(parts);
            return isDirectory;
        }

        private static void ValidateZipEntry(ZipArchiveEntry entry, bool isDirectory)
        {
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType != 0 && unixType is not 0x4000 and not 0x8000)
            {
                throw new InvalidOperationException($"ZIP entry type is not supported: '{entry.FullName}'.");
            }

            if (isDirectory)
            {
                return;
            }

            if (entry.Length < 0)
            {
                throw new InvalidOperationException($"ZIP entry has invalid length: '{entry.FullName}'.");
            }
        }

        private void EnsurePlaceholder(WorkspaceFileState state)
        {
            EnsureSafeInputHydrationPath(state.LocalPath);
            _secureInputRoot.EnsurePlaceholder(state.RelativePath, state.ModifiedUtc);
        }

        private async Task FetchToFileAsync(WorkspaceFileState state, IAsyncStreamReader<RunnerWorkspaceServiceReadWorkspaceFileResponse> responseStream, CancellationToken cancellationToken)
        {
            EnsureSafeInputHydrationPath(state.LocalPath);
            using var publication = _secureInputRoot.CreatePublication(state.RelativePath);
            var sawMetadata = false;
            await using (var destination = publication.OpenWriteStream())
            {
                while (await responseStream.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    var response = responseStream.Current;
                    switch (response.PayloadCase)
                    {
                        case RunnerWorkspaceServiceReadWorkspaceFileResponse.PayloadOneofCase.Metadata:
                            sawMetadata = true;
                            var modifiedUtc = ToDateTime(response.Metadata.ModifiedUtc);
                            if (modifiedUtc.HasValue)
                            {
                                state.ModifiedUtc = modifiedUtc;
                            }

                            if (response.Metadata.SizeBytes.HasValue)
                            {
                                state.SizeBytes = response.Metadata.SizeBytes.Value;
                            }

                            break;
                        case RunnerWorkspaceServiceReadWorkspaceFileResponse.PayloadOneofCase.Chunk:
                            if (!sawMetadata)
                            {
                                throw new InvalidOperationException("Workspace file stream missing metadata frame.");
                            }

                            await destination.WriteAsync(response.Chunk.Memory, cancellationToken).ConfigureAwait(false);
                            break;
                    }
                }

                if (!sawMetadata)
                {
                    throw new InvalidOperationException("Workspace file stream completed without metadata.");
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (state.ModifiedUtc.HasValue)
                {
                    publication.SetModifiedUtc(state.ModifiedUtc.Value);
                }
            }

            publication.Commit();
        }

        private static DateTime? ToDateTime(Timestamp? timestamp)
        {
            if (timestamp is null || (timestamp.Seconds == 0 && timestamp.Nanos == 0))
            {
                return null;
            }

            return timestamp.ToDateTime().ToUniversalTime();
        }

        private static string ResolveWorkspacePath(string rootDirectory, string relativePath)
        {
            var normalized = relativePath.Replace('\\', '/').Trim('/');
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var combined = rootDirectory;
            foreach (var part in parts)
            {
                combined = Path.Combine(combined, part);
            }

            return NormalizePath(combined);
        }

        private bool TryGetRelativeInputPath(string localPath, out string relativePath)
        {
            relativePath = string.Empty;

            var inputRoot = NormalizePath(_paths.InputDirectory);
            var normalizedPath = NormalizePath(localPath);
            var candidateRelativePath = Path.GetRelativePath(inputRoot, normalizedPath);
            if (string.IsNullOrWhiteSpace(candidateRelativePath) || candidateRelativePath == ".")
            {
                return false;
            }

            if (Path.IsPathRooted(candidateRelativePath))
            {
                return false;
            }

            var normalizedRelativePath = candidateRelativePath.Replace('\\', '/');
            var parts = normalizedRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || parts.Any(static part => part is "." or ".."))
            {
                return false;
            }

            relativePath = string.Join('/', parts);
            return true;
        }

        private bool IsSafeInputHydrationPath(string localPath)
        {
            if (!TryGetRelativeInputPath(localPath, out _))
            {
                return false;
            }

            var inputRoot = NormalizePath(_paths.InputDirectory);
            var existingParent = Path.GetDirectoryName(NormalizePath(localPath));
            while (existingParent is not null &&
                   !Directory.Exists(existingParent) &&
                   !string.Equals(existingParent, inputRoot, StringComparison.Ordinal))
            {
                if (File.Exists(existingParent))
                {
                    return false;
                }

                existingParent = Path.GetDirectoryName(existingParent);
            }

            if (existingParent is null)
            {
                return false;
            }

            var canonicalRoot = LinuxPathCanonicalizer.ResolveExistingPath(inputRoot);
            var canonicalParent = LinuxPathCanonicalizer.ResolveExistingPath(existingParent);
            var relativeParent = Path.GetRelativePath(canonicalRoot, canonicalParent);
            if (Path.IsPathRooted(relativeParent))
            {
                return false;
            }

            var parentParts = relativeParent
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parentParts.All(static part => part is not "..");
        }

        private void EnsureSafeInputHydrationPath(string localPath)
        {
            if (!IsSafeInputHydrationPath(localPath))
            {
                throw new UnauthorizedAccessException(
                    $"Workspace input path escapes its canonical root: '{localPath}'.");
            }
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best effort temp cleanup.
            }
        }

        public ValueTask DisposeAsync()
        {
            _disposeCts.Cancel();
            return DisposeAsyncCore();
        }

        private async ValueTask DisposeAsyncCore()
        {
            try
            {
                var tasks = _downloadTasks.Values.ToArray();
                if (tasks.Length > 0)
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                // Expected during teardown.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Workspace mirror disposal observed a failed download for session {SessionId}.", _sessionId);
            }

            _inputCatalog.Clear();
            _downloadTasks.Clear();
            _secureInputRoot.Dispose();
            _disposeCts.Dispose();
        }

        private sealed class WorkspaceFileState(
            string editorSid,
            WorkspaceRoot root,
            string relativePath,
            string localPath,
            long? sizeBytes,
            DateTime? modifiedUtc)
        {
            public string EditorSid { get; } = editorSid;
            public WorkspaceRoot Root { get; } = root;
            public string RelativePath { get; } = relativePath;
            public string LocalPath { get; } = localPath;
            public long? SizeBytes { get; set; } = sizeBytes;
            public DateTime? ModifiedUtc { get; set; } = modifiedUtc;
            public bool Downloaded { get; set; }
            public bool IsDynamic { get; init; }
        }
    }
}
