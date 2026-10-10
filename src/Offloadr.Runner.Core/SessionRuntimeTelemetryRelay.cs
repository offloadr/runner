using Offloadr.Runner.V1;
using Grpc.Core;
using System.Threading.Channels;

namespace Offloadr.Runner.Core;

/// <summary>
/// Batches live runtime telemetry snapshots for the active session and forwards them to Offloadr API.
/// </summary>
internal sealed class SessionRuntimeTelemetryRelay : IAsyncDisposable
{
    private const int DefaultQueueCapacity = 256;
    private const int MaxBatchSize = 16;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    private readonly string _runnerId;
    private readonly RuntimeIdentityRegistry? _runtimeIdentities;
    private readonly Channel<SessionRuntimeTelemetryEntry> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _senderTask;
    private readonly object _sinkLock = new();

    private IRunnerSessionSink? _sink;

    public SessionRuntimeTelemetryRelay(string runnerId, RuntimeIdentityRegistry? runtimeIdentities = null)
        : this(runnerId, DefaultQueueCapacity, runtimeIdentities)
    {
    }

    internal SessionRuntimeTelemetryRelay(string runnerId, int queueCapacity, RuntimeIdentityRegistry? runtimeIdentities = null)
    {
        _runnerId = runnerId ?? throw new ArgumentNullException(nameof(runnerId));
        _runtimeIdentities = runtimeIdentities;
        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity), "Queue capacity must be positive.");
        }

        _queue = Channel.CreateBounded<SessionRuntimeTelemetryEntry>(new BoundedChannelOptions(queueCapacity)
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
    /// Queues one telemetry sample. Pass the identity of the runtime it was taken for, so a
    /// sample is not labelled with a replacement that became current while it was taken.
    /// </summary>
    public void Enqueue(string sessionId, RunnerRuntimeTelemetrySnapshot stats, RuntimeIdentity? runtimeIdentity = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || stats is null)
        {
            return;
        }

        var entry = new SessionRuntimeTelemetryEntry
        {
            RunnerId = _runnerId,
            SessionId = sessionId.Trim(),
            Telemetry = stats.Clone()
        };
        if (runtimeIdentity is { IsValid: true } sampled)
        {
            entry.LifecycleGeneration = sampled.LifecycleGeneration;
            entry.RuntimeEpoch = sampled.RuntimeEpoch;
            entry.RuntimeInstanceId = sampled.RuntimeInstanceId;
        }
        else if (_runtimeIdentities?.TryGet(sessionId, out var identity) == true)
        {
            entry.LifecycleGeneration = identity.LifecycleGeneration;
            entry.RuntimeEpoch = identity.RuntimeEpoch;
            entry.RuntimeInstanceId = identity.RuntimeInstanceId;
        }
        _queue.Writer.TryWrite(entry);
    }

    private async Task SenderLoopAsync()
    {
        var buffer = new List<SessionRuntimeTelemetryEntry>(MaxBatchSize);

        while (!_shutdown.IsCancellationRequested)
        {
            SessionRuntimeTelemetryEntry entry;
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

    private async Task FlushBufferWithRetryAsync(List<SessionRuntimeTelemetryEntry> entries)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            var flushResult = await TryFlushBufferAsync(entries).ConfigureAwait(false);
            if (flushResult != FlushResult.Retry)
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

    private async Task<FlushResult> TryFlushBufferAsync(List<SessionRuntimeTelemetryEntry> entries)
    {
        if (entries.Count == 0)
        {
            return FlushResult.Delivered;
        }

        if (_shutdown.IsCancellationRequested)
        {
            return FlushResult.Retry;
        }

        IRunnerSessionSink? sink;
        lock (_sinkLock)
        {
            sink = _sink;
        }

        if (sink is null)
        {
            return FlushResult.Retry;
        }

        var request = new ReportRuntimeTelemetryRequest();
        request.Entries.AddRange(entries);

        try
        {
            await sink.ReportRuntimeTelemetryAsync(request, _shutdown.Token).ConfigureAwait(false);
            return FlushResult.Delivered;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return FlushResult.Retry;
        }
        catch (RpcException ex) when (IsBestEffortDropStatus(ex.StatusCode))
        {
            RunnerLog.Warning<SessionRuntimeTelemetryRelay>(
                $"Dropping session runtime telemetry batch ({entries.Count}) after {ex.StatusCode}: {ex.Status.Detail}");
            return FlushResult.Dropped;
        }
        catch (Exception ex)
        {
            RunnerLog.Error<SessionRuntimeTelemetryRelay>(ex, $"Failed to report session runtime telemetry batch ({entries.Count}): {ex.Message}");
            return FlushResult.Dropped;
        }
    }

    private static bool IsBestEffortDropStatus(StatusCode statusCode)
        => statusCode is StatusCode.DeadlineExceeded
            or StatusCode.Unavailable
            or StatusCode.PermissionDenied
            or StatusCode.Unauthenticated;

    private enum FlushResult
    {
        Delivered,
        Dropped,
        Retry,
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
