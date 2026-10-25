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
    private readonly CancellationTokenSource _admission = new();
    private readonly CancellationTokenRegistration _admissionRegistration;
    private readonly object _decisionGate = new();
    private readonly TaskCompletionSource<RuntimeActivityDisposition> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _firstObserved = -1;
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
        _admissionRegistration = _admission.Token.Register(() => Decide(RuntimeActivityDisposition.Expired));
        _completion = DeliverAsync();
    }

    /// <summary>The finite admission decision; does not imply the original transport has ended.</summary>
    public Task<RuntimeActivityDisposition> Decision => _decision.Task;

    /// <summary>Completes only after the original delivery task has ended.</summary>
    public Task<RuntimeActivityDisposition> Completion => _completion;

    // Signal at native observation, before any optional queue, log or parser.
    // The monotonic deadline starts here and is never renewed by a retry.
    public void Signal()
    {
        lock (_decisionGate)
        {
            if (_stopping.IsCancellationRequested || _decision.Task.IsCompleted || _firstObserved >= 0) return;
            _firstObserved = Stopwatch.GetTimestamp();
            _admission.CancelAfter(_deliveryBudget);
            _observed.TrySetResult(_firstObserved);
        }
    }

    private RuntimeActivityDisposition Decide(RuntimeActivityDisposition disposition)
    {
        lock (_decisionGate)
        {
            if (_decision.Task.IsCompleted) return _decision.Task.Result;
            if (_stopping.IsCancellationRequested) disposition = RuntimeActivityDisposition.Cancelled;
            else if (_firstObserved >= 0 && Stopwatch.GetElapsedTime(_firstObserved) >= _deliveryBudget)
                disposition = RuntimeActivityDisposition.Expired;
            _decision.TrySetResult(disposition);
            if (disposition != RuntimeActivityDisposition.Expired)
                _admission.CancelAfter(Timeout.InfiniteTimeSpan);
            return disposition;
        }
    }

    private async Task<RuntimeActivityDisposition> DeliverAsync()
    {
        try
        {
            var observedAt = await _observed.Task.WaitAsync(_stopping.Token).ConfigureAwait(false);
            var remaining = _deliveryBudget - Stopwatch.GetElapsedTime(observedAt);
            if (remaining <= TimeSpan.Zero) return Decide(RuntimeActivityDisposition.Expired);
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, _admission.Token);
            var delay = _retryDelay;
            while (true)
            {
                if (admission.IsCancellationRequested)
                    return Decide(_stopping.IsCancellationRequested ? RuntimeActivityDisposition.Cancelled : RuntimeActivityDisposition.Expired);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
                attempt.CancelAfter(_attemptTimeout);
                try
                {
                    var outcome = await _sink.ReportAsync(_observation, attempt.Token).ConfigureAwait(false);
                    if (_stopping.IsCancellationRequested) return Decide(RuntimeActivityDisposition.Cancelled);
                    if (admission.IsCancellationRequested) return Decide(RuntimeActivityDisposition.Expired);
                    switch (outcome)
                    {
                        case RuntimeActivityOutcome.Applied:
                        case RuntimeActivityOutcome.AlreadyAppliedExactly:
                            return Decide(RuntimeActivityDisposition.Accepted);
                        case RuntimeActivityOutcome.ObsoleteOrPreempted:
                            return Decide(RuntimeActivityDisposition.Retired);
                        case RuntimeActivityOutcome.NotReady:
                            break;
                        default:
                            return Decide(RuntimeActivityDisposition.Rejected);
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
                    return Decide(_stopping.IsCancellationRequested ? RuntimeActivityDisposition.Cancelled : RuntimeActivityDisposition.Rejected);
                }

                await Task.Delay(delay, admission.Token).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(MaximumRetryDelay.TotalMilliseconds, delay.TotalMilliseconds * 2));
            }
        }
        catch (OperationCanceledException)
        {
            return Decide(_stopping.IsCancellationRequested ? RuntimeActivityDisposition.Cancelled : RuntimeActivityDisposition.Expired);
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
            var cancellation = _stopping.CancelAsync();
            Decide(RuntimeActivityDisposition.Cancelled);
            await cancellation.ConfigureAwait(false);
        }
        finally
        {
            try { await _completion.ConfigureAwait(false); }
            finally
            {
                _admissionRegistration.Dispose();
                _admission.Dispose();
                _stopping.Dispose();
            }
        }
    }
}
