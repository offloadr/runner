using Microsoft.Extensions.Logging;
using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Launcher;
using Offloadr.Runner.Supervisor.Settings;
using Selected = Offloadr.Runner.Supervisor.Containers.ContainerEngineSelection.Selected;

namespace Offloadr.Runner.Supervisor;

/// <content>
/// The container that runs: watching it, recording its exit, releasing it once idle, and stopping it
/// for a Stop or as the app closes. Run again from the top on every tick (see the class remarks).
/// </content>
public sealed partial class RunnerSupervisor
{
    // Watching

    /// <summary>The lane that looks at the supervised container, whatever the main procedure does meanwhile.</summary>
    private readonly Slot _watchSlot = new("watch");

    /// <summary>Raised whenever the supervised container changes or a job starts that changes it: a look begun before does not count.</summary>
    private long _watchEpoch;

    /// <summary>A job is starting, stopping or removing the container: it is not watched until that job's result is in.</summary>
    private bool _containerBusy;
    private bool _watchNow;
    private TimeSpan? _watchedAt;

    /// <summary>The watch is removing the container it saw stopped: no procedure steps until that is recorded.</summary>
    private bool _removingExited;

    private void ContainerChanged() => _watchEpoch++;

    private void ContainerBusy(bool busy)
    {
        _containerBusy = busy;
        _watchEpoch++;
    }

    /// <summary>
    /// Looks at the supervised container every <see cref="SupervisorOptions.MonitorInterval"/>: also while
    /// a launch that will replace it downloads, so a session it takes meanwhile shows, and its exit is
    /// recorded (and told to Offloadr) when it happens, not when the download ends.
    /// </summary>
    private void StepWatch()
    {
        // Whenever one is owned and no job is changing it: also after a Stop that failed (the view then
        // needs attention), so its later exit is still recorded and told to Offloadr.
        var watched = _ownsContainer && !_containerBusy && _stop is null && View.State != SupervisorState.Stopping;
        if (!watched)
        {
            return;
        }

        if (_watchSlot.Busy)
        {
            return;
        }

        var interval = _monitorFailures > 0
            ? RegistrationPolicy.ComputeBackoff(_monitorFailures - 1, _options.MonitorInterval, MonitorRetryMaxBackoff)
            : _options.MonitorInterval;
        var dueAt = (_watchedAt ?? TimeSpan.MinValue) + interval;
        if (!_watchNow && _watchedAt is not null && _now < dueAt)
        {
            WakeAt(dueAt);
            return;
        }

        _watchNow = false;
        _watchedAt = _now;
        var epoch = _watchEpoch;
        var (engine, id, attempt) = (_containerEngine ?? _runningEngine ?? _engineSelection.Current, _containerId, _attemptId);
        var readKey = _adoptedLaunchPending;
        var readStatus = _statusReadTimestamp is not { } lastRead || _time.GetElapsedTime(lastRead) >= _options.StatusInterval;
        Start(_watchSlot, "watch", token => _fx.ObserveAsync(engine, id, attempt, readKey, readStatus, token), result =>
        {
            if (result.Cancelled || epoch != _watchEpoch || !_ownsContainer)
            {
                // The container changed, or a job that changes it began, since this look: it says nothing now.
                return;
            }

            if (result.Error is ContainerEngineException failure)
            {
                NoteWatchFailed(failure);
            }
            else if (result.Error is { } unexpected)
            {
                _logger.LogError(unexpected, "Runner supervision step failed.");
                AppendLog($"supervisor: Unexpected error: {unexpected.Message}");
            }
            else
            {
                RecordObservation(result.Value!, engine);
            }
        });
    }

    /// <summary>
    /// Docker failing to answer (while Docker Desktop restarts, say) says nothing about the container,
    /// so the runner stays supervised and the look is tried again with a backoff rather than faulting
    /// and losing track of it.
    /// </summary>
    private void NoteWatchFailed(ContainerEngineException failure)
    {
        _logger.LogWarning("Docker did not answer while checking the runner: {Error}", SupervisorEffects.Told(failure));
        if (_monitorFailures++ != 0)
        {
            return;
        }

        AppendLog($"supervisor: Docker did not answer ({failure.Message}); still watching the runner.");
        if (View.State == SupervisorState.Running)
        {
            _messageBeforeMonitorFailure = View.Message;
            Update(view => view with { Message = "Docker is not answering. The runner is still watched and checked again shortly." });
        }
        else
        {
            // Another state has its own message (a Stop that failed, say), which stays.
            _messageBeforeMonitorFailure = null;
        }


        // Docker coming back may not bring the GPU with it: checked again, however it comes back.
        _checked = _checked with { PassedFor = null };
        if (_linkRun is not null)
        {
            // No launch could start meanwhile: the computer is not offered until Docker answers.
            // The container stays supervised.
            if (_unsettledLaunch is null && !HoldsRevision())
            {
                CancelLink();
            }
            else
            {
                // A launch still owes Offloadr its outcome, which only the engine's answer can settle, or is
                // still named, its container started and no executor matched yet: the launcher stays to deliver
                // what is reported, offering no editor. Its offer's revision stays as it is: a higher one would
                // retire that launch, whose container may have started.
                PauseOffer();
            }

            _linkSuspended = true;
            _resumeAvailabilityAt = TimeSpan.Zero;
        }
    }

    private void RecordObservation(SupervisorEffects.Observation seen, Selected engine)
    {
        if (seen.LearnedId is { } learned)
        {
            _containerId = learned;
        }

        if (seen.Taker is { } taker)
        {
            // Another container took the name between checks: it is not followed, read or stopped as
            // if it were the runner.
            AppendLog("supervisor: a different container now has the runner's name.");
            _rejectedContainerId = NormalizeContainerId(taker.Id);
        }

        // The engine answered, whatever it said of the container.
        if (_monitorFailures > 0)
        {
            _monitorFailures = 0;
            AppendLog("supervisor: Docker is answering again.");
            var message = _messageBeforeMonitorFailure;
            if (message is not null && View.State == SupervisorState.Running)
            {
                Update(view => view with { Message = message });
            }
        }

        if (_linkSuspended && _now >= _resumeAvailabilityAt)
        {
            _linkSuspended = false;
            if (_in.Wanted)
            {
                // Offered again, the GPU proved first for the next launch.
                _checked = _checked with { PassedFor = null };
                _refreshQueued = true;
            }
        }

        if (seen.Container is not { Running: true })
        {
            NoteExited(seen.Container, engine);
            return;
        }

        if (_unsettledLaunch is { } unsettled)
        {
            // The engine answers: the start whose outcome it had not told did start it.
            SettleStarted(unsettled, seen.Container);
        }

        // Seen running: its absence, from now on, is an exit.
        _seenRunning = true;
        ConfirmLaunched(_containerLaunchId);
        if (seen.KeyRead)
        {
            // What the adopted runner was started with, so its log can be followed redacted.
            _adoptedLaunchPending = false;
            RememberStartedWith(_containerLaunchId, seen.Origin, seen.Key);
            if (seen.Key is { } key)
            {
                Volatile.Write(ref _activeRunnerKey, key);
                if (_logWanted is null)
                {
                    StartLogFollower();
                }
            }
        }

        ResumeLogFollowerIfStopped();
        if (seen.StatusRead)
        {
            RecordStatus(seen.Status);
        }

        if (HasCurrentSession())
        {
            _idleSince = _now;
            SetReleaseDue(false);
        }

        if (_now >= PlanRefreshDue())
        {
            RefreshPlanInBackground();
        }
    }

