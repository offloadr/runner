using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hardware;
using Offloadr.Runner.Supervisor.Launcher;
using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor;

/// <summary>
/// Makes this computer's GPU available to Offloadr and runs the runner containers Offloadr asks for.
/// The UI reads <see cref="View"/> and listens to <see cref="Changed"/>.
/// </summary>
/// <remarks>
/// <para>
/// While available, the app registers as the runner's launcher (<see cref="ILauncherLink"/>). It starts
/// a container only for the launch Offloadr names, and the agent in it registers as the executor
/// itself. A container stays warm after its session and is stopped, once Offloadr releases it, when it
/// has been idle for the warm time (<see cref="Settings.RunnerSettings.WarmMinutes"/>), when its settings changed, or when
/// another editor is needed. See "Launchers" in <c>docs/contracts-sync.md</c>.
/// </para>
/// <para>
/// <b>One loop owns every decision.</b> The rules that keep it so:
/// </para>
/// <list type="number">
/// <item>
/// All of this class's state is read and written on the loop (<see cref="ExecuteAsync"/>) alone, with
/// no lock. Other threads hand it immutable values (<see cref="Intent"/>, the settings, the link's
/// state) and wake it; what they read back are immutable values the loop published
/// (<see cref="View"/>, the offer).
/// </item>
/// <item>
/// The loop never waits for Docker or Offloadr. Every such call lives in <see cref="SupervisorEffects"/>,
/// which cannot reach this class's state, and runs as a job in a <see cref="Slot"/>. Its result comes
/// back as a message and is recorded as a <see cref="Fact{T}"/>. This class has one <c>await</c> on
/// the loop: its wait for the next message or timer.
/// </item>
/// <item>
/// A procedure that takes several jobs (going online, a launch, a Stop) is not a suspended method
/// holding locals. It is run again from its top on every tick, against the inputs of that tick, and
/// goes as far as the facts recorded so far allow. What it must keep from an earlier tick is a named
/// field of the procedure, chosen on purpose. So nothing can change between a check and the action
/// it guards, and nothing is decided on a value read before a wait unless the code says so.
/// </item>
/// <item>
/// What is owed outlives the procedure that incurred it: a launch outcome to deliver, a container
/// gone to report, a stopped container to remove, a container whose fate is unknown. Each is data
/// (most of it on disk), worked off by the loop on every tick whatever else goes on.
/// </item>
/// </list>
/// </remarks>
public sealed partial class RunnerSupervisor : BackgroundService
{
    public const string ManagedLabel = "app.offloadr.runner.managed";

    /// <summary>Where the models folder is mounted writable for downloads; its parent is closed to sessions.</summary>
    public const string ModelLibraryContainerPath = "/var/lib/offloadr-library/models";
    public const string TemplateLabel = "app.offloadr.runner.template";
    public const string SpecLabel = "app.offloadr.runner.spec";

    /// <summary>The image as Offloadr named it: a container is created from the exact image that was checked, by its ID.</summary>
    public const string ImageLabel = "app.offloadr.runner.image";

    /// <summary>Unique to each creation, so a failed one can tell its own container from another.</summary>
    public const string LaunchLabel = "app.offloadr.runner.launch";

    /// <summary>The Offloadr launch the container was started for (<c>RUNNER_LAUNCH_ID</c>); releases name it.</summary>
    public const string LaunchIdLabel = "app.offloadr.runner.launch-id";

    /// <summary>The Offloadr address and runner the container was launched for; only that one can release it.</summary>
    public const string LaunchTargetLabel = "app.offloadr.runner.launch-target";

    /// <summary>The <see cref="SupervisorOwner"/> that started the container; only that one adopts or stops it.</summary>
    public const string OwnerLabel = "app.offloadr.runner.owner";

    /// <summary>The variable that tells the agent which launch it was started for.</summary>
    public const string LaunchIdVariable = "RUNNER_LAUNCH_ID";

    private static readonly TimeSpan IdleWait = TimeSpan.FromHours(1);
    private static readonly TimeSpan MonitorRetryMaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogRetryMaxBackoff = TimeSpan.FromMinutes(1);

