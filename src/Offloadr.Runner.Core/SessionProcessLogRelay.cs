using Offloadr.Runner.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using System.Threading.Channels;

namespace Offloadr.Runner.Core;

/// <summary>
/// Batches stdout/stderr log lines from managed ComfyUI processes and forwards them to Offloadr API.
/// </summary>
internal sealed class SessionProcessLogRelay : IAsyncDisposable
{
    private const int DefaultQueueCapacity = 4096;
    private const int MaxBatchSize = 64;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(150);

    private readonly string _runnerId;
    private readonly RuntimeIdentityRegistry? _runtimeIdentities;
    private readonly Channel<SessionProcessLogEntry> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _senderTask;
    private readonly object _sinkLock = new();
    private readonly LogRelayDropWarning _dropWarning = new(LogRelayBounds.DropWarningInterval);
    private long _nextSequence;

    private IRunnerSessionSink? _sink;

    public SessionProcessLogRelay(string runnerId, RuntimeIdentityRegistry? runtimeIdentities = null)
        : this(runnerId, DefaultQueueCapacity, runtimeIdentities)
    {
    }

    internal SessionProcessLogRelay(string runnerId, int queueCapacity, RuntimeIdentityRegistry? runtimeIdentities = null)
    {
        _runnerId = runnerId ?? throw new ArgumentNullException(nameof(runnerId));
        _runtimeIdentities = runtimeIdentities;
        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity), "Queue capacity must be positive.");
        }

        _queue = Channel.CreateBounded<SessionProcessLogEntry>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _senderTask = Task.Run(SenderLoopAsync);
    }

    public void AttachSink(IRunnerSessionSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        lock (_sinkLock)
        {
            _sink = sink;
        }
    }

    public void DetachSink(IRunnerSessionSink sink)
    {
        lock (_sinkLock)
        {
            if (ReferenceEquals(_sink, sink))
            {
                _sink = null;
            }
        }
    }

    public void Enqueue(string sessionId, SessionProcessLogStream stream, string message)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var entry = new SessionProcessLogEntry
        {
            RunnerId = _runnerId,
            SessionId = sessionId.Trim(),
            Stream = stream,
            Message = LogRelayBounds.TruncateMessage(message),
            CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            Sequence = (ulong)Interlocked.Increment(ref _nextSequence)
        };
        if (_runtimeIdentities?.TryGet(sessionId, out var identity) == true)
        {
            entry.LifecycleGeneration = identity.LifecycleGeneration;
            entry.RuntimeEpoch = identity.RuntimeEpoch;
            entry.RuntimeInstanceId = identity.RuntimeInstanceId;
        }

        _queue.Writer.TryWrite(entry);
    }

    private async Task SenderLoopAsync()
    {
        var buffer = new List<SessionProcessLogEntry>(MaxBatchSize);
        SessionProcessLogEntry? carry = null;

        while (!_shutdown.IsCancellationRequested)
        {
            SessionProcessLogEntry entry;
            if (carry is not null)
            {
                entry = carry;
                carry = null;
            }
            else
            {
                try
                {
                    entry = await _queue.Reader.ReadAsync(_shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            buffer.Add(entry);
            var batchBytes = LogRelayBounds.EntryBytes(entry);
            var flushDelay = Task.Delay(FlushInterval, _shutdown.Token);

            while (buffer.Count < MaxBatchSize)
            {
                if (_queue.Reader.TryRead(out var next))
                {
                    var nextBytes = LogRelayBounds.EntryBytes(next);
                    if (batchBytes + nextBytes > LogRelayBounds.MaxBatchBytes)
                    {
                        carry = next;
                        break;
                    }

                    buffer.Add(next);
                    batchBytes += nextBytes;
                    continue;
                }

                try
                {
                    await Task.WhenAny(flushDelay).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!flushDelay.IsCompleted)
                {
                    continue;
                }

                break;
            }

            await FlushBufferWithRetryAsync(buffer).ConfigureAwait(false);
            buffer.Clear();
        }

        var drainBytes = 0;
        if (carry is not null)
        {
            buffer.Add(carry);
            drainBytes = LogRelayBounds.EntryBytes(carry);
        }

        while (_queue.Reader.TryRead(out var remaining))
        {
            var remainingBytes = LogRelayBounds.EntryBytes(remaining);
            if (buffer.Count >= MaxBatchSize || (buffer.Count > 0 && drainBytes + remainingBytes > LogRelayBounds.MaxBatchBytes))
            {
                await TryFlushBufferAsync(buffer).ConfigureAwait(false);
                buffer.Clear();
                drainBytes = 0;
            }

            buffer.Add(remaining);
            drainBytes += remainingBytes;
        }

        if (buffer.Count > 0)
        {
            await TryFlushBufferAsync(buffer).ConfigureAwait(false);
        }
    }

    private async Task FlushBufferWithRetryAsync(List<SessionProcessLogEntry> entries)
    {
        var attempt = 0;
        while (!_shutdown.IsCancellationRequested)
        {
            if (await TryFlushBufferAsync(entries, logFailure: attempt++ == 0).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                await Task.Delay(FlushInterval, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Returns true when the batch is finished with (delivered, or dropped because the control plane
    /// will never accept it) and false when the same batch should be retried.
    /// </summary>
    private async Task<bool> TryFlushBufferAsync(List<SessionProcessLogEntry> entries, bool logFailure = true)
    {
        if (entries.Count == 0)
        {
            return true;
        }

        if (_shutdown.IsCancellationRequested)
        {
            return false;
        }

        IRunnerSessionSink? sink;
        lock (_sinkLock)
        {
            sink = _sink;
        }

        if (sink is null)
        {
            return false;
        }

        var request = new ReportSessionLogsRequest();
        request.Entries.AddRange(entries);

        try
        {
            await sink.ReportSessionLogsAsync(request, _shutdown.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return false;
        }
        catch (RpcException ex) when (LogRelayBounds.IsNonRetryable(ex))
        {
            // Retrying a batch the control plane refuses would block every later session log line.
            var totals = _dropWarning.RecordDrop(entries.Count, DateTime.UtcNow);
            if (totals is { } dropped)
            {
                RunnerLog.Warning<SessionProcessLogRelay>(
                    $"Dropped {dropped.Batches} session process log batch(es) ({dropped.Entries} entries) the control plane will not accept; last status {ex.StatusCode}.");
            }

            return true;
        }
        catch (Exception ex)
        {
            if (logFailure)
            {
                RunnerLog.Error<SessionProcessLogRelay>(ex, $"Failed to report session process logs batch ({entries.Count}); retrying: {ex.Message}");
            }

            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_shutdown.IsCancellationRequested)
        {
            _shutdown.Cancel();
        }

        _queue.Writer.TryComplete();

        try
        {
            await _senderTask.ConfigureAwait(false);
        }
        catch
        {
            // Best effort during shutdown.
        }

        _shutdown.Dispose();
    }
}
