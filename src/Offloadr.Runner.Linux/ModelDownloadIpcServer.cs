
namespace Offloadr.Runner.Linux;

internal sealed class ModelDownloadIpcServer : IAsyncDisposable
{
    private readonly ModelDownloadService _downloadService;
    private readonly ArtifactUploadService _artifactUploadService;
    private readonly WorkspaceMirrorService _workspaceMirrorService;
    private readonly VfsIpcServer _server;

    public ModelDownloadIpcServer(string socketPath, ModelDownloadService downloadService, ArtifactUploadService artifactUploadService, WorkspaceMirrorService workspaceMirrorService)
    {
        if (string.IsNullOrWhiteSpace(socketPath)) throw new ArgumentException("Socket path is required.", nameof(socketPath));

        _downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
        _artifactUploadService = artifactUploadService ?? throw new ArgumentNullException(nameof(artifactUploadService));
        _workspaceMirrorService = workspaceMirrorService ?? throw new ArgumentNullException(nameof(workspaceMirrorService));

        var logEnabled = string.Equals(Environment.GetEnvironmentVariable("RUNNER_VFS_LOG"), "1", StringComparison.OrdinalIgnoreCase);
        _server = new VfsIpcServer(socketPath, HandleRequestAsync, logEnabled);
    }

    public void Start()
    {
        _server.Start();
    }

    public ValueTask DisposeAsync()
    {
        return _server.DisposeAsync();
    }

    private Task<VfsIpcResponse> HandleRequestAsync(
        VfsIpcRequest request,
        CancellationToken cancellationToken)
        => request.Operation switch
        {
            VfsIpcOperation.Open => OpenAsync(request, cancellationToken),
            VfsIpcOperation.EnsureRange => EnsureRangeAsync(request, cancellationToken),
            VfsIpcOperation.EnsureComplete => EnsureCompleteAsync(request, cancellationToken),
            VfsIpcOperation.Release => ReleaseAsync(request),
            _ => Task.FromResult(VfsIpcResponse.Error(VfsIpcStatus.InvalidHeader))
        };

    private async Task<VfsIpcResponse> OpenAsync(
        VfsIpcRequest request,
        CancellationToken cancellationToken)
    {
        var activeSessionId = _downloadService.GetActiveSessionId();
        if (string.IsNullOrWhiteSpace(activeSessionId) ||
            !string.Equals(request.SessionId, activeSessionId, StringComparison.Ordinal))
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
        CancellationToken cancellationToken)
    {
        var result = await _downloadService
            .EnsureRangeAsync(
                request.LeaseId,
                request.TransferEpoch,
                request.Offset,
                request.Length,
                cancellationToken)
            .ConfigureAwait(false);
        return VfsIpcResponse.Success(result.TransferEpoch);
    }

    private async Task<VfsIpcResponse> EnsureCompleteAsync(
        VfsIpcRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _downloadService
            .EnsureCompleteAsync(
                request.LeaseId,
                request.TransferEpoch,
                request.Reason,
                cancellationToken)
            .ConfigureAwait(false);
        return VfsIpcResponse.Success(result.TransferEpoch);
    }

    private async Task<VfsIpcResponse> ReleaseAsync(VfsIpcRequest request)
    {
        await _downloadService.ReleaseAsync(request.LeaseId).ConfigureAwait(false);
        return VfsIpcResponse.Success();
    }
}
