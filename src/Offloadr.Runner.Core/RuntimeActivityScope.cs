using System.Diagnostics;
using Grpc.Core;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Core;

// Values only: retry ownership must not retain an output line, a request body,
// a mutable sealing message or a later registry entry for the same session.
internal readonly record struct RuntimeActivityObservation(
    string SessionId,
    RuntimeIdentity Runtime,
    bool MemoryWorkspace,
    bool EncryptedUploads,
    bool EncryptedReadsRequired)
{
    public static RuntimeActivityObservation Capture(string sessionId, RuntimeIdentity runtime, SessionSealingStatus profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(profile);
        if (!runtime.IsValid || runtime.LifecycleGeneration > long.MaxValue || runtime.RuntimeEpoch > long.MaxValue ||
            !Guid.TryParse(runtime.RuntimeInstanceId, out var instance) || instance == Guid.Empty)
            throw new ArgumentException("An exact runtime identity is required.", nameof(runtime));
        return new(sessionId, runtime.Normalized(), profile.MemoryWorkspace, profile.EncryptedUploads, profile.EncryptedReadsRequired);
    }
}

internal interface IRuntimeActivitySink
{
    Task<RuntimeActivityOutcome> ReportAsync(RuntimeActivityObservation observation, CancellationToken cancellationToken);
}

internal enum RuntimeActivityDisposition
{
    Accepted,
    Retired,
    Rejected,
    Expired,
    Cancelled
}

/// <summary>
/// Owns the first-activity latch and its original delivery task. The process
/// lifetime consumes the disposition and owns termination and cleanup.
/// </summary>
internal sealed class RuntimeActivityScope : IAsyncDisposable
{
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(2);
    private readonly RuntimeActivityObservation _observation;
    private readonly IRuntimeActivitySink _sink;
    private readonly TimeSpan _deliveryBudget;
    private readonly TimeSpan _attemptTimeout;
    private readonly TimeSpan _retryDelay;
    private readonly TaskCompletionSource<long> _observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _disposeGate = new();
    private readonly Task<RuntimeActivityDisposition> _completion;
    private Task? _disposal;

    public RuntimeActivityScope(
        RuntimeActivityObservation observation,
        IRuntimeActivitySink sink,
        TimeSpan deliveryBudget,
        TimeSpan? attemptTimeout = null,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (deliveryBudget <= TimeSpan.Zero || deliveryBudget > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(deliveryBudget));
        _attemptTimeout = attemptTimeout ?? AttemptTimeout;
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(250);
        if (_attemptTimeout <= TimeSpan.Zero || _attemptTimeout > AttemptTimeout)
            throw new ArgumentOutOfRangeException(nameof(attemptTimeout));
        if (_retryDelay <= TimeSpan.Zero || _retryDelay > MaximumRetryDelay)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        _observation = observation;
        _sink = sink;
        _deliveryBudget = deliveryBudget;
        _completion = DeliverAsync();
    }

    public Task<RuntimeActivityDisposition> Completion => _completion;

    // Signal at native observation, before any optional queue, log or parser.
    // The monotonic deadline starts here and is never renewed by a retry.
    public void Signal()
    {
        if (!_stopping.IsCancellationRequested)
            _observed.TrySetResult(Stopwatch.GetTimestamp());
    }

    private async Task<RuntimeActivityDisposition> DeliverAsync()
    {
        try
        {
            var observedAt = await _observed.Task.WaitAsync(_stopping.Token).ConfigureAwait(false);
            var remaining = _deliveryBudget - Stopwatch.GetElapsedTime(observedAt);
            if (remaining <= TimeSpan.Zero) return RuntimeActivityDisposition.Expired;
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            admission.CancelAfter(remaining);
            var delay = _retryDelay;
            while (true)
            {
                if (admission.IsCancellationRequested)
                    return _stopping.IsCancellationRequested ? RuntimeActivityDisposition.Cancelled : RuntimeActivityDisposition.Expired;
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
                attempt.CancelAfter(_attemptTimeout);
                try
                {
                    var outcome = await _sink.ReportAsync(_observation, attempt.Token).ConfigureAwait(false);
                    if (_stopping.IsCancellationRequested) return RuntimeActivityDisposition.Cancelled;
                    if (admission.IsCancellationRequested) return RuntimeActivityDisposition.Expired;
                    switch (outcome)
                    {
                        case RuntimeActivityOutcome.Applied:
                        case RuntimeActivityOutcome.AlreadyAppliedExactly:
                            return RuntimeActivityDisposition.Accepted;
                        case RuntimeActivityOutcome.ObsoleteOrPreempted:
                            return RuntimeActivityDisposition.Retired;
                        case RuntimeActivityOutcome.NotReady:
                            break;
                        default:
                            return RuntimeActivityDisposition.Rejected;
                    }
                }
                catch (OperationCanceledException) when (attempt.IsCancellationRequested)
                {
                    // A per-attempt deadline retries; the original call has ended.
                }
                catch (RpcException exception) when (Transient(exception.StatusCode))
                {
                    // Never retain or log Status.Detail, trailers or request bodies.
                }
                catch (Exception exception)
                {
                    RunnerLog.Warning<RuntimeActivityScope>($"Runtime activity rejected; errorClass={exception.GetType().Name}.");
                    return _stopping.IsCancellationRequested ? RuntimeActivityDisposition.Cancelled : RuntimeActivityDisposition.Rejected;
                }

                await Task.Delay(delay, admission.Token).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(MaximumRetryDelay.TotalMilliseconds, delay.TotalMilliseconds * 2));
            }
        }
        catch (OperationCanceledException)
        {
            return _stopping.IsCancellationRequested ? RuntimeActivityDisposition.Cancelled : RuntimeActivityDisposition.Expired;
        }
    }

    private static bool Transient(StatusCode code)
        => code is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.ResourceExhausted
            or StatusCode.Cancelled or StatusCode.Unknown or StatusCode.Internal;

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new(_disposal ??= CancelAndJoinAsync());
    }

    private async Task CancelAndJoinAsync()
    {
        try
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            try { await _completion.ConfigureAwait(false); }
            finally { _stopping.Dispose(); }
        }
    }
}
