using Offloadr.Runner.V1;
using Google.Protobuf.WellKnownTypes;
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
    private readonly Channel<QueuedRunnerLogEntry> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _senderTask;
    private readonly object _sinkLock = new();
    private long _nextSequence;
    private volatile bool _completing;

    private IRunnerSessionSink? _sink;
    private CancellationTokenSource? _sinkLifetime;
    private long _sinkGeneration;

    public RunnerLogRelay(string runnerId)
        : this(runnerId, DefaultQueueCapacity)
    {
    }

    internal RunnerLogRelay(string runnerId, int queueCapacity)
    {
        _runnerId = runnerId ?? throw new ArgumentNullException(nameof(runnerId));
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
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (_sinkLock)
        {
            if (_sink is null)
            {
                return;
            }
        }

        var entry = new RunnerLogEntry
        {
            RunnerId = _runnerId,
            Category = string.IsNullOrWhiteSpace(category) ? "RunnerAgent" : category.Trim(),
            Message = message.Trim(),
            CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            Sequence = (ulong)Interlocked.Increment(ref _nextSequence),
        };

        _queue.Writer.TryWrite(new QueuedRunnerLogEntry(entry, Volatile.Read(ref _sinkGeneration)));
    }

    private async Task SenderLoopAsync()
    {
        var buffer = new List<QueuedRunnerLogEntry>(MaxBatchSize);

        while (!_shutdown.IsCancellationRequested)
        {
            QueuedRunnerLogEntry entry;
            try
            {
                entry = await _queue.Reader.ReadAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                break;
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
        CancellationTokenSource? sinkLifetime;
        lock (_sinkLock)
        {
            sink = _sink;
            sinkLifetime = _sinkLifetime;
        }

        if (sink is null || sinkLifetime is null)
        {
            return false;
        }

        var request = new ReportRunnerLogsRequest();
        request.Entries.AddRange(entries.Select(queued => queued.Entry));

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, sinkLifetime.Token);
            await sink.ReportRunnerLogsAsync(request, linkedCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException) when (sinkLifetime.IsCancellationRequested)
        {
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
        entries.RemoveAll(entry => entry.Generation != activeGeneration);
    }

    private readonly record struct QueuedRunnerLogEntry(RunnerLogEntry Entry, long Generation);
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
