using Microsoft.Extensions.Logging;
using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hardware;
using Offloadr.Runner.Supervisor.Launcher;
using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Settings;
using Offloadr.Runner.V1;
using Selected = Offloadr.Runner.Supervisor.Containers.ContainerEngineSelection.Selected;

namespace Offloadr.Runner.Supervisor;

/// <content>
/// What the loop knows, and the small decisions every procedure shares: what the launcher offers,
/// how availability ends or pauses, and the launcher link's run. All of it is the loop's alone.
/// </content>
public sealed partial class RunnerSupervisor
{
    // The supervised container

    private bool _ownsContainer;

    /// <summary>The ID of the container this supervisor launched or adopted.</summary>
    private string? _containerId;

    /// <summary>
    /// The label of the creation that made that container. Until the creation returns the ID (or when a cancelled
    /// one never did), it is what tells this creation's container from one another put under the name.
    /// </summary>
    private string? _attemptId;

    /// <summary>The Offloadr launch the container runs for, and the address and runner that asked; null for one an earlier app version started.</summary>
    private string? _containerLaunchId;
    private string? _containerLaunchTarget;

    /// <summary>The engine the supervised container runs on, from its dispatch (its ID may not be known yet) or its adoption.</summary>
    private Selected? _containerEngine;

    /// <summary>The engine the running container's image is on, for removing that image once a newer one replaced it.</summary>
    private Selected? _runningEngine;
    private string? _activeFingerprint;

    /// <summary>What the runner this supervisor last started or adopted runs, with its exact image.</summary>
    private LaunchTemplate? _runningTemplate;

    /// <summary>The image ID the running container runs.</summary>
    private string? _runningImageId;

    /// <summary>What an adopted runner was started with (its key, for redaction) is still to be read; retried.</summary>
    private bool _adoptedLaunchPending;

    /// <summary>The ID of a container that took the runner's name; it is not adopted.</summary>
    private string? _rejectedContainerId;

    /// <summary>When the container was last seen serving a session (or started), by <see cref="Now"/>.</summary>
    private TimeSpan _idleSince;

    /// <summary>A release Offloadr could not be asked about is asked again from then on.</summary>
    private TimeSpan _releaseRetryAt;

    // The status the agent writes

    /// <summary>Monotonic timestamp of the last status read; null before the first.</summary>
    private long? _statusReadTimestamp;
    private DateTimeOffset? _statusUpdatedUtc;

    /// <summary>Monotonic timestamp of when the agent's status last changed; null until it has.</summary>
    private long? _statusAdvancedTimestamp;

    /// <summary>When the status file was first read for the running container, as a monotonic timestamp.</summary>
    private long? _statusFirstReadTimestamp;

    /// <summary>The last status read named a session (a read that found no status leaves this as it was).</summary>
    private bool _sessionSeen;
    private int _monitorFailures;
    private string? _messageBeforeMonitorFailure;

    // What was checked, and is offered

    /// <summary>
    /// What the GPU check passed for and found. The launcher's heartbeat reads it from its own thread,
    /// so it is one immutable value, replaced whole by the loop.
    /// </summary>
    /// <param name="PassedFor">The engine, Settings value, context and GPU the in-container GPU check passed for last.</param>
    /// <param name="Engine">The exact engine and context whose containers passed the GPU check; launches go there.</param>
    /// <param name="Gpu">The GPU chosen when that check passed (null: every GPU); heartbeats offer only it.</param>
    /// <param name="Inventory">
    /// The GPUs, with names and memory, that containers on the checked engine get, read when this
    /// computer's own list cannot describe them (a remote engine); null otherwise.
    /// </param>
    /// <param name="Output">What that passing check saw inside a container (its GPUs, by UUID).</param>
    /// <param name="OfferNothing">
    /// The launcher runs but offers no editor and no GPU: what this computer would offer is being
    /// checked, or failed its checks, while a container runs that may still need releasing.
    /// </param>
    /// <param name="GpuDisproved">A check that ran found containers not to reach the GPU offered (not just an engine that did not answer).</param>
    private sealed record Checked(
        string? PassedFor = null,
        Selected? Engine = null,
        string? Gpu = null,
        IReadOnlyList<HostGpu>? Inventory = null,
        string? Output = null,
        bool OfferNothing = false,
        bool GpuDisproved = false);

    private volatile Checked _checked = new();

    /// <summary>
    /// What a heartbeat is built from, as one value: what was checked, with the view of the same moment
    /// (this computer's GPUs, the engine's memory, the catalog). Published by the loop whenever it works
    /// out the offer and at the end of every tick, so a heartbeat never mixes two moments.
    /// </summary>
    /// <param name="Held">What a pause keeps offering, with no editors, while the revision is held (<see cref="HoldsRevision"/>).</param>
    private sealed record Offered(Checked Known, SupervisorView View, HeldOffer? Held = null);

    /// <summary>
    /// The hardware last offered to a runner, whole. Kept rather than worked out again while the revision is
    /// held: a list of GPUs that comes back empty for a moment is no reason to retire a launch.
    /// </summary>
    private sealed record HeldOffer(string Target, HostGpuReport Gpus, string Signature, RunnerResources Resources);

    private HeldOffer? _lastOffer;

    /// <summary>
    /// The heartbeat's answer, whole: the hardware offered, the editors, and the revision that names
    /// exactly that hardware, for the runner the launcher registers as. Built on the loop, where the
    /// revision is assigned, so a heartbeat never stamps one moment's hardware with another's revision.
    /// </summary>
    private sealed record Published(string Target, LauncherProfile Profile);

    private volatile Published? _published;