    // The container's own exit

    /// <summary>
    /// The container was seen stopped, or is not there. One the engine still has is removed first (it
    /// holds the runner key), never by force: one started again since stays supervised, and nothing is recorded.
    /// </summary>
    private void NoteExited(ContainerInfo? container, Selected engine)
    {
        if (container is null || !IsOwned(container))
        {
            RecordExit(container);
            return;
        }

        var epoch = _watchEpoch;
        var target = Target(container);

        // Seen through before a Stop, a launch or the app closing touches the container: what it removes,
        // and what that was (never started, or exited), must not be lost to whatever comes next.
        _removingExited = true;
        _seenCreated |= container.NeverStarted;
        if (!container.NeverStarted && _containerLaunchId is { } ranLaunch)
        {
            // Saved before it is removed: with the answer to that lost, nothing would show that it ran.
            NoteRan(ranLaunch);
        }

        Start(_watchSlot, "remove exited", token => _fx.RemoveExitedAsync(engine, container, token), result =>
        {
            _removingExited = false;
            if (result.Cancelled || epoch != _watchEpoch || !_ownsContainer)
            {
                return;
            }

            if (result.Error is ContainerEngineException failure)
            {
                NoteWatchFailed(failure);
                return;
            }

            // (One the engine saw run meanwhile is no container that never started, whatever it was before.)
            _seenRunning |= result.Value is { Started: true };
            var exited = result.Value is { Started: true } ? container with { NeverStarted = false } : container;
            switch (result.Value?.What)
            {
                case SupervisorEffects.ExitedRemoval.Removed:
                    RecordExit(exited);
                    break;
                case SupervisorEffects.ExitedRemoval.Kept when result.Value.StillCreated && StartMayBeUnderWay:
                    // Still only created, after a start the engine never refused: it may start it yet, so
                    // nothing says it serves nothing. Not recorded as an exit, nor let go of: it stays
                    // supervised until it is removed, or seen running or exited.
                    if (Once(ref _keptCreatedNoted))
                    {
                        AppendLog($"warning: {result.Value.Problem} The runner container has not started, and may yet; it stays on the container engine, and is checked on.");
                    }

                    break;
                case SupervisorEffects.ExitedRemoval.Kept:
                    // It exited all the same: recorded as such. Left on its engine, still holding the runner
                    // key, it is removed there later.
                    AppendLog($"warning: {result.Value.Problem} The runner container stopped but stays on the container engine for now.");
                    NoteLeftover(engine, target);
                    RecordExit(exited);
                    break;
                default:
                    // Started again meanwhile: still supervised, and released before it stops.
                    break;
            }
        });
    }

    /// <summary>
    /// What an exit leaves: logged with its exit code, counted, told to Offloadr unless it released the
    /// container, and a session it served noted as interrupted. The container is not started again:
    /// Offloadr launches a new one when a session needs it.
    /// </summary>
    private void RecordExit(ContainerInfo? container)
    {
        StopLogFollower();
        var interrupted = _sessionSeen || View.HasActiveSession;
        var exit = container?.ExitCode switch
        {
            RunnerProtocolVersion.ExecutorFencedExitCode => " because Offloadr no longer needs it",
            RunnerProtocolVersion.ProtocolRejectedExitCode => " because Offloadr needs a different runner version; the next session downloads it",
            { } code => $" with exit code {code}",
            null => string.Empty,
        };
        AppendLog($"supervisor: the runner container stopped{exit}.");

        var granted = IsReleased(_containerLaunchId);
        if (string.Equals(_containerLaunchId, _releasedLaunch, StringComparison.OrdinalIgnoreCase) || string.Equals(_containerLaunchId, _releaseAsked, StringComparison.OrdinalIgnoreCase))
        {
            ClearReleaseMarker();
        }

        // One created and never started ran nothing: no exit is reported for it. (Not one whose start
        // was asked for and never refused: that may have run.)
        var neverRan = NeverRan(container is { NeverStarted: true } || (container is null && _seenCreated));
        var unsettled = _unsettledLaunch;
        if (unsettled is not null)
        {
            // The engine answers: the start whose outcome it had not told left this.
            SettleNotRunning(unsettled, neverRan);
            interrupted = false;
        }

        if (!granted && !neverRan && _containerLaunchId is { } exited)
        {
            // It ran and exited by itself: its launch never fails for that, whenever it is named again.
            _exitedLaunches.Add(exited);
        }

        AbandonContainer(granted ? ContainerFate.Released : neverRan ? ContainerFate.NeverStarted : ContainerFate.Gone);

        SelectCheckedEngine(_settings.Current);
        Update(view => view with
        {
            Interruption = interrupted ? $"The session on this computer was interrupted: the runner container stopped{exit}." : view.Interruption,
            UnexpectedExits = view.UnexpectedExits + (container?.ExitCode is RunnerProtocolVersion.ExecutorFencedExitCode ? 0 : 1),
        });

        if (_flight is not null || _online is not null || _stop is not null)
        {
            // The procedure in progress goes on without it: a launch needs no room made, the checks
            // begin again for a computer with no container.
            return;
        }

        SettleIdle();
        if (_in.Wanted)
        {
            // The settings may name another engine or GPU than the one that ran it, checked before
            // the computer is offered on.
            _refreshQueued = true;
        }
    }

    // Releasing an idle container

    private sealed class IdleRelease(bool outdated, bool granted) : IDisposable
    {
        /// <summary>The settings it was started with changed: released at once, not after the warm time.</summary>
        public bool Outdated { get; } = outdated;

        /// <summary>Offloadr granted its release (now, or before a stop that failed): it is stopped, never reused.</summary>
        public bool Granted { get; set; } = granted;

        /// <summary>The warm time, in minutes, in the settings that decided the release.</summary>
        public int WarmMinutes { get; init; }

        public long Id { get; set; }

        public Fact<RunnerStatusSnapshot?> Status { get; } = new();

        public bool StatusRecorded;

