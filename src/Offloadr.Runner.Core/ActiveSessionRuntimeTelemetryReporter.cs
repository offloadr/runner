using Offloadr.Runner.V1;

namespace Offloadr.Runner.Core;

internal sealed class ActiveSessionRuntimeTelemetryReporter
{
    private readonly Func<string> _getActiveSessionId;
    private readonly Func<RunnerRuntimeTelemetrySnapshot?> _captureSnapshot;
    private readonly Action<string, RunnerRuntimeTelemetrySnapshot> _enqueueSnapshot;
    private readonly TimeSpan _interval;

    public ActiveSessionRuntimeTelemetryReporter(
        Func<string> getActiveSessionId,
        Func<RunnerRuntimeTelemetrySnapshot?> captureSnapshot,
        Action<string, RunnerRuntimeTelemetrySnapshot> enqueueSnapshot,
        TimeSpan interval)
    {
        _getActiveSessionId = getActiveSessionId ?? throw new ArgumentNullException(nameof(getActiveSessionId));
        _captureSnapshot = captureSnapshot ?? throw new ArgumentNullException(nameof(captureSnapshot));
        _enqueueSnapshot = enqueueSnapshot ?? throw new ArgumentNullException(nameof(enqueueSnapshot));
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
                    var snapshot = _captureSnapshot();
                    if (snapshot is not null)
                    {
                        _enqueueSnapshot(sessionId, snapshot);
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
}