    private Offered Publish()
    {
        // Only for a runner there is: with no launcher and no key in Settings there is no runner to
        // offer to, and nothing to keep a revision for.
        var target = OfferTarget();
        var held = HoldsRevision() && _lastOffer is { } last && string.Equals(last.Target, target, StringComparison.Ordinal) ? last : null;
        var offered = new Offered(_checked, View, held);
        if (target is null)
        {
            _published = null;
            return offered;
        }

        var profile = BuildProfile(offered, target);
        _published = new Published(target, profile);
        if (OfferNow(offered) is var (gpus, signature, paused) && !paused)
        {
            _lastOffer = new HeldOffer(target, gpus, signature, profile.Resources);
        }

        return offered;
    }

    /// <summary>
    /// The images, by ID, whose containers were seen to reach the GPU since the engine, the GPU chosen and
    /// this computer's GPUs were last checked: a launch of one is not checked again before its start.
    /// </summary>
    private readonly HashSet<string> _gpuProvedImages = new(StringComparer.Ordinal);

    /// <summary>
    /// What the launcher offers each runner (the engine and GPU checked), with its revision: raised
    /// whenever that changes, never lowered. A launch carries the revision Offloadr checked it against,
    /// so one chosen for hardware no longer offered is refused, with no new launcher instance (which
    /// could take the runner from another computer). Asked from the heartbeat's thread too: guarded by itself.
    /// </summary>
    private readonly Dictionary<string, (string Signature, ulong Revision)> _offers = new(StringComparer.Ordinal);

    /// <summary>The running message a pause replaced, shown again once the computer is offered again.</summary>
    private string? _offeredAgainNote;

    /// <summary>Why availability ended while a container ran; shown once it is gone.</summary>
    private (string Message, FaultKind Kind)? _offlineFault;

    /// <summary>The launcher was stopped while Docker or the GPU failed, with a container still watched; resumed once they pass.</summary>
    private bool _linkSuspended;

    /// <summary>When a suspended launcher's checks are tried again, by <see cref="Now"/>.</summary>
    private TimeSpan _resumeAvailabilityAt;

    /// <summary>When Docker was last found answering, by <see cref="Now"/>.</summary>
    private TimeSpan _engineCheckedAt;
    private TimeSpan? _retryDueAt;

    /// <summary>The Stops asked when the retry at <see cref="_retryDueAt"/> was scheduled: a Stop since then wins.</summary>
    private long _retryStops;
    private int _retryAttempt;

    // The launch plan

    /// <summary>When the plan was last asked for, by <see cref="Now"/>; null before the first time.</summary>
    private TimeSpan? _planRefreshedAt;

    /// <summary>The Offloadr address and key whose launch plan request Offloadr refused for the key.</summary>
    private (string Origin, string Key)? _planRejectedFor;

    /// <summary>The Offloadr address and key the plan in the view was asked with.</summary>
    private (string Origin, string Key)? _planCredentials;
    private readonly Slot _planSlot = new("launch plan");

    // The launcher link

    private readonly Slot _linkSlot = new("launcher link");
    private readonly Slot _deliverySlot = new("outcome delivery");

    /// <summary>One run of the launcher link, from its start until its job ended.</summary>
    private sealed class LinkRun((string Origin, string Key) credentials)
    {
        public (string Origin, string Key) Credentials { get; } = credentials;

        /// <summary>The supervisor ended it: its end is not Offloadr refusing this launcher.</summary>
        public bool Cancelled { get; set; }

        /// <summary>Offloadr accepted a registration of this run.</summary>
        public bool Accepted { get; set; }
    }

    /// <summary>The run in the link's slot; null when none runs, or the one there was cancelled.</summary>
    private LinkRun? _linkRun;

    /// <summary>A run that ended by itself, still to be dealt with.</summary>
    private LinkRun? _linkEnded;

    /// <summary>The address and key the launcher runs for; null once its run was cancelled.</summary>
    private (string Origin, string Key)? LinkCredentials => _linkRun?.Credentials;

    /// <summary>True while a run of the link has not ended, also one cancelled a moment ago.</summary>
    private bool LinkRunning => _linkSlot.Busy;

    /// <summary>
    /// An outcome the launcher still owes Offloadr while this computer stops being offered: the run
    /// stays (offering nothing) until it is delivered or the deadline passes, then ends, unless it is
    /// wanted again by then.
    /// </summary>
    private sealed class Owed(LaunchReport outcome, LinkRun run, TimeSpan deadline, Func<bool> stillToEnd)
    {
        public LaunchReport Outcome { get; } = outcome;

        public LinkRun Run { get; } = run;

        public TimeSpan Deadline { get; } = deadline;

        public Func<bool> StillToEnd { get; } = stillToEnd;

        public Fact<bool> Delivered { get; } = new();
    }

    private Owed? _owed;

    // Small readings

    /// <summary>
    /// The runner this supervisor launched or adopted, by ID once known, for reading its status,
    /// environment and log: a container that takes the name after a check is never read as the runner.
    /// </summary>
    private string OurContainer => _containerId ?? _options.ContainerName;

    private bool IsOurContainer(ContainerInfo container) => SupervisorEffects.IsContainer(container, _containerId, _attemptId);

    /// <summary>A container monitoring found in place of the runner; it is never adopted as the runner.</summary>
    private bool IsRejectedContainer(ContainerInfo container) => SupervisorEffects.IsRejected(container, _rejectedContainerId);

    private string NameTakenMessage(ContainerInfo? container)
        => container is not null && IsManaged(container) && !IsOwned(container)
            ? $"The runner container '{_options.ContainerName}' belongs to another copy of this app (another user account, or another computer using the same container engine). Stop it there, then start again."
            : $"A container named '{_options.ContainerName}' already exists and was not started by this app. Remove or rename it, then start again.";

    /// <summary>
    /// The exact container that was checked, by ID: the name may pass to another container between
    /// the check and the stop or removal, and that one must not be touched.
    /// </summary>
    private string Target(ContainerInfo container)
        => string.IsNullOrWhiteSpace(container.Id) ? _options.ContainerName : container.Id;

