using Offloadr.Runner.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading.Channels;

namespace Offloadr.Runner.Core;

internal sealed class RunnerLogRelay : IAsyncDisposable
{
    private const int DefaultQueueCapacity = 4096;
    private const int MaxBatchSize = 64;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly string _runnerId;
    private readonly TimeProvider _clock;
    private readonly string _relayInstanceId = Guid.NewGuid().ToString("n");
    private readonly Channel<QueuedRunnerLogEntry> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _senderTask;
    private readonly object _sinkLock = new();
    private readonly LogRelayDropWarning _dropWarning = new(LogRelayBounds.DropWarningInterval);
    private long _nextSequence;
    private volatile bool _completing;

    private IRunnerSessionSink? _sink;
    private CancellationTokenSource? _sinkLifetime;
    private long _sinkGeneration;

    public RunnerLogRelay(string runnerId)
        : this(runnerId, DefaultQueueCapacity)
    {
    }

    internal RunnerLogRelay(string runnerId, int queueCapacity, TimeProvider? clock = null)
    {
        _runnerId = runnerId ?? throw new ArgumentNullException(nameof(runnerId));
        _clock = clock ?? TimeProvider.System;
        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity), "Queue capacity must be positive.");
        }

        _queue = Channel.CreateBounded<QueuedRunnerLogEntry>(new BoundedChannelOptions(queueCapacity)
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
            _sinkLifetime?.Cancel();
            _sinkLifetime?.Dispose();
            _sink = sink;
            _sinkLifetime = new CancellationTokenSource();
            _sinkGeneration++;
        }
    }

    public void DetachSink(IRunnerSessionSink sink)
    {
        lock (_sinkLock)
        {
            if (ReferenceEquals(_sink, sink))
            {
                _sinkLifetime?.Cancel();
                _sinkLifetime?.Dispose();
                _sinkLifetime = null;
                _sink = null;
                _sinkGeneration++;
            }
        }
    }

    public void Enqueue(string category, string message)
    {
        if (_completing || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        long capturedGeneration;
        lock (_sinkLock)
        {
            if (_sink is null)
            {
                return;
            }
            capturedGeneration = _sinkGeneration;
        }

        var deadline = new LogRelayDeadline(_clock);
        var entry = new RunnerLogEntry
        {
            RunnerId = _runnerId,
            RelayInstanceId = _relayInstanceId,
            Category = string.IsNullOrWhiteSpace(category) ? "RunnerAgent" : category.Trim(),
            Message = LogRelayBounds.TruncateMessage(message.Trim()),
            CreatedUtc = Timestamp.FromDateTimeOffset(deadline.CapturedUtc),
            Sequence = (ulong)Interlocked.Increment(ref _nextSequence),
        };

        _queue.Writer.TryWrite(new QueuedRunnerLogEntry(entry, capturedGeneration, deadline));
    }

    private async Task SenderLoopAsync()
    {
        var buffer = new List<QueuedRunnerLogEntry>(MaxBatchSize);
        QueuedRunnerLogEntry? carry = null;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                FillBuffer(buffer, ref carry);
                if (buffer.Count == 0)
                {
                    try
                    {
                        buffer.Add(await _queue.Reader.ReadAsync(_shutdown.Token).ConfigureAwait(false));
                    }
                    catch (ChannelClosedException) { break; }
                    catch (OperationCanceledException) { break; }
                    buffer.RemoveAll(entry => entry.Deadline.Remaining <= TimeSpan.Zero);
                    if (buffer.Count == 0) continue;
                    FillBuffer(buffer, ref carry);
                }

                if (buffer.Count < MaxBatchSize && carry is null)
                {
                    try { await Task.Delay(FlushInterval, _shutdown.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                await FlushBufferWithRetryAsync(buffer).ConfigureAwait(false);
                buffer.Clear();
            }

            // Graceful runner shutdown still gets one bounded attempt per surviving batch.
            // Session shutdown has already cancelled admission, so its batches are just retired.
            while (true)
            {
                FillBuffer(buffer, ref carry);
                if (buffer.Count == 0) break;
                await TryFlushBufferAsync(buffer).ConfigureAwait(false);
                buffer.Clear();
            }
        }
        finally
        {
            buffer.Clear();
            carry = null;
            while (_queue.Reader.TryRead(out _)) { }
        }
    }

    // Keep payload temporaries on this synchronous stack, outside the idle sender await.
    private void FillBuffer(List<QueuedRunnerLogEntry> buffer, ref QueuedRunnerLogEntry? carry)
    {
        var bytes = buffer.Sum(entry => LogRelayBounds.EntryBytes(entry.Entry));
        if (carry is { } pending)
        {
            carry = null;
            if (pending.Deadline.Remaining > TimeSpan.Zero)
            {
                buffer.Add(pending);
                bytes += LogRelayBounds.EntryBytes(pending.Entry);
            }
        }
        while (buffer.Count < MaxBatchSize && _queue.Reader.TryRead(out var next))
        {
            if (next.Deadline.Remaining <= TimeSpan.Zero) continue;
            var nextBytes = LogRelayBounds.EntryBytes(next.Entry);
            if (buffer.Count > 0 && bytes + nextBytes > LogRelayBounds.MaxBatchBytes)
            {
                carry = next;
                break;
            }
            buffer.Add(next);
            bytes += nextBytes;
        }
    }

    private async Task FlushBufferWithRetryAsync(List<QueuedRunnerLogEntry> entries)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            if (await TryFlushBufferAsync(entries).ConfigureAwait(false))
            {
                return;
            }

            if (_completing)
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

    private async Task<bool> TryFlushBufferAsync(List<QueuedRunnerLogEntry> entries)
    {
        DiscardStaleEntries(entries);
        if (entries.Count == 0)
        {
            return true;
        }

        if (_shutdown.IsCancellationRequested)
        {
            return false;
        }

        IRunnerSessionSink? sink;
        CancellationToken sinkToken;
        lock (_sinkLock)
        {
            sink = _sink;
            // Capture the token while its source is still owned by this connection.
            sinkToken = _sinkLifetime?.Token ?? default;
        }

        if (sink is null || !sinkToken.CanBeCanceled)
        {
            return false;
        }

        var remaining = entries.Min(entry => entry.Deadline.Remaining);
        if (remaining <= TimeSpan.Zero) return false;
        var request = new ReportRunnerLogsRequest();
        request.Entries.AddRange(entries.Select(queued => queued.Entry));

        try
        {
            using var budget = new CancellationTokenSource(remaining < LogRelayBounds.AttemptTimeout ? remaining : LogRelayBounds.AttemptTimeout, _clock);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, sinkToken, budget.Token);
            // Await the original call even when it ignores cancellation. Never overlap a replacement.
            await sink.ReportRunnerLogsAsync(request, linkedCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException) when (sinkToken.IsCancellationRequested)
        {
            return true;
        }
        catch (RpcException ex) when (LogRelayBounds.IsNonRetryable(ex))
        {
            // Retrying a batch the control plane refuses would block every later log line.
            // The warning is rate limited because it is forwarded through this relay too.
            var totals = _dropWarning.RecordDrop(entries.Count, DateTime.UtcNow);
            if (totals is { } dropped)
            {
                RunnerLog.Warning<RunnerLogRelay>(
                    $"Dropped {dropped.Batches} runner log batch(es) ({dropped.Entries} entries) the control plane will not accept; last status {ex.StatusCode}.");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void DiscardStaleEntries(List<QueuedRunnerLogEntry> entries)
    {
        var activeGeneration = Volatile.Read(ref _sinkGeneration);
        entries.RemoveAll(entry => entry.Generation != activeGeneration || entry.Deadline.Remaining <= TimeSpan.Zero);
    }

    private readonly record struct QueuedRunnerLogEntry(RunnerLogEntry Entry, long Generation, LogRelayDeadline Deadline);
    public async ValueTask DisposeAsync()
    {
        _completing = true;
        _queue.Writer.TryComplete();

        var gracefulShutdown = await Task.WhenAny(_senderTask, Task.Delay(ShutdownDrainTimeout)).ConfigureAwait(false);
        if (!ReferenceEquals(gracefulShutdown, _senderTask) && !_shutdown.IsCancellationRequested)
        {
            _shutdown.Cancel();
        }

        try
        {
            await _senderTask.ConfigureAwait(false);
        }
        catch
        {
            // Best effort during shutdown.
        }

        _sinkLifetime?.Cancel();
        _sinkLifetime?.Dispose();
        _shutdown.Dispose();
    }
}

internal sealed class RunnerLogForwardingProvider : ILoggerProvider
{
    private readonly RunnerLogRelay _relay;

    public RunnerLogForwardingProvider(RunnerLogRelay relay)
    {
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
    }

    public ILogger CreateLogger(string categoryName) => new ForwardingLogger(categoryName, _relay);

    public void Dispose()
    {
    }

    private sealed class ForwardingLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly RunnerLogRelay _relay;

        public ForwardingLogger(string categoryName, RunnerLogRelay relay)
        {
            _categoryName = string.IsNullOrWhiteSpace(categoryName) ? "RunnerAgent" : categoryName;
            _relay = relay;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)
                || string.IsNullOrWhiteSpace(_categoryName)
                || _categoryName.StartsWith("session:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(eventId.Name))
            {
                message = string.Concat(eventId.Name, " ", message);
            }

            _relay.Enqueue(_categoryName, message);
        }
    }
}
