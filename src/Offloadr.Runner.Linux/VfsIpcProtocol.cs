using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Offloadr.Runner.Linux;

public enum VfsIpcOperation : byte
{
    Open = 1,
    EnsureRange = 2,
    EnsureComplete = 3,
    Release = 4,
    AcknowledgeOpen = 5
}

public enum VfsIpcStatus : byte
{
    Success = 0,
    InvalidHeader = 1,
    InvalidLength = 2,
    ShortBody = 3,
    EnsureFailed = 4,
    Cancelled = 5,
    ServerError = 6,
    NotManaged = 7,
    IoError = 8,
    ReadOnly = 9
}

public enum VfsIpcOpenDisposition : byte
{
    Unmanaged = 0,
    FullReady = 1,
    RangeManaged = 2
}

public readonly record struct VfsIpcRequest(
    VfsIpcOperation Operation,
    string Path,
    string SessionId,
    ulong LeaseId,
    long TransferEpoch,
    long Offset,
    long Length,
    string Reason,
    bool WriteAccess)
{
    public static VfsIpcRequest Open(
        string path,
        string? sessionId = null,
        bool writeAccess = false)
        => new(
            VfsIpcOperation.Open,
            path,
            sessionId ?? string.Empty,
            0,
            0,
            0,
            0,
            string.Empty,
            writeAccess);

    public static VfsIpcRequest EnsureRange(
        ulong leaseId,
        long transferEpoch,
        long offset,
        long length)
        => new(VfsIpcOperation.EnsureRange, string.Empty, string.Empty, leaseId, transferEpoch, offset, length, string.Empty, false);

    public static VfsIpcRequest EnsureComplete(
        ulong leaseId,
        long transferEpoch,
        string? reason)
        => new(VfsIpcOperation.EnsureComplete, string.Empty, string.Empty, leaseId, transferEpoch, 0, 0, reason ?? string.Empty, false);

    public static VfsIpcRequest Release(ulong leaseId)
        => new(VfsIpcOperation.Release, string.Empty, string.Empty, leaseId, 0, 0, 0, string.Empty, false);

    public static VfsIpcRequest AcknowledgeOpen(ulong leaseId)
        => new(VfsIpcOperation.AcknowledgeOpen, string.Empty, string.Empty, leaseId, 0, 0, 0, string.Empty, false);
}

public readonly record struct VfsIpcResponse(
    VfsIpcStatus Status,
    VfsIpcOpenDisposition OpenDisposition,
    ulong LeaseId,
    long TransferEpoch,
    long ExpectedLength,
    ulong DeviceId = 0,
    ulong Inode = 0)
{
    public static VfsIpcResponse Success(long transferEpoch = 0)
        => new(VfsIpcStatus.Success, VfsIpcOpenDisposition.Unmanaged, 0, transferEpoch, 0);

    public static VfsIpcResponse Error(VfsIpcStatus status, long transferEpoch = 0)
        => new(status, VfsIpcOpenDisposition.Unmanaged, 0, transferEpoch, 0);
}

public static class VfsIpcProtocol
{
    private const uint Magic = 0x4f564653; // OVFS
    public const byte Version = 3;
    public const int HeaderLength = 12;
    public const int ResponseLength = 52;
    public const int MaxPayloadBytes = 32 * 1024;

