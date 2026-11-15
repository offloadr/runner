using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor.Hosting;

/// <summary>Keeps the computer from sleeping, or lets it sleep again: a power request on Windows.</summary>
public interface IKeepAwake
{
    void Set(bool awake);
}

/// <summary>
/// Keeps the computer awake while a session runs on its GPU, when Settings allow it (the default): sleep
/// would cut the session off. The display may still turn off, and the computer sleeps again once the
/// session ends. Only changes reach the power request.
/// </summary>
/// <remarks>
/// The supervisor and the settings store publish their current value before they raise Changed, on
/// whichever thread made the change. <see cref="Refresh"/> reads both inside its lock rather than taking
/// them from the notification, so a refresh delayed behind another can never apply an older value over
/// a newer one: whichever runs last reads the latest of both.
/// </remarks>
public sealed class SessionKeepAwake(IKeepAwake keepAwake, Func<SupervisorView> view, Func<RunnerSettings> settings) : IDisposable
{
    private readonly object _gate = new();
    private bool _awake;
    private bool _disposed;

    public static bool ShouldStayAwake(SupervisorView view, RunnerSettings settings)
        => settings.KeepAwakeDuringSessions && view.HasActiveSession;

    /// <summary>Reads the current view and settings and keeps awake, or lets the computer sleep, to match.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var awake = ShouldStayAwake(view(), settings());
            if (awake == _awake)
            {
                return;
            }

            keepAwake.Set(awake);
            _awake = awake;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_awake)
            {
                keepAwake.Set(false);
                _awake = false;
            }

            _disposed = true;
        }
    }
}