        public bool? AskedBefore { get; set; }

        public bool Noted;

        /// <summary>Cancels a release not sent yet when Settings name another address or key.</summary>
        public CancellationTokenSource Unsent { get; } = new();

        public CancellationTokenSource? UnlessSent { get; set; }

        /// <summary>Raised as soon as Settings ask again for the container as it is, for a release that only changed settings called for.</summary>
        public Fence.Watch? ReasonWatch { get; set; }

        public void Dispose()
        {
            UnlessSent?.Dispose();
            ReasonWatch?.Dispose();
            Unsent.Dispose();
        }

        public Fact<ReleaseResult?> Release { get; } = new();

        public StopOfReleased? Stop { get; set; }
    }

    /// <summary>
    /// Decides whether the container is to be released now: once it has been idle for the warm time, or
    /// at once when the settings it was started with changed. Offloadr decides: it refuses while anything
    /// is assigned. True when a release begins.
    /// </summary>
    private bool CheckIdleContainer()
    {
        if (!_ownsContainer || _containerBusy || _unsettledLaunch is not null)
        {
            // (One whose start is not settled yet is neither idle nor serving: the engine says first.)
            return false;
        }

        if (_containerLaunchId is { } released && string.Equals(released, _releasedLaunch, StringComparison.OrdinalIgnoreCase))
        {
            if (_now < _releaseRetryAt)
            {
                // Its last stop failed a moment ago: tried again shortly, not at once.
                return false;
            }

            // Released already (a stop after the grant failed): stopped again, without asking, whatever
            // the warm time or the launcher. A grant is for good.
            AppendLog("supervisor: stopping the runner container Offloadr released.");
            _idle = new IdleRelease(outdated: false, granted: true);
            return true;
        }

        // Only the Offloadr address and key that launched it can release it; one launched for earlier
        // settings, or by an earlier app version, runs until it exits or the person stops it.
        if (_linkRun is null || _containerLaunchId is null || _containerLaunchTarget is not { } target ||
            HasCurrentSession() || _now < _releaseRetryAt || !string.Equals(target, _link.LaunchTarget, StringComparison.Ordinal))
        {
            return false;
        }

        var settings = _settings.Current;
        if (LinkCredentials != Credentials(settings))
        {
            // Settings name another address or key, whose launcher is not running yet: the container
            // of the earlier ones is left as Stop or Quit would leave it to the person.
            return false;
        }

        if (IdleReleaseReason(settings) is not { } outdated)
        {
            return false;
        }

        _idle = new IdleRelease(outdated, granted: false) { Id = ++_ids, WarmMinutes = settings.WarmMinutes };
        return true;
    }

    /// <summary>
    /// Why the idle container is released now: Settings ask for another container than it is (true), or
    /// its warm time is over (false). Null while neither holds: it stays warm.
    /// </summary>
    private bool? IdleReleaseReason(RunnerSettings settings)
    {
        var outdated = settings.Validate().Count == 0 &&
                       _runningTemplate is { } template &&
                       (!string.Equals((_containerEngine ?? _engineSelection.Current).Configured, ContainerEngineSelection.Normalize(settings.ContainerEngineExecutable), StringComparison.Ordinal) ||
                        !string.Equals(Fingerprint(BuildSpec(settings, template)), _activeFingerprint, StringComparison.Ordinal));
        return outdated ? true : _releaseDue || _now - _idleSince >= WarmTimeFor(settings) ? false : null;
    }

    private bool StepIdleRelease(IdleRelease idle)
    {
        try
        {
            return StepIdleReleaseCore(idle);
        }
        catch (ContainerEngineException failure)
        {
            // Still supervised; the release is asked again (Offloadr grants a repeat) and the stop retried,
            // after a pause: an engine that keeps refusing is not asked on every tick.
            Cancel(_main);
            _releaseRetryAt = _now + _options.ReleaseRetryInterval;
            NoteWatchFailed(failure);
            return true;
        }
    }

