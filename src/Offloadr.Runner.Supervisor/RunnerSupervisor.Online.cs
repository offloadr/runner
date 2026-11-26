using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hardware;
using Offloadr.Runner.Supervisor.Launcher;
using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Processes;
using Offloadr.Runner.Supervisor.Settings;
using Offloadr.Runner.V1;
using Selected = Offloadr.Runner.Supervisor.Containers.ContainerEngineSelection.Selected;

namespace Offloadr.Runner.Supervisor;

/// <content>
/// What each tick does, in order, and the procedures that make this computer available: the startup
/// look for a runner started before, adoption, the checks of Docker and the GPU, and the launcher's
/// registration. Each is run again from its top on every tick (see the class remarks).
/// </content>
public sealed partial class RunnerSupervisor
{
    /// <summary>The lane of the one procedure in progress: going online, a launch, a Stop, an idle release.</summary>
    private readonly Slot _main = new("main");

    private Startup? _startup = new();
    private Online? _online;
    private Flight? _flight;
    private Stopping? _stop;
    private IdleRelease? _idle;
    private EngineLook? _engineLook;
    private Adopting? _lateAdoption;
    private Closing? _closing;

    // What was asked and is dealt with (see Intent), and what the loop asks of itself.
    private long _stopsSeen;
    private long _stopsDone;
    private long _startsDone;
    private long _autosDone;
    private long _refreshesDone;
    private long _resumesDone;
    private bool _refreshQueued;
    private bool _launchLook;
    private (bool TakeOver, long Stops)? _requeued;
    private TimeSpan? _lookAgainAt;
    private RunnerSettings? _seenSettings;
    private LauncherLinkState? _seenLink;
    private long _ids;

    /// <summary>The value a job brought, or its failure thrown where the procedure that asked for it stands.</summary>
    private static T Get<T>(Fact<T> fact)
    {
        if (fact.Error is { } error)
        {
            ExceptionDispatchInfo.Throw(error);
        }

        return fact.Value!;
    }

    /// <summary>True the first time it is asked of <paramref name="done"/>: for what a procedure does once, though it is run again.</summary>
    private static bool Once(ref bool done)
    {
        if (done)
        {
            return false;
        }

        done = true;
        return true;
    }

    private void DropProcedures()
    {
        Cancel(_main);
        _flight?.Dispose();
        PutBackAdoption();
        (_startup, _online, _flight, _stop, _idle, _engineLook) = (null, null, null, null, null, null);
        ContainerBusy(false);
    }

    /// <summary>The adoption under way (as the app starts, during a check, or of a runner found later), until it is settled.</summary>
    private Adopting? _adoptingNow;

    /// <summary>
    /// Drops an adoption that has not finished. Its container is still known to run: it counts as found
    /// running again, so a Stop or the app closing stops it, and otherwise it is adopted later.
    /// </summary>
    private void PutBackAdoption()
    {
        if (_adoptingNow is { Outcome: null } adopting && !_ownsContainer)
        {
            _foundRunning ??= (adopting.Container, adopting.Engine ?? _engineSelection.Current);
        }

        _adoptingNow = null;
        _lateAdoption = null;
    }

    private void Reconcile()
    {
        NoteInputs();
        if (_stopping.IsCancellationRequested)
        {
            StepClosing();
            return;
        }

        if (_startup is { } startup)
        {
            if (!StepStartup(startup))
            {
                // What is already known gone needs neither Docker nor a registered launcher: told meanwhile.
                WorkOffGoneReports();
                return;
            }

            _startup = null;
        }

        StepWatch();
        StepMain();

        // Again: what the procedure just did (a container started, or left for the watch to settle) may
        // be the watch's to look at from this very tick, and nothing else would wake the loop for it.
        StepWatch();
        WorkOffHeldFailures();
        WorkOffOwedOutcome();
        WorkOffGoneReports();
        WorkOffMaintenance();
        WorkOffSupersededImages();
        StepLogFollower();
    }

    /// <summary>
    /// Reads what other threads changed since the last tick: the link's state, the settings, a Stop. What
    /// each means for the launch in flight or the link's run is decided here, in the same step.
    /// </summary>
    private void NoteInputs()
    {
        var link = _link.State;
        if (_linkRun is { } run && (link.Connection == LauncherConnection.Online || _link.AcceptedOfferRevision > 0))
        {
            run.Accepted = true;
        }

        if (!Equals(link, _seenLink))
        {
            var before = _seenLink;
            _seenLink = link;
            if (link.Connection == LauncherConnection.Online && before?.Connection != LauncherConnection.Online)
            {
                // Registered: containers noted gone are reported now.
                _goneReportDue = true;
            }

            var note = View.State == SupervisorState.Running && link is { Connection: LauncherConnection.Online, StreamLive: true } ? _offeredAgainNote : null;
            if (note is not null)
            {
                // Offered again after a pause: the note that said it was not is gone.
                _offeredAgainNote = null;
            }

            Update(view => view.State == SupervisorState.Available
                ? view with { Launcher = link, Message = AvailableMessage(link, view.OfferPaused) }
                : note is not null ? view with { Launcher = link, Message = note } : view with { Launcher = link });
            _launchLook = true;
        }

        if (_flight is { } flight)
        {
            WithdrawIfNotNamed(flight, link);
        }

        var settings = _settings.Current;
        if (!ReferenceEquals(settings, _seenSettings))
        {
            if (_seenSettings is not null)
            {
                NoteSettingsChanged(settings);
            }

            _seenSettings = settings;
        }

        if (_in.Stops > _stopsSeen)
        {
            // A launcher not accepted yet ends now, not when the Stop is carried out: registering after
            // the Stop could take the runner from another computer, and before it is accepted it can
            // release nothing.
            _stopsSeen = _in.Stops;
            CancelLinkUnlessAccepted();
        }

        if (_lookAgainAt is { } again)
        {
            if (_now >= again)
            {
                _lookAgainAt = null;
                _launchLook = true;
            }
            else
            {
                WakeAt(again);
            }
        }
    }

    private void NoteSettingsChanged(RunnerSettings settings)
    {
        if (LinkCredentials is { } linked && linked != Credentials(settings))
        {
            // An idle release not sent yet is not sent for the earlier address or key, and a launcher
            // for them not accepted yet does not register: it could take that runner from another
            // computer before the refresh replaces it.
            try
            {
                _idle?.Unsent.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // That release has ended.
            }

            CancelLinkUnlessAccepted();
        }

        // A launch belongs to the Offloadr address and key whose launcher was sent it: with
        // others in Settings, its container could not be started for it.
        if (_flight is { Withdrawn: false } launching && launching.LinkCredentials is { } credentials && credentials != Credentials(settings))
        {
            Withdraw(launching, RunnerLaunchPhase.Failed, "The Offloadr address or runner key changed in the app's settings.");
        }
        else if (_flight is { Withdrawn: false } changing && _checked.Engine is not null && OfferedConfigurationChanged(settings))
        {
            // Nor on another engine or GPU than the one offered: the new one is checked and offered first.
            // Dropped here without an outcome: Offloadr retires it once it accepts the new offer, and a
            // FAILED sent first would fail its allocation instead of giving it a new launch.
            Withdraw(changing, phase: null, "The container engine or GPU changed in the app's settings.");
        }

        _refreshQueued = true;
    }

