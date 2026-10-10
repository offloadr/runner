
namespace Offloadr.Runner.Linux;

internal sealed class ModelDownloadIpcServer : IAsyncDisposable
{
    private readonly ModelDownloadService _downloadService;
    private readonly ArtifactUploadService _artifactUploadService;
    private readonly WorkspaceMirrorService _workspaceMirrorService;
    private readonly Func<string, uint?> _resolveSessionUserId;
    private readonly VfsIpcServer _server;

    /// <param name="resolveSessionUserId">
    /// Returns the Linux uid that runs the given session, or null when it has none. Only that
    /// uid (and the agent itself) may use the socket while the session is active.
    /// </param>
    public ModelDownloadIpcServer(
        string socketPath,
        ModelDownloadService downloadService,
        ArtifactUploadService artifactUploadService,
        WorkspaceMirrorService workspaceMirrorService,
        Func<string, uint?>? resolveSessionUserId = null)
    {
        if (string.IsNullOrWhiteSpace(socketPath)) throw new ArgumentException("Socket path is required.", nameof(socketPath));

        _downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
        _artifactUploadService = artifactUploadService ?? throw new ArgumentNullException(nameof(artifactUploadService));
        _workspaceMirrorService = workspaceMirrorService ?? throw new ArgumentNullException(nameof(workspaceMirrorService));
        _resolveSessionUserId = resolveSessionUserId ?? (static _ => null);

        var logEnabled = string.Equals(Environment.GetEnvironmentVariable("RUNNER_VFS_LOG"), "1", StringComparison.OrdinalIgnoreCase);
        _server = new VfsIpcServer(
            socketPath,
            HandleRequestAsync,
            logEnabled,
            authorizePeer: peer => TryResolvePeerSession(peer, out _));
    }

    public void Start()
    {
        _server.Start();
    }

    public ValueTask DisposeAsync()
    {
        return _server.DisposeAsync();
    }

    private static bool IsAgentPeer(VfsIpcPeerCredentials peer)
        => peer.IsRoot || peer.UserId == VfsIpcPeerCredentials.CurrentEffectiveUserId;

    /// <summary>
    /// Maps a connection to the session it may act for. The agent itself is trusted and not
    /// bound to a session (<paramref name="sessionId"/> is null); any other uid must be the
    /// Linux user of the currently active session.
    /// </summary>
    private bool TryResolvePeerSession(VfsIpcPeerCredentials peer, out string? sessionId)
    {
        sessionId = null;
        if (IsAgentPeer(peer))
        {
            return true;
        }

        var activeSessionId = _downloadService.GetActiveSessionId();
        if (string.IsNullOrWhiteSpace(activeSessionId) ||
            _resolveSessionUserId(activeSessionId) is not { } sessionUserId ||
            sessionUserId == VfsIpcPeerCredentials.RootUserId ||
            sessionUserId != peer.UserId)
        {
            return false;
        }

        sessionId = activeSessionId;
        return true;
    }

    private Task<VfsIpcResponse> HandleRequestAsync(
        VfsIpcRequest request,
        VfsIpcPeerCredentials peer,
        CancellationToken cancellationToken)
    {
        // Re-check at dispatch: the active session may have changed since the connection was accepted.
        if (!TryResolvePeerSession(peer, out var peerSessionId))
        {
            return Task.FromResult(VfsIpcResponse.Error(VfsIpcStatus.IoError, request.TransferEpoch));
        }

        return request.Operation switch
        {
            VfsIpcOperation.Open => OpenAsync(request, peerSessionId, cancellationToken),
            VfsIpcOperation.EnsureRange => EnsureRangeAsync(request, peerSessionId, cancellationToken),
            VfsIpcOperation.EnsureComplete => EnsureCompleteAsync(request, peerSessionId, cancellationToken),
            VfsIpcOperation.Release => ReleaseAsync(request, peerSessionId),
            _ => Task.FromResult(VfsIpcResponse.Error(VfsIpcStatus.InvalidHeader))
        };
    }

