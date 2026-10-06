using Offloadr.Runner.V1;
using Google.Protobuf.WellKnownTypes;
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
            Message = message,
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

        while (!_shutdown.IsCancellationRequested)
        {
            SessionProcessLogEntry entry;
            try
            {
                entry = await _queue.Reader.ReadAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            buffer.Add(entry);
            var flushDelay = Task.Delay(FlushInterval, _shutdown.Token);

            while (buffer.Count < MaxBatchSize)
            {
                if (_queue.Reader.TryRead(out var next))
                {
                    buffer.Add(next);
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

        while (_queue.Reader.TryRead(out var remaining))
        {
            buffer.Add(remaining);
            if (buffer.Count >= MaxBatchSize)
            {
                await TryFlushBufferAsync(buffer).ConfigureAwait(false);
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            await TryFlushBufferAsync(buffer).ConfigureAwait(false);
        }
    }

    private async Task FlushBufferWithRetryAsync(List<SessionProcessLogEntry> entries)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            if (await TryFlushBufferAsync(entries).ConfigureAwait(false))
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

    private async Task<bool> TryFlushBufferAsync(List<SessionProcessLogEntry> entries)
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
        catch (Exception ex)
        {
            RunnerLog.Error<SessionProcessLogRelay>(ex, $"Failed to report session process logs batch ({entries.Count}): {ex.Message}");
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
