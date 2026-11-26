using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hardware;
using Offloadr.Runner.Supervisor.Launcher;
using Offloadr.Runner.Supervisor.LaunchPlans;

namespace Offloadr.Runner.Supervisor;

public enum SupervisorState
{
    /// <summary>Not available: the person stopped it, or never started it.</summary>
    Stopped,

    /// <summary>Checking the settings, Docker and the GPU before offering this computer.</summary>
    Checking,

    /// <summary>Offered to Offloadr as a launcher, with no runner container running.</summary>
    Available,

    /// <summary>Downloading a runner image, for a session or for the first GPU check.</summary>
    Pulling,

    /// <summary>Starting the runner container a session needs.</summary>
    Starting,

    /// <summary>A runner container runs: serving a session, or kept warm for the next one.</summary>
    Running,

    /// <summary>Making the computer available failed for now (Docker still starting, say) and is tried again after a backoff.</summary>
    WaitingToRetry,
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
    int UnexpectedExits,
    DateTimeOffset UpdatedUtc)
{
    /// <summary>
    /// Why the runner needs attention (<see cref="SupervisorState.Faulted"/>) or why an automatic
    /// restart is waiting; null in every other state.
    /// </summary>
    public SupervisorFault? Fault { get; init; }

    /// <summary>How far the image download is while <see cref="SupervisorState.Pulling"/>; null otherwise.</summary>
    public PullProgress? PullProgress { get; init; }

    /// <summary>How the app's registration as the runner's launcher stands; null before it first ran.</summary>
    public LauncherLinkState? Launcher { get; init; }

    /// <summary>Why the last session on this computer was cut off, until the next launch or until dismissed.</summary>
    public string? Interruption { get; init; }

    /// <summary>Why the last launch Offloadr asked for could not start, until the next one.</summary>
    public SupervisorFault? LaunchFailure { get; init; }

    /// <summary>The container is released as soon as it is idle (its launch was withdrawn, say): it is not kept warm.</summary>
    public bool ReleaseDue { get; init; }

    /// <summary>The editor a launch is preparing, downloading or starting (ComfyUI, say); null when none is.</summary>
    public string? LaunchingEditor { get; init; }

    public static SupervisorView Initial(DateTimeOffset now)
        => new(SupervisorState.Stopped, "The runner is stopped.", null, null, null, null, null, null, null, 0, now);

    /// <summary>True while this computer is wanted available: being checked, offered, launching or running a container, or about to retry.</summary>
    public bool IsActive => State is SupervisorState.Checking or SupervisorState.Available or SupervisorState.Pulling
        or SupervisorState.Starting or SupervisorState.Running or SupervisorState.WaitingToRetry;

    /// <summary>
    /// The launcher runs but offers no editor and no GPU: what this computer offers failed its checks
    /// (or is being checked), while a runner container still runs or after it has gone.
    /// </summary>
    public bool OfferPaused { get; init; }

    /// <summary>
    /// The runner container that runs was started for a launch Offloadr still names, as far as is known: no
    /// executor has matched it yet. A launcher that starts anew meanwhile has that launch retired.
    /// </summary>
    public bool LaunchUnmatched { get; init; }

    /// <summary>
    /// True when Offloadr can start sessions here now: its launcher is accepted with a live stream and
    /// offers this computer. A container that keeps running for an earlier runner, or with no launcher,
    /// does not make it ready.
    /// </summary>
    public bool IsReady => State is SupervisorState.Running or SupervisorState.Available &&
                           !OfferPaused &&
                           Launcher is { Connection: LauncherConnection.Online, StreamLive: true };

    /// <summary>Short name of the state for headings, tooltips and menus.</summary>
    public string StateTitle => State switch
    {
        SupervisorState.Stopped => "Stopped",
        SupervisorState.Checking => "Checking…",
        SupervisorState.Available => Launcher?.Connection switch
        {
            LauncherConnection.Online when OfferPaused => "Checking…",
            LauncherConnection.Online when Launcher.StreamLive => "Ready for sessions",
            LauncherConnection.Refused or LauncherConnection.Unreachable => "Not connected to Offloadr",
            _ => "Connecting…",
        },
        SupervisorState.Pulling => LaunchingEditor is { } downloading ? $"Downloading {downloading}…" : "Downloading the image…",
        SupervisorState.Starting => (LaunchingEditor ?? EditorName(ActiveTemplate)) is { } starting ? $"Starting {starting}…" : "Starting…",
        SupervisorState.Running when HasActiveSession => EditorName(ActiveTemplate) is { } serving ? $"{serving} session running" : "Session running",
        SupervisorState.Running => IsReady ? "Ready for sessions" : "Running",
        SupervisorState.WaitingToRetry => "Trying again soon",
        SupervisorState.Stopping => "Stopping…",
        SupervisorState.Faulted when Fault?.Kind == FaultKind.AppOutdated => "Update needed",
        SupervisorState.Faulted => "Needs attention",
        _ => State.ToString(),
    };

    /// <summary>
    /// A container kept warm after its session (or before its first), with Offloadr able to start sessions
    /// here: the next session with that editor starts quicker.
    /// </summary>
    public bool IsWarm => State == SupervisorState.Running && IsReady && !HasActiveSession && !LaunchUnmatched && !ReleaseDue && ActiveTemplate is not null;

    /// <summary>The short name of an editor (ComfyUI, Forge Neo), for titles and menus; null for none.</summary>
    public static string? EditorName(LaunchTemplate? template) => template switch
    {
        null => null,
        { RuntimeKind: Settings.RunnerSettings.ComfyUiRuntimeKind } => "ComfyUI",
        { RuntimeKind: Settings.RunnerSettings.ForgeNeoRuntimeKind } => "Forge Neo",
        _ => template.Label.Split(" - ", 2)[0],
    };

    /// <summary>True when the runner reports an editor session; stopping now would interrupt someone's work.</summary>
    public bool HasActiveSession => !string.IsNullOrEmpty(RunnerStatus?.ActiveSessionId);

    /// <summary>
    /// A version to show in a narrow column: one that ends in a full commit hash, SHA-1 or SHA-256
    /// (<c>0.1.0+88e11e6b9771eca61addf03c73976fffb4c6e745</c>), keeps its first seven characters, as
    /// git shortens it. Anything else, such as a build date, is shown as it is.
    /// </summary>
    public static string? ShortVersion(string? version)
    {
        var plus = version?.IndexOf('+') ?? -1;
        if (plus < 0)
        {
            return version;
        }

        var hash = version![(plus + 1)..];
        return hash.Length is 40 or 64 && hash.All(Uri.IsHexDigit) ? version[..(plus + 1 + ShortHashLength)] : version;
    }

    private const int ShortHashLength = 7;
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