    private async Task<VfsIpcResponse> OpenAsync(
        VfsIpcRequest request,
        string? peerSessionId,
        CancellationToken cancellationToken)
    {
        var activeSessionId = _downloadService.GetActiveSessionId();
        if (string.IsNullOrWhiteSpace(activeSessionId) ||
            !string.Equals(request.SessionId, activeSessionId, StringComparison.Ordinal) ||
            peerSessionId is not null && !string.Equals(peerSessionId, activeSessionId, StringComparison.Ordinal))
        {
            return VfsIpcResponse.Error(VfsIpcStatus.IoError);
        }

        if (request.WriteAccess)
        {
            var writableResult = await _downloadService
                .OpenRangeManagedAsync(request.Path, activeSessionId, cancellationToken)
                .ConfigureAwait(false);
            if (writableResult.Disposition == ModelHydrationOpenDisposition.Unmanaged)
            {
                return VfsIpcResponse.Error(VfsIpcStatus.NotManaged);
            }

            if (writableResult.LeaseId != 0)
            {
                await _downloadService.ReleaseAsync(writableResult.LeaseId).ConfigureAwait(false);
            }

            return VfsIpcResponse.Error(VfsIpcStatus.ReadOnly, writableResult.TransferEpoch);
        }

        var artifactPath = _artifactUploadService.OwnsArtifactPath(request.Path);
        var handled = false;
        try
        {
            handled = await _artifactUploadService
                .TryEnsureArtifactAvailableAsync(request.Path, highPriority: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        if (!handled && !artifactPath)
        {
            handled = await _workspaceMirrorService
                .TryEnsureWorkspaceFileAvailableAsync(request.Path, highPriority: true, cancellationToken)
                .ConfigureAwait(false);
        }

        if (handled)
        {
            return new VfsIpcResponse(
                VfsIpcStatus.Success,
                VfsIpcOpenDisposition.FullReady,
                0,
                0,
                0);
        }

        if (artifactPath)
        {
            return VfsIpcResponse.Error(VfsIpcStatus.EnsureFailed);
        }

        var result = await _downloadService
            .OpenRangeManagedAsync(request.Path, activeSessionId, cancellationToken)
            .ConfigureAwait(false);
        if (result.Disposition == ModelHydrationOpenDisposition.Unmanaged)
        {
            var modelHandled = await _downloadService
                .TryEnsureRegisteredDownloadAsync(request.Path, cancellationToken, highPriority: true)
                .ConfigureAwait(false);
            return modelHandled
                ? new VfsIpcResponse(
                    VfsIpcStatus.Success,
                    VfsIpcOpenDisposition.FullReady,
                    0,
                    0,
                    0)
                : VfsIpcResponse.Error(VfsIpcStatus.NotManaged);
        }

        return result.Disposition switch
        {
            ModelHydrationOpenDisposition.FullReady => new VfsIpcResponse(
                VfsIpcStatus.Success,
                VfsIpcOpenDisposition.FullReady,
                0,
                result.TransferEpoch,
                result.ExpectedLength),
            ModelHydrationOpenDisposition.RangeManaged => new VfsIpcResponse(
                VfsIpcStatus.Success,
                VfsIpcOpenDisposition.RangeManaged,
                result.LeaseId,
                result.TransferEpoch,
                result.ExpectedLength,
                result.DeviceId,
                result.Inode),
            _ => VfsIpcResponse.Error(VfsIpcStatus.ServerError)
        };
    }

    private async Task<VfsIpcResponse> EnsureRangeAsync(
        VfsIpcRequest request,
        string? peerSessionId,
        CancellationToken cancellationToken)
    {
        var result = await _downloadService
            .EnsureRangeAsync(
                request.LeaseId,
                request.TransferEpoch,
                request.Offset,
                request.Length,
                cancellationToken,
                peerSessionId)
            .ConfigureAwait(false);
        return VfsIpcResponse.Success(result.TransferEpoch);
    }

    private async Task<VfsIpcResponse> EnsureCompleteAsync(
        VfsIpcRequest request,
        string? peerSessionId,
        CancellationToken cancellationToken)
    {
        var result = await _downloadService
            .EnsureCompleteAsync(
                request.LeaseId,
                request.TransferEpoch,
                request.Reason,
                cancellationToken,
                peerSessionId)
            .ConfigureAwait(false);
        return VfsIpcResponse.Success(result.TransferEpoch);
    }

    private async Task<VfsIpcResponse> ReleaseAsync(VfsIpcRequest request, string? peerSessionId)
    {
        // A release for a lease the peer's session does not own is a no-op.
        await _downloadService.ReleaseAsync(request.LeaseId, peerSessionId).ConfigureAwait(false);
        return VfsIpcResponse.Success();
    }
}