    private readonly IRunnerSettingsStore _settings;
    private readonly ContainerEngineSelection _engineSelection;
    private readonly SupervisorInstanceLock? _instanceLock;
    private readonly SupervisorOwner _owner;
    private readonly ILauncherLink _link;
    private readonly SupervisorOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RunnerSupervisor> _logger;
    private readonly SupervisorEffects _fx;
    private readonly LogBuffer _logs;
    private readonly long _clockOrigin;

    /// <summary>Work for the loop: each entry runs on it, records what it brings, and is followed by a tick.</summary>
    private readonly Channel<Action> _mailbox = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });

    // What other threads share with the loop: immutable values, swapped whole.
    private SupervisorView _view;
    private Intent _intent = new();
    private string? _activeRunnerKey;

    public RunnerSupervisor(
        IRunnerSettingsStore settings,
        IContainerEngine engine,
        ILaunchPlanProvider plans,
        IHostGpuProbe gpus,
        ILauncherLink link,
        SupervisorOptions options,
        TimeProvider time,
        ILogger<RunnerSupervisor> logger,
        ContainerEngineSelection? engineSelection = null,
        SupervisorInstanceLock? instanceLock = null,
        SupervisorOwner? owner = null,
        SupervisorPaths? paths = null,
        ISecretProtector? protector = null)
    {
        _settings = settings;
        _engineSelection = engineSelection ?? new ContainerEngineSelection();
        _instanceLock = instanceLock;
        _owner = owner ?? SupervisorOwner.Default;
        _link = link;
        _options = options;
        _time = time;
        _logger = logger;
        _releaseMarkerPath = paths?.ReleasedLaunch;
        _takeoverMarkerPath = paths?.TakenOver;
        _launchedPath = paths?.LaunchedContainers;
        _credentialsPath = paths?.LaunchedCredentials;
        _protector = protector;
        _leftoversPath = paths?.LeftoverContainers;
        _logs = new LogBuffer(options.LogCapacity);
        _view = SupervisorView.Initial(time.GetUtcNow());
        _clockOrigin = time.GetTimestamp();
        _fx = new SupervisorEffects(engine, _engineSelection, plans, gpus, link, options, time, logger, _owner.Id, AppendLog, (launchId, ran) => Post(() => NoteSeen(launchId, ran)));
    }

    /// <summary>Monotonic time since the supervisor was created; unlike the wall clock it never jumps.</summary>
    private TimeSpan Now() => _time.GetElapsedTime(_clockOrigin);

    public SupervisorView View => Volatile.Read(ref _view);

    public IReadOnlyList<string> LogLines => _logs.Snapshot();

    /// <summary>Keeps the runner's image pulls and the removal of unused runner images apart.</summary>
    internal RunnerImageGate ImageGate { get; } = new();

    /// <summary>The key the running container was started with, which Settings may no longer hold; for redaction only.</summary>
    internal string? ActiveRunnerKey => Volatile.Read(ref _activeRunnerKey);

    /// <summary>Raised after <see cref="View"/> or <see cref="LogLines"/> changed, on a background thread.</summary>
    public event Action? Changed;

    // What the person asks for

    /// <summary>
    /// What the person (or the app for them) asked for, as counts: the loop compares them with the
    /// counts it has dealt with. Swapped whole by whoever asks, so the loop always reads one moment.
    /// </summary>
    /// <param name="Wanted">The person wants this computer available.</param>
    /// <param name="StopAsked">A Stop was asked for, and no Start since.</param>
    /// <param name="Stops">Stops asked so far. A request stamped with an older count lost to a Stop.</param>
    /// <param name="Starts">Starts the person pressed, the last one after <paramref name="StartAfterStops"/> Stops.</param>
    /// <param name="StartupStops">The Stops asked when the app opened.</param>
    /// <param name="Autos">Starts with the app or after an update: they count only while no Stop was asked since the app opened.</param>
    /// <param name="ResumedSinceGpuCheck">The computer slept since the GPU was last proved to reach containers.</param>
    private sealed record Intent(
        bool Wanted = false,
        bool StopAsked = false,
        long Stops = 0,
        long Starts = 0,
        long StartAfterStops = 0,
        long StartupStops = 0,
        long Autos = 0,
        long Refreshes = 0,
        long Resumes = 0,
        bool ResumedSinceGpuCheck = false,
        bool FastStop = false,
        bool KeepRunnerOnShutdown = false,
        bool UpdateHold = false);

    private Intent Change(Func<Intent, Intent> change)
    {
        while (true)
        {
            var before = Volatile.Read(ref _intent);
            var after = change(before);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _intent, after, before), before))
            {
                Wake();
                return after;
            }
        }
    }

    /// <summary>
    /// True while the person wants this computer available: since a Start (even one not begun yet), a
    /// start with the app or reconnecting to a running runner, and until a Stop is asked for, which
    /// clears it at once, before the stop even runs, or availability needs attention.
    /// </summary>
    public bool RunnerWanted => Volatile.Read(ref _intent).Wanted;

    /// <summary>
    /// Makes this computer available to Offloadr. Pressed by the person, it also takes the runner back
    /// from another app that took it over meanwhile.
    /// </summary>
    public void RequestStart()
        => Change(intent => intent with { Wanted = true, StopAsked = false, Starts = intent.Starts + 1, StartAfterStops = intent.Stops });

    /// <summary>
    /// Asks for the start AutoStart wants when the app opens. It counts only while no Stop was asked
    /// since then: the startup checks before it can take long, and a Stop pressed meanwhile must win.
    /// </summary>
    private void RequestAutoStart()
        => Change(intent => intent with { Wanted = intent.Wanted || intent.Stops == intent.StartupStops, Autos = intent.Autos + 1 });

    /// <summary>
    /// Makes this computer unavailable and stops its runner container, cancelling a launch (for example
    /// a long image download) that is in progress.
    /// </summary>
    public void RequestStop()
    {
        Change(intent => intent with { Wanted = false, StopAsked = true, Stops = intent.Stops + 1 });
        _fence.Check();
    }

    /// <summary>
    /// Signals that reach a job at once, on the thread that causes them, without waiting for the loop:
    /// a Stop, or Settings naming another address or key. The loop hands a job a token made here; whoever
    /// presses Stop or saves Settings cancels it before returning. It carries no state of the supervisor:
    /// each condition reads only values that are swapped whole (<see cref="Intent"/>, the settings).
    /// </summary>
    /// <remarks>
    /// The loop still decides everything: it learns of the same change on its next tick. The signal only
    /// keeps a job from doing, in the moment between, what that tick would have forbidden: registering a
    /// launcher after a Stop, or sending a release for a key Settings no longer name.
    /// </remarks>
    private sealed class Fence
    {
        private readonly object _gate = new();
        private readonly List<Watch> _watching = [];

        public sealed class Watch(Func<bool> raised) : IDisposable
        {
            private readonly CancellationTokenSource _source = new();
            private int _done;

            public CancellationToken Token { get; } = default;

            public Func<bool> Raised { get; } = raised;

            public bool Done => Volatile.Read(ref _done) != 0;

            public CancellationToken Signal => _source.Token;

            public void Raise()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0)
                {
                    _source.Cancel();
                }
            }

            public void Dispose() => Interlocked.Exchange(ref _done, 1);
        }

        /// <summary>A signal raised as soon as <paramref name="raised"/> holds; checked now, and at every change.</summary>
        public Watch Until(Func<bool> raised)
        {
            var watch = new Watch(raised);
            lock (_gate)
            {
                _watching.RemoveAll(static known => known.Done);
                _watching.Add(watch);
            }

            // Added first, checked second: a change at this very moment is seen by one side or the other.
            if (raised())
            {
                watch.Raise();
            }

            return watch;
        }

        /// <summary>Something a condition reads changed: raises every signal whose condition holds now.</summary>
        public void Check()
        {
            Watch[] watching;
            lock (_gate)
            {
                watching = [.. _watching];
            }

            foreach (var watch in watching)
            {
                if (!watch.Done && watch.Raised())
                {
                    watch.Raise();
                }
            }
        }
    }

    private readonly Fence _fence = new();
    private Fence.Watch? _credentialsWatch;
    private (string Origin, string Key)? _credentialsWatched;

    /// <summary>
    /// A signal raised, on the thread that saves them, as soon as Settings name another address or key
    /// than <paramref name="credentials"/>: a release not sent yet is then not sent for the earlier ones.
    /// </summary>
    private CancellationToken CredentialsFence((string Origin, string Key)? credentials)
    {
        if (credentials is not { } watched)
        {
            return new CancellationToken(canceled: true);
        }

        if (_credentialsWatch is not { Done: false } || _credentialsWatched != credentials)
        {
            _credentialsWatch?.Dispose();
            _credentialsWatched = credentials;
            _credentialsWatch = _fence.Until(() => Credentials(_settings.Current) != watched);
        }

        return _credentialsWatch.Signal;
    }

    /// <summary>Re-checks the environment when stopped, or the settings and launch plan when available.</summary>
    public void RequestRefresh() => Change(intent => intent with { Refreshes = intent.Refreshes + 1 });

    /// <summary>Forgets the note that a session was interrupted.</summary>
    public void DismissInterruption() => Update(view => view with { Interruption = null });

    /// <summary>
    /// Windows is shutting down, restarting or signing the person out: the coming stop gives the runner only
    /// <see cref="SupervisorOptions.FastStopTimeout"/>, so the container (which holds the runner key in its
    /// environment) is removed before Windows ends the app.
    /// </summary>
    public void UseFastStop() => Change(intent => intent with { FastStop = true });

    /// <summary>The computer is about to sleep. Noted in the log, so a session cut off then reads as such.</summary>
    public void NotifySuspending() => AppendLog("supervisor: the computer is going to sleep.");

    /// <summary>
    /// The computer woke from sleep. Docker, the runner and its network connections may all have gone
    /// meanwhile, and the GPU may no longer reach containers: all are checked again now, not when the next
    /// check comes round.
    /// </summary>
    public void NotifyResumed()
    {
        Change(intent => intent with { Resumes = intent.Resumes + 1, ResumedSinceGpuCheck = true });

        // What waits on the GPU as it was checked before the sleep (a release not sent yet) is held back
        // at once, on this thread.
        _fence.Check();
    }

    /// <summary>
    /// Takes up no launch any more, if <paramref name="eligible"/> holds now and none is in flight: decided on
    /// the loop, in one step with what it starts, so a launch named at this moment is either seen here or left
    /// for the version that starts next. False when the restart has to wait; launches are taken up as before.
    /// </summary>
    public async Task<bool> TryHoldForUpdateAsync(Func<SupervisorView, bool> eligible)
    {
        var decided = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Change(intent => intent with { UpdateHold = true });
        Post(() =>
        {
            var holds = _flight is null && eligible(View);
            if (!holds)
            {
                ReleaseUpdateHold();
            }

            decided.TrySetResult(holds);
        });

        // (A loop that has ended starts nothing, and decides nothing.)
        if (ExecuteTask is { } loop && await Task.WhenAny(decided.Task, loop).ConfigureAwait(false) == loop && !decided.Task.IsCompleted)
        {
            return eligible(View);
        }

        return await decided.Task.ConfigureAwait(false);
    }

    /// <summary>The restart does not happen after all: launches are taken up again.</summary>
    public void ReleaseUpdateHold()
    {
        Change(intent => intent with { UpdateHold = false });

        // One named meanwhile is looked at now: nothing else would have the loop look.
        Post(() => _launchLook = true);
    }

    /// <summary>
    /// Makes the coming shutdown leave the runner container running, with the engine hint kept, so
    /// the copy of the app that starts next (after an update) adopts it and no session is cut off.
    /// </summary>
    public void KeepRunnerOnShutdown() => Change(intent => intent with { KeepRunnerOnShutdown = true });

    /// <summary>
    /// Makes the computer available again after an update when the previous version had it available.
    /// Like starting with the app, Docker trouble is retried with a backoff.
    /// </summary>
    public void ResumeAfterUpdate() => RequestAutoStart();

    /// <summary>Runs <c>nvidia-smi</c> in a throwaway container to prove Docker can reach the GPU.</summary>
    public async Task<GpuAccessCheck> CheckGpuAccessAsync(CancellationToken cancellationToken = default)
    {
        var image = View.ActiveTemplate?.Image ?? (View.Plan ?? BuiltInLaunchPlanCatalog.Plan()).Choose(_settings.Current)?.Image;
        var result = await _fx.CheckGpuAccessForViewAsync(image, cancellationToken).ConfigureAwait(false);
        Update(view => view with { GpuAccess = result });
        return result;
    }

    /// <summary>
    /// Waits for shutdown to finish its own budget, whatever the host's shutdown timeout (30 seconds by
    /// default, shorter than a runner's stop grace period): leaving early would skip removing the
    /// container, which keeps the runner key in its environment, and forgetting the engine hint.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        using var budget = new CancellationTokenSource(ShutdownBudget + TimeSpan.FromSeconds(15));
        await base.StopAsync(budget.Token).ConfigureAwait(false);
    }

    /// <summary>Time to stop the runner on shutdown: its stop grace period plus the inspect and remove around it.</summary>
    private TimeSpan ShutdownBudget => StopGrace + ShutdownMargin;

    private TimeSpan ShutdownMargin => Volatile.Read(ref _intent).FastStop ? _options.FastShutdownMargin : _options.ShutdownMargin;

    /// <summary>How long the runner gets to stop: shorter once Windows is shutting down.</summary>
    private TimeSpan StopGrace => Volatile.Read(ref _intent).FastStop ? _options.FastStopTimeout : _options.StopTimeout;

    // The loop

    /// <summary>The intent and the time of the tick in progress: one moment for every decision in it.</summary>
    private Intent _in = new();
    private TimeSpan _now;
    private CancellationToken _stopping;
    private TimeSpan? _wakeAt;
    private bool _ended;

    /// <summary>A procedure ended or began in this tick: the loop looks again before it waits.</summary>
    private bool _lookAgain;

    private void Post(Action onLoop) => _mailbox.Writer.TryWrite(onLoop);

    /// <summary>Has the loop look again: something it reads (the intent, the settings, the link) changed.</summary>
    private void Wake() => Post(static () => { });

    /// <summary>Has the loop look again at <paramref name="at"/> (by <see cref="Now"/>) at the latest.</summary>
    private void WakeAt(TimeSpan at) => _wakeAt = _wakeAt is { } known && known <= at ? known : at;

    private void WakeIn(TimeSpan delay) => WakeAt(_now + delay);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        Change(intent => intent with { StartupStops = intent.Stops });
        if (_instanceLock is not null && !_instanceLock.TryAcquire(out var lockProblem))
        {
            // Another copy manages the runner (or the lock cannot even be taken); this one must
            // neither adopt, start nor remove it.
            Fault(lockProblem!, _instanceLock.HeldElsewhere ? FaultKind.AnotherCopy : FaultKind.Unknown);
            return;
        }

        LoadJournals();
        void SettingsChanged(RunnerSettings changed)
        {
            _fence.Check();
            Wake();
        }

        void LinkChanged(object? sender, EventArgs e) => Wake();
        _settings.Changed += SettingsChanged;
        _link.StateChanged += LinkChanged;
        using var closing = stoppingToken.Register(() =>
        {
            // Shutdown raises what waits on it at once, as a Stop does, before the loop's next tick.
            _fence.Check();
            Wake();
        });
        try
        {
            while (true)
            {
                Tick();

                // A tick in which a procedure ended or began is followed by another at once: what it
                // changed may be for a step that had its turn earlier in that tick, and nothing else
                // need come along to wake the loop for it.
                for (var again = 0; again < 4 && _lookAgain && !_ended; again++)
                {
                    Tick();
                }

                if (_ended)
                {
                    break;
                }

                // The loop's one wait: for work handed to it, or the next time it asked to look again.
                using var waiting = new CancellationTokenSource();
                var readable = _mailbox.Reader.WaitToReadAsync(waiting.Token).AsTask();
                var due = Task.Delay(Max((_wakeAt ?? _now + IdleWait) - Now(), TimeSpan.Zero), _time, waiting.Token);
                await Task.WhenAny(readable, due).ConfigureAwait(false);
                await waiting.CancelAsync().ConfigureAwait(false);
                while (_mailbox.Reader.TryRead(out var work))
                {
                    Run(work);
                }
            }
        }
        finally
        {
            _settings.Changed -= SettingsChanged;
            _link.StateChanged -= LinkChanged;
            _instanceLock?.Dispose();
        }
    }

    /// <summary>With its stack trace, except an engine's failure: its message holds the engine's own output.</summary>
    private void LogUnexpected(Exception error, string what)
        => _logger.LogError(error is ContainerEngineException ? null : error, "{What}: {Error}", what, SupervisorEffects.Told(error));

    private void Run(Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            LogUnexpected(ex, "Recording a result failed");
        }
    }

    /// <summary>When the supervised container's launch was first seen as one no executor had matched.</summary>
    private (string LaunchId, TimeSpan Since)? _unmatched;

    /// <summary>
    /// A launch stays named until an executor matched it (and it was told as started), or it was retired: only
    /// a live stream says that it is named no more, so a launcher that went offline meanwhile changes nothing.
    /// A session running in it says as much. Bounded, as a launcher that stays offline never learns.
    /// </summary>
    private void NoteLaunchUnmatched()
    {
        var unmatched = _ownsContainer && _containerLaunchId is { } launchId && _dispatched.Contains(launchId) &&
                        LaunchStillNamed(launchId) && !View.HasActiveSession;
        if (!unmatched)
        {
            _unmatched = null;
        }
        else
        {
            if (!string.Equals(_unmatched?.LaunchId, _containerLaunchId, StringComparison.OrdinalIgnoreCase))
            {
                _unmatched = (_containerLaunchId!, _now);
            }

            var until = _unmatched!.Value.Since + _options.LaunchMatchLimit;
            unmatched = _now < until;
            if (unmatched)
            {
                WakeAt(until);
            }
        }

        if (View.LaunchUnmatched != unmatched)
        {
            Update(view => view with { LaunchUnmatched = unmatched });
        }
    }

    /// <summary>One look at everything: reads the inputs of this moment and takes every step they allow.</summary>
    private void Tick()
    {
        _wakeAt = null;
        _lookAgain = false;
        _now = Now();
        _in = Volatile.Read(ref _intent);
        try
        {
            Reconcile();
            NoteLaunchUnmatched();
            Publish();
        }
        catch (Exception ex)
        {
            // A step that throws would throw again on the next tick: what it was part of is dropped.
            LogUnexpected(ex, "Runner supervision step failed");
            DropProcedures();
            var message = ex is ContainerEngineException ? ex.Message : $"Unexpected error: {ex.Message}";
            if (_ownsContainer)
            {
                // The container is still there and stays supervised.
                AppendLog($"supervisor: {message}");
            }
            else if (!_stopping.IsCancellationRequested)
            {
                Fault(message, KindOf(ex));
            }
        }
    }

    // Jobs

    /// <summary>
    /// A lane for work that waits on Docker or Offloadr: one job at a time. The loop starts it, and may
    /// cancel it; what it brings comes back to the loop as a message.
    /// </summary>
    private sealed class Slot(string name)
    {
        public string Name { get; } = name;

        public Job? Job { get; set; }

        public bool Busy => Job is not null;
    }

    private sealed class Job(string key, CancellationTokenSource cancellation)
    {
        public string Key { get; } = key;

        public CancellationTokenSource Cancellation { get; } = cancellation;
    }

    /// <summary>What a job brought back: a value, the failure it ended with, or nothing because it was cancelled.</summary>
    private readonly record struct Result<T>(T? Value, Exception? Error, bool Cancelled)
    {
        public bool Succeeded => Error is null && !Cancelled;
    }

    /// <summary>
    /// Something a procedure learned from a job, kept with what it was learned for. Unknown until the
    /// job ends; a cancelled job leaves it unknown.
    /// </summary>
    private sealed class Fact<T>
    {
        private string? _for;

        public T? Value { get; private set; }

        public Exception? Error { get; private set; }

        public bool Known => _for is not null;

        public bool KnownFor(string key) => string.Equals(_for, key, StringComparison.Ordinal);

        public void Set(string key, T? value, Exception? error = null)
        {
            _for = key;
            Value = value;
            Error = error;
        }

        public void Forget()
        {
            _for = null;
            Value = default;
            Error = null;
        }
    }

    /// <summary>Starts <paramref name="work"/> in <paramref name="slot"/>, which must be free; <paramref name="record"/> runs on the loop with what it brought.</summary>
    private void Start<T>(Slot slot, string key, Func<CancellationToken, Task<T>> work, Action<Result<T>> record)
    {
        var cancellation = new CancellationTokenSource();
        var job = new Job(key, cancellation);
        slot.Job = job;
        var token = cancellation.Token;
        _ = Task.Run(async () =>
        {
            Result<T> result;
            try
            {
                result = new Result<T>(await work(token).ConfigureAwait(false), null, false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                result = new Result<T>(default, null, true);
            }
            catch (Exception ex)
            {
                result = new Result<T>(default, ex, false);
            }

            Post(() =>
            {
                if (ReferenceEquals(slot.Job, job))
                {
                    slot.Job = null;
                }

                cancellation.Dispose();
                record(result);
            });
        });
    }

    /// <summary>
    /// True while <paramref name="fact"/> is still to be learned for <paramref name="key"/>: its job runs in
    /// <paramref name="slot"/>, or was started just now. A job for something else is cancelled first.
    /// A procedure returns when this says true, and is run again when the job has ended.
    /// </summary>
    private bool Need<T>(Slot slot, Fact<T> fact, string key, Func<CancellationToken, Task<T>> work)
    {
        if (fact.KnownFor(key))
        {
            return false;
        }

        if (slot.Job is { } running)
        {
            if (!string.Equals(running.Key, key, StringComparison.Ordinal))
            {
                Cancel(slot);
            }

            return true;
        }

        Start(slot, key, work, result =>
        {
            if (!result.Cancelled)
            {
                fact.Set(key, result.Value, result.Error);
            }
        });
        return true;
    }

    /// <summary>Cancels the job in <paramref name="slot"/>, if any. True while it has not ended yet.</summary>
    private static bool Cancel(Slot slot)
    {
        if (slot.Job is not { } job)
        {
            return false;
        }

        try
        {
            job.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It ended, and its message is on the way.
        }

        return true;
    }

    // The view

    private void Set(SupervisorState state, string message)
        => Update(view => view with { State = state, Message = message });

    private void Update(Func<SupervisorView, SupervisorView> change)
    {
        while (true)
        {
            var before = Volatile.Read(ref _view);
            var next = change(before) with { UpdatedUtc = _time.GetUtcNow() };

            // What the view shows, and diagnostics copy, carries no signed URL an engine printed.
            next = next with
            {
                Message = RedactUrlQueries(next.Message),
                Fault = Redacted(next.Fault),
                LaunchFailure = Redacted(next.LaunchFailure),
            };

            // A fault explains only the state it belongs to; once the runner moves on it is gone.
            if (next.Fault is not null && next.State is not (SupervisorState.Faulted or SupervisorState.WaitingToRetry))
            {
                next = next with { Fault = null };
            }

            if (next.PullProgress is not null && next.State != SupervisorState.Pulling)
            {
                next = next with { PullProgress = null };
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref _view, next, before), before))
            {
                break;
            }
        }

        RaiseChanged();
    }

    private static SupervisorFault? Redacted(SupervisorFault? fault)
        => fault is null ? null : fault with { Message = RedactUrlQueries(fault.Message), Detail = fault.Detail is null ? null : RedactUrlQueries(fault.Detail) };

    private void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A supervisor change handler failed.");
            }
        }
    }

    private void AppendLog(string line)
    {
        line = RedactUrlQueries(line);
        // The running container keeps the key it was started with after Settings change to
        // another one, so both are hidden.
        line = RedactKey(line, Volatile.Read(ref _activeRunnerKey));
        line = RedactKey(line, _settings.Current.RunnerKey?.Trim());
        _logs.Add(line);
        RaiseChanged();
    }
}
