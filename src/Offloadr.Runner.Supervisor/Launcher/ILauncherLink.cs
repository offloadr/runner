using Offloadr.Runner.Supervisor.Settings;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Supervisor.Launcher;

/// <summary>How the desktop app's launcher registration stands with Offloadr.</summary>
public enum LauncherConnection
{
    /// <summary>Not running: not started yet, or stopped.</summary>
    Offline,

    /// <summary>Running, but no registration has been answered yet.</summary>
    Connecting,

    /// <summary>The last registration was accepted.</summary>
    Online,

    /// <summary>Offloadr answered no, with a message; the link keeps asking.</summary>
    Refused,

    /// <summary>Offloadr could not be reached; the link keeps asking.</summary>
    Unreachable,

    /// <summary>This app and Offloadr no longer share a protocol. Final: update the app.</summary>
    Outdated,

    /// <summary>Another launcher took over this runner (the newest one wins). Final until started again.</summary>
    Superseded,
}

/// <summary>
/// A launch Offloadr named (<c>LaunchRunnerCommand</c>), as an immutable value. It belongs to the run
/// of the link whose stream named it: report on it with the value <see cref="LauncherLinkState.Launch"/> gave.
/// <see cref="OfferRevision"/> is the <see cref="LauncherProfile.OfferRevision"/> Offloadr checked it against.
/// </summary>
public sealed record LaunchRequest(string LaunchId, ulong Sequence, string EditorTemplate, string RunnerImage, ulong OfferRevision = 0)
{
    /// <summary>The run of the link that received it; 0 for one made by hand, which no run accepts.</summary>
    internal long Run { get; init; }

    internal static LaunchRequest From(LaunchRunnerCommand command, long run)
        => new(command.LaunchId.Trim(), command.LaunchSequence, command.EditorTemplate.Trim(), command.RunnerImage.Trim(), command.OfferRevision) { Run = run };
}

/// <summary>
/// What the launcher link knows right now. <see cref="Launch"/> is the launch the latest
/// <c>LauncherState</c> named, kept while the stream is down so a download can go on; a container
/// may only be started while <see cref="ILauncherLink.MayStart"/> says so.
/// </summary>
public sealed record LauncherLinkState(
    LauncherConnection Connection,
    string? Message,
    bool StreamLive,
    LaunchRequest? Launch)
{
    public static LauncherLinkState Offline { get; } = new(LauncherConnection.Offline, null, false, null);

    /// <summary>True when the link has stopped for good and will not register again by itself.</summary>
    public bool IsFinal => Connection is LauncherConnection.Outdated or LauncherConnection.Superseded;
}

/// <summary>
/// What a launcher heartbeat says about this computer: its GPU and the editors it can start, and the
/// revision of that offer, which rises whenever the GPU or container engine it launches on changes.
/// </summary>
public sealed record LauncherProfile(RunnerResources Resources, IReadOnlyList<string> EditorTemplates, ulong OfferRevision = 0);

/// <summary>One progress or outcome report for a launch.</summary>
public sealed record LaunchReport(
    LaunchRequest Launch,
    RunnerLaunchPhase Phase,
    string? Message = null,
    ulong DownloadedBytes = 0,
    ulong TotalBytes = 0)
{
    public string LaunchId => Launch.LaunchId;

    public bool IsTerminal => Phase is RunnerLaunchPhase.Started or RunnerLaunchPhase.Failed
        or RunnerLaunchPhase.Superseded or RunnerLaunchPhase.Cancelled;
}

/// <param name="Released">Offloadr granted the release: the container may be stopped.</param>
/// <param name="Reached">False when Offloadr could not be asked; keep the container and ask again.</param>
/// <param name="Sent">
/// False when the request was not sent at all (the launch it was for was no longer named, or no launcher
/// runs to send it): nothing can have been granted.
/// </param>
public sealed record ReleaseResult(bool Released, bool Reached, string Message, bool Sent = true)
{
    /// <summary>
    /// False when Offloadr refused the call itself (a gRPC error) rather than its release handler
    /// answering: not granted, but nothing was decided about the container either.
    /// </summary>
    public bool Answered { get; init; } = true;
}

/// <summary>
/// The desktop app's registration as a launcher (see "Launchers" in <c>docs/contracts-sync.md</c>):
/// heartbeats, the <c>LauncherStream</c> that names the launch to work on, launch reports and
/// releases. It starts nothing itself.
/// </summary>
public interface ILauncherLink
{
    LauncherLinkState State { get; }

    /// <summary>
    /// Raised after <see cref="State"/> changes, on the thread that changed it. A signal only: read
    /// <see cref="State"/> again, since signals from different threads may arrive out of order.
    /// </summary>
    event EventHandler? StateChanged;

    /// <summary>
    /// Registers with the key and address in <paramref name="settings"/> and follows the launcher
    /// stream until <paramref name="cancellationToken"/>, or until Offloadr refuses this launcher for
    /// good (<see cref="LauncherLinkState.IsFinal"/>). <paramref name="profile"/> is read for every heartbeat.
    /// A run after a takeover (<see cref="LauncherConnection.Superseded"/>) registers as a new
    /// instance, which takes the runner back. A launcher that was not running when another took over
    /// learns it from its first heartbeat, so it takes one more run to come back.
    /// </summary>
    Task RunAsync(RunnerSettings settings, Func<LauncherProfile> profile, CancellationToken cancellationToken);

