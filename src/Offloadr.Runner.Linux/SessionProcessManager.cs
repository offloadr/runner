using Offloadr.Common.V1;
using Offloadr.EditorRuntime.V1;
using Offloadr.Runner.V1;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Offloadr.Runner.Linux;

/// <summary>
/// Manages lifecycle of editor worker processes, keeping each session isolated under its own ephemeral user.
/// </summary>
internal sealed partial class SessionProcessManager : IDisposable
{
    private static readonly TimeSpan ExitWaitAfterKill = TimeSpan.FromSeconds(5);
    // Match Forge's browser progress cadence closely enough to keep snapshots fresh without over-polling.
    private const string DefaultProcessPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
    private static readonly HashSet<string> HttpMethodsWithoutBody = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET",
        "HEAD",
        "DELETE",
        "OPTIONS",
        "TRACE",
    };

    private readonly ConcurrentDictionary<string, SessionContext> _sessions = new();
    // Prepared identities, from user creation until isolation cleanup. Unlike _sessions this
    // covers the window before launch, so the editor can use the IPC socket as soon as it runs.
    private readonly ConcurrentDictionary<string, PreparedSessionIdentity> _preparedIdentities = new(StringComparer.Ordinal);
    private readonly string _sessionRoot;
    private readonly string _entryPointPath;
    private readonly string _workingDirectory;
    private readonly string _bundledCustomNodesSeedPath;
    private readonly string _bundledInputSeedPath;
    private readonly string _seedVirtualEnvPath;
    private readonly string _uvBinaryPath;
    private readonly string _runtimeKind;
    private readonly string _readyPath;
    private readonly int _comfyPort;
    private readonly TimeSpan _shutdownGracePeriod;
    private readonly TimeSpan _readyTimeout;
    private readonly string _readyHost;
    private readonly HttpClient _httpClient;
    private readonly ISessionIsolationStrategy _sessionIsolationStrategy;
    private readonly RunnerVfsEnvironmentBuilder _vfsEnvironmentBuilder;
    private readonly ILinuxCommandRunner _signalCommandRunner;
    private readonly SessionProcessLogRelay? _processLogRelay;

    internal Func<string, int?, CancellationToken, Task>? UnexpectedSessionExitCleanup { get; set; }

    public SessionProcessManager()
        : this(
            SessionProcessOptions.FromEnvironment(),
            new LinuxUserIsolationStrategy(new LinuxCommandRunner()),
            new RunnerVfsEnvironmentBuilder(),
            new LinuxCommandRunner())
    {
    }

    internal SessionProcessManager(
        SessionProcessOptions options,
        ISessionIsolationStrategy sessionIsolationStrategy,
        RunnerVfsEnvironmentBuilder vfsEnvironmentBuilder,
        ILinuxCommandRunner signalCommandRunner,
        SessionProcessLogRelay? processLogRelay = null,
        HttpMessageHandler? httpMessageHandler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _sessionIsolationStrategy = sessionIsolationStrategy ?? throw new ArgumentNullException(nameof(sessionIsolationStrategy));
        _vfsEnvironmentBuilder = vfsEnvironmentBuilder ?? throw new ArgumentNullException(nameof(vfsEnvironmentBuilder));
        _signalCommandRunner = signalCommandRunner ?? throw new ArgumentNullException(nameof(signalCommandRunner));
        _processLogRelay = processLogRelay;
        _sessionRoot = options.SessionRoot;
        _entryPointPath = options.EntryPointPath;
        _workingDirectory = options.WorkingDirectory;
        _bundledCustomNodesSeedPath = options.BundledCustomNodesSeedPath;
        _bundledInputSeedPath = options.BundledInputSeedPath;
        _seedVirtualEnvPath = options.SeedVirtualEnvPath;
        _uvBinaryPath = options.UvBinaryPath;
        _runtimeKind = string.IsNullOrWhiteSpace(options.RuntimeKind) ? "comfyui" : options.RuntimeKind.Trim().ToLowerInvariant();
        _readyPath = string.IsNullOrWhiteSpace(options.ReadyPath) ? "/" : options.ReadyPath.Trim();
        _comfyPort = options.ComfyPort;
        _shutdownGracePeriod = options.ShutdownGracePeriod;
        _readyTimeout = options.ReadyTimeout;
        _readyHost = options.ReadyHost;

        var handler = httpMessageHandler ?? new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(2)
        };
        _httpClient = new HttpClient(handler);

        Directory.CreateDirectory(_sessionRoot);
    }

    public string ComfyHost => _readyHost;
    public int ComfyPort => _comfyPort;
    public string RuntimeKind => _runtimeKind;

    private static string SessionLogCategory(string sessionId)
        => $"session:{sessionId}";

    private static void LogSessionInfo(string sessionId, string message)
        => RunnerLog.Info<SessionProcessManager>($"[session:{sessionId}] {message}");

    private static void LogSessionDebug(string sessionId, string message)
        => RunnerLog.Debug<SessionProcessManager>($"[session:{sessionId}] {message}");

    private static void LogSessionWarning(string sessionId, string message)
        => RunnerLog.Warning<SessionProcessManager>($"[session:{sessionId}] {message}");

    public Task StartSessionAsync(StartSessionCommand command, CancellationToken cancellationToken)
        => StartSessionAsync(command, initializer: null, cancellationToken);

    public Task StartSessionAsync(
        StartSessionCommand command,
        Func<SessionPaths, CancellationToken, Task>? initializer,
        CancellationToken cancellationToken)
        => StartSessionAsync(command, initializer, afterWorkspaceRootsPrepared: null, cancellationToken);

    public async Task StartSessionAsync(
        StartSessionCommand command,
        Func<SessionPaths, CancellationToken, Task>? initializer,
        Func<SessionPaths, CancellationToken, Task>? afterWorkspaceRootsPrepared,
        CancellationToken cancellationToken)
    {
        if (command == null) throw new ArgumentNullException(nameof(command));
        if (string.IsNullOrWhiteSpace(command.SessionId)) throw new ArgumentException("Session id is required.", nameof(command));

        var sessionId = command.SessionId.Trim();

        var runtimeIdentity = new RuntimeIdentity(command.LifecycleGeneration, command.RuntimeEpoch,
            Guid.TryParse(command.RuntimeInstanceId, out var instance) && instance != Guid.Empty ? instance.ToString("n") : string.Empty);
        if (!runtimeIdentity.IsValid)
            throw new ArgumentException("An exact lifecycle generation, runtime epoch and instance are required.", nameof(command));
        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            if (existing.RuntimeIdentity != runtimeIdentity || existing.StopInProgress || existing.Process.HasExited)
                throw new InvalidOperationException("The tracked session is a different or retired runtime.");
            LogSessionInfo(sessionId, "Already running at the exact identity; ignoring duplicate start.");
            return;
        }

        // Offloadr API currently schedules a single active session per runner.
        if (!_sessions.IsEmpty)
        {
            throw new InvalidOperationException("Concurrent sessions are not supported yet on this runner.");
        }

        PreparedSessionIdentity identity = default;
        SessionContext? launchedContext = null;
        var prepared = false;

        try
        {
            Directory.CreateDirectory(_sessionRoot);
            RemoveStaleSessionHome(sessionId);
            identity = await _sessionIsolationStrategy.PrepareAsync(sessionId, _sessionRoot, cancellationToken).ConfigureAwait(false);
            prepared = true;
            _preparedIdentities[sessionId] = identity;
            var paths = await PrepareSessionDirectoriesAsync(sessionId, identity.HomeDirectory, cancellationToken).ConfigureAwait(false);

            // Copy bundled input seeds before the initializer can create input catalog placeholders.
            PrepareBundledInputSeeds(paths);
            if (initializer is not null)
            {
                await initializer(paths, cancellationToken).ConfigureAwait(false);
            }

            PrepareBundledCustomNodeSeeds(paths);
            if (afterWorkspaceRootsPrepared is not null)
            {
                await afterWorkspaceRootsPrepared(paths, cancellationToken).ConfigureAwait(false);
            }

            PrepareManagerPathShims(paths);
            if (string.Equals(_runtimeKind, "comfyui", StringComparison.OrdinalIgnoreCase))
            {
                EnsureManagerConfig(paths.UserDirectory);
            }

            if (identity.CleanupIdentity)
            {
                // PrepareAsync performs an initial chown, but session directories and startup-seeded files
                // are created afterward as the runner-agent account. Re-apply ownership once startup
                // provisioning is complete so the isolated session user owns the full runtime tree.
                await _signalCommandRunner
                    .RunAsync(LinuxCommandFactory.ChownRecursive(identity.UserName, identity.HomeDirectory), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.Equals(_runtimeKind, "comfyui", StringComparison.OrdinalIgnoreCase))
            {
                await TryRunManagerUvSyncAsync(sessionId, identity, paths, cancellationToken).ConfigureAwait(false);
            }

            launchedContext = await LaunchSessionProcessAsync(sessionId, runtimeIdentity, identity, paths).ConfigureAwait(false);
            if (!_sessions.TryAdd(sessionId, launchedContext))
            {
                throw new InvalidOperationException($"Failed to track session '{sessionId}'.");
            }

            _ = MonitorExitAsync(launchedContext);
            LogSessionInfo(sessionId, $"Started pid={launchedContext.Process.Id} user={identity.UserName} port={_comfyPort}.");
        }
        catch
        {
            if (launchedContext is not null)
            {
                await CleanupUntrackedLaunchAsync(launchedContext).ConfigureAwait(false);
            }
            else if (prepared)
            {
                await CleanupPreparedStartupAsync(identity).ConfigureAwait(false);
            }
            throw;
        }
    }

    public Task StopSessionAsync(string sessionId, CancellationToken cancellationToken)
        => StopSessionAsync(sessionId, beforeCleanup: null, cancellationToken);

    internal async Task StopSessionAsync(
        string sessionId,
        Func<string, CancellationToken, Task>? beforeCleanup,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        sessionId = sessionId.Trim();

        if (!_sessions.TryGetValue(sessionId, out var context))
        {
            if (beforeCleanup is not null)
            {
                await beforeCleanup(sessionId, cancellationToken).ConfigureAwait(false);
            }

            LogSessionInfo(sessionId, "Stop requested for unknown session.");
            return;
        }

        if (!context.TryBeginStop(out var stopCompletion, out var inFlightStop))
        {
            // A concurrent caller owns termination. Returning now would let this
            // caller clear state and acknowledge while the child is still alive.
            LogSessionInfo(sessionId, "Stop already in progress; awaiting it.");
            await inFlightStop.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        context.Cancellation.Cancel();
        var beforeCleanupCompleted = false;
        try
        {
            await TerminateProcessAsync(context, cancellationToken).ConfigureAwait(false);
            if (beforeCleanup is not null)
            {
                await beforeCleanup(sessionId, cancellationToken).ConfigureAwait(false);
                beforeCleanupCompleted = true;
            }

            await CleanupTrackedContextAsync(context, cancellationToken).ConfigureAwait(false);
            stopCompletion.TrySetResult();
        }
        catch (Exception ex)
        {
            context.ResetStopState();

            try
            {
                if (context.Process.HasExited)
                {
                    if (!beforeCleanupCompleted)
                    {
                        await RunBestEffortBeforeCleanupAsync(beforeCleanup, sessionId).ConfigureAwait(false);
                    }

                    await CleanupTrackedContextAsync(context, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                stopCompletion.TrySetException(ex);
                _ = stopCompletion.Task.Exception;
            }

            throw;
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken)
    {
        var sessionIds = _sessions.Keys.ToArray();
        foreach (var sessionId in sessionIds)
        {
            try
            {
                await StopSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RunnerLog.Error<SessionProcessManager>(ex, $"Failed to stop session '{sessionId}': {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        foreach (var entry in _sessions.Values)
        {
            entry.Cancellation.Cancel();
            try { entry.Process.Dispose(); } catch { }
        }
        _sessions.Clear();
        try { _httpClient.Dispose(); } catch { }
    }

    public bool TryGetSessionPaths(string sessionId, out SessionPaths paths)
    {
        if (_sessions.TryGetValue(sessionId, out var context))
        {
            paths = context.Paths;
            return true;
        }

        paths = default;
        return false;
    }

    public string GetActiveSessionId()
    {
        return _sessions.Keys.FirstOrDefault() ?? string.Empty;
    }

    /// <summary>
    /// Removes session users and homes left by an earlier run of the agent. Does nothing once a
    /// session has been prepared, so it can never touch a live session.
    /// </summary>
    public async Task CleanupStaleSessionsAsync(CancellationToken cancellationToken)
    {
        if (!_sessions.IsEmpty || !_preparedIdentities.IsEmpty)
        {
            return;
        }

        try
        {
            await _sessionIsolationStrategy.CleanupStaleAsync(_sessionRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RunnerLog.Error<SessionProcessManager>(ex, $"Failed to remove stale session state: {ex.Message}");
        }
    }

    /// <summary>The uid of the Linux user prepared for <paramref name="sessionId"/>, if any.</summary>
    public uint? GetSessionUserId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return _preparedIdentities.TryGetValue(sessionId.Trim(), out var identity) ? identity.UserId : null;
    }

    public async Task WaitForSessionReadyAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Session id is required.", nameof(sessionId));
        sessionId = sessionId.Trim();

        var deadline = DateTime.UtcNow + _readyTimeout;
        var delay = TimeSpan.FromMilliseconds(500);

        LogSessionInfo(sessionId, $"Waiting for ready endpoint {_readyHost}:{_comfyPort}{_readyPath}...");

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_sessions.TryGetValue(sessionId, out var context))
            {
                throw new InvalidOperationException($"Session '{sessionId}' is not tracked (likely stopped).");
            }

            if (context.Process.HasExited)
            {
                throw new InvalidOperationException($"Session '{sessionId}' exited before becoming ready (code {context.Process.ExitCode}).");
            }

            if (await ProbeRuntimeAsync(cancellationToken).ConfigureAwait(false))
            {
                LogSessionInfo(sessionId, $"Ready on port {_comfyPort}.");
                return;
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out after {_readyTimeout.TotalSeconds:F0}s waiting for session '{sessionId}' to expose editor runtime '{_runtimeKind}' on {_readyHost}:{_comfyPort}.");
    }

    public Task<PromptSubmissionResult> SubmitEditorActionAsync(SubmitPromptCommand command, CancellationToken cancellationToken)
        => SubmitEditorActionAsync(command, responseEventSink: null, cancellationToken);

    public async Task<PromptSubmissionResult> SubmitEditorActionAsync(
        SubmitPromptCommand command,
        Func<string, ReadOnlyMemory<byte>, CancellationToken, Task>? responseEventSink,
        CancellationToken cancellationToken,
        Action? onNativeRequestAttempt = null)
    {
        if (command == null) throw new ArgumentNullException(nameof(command));
        var sessionId = command.SessionId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("session_id required", nameof(command));

        var context = RequirePromptContext(command, cancellationToken);
        using var runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Cancellation.Token);
        cancellationToken = runtimeCancellation.Token;

        var method = string.IsNullOrWhiteSpace(command.HttpMethod)
            ? HttpMethod.Post
            : new HttpMethod(command.HttpMethod.Trim().ToUpperInvariant());
        var path = string.IsNullOrWhiteSpace(command.HttpPath)
            ? "api/prompt"
            : command.HttpPath.Trim().TrimStart('/');
        if (string.Equals(command.EditorRuntimeKind, "comfyui", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(command.HttpPath))
        {
            path = "api/prompt";
        }

        var promptJson = command.PromptJson ?? "{}";
        if (IsForgeRuntime(command) &&
            string.Equals(method.Method, HttpMethod.Post.Method, StringComparison.OrdinalIgnoreCase) &&
            PathMatchesRoute(path, "queue/join"))
        {
            promptJson = PrepareForgePromptJson(command, context);
            await ReplayForgePreflightsAsync(command, context, cancellationToken, onNativeRequestAttempt).ConfigureAwait(false);
            promptJson = await NormalizeForgeQueueJoinDataArityAsync(promptJson, command, context, cancellationToken).ConfigureAwait(false);
        }

        var builder = new UriBuilder("http", _readyHost, _comfyPort, path);
        if (!string.IsNullOrWhiteSpace(command.HttpQuery))
        {
            builder.Query = command.HttpQuery.TrimStart('?');
        }

        using var request = new HttpRequestMessage(method, builder.Uri);
        if (!HttpMethodsWithoutBody.Contains(method.Method))
        {
            var contentType = string.IsNullOrWhiteSpace(command.ContentType) ? "application/json" : command.ContentType.Trim();
            var content = new StringContent(promptJson, Encoding.UTF8);
            if (System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var parsedContentType))
            {
                content.Headers.ContentType = parsedContentType;
            }
            else
            {
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            }

            request.Content = content;
        }

        EnsurePromptContext(command, context, cancellationToken);
        onNativeRequestAttempt?.Invoke();
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var success = response.IsSuccessStatusCode;
        var message = success ? "Prompt submitted" : response.ReasonPhrase ?? "Prompt submission failed";
        var responseContentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
        if (responseEventSink is not null && success)
        {
            await StreamResponseChunksAsync(response.Content,
                (chunk, token) => responseEventSink(ServiceClientManager.HttpResponseChunkEventType, chunk, token),
                cancellationToken).ConfigureAwait(false);

            return new PromptSubmissionResult(success, (int)response.StatusCode, [], message, responseContentType);
        }

        // The response headers prove the synchronous native call finished. A body
        // too large to retain is omitted explicitly rather than turned into an
        // unknown outcome that would hold the runner's mutation lane.
        var responseBody = await TryReadNativeResponseBytesAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return responseBody is null
            ? new PromptSubmissionResult(success, (int)response.StatusCode, [], message, responseContentType, BodyOmitted: true)
            : new PromptSubmissionResult(success, (int)response.StatusCode, responseBody, message, responseContentType);
    }

    public async Task<EditorRuntimeRelayResult> RelayEditorRuntimeRequestAsync(
        RelayEditorRuntimeRequestCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var sessionId = command.SessionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessions.TryGetValue(sessionId, out var context))
        {
            throw new InvalidOperationException($"Session '{sessionId}' is not running.");
        }

        var expected = new RuntimeIdentity(command.LifecycleGeneration, command.RuntimeEpoch,
            Guid.TryParse(command.RuntimeInstanceId, out var instance) && instance != Guid.Empty ? instance.ToString("n") : string.Empty);
        if (!expected.IsValid) throw new ArgumentException("The relay requires an exact runtime identity.", nameof(command));
        using var runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Cancellation.Token);
        cancellationToken = runtimeCancellation.Token;
        void EnsureCurrent()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.StopInProgress || context.Process.HasExited || context.RuntimeIdentity != expected
                || !_sessions.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, context))
                throw new InvalidOperationException("The captured relay runtime is stopped, replaced or mismatched.");
        }
        EnsureCurrent();

        var method = new HttpMethod(string.IsNullOrWhiteSpace(command.Method) ? "GET" : command.Method.Trim());
        var path = (command.Path ?? string.Empty).Trim().TrimStart('/');
        var builder = new UriBuilder("http", _readyHost, _comfyPort, path);
        if (!string.IsNullOrWhiteSpace(command.Query))
        {
            builder.Query = command.Query.TrimStart('?');
        }

        using var request = new HttpRequestMessage(method, builder.Uri);
        if (!HttpMethodsWithoutBody.Contains(method.Method))
        {
            var content = new ByteArrayContent(command.Body.ToByteArray());
            if (System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(command.ContentType, out var parsedContentType))
            {
                content.Headers.ContentType = parsedContentType;
            }
            request.Content = content;
        }

        EnsureCurrent();
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await responseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (body.Length + read > ComfyRuntimeTransportLimits.MaxResponseBodyBytes)
                {
                    return new EditorRuntimeRelayResult(413, [], "application/json");
                }

                await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new EditorRuntimeRelayResult(
            (int)response.StatusCode,
            body.ToArray(),
            response.Content.Headers.ContentType?.ToString() ?? string.Empty);
    }

    private string PrepareForgePromptJson(SubmitPromptCommand command, SessionContext context)
        => PrepareForgePromptJson(command.PromptJson ?? "{}", command.ArtifactReferences, context.Paths);

    internal static string PrepareForgePromptJson(
        string promptJson,
        IEnumerable<PromptArtifactReference> artifactReferences,
        SessionPaths paths)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var reference in artifactReferences)
        {
            if (string.IsNullOrWhiteSpace(reference.Placeholder) ||
                string.IsNullOrWhiteSpace(reference.Filename))
            {
                continue;
            }

            var (artifactRoot, relativePath, localPath) = ResolvePromptArtifactPath(paths, reference);
            var separatorIndex = relativePath.LastIndexOf('/');
            if (separatorIndex > 0)
            {
                // The session owns its artifact roots; create subfolders without
                // following symlinks so they stay beneath the root.
                Directory.CreateDirectory(artifactRoot);
                using var secureRoot = new LinuxSecureDirectoryRoot(artifactRoot);
                secureRoot.EnsureDirectory(relativePath[..separatorIndex]);
            }

            replacements[reference.Placeholder] = localPath;
        }

        if (replacements.Count == 0)
        {
            return promptJson;
        }

        var root = JsonNode.Parse(promptJson) ??
            throw new JsonException("Forge prompt JSON cannot be empty.");
        RewriteForgeArtifactPlaceholders(root, replacements);
        return root.ToJsonString();
    }

    private static void RewriteForgeArtifactPlaceholders(JsonNode node, IReadOnlyDictionary<string, string> replacements)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(static property => property.Key).ToArray())
            {
                var child = obj[key];
                if (TryResolveForgeArtifactPlaceholder(child, replacements, out var localPath))
                {
                    obj[key] = localPath;
                }
                else if (child is not null)
                {
                    RewriteForgeArtifactPlaceholders(child, replacements);
                }
            }

            return;
        }

        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var child = array[i];
                if (TryResolveForgeArtifactPlaceholder(child, replacements, out var localPath))
                {
                    array[i] = localPath;
                }
                else if (child is not null)
                {
                    RewriteForgeArtifactPlaceholders(child, replacements);
                }
            }
        }
    }

    private static bool TryResolveForgeArtifactPlaceholder(
        JsonNode? node,
        IReadOnlyDictionary<string, string> replacements,
        out string localPath)
    {
        localPath = string.Empty;
        if (node is not JsonValue value ||
            !value.TryGetValue<string>(out var text) ||
            text is null ||
            !replacements.TryGetValue(text, out var replacement))
        {
            return false;
        }

        localPath = replacement;
        return true;
    }

    private async Task ReplayForgePreflightsAsync(SubmitPromptCommand command, SessionContext context, CancellationToken cancellationToken, Action? onNativeRequestAttempt)
    {
        foreach (var preflight in command.PreflightRequests)
        {
            if (string.IsNullOrWhiteSpace(preflight.Path))
            {
                continue;
            }

            var method = string.IsNullOrWhiteSpace(preflight.Method)
                ? HttpMethod.Post
                : new HttpMethod(preflight.Method.Trim().ToUpperInvariant());
            var path = preflight.Path.Trim().TrimStart('/');
            var builder = new UriBuilder("http", _readyHost, _comfyPort, path);
            if (!string.IsNullOrWhiteSpace(preflight.Query))
            {
                builder.Query = preflight.Query.TrimStart('?');
            }

            using var request = new HttpRequestMessage(method, builder.Uri);
            if (!HttpMethodsWithoutBody.Contains(method.Method))
            {
                var content = new StringContent(preflight.Body ?? string.Empty, Encoding.UTF8);
                if (System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(preflight.ContentType, out var parsedContentType))
                {
                    content.Headers.ContentType = parsedContentType;
                }
                else
                {
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                }

                request.Content = content;
            }

            EnsurePromptContext(command, context, cancellationToken);
            onNativeRequestAttempt?.Invoke();
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Forge preflight failed with {(int)response.StatusCode}; its effect is uncertain.");
            }
        }
    }

    private async Task<string> NormalizeForgeQueueJoinDataArityAsync(string promptJson, SubmitPromptCommand command, SessionContext context, CancellationToken cancellationToken)
    {
        try
        {
            var builder = new UriBuilder("http", _readyHost, _comfyPort, "config");
            EnsurePromptContext(command, context, cancellationToken);
            var configJson = await _httpClient.GetStringAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
            var normalized = NormalizeForgeQueueJoinDataArity(promptJson, configJson, out var adjustment);
            if (adjustment is not null)
            {
                LogSessionWarning(
                    command.SessionId,
                    $"Adjusted Forge queue/join data arity fn_index={adjustment.FnIndex} received={adjustment.ReceivedCount} expected={adjustment.ExpectedCount}.");
            }

            return normalized;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogSessionWarning(command.SessionId, $"Failed to normalize Forge queue/join data arity: {ex.Message}");
            return promptJson;
        }
    }

    internal static string NormalizeForgeQueueJoinDataArity(
        string promptJson,
        string configJson,
        out ForgeQueueDataArityAdjustment? adjustment)
    {
        adjustment = null;
        var root = JsonNode.Parse(promptJson) as JsonObject;
        if (root is null ||
            !TryReadForgeQueueFnIndex(root, out var fnIndex) ||
            root["data"] is not JsonArray data ||
            TryGetForgeDependencyInputCount(configJson, fnIndex) is not { } expectedCount ||
            expectedCount <= data.Count)
        {
            return promptJson;
        }

        var receivedCount = data.Count;
        while (data.Count < expectedCount)
        {
            data.Add((JsonNode?)null);
        }

        adjustment = new ForgeQueueDataArityAdjustment(fnIndex, receivedCount, expectedCount);
        return root.ToJsonString();
    }

    private static bool TryReadForgeQueueFnIndex(JsonObject root, out int fnIndex)
    {
        fnIndex = -1;
        return root.TryGetPropertyValue("fn_index", out var node) &&
            node is JsonValue value &&
            ((value.TryGetValue<int>(out fnIndex)) ||
             (value.TryGetValue<string>(out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out fnIndex)));
    }

    private static int? TryGetForgeDependencyInputCount(string configJson, int fnIndex)
    {
        if (fnIndex < 0)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(configJson);
        if (!doc.RootElement.TryGetProperty("dependencies", out var dependencies) ||
            dependencies.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? positionalFallback = null;
        var index = 0;
        foreach (var dependency in dependencies.EnumerateArray())
        {
            var hasDependencyId = TryReadForgeDependencyId(dependency, out var dependencyId);
            if (hasDependencyId && dependencyId == fnIndex)
            {
                return TryReadForgeDependencyInputCount(dependency);
            }

            if (index == fnIndex && !hasDependencyId)
            {
                positionalFallback = dependency;
            }

            index++;
        }

        return positionalFallback is { } dependencyByIndex
            ? TryReadForgeDependencyInputCount(dependencyByIndex)
            : null;
    }

    private static bool TryReadForgeDependencyId(JsonElement dependency, out int dependencyId)
    {
        dependencyId = -1;
        if (!dependency.TryGetProperty("id", out var idNode))
        {
            return false;
        }

        return idNode.ValueKind switch
        {
            JsonValueKind.Number => idNode.TryGetInt32(out dependencyId),
            JsonValueKind.String => int.TryParse(idNode.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out dependencyId),
            _ => false
        };
    }

    private static int? TryReadForgeDependencyInputCount(JsonElement dependency)
    {
        return dependency.TryGetProperty("inputs", out var inputs) &&
            inputs.ValueKind == JsonValueKind.Array
            ? inputs.GetArrayLength()
            : null;
    }

    private static (string Root, string RelativePath, string FullPath) ResolvePromptArtifactPath(SessionPaths paths, PromptArtifactReference reference)
    {
        var root = string.Equals(reference.Type, "output", StringComparison.OrdinalIgnoreCase)
            ? paths.OutputDirectory
            : paths.TempDirectory;
        if (string.IsNullOrWhiteSpace(reference.Filename))
        {
            throw new InvalidOperationException("Prompt artifact filename is required.");
        }

        if (!SessionArtifactPaths.TryResolve(root, reference.Subfolder, reference.Filename, out var candidate, out var relativePath))
        {
            throw new InvalidOperationException("Prompt artifact path escapes the session root.");
        }

        return (root, relativePath, candidate);
    }

    private static bool IsForgeRuntime(SubmitPromptCommand command)
        => string.Equals(command.EditorRuntimeKind, "forge-neo", StringComparison.OrdinalIgnoreCase);

    private static bool PathMatchesRoute(string path, string route)
        => path.Equals(route, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith($"{route}/", StringComparison.OrdinalIgnoreCase);

    internal static string BuildForgeProgressRequestJson(string taskId)
        => new JsonObject
        {
            ["id_task"] = taskId,
            ["id_live_preview"] = -1
        }.ToJsonString();

    private static async Task StreamResponseChunksAsync(
        HttpContent content,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> responseChunkSink,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                await responseChunkSink(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<SessionPaths> PrepareSessionDirectoriesAsync(
        string sessionId,
        string homeDirectory,
        CancellationToken cancellationToken)
    {
        // Ensure home exists (useradd --create-home should have handled this)
        Directory.CreateDirectory(homeDirectory);

        var paths = new SessionPaths
        {
            HomeDirectory = homeDirectory,
            UserDirectory = Path.Combine(homeDirectory, "user"),
            CustomNodesDirectory = Path.Combine(homeDirectory, "custom_nodes"),
            ModelsDirectory = Path.Combine(homeDirectory, "models"),
            VirtualEnvDirectory = Path.Combine(homeDirectory, ".venv"),
            OutputDirectory = Path.Combine(homeDirectory, "output"),
            InputDirectory = Path.Combine(homeDirectory, "input"),
            TempDirectory = Path.Combine(homeDirectory, "temp"),
            ScratchDirectory = Path.Combine(homeDirectory, "tmp"),
            CacheDirectory = Path.Combine(homeDirectory, ".cache"),
            WebDirectory = Path.Combine(homeDirectory, "web"),
            UvCacheDirectory = Path.Combine(homeDirectory, ".cache", "uv"),
            TorchInductorCacheDirectory = Path.Combine(homeDirectory, "tmp", "torchinductor"),
            LogsDirectory = Path.Combine(homeDirectory, "logs")
        };

        Directory.CreateDirectory(paths.UserDirectory);
        Directory.CreateDirectory(paths.CustomNodesDirectory);
        Directory.CreateDirectory(paths.OutputDirectory);
        Directory.CreateDirectory(paths.InputDirectory);
        Directory.CreateDirectory(paths.TempDirectory);
        Directory.CreateDirectory(paths.ScratchDirectory);
        Directory.CreateDirectory(paths.CacheDirectory);
        Directory.CreateDirectory(paths.WebDirectory);
        Directory.CreateDirectory(paths.UvCacheDirectory);
        Directory.CreateDirectory(paths.TorchInductorCacheDirectory);
        Directory.CreateDirectory(paths.LogsDirectory);
        await PrepareSessionVirtualEnvAsync(sessionId, paths, cancellationToken).ConfigureAwait(false);

        var projectedModelsRoot = ResolveProjectedModelsRoot(paths);
        EnsureProjectedModelsRoot(projectedModelsRoot, _sessionRoot);
        EnsureSessionModelsRoot(paths.ModelsDirectory, projectedModelsRoot);

        // Touch log file for stdout/err capture if needed later
        return paths;
    }

    private Task<SessionContext> LaunchSessionProcessAsync(
        string sessionId,
        RuntimeIdentity runtimeIdentity,
        PreparedSessionIdentity identity,
        SessionPaths paths)
    {
        Process? process = null;

        try
        {
            var sessionCategory = SessionLogCategory(sessionId);
            var psi = BuildEditorStartInfo(sessionId, identity, paths);

            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            process.Exited += (_, __) => exitTcs.TrySetResult(process.ExitCode);

            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start editor runtime '{_runtimeKind}' for session '{sessionId}'.");
            }

            // Session output is untrusted: read it with a bounded line reader rather than
            // BeginOutputReadLine, which buffers a line of any length.
            _ = BoundedLineReader.PumpAsync(process.StandardOutput, line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    RunnerLog.Info(sessionCategory, line);
                    _processLogRelay?.Enqueue(sessionId, SessionProcessLogStream.Stdout, line);
                }
            });
            _ = BoundedLineReader.PumpAsync(process.StandardError, line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    RunnerLog.Info(sessionCategory, line);
                    _processLogRelay?.Enqueue(sessionId, SessionProcessLogStream.Stderr, line);
                }
            });

            return Task.FromResult(new SessionContext(sessionId, runtimeIdentity, identity, paths, process, exitTcs.Task));
        }
        catch
        {
            CleanupFailedLaunch(process);
            throw;
        }
    }

    private void AddRuntimeArguments(ProcessStartInfo psi, SessionPaths paths)
    {
        foreach (var arg in BuildRuntimeArguments(_runtimeKind, _comfyPort, paths, ResolveProjectedModelsRoot(paths)))
        {
            psi.ArgumentList.Add(arg);
        }
    }

    internal static IReadOnlyList<string> BuildRuntimeArguments(
        string runtimeKind,
        int port,
        SessionPaths paths,
        string projectedModelsRoot)
    {
        var args = new List<string>
        {
            "--port",
            port.ToString(CultureInfo.InvariantCulture)
        };

        if (string.Equals(runtimeKind, "forge-neo", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--api");
            args.Add("--data-dir");
            args.Add(paths.HomeDirectory);
            args.Add("--model-ref");
            args.Add(projectedModelsRoot);
            return args;
        }

        args.Add("--disable-auto-launch");
        args.Add("--base-directory");
        args.Add(paths.HomeDirectory);
        return args;
    }

    private Task MonitorExitAsync(SessionContext context)
    {
        return Task.Run(async () =>
        {
            int? exitCode = null;
            try
            {
                exitCode = await context.ExitTask.ConfigureAwait(false);
                LogSessionExit(context, exitCode.Value);
            }
            catch (Exception ex)
            {
                RunnerLog.Error<SessionProcessManager>(ex, $"Session '{context.SessionId}' exit watcher failed: {ex.Message}");
            }
            finally
            {
                await CleanupAfterExitAsync(context, exitCode, CancellationToken.None).ConfigureAwait(false);
            }
        });
    }

    private static void LogSessionExit(SessionContext context, int exitCode)
    {
        if (!context.StopInProgress && exitCode != 0)
        {
            LogSessionWarning(context.SessionId, $"Exited unexpectedly code={exitCode}.");
            return;
        }

        LogSessionInfo(context.SessionId, $"Exited code={exitCode}.");
    }

    private async Task TerminateProcessAsync(SessionContext context, CancellationToken cancellationToken)
    {
        var proc = context.Process;
        if (proc.HasExited)
        {
            await context.ExitTask.ConfigureAwait(false);
            return;
        }

        LogSessionInfo(context.SessionId, $"Stopping pid={proc.Id}...");

        await SendSignalAsync(proc.Id, "TERM", cancellationToken).ConfigureAwait(false);
        var completed = await WaitForExitAsync(context.ExitTask, _shutdownGracePeriod, cancellationToken).ConfigureAwait(false);
        if (!completed && !proc.HasExited)
        {
            LogSessionWarning(context.SessionId, $"Did not exit after {_shutdownGracePeriod.TotalSeconds:F0}s; killing.");
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                RunnerLog.Error<SessionProcessManager>(ex, $"Failed to kill session '{context.SessionId}' process: {ex.Message}");
            }

            // Give a short window for the process tree to exit
            await WaitForExitAsync(context.ExitTask, ExitWaitAfterKill, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CleanupAfterExitAsync(SessionContext context, int? exitCode, CancellationToken cancellationToken)
    {
        context.Cancellation.Cancel();
        if (context.StopInProgress)
        {
            return;
        }

        await RunBestEffortUnexpectedExitCleanupAsync(context.SessionId, exitCode, cancellationToken).ConfigureAwait(false);
        await CleanupTrackedContextAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunBestEffortUnexpectedExitCleanupAsync(
        string sessionId,
        int? exitCode,
        CancellationToken cancellationToken)
    {
        var cleanup = UnexpectedSessionExitCleanup;
        if (cleanup is null)
        {
            return;
        }

        try
        {
            await cleanup(sessionId, exitCode, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunnerLog.Error<SessionProcessManager>(
                ex,
                $"Unexpected-exit cleanup hook failed for session '{sessionId}': {ex.Message}");
        }
    }

    private async Task CleanupTrackedContextAsync(SessionContext context, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(context.SessionId, out var tracked) || !ReferenceEquals(tracked, context))
        {
            return;
        }

        if (context.TryBeginCleanup())
        {
            // Keep the retired context visible until its filesystem cleanup is
            // finished. Otherwise a same-ID replacement can create a home which
            // the old cleanup then removes. Removal also compares the exact value.
            try { await CleanupContextAsync(context, cancellationToken).ConfigureAwait(false); }
            finally
            {
                ((ICollection<KeyValuePair<string, SessionContext>>)_sessions).Remove(new(context.SessionId, context));
            }
        }
    }

    private async Task RunBestEffortBeforeCleanupAsync(
        Func<string, CancellationToken, Task>? beforeCleanup,
        string sessionId)
    {
        if (beforeCleanup is null)
        {
            return;
        }

        try
        {
            await beforeCleanup(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunnerLog.Error<SessionProcessManager>(ex, $"Best-effort cleanup hook failed for session '{sessionId}': {ex.Message}");
        }
    }

    private async Task CleanupContextAsync(SessionContext context, CancellationToken cancellationToken)
    {
        context.Cancellation.Cancel();
        try { context.Process.Dispose(); } catch { }

        await CleanupPreparedStartupAsync(context.Identity).ConfigureAwait(false);
    }

    private async Task CleanupPreparedStartupAsync(PreparedSessionIdentity identity)
    {
        // Stop authorizing the uid before its processes are killed and the user is removed.
        foreach (var prepared in _preparedIdentities.Where(pair => pair.Value == identity).ToArray())
        {
            ((ICollection<KeyValuePair<string, PreparedSessionIdentity>>)_preparedIdentities).Remove(prepared);
        }

        try
        {
            await _sessionIsolationStrategy.CleanupAsync(identity, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunnerLog.Error<SessionProcessManager>(ex, $"Failed to cleanup isolation for user '{identity.UserName}': {ex.Message}");
        }

        DeleteSessionHomeDirectory(identity.HomeDirectory);
    }

    /// <summary>
    /// Session homes are removed when their session ends, so an existing home for an untracked
    /// session was left behind by an earlier agent process. Its session user could write to it
    /// and may have planted links that root would follow while seeding and configuring the new
    /// session, so the new session always starts from an empty home.
    /// </summary>
    private void RemoveStaleSessionHome(string sessionId)
    {
        if (sessionId is "." or ".." || sessionId.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            return;
        }

        var root = Path.GetFullPath(_sessionRoot).TrimEnd(Path.DirectorySeparatorChar);
        var home = Path.Combine(root, sessionId);
        if (!string.Equals(Path.GetDirectoryName(home), root, StringComparison.Ordinal))
        {
            return;
        }

        var info = new DirectoryInfo(home);
        if (info.LinkTarget is not null)
        {
            LogSessionWarning(sessionId, "Removing a session home link left by an earlier runner process.");
            File.Delete(home);
        }
        else if (info.Exists)
        {
            LogSessionWarning(sessionId, "Removing a session home left by an earlier runner process.");
            Directory.Delete(home, recursive: true);
        }
        else if (File.Exists(home))
        {
            File.Delete(home);
        }
    }

    private void DeleteSessionHomeDirectory(string homeDirectory)
    {
        if (!Directory.Exists(homeDirectory))
        {
            return;
        }

        if (!SessionHomePaths.IsDirectChildOf(_sessionRoot, homeDirectory))
        {
            RunnerLog.Error<SessionProcessManager>(
                $"Refusing to remove session directory '{homeDirectory}' outside session root '{_sessionRoot}'.");
            return;
        }

        try
        {
            Directory.Delete(homeDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            RunnerLog.Error<SessionProcessManager>(ex, $"Failed to remove session directory '{homeDirectory}': {ex.Message}");
        }
    }

    private async Task SendSignalAsync(int pid, string signal, CancellationToken cancellationToken)
    {
        await _signalCommandRunner.RunAsync(LinuxCommandFactory.SendSignal(pid, signal), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForExitAsync(Task<int> exitTask, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var delayTask = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(exitTask, delayTask).ConfigureAwait(false);
        if (completed == exitTask)
        {
            await exitTask.ConfigureAwait(false);
            return true;
        }
        return false;
    }

    private async Task<bool> ProbeRuntimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var readyPath = string.IsNullOrWhiteSpace(_readyPath) ? "/" : _readyPath;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{_readyHost}:{_comfyPort}{readyPath}");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    internal static IReadOnlyDictionary<string, string> BuildSessionEnvironment(
        string sessionId,
        PreparedSessionIdentity identity,
        SessionPaths paths)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = paths.HomeDirectory,
            ["VIRTUAL_ENV"] = paths.VirtualEnvDirectory,
            ["USER"] = identity.UserName,
            ["LOGNAME"] = identity.UserName,
            ["PATH"] = BuildSessionProcessPath(paths.VirtualEnvDirectory),
            ["XDG_CACHE_HOME"] = paths.CacheDirectory,
            ["UV_CACHE_DIR"] = paths.UvCacheDirectory,
            ["UV_PROJECT_ENVIRONMENT"] = paths.VirtualEnvDirectory,
            ["TORCHINDUCTOR_CACHE_DIR"] = paths.TorchInductorCacheDirectory,
            ["PYTHONUNBUFFERED"] = "1",
            ["TMPDIR"] = paths.ScratchDirectory,
            ["COMFYUI_SESSION_ID"] = sessionId
        };
    }

    private static string BuildSessionProcessPath(string virtualEnvDirectory)
    {
        var inheritedPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(inheritedPath))
        {
            inheritedPath = DefaultProcessPath;
        }

        return $"{CombineRuntimePath(virtualEnvDirectory, "bin")}{Path.PathSeparator}{inheritedPath}";
    }

    internal async Task<bool> TryRunManagerUvSyncAsync(
        string sessionId,
        PreparedSessionIdentity identity,
        SessionPaths paths,
        CancellationToken cancellationToken)
    {
        var pythonExecutable = GetSessionPythonExecutable(paths);
        if (!File.Exists(pythonExecutable))
        {
            return false;
        }

        const int maxAttempts = 3;
        using var detachTransaction = new SeedPayloadDetachTransaction(paths.VirtualEnvDirectory);
        try
        {
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var result = await RunManagerUvSyncOnceAsync(sessionId, identity, paths, cancellationToken).ConfigureAwait(false);
                if (result.ExitCode == 0)
                {
                    detachTransaction.Commit();
                    return true;
                }

                if (attempt < maxAttempts && TryDetachSeedPayloadForFailedUvRemoval(paths, result.Output, detachTransaction))
                {
                    await _signalCommandRunner
                        .RunAsync(LinuxCommandFactory.ChownRecursive(identity.UserName, paths.VirtualEnvDirectory), cancellationToken)
                        .ConfigureAwait(false);
                    LogSessionWarning(sessionId, "Manager dependency sync tried to replace a seeded package; detached session seed links and retrying.");
                    continue;
                }

                detachTransaction.Rollback();
                LogSessionWarning(sessionId, $"Manager dependency sync failed with exit code {result.ExitCode}; continuing startup.");
                return false;
            }

            detachTransaction.Rollback();
            return false;
        }
        catch
        {
            detachTransaction.Rollback();
            throw;
        }
    }

    internal async Task<ProcessRunResult> RunManagerUvSyncOnceAsync(
        string sessionId,
        PreparedSessionIdentity identity,
        SessionPaths paths,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = BuildManagerUvSyncStartInfo(sessionId, identity, paths),
            EnableRaisingEvents = true
        };

        var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        var outputLock = new object();
        void OnOutputLine(string line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                RunnerLog.Info(SessionLogCategory(sessionId), line);
                CaptureProcessOutput(output, outputLock, line);
            }
        }

        process.Exited += (_, __) => exitTcs.TrySetResult(process.ExitCode);

        try
        {
            if (!process.Start())
            {
                LogSessionWarning(sessionId, "Failed to start manager dependency sync; continuing without synced custom node deps.");
                return new ProcessRunResult(1, string.Empty);
            }
        }
        catch (Exception ex)
        {
            LogSessionWarning(sessionId, $"Failed to start manager dependency sync: {ex.Message}; continuing without synced custom node deps.");
            return new ProcessRunResult(1, ex.Message);
        }

        // The sync runs session-controlled package builds; bound each line it prints.
        var stdoutPump = BoundedLineReader.PumpAsync(process.StandardOutput, OnOutputLine);
        var stderrPump = BoundedLineReader.PumpAsync(process.StandardError, OnOutputLine);

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort cancellation.
            }
        });

        var exitCode = await exitTcs.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.WhenAll(stdoutPump, stderrPump).WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (outputLock)
        {
            return new ProcessRunResult(exitCode, output.ToString());
        }
    }

    private static void CaptureProcessOutput(StringBuilder output, object outputLock, string line)
    {
        const int maxCapturedChars = 64 * 1024;
        lock (outputLock)
        {
            output.AppendLine(line);
            if (output.Length > maxCapturedChars)
            {
                output.Remove(0, output.Length - maxCapturedChars);
            }
        }
    }

    private static string GetSessionPythonExecutable(SessionPaths paths)
        => CombineRuntimePath(paths.VirtualEnvDirectory, "bin", "python");

    internal ProcessStartInfo BuildEditorStartInfo(
        string sessionId,
        PreparedSessionIdentity identity,
        SessionPaths paths)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _entryPointPath,
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UserName = identity.UserName,
            CreateNoWindow = true
        };

        AddRuntimeArguments(psi, paths);

        SessionEnvironmentFilter.RemoveNonInheritable(psi.Environment);
        foreach (var kvp in BuildSessionEnvironment(sessionId, identity, paths))
        {
            psi.Environment[kvp.Key] = kvp.Value;
        }

        var vfsEnvironment = _vfsEnvironmentBuilder.Build(
            new RunnerVfsSessionPaths(
                paths.ModelsDirectory,
                paths.OutputDirectory,
                paths.InputDirectory,
                paths.TempDirectory),
            sessionId: sessionId);
        foreach (var kvp in vfsEnvironment)
        {
            psi.Environment[kvp.Key] = kvp.Value;
        }

        return psi;
    }

    internal ProcessStartInfo BuildManagerUvSyncStartInfo(
        string sessionId,
        PreparedSessionIdentity identity,
        SessionPaths paths)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GetSessionPythonExecutable(paths),
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UserName = identity.UserName,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("cm_cli");
        psi.ArgumentList.Add("uv-sync");
        psi.ArgumentList.Add("--user-directory");
        psi.ArgumentList.Add(paths.UserDirectory);

        SessionEnvironmentFilter.RemoveNonInheritable(psi.Environment);
        foreach (var kvp in BuildSessionEnvironment(sessionId, identity, paths))
        {
            psi.Environment[kvp.Key] = kvp.Value;
        }

        psi.Environment["COMFYUI_PATH"] = _workingDirectory;
        return psi;
    }

    private async Task CleanupUntrackedLaunchAsync(SessionContext context)
    {
        try
        {
            if (!context.Process.HasExited)
            {
                context.Process.Kill(entireProcessTree: true);
                await WaitForExitAsync(context.ExitTask, ExitWaitAfterKill, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best effort for failed launch cleanup.
        }
        finally
        {
            CleanupFailedLaunch(context.Process);
        }

        try
        {
            await CleanupPreparedStartupAsync(context.Identity).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunnerLog.Error<SessionProcessManager>(ex, $"Failed to cleanup untracked launch for session '{context.SessionId}': {ex.Message}");
        }
    }

    private void CleanupFailedLaunch(Process? process)
    {
        if (process is not null)
        {
            try { process.Dispose(); } catch { }
        }
    }

    private async Task PrepareSessionVirtualEnvAsync(
        string sessionId,
        SessionPaths paths,
        CancellationToken cancellationToken)
    {
        var targetDirectory = paths.VirtualEnvDirectory;
        Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory)!);

        if (string.IsNullOrWhiteSpace(_seedVirtualEnvPath) || !Directory.Exists(_seedVirtualEnvPath))
        {
            throw new InvalidOperationException($"Seed virtualenv not found at '{_seedVirtualEnvPath}'.");
        }

        var sourceRoot = Path.GetFullPath(_seedVirtualEnvPath);
        var targetRoot = Path.GetFullPath(targetDirectory);
        if (IsSamePath(sourceRoot, targetRoot) || IsPathWithinRoot(targetRoot, sourceRoot))
        {
            throw new InvalidOperationException($"Session virtualenv target '{targetRoot}' must not be inside seed virtualenv '{sourceRoot}'.");
        }

        RemovePathIfPresent(targetRoot);
        await CreateSessionVirtualEnvAsync(
                sessionId,
                sourceRoot,
                targetRoot,
                paths.HomeDirectory,
                paths.UvCacheDirectory,
                _uvBinaryPath,
                cancellationToken)
            .ConfigureAwait(false);
        MaterializeSeedBinTools(sourceRoot, targetRoot);
        MaterializeSeedSitePackagesOverlay(sourceRoot, targetRoot);
    }

    private static async Task CreateSessionVirtualEnvAsync(
        string sessionId,
        string seedVirtualEnvPath,
        string targetVirtualEnvPath,
        string homeDirectory,
        string uvCacheDirectory,
        string uvBinaryPath,
        CancellationToken cancellationToken)
    {
        var seedPython = GetRequiredSeedExecutable(seedVirtualEnvPath, "python");

        Directory.CreateDirectory(Path.GetDirectoryName(targetVirtualEnvPath)!);
        Directory.CreateDirectory(uvCacheDirectory);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = uvBinaryPath,
                WorkingDirectory = Path.GetDirectoryName(targetVirtualEnvPath)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                CreateNoWindow = true
            }
        };

        process.StartInfo.ArgumentList.Add("venv");
        process.StartInfo.ArgumentList.Add("--python");
        process.StartInfo.ArgumentList.Add(seedPython);
        process.StartInfo.ArgumentList.Add(targetVirtualEnvPath);
        process.StartInfo.Environment["HOME"] = homeDirectory;
        process.StartInfo.Environment["UV_CACHE_DIR"] = uvCacheDirectory;
        process.StartInfo.Environment["PATH"] = BuildSeedPreparationPath();

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start runner uv for session '{sessionId}'.");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to start runner uv at '{uvBinaryPath}' for session '{sessionId}': {ex.Message}", ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort cancellation cleanup.
            }

            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to create session virtualenv '{targetVirtualEnvPath}' from seed '{seedVirtualEnvPath}' " +
                $"for session '{sessionId}' (exit {process.ExitCode}): {FormatProcessOutput(stdout, stderr)}");
        }

        var sessionPython = CombineRuntimePath(targetVirtualEnvPath, "bin", "python");
        if (!File.Exists(sessionPython))
        {
            throw new InvalidOperationException($"Seed uv created '{targetVirtualEnvPath}' without expected session python '{sessionPython}'.");
        }
    }

    private static string GetRequiredSeedExecutable(string seedVirtualEnvPath, string executableName)
    {
        var executablePath = CombineRuntimePath(seedVirtualEnvPath, "bin", executableName);
        if (!File.Exists(executablePath))
        {
            throw new InvalidOperationException($"Seed virtualenv is missing required executable '{executablePath}'.");
        }

        return executablePath;
    }

    private static string BuildSeedPreparationPath()
    {
        var inheritedPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(inheritedPath))
        {
            inheritedPath = DefaultProcessPath;
        }

        return inheritedPath;
    }

    private static string CombineRuntimePath(string root, params string[] parts)
    {
        var separator = root.Contains('\\', StringComparison.Ordinal) ? "\\" : "/";
        var trimmedRoot = root.TrimEnd('/', '\\');
        var suffix = string.Join(separator, parts.Select(part => part.Trim('/', '\\')).Where(part => part.Length > 0));
        return string.IsNullOrWhiteSpace(suffix) ? trimmedRoot : $"{trimmedRoot}{separator}{suffix}";
    }

    private static string FormatProcessOutput(string stdout, string stderr)
    {
        var output = string.Join(
            " ",
            new[] { stderr.Trim(), stdout.Trim() }.Where(value => value.Length > 0));
        if (output.Length == 0)
        {
            return "no output";
        }

        const int maxLength = 2000;
        return output.Length <= maxLength ? output : output[..maxLength] + "...";
    }

    private static void MaterializeSeedBinTools(string seedVirtualEnvPath, string targetVirtualEnvPath)
    {
        var seedBinDirectory = Path.Combine(seedVirtualEnvPath, "bin");
        var targetBinDirectory = Path.Combine(targetVirtualEnvPath, "bin");
        if (!Directory.Exists(seedBinDirectory) || !Directory.Exists(targetBinDirectory))
        {
            return;
        }

        foreach (var sourceEntry in Directory.EnumerateFileSystemEntries(seedBinDirectory))
        {
            var entryName = Path.GetFileName(sourceEntry);
            if (string.IsNullOrWhiteSpace(entryName) ||
                IsPythonExecutableName(entryName) ||
                File.GetAttributes(sourceEntry).HasFlag(FileAttributes.Directory))
            {
                continue;
            }

            var targetEntry = Path.Combine(targetBinDirectory, entryName);
            if (FileSystemEntryExists(targetEntry))
            {
                continue;
            }

            MaterializeSeedBinTool(sourceEntry, targetEntry, seedVirtualEnvPath, targetVirtualEnvPath);
        }
    }

    internal static bool TryDetachSeedPayloadForFailedUvRemoval(SessionPaths paths, string processOutput)
    {
        using var detachTransaction = new SeedPayloadDetachTransaction(paths.VirtualEnvDirectory);
        var detached = TryDetachSeedPayloadForFailedUvRemoval(paths, processOutput, detachTransaction);
        if (detached)
        {
            detachTransaction.Commit();
        }
        else
        {
            detachTransaction.Rollback();
        }

        return detached;
    }

    private static bool TryDetachSeedPayloadForFailedUvRemoval(
        SessionPaths paths,
        string processOutput,
        SeedPayloadDetachTransaction detachTransaction)
    {
        if (!processOutput.Contains("failed to remove", StringComparison.OrdinalIgnoreCase) ||
            !processOutput.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The sync ran session code as the session user, which owns the venv by now, while the
        // detach below moves entries as root. Only act on site-packages reached through real
        // directories, so a planted symlink cannot point root at the shared seed or other
        // root-owned trees. The venv's parent is the session home, which the user cannot replace.
        var trustedRoot = Path.GetDirectoryName(Path.GetFullPath(paths.VirtualEnvDirectory));
        if (string.IsNullOrEmpty(trustedRoot) || !IsDirectoryChainWithoutLinks(trustedRoot, paths.VirtualEnvDirectory))
        {
            return false;
        }

        var detached = false;
        foreach (var failedPath in ExtractBacktickPaths(processOutput))
        {
            foreach (var sitePackages in EnumerateSitePackagesDirectories(paths.VirtualEnvDirectory))
            {
                if (!IsDirectoryChainWithoutLinks(trustedRoot, sitePackages))
                {
                    continue;
                }

                if (!TryGetSitePackagesRelativePath(sitePackages, failedPath, out var failedRelativePath, out var topLevelEntry))
                {
                    continue;
                }

                if (DetachSeedPayloadForFailedPath(sitePackages, failedRelativePath, topLevelEntry, detachTransaction, out var metadataEntriesToRemove))
                {
                    RemoveMetadataEntries(metadataEntriesToRemove, detachTransaction);
                    detached = true;
                }
            }
        }

        return detached;
    }

    /// <summary>
    /// True when every component of <paramref name="path"/> below <paramref name="trustedRoot"/>
    /// is an existing directory and none of them is a symbolic link.
    /// </summary>
    internal static bool IsDirectoryChainWithoutLinks(string trustedRoot, string path)
    {
        var root = Path.GetFullPath(trustedRoot);
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return true;
        }

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
        {
            return false;
        }

        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null || !info.Exists)
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> ExtractBacktickPaths(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var start = text.IndexOf('`', index);
            if (start < 0)
            {
                yield break;
            }

            var end = text.IndexOf('`', start + 1);
            if (end < 0)
            {
                yield break;
            }

            var value = text[(start + 1)..end];
            if (Path.IsPathFullyQualified(value))
            {
                yield return value;
            }

            index = end + 1;
        }
    }

    private static bool TryGetSitePackagesRelativePath(
        string sitePackages,
        string failedPath,
        out string failedRelativePath,
        out string topLevelEntry)
    {
        failedRelativePath = string.Empty;
        topLevelEntry = string.Empty;
        var relativePath = Path.GetRelativePath(sitePackages, failedPath);
        if (string.Equals(relativePath, ".", StringComparison.Ordinal) ||
            relativePath.StartsWith("..", StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(relativePath))
        {
            return false;
        }

        failedRelativePath = NormalizeMetadataRelativePath(relativePath);
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var separatorIndex = relativePath.IndexOfAny(separators);
        topLevelEntry = separatorIndex < 0 ? relativePath : relativePath[..separatorIndex];
        return !string.IsNullOrWhiteSpace(topLevelEntry);
    }

    private static bool DetachSeedPayloadForFailedPath(
        string sitePackages,
        string failedRelativePath,
        string topLevelEntry,
        SeedPayloadDetachTransaction detachTransaction,
        out IReadOnlyCollection<string> metadataEntriesToRemove)
    {
        metadataEntriesToRemove = [];
        var targetEntry = Path.Combine(sitePackages, topLevelEntry);
        if (Directory.Exists(targetEntry))
        {
            if (!TryGetDirectorySymlinkTarget(targetEntry, out var sourceDirectory))
            {
                return DetachMaterializedNamespaceSeedPayload(
                    sitePackages,
                    targetEntry,
                    failedRelativePath,
                    topLevelEntry,
                    detachTransaction,
                    out metadataEntriesToRemove);
            }

            var isNamespacePackage = !File.Exists(Path.Combine(sourceDirectory, "__init__.py"));
            metadataEntriesToRemove = FindMetadataEntriesForFailedPath(
                sitePackages,
                failedRelativePath,
                topLevelEntry,
                allowTopLevelFallback: !isNamespacePackage);
            if (metadataEntriesToRemove.Count == 0 && isNamespacePackage)
            {
                return false;
            }

            if (isNamespacePackage)
            {
                if (!MaterializeNamespaceSeedPayload(sitePackages, targetEntry, sourceDirectory, failedRelativePath, topLevelEntry, metadataEntriesToRemove, detachTransaction))
                {
                    return false;
                }
            }
            else
            {
                detachTransaction.RemoveEntry(targetEntry);
            }

            return true;
        }

        if (File.Exists(targetEntry))
        {
            var info = new FileInfo(targetEntry);
            if (info.LinkTarget is null)
            {
                return false;
            }

            metadataEntriesToRemove = FindMetadataEntriesForFailedPath(
                sitePackages,
                failedRelativePath,
                topLevelEntry,
                allowTopLevelFallback: true);
            detachTransaction.RemoveEntry(targetEntry);
            return true;
        }

        return false;
    }

    private static bool DetachMaterializedNamespaceSeedPayload(
        string sitePackages,
        string targetDirectory,
        string failedRelativePath,
        string topLevelEntry,
        SeedPayloadDetachTransaction detachTransaction,
        out IReadOnlyCollection<string> metadataEntriesToRemove)
    {
        metadataEntriesToRemove = FindMetadataEntriesForFailedPath(
            sitePackages,
            failedRelativePath,
            topLevelEntry,
            allowTopLevelFallback: false);
        if (metadataEntriesToRemove.Count == 0)
        {
            return false;
        }

        var childNamesToDetach = GetNamespaceChildNamesToDetach(sitePackages, metadataEntriesToRemove, failedRelativePath, topLevelEntry);
        if (childNamesToDetach.Count == 0)
        {
            return false;
        }

        var detached = false;
        foreach (var childName in childNamesToDetach)
        {
            detached |= DeleteMaterializedNamespaceChild(Path.Combine(targetDirectory, childName), detachTransaction);
        }

        return detached;
    }

    private static bool DeleteMaterializedNamespaceChild(string childPath, SeedPayloadDetachTransaction detachTransaction)
    {
        if (FileSystemEntryExists(childPath))
        {
            detachTransaction.RemoveEntry(childPath);
            return true;
        }

        return false;
    }

    private static bool TryGetDirectorySymlinkTarget(string path, out string sourceDirectory)
    {
        sourceDirectory = string.Empty;
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is null)
        {
            return false;
        }

        sourceDirectory = Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? string.Empty;
        return Directory.Exists(sourceDirectory);
    }

    private static IReadOnlyCollection<string> FindMetadataEntriesForFailedPath(
        string sitePackages,
        string failedRelativePath,
        string topLevelEntry,
        bool allowTopLevelFallback)
    {
        var metadataEntries = Directory.EnumerateFileSystemEntries(sitePackages)
            .Where(entry => IsPythonMetadataEntry(Path.GetFileName(entry)))
            .ToArray();
        var exactMatches = metadataEntries
            .Where(entry => MetadataReferencesRelativePath(entry, sitePackages, failedRelativePath))
            .ToArray();
        if (!allowTopLevelFallback)
        {
            return exactMatches;
        }

        var topLevelMatches = metadataEntries
            .Where(entry => MetadataReferencesTopLevelEntry(entry, topLevelEntry))
            .ToArray();
        return exactMatches
            .Concat(topLevelMatches)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool MaterializeNamespaceSeedPayload(
        string sitePackages,
        string targetDirectory,
        string sourceDirectory,
        string failedRelativePath,
        string topLevelEntry,
        IReadOnlyCollection<string> metadataEntriesToRemove,
        SeedPayloadDetachTransaction detachTransaction)
    {
        var childNamesToDetach = GetNamespaceChildNamesToDetach(sitePackages, metadataEntriesToRemove, failedRelativePath, topLevelEntry);
        if (childNamesToDetach.Count == 0)
        {
            return false;
        }

        detachTransaction.RemoveEntry(targetDirectory);
        Directory.CreateDirectory(targetDirectory);
        foreach (var sourceEntry in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            var childName = Path.GetFileName(sourceEntry);
            if (string.IsNullOrEmpty(childName) || childNamesToDetach.Contains(childName))
            {
                continue;
            }

            LinkPayloadEntry(sourceEntry, Path.Combine(targetDirectory, childName));
        }

        return true;
    }

    private static HashSet<string> GetNamespaceChildNamesToDetach(
        string sitePackages,
        IReadOnlyCollection<string> metadataEntries,
        string failedRelativePath,
        string topLevelEntry)
    {
        var childNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var metadataEntry in metadataEntries)
        {
            foreach (var relativePath in EnumerateMetadataRelativePaths(metadataEntry, sitePackages))
            {
                if (TryGetNamespaceChildName(relativePath, topLevelEntry, out var childName))
                {
                    childNames.Add(childName);
                }
            }
        }

        if (TryGetNamespaceChildName(failedRelativePath, topLevelEntry, out var failedChildName))
        {
            childNames.Add(failedChildName);
        }

        return childNames;
    }

    private static bool TryGetNamespaceChildName(string relativePath, string topLevelEntry, out string childName)
    {
        childName = string.Empty;
        var normalizedPath = NormalizeMetadataRelativePath(relativePath);
        var prefix = $"{topLevelEntry}/";
        if (!normalizedPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remaining = normalizedPath[prefix.Length..];
        var separatorIndex = remaining.IndexOf('/');
        childName = separatorIndex < 0 ? remaining : remaining[..separatorIndex];
        return !string.IsNullOrWhiteSpace(childName) && childName is not "." and not "..";
    }

    private static void RemoveMetadataEntries(IEnumerable<string> metadataEntries, SeedPayloadDetachTransaction detachTransaction)
    {
        foreach (var metadataEntry in metadataEntries)
        {
            detachTransaction.RemoveEntry(metadataEntry);
        }
    }

    private static bool MetadataReferencesTopLevelEntry(string metadataEntry, string topLevelEntry)
    {
        if (!Directory.Exists(metadataEntry))
        {
            return MetadataEntryNameMatchesTopLevelEntry(metadataEntry, topLevelEntry);
        }

        return EnumerateMetadataRelativePaths(metadataEntry, Path.GetDirectoryName(metadataEntry)!)
            .Any(path => MetadataRelativePathReferencesTopLevelEntry(path, topLevelEntry));
    }

    private static bool MetadataReferencesRelativePath(string metadataEntry, string sitePackages, string failedRelativePath)
        => EnumerateMetadataRelativePaths(metadataEntry, sitePackages)
            .Any(path => string.Equals(path, failedRelativePath, StringComparison.Ordinal));

    private static IEnumerable<string> EnumerateMetadataRelativePaths(string metadataEntry, string sitePackages)
    {
        if (!Directory.Exists(metadataEntry))
        {
            yield break;
        }

        foreach (var metadataPathList in new[] { "RECORD", "top_level.txt", "SOURCES.txt", "installed-files.txt" })
        {
            var pathList = Path.Combine(metadataEntry, metadataPathList);
            if (!File.Exists(pathList))
            {
                continue;
            }

            foreach (var line in File.ReadLines(pathList))
            {
                var separatorIndex = line.IndexOf(',');
                var rawEntry = separatorIndex < 0 ? line : line[..separatorIndex];
                foreach (var relativePath in ExpandMetadataRelativePath(sitePackages, metadataEntry, rawEntry))
                {
                    yield return relativePath;
                }
            }
        }
    }

    private static IEnumerable<string> ExpandMetadataRelativePath(string sitePackages, string metadataEntry, string rawEntry)
    {
        var normalizedEntry = NormalizeMetadataRelativePath(rawEntry);
        if (string.IsNullOrEmpty(normalizedEntry))
        {
            yield break;
        }

        yield return normalizedEntry;

        foreach (var baseDirectory in new[] { sitePackages, Path.GetDirectoryName(metadataEntry)! })
        {
            string relativePath;
            try
            {
                var fullPath = Path.IsPathFullyQualified(normalizedEntry)
                    ? normalizedEntry
                    : Path.GetFullPath(Path.Combine(baseDirectory, normalizedEntry));
                relativePath = Path.GetRelativePath(sitePackages, fullPath);
            }
            catch
            {
                continue;
            }

            if (!Path.IsPathFullyQualified(relativePath) && !relativePath.StartsWith("..", StringComparison.Ordinal))
            {
                yield return NormalizeMetadataRelativePath(relativePath);
            }
        }
    }

    private static bool MetadataRelativePathReferencesTopLevelEntry(string relativePath, string topLevelEntry)
    {
        var topLevelSeparatorIndex = relativePath.IndexOf('/');
        var recordTopLevelEntry = topLevelSeparatorIndex < 0 ? relativePath : relativePath[..topLevelSeparatorIndex];
        return string.Equals(recordTopLevelEntry, topLevelEntry, StringComparison.Ordinal);
    }

    private static bool MetadataEntryNameMatchesTopLevelEntry(string metadataEntry, string topLevelEntry)
    {
        var entryName = Path.GetFileName(metadataEntry);
        var suffix = entryName.EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase)
            ? ".dist-info"
            : entryName.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase)
                ? ".egg-info"
                : string.Empty;
        if (string.IsNullOrEmpty(suffix))
        {
            return false;
        }

        return string.Equals(
            NormalizePythonName(entryName[..^suffix.Length]),
            NormalizePythonName(topLevelEntry),
            StringComparison.Ordinal);
    }

    private static string NormalizePythonName(string value)
        => value.Replace('_', '-').Replace('.', '-').ToLowerInvariant();

    private static string NormalizeMetadataRelativePath(string path)
    {
        var normalized = path.Trim().Trim('"').Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private static bool IsPythonExecutableName(string entryName)
        => string.Equals(entryName, "python", StringComparison.Ordinal) ||
            (entryName.StartsWith("python", StringComparison.Ordinal) &&
             entryName.Length > "python".Length &&
             (char.IsAsciiDigit(entryName["python".Length]) || entryName["python".Length] == '.'));

    private static void MaterializeSeedBinTool(
        string sourcePath,
        string targetPath,
        string seedVirtualEnvPath,
        string targetVirtualEnvPath)
    {
        var resolvedSourcePath = ResolveFilePath(sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        if (TryReadSmallUtf8TextFile(resolvedSourcePath, out var text))
        {
            var rewritten = text.Replace(seedVirtualEnvPath, targetVirtualEnvPath, StringComparison.Ordinal);
            File.WriteAllText(targetPath, rewritten, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            CopyUnixFileMode(resolvedSourcePath, targetPath);
            return;
        }

        File.CreateSymbolicLink(targetPath, resolvedSourcePath);
    }

    private static bool TryReadSmallUtf8TextFile(string filePath, out string text)
    {
        text = string.Empty;
        if (!File.Exists(filePath))
        {
            return false;
        }

        var info = new FileInfo(filePath);
        if (info.Length > 1024 * 1024)
        {
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Contains((byte)0))
            {
                return false;
            }

            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static void CopyUnixFileMode(string sourcePath, string targetPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(targetPath, File.GetUnixFileMode(sourcePath));
        }
        catch
        {
            // File modes are best-effort; ownership is corrected by the normal session chown.
        }
    }

    private static void MaterializeSeedSitePackagesOverlay(string seedVirtualEnvPath, string targetVirtualEnvPath)
    {
        foreach (var seedSitePackages in EnumerateSitePackagesDirectories(seedVirtualEnvPath))
        {
            var relativePath = Path.GetRelativePath(seedVirtualEnvPath, seedSitePackages);
            var targetSitePackages = Path.Combine(targetVirtualEnvPath, relativePath);
            Directory.CreateDirectory(targetSitePackages);

            foreach (var sourceEntry in Directory.EnumerateFileSystemEntries(seedSitePackages))
            {
                var entryName = Path.GetFileName(sourceEntry);
                if (string.IsNullOrEmpty(entryName))
                {
                    continue;
                }

                var targetEntry = Path.Combine(targetSitePackages, entryName);
                if (IsPythonMetadataEntry(entryName))
                {
                    CopyMetadataEntry(sourceEntry, targetEntry);
                }
                else
                {
                    LinkPayloadEntry(sourceEntry, targetEntry);
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateSitePackagesDirectories(string virtualEnvPath)
    {
        foreach (var libDirectoryName in new[] { "lib", "lib64" })
        {
            var libDirectory = Path.Combine(virtualEnvPath, libDirectoryName);
            if (!Directory.Exists(libDirectory))
            {
                continue;
            }

            foreach (var pythonDirectory in Directory.EnumerateDirectories(libDirectory, "python*", SearchOption.TopDirectoryOnly))
            {
                var sitePackages = Path.Combine(pythonDirectory, "site-packages");
                if (Directory.Exists(sitePackages))
                {
                    yield return sitePackages;
                }
            }
        }
    }

    private static bool IsPythonMetadataEntry(string entryName)
        => entryName.EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase) ||
            entryName.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase);

    private static void CopyMetadataEntry(string sourcePath, string targetPath)
    {
        if (FileSystemEntryExists(targetPath))
        {
            return;
        }

        var attributes = File.GetAttributes(sourcePath);
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            CopyMetadataDirectory(sourcePath, targetPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(ResolveFilePath(sourcePath), targetPath, overwrite: false);
    }

    private static void CopyMetadataDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var sourceEntry in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            var targetEntry = Path.Combine(targetDirectory, Path.GetFileName(sourceEntry));
            var attributes = File.GetAttributes(sourceEntry);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                CopyMetadataDirectory(ResolveDirectoryPath(sourceEntry), targetEntry);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetEntry)!);
                File.Copy(ResolveFilePath(sourceEntry), targetEntry, overwrite: false);
            }
        }
    }

    private static string ResolveDirectoryPath(string path)
    {
        if (!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            return path;
        }

        return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
    }

    private static string ResolveFilePath(string path)
    {
        if (!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            return path;
        }

        return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
    }

    private static void LinkPayloadEntry(string sourcePath, string targetPath)
    {
        if (FileSystemEntryExists(targetPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var attributes = File.GetAttributes(sourcePath);
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            Directory.CreateSymbolicLink(targetPath, sourcePath);
        }
        else
        {
            File.CreateSymbolicLink(targetPath, sourcePath);
        }
    }

    private static bool FileSystemEntryExists(string path)
    {
        var parentDirectory = Path.GetDirectoryName(path);
        var entryName = Path.GetFileName(path);
        return !string.IsNullOrWhiteSpace(parentDirectory) &&
            Directory.Exists(parentDirectory) &&
            Directory.EnumerateFileSystemEntries(parentDirectory).Any(entry =>
                string.Equals(Path.GetFileName(entry), entryName, StringComparison.Ordinal));
    }

    private static bool IsSamePath(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.Ordinal);

    private static bool IsPathWithinRoot(string candidate, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return string.Equals(relative, ".", StringComparison.Ordinal) ||
            (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative));
    }

    private void PrepareBundledInputSeeds(SessionPaths paths)
        => SeedDirectoryIfEmpty(_bundledInputSeedPath, paths.InputDirectory);

    private void PrepareBundledCustomNodeSeeds(SessionPaths paths)
        => SeedDirectoryIfEmpty(_bundledCustomNodesSeedPath, paths.CustomNodesDirectory);

    private void PrepareManagerPathShims(SessionPaths paths)
    {
        if (string.Equals(_runtimeKind, "forge-neo", StringComparison.OrdinalIgnoreCase))
        {
            PrepareManagerPathShim("extensions", paths.CustomNodesDirectory);
            PrepareManagerPathShim("output", paths.OutputDirectory);
            PrepareManagerPathShim("cache", paths.CacheDirectory);
            return;
        }

        PrepareManagerPathShim("user", paths.UserDirectory);
        PrepareManagerPathShim("custom_nodes", paths.CustomNodesDirectory);
        PrepareManagerPathShim("web", paths.WebDirectory);
    }

    private void PrepareManagerPathShim(string relativePath, string targetPath)
    {
        var shimPath = Path.Combine(_workingDirectory, relativePath);
        if (IsDirectoryLinkToTarget(shimPath, targetPath))
        {
            return;
        }

        RemovePathIfPresent(shimPath);
        Directory.CreateDirectory(Path.GetDirectoryName(shimPath)!);
        Directory.CreateSymbolicLink(shimPath, targetPath);
    }

    internal static void EnsureManagerConfig(string userDirectory)
    {
        var managerDirectory = Path.Combine(userDirectory, "__manager");
        Directory.CreateDirectory(managerDirectory);

        var configPath = Path.Combine(managerDirectory, "config.ini");
        var lines = File.Exists(configPath)
            ? File.ReadAllLines(configPath).ToList()
            : [];

        UpsertIniValue(lines, "default", "use_uv", "True");
        UpsertIniValue(lines, "default", "network_mode", "offline");

        var content = lines.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, lines) + Environment.NewLine;
        File.WriteAllText(configPath, content);
    }

    private static void UpsertIniValue(List<string> lines, string sectionName, string key, string value)
    {
        var sectionHeader = $"[{sectionName}]";
        var sectionStart = lines.FindIndex(line => string.Equals(line.Trim(), sectionHeader, StringComparison.OrdinalIgnoreCase));
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(string.Empty);
            }

            lines.Add(sectionHeader);
            lines.Add($"{key} = {value}");
            return;
        }

        var sectionEnd = lines.FindIndex(sectionStart + 1, line => IsSectionHeader(line));
        if (sectionEnd < 0)
        {
            sectionEnd = lines.Count;
        }

        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            if (TryGetIniKey(lines[i], out var existingKey) &&
                string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key} = {value}";
                return;
            }
        }

        lines.Insert(sectionEnd, $"{key} = {value}");
    }

    private static bool IsSectionHeader(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 3 && trimmed[0] == '[' && trimmed[^1] == ']';
    }

    private static bool TryGetIniKey(string line, out string key)
    {
        key = string.Empty;
        var trimmed = line.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith(';') || IsSectionHeader(trimmed))
        {
            return false;
        }

        var separatorIndex = trimmed.IndexOf('=');
        if (separatorIndex < 0)
        {
            separatorIndex = trimmed.IndexOf(':');
        }

        if (separatorIndex <= 0)
        {
            return false;
        }

        key = trimmed[..separatorIndex].Trim();
        return key.Length > 0;
    }

    private string ResolveProjectedModelsRoot(SessionPaths paths)
        => string.Equals(_runtimeKind, "forge-neo", StringComparison.OrdinalIgnoreCase)
            ? paths.ModelsDirectory
            : "/comfyui/models";

    private static void EnsureProjectedModelsRoot(string projectedModelsRoot, string sessionRoot)
    {
        if (IsDirectoryLinkWithinRoot(projectedModelsRoot, sessionRoot))
        {
            RemovePathIfPresent(projectedModelsRoot);
            Directory.CreateDirectory(projectedModelsRoot);
        }
    }

    private static void EnsureSessionModelsRoot(string modelsDirectory, string projectedModelsRoot)
    {
        if (IsSamePath(modelsDirectory, projectedModelsRoot))
        {
            Directory.CreateDirectory(modelsDirectory);
            return;
        }

        if (IsDirectoryLinkToTarget(modelsDirectory, projectedModelsRoot))
        {
            return;
        }

        RemovePathIfPresent(modelsDirectory);
        Directory.CreateSymbolicLink(modelsDirectory, projectedModelsRoot);
    }

    private static bool IsDirectoryLinkWithinRoot(string path, string root)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (info.LinkTarget is null)
            {
                return false;
            }

            var parentPath = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return false;
            }

            var targetPath = Path.IsPathFullyQualified(info.LinkTarget)
                ? info.LinkTarget
                : Path.GetFullPath(Path.Combine(parentPath, info.LinkTarget));

            return IsPathWithinRoot(targetPath, root);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDirectoryLinkToTarget(string path, string targetPath)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (info.LinkTarget is null)
            {
                return false;
            }

            var resolved = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            return resolved is not null &&
                string.Equals(
                    Path.GetFullPath(resolved.FullName),
                    Path.GetFullPath(targetPath),
                    StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void RemovePathIfPresent(string path)
    {
        try
        {
            var parentDirectory = Path.GetDirectoryName(path);
            var entryName = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(parentDirectory) &&
                Directory.Exists(parentDirectory) &&
                Directory.EnumerateFileSystemEntries(parentDirectory, entryName, SearchOption.TopDirectoryOnly).Any())
            {
                try
                {
                    Directory.Delete(path);
                }
                catch
                {
                    File.Delete(path);
                }

                return;
            }
        }
        catch
        {
            // Fall back to normal existence checks below.
        }

        if (Directory.Exists(path))
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(path);
            }
            else
            {
                Directory.Delete(path, recursive: true);
            }

            return;
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void SeedDirectoryIfEmpty(string sourceDirectory, string targetDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
            return;
        }

        Directory.CreateDirectory(targetDirectory);
        if (Directory.EnumerateFileSystemEntries(targetDirectory).Any())
        {
            return;
        }

        CopyDirectoryContents(sourceDirectory, targetDirectory);
    }

    private static void CopyDirectoryContents(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var destination = Path.Combine(targetDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    public readonly record struct SessionPaths
    {
        public string HomeDirectory { get; init; }
        public string UserDirectory { get; init; }
        public string CustomNodesDirectory { get; init; }
        public string ModelsDirectory { get; init; }
        public string VirtualEnvDirectory { get; init; }
        public string OutputDirectory { get; init; }
        public string InputDirectory { get; init; }
        public string TempDirectory { get; init; }
        public string ScratchDirectory { get; init; }
        public string CacheDirectory { get; init; }
        public string WebDirectory { get; init; }
        public string UvCacheDirectory { get; init; }
        public string TorchInductorCacheDirectory { get; init; }
        public string LogsDirectory { get; init; }
    }

    internal sealed record ForgeQueueDataArityAdjustment(int FnIndex, int ReceivedCount, int ExpectedCount);

    private sealed class SessionContext
    {
        private readonly object _stopGate = new();
        private TaskCompletionSource? _stopCompletion;
        private int _stopState;
        private int _cleanupState;

        public SessionContext(string sessionId, RuntimeIdentity runtimeIdentity, PreparedSessionIdentity identity, SessionPaths paths, Process process, Task<int> exitTask)
        {
            SessionId = sessionId;
            RuntimeIdentity = runtimeIdentity;
            Identity = identity;
            Paths = paths;
            Process = process;
            ExitTask = exitTask;
        }

        public string SessionId { get; }
        public RuntimeIdentity RuntimeIdentity { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public PreparedSessionIdentity Identity { get; }
        public string UserName => Identity.UserName;
        public SessionPaths Paths { get; }
        public string HomeDirectory => Paths.HomeDirectory;
        public Process Process { get; }
        public Task<int> ExitTask { get; }
        public bool StopInProgress => Volatile.Read(ref _stopState) != 0;

        public bool TryBeginCleanup() => Interlocked.CompareExchange(ref _cleanupState, 1, 0) == 0;

        /// <summary>
        /// Claims termination for one caller. Later callers receive the in-flight
        /// stop so they can await the same process exit instead of returning early.
        /// </summary>
        public bool TryBeginStop(out TaskCompletionSource stopCompletion, out Task inFlightStop)
        {
            lock (_stopGate)
            {
                if (_stopCompletion is { } existing)
                {
                    stopCompletion = existing;
                    inFlightStop = existing.Task;
                    return false;
                }

                _stopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _stopState, 1);
                stopCompletion = _stopCompletion;
                inFlightStop = _stopCompletion.Task;
                return true;
            }
        }

        /// <summary>Allows a later stop to retry after this one failed.</summary>
        public void ResetStopState()
        {
            lock (_stopGate)
            {
                _stopCompletion = null;
                Volatile.Write(ref _stopState, 0);
            }
        }
    }

    internal readonly record struct ProcessRunResult(int ExitCode, string Output);

    private sealed class SeedPayloadDetachTransaction : IDisposable
    {
        private readonly string _backupRoot;
        private readonly List<BackupEntry> _entries = [];
        private bool _completed;

        public SeedPayloadDetachTransaction(string virtualEnvDirectory)
        {
            _backupRoot = Path.Combine(virtualEnvDirectory, ".offloadr-seed-detach-backup", Guid.NewGuid().ToString("N"));
        }

        public void RemoveEntry(string path)
        {
            if (!FileSystemEntryExists(path))
            {
                return;
            }

            if (_entries.Any(entry => IsSamePath(entry.OriginalPath, path)))
            {
                RemovePathIfPresent(path);
                return;
            }

            var backupPath = Path.Combine(_backupRoot, _entries.Count.ToString(CultureInfo.InvariantCulture));
            var backupContainer = Path.GetDirectoryName(_backupRoot)!;
            if (new DirectoryInfo(backupContainer).LinkTarget is not null)
            {
                // The session user owns the venv; never let root follow a planted link out of it.
                throw new InvalidOperationException($"Seed detach backup directory '{backupContainer}' is a symbolic link.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            MoveEntry(path, backupPath);
            _entries.Add(new BackupEntry(path, backupPath));
        }

        public void Commit()
        {
            if (_completed)
            {
                return;
            }

            RemovePathIfPresent(_backupRoot);
            _completed = true;
        }

        public void Rollback()
        {
            if (_completed)
            {
                return;
            }

            for (var index = _entries.Count - 1; index >= 0; index--)
            {
                var entry = _entries[index];
                if (!FileSystemEntryExists(entry.BackupPath))
                {
                    continue;
                }

                RemovePathIfPresent(entry.OriginalPath);
                Directory.CreateDirectory(Path.GetDirectoryName(entry.OriginalPath)!);
                MoveEntry(entry.BackupPath, entry.OriginalPath);
            }

            RemovePathIfPresent(_backupRoot);
            _completed = true;
        }

        public void Dispose()
        {
            if (!_completed)
            {
                Rollback();
            }
        }

        private static void MoveEntry(string sourcePath, string targetPath)
        {
            var attributes = File.GetAttributes(sourcePath);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                Directory.Move(sourcePath, targetPath);
            }
            else
            {
                File.Move(sourcePath, targetPath);
            }
        }

        private readonly record struct BackupEntry(string OriginalPath, string BackupPath);
    }

    public readonly record struct PromptSubmissionResult(
        bool Success,
        int StatusCode,
        byte[] Body,
        string Message,
        string ContentType = "application/json",
        bool BodyOmitted = false);

    public readonly record struct EditorRuntimeRelayResult(int StatusCode, byte[] Body, string ContentType);
}
