using Aria2NET.Exceptions;
using System.Buffers.Binary;
using System.Globalization;

namespace Offloadr.Runner.Core;

internal sealed class Aria2DownloadBackend : IModelTransferBackend
{
    private const long Aria2RpcOperationalErrorCode = 1;
    private const string InvalidGidPrefix = "Invalid GID ";
    private const string MissingGidPrefix = "GID ";
    private const string MissingGidSuffix = " is not found";
    private const int ManagedPieceLengthBytes = 1024 * 1024;
    private const string ManagedPieceLength = "1M";
    private readonly Aria2ProcessManager _manager;

    public Aria2DownloadBackend(
        Aria2Settings settings,
        bool useProcessWatchdog = true)
    {
        _manager = new Aria2ProcessManager(settings, useProcessWatchdog);
    }

    public string Name => "aria2";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _manager.StartAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ModelTransferHandle>> CreateAsync(
        ModelTransferCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var destination = Path.GetFullPath(request.DestinationPath);
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException($"Download destination '{destination}' does not contain a directory.");
        }

        var options = BuildOptions(request, destination, directory);
        PrepareResumeState(request, destination);

        var requiresDeterministicHandle = !string.IsNullOrWhiteSpace(request.PreferredIdentifier);
        if (ShouldUseMetalink(request))
        {
            var gids = await _manager
                .AddMetalinkAsync(request.MetalinkContent!, options, request.QueuePosition, cancellationToken)
                .ConfigureAwait(false);
            return gids.Select(static gid => new ModelTransferHandle(gid)).ToArray();
        }

        var uris = request.SourceUris
            .Where(static uri => !string.IsNullOrWhiteSpace(uri))
            .Select(static uri => uri.Trim())
            .ToArray();
        if (uris.Length == 0)
        {
            if (request.MetalinkContent is { Length: > 0 } && requiresDeterministicHandle)
            {
                throw new InvalidOperationException(
                    $"Deterministic transfer for '{destination}' requires at least one source URI because aria2.addMetalink assigns its own GIDs.");
            }

            throw new InvalidOperationException($"Download destination '{destination}' has no source URI or Metalink.");
        }