    /// <summary>Steps the procedure in progress; when it ends, the next thing asked for begins in the same tick.</summary>
    private void StepMain()
    {
        if (_removingExited)
        {
            // The watch is removing the container it saw stopped: recorded first (it wakes the loop).
            return;
        }

        while (true)
        {
            if (_stop is { } stop)
            {
                if (!StepStop(stop))
                {
                    return;
                }

                _stop = null;
                _lookAgain = true;
            }
            else if (_online is { } online)
            {
                if (!StepOnline(online))
                {
                    return;
                }

                // A launch Offloadr names is looked at as soon as the computer is available.
                _online = null;
                _launchLook = true;
                _lookAgain = true;
            }
            else if (_flight is { } flight)
            {
                if (!StepFlight(flight))
                {
                    return;
                }

                KeepSleptGpuCheckOwed(flight.Start.SleptGpu, flight.Room.SleptGpu);
                flight.Dispose();
                _flight = null;
                _lookAgain = true;

                // Offloadr may name another launch by now, or still name this one.
                _launchLook = true;
            }
            else if (_idle is { } idle)
            {
                if (!StepIdleRelease(idle))
                {
                    return;
                }

                // Its job has ended by now: the signals it was given are let go of.
                idle.Dispose();
                _idle = null;
                _lookAgain = true;
            }
            else if (_lateAdoption is { } adoption)
            {
                if (_in.Stops > _stopsDone)
                {
                    // A Stop does not wait for it (it may be reading Offloadr's catalog): its container
                    // is handed to the Stop, which stops it.
                    if (Cancel(_main))
                    {
                        return;
                    }

                    PutBackAdoption();
                    _lookAgain = true;
                    continue;
                }

                bool? adopted;
                try
                {
                    adopted = StepAdopt(adoption);
                }
                catch (ContainerEngineException ex)
                {
                    // Its engine does not answer after all: found again at the next look.
                    _logger.LogWarning("Could not take a runner container this app started under supervision: {Error}", ex.Summary);
                    Cancel(_main);
                    adopted = false;
                }

                if (adopted is null)
                {
                    return;
                }

                _lateAdoption = null;
                _lookAgain = true;
                if (adopted == true && _in.Wanted)
                {
                    // Offered on only once its engine and the GPU are checked again.
                    _refreshQueued = true;
                }
            }
            else if (_engineLook is { } look)
            {
                if (!StepEngineLook(look))
                {
                    return;
                }

                _engineLook = null;
                _lookAgain = true;
            }
            else if (!NextCommand())
            {
                return;
            }
            else
            {
                _lookAgain = true;
            }
        }
    }

    /// <summary>Takes up the next thing asked for, in the order a Stop always wins. False when there is nothing to do now.</summary>
    private bool NextCommand()
    {
        if (_in.Stops > _stopsDone)
        {
            // An adoption cut short for it hands its container over: known to run, it is the Stop's to stop.
            PutBackAdoption();
            _stop = new Stopping(_in.Stops);
            return true;
        }

        if (_in.Starts > _startsDone)
        {
            // A Start pressed before a later Stop does not run.
            _startsDone = _in.Starts;
            if (_in.StartAfterStops == _in.Stops)
            {
                _online = new Online(automatic: false, takeOver: true, _in.Stops);
                return true;
            }
        }

        if (_in.Autos > _autosDone)
        {
            // An auto-start retries Docker trouble with a backoff (Docker is often still starting at sign-in).
            _autosDone = _in.Autos;
            if (_in.Stops == _in.StartupStops)
            {
                _online = new Online(automatic: true, takeOver: false, _in.Stops);
                return true;
            }
        }

        if (_requeued is { } again)
        {
            // Other settings were saved while the last checks ran: those are checked and offered instead.
            _requeued = null;
            if (again.Stops == _in.Stops)
            {
                _online = new Online(automatic: !again.TakeOver, again.TakeOver, again.Stops);
                return true;
            }
        }

        if (_in.Refreshes > _refreshesDone || _refreshQueued)
        {
            _refreshesDone = _in.Refreshes;
            _refreshQueued = false;
            if (Refresh())
            {
                return true;
            }
        }

        if (_in.Resumes > _resumesDone)
        {
            _resumesDone = _in.Resumes;
            if (HandleResumed())
            {
                return true;
            }
        }

        if (_linkEnded is not null)
        {
            _linkEnded = null;
            HandleLinkEnded();
        }

        if (_strays.Count > 0 && _ownsContainer && _in is { Wanted: false, StopAsked: true })
        {
            // The person asked for a Stop, which could not stop the supervised runner (its engine does not
            // answer): it still stands for a runner found since on an engine that does.
            _stop = new Stopping(_in.Stops) { StraysOnly = true };
            return true;
        }

        if (_foundRunning is not null && !_ownsContainer && _in is { Wanted: false, StopAsked: true })
        {
            // The person asked for the runner to be stopped, and this one could not be found then (its
            // engine did not answer): the Stop still stands for it. Stopped now, not adopted.
            _stop = new Stopping(_in.Stops);
            return true;
        }

        if (_foundRunning is { } found)
        {
            _foundRunning = null;
            if (_ownsContainer && found.Container.Labels.TryGetValue(LaunchIdLabel, out var besides) &&
                !string.Equals(besides, _containerLaunchId, StringComparison.OrdinalIgnoreCase))
            {
                // Another container is supervised by now (a launch won the place): this one stays known
                // beside it, so a Stop or the app closing stops it too.
                _strays[besides] = found;
            }

            if (!_ownsContainer)
            {
                // On the engine it runs on, which Settings may no longer name.
                _engineSelection.SelectExactly(found.Engine);
                _lateAdoption = new Adopting(found.Container);
                return true;
            }
        }

        if (_launchLook)
        {
            _launchLook = false;
            if (LookAtLaunch())
            {
                return true;
            }
        }

        return RegularChecks();
    }

    private bool Refresh()
    {
        if (_in.Wanted && View.State is SupervisorState.Available or SupervisorState.Running)
        {
            // Settings may have changed: an address or key the launcher must use, an engine or GPU to
            // check again. A running container keeps running; it is replaced once it is idle.
            _online = new Online(automatic: true, takeOver: false, _in.Stops);
            _watchNow = true;
            return true;
        }

        if (!View.IsActive && View.State != SupervisorState.Stopping)
        {
            _engineLook = new EngineLook(whileAvailable: false);
            return true;
        }

        return false;
    }

    private bool HandleResumed()
    {
        AppendLog("supervisor: the computer woke from sleep; checking the runner again.");
        switch (View.State)
        {
            case SupervisorState.WaitingToRetry:
                // Docker was likely asleep too: the retry runs now rather than when its backoff ends.
                _retryDueAt = _now;
                return false;
            case SupervisorState.Running:
                // The container may have gone with Docker; and after sleep the GPU can stop reaching
                // containers until Docker restarts, which the check shows. The next launch proves it again.
                _checked = _checked with { PassedFor = null };
                _watchNow = true;
                if (_in.Wanted)
                {
                    // Offered on only if Docker and the GPU pass again; the container keeps serving either way.
                    _refreshQueued = true;
                }
                else
                {
                    CheckGpuAccessInBackground();
                }

                return false;
            case SupervisorState.Available:
                // Docker may be gone, and the GPU may no longer reach containers: both are proved again.
                _checked = _checked with { PassedFor = null };
                _online = new Online(automatic: true, takeOver: false, _in.Stops);
                return true;
            default:
                if (!View.IsActive)
                {
                    _engineLook = new EngineLook(whileAvailable: false);
                    return true;
                }

                return false;
        }
    }

    /// <summary>What comes round by itself: a retry that is due, Docker still answering, the plan, an idle container.</summary>
    private bool RegularChecks()
    {
        switch (View.State)
        {
            case SupervisorState.WaitingToRetry when _retryDueAt is { } due:
                if (_now < due)
                {
                    WakeAt(due);
                    return false;
                }

                // Under the Stops it was scheduled in: a Stop since then wins.
                _retryDueAt = null;
                _online = new Online(automatic: true, takeOver: false, _retryStops);
                return true;
            case SupervisorState.Running:
                return CheckIdleContainer();
            case SupervisorState.Available:
                var engineDue = _engineCheckedAt + _options.EngineCheckInterval;
                if (_now >= engineDue)
                {
                    _engineLook = new EngineLook(whileAvailable: true);
                    return true;
                }

                if (_now >= PlanRefreshDue())
                {
                    RefreshPlanInBackground();
                }

                WakeAt(Min(engineDue, Max(PlanRefreshDue(), _now + _options.MonitorInterval)));
                return false;
            default:
                return false;
        }
    }

    private readonly Fact<SupervisorEffects.PlanAnswer> _regularPlan = new();

