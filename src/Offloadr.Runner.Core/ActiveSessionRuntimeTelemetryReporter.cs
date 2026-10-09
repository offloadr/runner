using Offloadr.Runner.V1;

namespace Offloadr.Runner.Core;

internal sealed class ActiveSessionRuntimeTelemetryReporter
{
    private readonly Func<string> _getActiveSessionId;
    private readonly Func<RunnerRuntimeTelemetrySnapshot?> _captureSnapshot;
    private readonly Action<string, RunnerRuntimeTelemetrySnapshot, RuntimeIdentity?> _enqueueSnapshot;
    private readonly Func<string, RuntimeIdentity?> _getRuntimeIdentity;
    private readonly TimeSpan _interval;

    public ActiveSessionRuntimeTelemetryReporter(
        Func<string> getActiveSessionId,
        Func<RunnerRuntimeTelemetrySnapshot?> captureSnapshot,
        Action<string, RunnerRuntimeTelemetrySnapshot, RuntimeIdentity?> enqueueSnapshot,
        TimeSpan interval,
        Func<string, RuntimeIdentity?>? getRuntimeIdentity = null)
    {
        _getActiveSessionId = getActiveSessionId ?? throw new ArgumentNullException(nameof(getActiveSessionId));
        _captureSnapshot = captureSnapshot ?? throw new ArgumentNullException(nameof(captureSnapshot));
        _enqueueSnapshot = enqueueSnapshot ?? throw new ArgumentNullException(nameof(enqueueSnapshot));
        _getRuntimeIdentity = getRuntimeIdentity ?? (static _ => null);
        _interval = interval < TimeSpan.Zero ? TimeSpan.Zero : interval;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_interval <= TimeSpan.Zero)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var sessionId = _getActiveSessionId();
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                try
                {
                    // Sampling can take seconds; a sample belongs to the runtime that was current
                    // when it started, and is dropped if that runtime was replaced meanwhile.
                    var runtime = _getRuntimeIdentity(sessionId);
                    var snapshot = _captureSnapshot();
                    if (snapshot is not null && IsSameRuntime(runtime, _getRuntimeIdentity(sessionId)))
                    {
                        _enqueueSnapshot(sessionId, snapshot, runtime);
                    }
                }
                catch (Exception ex)
                {
                    RunnerLog.Warning<ActiveSessionRuntimeTelemetryReporter>($"Failed sampling runtime telemetry for session '{sessionId}': {ex.Message}");
                }
            }

            try
            {
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static bool IsSameRuntime(RuntimeIdentity? before, RuntimeIdentity? after)
        => before is { } first ? after is { } second && first.SameRuntime(second) : after is null;
}
