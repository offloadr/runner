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
/// A launch: from the launch Offloadr names to its container running, or its outcome. Run again from
/// its top on every tick (see the class remarks): each check below is made against that tick's inputs.
/// </content>
public sealed partial class RunnerSupervisor
{
    private readonly Slot _flightDeliverySlot = new("launch outcome");

    private const string StaleOfferMessage = "This computer's GPU or container engine changed after Offloadr chose it. Start the session again.";

    /// <summary>A launch being prepared or started.</summary>
    private sealed class Flight(LaunchRequest launch, long stops, (string Origin, string Key)? linkCredentials, long id) : IDisposable
    {
        public LaunchRequest Launch { get; } = launch;

        public long Id { get; } = id;

        /// <summary>The Stops asked when it was decided: a Stop since ends it, and it reports nothing.</summary>
        public long Stops { get; } = stops;

        /// <summary>The address and key of the launcher that was sent it.</summary>
        public (string Origin, string Key)? LinkCredentials { get; } = linkCredentials;

        /// <summary>
        /// Cancelled when a Stop or shutdown ends the launch: an outcome not yet submitted then is not
        /// recorded at all, so nothing is acknowledged for a container about to be removed.
        /// </summary>
        public CancellationTokenSource StopSignal { get; } = new();

        /// <summary>Raises <see cref="StopSignal"/> on the thread that asks for the Stop or closes the app.</summary>
        public Fence.Watch? StopWatch { get; set; }

        /// <summary>Cancelled when the launch is withdrawn: a release not sent yet is not sent.</summary>
        public CancellationTokenSource Preparation { get; } = new();

        /// <summary>The latest launcher state no longer names it, or the settings no longer fit it.</summary>
        public bool Withdrawn { get; set; }

        /// <summary>What to report for the withdrawal; null when nothing can or should be reported.</summary>
        public RunnerLaunchPhase? Phase { get; set; }

        /// <summary>Why, for a withdrawal reported as failed.</summary>
        public string? Message { get; set; }

        public int Stage { get; set; }

        public string Label { get; set; } = launch.EditorTemplate;

        public string? Target { get; set; }

        public LaunchTemplate? Template { get; set; }

        public RunnerSettings? Settings { get; set; }

        /// <summary>The GPU chosen when the launch began; another one named since is checked before any launch runs on it.</summary>
        public string? CheckedGpu { get; set; }

        public Preflight Preflight { get; } = new();

        public Selected? Engine { get; set; }

        public Fact<IDisposable> ImageUse { get; } = new();

        public bool PullAnnounced;

        public Fact<SupervisorEffects.Pulled> Pull { get; } = new();

        public bool PullRecorded;

        public ImageInfo? Image { get; set; }

        public bool? Relist { get; set; }

        public GpuListing Listed { get; } = new();

        public bool ListedRecorded;

        public bool? Replacing { get; set; }

        public Room Room { get; } = new();

        public bool EngineSelected;

        public Fact<SupervisorEffects.NameCleared> Name { get; } = new();

        public ContainerSpec? Spec { get; set; }

        public string? AttemptId { get; set; }

        public Starting Start { get; set; } = new();

        public bool CreateAsked;

        /// <summary>A failure not submitted yet: what became of the container that was created is not known.</summary>
        public LaunchReport? HeldFailure { get; set; }

        public bool CreateRecorded;

        /// <summary>The container created for it, by ID, or why none was.</summary>
        public Fact<SupervisorEffects.Created> Created { get; } = new();

        public Fact<SupervisorEffects.CreatedLeft> CreateLeft { get; } = new();

        public Fact<SupervisorEffects.CreatedLeft> Discarded { get; } = new();

        /// <summary>Its container's start was spawned: from then on it is answered after the start, whatever is withdrawn.</summary>
        public bool Dispatched { get; set; }

        public Fact<bool> Run { get; } = new();

        /// <summary>The start was held back before the engine was asked at all: it did not happen.</summary>
        public bool StartPrevented { get; set; }

        public bool RunTimedOut { get; set; }

        public Fact<SupervisorEffects.FailedLaunchLeft> Left { get; } = new();

        public bool LeftRecorded;

        public bool Started { get; set; }

        public RunnerLaunchPhase? Retired { get; set; }

        public Fact<string?> StartedImage { get; } = new();

        /// <summary>How it ends, once that is decided: what is still waited for, and what is done then.</summary>
        public Ending? End { get; set; }

        public void Dispose()
        {
            // The signals are left to the collector: a report or a release handed one may still read it.
            StopWatch?.Dispose();
            if (ImageUse.Value is { } use)
            {
                use.Dispose();
            }

            Room.Ask.Unless?.Dispose();
            Room.Ask.OfferWatch?.Dispose();
        }
    }

    /// <summary>The last checks before the start, made again each time the launch waited for the stream.</summary>
    private sealed class Starting
    {
        public long Id { get; set; }

        /// <summary>How often the computer had woken when these checks began: once more since, and they are made again.</summary>
        public long Resumes { get; set; }

        /// <summary>How often the stream had dropped when these checks began: once more since, and they are made again.</summary>
        public long StreamDrops { get; set; }

        public GpuCheckStep? SleptGpu { get; set; }

        /// <summary>Whether containers of the launch's own image reach the GPU, asked when they listed none.</summary>
        public Fact<SupervisorEffects.GpuChecked> ImageGpu { get; } = new();

        public bool? Relist { get; set; }

        public GpuListing Listed { get; } = new();

        public bool ListedRecorded;

        public Fact<HostGpuReport> Host { get; } = new();

        public bool HostRecorded;

        public bool HostChanged;

        public bool HostUnread;
    }

    /// <summary>The GPUs containers of the launch's image get, as they list them: asked again while they list none.</summary>
    private sealed class GpuListing
    {
        public Fact<IReadOnlyList<HostGpu>> Listed { get; set; } = new();

        public Fact<SupervisorEffects.GpuChecked> ImageGpu { get; } = new();

        public int Tries { get; set; }

        public TimeSpan? RetryAt { get; set; }
    }

    private const int GpuListingTries = 3;

    /// <summary>Making room for a launch: the release of the container that runs now.</summary>
    private sealed class Room
    {
        public int Stage { get; set; }

        public GpuCheckStep? SleptGpu { get; set; }

        public Fact<ContainerInfo?> Named { get; set; } = new();

        /// <summary>How often the name was looked at (again after the stream dropped): each look is its own.</summary>
        public int NameLooks { get; set; }

        /// <summary>How often the computer had woken when the checks before a request began (the name's among them).</summary>
        public long Resumes { get; set; }

        /// <summary>How often the stream had dropped when the checks before a request began (the name's among them).</summary>
        public long StreamDrops { get; set; }

        public bool? SentUnanswered { get; set; }

        public Asking Ask { get; set; } = new();

        /// <summary>A release was sent, or the stop of a released container began: finished whatever is withdrawn.</summary>
        public bool Committed { get; set; }

        public StopOfReleased? Stop { get; set; }

        public TimeSpan? RetryAt { get; set; }
    }

    /// <summary>One request for the release, with the checks made right before it.</summary>
    private sealed class Asking
    {
        public long Id { get; set; }

        /// <summary>How often the computer had woken when this request's checks began.</summary>
        public long Resumes { get; set; }

        /// <summary>How often the stream had dropped when this request's checks began.</summary>
        public long StreamDrops { get; set; }

        public Fact<RunnerStatusSnapshot?> Status { get; } = new();

        public bool StatusRecorded;

        public Fact<ImageInfo?> Image { get; } = new();

        public GpuCheckStep? SleptGpu { get; set; }

        /// <summary>Whether containers of the replacement's own image reach the GPU.</summary>
        public Fact<SupervisorEffects.GpuChecked> ImageGpu { get; } = new();

        public bool? Relist { get; set; }

        public GpuListing Listed { get; } = new();

        public bool ListedRecorded;

        public Fact<HostGpuReport> Host { get; set; } = new();

        public bool HostRecorded;

        public bool HostChanged;

        public bool HostUnread;

        public bool Noted;

        /// <summary>What keeps the request from being sent (a withdrawal, another key): owned by this request.</summary>
        public CancellationTokenSource? Unless { get; set; }

        /// <summary>The settings as they were checked for this request.</summary>
        public RunnerSettings? Settings { get; set; }

        /// <summary>Raised as soon as other settings are saved than the ones checked.</summary>
        public Fence.Watch? OfferWatch { get; set; }

        public Fact<ReleaseResult?> Release { get; } = new();
    }

    /// <summary>How a launch ends: an outcome that gets a moment to reach Offloadr first, then what follows.</summary>
    private sealed class Ending(LaunchReport? deliver, Action? then)
    {
        public LaunchReport? Deliver { get; } = deliver;

        public Action? Then { get; } = then;

        public Fact<bool> Delivered { get; } = new();

        /// <summary>Waits until Offloadr accepted the current offer, or this long, before a launch chosen for an older one is answered.</summary>
        public TimeSpan? StaleUntil { get; set; }
    }

    private static Ending Ended => new(null, null);

    /// <summary>True when a Stop or shutdown ends <paramref name="flight"/>: nothing is reported for it.</summary>
    private bool Stopped(Flight flight) => _in.Stops != flight.Stops || !_in.Wanted || _stopping.IsCancellationRequested;

    // Withdrawal

