namespace Offloadr.Runner.Core;

/// <summary>Original optional-log delivery clock; retries cannot create a new window.</summary>
internal sealed class LogRelayDeadline
{
    private readonly TimeProvider _clock;
    private readonly long _capturedTimestamp;
    private readonly object _gate = new();
    private DateTimeOffset _lastUtc;
    private long _lastTimestamp;
    private bool _retired;

    public LogRelayDeadline(TimeProvider clock)
    {
        _clock = clock;
        CapturedUtc = _lastUtc = clock.GetUtcNow();
        _capturedTimestamp = _lastTimestamp = clock.GetTimestamp();
    }

    public DateTimeOffset CapturedUtc { get; }

    public TimeSpan Remaining
    {
        get
        {
            lock (_gate)
            {
                if (_retired) return TimeSpan.Zero;
                var utc = _clock.GetUtcNow();
                var stamp = _clock.GetTimestamp();
                var wall = utc - CapturedUtc;
                var elapsed = _clock.GetElapsedTime(_capturedTimestamp, stamp);
                if (utc < _lastUtc || stamp < _lastTimestamp || wall >= LogRelayBounds.MaximumQueuedAge || elapsed >= LogRelayBounds.MaximumQueuedAge)
                {
                    _retired = true;
                    return TimeSpan.Zero;
                }
                _lastUtc = utc;
                _lastTimestamp = stamp;
                return LogRelayBounds.MaximumQueuedAge - (wall > elapsed ? wall : elapsed);
            }
        }
    }
}