    /// <summary>Asks for the launch plan again: the catalog of editors the launcher offers and the images it names.</summary>
    private void RefreshPlanInBackground()
    {
        if (_planSlot.Busy)
        {
            return;
        }

        // Stamped as asked now, so it is not asked again on every tick while the answer is on its way.
        _planRefreshedAt = _now;
        _regularPlan.Forget();
        NeedPlan(_regularPlan, _settings.Current);
    }

    private readonly Slot _auxiliarySlot = new("auxiliary");

    /// <summary>Runs the Status page's GPU check by itself, for a container that runs with no check shown yet.</summary>
    private void CheckGpuAccessInBackground()
    {
        if (_auxiliarySlot.Busy)
        {
            return;
        }

        var image = View.ActiveTemplate?.Image ?? (View.Plan ?? BuiltInLaunchPlanCatalog.Plan()).Choose(_settings.Current)?.Image;
        var engine = _containerEngine ?? _engineSelection.Current;
        Start(_auxiliarySlot, "gpu access", token => _fx.CheckGpuAccessQuietlyAsync(image, engine, token), result =>
        {
            if (result.Value is { } check)
            {
                Update(view => view with { GpuAccess = check });
            }
        });
    }

    // Startup

    private sealed class Startup
    {
        public RememberedCheck Remembered { get; } = new();

        public SupervisorEffects.Remembered? RememberedOutcome { get; set; }

        public Fact<SupervisorEffects.Probed> Probe { get; } = new();

        public bool ProbeRecorded;

        public Selected? Engine { get; set; }

        public Fact<SupervisorEffects.NameLook> Named { get; } = new();

        public Adopting? Adopt { get; set; }

        public LaunchedCheck Launched { get; } = new();
    }

    /// <summary>
    /// Looks, when the app opens, for a runner container it started before, and adopts one that still
    /// runs. Docker may still be starting (at sign-in, say): a failure here faults, and the loop goes on,
    /// so Start, Stop and Refresh keep working; a start probes Docker again.
    /// </summary>
    private bool StepStartup(Startup startup)
    {
        try
        {
            return StepStartupCore(startup);
        }
        catch (Exception ex)
        {
            LogUnexpected(ex, "Runner supervisor startup check failed");
            Cancel(_main);
            Fault(ex is ContainerEngineException ? ex.Message : $"Unexpected error: {ex.Message}", KindOf(ex));
            RequestAutoStartIfSet();
            return true;
        }
    }

    private void RequestAutoStartIfSet()
    {
        if (_settings.Current.AutoStart)
        {
            RequestAutoStart();
            _in = Volatile.Read(ref _intent);
        }
    }

    private bool StepStartupCore(Startup startup)
    {
        if (startup.RememberedOutcome is null)
        {
            if (StepRemembered(startup.Remembered) is not { } outcome)
            {
                return false;
            }

            startup.RememberedOutcome = outcome;
            switch (outcome)
            {
                case SupervisorEffects.Remembered.Found:
                    // An adopted container is supervised either way; the computer is offered again only as at
                    // any app start: with AutoStart, or after an update that had it available. So a container
                    // a takeover left serving does not take the runner back when the app restarts.
                    RequestAutoStartIfSet();
                    return true;
                case SupervisorEffects.Remembered.Unreachable:
                    // Nothing is started on another engine while the runner may still run on this one;
                    // an automatic start keeps checking it, and Start overrides the hint.
                    Fault($"{UnreachableEngineMessage()} Start it, or press Start to run the runner on the engine in Settings.", FaultKind.EngineUnreachable);
                    RequestAutoStartIfSet();
                    return true;
            }
        }

        if (startup.Engine is null)
        {
            if (!_ownsContainer)
            {
                _engineSelection.Select(_settings.Current.ContainerEngineExecutable);
            }

            startup.Engine = _engineSelection.Current;
        }

        var engine = startup.Engine;
        if (Need(_main, startup.Probe, "startup probe", token => _fx.ProbeAsync(engine, readContext: true, token)))
        {
            return false;
        }

        var probed = Get(startup.Probe);
        if (Once(ref startup.ProbeRecorded))
        {
            Update(view => view with { Engine = probed.Engine, HostGpus = probed.Gpus });
            if (probed.Engine.Available && _engineSelection.Context is null)
            {
                // The runner found here is adopted on the context it was looked for in.
                _engineSelection.PinContext(probed.Pin);
            }
        }

        if (probed.Engine.Available)
        {
            var pinned = _engineSelection.Current;
            if (Need(_main, startup.Named, "startup name", token => _fx.LookAtNameAsync(pinned, rejectedId: null, NoteRemoved, token)))
            {
                return false;
            }

            Get(startup.Named);
            if (Get(startup.Named) is { Foreign: false, Holder: { Running: true } existing })
            {
                // Running, or started again as it was being removed: adopted either way.
                if (StepAdopt(startup.Adopt ??= new Adopting(existing)) is not { } adopted)
                {
                    return false;
                }

                if (adopted)
                {
                    RequestAutoStartIfSet();
                    return true;
                }
            }

            // The engine answered and runs no runner of this app by that name: one it started under another
            // name now is adopted, and any other it started is gone. A lookup that fails says nothing: a
            // start checks again before it offers anything.
            try
            {
                if (StepLaunched(startup.Launched) is not { } adoptedByLaunch)
                {
                    return false;
                }

                if (adoptedByLaunch)
                {
                    RequestAutoStartIfSet();
                    return true;
                }
            }
            catch (ContainerEngineException ex)
            {
                _logger.LogWarning("Could not look for the runner containers this app started: {Error}", ex.Summary);
            }
        }

        RequestAutoStartIfSet();
        return true;
    }

    private string UnreachableEngineMessage()
        => $"The runner may still be running on {_engineSelection.Executable ?? "the engine it was started on"}, which does not answer.";

    // Looking for a runner started before

    private sealed class RememberedCheck
    {
        public bool? Restored { get; set; }

        public Selected? Engine { get; set; }

        public Fact<SupervisorEffects.RememberedLook> Look { get; } = new();

        public bool LookRecorded;

        public Adopting? Adopt { get; set; }

        public bool AdoptSettled { get; set; }

        public LaunchedCheck Launched { get; } = new();
    }

    /// <summary>
    /// After a crash, looks for the runner on the engine it was started on, which Settings may no
    /// longer name. Found, it is adopted there; otherwise startup continues with the engine Settings name.
    /// Null while the look is under way.
    /// </summary>
    private SupervisorEffects.Remembered? StepRemembered(RememberedCheck check)
    {
        check.Restored ??= _engineSelection.TryRestore();
        if (check.Restored == false)
        {
            return SupervisorEffects.Remembered.None;
        }

        var engine = check.Engine ??= _engineSelection.Current;
        var rememberedId = _engineSelection.RememberedContainerId;
        if (Need(_main, check.Look, "remembered", token => _fx.LookForRememberedAsync(engine, rememberedId, NoteRemoved, token)))
        {
            return null;
        }

        var look = Get(check.Look);
        if (look.Outcome == SupervisorEffects.Remembered.Unreachable)
        {
            return SupervisorEffects.Remembered.Unreachable;
        }

        if (look.Container is { } container && !check.AdoptSettled)
        {
            if (Once(ref check.LookRecorded))
            {
                Update(view => view with { Engine = look.Engine, HostGpus = look.Gpus });
            }

            if (StepAdopt(check.Adopt ??= new Adopting(container)) is not { } adopted)
            {
                return null;
            }

            if (adopted)
            {
                return SupervisorEffects.Remembered.Found;
            }

            check.AdoptSettled = true;
        }

        // The engine answered and runs no runner of this app by that name or ID: one it started under
        // another name is adopted, any other it started there is gone (noted while that engine is still
        // the one selected), and otherwise the hint has done its job.
        try
        {
            if (StepLaunched(check.Launched) is not { } adoptedByLaunch)
            {
                return null;
            }

            if (adoptedByLaunch)
            {
                return SupervisorEffects.Remembered.Found;
            }
        }
        catch (ContainerEngineException ex)
        {
            // Inconclusive: one it started may still run there, under another name. The hint stays, and
            // nothing is started elsewhere until that engine says.
            _logger.LogWarning("Could not look for the runner containers this app started: {Error}", ex.Summary);
            return SupervisorEffects.Remembered.Unreachable;
        }

        _engineSelection.Forget();
        return SupervisorEffects.Remembered.None;
    }