    public static async Task WriteRequestAsync(
        Socket socket,
        VfsIpcRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var payload = BuildPayload(request);
        if (payload.Length > MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "VFS IPC payload is too large.");
        }

        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), Magic);
        header[4] = Version;
        header[5] = (byte)request.Operation;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), (uint)payload.Length);
        await SendExactAsync(socket, header, cancellationToken).ConfigureAwait(false);
        await SendExactAsync(socket, payload, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<(bool Success, VfsIpcRequest Request, VfsIpcStatus ErrorStatus)> TryReadRequestAsync(
        Socket socket,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var header = new byte[HeaderLength];
        if (await ReceiveExactAsync(socket, header, cancellationToken).ConfigureAwait(false) != HeaderLength)
        {
            return (false, default, VfsIpcStatus.InvalidHeader);
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4)) != Magic ||
            header[4] != Version ||
            !Enum.IsDefined((VfsIpcOperation)header[5]))
        {
            return (false, default, VfsIpcStatus.InvalidHeader);
        }

        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        if (payloadLength > MaxPayloadBytes)
        {
            return (false, default, VfsIpcStatus.InvalidLength);
        }

        var payload = new byte[payloadLength];
        if (await ReceiveExactAsync(socket, payload, cancellationToken).ConfigureAwait(false) != payload.Length)
        {
            return (false, default, VfsIpcStatus.ShortBody);
        }

        return TryParsePayload((VfsIpcOperation)header[5], payload, out var request)
            ? (true, request, VfsIpcStatus.Success)
            : (false, default, VfsIpcStatus.InvalidLength);
    }

    public static async Task WriteResponseAsync(
        Socket socket,
        VfsIpcResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var buffer = new byte[ResponseLength];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), Magic);
        buffer[4] = Version;
        buffer[5] = (byte)response.Status;
        buffer[6] = (byte)response.OpenDisposition;
        buffer[7] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(12, 8), response.LeaseId);
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(20, 8), response.TransferEpoch);
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(28, 8), response.ExpectedLength);
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(36, 8), response.DeviceId);
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(44, 8), response.Inode);
        await SendExactAsync(socket, buffer, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<VfsIpcResponse?> ReadResponseAsync(
        Socket socket,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var buffer = new byte[ResponseLength];
        if (await ReceiveExactAsync(socket, buffer, cancellationToken).ConfigureAwait(false) != ResponseLength ||
            BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0, 4)) != Magic ||
            buffer[4] != Version)
        {
            return null;
        }

        return new VfsIpcResponse(
            (VfsIpcStatus)buffer[5],
            (VfsIpcOpenDisposition)buffer[6],
            BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(12, 8)),
            BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(20, 8)),
            BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(28, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(36, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(44, 8)));
    }

    public static async Task<int> ReceiveExactAsync(
        Socket socket,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await socket
                .ReceiveAsync(buffer.AsMemory(total), SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static byte[] BuildPayload(VfsIpcRequest request)
    {
        switch (request.Operation)
        {
            case VfsIpcOperation.Open:
                {
                    var path = Encoding.UTF8.GetBytes(request.Path ?? string.Empty);
                    var session = Encoding.UTF8.GetBytes(request.SessionId ?? string.Empty);
                    if (path.Length == 0)
                    {
                        throw new ArgumentOutOfRangeException(nameof(request), "Open path is required.");
                    }

                    var payload = new byte[9 + path.Length + session.Length];
                    BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), (uint)path.Length);
                    BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), (uint)session.Length);
                    payload[8] = request.WriteAccess ? (byte)1 : (byte)0;
                    path.CopyTo(payload.AsSpan(9));
                    session.CopyTo(payload.AsSpan(9 + path.Length));
                    return payload;
                }
            case VfsIpcOperation.EnsureRange:
                {
                    var payload = new byte[32];
                    BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(0, 8), request.LeaseId);
                    BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(8, 8), request.TransferEpoch);
                    BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(16, 8), request.Offset);
                    BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(24, 8), request.Length);
                    return payload;
                }
            case VfsIpcOperation.EnsureComplete:
                {
                    var reason = Encoding.UTF8.GetBytes(request.Reason ?? string.Empty);
                    var payload = new byte[20 + reason.Length];
                    BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(0, 8), request.LeaseId);
                    BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(8, 8), request.TransferEpoch);
                    BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16, 4), (uint)reason.Length);
                    reason.CopyTo(payload.AsSpan(20));
                    return payload;
                }
            case VfsIpcOperation.Release:
            case VfsIpcOperation.AcknowledgeOpen:
                {
                    var payload = new byte[8];
                    BinaryPrimitives.WriteUInt64BigEndian(payload, request.LeaseId);
                    return payload;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    private static bool TryParsePayload(
        VfsIpcOperation operation,
        ReadOnlySpan<byte> payload,
        out VfsIpcRequest request)
    {
        request = default;
        switch (operation)
        {
            case VfsIpcOperation.Open:
                {
                    if (payload.Length < 9)
                    {
                        return false;
                    }

                    var pathLengthValue = BinaryPrimitives.ReadUInt32BigEndian(payload[..4]);
                    var sessionLengthValue = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4));
                    if (pathLengthValue > int.MaxValue || sessionLengthValue > int.MaxValue)
                    {
                        return false;
                    }

                    var pathLength = (int)pathLengthValue;
                    var sessionLength = (int)sessionLengthValue;
                    if (pathLength <= 0 ||
                        payload[8] > 1 ||
                        pathLength + sessionLength != payload.Length - 9)
                    {
                        return false;
                    }

                    request = VfsIpcRequest.Open(
                        Encoding.UTF8.GetString(payload.Slice(9, pathLength)),
                        Encoding.UTF8.GetString(payload.Slice(9 + pathLength, sessionLength)),
                        writeAccess: payload[8] == 1);
                    return true;
                }
            case VfsIpcOperation.EnsureRange:
                if (payload.Length != 32)
                {
                    return false;
                }

                request = VfsIpcRequest.EnsureRange(
                    BinaryPrimitives.ReadUInt64BigEndian(payload[..8]),
                    BinaryPrimitives.ReadInt64BigEndian(payload.Slice(8, 8)),
                    BinaryPrimitives.ReadInt64BigEndian(payload.Slice(16, 8)),
                    BinaryPrimitives.ReadInt64BigEndian(payload.Slice(24, 8)));
                return true;
            case VfsIpcOperation.EnsureComplete:
                {
                    if (payload.Length < 20)
                    {
                        return false;
                    }

                    var reasonLengthValue = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(16, 4));
                    if (reasonLengthValue > int.MaxValue)
                    {
                        return false;
                    }

                    var reasonLength = (int)reasonLengthValue;
                    if (reasonLength != payload.Length - 20)
                    {
                        return false;
                    }

                    request = VfsIpcRequest.EnsureComplete(
                        BinaryPrimitives.ReadUInt64BigEndian(payload[..8]),
                        BinaryPrimitives.ReadInt64BigEndian(payload.Slice(8, 8)),
                        Encoding.UTF8.GetString(payload.Slice(20, reasonLength)));
                    return true;
                }
            case VfsIpcOperation.Release:
            case VfsIpcOperation.AcknowledgeOpen:
                if (payload.Length != 8)
                {
                    return false;
                }

                var leaseId = BinaryPrimitives.ReadUInt64BigEndian(payload);
                request = operation == VfsIpcOperation.Release
                    ? VfsIpcRequest.Release(leaseId)
                    : VfsIpcRequest.AcknowledgeOpen(leaseId);
                return true;
            default:
                return false;
        }
    }

    private static async Task SendExactAsync(
        Socket socket,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var sent = 0;
        while (sent < buffer.Length)
        {
            var wrote = await socket
                .SendAsync(buffer.AsMemory(sent), SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);
            if (wrote == 0)
            {
                throw new IOException("Socket closed while writing VFS IPC payload.");
            }

            sent += wrote;
        }
    }
}