    private bool StepIdleReleaseCore(IdleRelease idle)
    {
        if (!_ownsContainer)
        {
            // It exited meanwhile: recorded by the watch.
            if (Cancel(_main))
            {
                return false;
            }

            SettleIdle();
            return true;
        }

        if (_in.Stops > _stopsDone && !idle.Granted)
        {
            // The person asked for a Stop: it does not wait for this release to be answered. The note that
            // it was asked stays (it may yet be granted), and the Stop asks again, briefly, itself.
            return !Cancel(_main);
        }

        if (idle.Granted)
        {
            if (!StepStopOfReleased(idle.Stop ??= new StopOfReleased()))
            {
                return false;
            }

            SettleIdle();
            if (idle.Outdated)
            {
                // The changed settings may name another engine or GPU, which is checked before the next launch.
                _refreshQueued = true;
            }

            return true;
        }

        if (_containerLaunchId is not { } launchId || _containerLaunchTarget is not { } target)
        {
            return true;
        }

        var (engine, container) = (_containerEngine ?? _engineSelection.Current, OurContainer);
        if (Need(_main, idle.Status, $"idle status {idle.Id}", token => _fx.ReadStatusAsync(engine, container, token)))
        {
            return false;
        }

        if (Once(ref idle.StatusRecorded))
        {
            if (idle.Status.Error is ContainerEngineException unread)
            {
                AppendLog($"supervisor: could not read the runner's status ({unread.Message}); asking by its launch alone.");
            }
            else
            {
                RecordStatus(Get(idle.Status));
            }
        }

        if (!idle.Release.Known && !_main.Busy)
        {
            if (HasCurrentSession())
            {
                _idleSince = _now;
                SetReleaseDue(false);
                return true;
            }

            if (!idle.Outdated && !_releaseDue && _options.WarmTime is null && _settings.Current.WarmMinutes != idle.WarmMinutes)
            {
                // Another warm time was saved since its end decided this: decided again.
                return true;
            }

            // Not sent once Settings name another address or key: a container of the earlier ones is
            // left as Stop or Quit would leave it to the person.
            if (LinkCredentials != Credentials(_settings.Current) || _in.Stops > _stopsDone)
            {
                return true;
            }

            // Nor once its reason is gone (the settings were changed back while its status was read): a
            // grant is for good, and would cost the next session a cold start for nothing.
            if (IdleReleaseReason(_settings.Current) is null)
            {
                return true;
            }

            // An earlier request may have gone unanswered (and been granted): its note stays whatever happens now.
            // Not asked unless noted first: a grant whose note is missing would let a crash reuse a fenced container.
            idle.AskedBefore ??= string.Equals(_releaseAsked, launchId, StringComparison.OrdinalIgnoreCase);
            if (Once(ref idle.Noted) && !WriteReleaseAsked(launchId))
            {
                _releaseRetryAt = _now + _options.ReleaseRetryInterval;
                return true;
            }
        }

        var executor = idle.Status.Error is null ? idle.Status.Value?.InstanceId : null;
        if (idle.UnlessSent is null && _runningTemplate is { } runs && _activeFingerprint is { } fingerprint &&
            !_releaseDue && _now - _idleSince < WarmTimeFor(_settings.Current))
        {
            // Only changed settings call for this release (its warm time is not over): not sent once they
            // are changed back, on the thread that saves them. A grant is for good, and would cost the
            // next session a cold start for nothing.
            var configured = (_containerEngine ?? _engineSelection.Current).Configured;
            idle.ReasonWatch = _fence.Until(() =>
            {
                try
                {
                    var saved = _settings.Current;
                    return saved.HasRunnerKey &&
                           string.Equals(configured, ContainerEngineSelection.Normalize(saved.ContainerEngineExecutable), StringComparison.Ordinal) &&
                           string.Equals(Fingerprint(BuildSpec(saved, runs)), fingerprint, StringComparison.Ordinal);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
                {
                    // Settings that cannot describe a container ask for none: the reason stands.
                    return false;
                }
            });
        }
        else if (idle.UnlessSent is null && !_releaseDue && _options.WarmTime is null)
        {
            // Its warm time is over: not sent once a warm time other than the one that decided it is saved.
            var minutes = idle.WarmMinutes;
            idle.ReasonWatch = _fence.Until(() => _settings.Current.WarmMinutes != minutes);
        }

        var unsent = (idle.UnlessSent ??= idle.ReasonWatch is { } reason
            ? CancellationTokenSource.CreateLinkedTokenSource(idle.Unsent.Token, CredentialsFence(LinkCredentials), reason.Signal)
            : CancellationTokenSource.CreateLinkedTokenSource(idle.Unsent.Token, CredentialsFence(LinkCredentials))).Token;
        if (Need(_main, idle.Release, $"idle release {idle.Id}", token => _fx.ReleaseAsync(launchId, executor, target, within: null, onlyFor: null, unsent, token)))
        {
            return false;
        }

        var result = Get(idle.Release)!;
        if (!result.Sent)
        {
            if (idle.AskedBefore != true)
            {
                ClearReleaseMarker();
            }

            _releaseRetryAt = _now + _options.ReleaseRetryInterval;
            return true;
        }

        if (!result.Released)
        {
            if (result.Reached)
            {
                // Something is assigned to it, or about to be: it serves, and idles again afterwards.
                // Refused for certain by the release handler, so no longer noted; an unanswered one, or a
                // call refused before the handler, may have been granted.
                if (result.Answered)
                {
                    ClearReleaseMarker();
                }

                AppendLog("supervisor: Offloadr keeps the runner container for now.");
                _idleSince = _now;
                SetReleaseDue(false);
            }

            _releaseRetryAt = _now + _options.ReleaseRetryInterval;
            return true;
        }

        AppendLog(idle.Outdated
            ? "supervisor: stopping the idle runner container, which uses earlier settings."
            : "supervisor: stopping the runner container, idle for the warm time.");
        idle.Granted = true;
        return StepIdleReleaseCore(idle);
    }

    // Stopping the container

    /// <summary>The stop of a container whose release Offloadr granted. Never reused, even after a crash in between.</summary>
    private sealed class StopOfReleased
    {
        public bool Begun;

        public string? MessageBefore { get; set; }

        public ContainerStop Stop { get; } = new();
    }

    private bool StepStopOfReleased(StopOfReleased stopping)
    {
        if (Once(ref stopping.Begun))
        {
            // Granted for good: not shown as kept warm, also while its stop is retried.
            SetReleaseDue(true);
            WriteReleaseMarker(_containerLaunchId);
            stopping.MessageBefore = View.Message;
            Set(SupervisorState.Stopping, "Stopping the runner container…");
        }

        try
        {
            if (!StepContainerStop(stopping.Stop))
            {
                return false;
            }
        }
        catch (ContainerEngineException)
        {
            // Still supervised; the release is asked again (Offloadr grants a repeat) and the stop retried.
            Set(SupervisorState.Running, stopping.MessageBefore ?? View.Message);
            throw;
        }

        ClearReleaseMarker();
        return true;
    }

    /// <summary>
    /// Takes a runner container of this app that a look found running, while none is supervised, as the
    /// container to stop: known by its ID, its launch and the engine it runs on. True when one was taken.
    /// </summary>
    private bool TakeFoundForStop()
    {
        if (_ownsContainer)
        {
            return false;
        }

        if (_foundRunning is not { } found)
        {
            return false;
        }

        _foundRunning = null;
        TakeForStop(found.Container, found.Engine);
        return true;
    }

    private void TakeForStop(ContainerInfo container, Selected engine)
    {
        AppendLog("supervisor: stopping a runner container this app started earlier, found on the engine it runs on.");
        _ownsContainer = true;
        _containerId = NormalizeContainerId(container.Id);
        _attemptId = container.Labels.TryGetValue(LaunchLabel, out var attempt) ? attempt : null;
        _containerLaunchId = container.Labels.TryGetValue(LaunchIdLabel, out var launchId) ? launchId : null;
        _containerLaunchTarget = container.Labels.TryGetValue(LaunchTargetLabel, out var target) ? target : null;
        _containerEngine = engine;
        ConfirmLaunched(_containerLaunchId);
        if (_containerLaunchId is { } taken)
        {
            // (Known by its launch before: it is this one.)
            _strays.Remove(taken);
            if (_foundRunning is { } waiting && waiting.Container.Labels.TryGetValue(LaunchIdLabel, out var queued) &&
                string.Equals(queued, taken, StringComparison.OrdinalIgnoreCase))
            {
                _foundRunning = null;
            }
        }

        ContainerChanged();
    }

    /// <summary>The stops of runners known to run beside the supervised one, one after the other.</summary>
    private sealed class StrayStops
    {
        public long Id { get; set; }

        public (string LaunchId, ContainerInfo Container, Selected Engine)? Current { get; set; }

        public Fact<ReleaseResult?> Release { get; set; } = new();

        public bool Released { get; set; }

        /// <summary>An earlier request of it may have gone unanswered (and been granted): its note stays.</summary>
        public bool? AskedBefore { get; set; }

        public Fact<SupervisorEffects.StrayStopped> Stopped { get; set; } = new();

        /// <summary>The engine confirmed the stop: only the removal is still under way.</summary>
        public bool Confirmed { get; set; }
    }

    /// <summary>
    /// Stops every runner container of this app known to run beside the supervised one (left on an engine
    /// the person moved on from, which answers again): each on its own engine, by its ID, released first
    /// if Offloadr grants that. Whatever became of the supervised container's own stop: an engine that
    /// does not answer must not leave a runner running on one that does. False while that is under way.
    /// </summary>
    /// <param name="mayBegin">False once there is no time left: only what was done already is recorded.</param>
    private bool StepStrayStops(StrayStops stops, bool mayBegin = true)
    {
        while (true)
        {
            if (stops.Current is null)
            {
                if (_strays.Count == 0 || !mayBegin)
                {
                    return true;
                }

                var next = _strays.First();
                _strays.Remove(next.Key);
                stops.Current = (next.Key, next.Value.Container, next.Value.Engine);
                stops.Release = new Fact<ReleaseResult?>();

                // (Granted before a stop of it that failed: for good, and not asked again.)
                stops.Released = ReleaseNoted(next.Key) == ReleaseNote.Granted;
                stops.AskedBefore = null;
                stops.Stopped = new Fact<SupervisorEffects.StrayStopped>();
                stops.Confirmed = false;
                stops.Id = ++_ids;
                AppendLog("supervisor: stopping a runner container this app started earlier, found on the engine it runs on.");
            }

            var (launchId, container, engine) = stops.Current.Value;
            var target = container.Labels.TryGetValue(LaunchTargetLabel, out var launchedFor) ? launchedFor : null;
            var (askAs, grace) = (ReportAsFor(launchId, target, _settings.Current), StopGrace);
            if (target is not null && !stops.Released && (mayBegin || stops.Release.Known))
            {
                // Asked, briefly, and stopped whatever the answer. Noted before it is asked and once it is
                // granted, before the stop: should that not happen (a crash, an engine that stops
                // answering), one Offloadr released is never taken up again.
                if (!stops.Release.Known && !_main.Busy)
                {
                    stops.AskedBefore ??= ReleaseNoted(launchId) != ReleaseNote.None;
                    NoteRelease(launchId, ReleaseNote.Asked);
                }

                var within = _options.StopReleaseTimeout;
                if (Need(_main, stops.Release, $"stray release {stops.Id}", token => _fx.ReleaseAsync(launchId, executor: null, target, within, onlyFor: null, CancellationToken.None, token, askAs)))
                {
                    return false;
                }

                if (stops.Release is { Error: null, Value: { Released: true } })
                {
                    stops.Released = true;
                    NoteRelease(launchId, ReleaseNote.Granted);
                }
                else if (stops.Release is { Error: null, Value.Sent: false } && stops.AskedBefore != true)
                {
                    // Not sent at all: nothing was granted.
                    NoteRelease(launchId, ReleaseNote.None);
                }
            }

            if (!mayBegin && !stops.Stopped.Known)
            {
                return true;
            }

            var stopping = stops.Id;
            void Confirmed() => Post(() => stops.Confirmed |= stops.Id == stopping);
            if (Need(_main, stops.Stopped, $"stray stop {stops.Id}", token => _fx.StopStrayAsync(engine, container, grace, Confirmed, token)))
            {
                return false;
            }

            stops.Current = null;
            if (stops.Stopped.Error is not null || stops.Stopped.Value is not { } stopped)
            {
                // Its engine does not answer after all: its record stays, and it is looked for again.
                _logger.LogWarning("Could not stop a runner container on the engine it runs on: {Error}", stops.Stopped.Error?.GetType().Name);
                continue;
            }

            if (stopped.Leftover is { } leftover)
            {
                NoteLeftover(engine, leftover);
            }

            if (stops.Released)
            {
                ForgetLaunched(launchId);
            }
            else if (target is not null)
            {
                // It ran, and was stopped without a grant: told to Offloadr, and its launch never fails for it.
                _exitedLaunches.Add(launchId);
                NoteGone(launchId, target, executor: null);
            }
        }
    }

    private sealed class ContainerStop
    {
        public long Id { get; set; }

        public bool WasFollowing { get; set; }

        public Fact<SupervisorEffects.StoppedContainer> Stopped { get; set; } = new();

        /// <summary>It was taken for created, and runs: Offloadr is asked to release it before it is stopped.</summary>
        public ReleaseBeforeStop? Late { get; set; }

        /// <summary>The engine confirmed the stop: only the removal is still under way.</summary>
        public bool Confirmed { get; set; }

        public bool Recorded;
    }

    /// <summary>
    /// Stops and removes the supervised container (or, owning none, a runner of this app left under the
    /// name). False while that is under way. Throws when the engine does not answer: the runner may
    /// still be running, stays supervised, and its log keeps being shown.
    /// </summary>
    private bool StepContainerStop(ContainerStop stop)
    {
        if (stop.Id == 0)
        {
            stop.Id = ++_ids;
            stop.WasFollowing = _logWanted is not null;
            StopLogFollower();
            ContainerBusy(true);
        }

        if (!stop.Stopped.Known && !_main.Busy && !_ownsContainer && View.Engine?.Available == false)
        {
            // Nothing is owned, and the engine did not answer the last time it was asked: nothing to stop.
            ContainerBusy(false);
            _activeFingerprint = null;
            Volatile.Write(ref _activeRunnerKey, null);
            SelectCheckedEngine(_settings.Current);
            return true;
        }

        var (engine, id, attempt, grace) = (_ownsContainer ? _containerEngine ?? _engineSelection.Current : _engineSelection.Current, _containerId, _attemptId, StopGrace);

        // (Its start asked for and never refused: kept still only created, it is not let go of as stopped.)
        var mayStart = _ownsContainer && StartMayBeUnderWay;

        // Seen to have run, whatever becomes of the stop: it is no container that never started, also when
        // its start was never asked for here (someone else started it).
        void Ran() => Post(() =>
        {
            if (_ownsContainer && string.Equals(_attemptId, attempt, StringComparison.Ordinal) && (attempt is not null || string.Equals(_containerId, id, StringComparison.Ordinal)))
            {
                _seenRunning = true;
                ConfirmLaunched(_containerLaunchId);
            }
        });
        void Confirmed() => Post(() => stop.Confirmed = true);

        // (Noted as one that never started, or not known at all, it was not asked to be released: found
        // running, it is first.)
        var unasked = !_ownsContainer ||
                      (_containerLaunchId is { } unaskedLaunch && _launched.TryGetValue(unaskedLaunch, out var noted) && noted.RanNothingIfCreated);
        if (Need(_main, stop.Stopped, $"stop container {stop.Id}", token => _fx.StopContainerAsync(engine, id, attempt, grace, mayStart, unasked, Ran, Confirmed, token)))
        {
            return false;
        }

        if (stop.Stopped is { Error: null, Value.Running: { } running })
        {
            // Nothing was asked of Offloadr before, as it was noted as one that never started, or was not known
            // (the engine did not answer until now): asked now, briefly, so nothing is assigned to it while it
            // stops. From here on it is the one to stop, by its ID.
            if (!_ownsContainer)
            {
                TakeForStop(running, engine);
            }

            _seenRunning = true;
            ConfirmLaunched(_containerLaunchId);
            if (!StepReleaseBeforeStop(stop.Late ??= new ReleaseBeforeStop()))
            {
                return false;
            }

            stop.Late = null;
            stop.Stopped = new Fact<SupervisorEffects.StoppedContainer>();
            stop.Id = ++_ids;
            _lookAgain = true;
            return false;
        }

        if (!Once(ref stop.Recorded))
        {
            return true;
        }

        ContainerBusy(false);
        if (stop.Stopped.Error is { } failure)
        {
            if (failure is ContainerEngineException && stop.WasFollowing)
            {
                StartLogFollower(resume: true);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }

        var stopped = stop.Stopped.Value!;
        if (stopped.Leftover is { } leftover)
        {
            NoteLeftover(engine, leftover);
        }

        if (_ownsContainer)
        {
            // Not found by the time the settle window ended is no proof none was created: the daemon may
            // still create it. Its record stays, and it is looked for by its launch label later.
            AbandonContainer(stopped.NeverCreated ? ContainerFate.Unknown
                : stopped.NeverStarted || (!stopped.Stopped && _seenCreated && NeverRan(true)) ? ContainerFate.NeverStarted
                : null);
        }
        else
        {
            // Nothing was owned, so a remembered engine (a runner that may still run there, not yet
            // checked) is kept: the next start looks there first.
            _activeFingerprint = null;
            Volatile.Write(ref _activeRunnerKey, null);
        }

        SelectCheckedEngine(_settings.Current);
        return true;
    }

    private sealed class ReleaseBeforeStop
    {
        public long Id { get; set; }

        public bool? Asks { get; set; }

        public RunnerSettings? AskAs { get; set; }

        public TimeSpan Deadline { get; set; }

        public Fact<RunnerStatusSnapshot?> Status { get; } = new();

        public bool Noted;

        /// <summary>An earlier request of it may have gone unanswered (and been granted): its note stays.</summary>
        public bool? AskedBefore { get; set; }

        public bool Cleared;

        public Fact<ReleaseResult?> Release { get; } = new();
    }

    /// <summary>
    /// Before a Stop or Quit stops a launched container anyway, asks Offloadr once, briefly, to release
    /// it: granted, nothing more is assigned to it while it stops. Its answer does not change the stop.
    /// With no launcher running (an adopted container, AutoStart off), it is asked all the same, with the
    /// address and key in Settings, and nothing is registered for it: registering only to ask could take
    /// the runner over from another computer. False while it is asked.
    /// </summary>
    private bool StepReleaseBeforeStop(ReleaseBeforeStop release)
    {
        if (release.Asks is null)
        {
            release.Id = ++_ids;
            // Of the launcher that runs for the runner it was launched for. With none (an adopted container
            // with AutoStart off, or one launched for an address or key Settings no longer hold), it is asked
            // all the same, registering nothing: as the launcher that last ran for that runner, or with the
            // address and key in Settings if they name it. A release is decided on the executor's state,
            // whoever asks, and only the Offloadr that launched it can say.
            release.AskAs = ReportAsFor(_containerLaunchId, _containerLaunchTarget, _settings.Current);
            release.Asks = _ownsContainer && _containerLaunchId is not null && _containerLaunchTarget is not null &&
                           !IsReleased(_containerLaunchId) &&
                           // One that was only created (its start never asked for) runs nothing to release:
                           // a grant would fence its launch for good, where removing it costs nothing.
                           !(_launched.TryGetValue(_containerLaunchId, out var noted) && noted.RanNothingIfCreated);

            // Shorter while Windows shuts down, which ends the app within seconds.
            release.Deadline = _now + (_in.FastStop ? Min(_options.StopReleaseTimeout, TimeSpan.FromSeconds(2)) : _options.StopReleaseTimeout);
        }

        if (release.Asks != true || _containerLaunchId is not { } launchId || _containerLaunchTarget is not { } target)
        {
            return true;
        }

        // The executor named only from this very container's status, read now by its ID, briefly. Before
        // the creation returned one, the name may belong to another container: none is named then. The
        // instance is optional: unread, the release names the launch alone.
        string? executor = null;
        if (_containerId is { } id)
        {
            var engine = _containerEngine ?? _engineSelection.Current;
            var budget = Min(TimeSpan.FromSeconds(1), Max(release.Deadline - _now, TimeSpan.Zero));
            if (Need(_main, release.Status, $"stop status {release.Id}", token => _fx.ReadStatusWithinAsync(engine, id, budget, token)))
            {
                return false;
            }

            executor = release.Status.Value?.InstanceId;
            if (release.Status.Value is { } read && Once(ref release.Noted))
            {
                RecordStatus(read);
            }
        }

        if (!release.Release.Known && !_main.Busy)
        {
            release.AskedBefore ??= string.Equals(_releaseAsked, launchId, StringComparison.OrdinalIgnoreCase);
            WriteReleaseAsked(launchId);
        }

        var within = Max(release.Deadline - _now, TimeSpan.FromMilliseconds(1));
        var askAs = release.AskAs;
        if (Need(_main, release.Release, $"stop release {release.Id}", token => _fx.ReleaseAsync(launchId, executor, target, within, onlyFor: null, CancellationToken.None, token, askAs)))
        {
            return false;
        }

        // Unanswered in time, the container stops all the same. (Not sent at all, nothing was granted.)
        if (release.Release is { Error: null, Value.Sent: false } && release.AskedBefore != true && Once(ref release.Cleared))
        {
            ClearReleaseMarker();
        }

        if (release.Release.Value is { Released: true } && string.Equals(_containerLaunchId, launchId, StringComparison.OrdinalIgnoreCase))
        {
            WriteReleaseMarker(launchId);
        }

        return true;
    }

    /// <summary>
    /// Waits, briefly, for containers noted gone to be told to Offloadr while the launcher still runs, so
    /// it ends their sessions now. What is not sent in time stays on disk, for the next start.
    /// </summary>
    private sealed class GoneDrain
    {
        public TimeSpan? Until { get; set; }

        public TimeSpan? Since { get; set; }
    }

    private bool StepGoneDrain(GoneDrain drain, TimeSpan budget)
    {
        if (drain.Until is null)
        {
            drain.Until = _now + budget;
            drain.Since = _goneReportedAt;
            _goneReportDue = true;
        }

        if (!HasGoneToReport || _now >= drain.Until)
        {
            return true;
        }

        WorkOffGoneReports();
        if (_goneSlot.Busy || _goneReportedAt == drain.Since)
        {
            WakeAt(drain.Until.Value);
            return false;
        }

        // One attempt went out and something is still unanswered: sent again at the next start.
        return true;
    }

    // A Stop

    private sealed class Stopping(long stops)
    {
        public long Stops { get; } = stops;

        /// <summary>
        /// Only the runners found beside the supervised one are stopped: the person's Stop could not stop
        /// the supervised one (its engine does not answer), and still stands for those found since.
        /// </summary>
        public bool StraysOnly { get; init; }

        public bool Announced;

        public ReleaseBeforeStop Release { get; set; } = new();

        public ContainerStop Stop { get; set; } = new();

        public Exception? Failure { get; set; }

        public StrayStops Strays { get; } = new();

        public GoneDrain Drain { get; } = new();
    }

    /// <summary>
    /// Makes this computer unavailable and stops its runner container. The person asked: the container
    /// stops whether or not Offloadr released it (the app warned about a running session).
    /// </summary>
    private bool StepStop(Stopping stop)
    {
        if (stop.StraysOnly)
        {
            return StepStrayStops(stop.Strays);
        }

        if (Once(ref stop.Announced))
        {
            Set(SupervisorState.Stopping, "Stopping the runner…");
            if (!_ownsContainer)
            {
                // A runner of this app found running and not supervised yet is the one to stop.
                TakeFoundForStop();
            }
        }

        if (stop.Failure is null)
        {
            if (!StepReleaseBeforeStop(stop.Release))
            {
                return false;
            }

            try
            {
                if (!StepContainerStop(stop.Stop))
                {
                    return false;
                }
            }
            catch (ContainerEngineException ex)
            {
                stop.Failure = ex;
            }

            if (stop.Failure is null && TakeFoundForStop())
            {
                // A runner container of this app found meanwhile, on the engine it was started on: the
                // Stop is for it too. Released and stopped there by its ID, not adopted once the Stop is over.
                stop.Release = new ReleaseBeforeStop();
                stop.Stop = new ContainerStop();
                _lookAgain = true;
                return false;
            }
        }

        // (Also when the supervised container's own engine did not answer: each on its own engine.)
        if (!StepStrayStops(stop.Strays))
        {
            return false;
        }

        if (stop.Failure is ContainerEngineException failure)
        {
            if (CancelLink())
            {
                return false;
            }

            Fault(failure.Message, failure.Kind);
            _stopsDone = stop.Stops;
            return true;
        }

        // Stopped without a grant: told while the launcher still runs, briefly, so Offloadr ends that
        // session now. It offers nothing meanwhile.
        if (!StepGoneDrain(stop.Drain, _options.StopReleaseTimeout))
        {
            return false;
        }

        if (CancelLink())
        {
            return false;
        }

        ClearReleaseMarker();
        _retryDueAt = null;

        // Kept pinned while available; after a Stop, the next Start resolves the context again (a GPU
        // check passed there still counts while it is the same one).
        _checked = _checked with { Engine = null };
        Update(view => view with
        {
            State = SupervisorState.Stopped,
            Message = "The runner is stopped.",
            RunnerStatus = null,
            ActiveTemplate = null,
            Image = null,
        });
        _stopsDone = stop.Stops;
        return true;
    }

    // As the app closes

    private sealed class Closing
    {
        public bool Begun;

        /// <summary>The launch that was in flight, kept for what the look after its failed start still tells.</summary>
        public Flight? Flight { get; set; }

        public bool? Keeps { get; set; }

        public TimeSpan Deadline { get; set; }

        public ReleaseBeforeStop Release { get; set; } = new();

        public ContainerStop Stop { get; set; } = new();

        public bool Stopped { get; set; }

        public StrayStops Strays { get; set; } = new();

        public GoneDrain Drain { get; } = new();
    }

    /// <summary>
    /// The app is closing. A launch in flight reports nothing: host shutdown is never a result. The
    /// container is stopped, unless an update is to adopt it (and no Stop was asked for meanwhile: one
    /// asked for as the app goes down, from the tray say, may never have run).
    /// </summary>
    private void StepClosing()
    {
        var closing = _closing ??= new Closing();
        if (Once(ref closing.Begun))
        {
            // For all of it, from here: the host ends this app a moment after it.
            closing.Deadline = _now + ShutdownBudget;
            _flight?.StopSignal.Cancel();
            _flight?.Preparation.Cancel();
            _flight?.Dispose();
            closing.Flight = _flight;

            // The look after a failed start removes what it finds stopped: it is let finish, so that what
            // it removed, and whether that ever started, is known to the cleanup below.
            // (So is the look for what a creation left.)
            var looking = _flight is { } flight && LooksForItsContainer(flight);
            if (_stop is { } stopOfStrays)
            {
                // The stop of a runner beside the supervised one that is under way is carried on too: it
                // was taken off the list of those known, and nothing else would stop it.
                closing.Strays = stopOfStrays.Strays;
            }

            if (_stop is { Failure: null, StraysOnly: false } stopping)
            {
                // A Stop under way is carried on, not begun again: what it asked Offloadr, and what it
                // found as it removed the container (one that never started, say), stands. A removal it
                // has under way is let finish for that.
                closing.Release = stopping.Release;
                closing.Stop = stopping.Stop;
                looking |= stopping.Stop.Id != 0 && _main.Job is { } removing &&
                           string.Equals(removing.Key, $"stop container {stopping.Stop.Id}", StringComparison.Ordinal);
            }

            PutBackAdoption();
            (_startup, _online, _flight, _stop, _idle, _engineLook) = (null, null, null, null, null, null);
            StopLogFollower();
            _owed = null;
            foreach (var slot in new[] { _main, _watchSlot, _planSlot, _auxiliarySlot, _imageSlot, _maintenanceSlot, _deliverySlot, _flightDeliverySlot })
            {
                // (Nor is a removal the watch began cut short: what it removed is recorded first.)
                if ((!looking || !ReferenceEquals(slot, _main)) && (!_removingExited || !ReferenceEquals(slot, _watchSlot)))
                {
                    Cancel(slot);
                }
            }
        }

        if (_main.Busy || _watchSlot.Busy || _maintenanceSlot.Busy)
        {
            // What they were doing is recorded first: a creation that returned names the container to stop,
            // and a look may have removed one, which nothing shows afterwards. Not past the deadline, which
            // ends whatever is under way (what it had done by then is recorded all the same).
            // A container that is stopped, and only still being removed, is left for later a little before
            // that: so that it is gone is told to Offloadr now, as this app may never run again to say so.
            var removing = _main.Job is { } last &&
                           ((closing.Stop.Confirmed && string.Equals(last.Key, $"stop container {closing.Stop.Id}", StringComparison.Ordinal)) ||
                            (closing.Strays.Confirmed && string.Equals(last.Key, $"stray stop {closing.Strays.Id}", StringComparison.Ordinal)));
            var reports = closing.Deadline - Min(_options.StopReleaseTimeout, ShutdownMargin / 3);
            if (_now >= closing.Deadline)
            {
                Cancel(_main);
                Cancel(_watchSlot);
                Cancel(_maintenanceSlot);
            }
            else if (removing && _now >= reports)
            {
                Cancel(_main);
                WakeAt(closing.Deadline);
            }
            else
            {
                WakeAt(removing ? reports : closing.Deadline);
            }

            return;
        }

        if (closing.Flight is { } ended)
        {
            closing.Flight = null;

            // A start the engine refused did not happen, also when the app closes before the look after it.
            NoteStartAnswer(ended);
            if (ended.Left is { Known: true, Error: null, Value: { } found })
            {
                // A container it removed is accounted for as what it was (never started, or exited), not
                // as one the cleanup cannot find.
                LetGoOfLeft(ended, found);
            }

            LetGoOfCreatedFound(ended);
        }

        if (closing.Keeps is null)
        {
            ContainerBusy(false);
            if (_options.StopContainerOnShutdown)
            {
                // One found running and not supervised yet is this app's to stop as well.
                TakeFoundForStop();
            }

            closing.Keeps = !_options.StopContainerOnShutdown || !_ownsContainer || (_in.KeepRunnerOnShutdown && !_in.StopAsked);
        }

        if (closing.Keeps == true && _options.StopContainerOnShutdown && _ownsContainer && _in.StopAsked)
        {
            // Kept for the update to adopt, and a Stop was asked since (from the tray, as the app goes
            // down): it wins while the app still runs, and the container stops after all.
            closing.Keeps = false;
        }

        if (closing.Keeps == false && !closing.Stopped)
        {
            if (_now >= closing.Deadline && !closing.Stop.Stopped.Known)
            {
                _logger.LogWarning("Could not stop the runner container while closing: out of time.");
                closing.Stopped = true;
            }
            else
            {
                WakeAt(closing.Deadline);
                try
                {
                    // While the launcher still runs: Offloadr may release the container before it stops.
                    if (!StepReleaseBeforeStop(closing.Release) || !StepContainerStop(closing.Stop))
                    {
                        return;
                    }

                    ClearReleaseMarker();
                }
                catch (ContainerEngineException ex)
                {
                    _logger.LogWarning("Could not stop the runner container while closing: {Error}", SupervisorEffects.Told(ex));
                }

                closing.Stopped = true;
            }
        }

        if (closing.Keeps == false && closing.Stopped && _now < closing.Deadline && _options.StopContainerOnShutdown && TakeFoundForStop())
        {
            // Another runner of this app is known to run (on an engine the person moved on from, which
            // answers again): stopped too, within the same time. It holds the runner key.
            closing.Release = new ReleaseBeforeStop();
            closing.Stop = new ContainerStop();
            closing.Stopped = false;
            _lookAgain = true;
            return;
        }

        if (_main.Busy)
        {
            return;
        }

        if (_options.StopContainerOnShutdown && !(_in.KeepRunnerOnShutdown && !_in.StopAsked) &&
            !StepStrayStops(closing.Strays, mayBegin: _now < closing.Deadline))
        {
            // Runners of this app known to run beside the supervised one (on an engine the person moved
            // on from, which answers again) are stopped too, each on its own engine, whatever became of
            // the supervised one's stop: they hold the runner key.
            WakeAt(closing.Deadline);
            return;
        }

        // Containers gone that Offloadr was not told of yet (one this shutdown stopped without a grant, or
        // one that exited a moment ago): told before the launcher stops, briefly, within the shutdown
        // budget; sent again at the next start otherwise.
        var drainBudget = closing.Keeps == false
            ? Max(closing.Deadline - _now, TimeSpan.Zero)
            : Min(Max(closing.Deadline - _now, TimeSpan.Zero), _options.StopReleaseTimeout);
        if (!StepGoneDrain(closing.Drain, drainBudget))
        {
            return;
        }

        Cancel(_goneSlot);
        if (CancelLink() || _linkSlot.Busy || _logSlot.Busy)
        {
            return;
        }

        _ended = true;
    }

    // The container's log

    private readonly Slot _logSlot = new("log");
    private (Selected Engine, string Container, long Generation, int Tail)? _logWanted;
    private long _logStarted;
    private bool _logEnded;
    private string? _logEndReason;
    private TimeSpan _logRetryAt;
    private int _logRestarts;

    /// <param name="resume">
    /// Follows again after the stream dropped, from new lines only, so earlier lines are not repeated.
    /// </param>
    private void StartLogFollower(bool resume = false)
    {
        if (!resume)
        {
            _logRestarts = 0;
            _logRetryAt = TimeSpan.Zero;
        }

        _logWanted = (_containerEngine ?? _engineSelection.Current, OurContainer, ++_ids, resume ? 0 : 200);
        _logEnded = false;
        Cancel(_logSlot);
    }

    private void StopLogFollower()
    {
        _logWanted = null;
        Cancel(_logSlot);
    }

    /// <summary>Follows the log that is wanted, once the stream followed before has ended.</summary>
    private void StepLogFollower()
    {
        if (_logWanted is not { } wanted || _logSlot.Busy || _logStarted == wanted.Generation)
        {
            return;
        }

        _logStarted = wanted.Generation;

        // Redacted with the key of the container that writes the lines: one still arriving after that
        // container was let go of, or after Settings name another key, hides it all the same.
        var key = ActiveRunnerKey;
        void Line(string line) => AppendLog(RedactKey(line, key));
        Start(_logSlot, $"log {wanted.Generation}", async token =>
        {
            await _fx.FollowLogsAsync(wanted.Engine, wanted.Container, wanted.Tail, Line, token).ConfigureAwait(false);
            return true;
        }, result =>
        {
            if (_logWanted?.Generation == wanted.Generation && !result.Cancelled)
            {
                _logEnded = true;
                _logEndReason = result.Error?.Message;
            }
        });
    }

    /// <summary>
    /// Follows the log of a still-running container again when following stopped on its own (the
    /// engine restarted, say), with a backoff so a stream that keeps failing does not flood the log.
    /// </summary>
    private void ResumeLogFollowerIfStopped()
    {
        if (_logWanted is null || !_logEnded || _logSlot.Busy || _now < _logRetryAt)
        {
            return;
        }

        var reason = _logEndReason;
        AppendLog($"supervisor: the runner log stopped{(reason is null ? string.Empty : $" ({reason})")}; following it again. Lines written meanwhile are not shown.");
        _logRetryAt = _now + RegistrationPolicy.ComputeBackoff(_logRestarts++, _options.MonitorInterval, LogRetryMaxBackoff);
        StartLogFollower(resume: true);
    }
}
