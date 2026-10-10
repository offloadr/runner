
namespace Offloadr.Runner.Tests;

internal abstract class TestModelTransferBackend : IModelTransferBackend
{
    public virtual string Name => "test";

    public virtual Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual Task<IReadOnlyList<ModelTransferHandle>> CreateAsync(
        ModelTransferCreateRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelTransferHandle> handles =
        [
            new(request.PreferredIdentifier ?? "gid-1")
        ];
        return Task.FromResult(handles);
    }

    public virtual Task<ModelTransferSnapshot> GetStatusAsync(
        ModelTransferHandle handle,
        CancellationToken cancellationToken)
        => Task.FromResult(Snapshot(handle, "complete", 1, 1));

    public virtual Task<IReadOnlyList<ModelTransferSnapshot>> ListAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ModelTransferSnapshot>>([]);

    public virtual Task ForcePauseAsync(ModelTransferHandle handle, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public virtual Task UnpauseAsync(ModelTransferHandle handle, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public virtual Task ChangeUriAsync(
        ModelTransferHandle handle,
        IReadOnlyList<string> currentUris,
        IReadOnlyList<string> replacementUris,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public virtual Task MoveToFrontAsync(ModelTransferHandle handle, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public virtual Task RemoveAsync(ModelTransferHandle handle, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public virtual Task SaveSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static ModelTransferSnapshot Snapshot(
        ModelTransferHandle handle,
        string? status,
        long completedLength,
        long totalLength,
        long downloadSpeed = 0,
        string? errorCode = null,
        string? errorMessage = null,
        string? bitfield = null,
        long pieceLength = 0,
        long numPieces = 0,
        IReadOnlyList<ModelTransferFileSnapshot>? files = null)
        => new(
            handle,
            status,
            completedLength,
            totalLength,
            downloadSpeed,
            bitfield,
            pieceLength,
            numPieces,
            errorCode,
            errorMessage,
            files ?? []);
}
