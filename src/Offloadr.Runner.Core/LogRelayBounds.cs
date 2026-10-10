using System.Text;
using Google.Protobuf;
using Grpc.Core;

namespace Offloadr.Runner.Core;

/// <summary>
/// Size and delivery bounds shared by the runner and session log relays, so one oversized line
/// or a batch the control plane refuses cannot stall log delivery or grow memory without limit.
/// </summary>
internal static class LogRelayBounds
{
    /// <summary>Largest UTF-8 size of a single forwarded log message, including the truncation marker.</summary>
    public const int MaxMessageBytes = 16 * 1024;

    /// <summary>Largest serialized size of the entries in one report batch, far below the gRPC message limit.</summary>
    public const int MaxBatchBytes = 1024 * 1024;

    public static readonly TimeSpan DropWarningInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Upper bound of the bytes an entry adds to a report request: the entry itself plus the
    /// repeated-field tag and length prefix.
    /// </summary>
    public static int EntryBytes(IMessage entry)
    {
        var size = entry.CalculateSize();
        return size + 1 + CodedOutputStream.ComputeLengthSize(size);
    }

    /// <summary>
    /// Returns <paramref name="message"/> unchanged when it fits in <paramref name="maxBytes"/> UTF-8 bytes,
    /// otherwise a prefix that ends on a character boundary followed by a marker naming the original size.
    /// </summary>
    public static string TruncateMessage(string message, int maxBytes = MaxMessageBytes)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Length <= maxBytes / 3)
        {
            return message;
        }

        var totalBytes = Encoding.UTF8.GetByteCount(message);
        if (totalBytes <= maxBytes)
        {
            return message;
        }

        var marker = $" [truncated from {totalBytes} bytes]";
        var budget = Math.Max(0, maxBytes - Encoding.UTF8.GetByteCount(marker));
        var usedBytes = 0;
        var index = 0;
        while (index < message.Length)
        {
            var charCount = char.IsHighSurrogate(message[index])
                && index + 1 < message.Length
                && char.IsLowSurrogate(message[index + 1])
                    ? 2
                    : 1;
            var charBytes = Encoding.UTF8.GetByteCount(message.AsSpan(index, charCount));
            if (usedBytes + charBytes > budget)
            {
                break;
            }

            usedBytes += charBytes;
            index += charCount;
        }

        return string.Concat(message.AsSpan(0, index), marker);
    }

    /// <summary>
    /// True when retrying the same batch cannot succeed: the control plane rejected its content or
    /// identity, or the batch exceeds a message size limit. Such a batch is dropped instead of
    /// blocking every later log line.
    /// </summary>
    public static bool IsNonRetryable(Exception exception)
        => exception is RpcException rpc && rpc.StatusCode is StatusCode.InvalidArgument
            or StatusCode.FailedPrecondition
            or StatusCode.NotFound
            or StatusCode.AlreadyExists
            or StatusCode.PermissionDenied
            or StatusCode.OutOfRange
            or StatusCode.Unimplemented
            or StatusCode.ResourceExhausted;
}

/// <summary>Rate limits the warning a relay logs when it drops batches, so dropping cannot flood the logs.</summary>
internal sealed class LogRelayDropWarning
{
    private readonly object _lock = new();
    private readonly TimeSpan _interval;
    private DateTime _lastWarningUtc = DateTime.MinValue;
    private long _droppedBatches;
    private long _droppedEntries;

    public LogRelayDropWarning(TimeSpan interval)
    {
        _interval = interval;
    }

    /// <summary>
    /// Records a dropped batch. Returns the totals since the last warning when a warning is due,
    /// or null when the drop is folded into the next warning.
    /// </summary>
    public (long Batches, long Entries)? RecordDrop(int entries, DateTime utcNow)
    {
        lock (_lock)
        {
            _droppedBatches++;
            _droppedEntries += entries;
            if (utcNow - _lastWarningUtc < _interval)
            {
                return null;
            }

            var totals = (_droppedBatches, _droppedEntries);
            _lastWarningUtc = utcNow;
            _droppedBatches = 0;
            _droppedEntries = 0;
            return totals;
        }
    }
}