    private sealed class LaunchedCheck
    {
        public IReadOnlyList<(string LaunchId, Selected Engine)>? Candidates { get; set; }

        public Fact<IReadOnlyList<SupervisorEffects.LaunchedLook>> Looked { get; } = new();

        public Adopting? Adopt { get; set; }
    }

    /// <summary>
    /// Every container noted as started on the selected engine that is not the one supervised now is
    /// looked up there: one that does not run is gone, and, owning none, one found running under another
    /// name is adopted (so it is released before anything replaces it, and Stop or Quit reaches it).
    /// Null while that is under way; true when one was adopted. Throws when that engine does not answer:
    /// the caller must not offer a replacement while one may still run there.
    /// </summary>
    private bool? StepLaunched(LaunchedCheck check)
    {
        var candidates = check.Candidates ??= UnresolvedLaunches(on: _engineSelection.Current);
        if (candidates.Count == 0)
        {
            return false;
        }

        // (Each look is recorded as it is made, whatever becomes of the ones after it.)
        if (Need(_main, check.Looked, "launched", token => _fx.LookForLaunchedAsync(candidates, tolerant: false, look => Post(() => RecordLaunchedLook(look, maintenance: false)), token)))
        {
            return null;
        }

        var looked = Get(check.Looked);
        if (looked.Any(MayStartYet))
        {
            // Its start was asked for before the app closed, and the engine may still make it: nothing is
            // offered beside it on that engine until it is removed, seen exited, or runs (and is adopted).
            throw new ContainerEngineException("A runner container this app created earlier has not started, may start yet, and could not be removed from the container engine.");
        }

        if (_ownsContainer && check.Adopt is null)
        {
            return false;
        }

        if (looked.FirstOrDefault(look => look.Running is not null)?.Running is not { } running)
        {
            return false;
        }

        return StepAdopt(check.Adopt ??= new Adopting(running));
    }

    // Adoption

    private sealed class Adopting(ContainerInfo container)
    {
        public ContainerInfo Container { get; } = container;

        public RunnerSettings? Settings { get; set; }

        public Selected? Engine { get; set; }

        public Fact<SupervisorEffects.Adoption> Prepared { get; } = new();

        /// <summary>It is stopped instead of adopted: Offloadr released it before the app closed. Decided once.</summary>
        public bool? Stops { get; set; }

        public bool? Outcome { get; set; }
    }

    /// <summary>
    /// Takes over a runner container this app started before (it restarted, crashed or was updated).
    /// One whose release Offloadr already granted is stopped instead, and false is the outcome. Null
    /// while it is under way.
    /// </summary>
    private bool? StepAdopt(Adopting adopting)
    {
        if (adopting.Outcome is { } settled)
        {
            return settled;
        }

        var container = adopting.Container;
        container.Labels.TryGetValue(LaunchIdLabel, out var launchId);
        var released = IsReleased(launchId);

        // Taken up here: the same container, queued by a look for it by its launch, is not taken up again.
        if (_foundRunning is { } queued && string.Equals(NormalizeContainerId(queued.Container.Id), NormalizeContainerId(container.Id), StringComparison.OrdinalIgnoreCase))
        {
            _foundRunning = null;
        }

        if (launchId is not null)
        {
            _strays.Remove(launchId);
        }

        // One snapshot: the catalog is noted under the address and key it was asked with.
        var settings = adopting.Settings ??= _settings.Current;
        var engine = adopting.Engine ??= _engineSelection.Current;
        container.Labels.TryGetValue(TemplateLabel, out var templateId);
        container.Labels.TryGetValue(SpecLabel, out var fingerprint);
        var image = container.Labels.TryGetValue(ImageLabel, out var named) ? named : container.Image;

        // The key is part of the fingerprint, so the container was started with the current key
        // exactly when its own image and template under the current settings give its fingerprint.
        var startedWithCurrentSettings = settings.HasRunnerKey &&
            string.Equals(
                Fingerprint(BuildSpec(settings, new LaunchTemplate(templateId ?? string.Empty, string.Empty, image, string.Empty))),
                fingerprint,
                StringComparison.Ordinal);
        if (released && !adopting.Prepared.Known && !_main.Busy)
        {
            AppendLog("supervisor: stopping the runner container Offloadr released before the app closed.");
        }

        var (grace, stops) = (StopGrace, adopting.Stops ??= released);
        if (Need(_main, adopting.Prepared, "adopt", token => _fx.PrepareAdoptionAsync(engine, container, stops, readKey: !startedWithCurrentSettings, settings, grace, token)))
        {
            // Known to run from here on, adopted yet or not: the app closing meanwhile still stops it.
            _adoptingNow = adopting;
            return null;
        }

        if (ReferenceEquals(_adoptingNow, adopting))
        {
            _adoptingNow = null;
        }

        var prepared = Get(adopting.Prepared);
        if (_engineSelection.Context is null)
        {
            _engineSelection.PinContext(prepared.Pin);
        }

        if (prepared.StoppedReleased)
        {
            // Released before the app could stop it: it may no longer serve, and is never reused. Offloadr
            // knows it is gone: its record goes too, so that nothing is reported for it later.
            if (launchId is not null)
            {
                ForgetLaunched(launchId);
            }

            if (string.Equals(launchId, _releasedLaunch, StringComparison.OrdinalIgnoreCase))
            {
                ClearReleaseMarker();
            }

            _engineSelection.Forget();
            adopting.Outcome = false;
            return false;
        }

        if (!_engineSelection.Remember(NormalizeContainerId(container.Id), out var rememberError))
        {
            // The runner already runs; it is supervised all the same, only less safely across a crash.
            AppendLog($"supervisor: {rememberError}");
        }

        if (prepared.Plan is { } answer)
        {
            if (answer.Rejected)
            {
                _planRejectedFor = answer.Credentials;
            }
            else if (answer.Plan is not null && _planRejectedFor == answer.Credentials)
            {
                _planRejectedFor = null;
            }
        }

        var plan = prepared.Plan?.Plan;
        if (plan is { Source: LaunchPlanSource.Unreachable or LaunchPlanSource.BuiltIn } &&
            CatalogOf(Credentials(settings)) is { Source: LaunchPlanSource.ControlPlane } given)
        {
            // (As for any refresh: an outage keeps what Offloadr gave this runner last.)
            plan = given;
        }

        var template = plan?.Find(templateId) ?? BuiltInLaunchPlanCatalog.Plan().Find(templateId);
        var now = _now;
        _ownsContainer = true;
        _activeFingerprint = fingerprint;
        _containerId = NormalizeContainerId(container.Id);
        _attemptId = container.Labels.TryGetValue(LaunchLabel, out var attempt) ? attempt : null;
        _containerLaunchId = launchId;
        _containerLaunchTarget = container.Labels.TryGetValue(LaunchTargetLabel, out var target) ? target : null;
        _runningImageId = container.ImageId;
        _runningEngine = _engineSelection.Current;
        _containerEngine = _engineSelection.Current;
        _runningTemplate = new LaunchTemplate(
            templateId ?? string.Empty,
            template?.RuntimeKind ?? string.Empty,
            image,
            template?.Label ?? templateId ?? image);

        // Started with another key: the container's own environment holds it, so its log can still be redacted.
        Volatile.Write(ref _activeRunnerKey, startedWithCurrentSettings ? settings.RunnerKey!.Trim() : prepared.Key);
        RememberStartedWith(launchId, prepared.Origin, prepared.Key);
        _adoptedLaunchPending = !startedWithCurrentSettings && prepared.KeyPending;

        // A release asked before the app closed may have been granted: it is asked again first, once
        // the computer is offered. Not offered (no AutoStart), the launcher is not registered just to ask:
        // that could take the runner from another computer. A grant needs no asking either way: it
        // fenced the container's agent, which then exits by itself (exit code 4), and the note goes with it.
        ConfirmLaunched(launchId);
        if (launchId is not null && ReleaseNoted(launchId) == ReleaseNote.Asked)
        {
            // Supervised from here on: its release is noted as any supervised container's is.
            WriteReleaseAsked(launchId);
            NoteRelease(launchId, ReleaseNote.None);
        }

        // One whose release was asked before is released again as soon as it is idle, whatever the warm time.
        _idleSince = now;
        SetReleaseDue(launchId is not null && string.Equals(launchId, _releaseAsked, StringComparison.OrdinalIgnoreCase));
        _releaseRetryAt = TimeSpan.Zero;
        ResetStatusTracking();
        ContainerChanged();
        if (plan is not null)
        {
            _planRefreshedAt = now;
            _planCredentials = Credentials(settings);
            NoteCatalog(Credentials(settings), plan, replace: true);
        }

        if (ActiveRunnerKey is not null)
        {
            StartLogFollower();
        }
        else
        {
            // It may have been started with an earlier key, which its log could print and which
            // the log cannot be redacted against until that key is known.
            AppendLog(_adoptedLaunchPending
                ? "supervisor: the runner that was already running uses earlier settings; its log is shown once they can be read."
                : "supervisor: the runner that was already running uses earlier settings; its log is not shown.");
        }

        var reconnected = $"Reconnected to the runner container that was already running{(template is null ? "." : $" ({template.Label}).")}";
        Update(view => view with
        {
            State = SupervisorState.Running,
            Message = launchId is null
                ? $"{reconnected} An earlier version of the app started it, so it is not stopped by itself: stop the runner to replace it."
                : reconnected,
            Plan = plan ?? view.Plan,
            ActiveTemplate = template,
        });
        adopting.Outcome = true;
        return true;
    }