    /// <summary>
    /// True when the running container is one the launcher for <paramref name="settings"/> cannot
    /// release: started by an app version before launchers, or for another address or key.
    /// </summary>
    private bool BlocksEveryLaunch(RunnerSettings settings)
        => _ownsContainer && (_containerLaunchId is null || !string.Equals(_containerLaunchTarget, LaunchTargetFor(settings), StringComparison.Ordinal));

    /// <summary>
    /// True when the agent's status names a session and it still writes that status. A snapshot it
    /// stopped updating (its disk full, say) proves nothing; Offloadr's release decides then.
    /// </summary>
    private bool HasCurrentSession()
    {
        if (!View.HasActiveSession)
        {
            return false;
        }

        var lastChange = _statusAdvancedTimestamp ?? _statusFirstReadTimestamp;
        return lastChange is not { } since || _time.GetElapsedTime(since) <= _options.StatusStaleAfter;
    }

    private TimeSpan PlanRefreshDue()
        => (_planRefreshedAt ?? TimeSpan.Zero) + (View.Plan?.RefreshInterval ?? BuiltInLaunchPlanCatalog.RefreshInterval);

    /// <summary>
    /// With no container running, selects the engine and context the checks passed on while Settings
    /// name that engine, whatever `docker context use` changed since; otherwise the one Settings name.
    /// So launches, image removals and GPU checks reach the daemon the launcher offers.
    /// </summary>
    private void SelectCheckedEngine(RunnerSettings settings)
    {
        var configured = _engineSelection.Resolve(settings.ContainerEngineExecutable);
        if (_checked.Engine is { } known && known.Configured == configured.Configured && known.Executable == configured.Executable)
        {
            _engineSelection.SelectExactly(known);
        }
        else
        {
            _engineSelection.Select(settings.ContainerEngineExecutable);
        }
    }

    // The offer

    /// <summary>True when Settings name an engine or GPU other than the ones the GPU check passed for, or that check is due again.</summary>
    private bool NeedsNewChecks(RunnerSettings settings) => _checked.PassedFor is null || OfferedConfigurationChanged(settings);

