using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hardware;
using Offloadr.Runner.Supervisor.LaunchPlans;

namespace Offloadr.Runner.Supervisor;

public enum SupervisorState
{
    Stopped,
    Checking,
    ResolvingPlan,
    Pulling,
    Starting,
    Running,

    /// <summary>Running, but a different image or setting is waiting for the current session to end.</summary>
    RestartPending,

    /// <summary>The runner exited unexpectedly and will be started again after a backoff.</summary>
    WaitingToRestart,
    Stopping,
    Faulted,
}

/// <summary>Immutable snapshot of everything the UI shows. Replaced as a whole on every change.</summary>
public sealed record SupervisorView(
    SupervisorState State,
    string Message,
    ContainerEngineInfo? Engine,
    HostGpuReport? HostGpus,
    GpuAccessCheck? GpuAccess,
    LaunchPlan? Plan,
    LaunchTemplate? ActiveTemplate,
    ImageInfo? Image,
    RunnerStatusSnapshot? RunnerStatus,
    int Restarts,
    DateTimeOffset UpdatedUtc)
{
    public static SupervisorView Initial(DateTimeOffset now)
        => new(SupervisorState.Stopped, "The runner is stopped.", null, null, null, null, null, null, null, 0, now);

    /// <summary>True while a runner container is wanted: starting, running or about to restart.</summary>
    public bool IsActive => State is SupervisorState.Checking or SupervisorState.ResolvingPlan or SupervisorState.Pulling
        or SupervisorState.Starting or SupervisorState.Running or SupervisorState.RestartPending or SupervisorState.WaitingToRestart;

    /// <summary>Short name of the state for headings, tooltips and menus.</summary>
    public string StateTitle => State switch
    {
        SupervisorState.Stopped => "Stopped",
        SupervisorState.Checking => "Checking…",
        SupervisorState.ResolvingPlan => "Finding the image…",
        SupervisorState.Pulling => "Downloading the image…",
        SupervisorState.Starting => "Starting…",
        SupervisorState.Running => "Running",
        SupervisorState.RestartPending => "Running, restart pending",
        SupervisorState.WaitingToRestart => "Restarting soon",
        SupervisorState.Stopping => "Stopping…",
        SupervisorState.Faulted => "Needs attention",
        _ => State.ToString(),
    };

    /// <summary>True when the runner reports an editor session; stopping now would interrupt someone's work.</summary>
    public bool HasActiveSession => !string.IsNullOrEmpty(RunnerStatus?.ActiveSessionId);
}

/// <summary>Bounded, thread-safe buffer of recent log lines.</summary>
internal sealed class LogBuffer(int capacity)
{
    private readonly Queue<string> _lines = new();

    public void Add(string line)
    {
        lock (_lines)
        {
            _lines.Enqueue(line);
            while (_lines.Count > capacity)
            {
                _lines.Dequeue();
            }
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_lines)
        {
            return [.. _lines];
        }
    }
}