    // Going online

    private sealed class Online(bool automatic, bool takeOver, long stops)
    {
        /// <summary>
        /// Not a person pressing Start. Docker trouble then schedules another attempt with a backoff instead
        /// of faulting, so the computer becomes available on its own once Docker does.
        /// </summary>
        public bool Automatic { get; } = automatic;

        /// <summary>A person pressed Start: if another app took the runner over while this one was off, this one takes it back.</summary>
        public bool TakeOver { get; } = takeOver;

        /// <summary>The Stops asked when this start was decided; a later Stop cancels it.</summary>
        public long Stops { get; } = stops;

        /// <summary>The settings this start checks and registers for: one snapshot, compared with the latest before it registers.</summary>
        public RunnerSettings? Settings { get; set; }

        public int Stage { get; set; }

        /// <summary>Whether a container was supervised when the checks began: one that goes meanwhile has the checks begin again.</summary>
        public bool? WithContainer { get; set; }

        public bool LinkDecided;

        public bool EndingLink { get; set; }

        public bool? Resuming { get; set; }

        public Fact<SupervisorEffects.Observation> Resumed { get; } = new();

        public bool ResumeRecorded;

        public EnvironmentCheck Environment { get; } = new();

        public bool? ReadingStatus { get; set; }

        public Fact<RunnerStatusSnapshot?> Status { get; } = new();

        public bool StatusRecorded;

        public Preflight Preflight { get; } = new();

        public (string Message, FaultKind Kind, string Reported)? Problem { get; set; }

        public bool Suspended;

        public Fact<SupervisorEffects.PlanAnswer> LinkPlan { get; } = new();
    }