    /// <summary>
    /// Withdraws the launch in flight when <paramref name="state"/> names another launch, or none. A run
    /// that ended (availability off, new settings) has no state to report the withdrawal to.
    /// </summary>
    private void WithdrawIfNotNamed(Flight flight, LauncherLinkState state)
    {
        if (flight.Withdrawn || string.Equals(state.Launch?.LaunchId, flight.Launch.LaunchId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Withdraw(
            flight,
            state.Connection == LauncherConnection.Offline || state.IsFinal
                ? null
                : state.Launch is { } newer && newer.Sequence > flight.Launch.Sequence
                    ? RunnerLaunchPhase.Superseded
                    : RunnerLaunchPhase.Cancelled);
    }

    /// <summary>Withdraws <paramref name="flight"/>, to be reported as <paramref name="phase"/> (null: not reported).</summary>
    private static void Withdraw(Flight flight, RunnerLaunchPhase? phase, string? message = null)
    {
        flight.Withdrawn = true;
        flight.Phase = phase;
        flight.Message = message;
        flight.Preparation.Cancel();
    }

    // Reports

    private void Report(Flight flight, RunnerLaunchPhase phase, string? message = null)
        => Submit(flight, new LaunchReport(flight.Launch, phase, message));

    /// <summary>
    /// Submits a report of <paramref name="flight"/>. A failure is kept until what became of a container the
    /// launch created is known: someone else may have started it, and one that ran and exited is told as an
    /// exit alone, which lets the allocation launch again where FAILED would fail it.
    /// </summary>
    private void Submit(Flight flight, LaunchReport report)
    {
        if (report.Phase == RunnerLaunchPhase.Failed && flight.CreateAsked && !flight.Dispatched &&
            ((_ownsContainer && string.Equals(_attemptId, flight.AttemptId, StringComparison.Ordinal)) || CreatedIsUnknown(flight)))
        {
            flight.HeldFailure ??= report;
            return;
        }

        if (!report.IsTerminal)
        {
            // In a lane of its own, in order: the loop waits for no send. (An outcome is recorded by the link
            // at once, under the fence, and delivered by it; progress that comes after it is dropped there.)
            _progress.Enqueue((report, ReportSignal(flight)));
            WorkOffProgress();
            return;
        }

        _fx.Report(report, ReportSignal(flight));
    }

    private readonly Slot _progressSlot = new("launch progress");
    private readonly Queue<(LaunchReport Report, CancellationToken Unless)> _progress = new();

    private void WorkOffProgress()
    {
        if (_progressSlot.Busy || !_progress.TryDequeue(out var next))
        {
            return;
        }

        Start(_progressSlot, "progress", async _ =>
        {
            await _fx.ReportProgressAsync(next.Report, next.Unless).ConfigureAwait(false);
            return true;
        }, _ => WorkOffProgress());
    }

    /// <summary>The engine did not say what became of the container the launch created (or asked to).</summary>
    private static bool CreatedIsUnknown(Flight flight)
        => flight.Discarded is { Error: not null } or { Value.Unanswered: true } || flight.CreateLeft is { Error: not null } or { Value.Unanswered: true };

    /// <summary>Submits the failure kept while the launch's container was removed, unless that container ran after all.</summary>
    private void SubmitHeldFailure(Flight flight)
    {
        if (flight.HeldFailure is not { } held)
        {
            return;
        }

        flight.HeldFailure = null;
        if (flight.Discarded.Value is { Running: not null } || _exitedLaunches.Contains(flight.Launch.LaunchId))
        {
            return;
        }

        if (CreatedIsUnknown(flight) && _launched.ContainsKey(flight.Launch.LaunchId))
        {
            // The engine does not say. Kept with the launch's record, until a look settles that.
            _heldFailures[flight.Launch.LaunchId] = (held, flight.Stops);
            return;
        }

        _fx.Report(held, ReportSignal(flight));
    }

    /// <summary>Launches that failed and whose created container the engine did not describe: submitted once it is known that it never ran.</summary>
    private readonly Dictionary<string, (LaunchReport Report, long Stops)> _heldFailures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Submits each failure kept until its launch's record was settled, or drops it: the container ran, or a Stop came.</summary>
    private void WorkOffHeldFailures()
    {
        if (_heldFailures.Count == 0)
        {
            return;
        }

        foreach (var (launchId, (report, stops)) in _heldFailures.ToList())
        {
            if (_in.Stops != stops || !_in.Wanted || _exitedLaunches.Contains(launchId) ||
                (_ownsContainer && string.Equals(launchId, _containerLaunchId, StringComparison.OrdinalIgnoreCase)) ||
                (_launched.TryGetValue(launchId, out var record) && record.Gone))
            {
                _heldFailures.Remove(launchId);
            }
            else if (!_launched.ContainsKey(launchId))
            {
                // Settled, as one that never ran.
                _heldFailures.Remove(launchId);
                _fx.Report(report, StopFence());
            }
        }
    }

    /// <summary>
    /// The signal a report of <paramref name="flight"/> is submitted under, raised first if a Stop or
    /// shutdown was asked since this tick began: read at the submission itself, so nothing is
    /// acknowledged for a container that is about to be removed.
    /// </summary>
    /// <summary>
    /// The signal an outcome is submitted under when no launch is in flight for it (one named again, or
    /// settled late by the watch): raised, on the thread that asks, as soon as a Stop or shutdown is asked
    /// that this loop has not carried out. Read at the submission itself, as <see cref="ReportSignal"/> is.
    /// </summary>
    private CancellationToken StopFence()
    {
        var done = _stopsDone;
        if (_stopWatch is not { Done: false } || _stopWatched != done)
        {
            _stopWatch?.Dispose();
            _stopWatched = done;
            _stopWatch = _fence.Until(() =>
            {
                var asked = Volatile.Read(ref _intent);
                return asked.Stops != done || !asked.Wanted || _stopping.IsCancellationRequested;
            });
        }

        return _stopWatch.Signal;
    }

    private Fence.Watch? _stopWatch;
    private long _stopWatched;

    private CancellationToken ReportSignal(Flight flight)
    {
        var asked = Volatile.Read(ref _intent);
        if (asked.Stops != flight.Stops || !asked.Wanted || _stopping.IsCancellationRequested)
        {
            flight.StopSignal.Cancel();
        }

        return flight.StopSignal.Token;
    }

    /// <summary>Reports that the launch failed and shows why; the computer stays available. Returns the outcome submitted.</summary>
    /// <param name="message">Shown in the app and its log.</param>
    /// <param name="reported">
    /// What Offloadr is told, when <paramref name="message"/> may hold what must not leave this computer:
    /// an engine's own output (a signed URL, say) or a local path. Null: the message itself, written here.
    /// </param>
    private LaunchReport FailLaunch(LaunchRequest launch, CancellationToken stopSignal, string message, FaultKind kind = FaultKind.Unknown, string? reported = null)
        => FailLaunch(launch, message, kind, reported, outcome => _fx.Report(outcome, stopSignal));

    private LaunchReport FailLaunch(LaunchRequest launch, string message, FaultKind kind, string? reported, Action<LaunchReport> submit)
    {
        _logger.LogWarning("Launch {LaunchId} failed ({Kind}).", launch.LaunchId, kind);
        AppendLog($"supervisor: could not start {launch.EditorTemplate} for a session: {message}");

        // Not submitted at all once a Stop or shutdown cancelled the launch: that is no outcome Offloadr asked for.
        var outcome = new LaunchReport(launch, RunnerLaunchPhase.Failed, reported ?? message);
        submit(outcome);
        ShowWithoutLaunch();
        Update(view => view with
        {
            Message = $"Could not start {launch.EditorTemplate} for a session: {message}",
            LaunchFailure = SupervisorFault.For(kind, message, view.Engine?.Kind ?? ContainerEngineKind.Docker),
        });
        return outcome;
    }

    private LaunchReport FailLaunch(Flight flight, string message, FaultKind kind = FaultKind.Unknown, string? reported = null)
        => FailLaunch(flight.Launch, message, kind, reported, outcome => Submit(flight, outcome));

    /// <summary>What the view shows once no launch is in progress: the container that runs, or the idle state.</summary>
    private void ShowWithoutLaunch()
    {
        if (_ownsContainer)
        {
            Set(SupervisorState.Running, View.Message);
        }
        else
        {
            SettleIdle();
        }
    }

    // Looking at the launch Offloadr names

    /// <summary>
    /// Starts the launch Offloadr names now, if there is one this computer has not handled yet. True
    /// when a launch is in flight from now on.
    /// </summary>
    private bool LookAtLaunch()
    {
        if (_in.UpdateHold || !_in.Wanted || _in.Stops > _stopsDone || _linkRun is null || View.State is not (SupervisorState.Available or SupervisorState.Running))
        {
            return false;
        }

        var state = _link.State;
        if (state.Launch is not { } launch || _link.OutcomeOf(launch.LaunchId) is not null)
        {
            // Nothing named, or already answered.
            return false;
        }

        if (_ownsContainer && string.Equals(_containerLaunchId, launch.LaunchId, StringComparison.OrdinalIgnoreCase))
        {
            // Its container already runs (one a cancelled start left, watched again since): it is not
            // started twice, but its outcome is still owed, and Offloadr keeps naming it until then. Not
            // while the engine has not said whether it started at all: that settles its outcome first.
            if (View.State == SupervisorState.Running && _unsettledLaunch is null)
            {
                _fx.Report(new LaunchReport(launch, RunnerLaunchPhase.Started), StopFence());
            }

            return false;
        }

        if (_heldFailures.ContainsKey(launch.LaunchId))
        {
            // It failed, and what became of the container it created is not known yet: answered then.
            _lookAgainAt = _now + (_options.MonitorInterval * 10);
            return false;
        }

        if (_dispatched.Contains(launch.LaunchId) || _exitedLaunches.Contains(launch.LaunchId) ||
            (_launched.TryGetValue(launch.LaunchId, out var noted) && !noted.Pending))
        {
            if (_launched.TryGetValue(launch.LaunchId, out var record))
            {
                // Its container ran and is gone, and Offloadr is being told so: that report retires the
                // launch and lets its allocation launch again, where a FAILED sent first would fail it.
                // Or what became of it is not known yet (its engine does not answer, and the person moved
                // on to another): it may still run, and nothing is said of its launch until that engine does.
                _lookAgainAt = _now + (_options.MonitorInterval * 10);
                return false;
            }

            if (_exitedLaunches.Contains(launch.LaunchId))
            {
                // It started, and exited by itself, and Offloadr took that report. Still named, its
                // executor had matched it, and it stays named until this launcher's terminal report: that
                // it started is the true one. FAILED would fail an allocation over a container that ran.
                _fx.Report(new LaunchReport(launch, RunnerLaunchPhase.Started), StopFence());
                return false;
            }

            // Its start was asked for once, and its container never ran (it was removed, created, before
            // its outcome went out). Never started twice with the same launch id: answered, so Offloadr
            // launches anew. (One that ran, and that a Stop removed, is answered as started above.)
            FailLaunch(launch, StopFence(), "The runner container started for this launch was stopped before it could be used.");
            return false;
        }

        if (_link.LaunchTarget is { } offeredTarget && launch.OfferRevision != OfferRevision(offeredTarget, OfferNow().Signature))
        {
            // Chosen for a GPU or container engine this computer no longer offers. Offloadr retires it once
            // it accepts a heartbeat with the current offer, and offers the allocation a new launch: a FAILED
            // sent before that would fail the allocation instead. So it is answered only once that heartbeat
            // was accepted (and the launch is still named), and looked at again meanwhile.
            if (_link.AcceptedOfferRevision >= OfferRevision(offeredTarget, OfferNow().Signature))
            {
                FailLaunch(launch, StopFence(), StaleOfferMessage);
            }
            else
            {
                _lookAgainAt = _now + (_options.MonitorInterval * 10);
            }

            return false;
        }

        if (_checked.OfferNothing)
        {
            // Named on an offer from before the pause: answered at once, so Offloadr looks elsewhere.
            FailLaunch(launch, StopFence(), !BlocksEveryLaunch(_settings.Current)
                ? "This computer is not offering launches right now."
                : _containerLaunchId is null ? EarlierAppContainer : EarlierSettingsContainer);
            return false;
        }

        var flight = _flight = new Flight(launch, _in.Stops, LinkCredentials, ++_ids);

        // A Stop or the app closing reaches its reports at once, on the thread that asks: one still
        // waiting to be submitted then (the link is busy) is not recorded after it.
        var stops = flight.Stops;
        flight.StopWatch = _fence.Until(() =>
        {
            var asked = Volatile.Read(ref _intent);
            return asked.Stops != stops || !asked.Wanted || _stopping.IsCancellationRequested;
        });
        flight.StopWatch.Signal.Register(static signal => ((CancellationTokenSource)signal!).Cancel(), flight.StopSignal);

        // A state that withdrew it before it was in flight here is applied now.
        WithdrawIfNotNamed(_flight, _link.State);
        return true;
    }

    // The launch

    /// <summary>
    /// Starts the container for a launch: checks the engine it runs on, downloads the image, makes room
    /// (a warm container of another launch is released and stopped first), waits until the launch may
    /// start, runs it and reports the outcome. A newer launch, a withdrawal or a Stop ends it. True
    /// once it is over.
    /// </summary>
    private bool StepFlight(Flight flight)
    {
        if (Stopped(flight))
        {
            // A Stop (or shutdown) ends everything, and no outcome is the app's to report: the Stop that
            // follows cleans up, also what a creation under way still makes.
            flight.StopSignal.Cancel();
            flight.Preparation.Cancel();
            Cancel(_flightDeliverySlot);
            if (LooksForItsContainer(flight))
            {
                // The look after a failed start, or for what a creation left, removes what it finds stopped: it
                // is let finish, so that what it removed, and whether that ever started, is known to the Stop
                // that follows.
                return false;
            }

            if (Cancel(_main))
            {
                return false;
            }

            // A start the engine refused did not happen, also when the Stop comes before the look after
            // it: the container the Stop then finds created never ran.
            NoteStartAnswer(flight);
            if (flight.Left is { Known: true, Error: null, Value: { } found })
            {
                // Found before the Stop and not acted on yet: a container it removed is accounted for
                // as what it was (never started, or exited), not as one the Stop cannot find.
                LetGoOfLeft(flight, found);
            }

            LetGoOfCreatedFound(flight);
            ContainerBusy(false);
            if (_in.Stops > _stopsDone)
            {
                Set(SupervisorState.Stopping, "Stopping the runner…");
            }
            else if (!_stopping.IsCancellationRequested)
            {
                ShowWithoutLaunch();
            }

            return true;
        }

        try
        {
            while (true)
            {
                if (flight.End is { } end)
                {
                    // Ending without having started its container: what it created goes first.
                    if (!DiscardCreated(flight))
                    {
                        return false;
                    }

                    SubmitHeldFailure(flight);
                    return StepEnding(flight, end);
                }

                // A withdrawal ends only the preparation: never a start under way, nor a release
                // that was sent or the stop of a container Offloadr released.
                if (Interruptible(flight))
                {
                    if (Cancel(_main))
                    {
                        return false;
                    }

                    EndWithdrawn(flight);
                    continue;
                }

                var over = StepFlightCore(flight);
                if (flight.End is not null)
                {
                    continue;
                }

                if (over)
                {
                    flight.End = Ended;
                    continue;
                }

                if (Interruptible(flight))
                {
                    // The step itself came to where the withdrawal counts (a release it had sent was answered).
                    continue;
                }

                return false;
            }
        }
        catch (Exception ex) when (ex is ContainerEngineException or ProcessStartException)
        {
            Cancel(_main);
            ContainerBusy(false);
            if (flight.Withdrawn)
            {
                // Withdrawn meanwhile: that is the launch's outcome, whatever Docker said after.
                AppendLog($"supervisor: {ex.Message}");
                if (flight.Phase is { } phase)
                {
                    Report(flight, phase, flight.Message);
                }

                ShowWithoutLaunch();
                flight.End = Ended;
                _lookAgain = true;
                return false;
            }

            var kind = ex is ProcessStartException ? FaultKind.EngineMissing : KindOf(ex);
            var failed = FailLaunch(flight, ex.Message, kind, ex is ContainerEngineException engineFailure ? engineFailure.Summary : "The container engine could not be started.");

            // Docker went away after the checks (during the download, say): not offered meanwhile. Its end
            // is taken up on the next tick, where what it created (if anything) is removed first.
            flight.End = LeavesNothingLaunchable(kind) ? StopOfferingAfter(failed, ex.Message, kind) : Ended;
            _lookAgain = true;
            return false;
        }
    }

    /// <summary>True when a withdrawal ends the launch now: nothing was started for it, and nothing it sent is unanswered.</summary>
    private static bool Interruptible(Flight flight) => flight.Withdrawn && !flight.Dispatched && !flight.Room.Committed;

    /// <summary>Offloadr no longer wants it (or the settings no longer fit it), and nothing was started for it.</summary>
    private void EndWithdrawn(Flight flight)
    {
        AppendLog(flight.Message is { } why
            ? $"supervisor: stopped the launch of {flight.Label}: {why}"
            : $"supervisor: Offloadr withdrew the launch of {flight.Label}.");
        LaunchReport? withdrawn = null;
        if (flight.Phase is { } phase)
        {
            withdrawn = new LaunchReport(flight.Launch, phase, flight.Message);
            Submit(flight, withdrawn);
        }
        else if (flight.Message is not null)
        {
            // Dropped for another engine or GPU: they are checked and offered, and the launch is looked
            // at again once Offloadr may have retired it.
            _refreshQueued = true;
            _lookAgainAt = _now + (_options.MonitorInterval * 10);
        }

        // New settings will replace the launcher that delivers a failure: it gets a moment first.
        flight.End = new Ending(flight.Phase == RunnerLaunchPhase.Failed ? withdrawn : null, ShowWithoutLaunch);
    }

    /// <summary>Finishes a launch whose end is decided: waits out what it still waits for, then does what follows.</summary>
    private bool StepEnding(Flight flight, Ending end)
    {
        if (end.StaleUntil is { } until)
        {
            // Answered once Offloadr accepted a heartbeat with the current offer (which retires it: a
            // withdrawal then ends this flight as usual), or after the wait.
            if (flight.Withdrawn)
            {
                flight.End = null;
                EndWithdrawn(flight);
                return StepEnding(flight, flight.End!);
            }

            var accepted = _link.LaunchTarget is not { } target || _link.AcceptedOfferRevision >= OfferRevision(target, OfferNow().Signature);
            if (!accepted && _now < until)
            {
                WakeIn(_options.MonitorInterval);
                return false;
            }

            if (_link.LaunchTarget is { } current && _link.AcceptedOfferRevision >= OfferRevision(current, OfferNow().Signature))
            {
                // Accepted, and still named (or this would have been withdrawn): it is answered now.
                FailLaunch(flight, StaleOfferMessage);
            }
            else
            {
                // Not accepted in time (the launcher may be offline): left without an outcome, and looked at
                // again later, as a FAILED now would fail the allocation. Ready again meanwhile, so a launch
                // Offloadr names against the new offer is started.
                ShowWithoutLaunch();
                _lookAgainAt = _now + (_options.MonitorInterval * 10);
            }

            return true;
        }

        if (end.Deliver is { } outcome && !flight.StopSignal.IsCancellationRequested &&
            Need(_flightDeliverySlot, end.Delivered, $"deliver {flight.Id}", token => _fx.DeliverAsync(outcome, _options.StopReleaseTimeout, token)))
        {
            // A bounded wait for an outcome already submitted to reach Offloadr, before the launcher that
            // delivers it stops: otherwise an allocation would wait out its timeout for a known failure.
            return false;
        }

        end.Then?.Invoke();
        return true;
    }

    /// <summary>
    /// A launch found that this computer cannot launch: its failure gets a moment to reach Offloadr, then
    /// the computer stops being offered. Docker or the GPU are tried again by themselves (a running
    /// container keeps serving meanwhile); settings that cannot be used need the person.
    /// </summary>
    private Ending StopOfferingAfter(LaunchReport failed, string message, FaultKind kind)
        => new(failed, () =>
        {
            if (kind is FaultKind.Settings or FaultKind.NameTaken)
            {
                // The launcher stays, offering nothing, until that outcome is delivered: ending it now would
                // leave the allocation to time out instead of learning why.
                LoseAvailability(message, (message, kind), deliverFirst: failed);
                return;
            }

            _checked = _checked with { PassedFor = null };
            if (_ownsContainer)
            {
                SuspendAvailability(message);
            }
            else
            {
                // Retried by itself; the launcher stays, offering nothing, until that outcome is delivered.
                FailStart(message, automatic: true, kind, deliverFirst: failed);
            }
        });

    /// <summary>
    /// Fails a launch in flight that was chosen for an older offer, once Offloadr accepted a heartbeat with
    /// the current one, or after <see cref="SupervisorOptions.StaleOfferWait"/>. A FAILED sent before that
    /// heartbeat would fail the allocation rather than let it launch again.
    /// </summary>
    private Ending FailStale() => new(null, null) { StaleUntil = _now + _options.StaleOfferWait };

    /// <summary>
    /// Leaves a launch that settings saved since moved to other hardware, without an outcome: the new engine
    /// or GPU is checked and offered, Offloadr retires the launch once it accepts that offer and gives the
    /// allocation a new one; a FAILED sent first would fail the allocation instead.
    /// </summary>
    private Ending DropForOfferChange(Flight flight, string why)
    {
        AppendLog($"supervisor: stopped the launch of {flight.Label}: {why}");
        ShowWithoutLaunch();
        _refreshQueued = true;
        _lookAgainAt = _now + (_options.MonitorInterval * 10);
        return Ended;
    }

    /// <summary>
    /// Ends a launch whose container is no longer the one Settings ask for, found right before it is
    /// created or started. Another engine or GPU is other hardware than Offloadr chose this computer for:
    /// the launch is dropped without an outcome, as when that is saved earlier (Offloadr retires it once
    /// it accepts the new offer, and gives the allocation a new one). Anything else fails the launch.
    /// </summary>
    private void EndForChangedSettings(Flight flight)
    {
        var current = _settings.Current;
        if (Credentials(current) == flight.LinkCredentials &&
            (!string.Equals(flight.Engine!.Configured, ContainerEngineSelection.Normalize(current.ContainerEngineExecutable), StringComparison.Ordinal) ||
             !string.Equals(current.SelectedGpuUuid, flight.CheckedGpu, StringComparison.OrdinalIgnoreCase)))
        {
            flight.End = DropForOfferChange(flight, "The container engine or GPU changed in the app's settings.");
            return;
        }

        FailLaunch(flight, "The app's settings changed while the launch waited for Offloadr.");
        _refreshQueued = true;
        flight.End = Ended;
    }

    /// <summary>
    /// The settings now, if this launch may still run on them: valid, with the key and address of
    /// the launcher that was sent the launch, and the engine and GPU checked for it. Otherwise the
    /// launch's end is set, and null.
    /// </summary>
    private RunnerSettings? SettingsStillHold(Flight flight)
    {
        var current = _settings.Current;
        if (current.Validate() is { Count: > 0 } changedProblems)
        {
            // A models folder gone (a drive removed, say) raises no settings change: not offered on.
            var unusable = FailLaunch(flight, $"The runner's settings need attention: {changedProblems[0]}", FaultKind.Settings, SettingsReported);
            flight.End = StopOfferingAfter(unusable, changedProblems[0], FaultKind.Settings);
            return null;
        }

        if (Credentials(current) != flight.LinkCredentials)
        {
            FailLaunch(flight, "The Offloadr address or runner key changed in the app's settings.");
            flight.End = Ended;
            return null;
        }

        var engine = flight.Engine!;
        if (!string.Equals(engine.Configured, ContainerEngineSelection.Normalize(current.ContainerEngineExecutable), StringComparison.Ordinal))
        {
            // The engine checked above is not the one Settings name now: it is checked first.
            flight.End = DropForOfferChange(flight, "The container engine changed in the app's settings.");
            return null;
        }

        if (!string.Equals(current.SelectedGpuUuid, flight.CheckedGpu, StringComparison.OrdinalIgnoreCase))
        {
            // Nor was the GPU now chosen proved to reach containers.
            flight.End = DropForOfferChange(flight, "The GPU changed in the app's settings.");
            return null;
        }

        if (ModelsFolderProblem(current, engine.Context) is { } changedModelsProblem)
        {
            FailLaunch(flight, changedModelsProblem);
            flight.End = Ended;
            return null;
        }

        return current;
    }

    /// <summary>
    /// The GPU proved again if the computer slept since it was checked, so a GPU lost in sleep costs no
    /// usable executor. False while that is under way, or once it failed the launch.
    /// </summary>
    private bool SleptGpuStillWorks(Flight flight, GpuCheckStep? step, Action<GpuCheckStep> keep, Action<(string Message, FaultKind Kind)> failed)
    {
        if (step is { Key: not null } && _in.ResumedSinceGpuCheck)
        {
            // The computer slept again since this check began: it covers the sleep before, not this one.
            step = null;
        }

        if (step is null)
        {
            if (!_in.ResumedSinceGpuCheck)
            {
                return true;
            }

            step = new GpuCheckStep();
            keep(step);
        }

        if (StepGpuCheck(step, _settings.Current, flight.Engine!, mayDownload: true) is not { } gpu)
        {
            return false;
        }

        if (gpu.Problem is { } problem)
        {
            failed(problem);
            return false;
        }

        return true;
    }

    /// <summary>
    /// The image the launch runs, as one thing: what is checked before a warm container is released is what
    /// is created after it, whatever a tag is moved to meanwhile. A reference by digest names one image
    /// already; a tag is taken as the image it named when it was downloaded.
    /// </summary>
    private static string ExactImage(Flight flight)
        => flight.Image is { } image ? ExactImage(flight.Template!.Image, image) : flight.Template!.Image;

    internal static string ExactImage(string reference, ImageInfo image)
        => reference.Contains('@', StringComparison.Ordinal) || image.Id.Length == 0 ? reference : image.Id;

    /// <summary>
    /// Whether containers of the launch's own image reach the GPU (the one chosen in Settings, if any).
    /// False while that is asked, or once it failed the launch: the engine would turn its container down.
    /// </summary>
    private bool ImageReachesGpu(Flight flight, Fact<SupervisorEffects.GpuChecked> check, string key)
    {
        var (engine, image, chosen) = (flight.Engine!, ExactImage(flight), flight.CheckedGpu);
        if (Need(_main, check, key, token => _fx.CheckImageGpuAsync(engine, image, chosen, token)))
        {
            return false;
        }

        var found = Get(check);
        if (found.Problem is not { } problem)
        {
            if (flight.Image is { Id.Length: > 0 } proved && !SeesOtherGpus(found))
            {
                _gpuProvedImages.Add(proved.Id);
            }

            return true;
        }

        // Not offered until a check passes: whether only this image is at fault is for that to say. (An
        // engine that did not answer the check says nothing of the GPU.)
        var failed = found.Kind == FaultKind.GpuUnavailable
            ? FailLaunch(flight, $"Containers of the {flight.Label} image cannot use the GPU: {problem}", found.Kind, "Containers on this computer cannot use the GPU.")
            : FailLaunch(flight, problem, found.Kind, "The container engine on this computer did not answer.");
        flight.End = StopOfferingAfter(failed, problem, found.Kind);
        return false;
    }

    /// <summary>
    /// The GPUs containers of the launch's image list. None listed says nothing by itself: this image's
    /// containers may be the ones that cannot use the GPU, which is the launch's failure, and otherwise the
    /// listing failed (it did not finish, say) and is made again. Never recorded as what there is: that would
    /// be another offer, and have the launch retired for hardware that did not change. Null while it is asked,
    /// or once it failed the launch.
    /// </summary>
    private IReadOnlyList<HostGpu>? ListGpus(Flight flight, GpuListing listing, string key)
    {
        if (listing.RetryAt is { } retryAt)
        {
            if (_now < retryAt)
            {
                WakeAt(retryAt);
                return null;
            }

            listing.RetryAt = null;
            listing.Listed = new Fact<IReadOnlyList<HostGpu>>();
        }

        var (engine, image) = (flight.Engine!, ExactImage(flight));
        if (Need(_main, listing.Listed, $"{key}.{listing.Tries}", token => _fx.ListContainerGpusAsync(engine, image, token)))
        {
            return null;
        }

        var listed = Get(listing.Listed);
        if (listed.Count > 0)
        {
            return listed;
        }

        if (!ImageReachesGpu(flight, listing.ImageGpu, $"{key} image"))
        {
            return null;
        }

        if (++listing.Tries < GpuListingTries)
        {
            listing.RetryAt = _now + _options.MonitorInterval;
            WakeAt(listing.RetryAt.Value);
            return null;
        }

        FailLaunch(flight, "The name and memory of the GPU containers get could not be read. Check the NVIDIA driver.", FaultKind.Unknown, "The GPU of this computer could not be read.");
        flight.End = Ended;
        return null;
    }

    /// <summary>
    /// Containers of the image checked listed other GPUs than are offered (which another image's containers
    /// showed), with none chosen in Settings: they are asked what they get, and Offloadr chooses anew on that.
    /// </summary>
    private bool SeesOtherGpus(SupervisorEffects.GpuChecked found)
    {
        if (_checked.Gpu is not null || _checked.Inventory is not null || found.Check is not { } check)
        {
            return false;
        }

        var seen = NamedUuids(check.Output).ToList();
        var offered = CurrentOffer().Gpus.Gpus.Select(static gpu => gpu.Uuid).OfType<string>().ToList();
        return seen.Count > 0 && offered.Count > 0 && !seen.SequenceEqual(offered, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A check of the GPU after sleep that was begun and is dropped unfinished (the stream went down, or
    /// its launch ended) is still owed: beginning it took the note that the computer slept, which is put
    /// back, so the next start makes the check again.
    /// </summary>
    private void KeepSleptGpuCheckOwed(params GpuCheckStep?[] steps)
    {
        if (steps.Any(static step => step is { Key: not null } && !step.Outcome.Done))
        {
            _in = Change(intent => intent with { ResumedSinceGpuCheck = true });
        }
    }

    /// <summary>
    /// Whether the GPUs offered are asked of a container right before a start (or a release that makes
    /// room for one): when the offer already names what containers reported (an engine on another
    /// computer), and when this computer has several GPUs with none chosen in Settings (whether
    /// containers were seen to get one of them or all). The executor advertises the first GPU its
    /// container lists; which GPUs containers get, and in which order, can change (a driver that
    /// enumerates anew, an engine that exposes another one) without this computer's own list changing:
    /// only a container says.
    /// </summary>
    private bool ContainersListTheirGpus()
        => _checked.Inventory is not null ||
           (_settings.Current.SelectedGpuUuid is null && (View.HostGpus?.Gpus.Count ?? 0) > 1);

    /// <summary>
    /// Lists this computer's own GPUs again, for an offer that names them: one can go (unplugged, its
    /// driver fallen over) without the computer sleeping, during a download or while a container is
    /// created, and with no GPU chosen in Settings a container would then get another one than Offloadr
    /// chose this computer for. False while the listing is under way.
    /// </summary>
    /// <param name="changed">Set when the GPUs listed are not the ones listed before (one appeared, went, or moved).</param>
    /// <param name="unread">
    /// Set when none were listed (nvidia-smi did not answer, say): no list, which says nothing of what containers
    /// get. They are asked themselves, and the list of before stands.
    /// </param>
    private bool NoteHostGpus(Fact<HostGpuReport> host, ref bool recorded, string key, ref bool changed, ref bool unread)
    {
        if (Need(_main, host, key, token => _fx.ProbeHostGpusAsync(token)))
        {
            return false;
        }

        var listed = Get(host);
        if (Once(ref recorded))
        {
            if (listed.Gpus.Count == 0)
            {
                unread = true;
                _gpuProvedImages.Clear();
                return true;
            }

            changed = View.HostGpus is not { } before ||
                      !before.Gpus.Select(static gpu => gpu.Uuid).SequenceEqual(listed.Gpus.Select(static gpu => gpu.Uuid), StringComparer.OrdinalIgnoreCase);
            if (changed)
            {
                _gpuProvedImages.Clear();
            }

            Update(view => view with { HostGpus = listed });
            if (CurrentOffer().Gpus.Gpus.Count == 0)
            {
                // Nothing to offer now: checked again, which says why (and offers again once it passes).
                _checked = _checked with { PassedFor = null };
                _refreshQueued = true;
            }
        }

        return true;
    }

    /// <summary>Steps the launch as far as the facts recorded so far allow. True when it is over; <see cref="Flight.End"/> may say how.</summary>
    private bool StepFlightCore(Flight flight)
    {
        var launch = flight.Launch;
        if (flight.Stage == 0)
        {
            flight.Target = _link.LaunchTarget;
            if (flight.Target is null || flight.LinkCredentials is null)
            {
                return true;
            }

            AppendLog($"supervisor: Offloadr asks for {launch.EditorTemplate} (launch {launch.LaunchId}).");
            Report(flight, RunnerLaunchPhase.Preparing);
            Update(view => view with { Interruption = null, LaunchFailure = null });
            var known = View.Plan?.Find(launch.EditorTemplate) ?? BuiltInLaunchPlanCatalog.Plan().Find(launch.EditorTemplate);
            flight.Label = known?.Label ?? launch.EditorTemplate;
            if (string.IsNullOrWhiteSpace(launch.RunnerImage))
            {
                FailLaunch(flight, $"Offloadr named no runner image for {flight.Label}.");
                return true;
            }

            flight.Template = new LaunchTemplate(launch.EditorTemplate, known?.RuntimeKind ?? string.Empty, launch.RunnerImage, flight.Label);
            var settings = flight.Settings = _settings.Current;
            if (settings.Validate() is { Count: > 0 } problems)
            {
                // A models folder gone (a drive removed, say) raises no settings change: not offered on.
                var unusable = FailLaunch(flight, $"The runner's settings need attention: {problems[0]}", FaultKind.Settings, SettingsReported);
                flight.End = StopOfferingAfter(unusable, problems[0], FaultKind.Settings);
                return true;
            }

            if (Credentials(settings) != flight.LinkCredentials)
            {
                // Saved before this launch was in flight: nothing is released or downloaded for it.
                FailLaunch(flight, "The Offloadr address or runner key changed in the app's settings.");
                return true;
            }

            Set(SupervisorState.Starting, $"Preparing {flight.Label} for a session…");
            if (_checked.Engine is not null && OfferedConfigurationChanged(settings))
            {
                // Offloadr chose this computer for the engine and GPU it offered; others named since
                // are checked, and offered, before any launch runs on them.
                flight.End = DropForOfferChange(flight, "The container engine or GPU changed in the app's settings.");
                return true;
            }

            flight.CheckedGpu = settings.SelectedGpuUuid;
            flight.Stage = 1;
        }

        var template = flight.Template!;
        var label = flight.Label;
        if (flight.Stage == 1)
        {
            // The engine and context this launch runs on, checked before the container running now is
            // released for it: a launch that cannot start costs no warm container. On the one whose
            // containers passed the GPU check, whatever `docker context use` changed since.
            if (StepPreflight(flight.Preflight, flight.Settings!) is not { } preflight)
            {
                return false;
            }

            flight.Engine = preflight.Engine;
            if (preflight.Problem is { } problem)
            {
                var failed = FailLaunch(flight, problem.Message, problem.Kind, problem.Reported);
                if (LeavesNothingLaunchable(problem.Kind))
                {
                    flight.End = StopOfferingAfter(failed, problem.Message, problem.Kind);
                }

                return true;
            }

            if (!OfferStillCurrent(launch))
            {
                // The checks found other GPUs than were offered when Offloadr chose this computer.
                flight.End = FailStale();
                return true;
            }

            flight.Stage = 2;
        }

        var engine = flight.Engine!;
        if (flight.Stage == 2)
        {
            // From here until its container runs, the image must stay: a removal of unused images waits,
            // and one under way finishes first. Downloaded before a warm container is released for it, on
            // the engine checked: a download that fails then costs no usable executor. That container's
            // status keeps being read meanwhile, as it may still take a session.
            if (Need(_main, flight.ImageUse, $"image use {flight.Id}", token => _fx.EnterImageUseAsync(ImageGate, token)))
            {
                return false;
            }

            Get(flight.ImageUse);
            var pullKey = $"pull {flight.Id}";
            if (Once(ref flight.PullAnnounced))
            {
                Report(flight, RunnerLaunchPhase.Downloading);
                Set(SupervisorState.Pulling, $"Downloading {label} for a session. The first download is large and can take a while.");
            }

            bool Pulling() => ReferenceEquals(_flight, flight) && _main.Job is { } job && job.Key == pullKey;
            if (Need(_main, flight.Pull, pullKey, token => _fx.PullForLaunchAsync(
                    engine,
                    template.Image,
                    progress => Post(() =>
                    {
                        if (Pulling())
                        {
                            Update(view => view.State == SupervisorState.Pulling ? view with { PullProgress = progress } : view);
                        }
                    }),
                    () => Post(() =>
                    {
                        if (Pulling() && !Stopped(flight))
                        {
                            Report(flight, RunnerLaunchPhase.Starting);
                        }
                    }),
                    token)))
            {
                return false;
            }

            var pulled = Get(flight.Pull);
            if (Once(ref flight.PullRecorded))
            {
                if (pulled.Failure is { } pullFailure)
                {
                    if (pulled.Image is null)
                    {
                        var failed = FailLaunch(flight, pullFailure.Message, pullFailure.Kind, pullFailure.Summary);
                        if (LeavesNothingLaunchable(pullFailure.Kind))
                        {
                            flight.End = StopOfferingAfter(failed, pullFailure.Message, pullFailure.Kind);
                        }

                        return true;
                    }

                    // Offline or registry trouble: run the copy this machine already has.
                    AppendLog($"warning: {pullFailure.Message} Using the copy already on this machine.");
                    Report(flight, RunnerLaunchPhase.Starting);
                }
                else if (pulled.Image is null)
                {
                    // Downloaded, then removed before it could be looked at (a cleanup outside the app): no
                    // warm container is released for a start that would fail.
                    FailLaunch(flight, $"The {label} image was removed from this computer as it was being prepared.");
                    return true;
                }

                flight.Image = pulled.Image;
            }

            flight.Stage = 3;
        }

        if (flight.Stage == 3)
        {
            if (flight.Relist ??= _checked.Inventory is not null)
            {
                // The offer names the GPUs the containers reported (a remote engine): asked again, with this
                // launch's own image (here now, whatever was removed since the check), so a GPU removed or
                // replaced there makes this launch one chosen for an older offer.
                if (ListGpus(flight, flight.Listed, $"list {flight.Id}") is not { } listed)
                {
                    return flight.End is not null;
                }

                if (Once(ref flight.ListedRecorded))
                {
                    _checked = _checked with { Inventory = listed };
                    if (CurrentOffer().Gpus.Gpus.Count == 0)
                    {
                        // Nothing to offer now: checked again, which says why.
                        _checked = _checked with { PassedFor = null };
                        _refreshQueued = true;
                    }
                }

                if (!OfferStillCurrent(launch))
                {
                    flight.End = FailStale();
                    return true;
                }
            }

            flight.Stage = 4;
        }

        if (flight.Stage == 4)
        {
            flight.Replacing ??= _ownsContainer;
            if (flight.Replacing == true && _ownsContainer)
            {
                if (!MakeRoom(flight))
                {
                    return false;
                }

                if (flight.End is not null)
                {
                    return true;
                }

                if (flight.Withdrawn)
                {
                    // Room was made (a release that was sent is honoured), but the launch is withdrawn:
                    // that is its outcome, and nothing is started for it.
                    return false;
                }
            }
            else if (flight.Room.Committed)
            {
                // The container went by itself (the watch recorded its exit) while its release was asked or
                // its stop began: nothing of that is owed any more, and a withdrawal counts again.
                flight.Room.Committed = false;
                flight.Room.Stop = null;
                if (Cancel(_main) || flight.Withdrawn)
                {
                    return false;
                }
            }

            flight.Stage = 5;
        }

        if (flight.Stage == 5)
        {
            if (Once(ref flight.EngineSelected))
            {
                _engineSelection.SelectExactly(engine);
                Update(view => view with { Image = flight.Image, ActiveTemplate = template });
                Set(SupervisorState.Starting, $"Starting {label} for a session…");
            }

            var rejected = _rejectedContainerId;
            if (Need(_main, flight.Name, $"name {flight.Id}", token => _fx.ClearNameAsync(engine, rejected, NoteRemoved, token)))
            {
                return false;
            }

            var name = Get(flight.Name);
            if (name.StartedAgain)
            {
                // Never by force: one started since it was looked at (by hand, or a late start) runs without a
                // release, and is not this launch's to remove.
                const string startedAgain = "A stopped runner container on this computer was started again outside the app. Stop it, then start the runner again.";
                var blocked = FailLaunch(flight, startedAgain, FaultKind.NameTaken);
                flight.End = StopOfferingAfter(blocked, startedAgain, FaultKind.NameTaken);
                return true;
            }

            if (name.Holder is { } existing)
            {
                // Taken meanwhile, by another copy of this app or by hand: not this app's to remove.
                // The computer stays available, so the failure still reaches Offloadr.
                var blocked = FailLaunch(flight, NameTakenMessage(existing), FaultKind.NameTaken);
                flight.End = StopOfferingAfter(blocked, NameTakenMessage(existing), FaultKind.NameTaken);
                return true;
            }

            // Read again: Settings saved during the download apply to this container.
            if (SettingsStillHold(flight) is not { } current)
            {
                return true;
            }

            flight.Settings = current;
            var spec = BuildSpec(current, template);

            // Not part of the fingerprint: a container is the same runner whichever launch started it.
            flight.AttemptId = Guid.NewGuid().ToString("N");
            flight.Spec = spec with
            {
                Image = ExactImage(flight),
                Environment = new Dictionary<string, string>(spec.Environment, StringComparer.Ordinal) { [LaunchIdVariable] = launch.LaunchId },
                Labels = new Dictionary<string, string>(spec.Labels, StringComparer.Ordinal)
                {
                    [LaunchLabel] = flight.AttemptId,
                    [LaunchIdLabel] = launch.LaunchId,
                    [LaunchTargetLabel] = flight.Target!,
                    [ImageLabel] = template.Image,
                },
            };
            flight.Stage = 6;
        }

        if (flight.Stage == 6 && !Dispatch(flight))
        {
            return false;
        }

        if (flight.End is not null)
        {
            return true;
        }

        return flight.Stage == 7 ? AfterRun(flight) : flight.Stage == 8 && Finish(flight);
    }

    /// <summary>
    /// The settings the container is built from still hold: the launcher's address and key, the
    /// engine checked, and the same container (the fingerprint covers the key, folders and GPU).
    /// Validation reads the disk (the models folder), so the check at the spawn, under the link's
    /// lock, leaves it to the one just before.
    /// </summary>
    private bool StillTheContainerAskedFor(Flight flight, RunnerSettings current, bool validate = true)
        => Credentials(current) == flight.LinkCredentials &&
           string.Equals(flight.Engine!.Configured, ContainerEngineSelection.Normalize(current.ContainerEngineExecutable), StringComparison.Ordinal) &&
           (!validate || current.Validate().Count == 0) &&
           string.Equals(Fingerprint(BuildSpec(current, flight.Template!)), flight.Spec!.Labels[SpecLabel], StringComparison.Ordinal);

    /// <summary>
    /// Creates the launch's container, then starts it. The container is created first, on its own: one
    /// that is only created runs nothing, so a creation that is cancelled or given up can leave nothing
    /// that runs unseen, only an inert container to find and remove. The start is the guarded step:
    /// only while the stream is live and its latest state still names this launch, asked again right
    /// before the start, after the checks that can take time. False while it waits.
    /// </summary>
    private bool Dispatch(Flight flight)
    {
        var launch = flight.Launch;
        var engine = flight.Engine!;
        var template = flight.Template!;
        if (flight.CreateAsked && !flight.CreateRecorded)
        {
            // A creation asked for is seen through first, whatever the stream does meanwhile: cancelled
            // and asked again, the second would find the name taken by what the first made. What it makes
            // is inert, known by its ID, and this launch's to start once it may, or to remove.
            if (!Create(flight))
            {
                return false;
            }

            if (flight.End is not null)
            {
                return true;
            }
        }

        if (!_link.MayStart(launch))
        {
            // Asked again when the stream says so, and a little later all the same. What was checked for
            // the start counts no more: a GPU on a remote engine can change while the stream is down.
            if (flight.Start.Id != 0)
            {
                KeepSleptGpuCheckOwed(flight.Start.SleptGpu);
                flight.Start = new Starting();
            }

            Cancel(_main);
            WakeIn(_options.MonitorInterval);
            return false;
        }

        if (!flight.CreateRecorded)
        {
            // Created before the checks below, which must be fresh at the start: its creation takes a
            // while, and a GPU on a remote engine can change meanwhile. Only the container Settings still
            // ask for is made; one a later check turns down is removed, having run nothing.
            if (!StillTheContainerAskedFor(flight, _settings.Current))
            {
                EndForChangedSettings(flight);
                return true;
            }

            if (!Create(flight))
            {
                return false;
            }

            if (flight.End is not null)
            {
                return true;
            }
        }

        // The GPU proved again first if the computer slept during the launch (this waits), so the
        // settings check below is the last thing before the start.
        var start = flight.Start;
        var drops = _link.StreamDrops;
        if (start.Id != 0 && (start.Resumes != _in.Resumes || start.StreamDrops != drops))
        {
            // The computer woke since these checks began, or the stream dropped and came back while one
            // of them was under way: what they found (the GPUs a remote engine's containers get, say) is
            // from before, and must not overwrite, or stand in for, what is checked now.
            KeepSleptGpuCheckOwed(start.SleptGpu);
            start = flight.Start = new Starting();
        }

        if (start.Id == 0)
        {
            start.Id = ++_ids;
            start.Resumes = _in.Resumes;
            start.StreamDrops = drops;
        }

        if (!SleptGpuStillWorks(flight, start.SleptGpu, step => start.SleptGpu = step, problem =>
            {
                // The computer slept during the launch, and containers no longer reach the GPU.
                var sleptFailure = FailLaunch(flight, problem.Message, problem.Kind, "Containers on this computer cannot use the GPU.");
                flight.End = new Ending(sleptFailure, () => FailStart(problem.Message, automatic: true, problem.Kind));
            }))
        {
            return flight.End is not null;
        }

        if (start.Relist ??= ContainersListTheirGpus())
        {
            // The offer names what a remote engine's containers reported: asked again after the wait,
            // as a GPU there can change while the stream was down.
            if (ListGpus(flight, start.Listed, $"list again {start.Id}") is not { } listed)
            {
                return flight.End is not null;
            }

            if (Once(ref start.ListedRecorded))
            {
                _checked = _checked with { Inventory = listed };
                if (CurrentOffer().Gpus.Gpus.Count == 0)
                {
                    // Nothing to offer now: checked again, which says why (and offers again once it passes).
                    _checked = _checked with { PassedFor = null };
                    _refreshQueued = true;
                }
            }
        }
        else
        {
            // The check that passed for the offer may have run in another image, which says nothing of what
            // containers of this one get (the GPU chosen in Settings, say). Once for each image.
            if (flight.Image is { Id.Length: > 0 } cold && !_gpuProvedImages.Contains(cold.Id))
            {
                if (!ImageReachesGpu(flight, start.ImageGpu, $"start image gpu {start.Id}"))
                {
                    return flight.End is not null;
                }

                if (SeesOtherGpus(Get(start.ImageGpu)))
                {
                    start.Relist = true;
                    _lookAgain = true;
                    return false;
                }
            }

            if (!NoteHostGpus(start.Host, ref start.HostRecorded, $"host gpus {start.Id}", ref start.HostChanged, ref start.HostUnread))
            {
                return false;
            }

            if (start.HostUnread || (start.HostChanged && _settings.Current.SelectedGpuUuid is null))
            {
                start.HostUnread = false;
                // Another set of GPUs than was listed before, and none chosen: which of them containers
                // get, and in which order, was learned from a container of before. One is asked again.
                start.HostChanged = false;
                start.Relist = true;
                _lookAgain = true;
                return false;
            }
        }

        if (!OfferStillCurrent(launch))
        {
            // A GPU checked again meanwhile (after sleep, say) is not what was offered.
            flight.End = FailStale();
            return true;
        }

        // The wait may have been long (the stream was down): the container is still the one Settings ask for.
        if (!StillTheContainerAskedFor(flight, _settings.Current))
        {
            EndForChangedSettings(flight);
            return true;
        }

        // Checked under the link's own lock, and once more right before the engine's process is created. Not under the lock: creating a process
        // can stall, and the link must go on recording its stream. A state that lands in the instant between
        // that last check and the process is one that lands after the start: the process itself takes longer
        // than that to reach the engine, so no lock here could tell the two apart. A withdrawal after it is answered after the start: reported
        // withdrawn, its container released before it is stopped. Bounded, so a daemon that hangs cannot
        // hold back the launches after this one.
        var id = _containerId!;

        // Noted before the engine is asked, and before the link is: a crash right after must not take a
        // container that may have run for one that was only created, and the write must not stand
        // between the link's check and the start.
        if (!NoteStartAsked(launch.LaunchId))
        {
            // Not started without its record: what was created is removed as the launch ends.
            FailLaunch(flight, "The app could not save its record of the runner container in its data folder.", FaultKind.Unknown,
                "The app could not save its record of the runner container.");
            flight.End = Ended;
            return true;
        }

        var checkedAt = start.StreamDrops;
        var spawned = _link.TryStart(launch, streamDrops: checkedAt, dispatch: () =>
        {
            // A Stop pressed before this instant wins, though this tick began before it: read here, at
            // the start, so no executor starts (or registers) after it.
            var asked = Volatile.Read(ref _intent);
            if (asked.Stops != flight.Stops || !asked.Wanted || _stopping.IsCancellationRequested)
            {
                return;
            }

            // Settings saved before this instant win too: a container is never started with an address,
            // key, engine or setting other than the ones checked (it goes round once more, and fails there).
            var saved = _settings.Current;
            if (!StillTheContainerAskedFor(flight, saved, validate: false))
            {
                return;
            }

            if (asked.ResumedSinceGpuCheck)
            {
                // The computer slept since the last check, though this tick began before it woke: the
                // GPU is proved again first (it goes round once more, and checks there).
                return;
            }

            _dispatched.Add(launch.LaunchId);
            flight.Dispatched = true;
            _startUnanswered = true;
            StartRun(flight, (stillWanted, token) => _fx.StartContainer(engine, id, stillWanted, token), _options.RunTimeout, asked.Resumes, saved, checkedAt);
        });
        if (spawned && flight.Dispatched)
        {
            flight.Stage = 7;
            return true;
        }

        // Not started this time: its record says only created again.
        NoteStartNotAsked(launch.LaunchId);

        // Not started this time. It goes round once more, its last checks made again; its container
        // stays, created and inert.
        flight.Start = new Starting();
        WakeIn(_options.MonitorInterval);
        return false;
    }

    /// <summary>
    /// Creates the launch's container, once. It is this supervisor's from the moment its creation is
    /// asked for, so a Stop or shutdown looks for it, and removes it, whatever became of the call. False
    /// while the creation is under way; a creation that failed sets the launch's end.
    /// </summary>
    private bool Create(Flight flight)
    {
        var launch = flight.Launch;
        var engine = flight.Engine!;
        if (flight.CreateRecorded)
        {
            return true;
        }

        if (!flight.CreateAsked && _maintenanceSlot.Busy && _maintenanceLooksFor.Contains(launch.LaunchId))
        {
            // A look for what an earlier attempt of this launch left is under way, by its launch alone: it
            // would take the container made now for that one, and remove it. Ended first (it wakes the loop).
            Cancel(_maintenanceSlot);
            return false;
        }

        if (Once(ref flight.CreateAsked))
        {
            // Saved right before the container is made, not during the wait: after a crash it is the only
            // pointer to the engine this runner is on, and a later start could otherwise run a second
            // runner elsewhere; saved for nothing, it would only hold a later start to that engine.
            _engineSelection.SelectExactly(engine);
            if (!_engineSelection.Remember(out var rememberError))
            {
                FailLaunch(flight, rememberError!, FaultKind.Unknown, "The app could not save which container engine the runner uses.");
                flight.End = Ended;
                return true;
            }

            // Noted on disk before the container is made. Required: after a crash it is how the container
            // is found again, and how Offloadr learns it is gone.
            if (!NoteLaunched(launch.LaunchId, flight.Target!, engine))
            {
                _engineSelection.Forget();
                FailLaunch(flight, "The app could not save its record of the runner container in its data folder.", FaultKind.Unknown,
                    "The app could not save its record of the runner container.");
                flight.End = Ended;
                return true;
            }

            // What it is started with, kept (encrypted) until Offloadr knows it is gone: only that Offloadr
            // can be told, whatever Settings name by then. Required like the record itself: without it, a
            // report owed after Settings changed and the app restarted could never be sent.
            if (!RememberStartedWith(launch.LaunchId, flight.Settings!.OffloadrApiOrigin, flight.Settings.RunnerKey))
            {
                ForgetLaunched(launch.LaunchId);
                _engineSelection.Forget();
                FailLaunch(flight, "The app could not save its record of the runner container in its data folder.", FaultKind.Unknown,
                    "The app could not save its record of the runner container.");
                flight.End = Ended;
                return true;
            }

            // Until the engine returns the new ID, a container with this creation's label counts as this runner.
            _ownsContainer = true;
            Volatile.Write(ref _activeRunnerKey, flight.Settings!.RunnerKey!.Trim());
            _containerId = null;
            _startUnanswered = false;
            _seenRunning = false;
            _keptCreatedNoted = false;
            _seenCreated = false;
            _launchNoLongerNamed = false;
            _attemptId = flight.AttemptId;
            _containerLaunchId = launch.LaunchId;
            _containerLaunchTarget = flight.Target;
            _containerEngine = engine;
            ContainerChanged();
            ContainerBusy(true);
        }

        var (spec, timeout) = (flight.Spec!, _options.RunTimeout);
        if (Need(_main, flight.Created, $"create {flight.Id}", token => _fx.CreateAsync(engine, spec, timeout, token)))
        {
            return false;
        }

        if (flight.Created.Error is ProcessStartException notStarted)
        {
            // The engine executable did not even start, so nothing was created; nothing can be launched until it does.
            ContainerBusy(false);
            AbandonContainer(ContainerFate.NeverStarted);
            var failed = FailLaunch(flight, notStarted.Message, FaultKind.EngineMissing, "The container engine could not be started.");
            flight.End = StopOfferingAfter(failed, notStarted.Message, FaultKind.EngineMissing);
            return true;
        }

        var created = Get(flight.Created);
        if (created.Id is { } newId)
        {
            // Known by ID from now on: whatever becomes of its start, what it does can be asked.
            _containerId = NormalizeContainerId(newId) ?? newId;
            if (!_engineSelection.Remember(_containerId, out var idError))
            {
                AppendLog($"supervisor: {idError}");
            }

            flight.CreateRecorded = true;
            return true;
        }

        var failure = created.Failure!;

        // The CLI failing is no proof nothing was created (its answer lost, or it was given up): what it
        // left is looked for and removed. Whatever is found is inert: it was never started.
        // Only the engine's own refusal says nothing was made: after a call that was given up, or whose
        // answer was lost on the way, it may still appear, and is looked for a while.
        var (attempt, settle) = (flight.AttemptId!, created.GivenUp || !failure.Refused);
        if (Need(_main, flight.CreateLeft, $"create left {flight.Id}", token => _fx.RemoveCreatedAsync(engine, null, attempt, settle, token)))
        {
            return false;
        }

        var left = Get(flight.CreateLeft);
        ContainerBusy(false);
        LetGoOfCreated(engine, left);
        if (left.Running is not null || left.Started)
        {
            // Someone else started what this creation made: it runs (and stays supervised), or ran and is
            // gone, which Offloadr learns. Never a launch that failed: that would fail the allocation its
            // exit report lets retry.
            AppendLog($"supervisor: {failure.Message}");
            ShowWithoutLaunch();
            flight.End = Ended;
            return true;
        }

        if (flight.Withdrawn)
        {
            // Withdrawn meanwhile: that is the launch's outcome, not the engine's failure.
            AppendLog($"supervisor: {failure.Message}");
            if (flight.Phase is { } phase)
            {
                Report(flight, phase, flight.Message);
            }

            ShowWithoutLaunch();
            flight.End = Ended;
            return true;
        }

        // Nothing ran, and nothing unseen can: the launch failed, and says so at once.
        var outcome = FailLaunch(flight, failure.Message, failure.Kind, failure.Summary);
        flight.End = LeavesNothingLaunchable(failure.Kind) ? StopOfferingAfter(outcome, failure.Message, failure.Kind) : Ended;
        return true;
    }

    /// <summary>
    /// A launch ends without its container having been started (it failed a last check, or was
    /// withdrawn): the container it created is removed. It ran nothing, so nothing is reported for it.
    /// False while that is under way.
    /// </summary>
    private bool DiscardCreated(Flight flight)
    {
        if (flight.Dispatched || !_ownsContainer || !string.Equals(_attemptId, flight.AttemptId, StringComparison.Ordinal))
        {
            return true;
        }

        var (engine, id, attempt) = (flight.Engine!, _containerId, flight.AttemptId!);
        if (Need(_main, flight.Discarded, $"discard {flight.Id}", token => _fx.RemoveCreatedAsync(engine, id, attempt, settle: id is null, token)))
        {
            return false;
        }

        ContainerBusy(false);

        // (Not known: looked for, and removed, later.)
        LetGoOfCreated(engine, flight.Discarded.Error is null ? flight.Discarded.Value : null);
        if (!_ownsContainer && View.State == SupervisorState.Running)
        {
            // The launch's end was shown while its container was still this app's (it said "running"):
            // shown again without it, with why the launch failed kept.
            var (failure, message) = (View.LaunchFailure, View.Message);
            SettleIdle();
            if (failure is not null)
            {
                Update(view => view with { LaunchFailure = failure, Message = message });
            }
        }

        return true;
    }

    /// <summary>A look that removes what it finds of this launch's container is under way.</summary>
    private bool LooksForItsContainer(Flight flight)
        => _main.Job?.Key is { } key &&
           (string.Equals(key, $"left {flight.Id}", StringComparison.Ordinal) ||
            string.Equals(key, $"create left {flight.Id}", StringComparison.Ordinal) ||
            string.Equals(key, $"discard {flight.Id}", StringComparison.Ordinal));

    /// <summary>
    /// What a look for the launch's created container found, and was not acted on before a Stop (or the app
    /// closing) came: removed, running or exited, it is accounted for as that. Not found, or not said, it is
    /// for the Stop to look for.
    /// </summary>
    private void LetGoOfCreatedFound(Flight flight)
    {
        if (flight.Engine is not { } engine || !_ownsContainer || !string.Equals(_attemptId, flight.AttemptId, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var fact in new[] { flight.CreateLeft, flight.Discarded })
        {
            if (fact is { Known: true, Error: null, Value: { Unanswered: false } left } && (left.Settled || left.Running is not null || left.Started))
            {
                LetGoOfCreated(engine, left);
                return;
            }
        }
    }

    /// <summary>
    /// Lets go of a container that was created and not started by this app, by what a look for it found.
    /// Seen, or looked for and not there after a definite refusal: nothing of it is left. Not seen after a
    /// call that was given up, or with an engine that did not answer: it may still be created, inert, and
    /// is looked for a while longer to be removed. One that someone else started is still this app's
    /// container: running, it is taken under supervision like any runner of this app found running; run
    /// and stopped again, it is gone, which Offloadr learns.
    /// </summary>
    private void LetGoOfCreated(Selected engine, SupervisorEffects.CreatedLeft? left)
    {
        if (left?.Leftover is { } leftover)
        {
            NoteLeftover(engine, leftover);
        }

        if (left?.Running is { } running)
        {
            AppendLog("supervisor: the runner container that was created runs, started outside the app; it stays supervised.");
            var launchId = _containerLaunchId;
            AbandonContainer(ContainerFate.Unknown);
            if (launchId is not null)
            {
                _unseen.Remove(launchId);
            }

            _foundRunning ??= (running, engine);
            return;
        }

        if (left is { Started: true })
        {
            if (_containerLaunchId is { } ran)
            {
                // It ran and exited: its launch never fails for that, nor is it started again.
                _exitedLaunches.Add(ran);
                _dispatched.Add(ran);
            }

            ConfirmLaunched(_containerLaunchId);
            AbandonContainer(ContainerFate.Gone);
            return;
        }

        AbandonContainer(left is { Settled: true } ? ContainerFate.NeverStarted : ContainerFate.Unknown);
    }

    /// <summary>
    /// Starts the container as the main job, given up after <paramref name="timeout"/>: off the loop, and
    /// outside the link's lock, as creating the engine's process can itself take long (a file scanner, a
    /// slow disk). Not spawned once the stream dropped (<paramref name="checkedDrops"/>) or names another
    /// launch, or none; once the computer woke from sleep more often than <paramref name="checkedResumes"/>,
    /// the count its last checks saw; nor once other settings were saved than
    /// <paramref name="checkedSettings"/>, the ones they saw.
    /// </summary>
    private void StartRun(Flight flight, Func<Func<bool>, CancellationToken, Task> spawn, TimeSpan timeout, long checkedResumes, RunnerSettings checkedSettings, long checkedDrops)
    {
        var cancellation = new CancellationTokenSource();
        var deadline = new CancellationTokenSource(timeout, _time);

        // A Stop (or the app closing) reaches the start itself, on the thread that asks for it: one before
        // the engine's process starts keeps it from starting, and one after ends it, without waiting for this loop.
        // So does another address or key saved in Settings: a container is never started for a runner
        // Settings no longer name.
        var (stops, credentials) = (flight.Stops, flight.LinkCredentials);
        bool StopAsked()
        {
            var asked = Volatile.Read(ref _intent);
            return asked.Stops != stops || !asked.Wanted || _stopping.IsCancellationRequested;
        }

        var stopped = _fence.Until(() => StopAsked() || Credentials(_settings.Current) != credentials);

        // Nor is it started once the stream no longer names its launch, the computer woke from sleep, or other
        // settings were saved (another GPU or engine, say), since its last checks: they are made again first.
        // Asked right before the engine's process exists, and never after: one under way is not cut short by it
        // (the engine may not have heard it yet, and an allocation would pay for a container that never ran).
        var launch = flight.Launch;
        bool Outdated() => Volatile.Read(ref _intent).Resumes != checkedResumes || !ReferenceEquals(_settings.Current, checkedSettings) ||
                           !_link.MayStart(launch, checkedDrops);
        var both = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, deadline.Token, stopped.Signal);
        var key = $"start {flight.Id}";
        var job = new Job(key, cancellation);
        _main.Job = job;
        _ = Task.Run(async () =>
        {
            // Raised already: the engine is not asked at all, and nothing of this start can be under way there.
            var prevented = both.IsCancellationRequested;
            Task run;
            try
            {
                run = prevented ? Task.FromCanceled(both.Token) : spawn(() => !Outdated(), both.Token);
            }
            catch (Exception ex)
            {
                run = Task.FromException(ex);
            }

            Exception? error = null;
            var cancelled = false;
            var timedOut = false;
            var heldBack = false;
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (cancellation.IsCancellationRequested || stopped.Signal.IsCancellationRequested)
            {
                cancelled = true;

                // Cancelled before the engine's command line was even started: as good as never asked.
                prevented |= ex is ProcessNotSpawnedException;
            }
            catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
            {
                timedOut = true;
                prevented |= ex is ProcessNotSpawnedException;
            }
            catch (ProcessNotSpawnedException)
            {
                heldBack = true;
                prevented = true;
            }
            catch (Exception ex)
            {
                error = ex;
            }

            Post(() =>
            {
                if (ReferenceEquals(_main.Job, job))
                {
                    _main.Job = null;
                }

                both.Dispose();
                deadline.Dispose();
                cancellation.Dispose();
                stopped.Dispose();
                if (cancelled && StopAsked())
                {
                    // Stopped: the container is known by its ID, and the Stop that follows stops and removes it.
                    // Held back before the engine was asked at all, the start did not happen: what the Stop
                    // finds is a container that was only created, removed with nothing asked of Offloadr.
                    if (prevented)
                    {
                        flight.StartPrevented = true;
                        NoteStartAnswer(flight);
                    }

                    return;
                }

                if (cancelled)
                {
                    // Held back by another address or key in Settings. No Stop follows: the launch itself
                    // looks at what became of the container. A start the engine was never asked for did
                    // not happen; one cut short may have reached it, and is looked at as such.
                    flight.StartPrevented = prevented;
                    flight.Run.Set(key, true, new ContainerEngineException(
                        "The Offloadr address or runner key changed in the app's settings before the runner container was started."));
                    return;
                }

                if (heldBack)
                {
                    // Held back by a wake from sleep, other settings, or a stream that dropped or no longer
                    // names the launch, before the engine was asked at all: the start did not happen. Its record says only created again, and the launch goes round
                    // once more, its last checks made again (the GPU, and whether it is still the container
                    // Settings ask for); its container stays, created and inert.
                    flight.Dispatched = false;
                    _dispatched.Remove(flight.Launch.LaunchId);
                    _startUnanswered = false;
                    NoteStartNotAsked(flight.Launch.LaunchId);
                    flight.Start = new Starting();
                    flight.Stage = 6;
                    return;
                }

                // (Given up before the engine's command line was even started: it was never asked.)
                flight.StartPrevented |= prevented;
                flight.RunTimedOut = timedOut;
                flight.Run.Set(key, true, timedOut
                    ? new ContainerEngineException($"Docker did not start the runner container within {timeout.TotalSeconds:0} seconds.")
                    : error);
            });
        });
    }

    /// <summary>
    /// Records what the look after a failed start left of the launch's container, once: one it found
    /// not running is let go of as what it was. Never started, it ran nothing; one that ran (or is no
    /// longer there to say) is gone, which Offloadr learns. One that runs, or that the engine would not
    /// describe, stays supervised.
    /// </summary>
    private void LetGoOfLeft(Flight flight, SupervisorEffects.FailedLaunchLeft left)
    {
        NoteStartAnswer(flight);
        _seenRunning |= left.Started;
        _seenCreated |= left.SeenCreated;
        if (left.What is SupervisorEffects.LaunchLeft.Running or SupervisorEffects.LaunchLeft.Unknown ||
            (left.StillCreated && StartMayBeUnderWay) ||
            !Once(ref flight.LeftRecorded))
        {
            // (Still only created after a start the engine never refused: it may start it yet. It stays
            // this app's container, for the watch, or the Stop that follows, to settle.)
            return;
        }

        if (left.Leftover is { } leftover)
        {
            // Stopped, and the engine would not remove it: it still holds the runner key, and goes later.
            NoteLeftover(flight.Engine!, leftover);
        }

        var ran = RanBefore(flight, left);
        if (ran)
        {
            // It ran (or may have) and is gone: Offloadr learns that, which lets the allocation launch again.
            _exitedLaunches.Add(flight.Launch.LaunchId);
        }

        AbandonContainer(ran ? ContainerFate.Gone : ContainerFate.NeverStarted);
    }

    /// <summary>
    /// Whether a container the look after a failed start found not running counts as one that ran: it
    /// was seen exited (or is gone), or it was seen created after a start the engine never refused.
    /// </summary>
    private bool RanBefore(Flight flight, SupervisorEffects.FailedLaunchLeft left)
        => left.What switch
        {
            // Not there, after a start the engine was never asked for: it ran nothing, whoever removed it.
            SupervisorEffects.LaunchLeft.Exited => left.Started || _seenRunning || !StartNotAsked(flight),
            SupervisorEffects.LaunchLeft.NeverStarted => !NeverRan(true),
            _ => false,
        };

    /// <summary>The start never reached the engine: it was held back, or the engine's command line did not run.</summary>
    private static bool StartNotAsked(Flight flight)
        => flight.StartPrevented || (flight.Run is { Known: true, Error: ProcessStartException } && !flight.RunTimedOut);

    /// <summary>
    /// Takes the start's own answer, once it returned: a start the engine refused (or whose command line
    /// did not even run) did not happen, and the container seen created after it never ran.
    /// </summary>
    private void NoteStartAnswer(Flight flight)
    {
        if (flight.StartPrevented ||
            (flight.Run is { Known: true, Error: ProcessStartException or ContainerEngineException { Refused: true } } && !flight.RunTimedOut))
        {
            if (_startUnanswered && !_seenRunning)
            {
                // Saved too: after a crash right here, the container found created is one that never ran,
                // not one whose start may have been under way. A start the engine was never asked for
                // leaves nothing to tell whatever became of the container. One it refused still does if
                // the container is not there to say that it never ran (someone removed it).
                if (StartNotAsked(flight))
                {
                    NoteStartNotAsked(flight.Launch.LaunchId);
                }
                else
                {
                    NoteStartRefused(flight.Launch.LaunchId);
                }
            }

            _startUnanswered = false;
        }
    }

    /// <summary>After the start returned: a failed one is looked at for what became of the container, a started one goes on.</summary>
    private bool AfterRun(Flight flight)
    {
        var launch = flight.Launch;
        var engine = flight.Engine!;
        if (!flight.Run.Known)
        {
            return false;
        }

        if (flight.Run.Error is { } failure)
        {
            if (failure is not (ContainerEngineException or ProcessStartException))
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
            }

            var kind = failure is ContainerEngineException engineFailure ? engineFailure.Kind : FaultKind.EngineMissing;
            var reported = failure is ContainerEngineException summarized ? summarized.Summary : "The container engine could not be started.";
            NoteStartAnswer(flight);

            // The CLI failing is no proof the container did not start (the daemon's answer lost, say, or
            // the start given up): it is known by its ID, so the engine is asked what became of it.
            var id = _containerId!;
            if (Need(_main, flight.Left, $"left {flight.Id}", token => _fx.LookAfterStartAsync(engine, id, token)))
            {
                return false;
            }

            var left = Get(flight.Left);
            _seenRunning |= left.Started;
            _seenCreated |= left.SeenCreated;
            var mayStartYet = left.StillCreated && StartMayBeUnderWay;
            if (left.What == SupervisorEffects.LaunchLeft.Unknown || mayStartYet)
            {
                // The engine does not say. Whether it never started (FAILED) or ran and exited (an exit to
                // report) is not known, and one sent for the other would cost the allocation: neither goes
                // out until the engine says. The launch does not wait for that in the way of everything
                // else: its container stays supervised, by its ID, and the watch settles it.
                // The same for one seen still only created that the engine would not remove, after a start
                // it never refused: it may start it yet, and telling it gone would let the allocation
                // launch again beside a container that then serves.
                AppendLog(mayStartYet
                    ? $"supervisor: {failure.Message} The runner container has not started, and the container engine would not remove it; checking."
                    : $"supervisor: {failure.Message} The container engine did not say whether the runner container started; checking.");
                _unsettledLaunch = new UnsettledLaunch(
                    launch, flight.AttemptId!, failure.Message, kind, reported,
                    flight.Withdrawn, flight.Phase, flight.Message,
                    flight.Template!, flight.Label, flight.Image, flight.Spec!.Labels[SpecLabel], engine);
                ContainerBusy(false);
                Set(SupervisorState.Running, $"Checking whether the {flight.Label} container started…");
                _watchNow = true;
                return true;
            }

            if (left.What == SupervisorEffects.LaunchLeft.Running)
            {
                AppendLog($"supervisor: {failure.Message} The container started all the same.");
            }
            else
            {
                ContainerBusy(false);
                var ran = RanBefore(flight, left);
                LetGoOfLeft(flight, left);
                if (flight.Withdrawn)
                {
                    // Withdrawn while Docker ran: that is the launch's outcome, not Docker's failure. Except
                    // that a container which ran and exited is told as an exit alone: a withdrawal that would
                    // be reported as FAILED (new settings) must not fail the allocation that exit lets retry.
                    AppendLog($"supervisor: {failure.Message}");
                    if (flight.Phase is { } withdrawnPhase && !(ran && withdrawnPhase == RunnerLaunchPhase.Failed))
                    {
                        Report(flight, withdrawnPhase, flight.Message);
                    }

                    if (!_ownsContainer)
                    {
                        SettleIdle();
                    }

                    return true;
                }

                if (ran)
                {
                    // No FAILED for a container that started and exited: its exit report says what happened,
                    // and a FAILED arriving first would fail the allocation it could still serve.
                    AppendLog($"supervisor: could not start {launch.EditorTemplate} for a session: {failure.Message}");
                    ShowWithoutLaunch();
                    Update(view => view with
                    {
                        Message = $"Could not start {launch.EditorTemplate} for a session: {failure.Message}",
                        LaunchFailure = SupervisorFault.For(kind, failure.Message, view.Engine?.Kind ?? ContainerEngineKind.Docker),
                    });
                    return true;
                }

                // Created and never started (a GPU it could not attach, say): it ran nothing, and the launch failed.
                var failed = FailLaunch(flight, failure.Message, kind, reported);
                if (LeavesNothingLaunchable(kind))
                {
                    // Docker or the GPU let it down: not offered until a fresh check passes.
                    flight.End = StopOfferingAfter(failed, failure.Message, kind);
                }

                return true;
            }
        }

        // Started: from now on a withdrawal is answered by the container's own registration.
        // Only a state that named another launch, or none, proves this one was retired: a
        // matched launch stays named. A takeover or new settings prove nothing, and the
        // container may already be serving; it is released like any other.
        WithdrawIfNotNamed(flight, _link.State);
        if (flight is { Withdrawn: true, Phase: RunnerLaunchPhase.Superseded or RunnerLaunchPhase.Cancelled })
        {
            flight.Retired = flight.Phase;
        }

        flight.Started = true;
        ConfirmLaunched(launch.LaunchId);
        ContainerBusy(false);
        if (flight.Retired is { } phase)
        {
            // Supervised like any other container, and released right away: stopped only once
            // Offloadr grants that (its agent is refused meanwhile, the launch being retired).
            AppendLog($"supervisor: Offloadr withdrew the launch of {flight.Label} as its container started; releasing it.");
            Report(flight, phase);
        }
        else
        {
            Report(flight, RunnerLaunchPhase.Started);
        }

        flight.Stage = 8;
        return Finish(flight);
    }

    /// <summary>The container runs: its image is read, and it is recorded as the runner.</summary>
    private bool Finish(Flight flight)
    {
        var engine = flight.Engine!;

        // The start runs whatever the tag named when the container was created.
        var containerId = _containerId;
        if (Need(_main, flight.StartedImage, $"started image {flight.Id}", token => _fx.StartedImageIdAsync(engine, containerId, token)))
        {
            return false;
        }

        if (!_ownsContainer || !string.Equals(_attemptId, flight.AttemptId, StringComparison.Ordinal))
        {
            // It exited while its image was looked up, and the watch recorded that: nothing runs to
            // follow or show, and the computer is as after any exit.
            ShowWithoutLaunch();
            if (_in.Wanted)
            {
                _refreshQueued = true;
            }

            return true;
        }

        CompleteStart(flight.Template!, flight.Label, flight.Image, flight.StartedImage.Value, flight.Spec!.Labels[SpecLabel], flight.Retired is not null, engine);
        return true;
    }

    /// <summary>
    /// Records a container that runs as the runner: what it runs, when it began to idle, its log
    /// followed, and the image it replaced removed. For a launch that just started it, and for one
    /// the watch found running after a start whose outcome the engine had not told.
    /// </summary>
    private void CompleteStart(LaunchTemplate template, string label, ImageInfo? image, string? startedImageId, string fingerprint, bool retired, Selected engine)
    {
        var previousImageId = _runningImageId;
        var previousImageReference = _runningTemplate?.Image;
        var previousEngine = _runningEngine ?? _engineSelection.Current;
        _activeFingerprint = fingerprint;
        _runningTemplate = template;
        _runningImageId = startedImageId ?? image?.Id;
        _runningEngine = engine;
        _containerEngine = engine;

        // A withdrawn launch's container counts as idle for the whole warm time already.
        _idleSince = retired ? _now - _options.WarmTime : _now;
        _releaseRetryAt = TimeSpan.Zero;
        ResetStatusTracking();
        ContainerChanged();
        StartLogFollower();
        Update(view => view with
        {
            State = SupervisorState.Running,
            Message = retired ? $"Releasing the {label} container Offloadr no longer needs…" : $"Running {label} for a session.",
            RunnerStatus = null,
        });

        if (previousImageId is not null && previousImageReference is not null &&
            !string.Equals(previousImageId, _runningImageId, StringComparison.Ordinal))
        {
            RemoveSupersededImage(previousImageId, previousImageReference, previousEngine);
        }
        else if (View.GpuAccess is null)
        {
            CheckGpuAccessInBackground();
        }
    }

    /// <summary>
    /// A launch whose start failed and whose container the engine would not describe: whether it never
    /// started (its launch failed) or ran (it serves, or exited) is not known yet. The container is
    /// known by its ID and stays supervised; the watch's next answer settles which report goes out, and
    /// neither is sent before. Everything a finished start records is kept here for that moment.
    /// </summary>
    private sealed record UnsettledLaunch(
        LaunchRequest Launch,
        string Attempt,
        string Message,
        FaultKind Kind,
        string Reported,
        bool Withdrawn,
        RunnerLaunchPhase? WithdrawnPhase,
        string? WithdrawnMessage,
        LaunchTemplate Template,
        string Label,
        ImageInfo? Image,
        string Fingerprint,
        Selected Engine);

    private UnsettledLaunch? _unsettledLaunch;

    /// <summary>The unsettled launch's container was seen running: it started after all, and is the runner.</summary>
    private void SettleStarted(UnsettledLaunch unsettled, ContainerInfo container)
    {
        _unsettledLaunch = null;
        AppendLog($"supervisor: the {unsettled.Label} container started after all.");
        ConfirmLaunched(unsettled.Launch.LaunchId);

        // Only a state that names another launch, or none, proves this one was retired.
        var state = _link.State;
        var named = string.Equals(state.Launch?.LaunchId, unsettled.Launch.LaunchId, StringComparison.OrdinalIgnoreCase);
        RunnerLaunchPhase? retired = unsettled.WithdrawnPhase is RunnerLaunchPhase.Superseded or RunnerLaunchPhase.Cancelled
            ? unsettled.WithdrawnPhase
            : named || state.Connection == LauncherConnection.Offline || state.IsFinal
                ? null
                : state.Launch is { } newer && newer.Sequence > unsettled.Launch.Sequence ? RunnerLaunchPhase.Superseded : RunnerLaunchPhase.Cancelled;
        _fx.Report(new LaunchReport(unsettled.Launch, retired ?? RunnerLaunchPhase.Started), StopFence());

        CompleteStart(unsettled.Template, unsettled.Label, unsettled.Image, container.ImageId, unsettled.Fingerprint, retired is not null, unsettled.Engine);
    }

    /// <summary>
    /// The unsettled launch's container is not running after all. One created and never started ran
    /// nothing: its launch failed (or was withdrawn), and says so now. One that ran is an exit, told
    /// as such by whoever records it.
    /// </summary>
    private void SettleNotRunning(UnsettledLaunch unsettled, bool neverStarted)
    {
        if (!neverStarted)
        {
            return;
        }

        // (Not sent once a Stop or shutdown was asked, whenever that was: read at the submission itself.)
        var unlessStopped = StopFence();
        if (unlessStopped.IsCancellationRequested)
        {
            return;
        }

        if (unsettled.Withdrawn)
        {
            if (unsettled.WithdrawnPhase is { } phase)
            {
                _fx.Report(new LaunchReport(unsettled.Launch, phase, unsettled.WithdrawnMessage), unlessStopped);
            }

            return;
        }

        // Withdrawn since (the latest state names another launch, or none): that is its outcome, not the failure.
        var state = _link.State;
        if (!string.Equals(state.Launch?.LaunchId, unsettled.Launch.LaunchId, StringComparison.OrdinalIgnoreCase))
        {
            if (state.Connection != LauncherConnection.Offline && !state.IsFinal)
            {
                _fx.Report(
                    new LaunchReport(unsettled.Launch, state.Launch is { } newer && newer.Sequence > unsettled.Launch.Sequence ? RunnerLaunchPhase.Superseded : RunnerLaunchPhase.Cancelled),
                    unlessStopped);
            }

            return;
        }

        AppendLog($"supervisor: could not start {unsettled.Launch.EditorTemplate} for a session: {unsettled.Message}");
        var failed = new LaunchReport(unsettled.Launch, RunnerLaunchPhase.Failed, unsettled.Reported);
        _fx.Report(failed, unlessStopped);
        if (LeavesNothingLaunchable(unsettled.Kind))
        {
            // Docker or the GPU let it down: what was checked before proves nothing now. Checked again at
            // once, which stops offering the computer until it passes, as when the failure is seen at once.
            // The launcher stays, offering nothing, until that outcome has reached Offloadr.
            _checked = _checked with { PassedFor = null };
            _refreshQueued = true;
            if (_linkRun is { } run)
            {
                Owe(failed, run, () => View.State == SupervisorState.WaitingToRetry);
            }
        }
    }

    private readonly Slot _imageSlot = new("image removal");

    /// <summary>Images a launch replaced, to be removed once no launch is under way: one could be about to use them.</summary>
    private readonly List<(string ImageId, string Reference, Selected Engine)> _superseded = [];

    private TimeSpan _supersededDueAt;

    private void RemoveSupersededImage(string imageId, string imageReference, Selected engine)
    {
        if (!_superseded.Any(known => known.ImageId == imageId && SameEngine(known.Engine, engine)))
        {
            _superseded.Add((imageId, imageReference, engine));
        }
    }

    /// <summary>
    /// Removes the images launches replaced, in a lane of its own, kept apart from pulls and launches as a
    /// removal from the Storage page is: one named again (by its digest, say) stays until its container is made.
    /// </summary>
    private void WorkOffSupersededImages()
    {
        if (_superseded.Count == 0 || _imageSlot.Busy || _flight is not null)
        {
            return;
        }

        if (_now < _supersededDueAt)
        {
            WakeAt(_supersededDueAt);
            return;
        }

        var (imageId, imageReference, engine) = _superseded[0];
        var pinned = (View.Plan?.Templates ?? [])
            .Concat(BuiltInLaunchPlanCatalog.Plan().Templates)
            .Select(static template => template.Image)
            .Where(static reference => reference.Contains('@', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // The one that runs (a rollback brought it back, say) is not superseded any more.
        if (_ownsContainer && string.Equals(imageId, _runningImageId, StringComparison.Ordinal))
        {
            _superseded.RemoveAt(0);
            return;
        }

        Start(_imageSlot, "superseded image", token => _fx.RemoveSupersededImageAsync(imageId, imageReference, engine, pinned, ImageGate, token), result =>
        {
            if (result is { Error: null, Cancelled: false, Value: null })
            {
                // A pull or launch is under way: tried again once it is done.
                _supersededDueAt = _now + _options.MonitorInterval;
                return;
            }

            _superseded.RemoveAll(known => known.ImageId == imageId && SameEngine(known.Engine, engine));
            if (View.GpuAccess is null && _ownsContainer)
            {
                CheckGpuAccessInBackground();
            }
        });
    }

    // Making room

    /// <summary>
    /// What was checked before a release that makes room counts no more (the stream dropped, the computer
    /// woke, the request was not sent, or it is asked again after a while): the next request checks
    /// everything again, from the wait for the stream on (the settings, the GPU, the name, the image).
    /// </summary>
    private void RedoRoomChecks(Room room)
    {
        var ask = room.Ask;
        KeepSleptGpuCheckOwed(ask.SleptGpu, room.SleptGpu);
        ask.Unless?.Dispose();
        ask.OfferWatch?.Dispose();
        room.Ask = new Asking();
        room.SleptGpu = null;
        room.Named = new Fact<ContainerInfo?>();
        room.NameLooks++;
        room.Stage = 0;
    }

    /// <summary>
    /// Frees the runner's place for a launch: the container running now (warm, or for other settings)
    /// is released by Offloadr and stopped. False while that is under way. When it may not be, the
    /// launch's end is set: it reported FAILED, or the release keeps being asked until the launch is
    /// withdrawn.
    /// </summary>
    private bool MakeRoom(Flight flight)
    {
        var launch = flight.Launch;
        var room = flight.Room;
        var engine = flight.Engine!;
        var template = flight.Template!;
        var label = flight.Label;
        if (room.Stop is { } stopping)
        {
            // A release granted fences the container for good: its stop finishes whatever is withdrawn.
            if (!StepStopOfReleased(stopping))
            {
                return false;
            }

            room.Stop = null;
            room.Committed = false;
            return true;
        }

        if (room.Stage == 0)
        {
            // A release fences the warm container for good: asked only while the stream still names
            // this launch, and once its image is here.
            if (!_link.MayStart(launch))
            {
                WakeIn(_options.MonitorInterval);
                return false;
            }

            // Counted from here, before the first of the checks (the name's too): a drop of the stream,
            // or a wake from sleep, during any of them has them all made again.
            room.Resumes = _in.Resumes;
            room.StreamDrops = _link.StreamDrops;
            room.Stage = 1;
        }

        if (room.Stage == 1)
        {
            // Settings saved during the download are checked before the warm container is released:
            // one the replacement could not start with costs no usable executor.
            if (SettingsStillHold(flight) is null)
            {
                return true;
            }

            // The computer slept since the GPU was checked: proved again before the warm container
            // is released, so a GPU lost in sleep costs no usable executor.
            if (!SleptGpuStillWorks(flight, room.SleptGpu, step => room.SleptGpu = step, problem =>
                {
                    FailLaunch(flight, problem.Message, problem.Kind, "Containers on this computer cannot use the GPU.");
                    _refreshQueued = true;
                    flight.End = Ended;
                }))
            {
                return flight.End is not null;
            }

            if (!OfferStillCurrent(launch))
            {
                flight.End = FailStale();
                return true;
            }

            // The name the replacement needs, on the engine it runs on: one held by another
            // container (the warm one itself excepted) would fail it after the release.
            var name = _options.ContainerName;
            if (Need(_main, room.Named, $"room name {flight.Id}.{room.NameLooks}", token => _fx.InspectContainerAsync(engine, name, token)))
            {
                return false;
            }

            if (Get(room.Named) is { } named && !IsOurContainer(named) && (!IsOwned(named) || IsRejectedContainer(named) || named.Running))
            {
                var blocked = FailLaunch(flight, NameTakenMessage(named), FaultKind.NameTaken);
                flight.End = StopOfferingAfter(blocked, NameTakenMessage(named), FaultKind.NameTaken);
                return true;
            }

            room.Stage = 2;
        }

        if (_containerLaunchId is not { } launchId || _containerLaunchTarget is not { } target)
        {
            FailLaunch(flight, EarlierAppContainer);
            flight.End = Ended;
            return true;
        }

        if (!string.Equals(target, _link.LaunchTarget, StringComparison.Ordinal))
        {
            // Only the Offloadr address and key that asked for it can release it.
            FailLaunch(flight, EarlierSettingsContainer);
            flight.End = Ended;
            return true;
        }

        if (string.Equals(launchId, _releasedLaunch, StringComparison.OrdinalIgnoreCase))
        {
            // Granted already (its stop failed before): stopped again, not asked again; a grant is for good.
            room.Committed = true;
            room.Stop = new StopOfReleased();
            return MakeRoom(flight);
        }

        // A request noted before this one (an idle release, say) may have been granted: until Offloadr's
        // release handler answers, its note stays.
        room.SentUnanswered ??= string.Equals(_releaseAsked, launchId, StringComparison.OrdinalIgnoreCase);
        if (room.RetryAt is { } retryAt)
        {
            // Asked again until Offloadr answers, or the launch is withdrawn. One that exited meanwhile
            // needs no release: the watch records its exit, and room is made.
            if (_now < retryAt)
            {
                WakeAt(retryAt);
                return false;
            }

            // Asked again with everything checked again, as for a first request: while it waited, another
            // container can have taken the name the replacement needs, or the settings changed.
            room.RetryAt = null;
            RedoRoomChecks(room);
            _lookAgain = true;
            return false;
        }

        var ask = room.Ask;
        if (ask.Id == 0)
        {
            // (As counted before the name was looked at: what changed during that look shows here too.)
            ask.Id = ++_ids;
            ask.Resumes = room.Resumes;
            ask.StreamDrops = room.StreamDrops;
        }

        var (containerEngine, container) = (_containerEngine ?? _engineSelection.Current, OurContainer);
        if (Need(_main, ask.Status, $"room status {ask.Id}", token => _fx.ReadStatusAsync(containerEngine, container, token)))
        {
            return false;
        }

        if (Once(ref ask.StatusRecorded))
        {
            if (ask.Status.Error is ContainerEngineException unread)
            {
                AppendLog($"supervisor: could not read the runner's status ({unread.Message}); asking by its launch alone.");
            }
            else
            {
                RecordStatus(Get(ask.Status));
            }
        }

        var status = ask.Status.Error is null ? ask.Status.Value : null;

        // Each new request only while the stream is live and still names this launch: a grant
        // fences the warm container for good. One already sent is honoured, granted or not.
        if (!ask.Release.Known && !_main.Busy)
        {
            if (!_link.MayStart(launch))
            {
                // What was checked for this request counts no more: a GPU on a remote engine can change
                // while the stream is down, the computer may sleep, another container can take the name
                // the replacement needs, its image can go. All of it is checked again, from the wait
                // for the stream on.
                RedoRoomChecks(room);
                WakeIn(_options.MonitorInterval);
                return false;
            }

            // Each release request (a retry too) only while the replacement could still start: one that
            // could not would cost the warm container for nothing.
            if (SettingsStillHold(flight) is not { } held)
            {
                return true;
            }

            ask.Settings = held;
        }

        // Its image still here (a cleanup outside the app may have removed it since).
        if (Need(_main, ask.Image, $"room image {ask.Id}", token => _fx.InspectImageAsync(engine, ExactImage(flight), token)))
        {
            return false;
        }

        if (Get(ask.Image) is null)
        {
            FailLaunch(flight, $"The {label} image was removed from this computer as it was being prepared.");
            flight.End = Ended;
            return true;
        }

        var dropsNow = _link.StreamDrops;
        if (!ask.Release.Known && !room.Committed && (ask.Resumes != _in.Resumes || ask.StreamDrops != dropsNow))
        {
            // The computer woke since this request's checks began, or the stream dropped and came back
            // while one of them was under way: made again, all of them (the settings, the name and the
            // image the replacement needs, the GPU), from the top.
            RedoRoomChecks(room);
            _lookAgain = true;
            return false;
        }

        if (!ask.Release.Known && !room.Committed)
        {
            // The computer slept since the GPU was checked (also while a release is retried).
            if (!SleptGpuStillWorks(flight, ask.SleptGpu, step => ask.SleptGpu = step, problem =>
                {
                    FailLaunch(flight, problem.Message, problem.Kind, "Containers on this computer cannot use the GPU.");
                    _refreshQueued = true;
                    flight.End = Ended;
                }))
            {
                return flight.End is not null;
            }

            if (ask.Relist ??= ContainersListTheirGpus())
            {
                // The offer names what a remote engine's containers reported: asked again right before
                // each request, as a GPU there can change while the launch waited (for the stream, or to
                // ask again). The warm container is not given up for a replacement that could not start.
                if (ListGpus(flight, ask.Listed, $"room list {ask.Id}") is not { } listed)
                {
                    return flight.End is not null;
                }

                if (Once(ref ask.ListedRecorded))
                {
                    _checked = _checked with { Inventory = listed };
                    if (CurrentOffer().Gpus.Gpus.Count == 0)
                    {
                        // Nothing to offer now: checked again, which says why (and offers again once it passes).
                        _checked = _checked with { PassedFor = null };
                        _refreshQueued = true;
                    }
                }
            }
            else
            {
                // With the replacement's own image, before each request: the check that passed for the offer
                // may have run in another one, and an image can need more of the driver than that one did.
                if (!ImageReachesGpu(flight, ask.ImageGpu, $"room image gpu {ask.Id}"))
                {
                    return flight.End is not null;
                }

                if (SeesOtherGpus(Get(ask.ImageGpu)))
                {
                    ask.Relist = true;
                    _lookAgain = true;
                    return false;
                }

                if (!NoteHostGpus(ask.Host, ref ask.HostRecorded, $"room host gpus {ask.Id}", ref ask.HostChanged, ref ask.HostUnread))
                {
                    return false;
                }

                if (ask.HostUnread || (ask.HostChanged && _settings.Current.SelectedGpuUuid is null))
                {
                    ask.HostUnread = false;
                    // (As before a start: a container says which GPUs containers get now.)
                    ask.HostChanged = false;
                    ask.Relist = true;
                    _lookAgain = true;
                    return false;
                }
            }

            if (!OfferStillCurrent(launch))
            {
                flight.End = FailStale();
                return true;
            }

            // Not asked unless noted first: a grant whose note is missing would let a crash reuse a fenced container.
            if (Once(ref ask.Noted) && !WriteReleaseAsked(launchId))
            {
                FailLaunch(flight, "The app could not save its note of the release in its data folder.", FaultKind.Unknown, "The app could not save its note of the release.");
                flight.End = Ended;
                return true;
            }
        }

        // Sent only while the stream still names this launch and the launch is still wanted here,
        // checked as one step with the sending once the note is on disk: a withdrawal or settings
        // change meanwhile sends none. One sent is answered, and a grant honoured. The note stays if
        // an earlier request went unanswered: that one may be granted.
        if (!ask.Release.Known)
        {
            room.Committed = true;
            var executor = status?.InstanceId;
            // Nor once other settings were saved than the ones checked just now (another engine or GPU, a
            // models folder that cannot be used): the replacement might not start, and the grant would cost
            // the warm container for nothing. Raised on the thread that saves them, and they are checked
            // here, on the loop, before it is asked again: nothing is read from disk there, as a Stop
            // pressed meanwhile looks at this too. Nor once the computer woke from sleep since the checks
            // above: the GPU is proved again first.
            var (checkedSettings, resumes) = (ask.Settings, ask.Resumes);
            ask.OfferWatch ??= _fence.Until(() => Volatile.Read(ref _intent).Resumes != resumes || !ReferenceEquals(_settings.Current, checkedSettings));
            var unless = (ask.Unless ??= CancellationTokenSource.CreateLinkedTokenSource(
                flight.Preparation.Token, CredentialsFence(flight.LinkCredentials), ask.OfferWatch.Signal)).Token;
            var checkedOn = ask.StreamDrops;
            if (Need(_main, ask.Release, $"room release {ask.Id}", token => _fx.ReleaseAsync(launchId, executor, target, within: null, onlyFor: launch, unless, token, streamDrops: checkedOn)))
            {
                return false;
            }
        }

        var result = Get(ask.Release)!;
        ask.Unless?.Dispose();
        ask.Unless = null;
        ask.OfferWatch?.Dispose();
        ask.OfferWatch = null;
        room.Committed = false;
        if (!result.Sent)
        {
            if (room.SentUnanswered != true)
            {
                ClearReleaseMarker();
            }

            // Not sent: the launch is no longer named, or no longer wanted here, or the stream is another
            // one than the checks were made on. It goes round once more, all its checks made again (a
            // withdrawal ends it there).
            RedoRoomChecks(room);
            WakeIn(_options.MonitorInterval);
            return false;
        }

        if (result is { Released: false, Reached: true, Answered: true })
        {
            // Refused for certain by the release handler (a granted one would be granted again); an
            // unanswered one, or a call refused before the handler, may have been granted, and stays noted.
            ClearReleaseMarker();
        }
        else if (!result.Released)
        {
            room.SentUnanswered = true;
        }

        if (result.Released)
        {
            AppendLog($"supervisor: Offloadr released the {_runningTemplate?.Label ?? "runner"} container for {launch.EditorTemplate}.");
            room.Committed = true;
            room.Stop = new StopOfReleased();
            room.Ask = new Asking();
            return MakeRoom(flight);
        }

        // Withdrawn while Offloadr was asked: that is the launch's outcome, not the refusal.
        if (flight.Withdrawn)
        {
            room.Ask = new Asking();
            return false;
        }

        if (result is { Reached: true, Answered: true })
        {
            // Offloadr keeps it (a session, or a start or stop for it, is pending): the launch cannot be served here.
            FailLaunch(flight, "The runner container on this computer is still in use.");
            flight.End = Ended;
            return true;
        }

        if (result.Reached)
        {
            // Refused before Offloadr's release handler looked (an authorization problem, say): no answer
            // about the container. The launch fails with that, not as "in use"; the note stays.
            FailLaunch(flight, $"Offloadr could not be asked to release the runner container here: {result.Message}");
            flight.End = Ended;
            return true;
        }

        room.SentUnanswered = true;
        AppendLog("supervisor: could not ask Offloadr to release the running container; asking again shortly.");
        room.RetryAt = _now + _options.ReleaseRetryInterval;
        WakeAt(room.RetryAt.Value);
        return false;
    }
}