        var gid = await _manager
            .AddUriAsync(uris, options, request.QueuePosition, cancellationToken)
            .ConfigureAwait(false);
        return [new ModelTransferHandle(gid)];
    }

    internal static bool ShouldUseMetalink(ModelTransferCreateRequest request)
        => request.MetalinkContent is { Length: > 0 } &&
           string.IsNullOrWhiteSpace(request.PreferredIdentifier);

    public async Task<ModelTransferSnapshot> GetStatusAsync(
        ModelTransferHandle handle,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await _manager.GetStatusAsync(handle.Value, cancellationToken).ConfigureAwait(false);
            return MapStatus(status);
        }
        catch (Aria2Exception ex) when (IsUnavailableGidError(ex.ResultCode, ex.ResultMessage))
        {
            throw new ModelTransferStatusUnavailableException(ex.ResultMessage ?? ex.Message, ex);
        }
    }

    public async Task<IReadOnlyList<ModelTransferSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        var statuses = await _manager.ListAsync(cancellationToken).ConfigureAwait(false);
        return statuses.Select(MapStatus).ToArray();
    }

    public async Task ForcePauseAsync(ModelTransferHandle handle, CancellationToken cancellationToken)
    {
        try
        {
            await _manager.ForcePauseAsync(handle.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Aria2Exception ex) when (IsUnavailableGidError(ex.ResultCode, ex.ResultMessage))
        {
            throw new ModelTransferStatusUnavailableException(ex.ResultMessage ?? ex.Message, ex);
        }
    }

    public async Task UnpauseAsync(ModelTransferHandle handle, CancellationToken cancellationToken)
    {
        await _manager.UnpauseAsync(handle.Value, cancellationToken).ConfigureAwait(false);
    }

    public Task ChangeUriAsync(
        ModelTransferHandle handle,
        IReadOnlyList<string> currentUris,
        IReadOnlyList<string> replacementUris,
        CancellationToken cancellationToken)
        => _manager.ChangeUriAsync(handle.Value, currentUris, replacementUris, cancellationToken);

    public async Task MoveToFrontAsync(ModelTransferHandle handle, CancellationToken cancellationToken)
    {
        try
        {
            await _manager.MoveToFrontAsync(handle.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Aria2RawRpcException ex) when (IsUnavailableGidError(ex.ResultCode, ex.ResultMessage))
        {
            throw new ModelTransferStatusUnavailableException(ex.ResultMessage ?? ex.Message, ex);
        }
    }

    public async Task RemoveAsync(ModelTransferHandle handle, CancellationToken cancellationToken)
    {
        var forceRemovalCompleted = false;
        try
        {
            var status = await _manager
                .GetStatusAsync(handle.Value, cancellationToken)
                .ConfigureAwait(false);
            if (IsStoppedStatus(status.Status))
            {
                await _manager
                    .RemoveDownloadResultAsync(handle.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _manager.ForceRemoveAsync(handle.Value, cancellationToken).ConfigureAwait(false);
                forceRemovalCompleted = true;
                await _manager
                    .RemoveDownloadResultAsync(handle.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Aria2Exception ex) when (
            forceRemovalCompleted &&
            IsUnavailableGidError(ex.ResultCode, ex.ResultMessage))
        {
            // The active transfer was removed and aria2 no longer retains a result to purge.
        }
        catch (Aria2Exception ex) when (IsUnavailableGidError(ex.ResultCode, ex.ResultMessage))
        {
            throw new ModelTransferStatusUnavailableException(ex.ResultMessage ?? ex.Message, ex);
        }
    }

    internal static bool IsStoppedStatus(string? status)
        => string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, "error", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, "removed", StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnavailableGidError(long resultCode, string? resultMessage)
    {
        if (resultCode != Aria2RpcOperationalErrorCode ||
            string.IsNullOrWhiteSpace(resultMessage))
        {
            return false;
        }

        var message = resultMessage.Trim();
        if (message.StartsWith(InvalidGidPrefix, StringComparison.Ordinal))
        {
            return message.Length > InvalidGidPrefix.Length;
        }

        if (!message.StartsWith(MissingGidPrefix, StringComparison.Ordinal) ||
            !message.EndsWith(MissingGidSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var gidLength = message.Length - MissingGidPrefix.Length - MissingGidSuffix.Length;
        if (gidLength is < 1 or > 16)
        {
            return false;
        }

        foreach (var value in message.AsSpan(MissingGidPrefix.Length, gidLength))
        {
            if (value is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f') and
                not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }

    public Task SaveSessionAsync(CancellationToken cancellationToken)
        => _manager.SaveSessionAsync(cancellationToken);

    public ValueTask DisposeAsync() => _manager.DisposeAsync();

    internal IReadOnlyDictionary<string, object> BuildOptions(
        ModelTransferCreateRequest request)
    {
        var destination = Path.GetFullPath(request.DestinationPath);
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException($"Download destination '{destination}' does not contain a directory.");
        }

        return BuildOptions(request, destination, directory);
    }

    private Dictionary<string, object> BuildOptions(
        ModelTransferCreateRequest request,
        string destination,
        string directory)
    {
        var options = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["dir"] = directory,
            ["out"] = Path.GetFileName(destination),
            ["allow-overwrite"] = "true",
            ["auto-file-renaming"] = "false",
            ["always-resume"] = "true",
            ["continue"] = request.ResumeMode == ModelTransferResumeMode.None ? "true" : "false",
            ["disk-cache"] = "0",
            ["stream-piece-selector"] = "inorder",
            ["piece-length"] = ManagedPieceLength,
            ["max-connection-per-server"] = _manager.Options.MaxConnectionPerServer.ToString(CultureInfo.InvariantCulture),
            ["split"] = _manager.Options.Split.ToString(CultureInfo.InvariantCulture),
            ["min-split-size"] = _manager.Options.MinSplitSize,
            ["file-allocation"] = _manager.Options.FileAllocation,
        };

        if (request.StartPaused)
        {
            options["pause"] = "true";
        }

        if (!string.IsNullOrWhiteSpace(request.PreferredIdentifier))
        {
            var identifier = request.PreferredIdentifier.Trim();
            if (identifier.Length != 16 ||
                identifier.Any(static value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            {
                throw new InvalidOperationException(
                    "Preferred transfer identifier must be 16 lowercase hexadecimal characters.");
            }

            options["gid"] = identifier;
        }

        return options;
    }

    internal static void PrepareResumeState(ModelTransferCreateRequest request)
        => PrepareResumeState(request, Path.GetFullPath(request.DestinationPath));

    private static void PrepareResumeState(
        ModelTransferCreateRequest request,
        string destination)
    {
        if (request.ResumeMode == ModelTransferResumeMode.None)
        {
            return;
        }

        if (request.ExpectedLength <= 0 ||
            !File.Exists(destination) ||
            new FileInfo(destination).Length != request.ExpectedLength)
        {
            throw new InvalidOperationException(
                $"Preallocated transfer target '{destination}' must exist at length {request.ExpectedLength}.");
        }

        var controlPath = $"{destination}.aria2";
        if (request.ResumeMode == ModelTransferResumeMode.RequireExisting)
        {
            if (!File.Exists(controlPath))
            {
                throw new InvalidOperationException(
                    $"Verified resume state for preallocated transfer target '{destination}' is missing.");
            }

            return;
        }

        var emptyControl = BuildEmptyControlFile(request.ExpectedLength);
        if (File.Exists(controlPath))
        {
            if (File.ReadAllBytes(controlPath).AsSpan().SequenceEqual(emptyControl))
            {
                return;
            }

            throw new InvalidOperationException(
                $"Preallocated transfer target '{destination}' has unverified existing resume state.");
        }

        var temporaryPath = $"{controlPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(emptyControl);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, controlPath);
        }
        catch (IOException) when (
            File.Exists(controlPath) &&
            File.ReadAllBytes(controlPath).AsSpan().SequenceEqual(emptyControl))
        {
            // An identical initializer won the atomic create race.
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static byte[] BuildEmptyControlFile(long expectedLength)
    {
        const int fixedLength = 38;
        var numPieces = 1 + ((expectedLength - 1) / ManagedPieceLengthBytes);
        var bitfieldLength = checked((int)(1 + ((numPieces - 1) / 8)));
        var contents = new byte[checked(fixedLength + bitfieldLength)];
        var span = contents.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span, 1);
        BinaryPrimitives.WriteUInt32BigEndian(span[2..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(span[6..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(span[10..], ManagedPieceLengthBytes);
        BinaryPrimitives.WriteInt64BigEndian(span[14..], expectedLength);
        BinaryPrimitives.WriteInt64BigEndian(span[22..], 0);
        BinaryPrimitives.WriteInt32BigEndian(span[30..], bitfieldLength);
        BinaryPrimitives.WriteUInt32BigEndian(span[(34 + bitfieldLength)..], 0);
        return contents;
    }

    private static ModelTransferSnapshot MapStatus(Aria2NET.DownloadStatusResult status)
        => new(
            new ModelTransferHandle(status.Gid),
            status.Status,
            status.CompletedLength,
            status.TotalLength,
            status.DownloadSpeed,
            status.Bitfield,
            status.PieceLength,
            status.NumPieces ?? 0,
            status.ErrorCode,
            status.ErrorMessage,
            status.Files
                .Select(static file => new ModelTransferFileSnapshot(
                    file.Path,
                    file.Length,
                    file.CompletedLength,
                    file.Uris
                        .Select(static uri => uri.Uri)
                        .Where(static uri => !string.IsNullOrWhiteSpace(uri))
                        .ToArray()))
                .ToArray());
}
