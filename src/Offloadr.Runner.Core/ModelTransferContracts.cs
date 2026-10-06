namespace Offloadr.Runner.Core;

public readonly record struct ModelTransferHandle
{
    public ModelTransferHandle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Transfer handle is required.", nameof(value));
        }

        Value = value.Trim();
    }

    internal string Value { get; }

    public override string ToString() => Value;
}

public enum ModelTransferResumeMode
{
    None,
    InitializeEmpty,
    RequireExisting
}

public sealed record ModelTransferCreateRequest(
    string DestinationPath,
    long ExpectedLength,
    IReadOnlyList<string> SourceUris,
    byte[]? MetalinkContent,
    string? PreferredIdentifier,
    bool StartPaused,
    int? QueuePosition,
    ModelTransferResumeMode ResumeMode = ModelTransferResumeMode.None);

public sealed record ModelTransferFileSnapshot(
    string Path,
    long Length,
    long CompletedLength,
    IReadOnlyList<string> Uris);

public sealed record ModelTransferSnapshot(
    ModelTransferHandle Handle,
    string? Status,
    long CompletedLength,
    long TotalLength,
    long DownloadSpeed,
    string? Bitfield,
    long PieceLength,
    long NumPieces,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<ModelTransferFileSnapshot> Files)
{
    public bool IsComplete => string.Equals(Status, "complete", StringComparison.OrdinalIgnoreCase);

    public bool IsStopped => IsComplete ||
        string.Equals(Status, "error", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Status, "removed", StringComparison.OrdinalIgnoreCase);
}

public interface IModelTransferBackend : IAsyncDisposable
{
    string Name { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ModelTransferHandle>> CreateAsync(
        ModelTransferCreateRequest request,
        CancellationToken cancellationToken);

    Task<ModelTransferSnapshot> GetStatusAsync(
        ModelTransferHandle handle,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ModelTransferSnapshot>> ListAsync(CancellationToken cancellationToken);

    Task ForcePauseAsync(ModelTransferHandle handle, CancellationToken cancellationToken);

    Task UnpauseAsync(ModelTransferHandle handle, CancellationToken cancellationToken);

    Task ChangeUriAsync(
        ModelTransferHandle handle,
        IReadOnlyList<string> currentUris,
        IReadOnlyList<string> replacementUris,
        CancellationToken cancellationToken);

    Task MoveToFrontAsync(ModelTransferHandle handle, CancellationToken cancellationToken);

    Task RemoveAsync(ModelTransferHandle handle, CancellationToken cancellationToken);

    Task SaveSessionAsync(CancellationToken cancellationToken);
}

public sealed class ModelTransferStatusUnavailableException : Exception
{
    public ModelTransferStatusUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
