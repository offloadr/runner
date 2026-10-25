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
    private readonly TimeProvider _clock;
    private readonly RuntimeIdentityRegistry? _runtimeIdentities;
    private readonly Channel<QueuedSessionLogEntry> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _senderTask;
    private readonly object _sinkLock = new();
    private readonly LogRelayDropWarning _dropWarning = new(LogRelayBounds.DropWarningInterval);
    private long _nextSequence;
    private volatile bool _completing;

    private IRunnerSessionSink? _sink;

    public SessionProcessLogRelay(string runnerId, RuntimeIdentityRegistry? runtimeIdentities = null)
        : this(runnerId, DefaultQueueCapacity, runtimeIdentities)
    {
    }

    internal SessionProcessLogRelay(string runnerId, int queueCapacity, RuntimeIdentityRegistry? runtimeIdentities = null, TimeProvider? clock = null)
    {
        _runnerId = runnerId ?? throw new ArgumentNullException(nameof(runnerId));
        _clock = clock ?? TimeProvider.System;
        _runtimeIdentities = runtimeIdentities;
        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity), "Queue capacity must be positive.");
        }

        _queue = Channel.CreateBounded<QueuedSessionLogEntry>(new BoundedChannelOptions(queueCapacity)
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

    /// <summary>
    /// Queues one line of session process output. Pass the identity of the runtime that
    /// wrote it: after a replacement under the same session id, the old process's buffered
    /// output must not be labelled with the new runtime's identity.
    /// </summary>
    public void Enqueue(string sessionId, SessionProcessLogStream stream, string message, RuntimeIdentity? runtimeIdentity = null)
    {
        if (_completing || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var deadline = new LogRelayDeadline(_clock);
        var entry = new SessionProcessLogEntry
        {
            RunnerId = _runnerId,
            SessionId = sessionId.Trim(),
            Stream = stream,
            Message = LogRelayBounds.TruncateMessage(message),
            CreatedUtc = Timestamp.FromDateTimeOffset(deadline.CapturedUtc),
            Sequence = (ulong)Interlocked.Increment(ref _nextSequence)
        };
        if (runtimeIdentity is { IsValid: true } writer)
        {
            entry.LifecycleGeneration = writer.LifecycleGeneration;
            entry.RuntimeEpoch = writer.RuntimeEpoch;
            entry.RuntimeInstanceId = writer.RuntimeInstanceId;
        }
        else if (_runtimeIdentities?.TryGet(sessionId, out var identity) == true)
        {
            entry.LifecycleGeneration = identity.LifecycleGeneration;
            entry.RuntimeEpoch = identity.RuntimeEpoch;
            entry.RuntimeInstanceId = identity.RuntimeInstanceId;
        }

        _queue.Writer.TryWrite(new(entry, deadline));
    }

    private async Task SenderLoopAsync()
    {
        var buffer = new List<QueuedSessionLogEntry>(MaxBatchSize);
        QueuedSessionLogEntry? carry = null;
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
    private void FillBuffer(List<QueuedSessionLogEntry> buffer, ref QueuedSessionLogEntry? carry)
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

    private async Task FlushBufferWithRetryAsync(List<QueuedSessionLogEntry> entries)
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
    private async Task<bool> TryFlushBufferAsync(List<QueuedSessionLogEntry> entries, bool logFailure = true)
    {
        entries.RemoveAll(entry => entry.Deadline.Remaining <= TimeSpan.Zero);
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

        var remaining = entries.Min(entry => entry.Deadline.Remaining);
        if (remaining <= TimeSpan.Zero) return false;
        var request = new ReportSessionLogsRequest();
        request.Entries.AddRange(entries.Select(queued => queued.Entry));

        try
        {
            using var budget = new CancellationTokenSource(remaining < LogRelayBounds.AttemptTimeout ? remaining : LogRelayBounds.AttemptTimeout, _clock);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, budget.Token);
            // Cancellation retires this attempt, not its original task. Join before any retry.
            await sink.ReportSessionLogsAsync(request, attempt.Token).ConfigureAwait(false);
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
                RunnerLog.Warning<SessionProcessLogRelay>($"Session log report failed; entries={entries.Count}; errorClass={ex.GetType().Name}; retrying within original age.");
            }

            return false;
        }
    }

    private readonly record struct QueuedSessionLogEntry(SessionProcessLogEntry Entry, LogRelayDeadline Deadline);

    public async ValueTask DisposeAsync()
    {
        _completing = true;
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
