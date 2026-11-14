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
public sealed class SessionKeepAwake(IKeepAwake keepAwake) : IDisposable
{
    private readonly object _gate = new();
    private bool _awake;
    private bool _disposed;

    public static bool ShouldStayAwake(SupervisorView view, RunnerSettings settings)
        => settings.KeepAwakeDuringSessions && view.HasActiveSession;

    public void Apply(SupervisorView view, RunnerSettings settings) => Apply(ShouldStayAwake(view, settings));

    public void Apply(bool awake)
    {
        lock (_gate)
        {
            if (_disposed || awake == _awake)
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