    /// <summary>Settings name another address or key, engine, GPU or models folder than <paramref name="settings"/> by now.</summary>
    private bool Superseded(RunnerSettings settings)
        => _settings.Current is var now && !ReferenceEquals(now, settings) &&
           (Credentials(now) != Credentials(settings) ||
            !string.Equals(ContainerEngineSelection.Normalize(now.ContainerEngineExecutable), ContainerEngineSelection.Normalize(settings.ContainerEngineExecutable), StringComparison.Ordinal) ||
            !string.Equals(now.SelectedGpuUuid, settings.SelectedGpuUuid, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(now.LocalModelsDirectory, settings.LocalModelsDirectory, StringComparison.Ordinal));

    /// <summary>
    /// Makes this computer available: checks the settings, Docker and that containers reach the GPU,
    /// then runs the launcher link. Idempotent: what already passed is not checked again, and a
    /// launcher already running with the current address and key is left alone. True once it is done.
    /// </summary>
    private bool StepOnline(Online online)
    {
        if (_in.Stops != online.Stops)
        {
            // Stop was pressed after this start was decided: the Stop that follows cleans up.
            if (Cancel(_main))
            {
                return false;
            }

            Set(SupervisorState.Stopping, "Stopping the runner…");
            return true;
        }

        try
        {
            return StepOnlineCore(online);
        }
        catch (Exception ex) when (ex is ContainerEngineException or ProcessStartException)
        {
            Cancel(_main);
            FailStart(ex, online.Automatic);
            return true;
        }
    }

    private bool StepOnlineCore(Online online)
    {
        if (online.Settings is null)
        {
            _in = Change(intent => intent.Stops == online.Stops ? intent with { Wanted = true } : intent);
            _retryDueAt = null;
            _offlineFault = null;
            online.Settings = _settings.Current;
        }

        var settings = online.Settings;
        if (online.Stage > 0 && !_main.Busy && Superseded(settings))
        {
            // Saved meanwhile: what was checked for the earlier ones decides nothing (a failure of theirs would
            // end a Start the person pressed). The ones saved are checked instead.
            _requeued = (online.TakeOver, online.Stops);
            return true;
        }

        if (online.Stage == 0)
        {
            if (!settings.HasRunnerKey)
            {
                // What a first start most likely lacks: named before any other problem, and before
                // Docker is asked anything.
                FaultUnlessRunning(RunnerSettings.MissingKeyProblem, FaultKind.KeyMissing);
                return true;
            }

            if (settings.Validate() is { Count: > 0 } problems)
            {
                FaultUnlessRunning(problems[0], FaultKind.Settings);
                return true;
            }

            if (online.TakeOver)
            {
                ForgetTakeover(settings);
            }
            else if (IsTakenOver(settings))
            {
                // Another app took this runner over: only the person's Start takes it back, not a
                // start with the app or after an update.
                const string takenOver = "Another Offloadr Runner app took over this runner, perhaps on another computer. Press Start to offer this computer's GPU again.";
                LoseAvailability(takenOver);
                if (!_ownsContainer)
                {
                    Update(view => view with { Message = takenOver });
                }

                return true;
            }

            online.Stage = 1;
        }

        if (online.Stage == 1)
        {
            if (Once(ref online.LinkDecided))
            {
                if (_linkRun is not null && _ownsContainer && LinkCredentials == Credentials(settings) && NeedsNewChecks(settings))
                {
                    // A container runs: the launcher stays, so it can release that container once idle, but
                    // offers nothing while the new engine or GPU is checked.
                    PauseOffer();
                }
                else if (_linkRun is not null && LinkCredentials == Credentials(settings) && _owed is { } owed && ReferenceEquals(owed.Run, _linkRun))
                {
                    // It still owes Offloadr a launch's outcome: it stays, offering nothing, while the checks
                    // run again, and offers again once they pass.
                    PauseOffer();
                }
                else if (_linkRun is not null && ((LinkCredentials is { } running && running != Credentials(settings)) || NeedsNewChecks(settings)))
                {
                    // Another address or key, engine or GPU: nothing is offered while the new ones are
                    // checked (which can take a download, with no launch progress sent meanwhile).
                    // Launches named then would fail, or wait unanswered.
                    online.EndingLink = true;
                }
            }

            if (online.EndingLink)
            {
                if (CancelLink())
                {
                    return false;
                }

                online.EndingLink = false;
                if (!_ownsContainer)
                {
                    Set(SupervisorState.Checking, "Checking Docker and your GPU…");
                }
            }

            online.Stage = 2;
        }

        if (online.Stage == 2)
        {
            if (online.Resuming ??= _ownsContainer && View.State != SupervisorState.Running)
            {
                // Kept after a stop that failed (Docker did not answer): watched again if it still
                // runs, forgotten if it is gone, before the computer is offered again.
                var (engine, id, attempt) = (_containerEngine ?? _engineSelection.Current, _containerId, _attemptId);
                if (Need(_main, online.Resumed, "resume", token => _fx.ResumeOwnedAsync(engine, id, attempt, token)))
                {
                    return false;
                }

                if (online.Resumed.Error is ContainerEngineException && !online.Automatic && Once(ref online.ResumeRecorded))
                {
                    if (string.Equals(engine.Configured, ContainerEngineSelection.Normalize(settings.ContainerEngineExecutable), StringComparison.Ordinal))
                    {
                        // The same engine as Settings name: nothing else to start on. It says why.
                        Get(online.Resumed);
                    }

                    // The person pressed Start with another engine in Settings, and the one the runner
                    // was on still does not answer: started there, as asked. The container's record
                    // stays: it is looked for on its own engine once that answers (taken up again if it
                    // runs, told to Offloadr if it is gone).
                    AppendLog($"supervisor: {UnreachableEngineMessage()} Starting on the engine in Settings, as asked.");
                    AbandonContainer(ContainerFate.Unknown);
                }

                var resumed = online.ResumeRecorded && online.Resumed.Error is not null ? null : Get(online.Resumed);
                if (resumed is not null && Once(ref online.ResumeRecorded))
                {
                    _containerId = resumed.LearnedId ?? _containerId;
                    if (resumed.Container is not null)
                    {
                        ResetStatusTracking();
                        ContainerChanged();
                        StartLogFollower(resume: true);
                        Update(view => view with { State = SupervisorState.Running, Message = "Watching the runner container again." });
                    }
                    else
                    {
                        AbandonContainer(resumed.NeverStarted ? ContainerFate.NeverStarted : null);
                    }
                }
            }

            online.Stage = 3;
            online.WithContainer = _ownsContainer;
        }

        if (online.WithContainer == true && !_ownsContainer)
        {
            // The container went while its checks ran (it exited): they begin again, without it.
            if (Cancel(_main))
            {
                return false;
            }

            _online = new Online(online.Automatic, online.TakeOver, online.Stops) { Settings = settings, Stage = 1, LinkDecided = true };
            Set(SupervisorState.Checking, "Checking Docker and your GPU…");
            return StepOnlineCore(_online);
        }

        if (online.Stage == 3)
        {
            if (online.WithContainer == false)
            {
                if (StepEnvironment(online.Environment, settings, online.Automatic) is not { } passed)
                {
                    return false;
                }

                if (!passed)
                {
                    return true;
                }

                if (!_ownsContainer)
                {
                    // Checked, as offered from now on.
                    ResumeOffer();
                }
            }

            online.Stage = 4;
        }

        if (online.Stage == 4)
        {
            if (online.ReadingStatus ??= _ownsContainer && View.RunnerStatus is null)
            {
                // Before the checks, which can take a while (a download): whether the container serves a
                // session is known meanwhile, so a Quit warns about it.
                var (engine, container) = (_containerEngine ?? _engineSelection.Current, OurContainer);
                if (Need(_main, online.Status, "status", token => _fx.ReadStatusAsync(engine, container, token)))
                {
                    return false;
                }

                if (Once(ref online.StatusRecorded))
                {
                    if (online.Status.Error is ContainerEngineException unread)
                    {
                        AppendLog($"supervisor: could not read the runner's status ({unread.Message}); asking by its launch alone.");
                    }
                    else
                    {
                        RecordStatus(Get(online.Status));
                    }
                }
            }

            online.Stage = 5;
        }

        if (online.Stage == 5)
        {
            if (_ownsContainer)
            {
                if (StepPreflight(online.Preflight, settings) is not { } checkedEngine)
                {
                    return false;
                }

                online.Problem = checkedEngine.Problem;
            }

            online.Stage = 6;
        }

        if (online.Problem is { } failed)
        {
            // A container runs (adopted, or on settings since changed): it keeps serving, but the
            // launcher offers only an engine and GPU that passed their checks. Docker or the GPU
            // are tried again by themselves; a setting needs the person.
            if (failed.Kind == FaultKind.Settings)
            {
                LoseAvailability(failed.Message, (failed.Message, failed.Kind));
                return true;
            }

            if (Once(ref online.Suspended))
            {
                SuspendAvailability(failed.Message);
            }

            // Still registered, offering nothing: the container can be released once idle (after
            // which the checks can download what they need), and it is no one else's to release.
            // Not for one this launcher could not release anyway. A Start the person pressed takes
            // the runner back here too, in one go.
            if (_linkRun is null && (online.TakeOver || !IsTakenOver(settings)) && !BlocksEveryLaunch(settings) &&
                StartLink(settings, online.TakeOver, online.LinkPlan) is null)
            {
                return false;
            }

            return true;
        }

        if (_ownsContainer && BlocksEveryLaunch(settings))
        {
            // Every launch would need this container released first, and this launcher cannot
            // release it. Not registered either until it is gone (a Stop, or its own exit): a
            // launcher that can neither start a container nor release this one would only take the
            // runner from a computer that serves it.
            PauseOffer();
            if (CancelLink())
            {
                return false;
            }

            const string blocked = "A runner container for earlier settings, or from an earlier version of the app, runs here, so no session can start here until it stops. Stop the runner to replace it.";
            Update(view => view with { Message = blocked });
            _retryAttempt = 0;
            return true;
        }

        if (_ownsContainer)
        {
            // Checked: offered again.
            ResumeOffer();
        }

        // The checks above can take long (a first download): settings saved meanwhile, with another
        // address or key, engine or GPU, are checked and offered instead; the old ones are not
        // registered for (Offloadr would send launches this computer then refuses).
        if (Credentials(_settings.Current) != Credentials(settings) || NeedsNewChecks(_settings.Current))
        {
            _requeued = (online.TakeOver, online.Stops);
            return true;
        }

        // A takeover noted while the checks ran counts: only the person's Start takes the runner back.
        if (!online.TakeOver && IsTakenOver(settings))
        {
            LoseAvailability("Another Offloadr Runner app took over this runner, perhaps on another computer. Press Start to offer this computer's GPU again.");
            return true;
        }

        if (StartLink(settings, online.TakeOver, online.LinkPlan) is not { } linked)
        {
            return false;
        }

        if (!linked)
        {
            // Other settings were saved at the last moment: those are checked and offered instead.
            _requeued = (online.TakeOver, online.Stops);
            return true;
        }

        _retryAttempt = 0;
        if (!_ownsContainer)
        {
            SettleIdle();
        }

        return true;
    }

    // The checks before this computer is offered

    private sealed class EnvironmentCheck
    {
        public bool Announced;

        public RememberedCheck Remembered { get; } = new();

        public SupervisorEffects.Remembered? RememberedOutcome { get; set; }

        public Selected? Engine { get; set; }

        public Fact<SupervisorEffects.Probed> Probe { get; } = new();

        public bool ProbeRecorded;

        public Selected? Pinned { get; set; }

        public Fact<SupervisorEffects.NameLook> Named { get; } = new();

        public Adopting? Adopt { get; set; }

        public LaunchedCheck Launched { get; } = new();

        public bool? PlanWanted { get; set; }

        public Fact<SupervisorEffects.PlanAnswer> Plan { get; } = new();

        public GpuCheckStep Gpu { get; } = new();
    }

    /// <summary>
    /// The checks before this computer is offered: Docker answers, no foreign container has the runner's
    /// name, and containers reach the GPU. False when one failed (it faulted, or retries); null while
    /// they are under way.
    /// </summary>
    private bool? StepEnvironment(EnvironmentCheck check, RunnerSettings settings, bool automatic)
    {
        if (Once(ref check.Announced) && View.State is not (SupervisorState.Available or SupervisorState.Running))
        {
            Set(SupervisorState.Checking, "Checking Docker and your GPU…");
        }

        if (check.RememberedOutcome is null)
        {
            if (StepRemembered(check.Remembered) is not { } outcome)
            {
                return null;
            }

            check.RememberedOutcome = outcome;
            switch (outcome)
            {
                case SupervisorEffects.Remembered.Found:
                    return true;
                case SupervisorEffects.Remembered.Unreachable when automatic:
                    FailStart($"{UnreachableEngineMessage()} Press Start to run the runner on the engine in Settings instead.", automatic, FaultKind.EngineUnreachable);
                    return false;
                case SupervisorEffects.Remembered.Unreachable:
                    // The person pressed Start: run on the engine Settings name.
                    AppendLog($"supervisor: {UnreachableEngineMessage()} Starting on the engine in Settings, as asked.");
                    _engineSelection.Forget();
                    break;
            }
        }
        else if (check.RememberedOutcome == SupervisorEffects.Remembered.Found)
        {
            return true;
        }

        if (check.Engine is null)
        {
            // Resolved again only while no container runs, so discovery cannot move a running one to another
            // engine; the engine and context already checked are kept while Settings name the same engine,
            // whatever `docker context use` changed since.
            SelectCheckedEngine(settings);
            check.Engine = _engineSelection.Current;
        }

        var engine = check.Engine;
        if (Need(_main, check.Probe, "probe", token => _fx.ProbeAsync(engine, readContext: true, token)))
        {
            return null;
        }

        var probed = Get(check.Probe);
        if (Once(ref check.ProbeRecorded))
        {
            Update(view => view with { Engine = probed.Engine, HostGpus = probed.Gpus });
            if (probed.Engine.Available)
            {
                _engineCheckedAt = _now;

                // Pinned as soon as the probe identified the engine (Podman installed as `docker`, say), so the
                // checks and the launches reach the same daemon, whatever `docker context use` changes meanwhile.
                if (_engineSelection.Context is null)
                {
                    _engineSelection.PinContext(probed.Pin);
                }
            }
        }

        if (!probed.Engine.Available)
        {
            _checked = _checked with { PassedFor = null };
            FailStart(probed.Engine.Error ?? "Docker is not available.", automatic, probed.Engine.ErrorKind);
            return false;
        }

        if (ModelsFolderProblem(settings, _engineSelection.Pin) is { } modelsProblem)
        {
            Fault(modelsProblem, FaultKind.Settings);
            return false;
        }

        var pinned = check.Pinned ??= _engineSelection.Current;
        var rejected = _rejectedContainerId;
        if (Need(_main, check.Named, "name", token => _fx.LookAtNameAsync(pinned, rejected, NoteRemoved, token)))
        {
            return null;
        }

        var named = Get(check.Named);
        if (named.Foreign)
        {
            FaultNameTaken(named.Holder);
            return false;
        }

        if (named.Holder is { Running: true } left)
        {
            // A runner this app started is still running and was not adopted at startup (Docker did
            // not answer then, say), or was started again as it was removed. It may be serving a
            // session, so it is adopted, not replaced.
            if (StepAdopt(check.Adopt ??= new Adopting(left)) is not { } adopted)
            {
                return null;
            }

            if (adopted)
            {
                return true;
            }
        }

        if (!_ownsContainer || check.Launched.Adopt is not null)
        {
            // One it started runs under another name: adopted, not replaced. Otherwise any it started is gone.
            if (StepLaunched(check.Launched) is not { } adoptedByLaunch)
            {
                return null;
            }

            if (adoptedByLaunch)
            {
                return true;
            }
        }

        var credentials = Credentials(settings);
        check.PlanWanted ??= _planRefreshedAt is null || _planCredentials != credentials || _planRejectedFor == credentials;
        if (check.PlanWanted == true && NeedPlan(check.Plan, settings))
        {
            return null;
        }

        if (_planRejectedFor == credentials)
        {
            // Offloadr refused the key: said now, not after a download of several gigabytes.
            const string refused = "Offloadr did not accept the runner key. Check it in Settings, or create a new one in the Offloadr web app.";
            if (automatic)
            {
                FailStart(refused, automatic, FaultKind.KeyRejected);
            }
            else
            {
                Fault(refused, FaultKind.KeyRejected);
            }

            return false;
        }

        if (StepGpuCheck(check.Gpu, settings, _engineSelection.Current, mayDownload: true) is not { } gpu)
        {
            return null;
        }

        if (gpu.Problem is { } problem)
        {
            if (problem.Kind == FaultKind.NoImageForEditor)
            {
                Fault(problem.Message, problem.Kind);
            }
            else
            {
                FailStart(problem.Message, automatic, problem.Kind);
            }

            return false;
        }

        return true;
    }

    private sealed class Preflight
    {
        public Selected? Target { get; set; }

        public Fact<SupervisorEffects.Probed> Probe { get; } = new();

        public bool ProbeRecorded;

        public GpuCheckStep Gpu { get; } = new();

        public long Id { get; set; }
    }

    /// <summary>
    /// The checks a launch's engine must pass, before a container running now is released for it: it
    /// answers, can mount the models folder, and its containers reach the GPU chosen. Run on that
    /// engine, so a container watched on another one is undisturbed. Brings the engine, pinned
    /// to its context, or why it cannot be used, with what may be told to Offloadr about it; null
    /// while they are under way.
    /// </summary>
    private (Selected Engine, (string Message, FaultKind Kind, string Reported)? Problem)? StepPreflight(Preflight preflight, RunnerSettings settings)
    {
        try
        {
            return StepPreflightCore(preflight, settings);
        }
        catch (ContainerEngineException ex)
        {
            // Docker failing a command (its context, say) is as much a failed check as an answer saying
            // so: the engine cannot be used, whatever its output says the cause is.
            return (_engineSelection.Current, (ex.Message, ex.Kind == FaultKind.Unknown ? FaultKind.EngineNotResponding : ex.Kind, ex.Summary));
        }
        catch (ProcessStartException ex)
        {
            return (_engineSelection.Current, (ex.Message, FaultKind.EngineMissing, "The container engine could not be started."));
        }
    }

    private (Selected Engine, (string Message, FaultKind Kind, string Reported)? Problem)? StepPreflightCore(Preflight preflight, RunnerSettings settings)
    {
        if (preflight.Target is null)
        {
            var configured = _engineSelection.Resolve(settings.ContainerEngineExecutable);
            preflight.Target = _checked.Engine is { } known && known.Configured == configured.Configured && known.Executable == configured.Executable
                ? known
                : configured;
            preflight.Id = ++_ids;
        }

        var probeOn = preflight.Target;
        if (Need(_main, preflight.Probe, $"preflight probe {preflight.Id}", token => _fx.ProbeAsync(probeOn, readContext: true, token)))
        {
            return null;
        }

        var probed = Get(preflight.Probe);
        if (Once(ref preflight.ProbeRecorded))
        {
            Update(view => view with { Engine = probed.Engine, HostGpus = probed.Gpus });
            if (probed.Engine.Available)
            {
                if (preflight.Target.Context is null)
                {
                    // Pinned as the check finds it, so the launch reaches the daemon that was checked.
                    preflight.Target = ContainerEngineSelection.WithContext(preflight.Target, probed.Pin);
                }

                _engineCheckedAt = _now;
            }
        }

        var target = preflight.Target;
        if (!probed.Engine.Available)
        {
            return (target, (probed.Engine.Error ?? "Docker is not available.", probed.Engine.ErrorKind, "Docker is not available on this computer."));
        }

        if (ModelsFolderProblem(settings, target.Context) is { } modelsProblem)
        {
            return (target, (modelsProblem, FaultKind.Settings, modelsProblem));
        }

        if (StepGpuCheck(preflight.Gpu, settings, target, mayDownload: !_ownsContainer) is not { } gpu)
        {
            return null;
        }

        if (gpu.Problem is { } gpuProblem)
        {
            return (target, (gpuProblem.Message, gpuProblem.Kind, gpuProblem.Kind == FaultKind.GpuUnavailable
                ? "Containers on this computer cannot use the GPU."
                : "The runner image for the GPU check could not be prepared."));
        }

        return (target, null);
    }

    private sealed class GpuCheckStep
    {
        public long Id { get; set; }

        public string? Key { get; set; }

        public SupervisorEffects.GpuCheckRequest? Request { get; set; }

        public Fact<SupervisorEffects.GpuChecked> Checked { get; } = new();

        public (bool Done, (string Message, FaultKind Kind)? Problem) Outcome { get; set; }
    }

    /// <summary>
    /// Proves containers of <paramref name="engine"/> reach the GPU, with a runner image this computer
    /// already has (or downloads the one for the editor in Settings). Kept until the engine, its context or
    /// the GPU choice changes. Null while it is under way; otherwise whether they do, or why not.
    /// </summary>
    /// <param name="mayDownload">
    /// False while a container runs: the check waits for an image that is here (or for the container to
    /// be gone), rather than download one beside a container that may be serving.
    /// </param>
    private (bool Done, (string Message, FaultKind Kind)? Problem)? StepGpuCheck(GpuCheckStep step, RunnerSettings settings, Selected engine, bool mayDownload)
    {
        if (step.Outcome.Done)
        {
            return step.Outcome;
        }

        if (step.Key is null)
        {
            step.Id = ++_ids;
            step.Key = $"{engine.Configured}|{engine.Executable}|{PinIdentity(engine.Context)}|{settings.SelectedGpuUuid}";
            if (_in.ResumedSinceGpuCheck)
            {
                // The computer slept since: the GPU may no longer reach containers.
                _in = Change(intent => intent with { ResumedSinceGpuCheck = false });
                _checked = _checked with { PassedFor = null };
            }

            if (string.Equals(_checked.PassedFor, step.Key, StringComparison.Ordinal))
            {
                var (previousEngine, previousGpu) = (_checked.Engine, _checked.Gpu);
                _checked = _checked with { Engine = engine, Gpu = settings.SelectedGpuUuid };
                if (CurrentOffer().Gpus.Gpus.Count > 0)
                {
                    step.Outcome = (true, null);
                    return step.Outcome;
                }

                // Passed, but nothing it could offer now (this computer's list came back empty since, say):
                // checked again, which asks the containers for their GPUs.
                _checked = _checked with { Engine = previousEngine, Gpu = previousGpu, PassedFor = null };
            }

            var plan = View.Plan ?? BuiltInLaunchPlanCatalog.Plan();
            var preferred = plan.Choose(settings);

            // The running container's own image first, on its engine: already here, whatever the catalog names now.
            string[] running = _ownsContainer && _runningImageId is { } runningId && SameEngine(_runningEngine, engine) ? [runningId] : [];
            step.Request = new SupervisorEffects.GpuCheckRequest(
                engine,
                [.. running.Concat(new[] { preferred }.Concat(plan.Templates).OfType<LaunchTemplate>().Select(template => template.Image)).Distinct(StringComparer.Ordinal)],
                preferred,
                mayDownload,
                settings.SelectedGpuUuid,
                View.HostGpus ?? HostGpuReport.None("Not checked yet."));
        }

        var request = step.Request!;
        var key = $"gpu check {step.Id}";

        // What the download shows, while this check is still the one under way: a container running
        // meanwhile keeps its state, and the download shows only in the log.
        bool Showing() => _main.Job is { } job && job.Key == key && !_ownsContainer;
        if (Need(_main, step.Checked, key, token => _fx.CheckGpuAsync(
                request,
                ImageGate,
                label => Post(() =>
                {
                    if (Showing())
                    {
                        Update(view => view with { LaunchingEditor = null });
                        Set(SupervisorState.Pulling, $"Downloading {label} to check the GPU. The first download is large and can take a while.");
                    }
                }),
                progress => Post(() =>
                {
                    if (Showing())
                    {
                        Update(view => view.State == SupervisorState.Pulling ? view with { PullProgress = progress } : view);
                    }
                }),
                () => Post(() =>
                {
                    if (Showing())
                    {
                        Set(SupervisorState.Checking, "Checking that containers can use the GPU…");
                    }
                }),
                token)))
        {
            return null;
        }

        var found = Get(step.Checked);
        if (found.Check is { } check)
        {
            Update(view => view with { GpuAccess = check });
        }

        if (found.Problem is { } problem)
        {
            if (found.GpuLost && SameEngine(_checked.Engine, engine) &&
                (string.Equals(_checked.Gpu, settings.SelectedGpuUuid, StringComparison.OrdinalIgnoreCase) || found.Check is { Succeeded: false }))
            {
                // What was offered is not there, for any launch (an engine that did not answer says nothing of it;
                // nor does another engine, or another GPU chosen since, of the one that was offered).
                _checked = _checked with { GpuDisproved = true };
            }

            step.Outcome = (true, (problem, found.Kind));
            return step.Outcome;
        }

        _checked = _checked with
        {
            GpuDisproved = false,
            PassedFor = step.Key,
            Engine = engine,
            Gpu = settings.SelectedGpuUuid,
            Output = found.Check!.Output,
            Inventory = found.Inventory,
        };
        _gpuProvedImages.Clear();
        if (found.ImageId is { Length: > 0 } provedImage)
        {
            _gpuProvedImages.Add(provedImage);
        }

        step.Outcome = (true, null);
        return step.Outcome;
    }

    // Looking at the engine by itself

    private sealed class EngineLook(bool whileAvailable)
    {
        /// <summary>While available with no container; otherwise a Refresh while stopped, which only shows what it finds.</summary>
        public bool WhileAvailable { get; } = whileAvailable;

        public Selected? Engine { get; set; }

        public Fact<SupervisorEffects.Probed> Probe { get; } = new();

        public Fact<ContainerEngineInfo> Answers { get; } = new();
    }

    /// <summary>
    /// While available with no container, Docker must still answer: a computer that could not start a
    /// launch stops being offered, and is offered again once Docker is back. While stopped, a Refresh
    /// shows what Docker and the GPU list say now.
    /// </summary>
    private bool StepEngineLook(EngineLook look)
    {
        if (look.Engine is null)
        {
            if (look.WhileAvailable)
            {
                _engineCheckedAt = _now;
                if (_checked.Engine is { } checkedEngine)
                {
                    // The engine and context launches go to, not wherever `docker context use` points now.
                    _engineSelection.SelectExactly(checkedEngine);
                }
            }
            else if (!_ownsContainer)
            {
                _engineSelection.Select(_settings.Current.ContainerEngineExecutable);
            }

            look.Engine = _engineSelection.Current;
        }

        if (_in.Stops > _stopsDone || View.State is SupervisorState.Stopping)
        {
            return !Cancel(_main);
        }

        var engine = look.Engine;
        if (!look.WhileAvailable)
        {
            if (Need(_main, look.Probe, "engine look", token => _fx.ProbeAsync(engine, readContext: false, token)))
            {
                return false;
            }

            var shown = Get(look.Probe);
            Update(view => view with { Engine = shown.Engine, HostGpus = shown.Gpus });
            return true;
        }

        if (Need(_main, look.Answers, "engine answers", token => _fx.ProbeEngineAsync(engine, token)))
        {
            return false;
        }

        if (View.State != SupervisorState.Available)
        {
            return true;
        }

        if (look.Answers.Error is { } failure && failure is ContainerEngineException or ProcessStartException)
        {
            // Docker coming back proves nothing about the GPU: it is checked again first.
            _checked = _checked with { PassedFor = null };
            FailStart(failure, automatic: true);
            return true;
        }

        var answer = Get(look.Answers);
        Update(view => view with { Engine = answer });
        if (!answer.Available)
        {
            _checked = _checked with { PassedFor = null };
            FailStart(answer.Error ?? "Docker is not available.", automatic: true, answer.ErrorKind);
        }

        return true;
    }
}