    /// <summary>
    /// True when Settings name an engine or GPU other than the ones checked and offered: a launch
    /// Offloadr chose this computer for, on what it offered, is not served on something else.
    /// </summary>
    private bool OfferedConfigurationChanged(RunnerSettings settings)
    {
        if (_checked.Engine is not { } known)
        {
            return true;
        }

        var configured = _engineSelection.Resolve(settings.ContainerEngineExecutable);
        return known.Configured != configured.Configured || known.Executable != configured.Executable ||
               !string.Equals(_checked.Gpu, settings.SelectedGpuUuid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The revision of what the launcher offers <paramref name="target"/> now (<see cref="CurrentOffer"/>):
    /// raised the first time the offer differs from what that revision offered, so a heartbeat and a
    /// launch read against it agree on the hardware.
    /// </summary>
    private ulong OfferRevision(string target, string signature)
    {
        lock (_offers)
        {
            var offer = !_offers.TryGetValue(target, out var known) ? (signature, 1UL)
                : known.Signature == signature ? known
                : (signature, known.Revision + 1);
            _offers[target] = offer;
            return offer.Item2;
        }
    }

    private const string NothingOffered = "nothing";

    /// <summary>
    /// What a launcher heartbeat says: the GPU containers get, and the editors the catalog names. Called
    /// by the link on its own thread: it hands out the answer the loop published whole.
    /// </summary>
    private LauncherProfile LauncherProfile()
    {
        if (OfferTarget() is not { } target)
        {
            return new LauncherProfile(LauncherResources.From(HostGpuReport.None("Not offered now."), null, null), [], 0);
        }

        if (!RunnerWanted || _stopping.IsCancellationRequested || Volatile.Read(ref _intent).Stops > Interlocked.Read(ref _stopsDone))
        {
            // A Stop, or the app closing, offers nothing from the moment it is asked: read here, since the
            // launcher (which stays to release a container) may send a heartbeat before the loop publishes
            // again. The same offer, and revision, the loop then publishes. Nor while a Stop is still
            // being carried out, though Start was pressed since: no launch is taken until it is done, and
            // the checks that follow publish the offer again.
            return new LauncherProfile(LauncherResources.From(HostGpuReport.None("Not offered now."), null, null), [], OfferRevision(target, NothingOffered));
        }

        if (_published is { } published && string.Equals(published.Target, target, StringComparison.Ordinal))
        {
            return published.Profile;
        }

        // A run for another runner than the loop last published for (its first heartbeat, before the
        // loop's next tick): built here once, from what was checked, offering nothing it has not.
        return BuildProfile(new Offered(_checked, View), target);
    }

    /// <summary>The runner the launcher offers to: the one it runs for, or the one the key in Settings names; null with neither.</summary>
    private string? OfferTarget()
    {
        if (_link.LaunchTarget is { } running)
        {
            return running;
        }

        var settings = _settings.Current;
        return settings.HasRunnerKey ? LaunchTargetFor(settings) : null;
    }

    private LauncherProfile BuildProfile(Offered offered, string target)
    {
        var (gpus, signature, paused) = OfferNow(offered);
        var revision = OfferRevision(target, signature);

        // Stopping, or quitting, offers nothing from the moment it is asked: the launcher stays only to
        // release a container. Quitting keeps RunnerWanted, which an update hands on. Nor is anything
        // offered on no GPU it could describe, rather than editors on no GPU.
        if (signature == NothingOffered)
        {
            return new LauncherProfile(LauncherResources.From(HostGpuReport.None("Not offered now."), null, null), [], revision);
        }

        if (paused && offered.Held is { } held)
        {
            // No editor to choose this computer for, on the hardware it offered.
            return new LauncherProfile(held.Resources, [], revision);
        }

        var view = offered.View;
        // The catalog Offloadr gave for this runner: a run for an earlier address or key may still be ending
        // (a release under way) when the one for the new ones arrives, and must not offer its editors.
        var editors = (_catalogs.TryGetValue(target, out var catalog) ? catalog : BuiltInLaunchPlanCatalog.Plan()).Templates
            .Select(template => template.EditorTemplate)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // The GPU that passed the check, not one Settings name since: that one is checked before it is offered.
        return new LauncherProfile(LauncherResources.From(gpus, offered.Known.Gpu, view.Engine?.MemoryBytes), editors, revision);
    }

    /// <summary>
    /// What the launcher offers now and its signature: <see cref="CurrentOffer"/>, or nothing at all while
    /// it offers nothing (paused, stopping, quitting, or no GPU it can describe). Nothing is an offer of its
    /// own, with its own revision: a launch chosen before is then one chosen for an older offer.
    /// </summary>
    private (HostGpuReport Gpus, string Signature, bool Paused) OfferNow() => OfferNow(Publish());

    private (HostGpuReport Gpus, string Signature, bool Paused) OfferNow(Offered offered)
    {
        var offer = CurrentOffer(offered);
        if (!offered.Known.OfferNothing && offer.Gpus.Gpus.Count > 0 && RunnerWanted && !_stopping.IsCancellationRequested)
        {
            return (offer.Gpus, offer.Signature, false);
        }

        // (A Stop, or the app closing, ends the container itself: nothing is held for it.)
        return offered.Held is { } held && RunnerWanted && !_stopping.IsCancellationRequested
            ? (held.Gpus, held.Signature, true)
            : (HostGpuReport.None("Not offered now."), NothingOffered, true);
    }

    /// <summary>
    /// The GPUs the launcher offers now, and what identifies that offer: the engine and context checked,
    /// the GPU chosen, and each GPU offered (a GPU gone after sleep changes it). An engine on another
    /// computer gives its containers that computer's GPUs, not these: then, or when this computer cannot
    /// list its own, the GPUs the checked containers reported are offered.
    /// </summary>
    private (HostGpuReport Gpus, string Signature) CurrentOffer() => CurrentOffer(Publish());

    private (HostGpuReport Gpus, string Signature) CurrentOffer(Offered offered)
    {
        var view = offered.View;
        var known = offered.Known;
        var remote = (known.Engine?.Context ?? _engineSelection.Pin) is { Remote: true };
        // Only the GPU chosen is offered, from whichever list describes it: one gone (after sleep, say)
        // leaves nothing to offer, and the check is made again, whatever other GPUs remain.
        // The containers' own list, once read (this computer's could not describe what they get), else this
        // computer's as far as the containers see it.
        var gpus = known.Inventory is { } inventory
            ? new HostGpuReport(inventory, null)
            : remote
                ? HostGpuReport.None("The container engine runs on another computer.")
                : VisibleToContainers(view.HostGpus ?? HostGpuReport.None("Not checked yet."), known.Output);
        gpus = gpus with { Gpus = OnlyChosen(gpus.Gpus, known.Gpu) };

        var engine = known.Engine;
        var signature = string.Join("#",
            engine is null ? "-" : $"{engine.Configured}|{engine.Executable}|{PinIdentity(engine.Context)}",
            known.Gpu ?? "-",
            string.Join(";", gpus.Gpus.Select(gpu => $"{gpu.Uuid}/{gpu.Name}/{gpu.MemoryMiB}")));
        return (gpus, signature);
    }

    /// <summary>True while what the launcher offers is still the offer <paramref name="launch"/> was chosen against.</summary>
    private bool OfferStillCurrent(LaunchRequest launch)
        => _link.LaunchTarget is not { } target || launch.OfferRevision == OfferRevision(target, OfferNow().Signature);

    /// <summary>The checks passed again: the launcher offers this computer again, and the view stops saying it does not.</summary>
    private void ResumeOffer()
    {
        var wasPaused = _checked.OfferNothing;
        _checked = _checked with { OfferNothing = false, GpuDisproved = false };
        if (wasPaused)
        {
            var note = _offeredAgainNote;
            var restores = note is not null && View.State == SupervisorState.Running;
            if (restores)
            {
                _offeredAgainNote = null;
            }

            Update(view => restores ? view with { Message = note!, OfferPaused = false }
                : view.State == SupervisorState.Available && view.OfferPaused ? view with { Message = AvailableMessage(_link.State, paused: false), OfferPaused = false }
                : view with { OfferPaused = false });
        }
    }

    /// <summary>
    /// Whether a pause must keep the offer's revision: the supervised container was asked to start for a
    /// launch that Offloadr, as far as is known, still names. A heartbeat with a higher revision retires a
    /// launch no executor has matched, and fences its container, which may have started and be about to
    /// register. Once Offloadr names another launch, or none, that one is matched or retired already, and
    /// that stands for this container whatever a later stream says. Nor is it held once a check found
    /// containers not to reach the GPU: what that launch was chosen for is not there.
    /// </summary>
    private bool HoldsRevision()
    {
        if (!_ownsContainer || _containerLaunchId is not { } launchId || !(_startUnanswered || _seenRunning))
        {
            return false;
        }

        return LaunchStillNamed(launchId) && !_checked.GpuDisproved;
    }

    /// <summary>
    /// Whether Offloadr, as far as is known, still names the launch of the supervised container: until a live
    /// stream names another, or none (or another launcher took over, which retires what is unmatched).
    /// </summary>
    private bool LaunchStillNamed(string launchId)
    {
        var link = _link.State;
        _launchNoLongerNamed |= link.IsFinal || (link.Launch is { } named
            ? !string.Equals(named.LaunchId, launchId, StringComparison.OrdinalIgnoreCase)
            : link.StreamLive);
        return !_launchNoLongerNamed;
    }

    /// <summary>Offloadr named another launch, or none, since the supervised container was asked to start for its own.</summary>
    private bool _launchNoLongerNamed;

    /// <summary>
    /// The launcher offers nothing for now; the view no longer counts the computer ready for new sessions.
    /// Nothing is an offer of its own, which retires launches chosen before, except while the revision is
    /// held (<see cref="HoldsRevision"/>): then it only lists no editors.
    /// </summary>
    private void PauseOffer()
    {
        _checked = _checked with { OfferNothing = true };
        Update(view => view.State == SupervisorState.Available
            ? view with { OfferPaused = true, Message = AvailableMessage(_link.State, paused: true) }
            : view with { OfferPaused = true });
    }

    // How availability ends, pauses and fails

    /// <summary>Clears what the person wants: availability needs attention, or ended without a Stop.</summary>
    private void Unwant() => _in = Change(intent => intent with { Wanted = false });

    /// <param name="keepLink">The launcher still has an outcome to deliver (offering nothing meanwhile), and ends once it has.</param>
    private void Fault(string message, FaultKind kind = FaultKind.Unknown, bool keepLink = false)
    {
        // Needs attention: nothing makes the computer available again until the person starts it.
        Unwant();
        if (!keepLink)
        {
            CancelLink();
        }

        _logger.LogWarning("Runner supervisor fault ({Kind}): {Message}", kind, RedactUrlQueries(message));
        AppendLog("supervisor: " + message);
        Update(view => view with { State = SupervisorState.Faulted, Message = message, Fault = SupervisorFault.For(kind, message, view.Engine?.Kind ?? ContainerEngineKind.Docker) });
    }

    private void FaultNameTaken(ContainerInfo? container = null) => Fault(NameTakenMessage(container), FaultKind.NameTaken);

    /// <summary>
    /// Ends availability for settings that cannot be used: the launcher stops, since a launch could not
    /// start. A running container keeps serving with the settings it has, and the fault shows once it is gone.
    /// </summary>
    private void FaultUnlessRunning(string message, FaultKind kind)
    {
        if (_ownsContainer)
        {
            AppendLog($"supervisor: {message}");
            LoseAvailability($"{message} Offloadr can start no new session here until it is fixed.", (message, kind));
            return;
        }

        Fault(message, kind);
    }

    /// <summary>Faults a start the person asked for; schedules another attempt for an automatic one.</summary>
    /// <param name="deliverFirst">An outcome the launcher keeps sending (offering nothing) before it stops for the wait.</param>
    private void FailStart(string message, bool automatic, FaultKind kind = FaultKind.Unknown, LaunchReport? deliverFirst = null)
    {
        if (!automatic)
        {
            Fault(message, kind);
            return;
        }

        // Not available meanwhile: the launcher stops until the checks pass again, once the outcome it
        // still owes has reached Offloadr (it offers nothing until then).
        if (deliverFirst is not null && _linkRun is { } run)
        {
            PauseOffer();
            Owe(deliverFirst, run, () => View.State == SupervisorState.WaitingToRetry);
        }
        else if (_owed is { } owed && ReferenceEquals(owed.Run, _linkRun))
        {
            // An outcome from before is still on its way: the launcher stays for it, offering nothing.
            PauseOffer();
        }
        else
        {
            CancelLink();
        }

        var delay = RegistrationPolicy.ComputeBackoff(_retryAttempt++, _options.RetryMinBackoff, _options.RetryMaxBackoff);
        _retryDueAt = _now + delay;
        _retryStops = _in.Stops;
        _logger.LogWarning("Making the runner available failed ({Kind}): {Message}", kind, RedactUrlQueries(message));
        AppendLog($"supervisor: {message} Trying again in {delay.TotalSeconds:0} s.");
        Update(view => view with
        {
            State = SupervisorState.WaitingToRetry,
            Message = $"{message} Trying again in {delay.TotalSeconds:0} seconds.",
            Fault = SupervisorFault.For(kind, message, view.Engine?.Kind ?? ContainerEngineKind.Docker),
        });
    }

    /// <summary>A start that failed on the engine; the exception says what kind of failure it was.</summary>
    private void FailStart(Exception failure, bool automatic) => FailStart(failure.Message, automatic, KindOf(failure));

    /// <summary>
    /// Availability ended without a Stop. A container keeps running (it may be serving a session) until
    /// it exits or the person stops it, and an update leaves it running.
    /// </summary>
    /// <param name="deliverFirst">An outcome the launcher keeps sending (offering nothing) before it ends.</param>
    private void LoseAvailability(string message, (string Message, FaultKind Kind)? fault = null, LaunchReport? deliverFirst = null)
    {
        Unwant();
        var keepLink = deliverFirst is not null && _linkRun is not null;
        if (keepLink)
        {
            // (Nothing is offered meanwhile, and nothing shown as if it were.)
            PauseOffer();
            Owe(deliverFirst!, _linkRun!, () => !_in.Wanted);
        }
        else
        {
            CancelLink();
        }

        if (_ownsContainer)
        {
            _offlineFault = fault;
            Update(view => view with { Message = $"{message} The runner container here keeps running until you stop it." });
            return;
        }

        if (fault is { } problem)
        {
            Fault(problem.Message, problem.Kind, keepLink);
            return;
        }

        SettleIdle();
    }

    /// <summary>
    /// Docker or the GPU let a launch down while a container runs: the launcher pauses, the computer
    /// stays wanted, and the checks are tried again with a backoff while the container keeps serving.
    /// </summary>
    private void SuspendAvailability(string message)
    {
        // The launcher stays, offering nothing, so the container can still be released once idle.
        PauseOffer();
        _linkSuspended = true;
        var delay = RegistrationPolicy.ComputeBackoff(_retryAttempt++, _options.RetryMinBackoff, _options.RetryMaxBackoff);
        _resumeAvailabilityAt = _now + delay;
        AppendLog($"supervisor: {message} Not offered meanwhile; checking again in {delay.TotalSeconds:0} s.");

        // What the view said before, shown again once the computer is offered again.
        _offeredAgainNote ??= View.Message;
        Update(view => view with { Message = $"{message} This computer is not offered until that is fixed; the runner container here keeps running." });
    }

    /// <summary>The state without a container, from availability and the launcher.</summary>
    private void SettleIdle()
    {
        if (_ownsContainer)
        {
            return;
        }

        var link = _link.State;
        if (_in.Wanted && LinkRunning && _linkRun is not null)
        {
            if (_checked.OfferNothing)
            {
                // Paused while the container ran: now that it is gone, the checks run again (and may
                // download what they need), rather than leave the launcher offering nothing.
                _refreshQueued = true;
            }

            Update(view => view with
            {
                State = SupervisorState.Available,
                Message = AvailableMessage(link, _checked.OfferNothing),
                RunnerStatus = null,
                ActiveTemplate = null,
                Image = null,
            });
        }
        else if (_in.Wanted)
        {
            // Wanted, but the launcher was stopped meanwhile (Docker did not answer): checked again now.
            _linkSuspended = false;
            _retryDueAt = _now;
            _retryStops = _in.Stops;
            Update(view => view with
            {
                State = SupervisorState.WaitingToRetry,
                Message = "Checking Docker and your GPU again…",
                RunnerStatus = null,
                ActiveTemplate = null,
                Image = null,
            });
        }
        else if (_offlineFault is { } problem)
        {
            _offlineFault = null;
            Fault(problem.Message, problem.Kind);
        }
        else if (link.Connection == LauncherConnection.Outdated)
        {
            Fault(link.Message ?? "Update the app to offer this computer's GPU again.", FaultKind.AppOutdated);
        }
        else
        {
            Update(view => view with
            {
                State = SupervisorState.Stopped,
                Message = link.Connection == LauncherConnection.Superseded && link.Message is { } message ? message : "The runner is stopped.",
                RunnerStatus = null,
                ActiveTemplate = null,
                Image = null,
            });
        }
    }

    // The launcher link's run

    /// <summary>Ends the launcher's run; a launch it named can no longer start, and nothing is reported. True while it has not ended yet.</summary>
    private bool CancelLink()
    {
        if (_linkRun is { } run)
        {
            // A run that ends after this is not Offloadr refusing the launcher.
            run.Cancelled = true;
            _linkRun = null;
        }

        // Nor is one that ended by itself just before, and is not dealt with yet: the launcher is ended now.
        _linkEnded = null;
        return Cancel(_linkSlot);
    }

    /// <summary>
    /// Ends a run Offloadr has not accepted yet, at once: registering after a Stop, or for an address
    /// or key Settings no longer name, could take that runner from another computer. One accepted stays,
    /// to ask for a release before its container stops.
    /// </summary>
    private void CancelLinkUnlessAccepted()
    {
        if (_linkRun is { Accepted: false } run)
        {
            // (No longer the run in the slot: one for the same address and key, should Settings name
            // them again, is a new one, started once this one's job has ended.)
            run.Cancelled = true;
            _linkRun = null;
            Cancel(_linkSlot);
        }
    }

    /// <summary>
    /// Runs the launcher link with <paramref name="settings"/>, unless it already runs with the same
    /// address and key. A change of either ends the run first: the new one registers for them.
    /// </summary>
    /// <returns>
    /// Null while it is not settled yet (the run before is ending, or the plan is asked for). False when
    /// Settings name another address or key by now: nothing was started for the old ones.
    /// </returns>
    private bool? StartLink(RunnerSettings settings, bool takeOver, Fact<SupervisorEffects.PlanAnswer> plan)
    {
        var credentials = Credentials(settings);
        if (_linkRun is { } running && running.Credentials == credentials)
        {
            return true;
        }

        if (CancelLink())
        {
            return null;
        }

        // The run that just ended may have ended taken over: only the person's Start takes the runner back.
        if (!takeOver && IsTakenOver(settings))
        {
            LoseAvailability("Another Offloadr Runner app took over this runner, perhaps on another computer. Press Start to offer this computer's GPU again.");
            return true;
        }

        // The editors offered are that Offloadr's, not those of the address or key before.
        if (_planCredentials != credentials && NeedPlan(plan, settings))
        {
            return null;
        }

        // Checked last, right before the registration: one for superseded settings could take that
        // runner over from another computer. Nothing lies between this check and the start.
        if (Credentials(_settings.Current) != credentials)
        {
            return false;
        }

        var run = new LinkRun(credentials);
        _linkRun = run;
        _linkEnded = null;

        // What was just checked is published before the run can send its first heartbeat: it must
        // never register with the offer of an earlier tick (every GPU, say, where one was chosen).
        Publish();

        // Ended at once, while Offloadr has not accepted it, by a Stop, by the app closing or by Settings
        // naming another address or key: on the thread that does either, before this loop even hears of
        // it. (One Offloadr accepted stays while the app closes: its container is released through it.)
        var stops = _in.Stops;
        var fenced = _fence.Until(() => Credentials(_settings.Current) != credentials || Volatile.Read(ref _intent).Stops != stops ||
                                        _stopping.IsCancellationRequested);
        Start(_linkSlot, "link", token => _fx.RunLinkAsync(settings, LauncherProfile, takeOver, fenced.Signal, token), result =>
        {
            fenced.Dispose();
            if (result.Value is { Superseded: true })
            {
                // Kept across app starts, and also when this run was being cancelled meanwhile: Offloadr's
                // refusal stands either way, and a start with the app must not take the runner back.
                RememberTakeover(run.Credentials);
            }

            if (result.Value is { Ended: true } && !run.Cancelled)
            {
                // Ended from here before it was accepted: not Offloadr refusing the launcher.
                run.Cancelled = true;
                if (ReferenceEquals(_linkRun, run))
                {
                    _linkRun = null;
                }

                // What ended it may be undone by now (a key changed and changed back before this loop
                // looked): still wanted for the same address and key, it is asked for again.
                var asked = Volatile.Read(ref _intent);
                if (asked.Wanted && asked.Stops == stops && !_stopping.IsCancellationRequested &&
                    Credentials(_settings.Current) == run.Credentials)
                {
                    _refreshQueued = true;
                }
            }

            if (!run.Cancelled)
            {
                if (ReferenceEquals(_linkRun, run))
                {
                    _linkRun = null;
                }

                _linkEnded = run;
            }
        });
        return true;
    }

    /// <summary>A run of the launcher link ended by itself: Offloadr refused this launcher for good.</summary>
    private void HandleLinkEnded()
    {
        var state = _link.State;
        AppendLog($"supervisor: the launcher stopped ({state.Connection}).");
        LoseAvailability($"{state.Message ?? "Offloadr does not accept this computer as a launcher."}");
    }

    /// <summary>Has the run <paramref name="run"/> stay until <paramref name="outcome"/> reached Offloadr, or a few minutes passed.</summary>
    private void Owe(LaunchReport outcome, LinkRun run, Func<bool> stillToEnd)
        => _owed = new Owed(outcome, run, _now + TimeSpan.FromMinutes(2), stillToEnd);

    /// <summary>
    /// Ends the launcher run that stayed to deliver an outcome, once it reached Offloadr (its link resends
    /// it until then) or the deadline passed; not if the person started the runner again, or another run
    /// took its place. Checked and ended in one step, on the loop.
    /// </summary>
    private void WorkOffOwedOutcome()
    {
        if (_owed is not { } owed)
        {
            return;
        }

        if (!ReferenceEquals(_linkRun, owed.Run))
        {
            // Ended, or another run since: that run is the person's.
            _owed = null;
            Cancel(_deliverySlot);
            return;
        }

        var waiting = _now < owed.Deadline &&
                      Need(_deliverySlot, owed.Delivered, "owed:" + owed.Outcome.LaunchId, token => _fx.DeliverAsync(owed.Outcome, TimeSpan.FromMinutes(2), token));
        if (waiting)
        {
            WakeAt(owed.Deadline);
            return;
        }

        _owed = null;
        Cancel(_deliverySlot);
        if (owed.StillToEnd())
        {
            CancelLink();
        }
    }

    // The launch plan

    /// <summary>True while a plan asked for <paramref name="settings"/> is still to come; it is recorded when it does.</summary>
    private bool NeedPlan(Fact<SupervisorEffects.PlanAnswer> fact, RunnerSettings settings)
    {
        if (fact.Known)
        {
            return false;
        }

        if (_planSlot.Busy)
        {
            // Another request is under way (a regular refresh, say): this one follows it.
            return true;
        }

        Start(_planSlot, "plan", token => _fx.GetPlanAsync(settings, token), result =>
        {
            if (result.Value is { } answer)
            {
                RecordPlan(answer);
                fact.Set("plan", answer);
            }
            else if (!result.Cancelled)
            {
                fact.Set("plan", null, result.Error);
            }
        });
        return true;
    }

    /// <summary>Records the catalog of editors the launcher offers and the images it names, as Offloadr answered.</summary>
    private void RecordPlan(SupervisorEffects.PlanAnswer answer)
    {
        var credentials = answer.Credentials;
        if (answer.Rejected)
        {
            _planRejectedFor = credentials;
        }
        else if (answer.Plan is not null && _planRejectedFor == credentials)
        {
            _planRejectedFor = null;
        }

        _planRefreshedAt = Now();
        var plan = answer.Plan;
        var known = _planCredentials == credentials ? View.Plan : CatalogOf(credentials);
        if (plan is null ||
            (known is { Source: LaunchPlanSource.ControlPlane } && plan.Source is LaunchPlanSource.Unreachable or LaunchPlanSource.BuiltIn))
        {
            // An outage, or a passing "not offered" answer (a proxy's 404 during a deploy), keeps the
            // catalog this Offloadr gave last. One another address or key gave does not apply.
            if (known is null)
            {
                NoteCatalog(credentials, plan ?? BuiltInLaunchPlanCatalog.Plan(source: LaunchPlanSource.Unreachable), replace: false);
                _planCredentials = credentials;
                Update(view => view with { Plan = plan ?? BuiltInLaunchPlanCatalog.Plan(source: LaunchPlanSource.Unreachable) });
            }
            else if (_planCredentials != credentials)
            {
                // Given before another runner was named in Settings: it is shown, and used, again.
                _planCredentials = credentials;
                Update(view => view with { Plan = known });
            }

            return;
        }

        _planCredentials = credentials;
        NoteCatalog(credentials, plan, replace: true);
        Update(view => view with { Plan = plan });
    }

    /// <summary>Notes the catalog of the runner <paramref name="credentials"/> name (with no key there is none to offer it for).</summary>
    private void NoteCatalog((string Origin, string Key) credentials, LaunchPlan plan, bool replace)
    {
        if (credentials.Key.Length == 0)
        {
            return;
        }

        try
        {
            var target = TakeoverTag(credentials);
            if (replace)
            {
                _catalogs[target] = plan;
            }
            else
            {
                _catalogs.TryAdd(target, plan);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            // An address that names nothing has no launcher either.
        }
    }

    /// <summary>The catalog the runner <paramref name="credentials"/> name was last given; null with none.</summary>
    private LaunchPlan? CatalogOf((string Origin, string Key) credentials)
    {
        if (credentials.Key.Length == 0)
        {
            return null;
        }

        try
        {
            return _catalogs.TryGetValue(TakeoverTag(credentials), out var plan) ? plan : null;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The catalog each runner (address and key) was last given, by launch target. Read by a heartbeat on the link's own thread.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LaunchPlan> _catalogs = new(StringComparer.Ordinal);

    // Letting go of the container


    /// <summary>
    /// A start of the supervised container was asked for, and the engine has not answered that it
    /// refused it (it succeeded, or its answer was lost or given up). From then on, seeing the container
    /// "created" is no proof it never ran: the engine may still start it, and one that started and
    /// exited before it was removed leaves nothing to say so. Only a refused start is a start that did
    /// not happen.
    /// </summary>
    private bool _startUnanswered;

    /// <summary>
    /// The engine showed the supervised container running at some point (the watch saw it, or the engine
    /// refused to remove it because it ran). That stands: seen "created" afterwards (by an engine that
    /// answers wrongly, or late), it is still a container that ran.
    /// </summary>
    private bool _seenRunning;

    /// <summary>Whether a container seen created and never started counts as one that ran nothing.</summary>
    private bool NeverRan(bool seenNeverStarted) => seenNeverStarted && !_startUnanswered && !_seenRunning;

    /// <summary>
    /// The start of the supervised container was asked for and never refused, and the engine has not
    /// shown it running: seen still only created, it may be started yet. That it serves nothing is known
    /// only once it is removed, or seen exited; kept on its engine as it is, it stays supervised.
    /// </summary>
    private bool StartMayBeUnderWay => _startUnanswered && !_seenRunning;

    /// <summary>
    /// The supervised container was seen created, by a look that went on to remove it: found gone afterwards
    /// (the removal's answer was lost), that is what it was, unless the engine showed it as one that ran since.
    /// </summary>
    private bool _seenCreated;

    /// <summary>Said once per container: it is still only created, and the engine would not remove it.</summary>
    private bool _keptCreatedNoted;

    /// <summary>What became of a container the supervisor lets go of, for Offloadr's record of it.</summary>
    private enum ContainerFate
    {
        /// <summary>Gone (exited, removed, or stopped without a granted release): reported with container_exited.</summary>
        Gone,

        /// <summary>Stopped after Offloadr granted its release: Offloadr knows; nothing to report.</summary>
        Released,

        /// <summary>Never started (never created, or created and removed unstarted): it ran nothing, and nothing is reported.</summary>
        NeverStarted,

        /// <summary>Not seen (its creation was cancelled or given up, and the engine may still make it): its record stays, looked for by its launch later.</summary>
        Unknown,
    }

    /// <summary>
    /// Stops treating the container as this supervisor's: after it was removed, or when the person
    /// chose to move on from an engine that no longer answers.
    /// </summary>
    private void AbandonContainer(ContainerFate? fate = null)
    {
        if (_containerLaunchId is { } launchId)
        {
            // A container this app started: Offloadr learns it is gone, unless it released it.
            var known = fate ?? (IsReleased(launchId) ? ContainerFate.Released : ContainerFate.Gone);
            if (known == ContainerFate.NeverStarted && (_startUnanswered || _seenRunning))
            {
                // Seen created, but its start was asked for and the engine never said it refused: it may
                // have started and exited before it was removed. Gone, for Offloadr to learn.
                known = ContainerFate.Gone;
            }

            if (known == ContainerFate.Gone && !_seenRunning && _launched.TryGetValue(launchId, out var record) && record.Pending)
            {
                // Its start was never asked for (it was created, and waited): whatever became of it since,
                // removed by someone else say, it ran nothing, and there is no exit to tell.
                known = ContainerFate.NeverStarted;
            }

            if (known == ContainerFate.Gone ||
                (known == ContainerFate.Released && (_seenRunning || _startUnanswered)))
            {
                // It ran (or may have) and is gone, whoever ended it, released or not: its launch never
                // fails for that, whenever Offloadr names it again. That it started is the true answer then.
                _exitedLaunches.Add(launchId);
            }

            if (known == ContainerFate.Gone && _containerLaunchTarget is { } target)
            {
                NoteGone(launchId, target, View.RunnerStatus?.InstanceId);
            }
            else if (known != ContainerFate.Unknown)
            {
                ForgetLaunched(launchId);
            }
        }

        if (fate == ContainerFate.Unknown && _containerLaunchId is { } unfound)
        {
            // Let go of unseen (its creation was cancelled or given up): what the engine still makes of it
            // is inert, and is looked for a while, to be removed.
            _unseen[unfound] = _now + _options.LateContainerWatch;
        }

        _unsettledLaunch = null;
        _adoptingNow = null;
        _startUnanswered = false;
        _seenRunning = false;
        _keptCreatedNoted = false;
        _seenCreated = false;
        _launchNoLongerNamed = false;
        _containerId = null;
        _attemptId = null;
        _containerLaunchId = null;
        _containerLaunchTarget = null;
        _containerEngine = null;
        _adoptedLaunchPending = false;
        _ownsContainer = false;
        _activeFingerprint = null;
        Volatile.Write(ref _activeRunnerKey, null);
        ContainerChanged();

        // A stopped one the engine kept is noted with its own engine (leftover-containers): the hint, which
        // says a runner may still run there, goes.
        _engineSelection.Forget();

        // Its status describes it alone: a release of the next container never names its executor.
        _offeredAgainNote = null;
        Update(view => view with { RunnerStatus = null });
    }

    private void ResetStatusTracking()
    {
        _monitorFailures = 0;
        _statusReadTimestamp = null;
        _statusUpdatedUtc = null;
        _statusAdvancedTimestamp = null;
        _statusFirstReadTimestamp = null;
        _sessionSeen = false;
    }

    /// <summary>Records a status read from the container, and when the agent last changed it.</summary>
    private void RecordStatus(RunnerStatusSnapshot? status)
    {
        _statusReadTimestamp = _time.GetTimestamp();
        if (status is not null)
        {
            _statusFirstReadTimestamp ??= _time.GetTimestamp();
            // Only a status says whether a session runs: none read (the container just died) changes nothing.
            _sessionSeen = !string.IsNullOrEmpty(status.ActiveSessionId);
        }

        // Freshness is judged by when this machine saw the agent's timestamp change, so clock
        // skew between the host and the container does not matter.
        if (status is not null && _statusUpdatedUtc is { } previous && status.UpdatedUtc != previous)
        {
            _statusAdvancedTimestamp = _time.GetTimestamp();
        }

        _statusUpdatedUtc = status?.UpdatedUtc;
        Update(view => view with { RunnerStatus = status });
    }
}