    /// <summary>
    /// True only while the run goes on, the stream is live (a message within the keepalive timeout),
    /// the state it sent last names exactly <paramref name="launch"/> (as this run received it) and no
    /// outcome was reported for it yet. Ask right before starting the container.
    /// </summary>
    /// <param name="streamDrops">
    /// The <see cref="StreamDrops"/> at which the caller's checks began: false once the stream dropped since,
    /// asked in one step with the rest (it may have come back, with the same launch named).
    /// </param>
    bool MayStart(LaunchRequest launch, long? streamDrops = null);

    /// <summary>
    /// How often the stream went from live to not live, in any run. What was checked at one count (the
    /// GPUs a container gets, say) was checked with the stream live throughout only while the count is
    /// still the same: a drop and a reconnect in between, which <see cref="MayStart"/> alone does not
    /// show afterwards, raise it.
    /// </summary>
    long StreamDrops { get; }

    /// <summary>
    /// The highest <see cref="LauncherProfile.OfferRevision"/> of a heartbeat Offloadr accepted in this run;
    /// 0 before any. Offloadr retires launches stamped lower once it accepts one: a launch chosen for an
    /// older offer is reported FAILED only after that, as a FAILED sent first would fail its allocation.
    /// </summary>
    ulong AcceptedOfferRevision { get; }

    /// <summary>
    /// Runs <paramref name="dispatch"/> only if <see cref="MayStart"/> holds, and with nothing able to
    /// change the link's state until it returns: the check and the start are one step. Keep
    /// <paramref name="dispatch"/> short and synchronous (spawning `docker run`, not waiting for it), and
    /// never call back into the link from it. False when the launch may not start now.
    /// </summary>
    /// <param name="streamDrops">
    /// The <see cref="StreamDrops"/> the caller's checks were made at: with another count by now (the
    /// stream dropped and came back, with the same launch named), nothing is started, and false returned.
    /// </param>
    bool TryStart(LaunchRequest launch, Action dispatch, long? streamDrops = null);



    /// <summary>
    /// Reports a launch's progress, for a launch this run's stream named (a report on a launch an
    /// earlier run received is dropped, whatever its id). A
    /// progress report is repeated while the launch is in flight; a terminal one is resent until
    /// Offloadr accepts it, refuses it for good or the run ends, even after
    /// <paramref name="cancellationToken"/> stops the wait for it. Reports go out one at a time, so a
    /// repeat never follows the terminal report. Never throws for a failed send.
    /// </summary>
    Task ReportAsync(LaunchReport report, CancellationToken cancellationToken);

    /// <summary>
    /// The Offloadr address and runner this run launches for, while it runs. Store it with each
    /// container started for a launch and pass it to <see cref="ReleaseAsync"/>.
    /// </summary>
    string? LaunchTarget { get; }

    /// <summary>
    /// The terminal report submitted for <paramref name="launchId"/>, kept while the link's address,
    /// runner and instance stay the same. When Offloadr names that launch again, do not start it:
    /// the link sends an outcome Offloadr has not answered again by itself.
    /// </summary>
    LaunchReport? OutcomeOf(string launchId);

    /// <summary>
    /// Asks once whether the container started for <paramref name="launchId"/> may be stopped. Only
    /// the Offloadr that asked for it can say: a <paramref name="launchTarget"/> other than the
    /// current <see cref="LaunchTarget"/> is not asked, and comes back not reached. With
    /// <paramref name="onlyFor"/>, a release that makes room for that launch, it is sent only if
    /// <see cref="MayStart"/> holds for it, checked as one step with the sending (as for
    /// <see cref="TryStart"/>); otherwise it comes back not sent. Nor is it sent once
    /// <paramref name="sendUnless"/> is cancelled (the caller no longer needs the room), checked in the
    /// same step; a request already sent is still answered. With <paramref name="containerExited"/>
    /// it reports a container that is gone (it exited, or was stopped without a granted release):
    /// Offloadr then ends its executor's session instead of checking that it is idle. Such a report needs
    /// no registered launcher: it goes as the launcher instance that last ran for that target, or, with
    /// <paramref name="reportAs"/> naming that target, as this link's instance for it. With
    /// <paramref name="reportAs"/>, a release that makes room for no launch (the person stops a container
    /// while the launcher does not run) is asked the same way, registering nothing.
    /// </summary>
    /// <param name="streamDrops">
    /// With <paramref name="onlyFor"/>: the <see cref="StreamDrops"/> the caller's checks were made at.
    /// With another count by now, the release is not sent.
    /// </param>
    Task<ReleaseResult> ReleaseAsync(string launchId, string? executorInstanceId, string launchTarget, CancellationToken cancellationToken,
        LaunchRequest? onlyFor = null, CancellationToken sendUnless = default, bool containerExited = false, RunnerSettings? reportAs = null,
        long? streamDrops = null);
}
