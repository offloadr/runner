using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hardware;
using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Processes;
using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor;

public sealed class SupervisorOptions
{
    public string ContainerName { get; init; } = "offloadr-runner";

    public string StatusFilePath { get; init; } = "/run/offloadr/status.json";

    public TimeSpan MonitorInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Before a switch stops an idle runner, its status is read again this much later. The agent
    /// publishes a session change within about a second, so a session accepted just before the first
    /// read shows by then.
    /// </summary>
    public TimeSpan StatusConfirmDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often a runner on a tag such as :latest checks whether the tag moved to a newer image.</summary>
    public TimeSpan ImageUpdateInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The agent rewrites its status file at least every 15 seconds. A snapshot that has not changed
    /// for this long, by this machine's clock, says nothing about whether a session is active.
    /// </summary>
    public TimeSpan StatusStaleAfter { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// After a Stop cancels `docker run`, how long the engine may still be creating the container the
    /// command asked for; the cleanup watches for it this long.
    /// </summary>
    public TimeSpan CancelledLaunchSettle { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan RestartMinBackoff { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan RestartMaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>A runner that stayed up this long resets the restart backoff.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromMinutes(2);

    public int LogCapacity { get; init; } = 2000;

    public bool StopContainerOnShutdown { get; init; } = true;
}

/// <summary>
/// Keeps one runner container alive on this machine. All state changes happen on one loop that
/// handles commands from the UI and periodic checks; the UI reads <see cref="View"/> and listens to
/// <see cref="Changed"/>.
/// </summary>
/// <remarks>
/// The supervisor never talks to the control plane as a runner: the container registers itself
/// with the same key. It only asks which image to run (<see cref="ILaunchPlanProvider"/>) and reads
/// the agent's local status file to learn whether a session is active before restarting.
/// </remarks>
public sealed class RunnerSupervisor : BackgroundService
{
    public const string ManagedLabel = "app.offloadr.runner.managed";
    public const string TemplateLabel = "app.offloadr.runner.template";
    public const string SpecLabel = "app.offloadr.runner.spec";

    /// <summary>Unique to each launch, so a failed launch can tell its own container from another.</summary>
    public const string LaunchLabel = "app.offloadr.runner.launch";

    /// <summary>The <see cref="SupervisorOwner"/> that started the container; only that one adopts or stops it.</summary>
    public const string OwnerLabel = "app.offloadr.runner.owner";

    private static readonly TimeSpan IdleWait = TimeSpan.FromHours(1);
    private static readonly TimeSpan MonitorRetryMaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogRetryMaxBackoff = TimeSpan.FromMinutes(1);

    private readonly IRunnerSettingsStore _settings;
    private readonly IContainerEngine _engine;
    private readonly ContainerEngineSelection _engineSelection;
    private readonly SupervisorInstanceLock? _instanceLock;
    private readonly SupervisorOwner _owner;
    private readonly ILaunchPlanProvider _plans;
    private readonly IHostGpuProbe _gpus;
    private readonly SupervisorOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RunnerSupervisor> _logger;
    private readonly Channel<Request> _commands = Channel.CreateUnbounded<Request>(new UnboundedChannelOptions { SingleReader = true });
    private readonly LogBuffer _logs;
    private readonly object _gate = new();
    private readonly long _clockOrigin;

    private SupervisorView _view;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _logFollower;
    private Task _logTask = Task.CompletedTask;
    /// <summary>
    /// When the plan was last asked for, by <see cref="Now"/>; null before the first time. Like every
    /// timer here it runs on the monotonic clock, so a clock change neither delays nor hurries it.
    /// </summary>
    private TimeSpan? _planRefreshedAt;
    /// <summary>Monotonic timestamp of the last status read; null before the first.</summary>
    private long? _statusReadTimestamp;
    private TimeSpan _runningSince;
    private TimeSpan? _restartDueAt;
    private DateTimeOffset? _statusUpdatedUtc;
    /// <summary>Monotonic timestamp of when the agent's status last changed; null until it has.</summary>
    private long? _statusAdvancedTimestamp;
    private string? _activeFingerprint;
    private LaunchPlanSource? _activePlanSource;

    /// <summary>What the runner this supervisor last started or adopted runs, with its exact image.</summary>
    private LaunchTemplate? _runningTemplate;
    private TimeSpan _logRetryAt;
    private int _logRestarts;

    /// <summary>The image a pending switch has already downloaded.</summary>
    private string? _preparedImage;

    /// <summary>The ID of the image the switch downloaded, which the start must find unchanged.</summary>
    private string? _preparedImageId;

    /// <summary>The background download a switch or an image update check is waiting for.</summary>
    private ImageDownload? _imageDownload;

    /// <summary>The other engine a pending switch prepares, with its pinned context.</summary>
    private ContainerEngineSelection.Selected? _switchTarget;
    private CancellationToken _stopping;

    /// <summary>The image ID the running container runs, a newer one its tag now names, and when the tag was last checked.</summary>
    private string? _runningImageId;
    private string? _newerImageId;
    private TimeSpan _imageCheckedAt;
    private ImageFailure? _imageFailure;

    /// <summary>
    /// The Offloadr address and runner key the control plane refused together; a switch to them
    /// would take the runner offline. The same key at another address is a different credential.
    /// </summary>
    private (string Origin, string? Key)? _rejectedCredentials;

    /// <summary>The Offloadr address and key the plan Offloadr last answered with was asked with.</summary>
    private (string Origin, string? Key)? _planCredentials;

    /// <summary>The settings the running container was started with (known for an adopted one only when they match).</summary>
    private RunnerSettings? _lastLaunchSettings;

    /// <summary>What an adopted runner was started with (key, address) is still to be read; retried.</summary>
    private bool _adoptedLaunchPending;
    private string? _activeRunnerKey;
    private long _stopGeneration;
    private long _startupGeneration;
    private int _restartAttempt;
    private int _monitorFailures;
    private string? _messageBeforeMonitorFailure;
    private bool _ownsContainer;

    /// <summary>The ID of the container this supervisor launched or adopted.</summary>
    private string? _containerId;

    /// <summary>
    /// The launch label of that container. Until `docker run` returns the ID (or when a cancelled run
    /// never did), it is what tells this launch's container from one another launch put under the name.
    /// </summary>
    private string? _launchId;

    /// <summary>The ID of a container that took the runner's name; it is not adopted on a restart.</summary>
    private string? _rejectedContainerId;

    public RunnerSupervisor(
        IRunnerSettingsStore settings,
        IContainerEngine engine,
        ILaunchPlanProvider plans,
        IHostGpuProbe gpus,
        SupervisorOptions options,
        TimeProvider time,
        ILogger<RunnerSupervisor> logger,
        ContainerEngineSelection? engineSelection = null,
        SupervisorInstanceLock? instanceLock = null,
        SupervisorOwner? owner = null)
    {
        _settings = settings;
        _engine = engine;
        _engineSelection = engineSelection ?? new ContainerEngineSelection();
        _instanceLock = instanceLock;
        _owner = owner ?? SupervisorOwner.Default;
        _plans = plans;
        _gpus = gpus;
        _options = options;
        _time = time;
        _logger = logger;
        _logs = new LogBuffer(options.LogCapacity);
        _view = SupervisorView.Initial(time.GetUtcNow());
        _clockOrigin = time.GetTimestamp();
    }

    /// <summary>Monotonic time since the supervisor was created; unlike the wall clock it never jumps.</summary>
    private TimeSpan Now() => _time.GetElapsedTime(_clockOrigin);

    private enum Command
    {
        Start,

        /// <summary>The start that AutoStart asks for when the app opens; Docker trouble is retried.</summary>
        AutoStart,
        Stop,
        Refresh,
    }

    private readonly record struct Request(Command Kind, long StopGeneration);

    public SupervisorView View => Volatile.Read(ref _view);

    public IReadOnlyList<string> LogLines => _logs.Snapshot();

    /// <summary>Raised after <see cref="View"/> or <see cref="LogLines"/> changed, on a background thread.</summary>
    public event Action? Changed;

    public void RequestStart() => Enqueue(Command.Start);

    /// <summary>
    /// Queues the start AutoStart asks for, stamped with the Stop generation from when the app opened:
    /// the startup checks before it can take long, and a Stop pressed meanwhile must win.
    /// </summary>
    private void RequestAutoStart()
    {
        lock (_gate)
        {
            _commands.Writer.TryWrite(new Request(Command.AutoStart, _startupGeneration));
        }
    }

    /// <summary>Stops the runner, cancelling a start (for example a long image download) that is in progress.</summary>
    public void RequestStop()
    {
        lock (_gate)
        {
            // Recorded, not only signalled: a start that has not begun yet (queued behind a slow
            // check, or the start half of a switch) sees the newer generation and does not run.
            // Queued under the same lock as the count, so no request queued after it can carry the
            // older count, and none queued before it can pass a check against the newer one.
            var generation = ++_stopGeneration;
            _operation?.Cancel();
            _imageDownload?.Cancellation.Cancel();
            _imageDownload = null;
            _commands.Writer.TryWrite(new Request(Command.Stop, generation));
        }
    }

    /// <summary>Re-checks the environment when stopped, or the launch plan when running.</summary>
    public void RequestRefresh() => Enqueue(Command.Refresh);

    /// <summary>Queues a request with the Stop generation, both under the lock that Stop takes.</summary>
    private void Enqueue(Command kind)
    {
        lock (_gate)
        {
            _commands.Writer.TryWrite(new Request(kind, _stopGeneration));
        }
    }

    /// <summary>Runs <c>nvidia-smi</c> in a throwaway container to prove Docker can reach the GPU.</summary>
    public async Task<GpuAccessCheck> CheckGpuAccessAsync(CancellationToken cancellationToken = default)
    {
        var image = View.ActiveTemplate?.Image ?? (View.Plan ?? BuiltInLaunchPlanCatalog.Plan()).Choose(_settings.Current)?.Image;
        GpuAccessCheck result;
        try
        {
            // Docker missing or not answering is a failed check to show, not an error for the caller.
            result = image is null || await _engine.InspectImageAsync(image, cancellationToken).ConfigureAwait(false) is null
                ? new GpuAccessCheck(false, "Start the runner once so its image is downloaded, then check again.")
                : await _engine.CheckGpuAccessAsync(image, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ContainerEngineException or ProcessStartException)
        {
            result = new GpuAccessCheck(false, ex.Message);
        }

        Update(view => view with { GpuAccess = result });
        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _startupGeneration = CurrentStopGeneration();
        if (_instanceLock is not null && !_instanceLock.TryAcquire(out var lockProblem))
        {
            // Another copy manages the runner (or the lock cannot even be taken); this one must
            // neither adopt, restart nor remove it.
            Fault(lockProblem!);
            return;
        }

        _settings.Changed += OnSettingsChanged;
        try
        {
            try
            {
                await InitializeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // Docker may still be starting (at sign-in, say). The loop must run anyway, so
                // Start, Stop and Refresh keep working; a start probes Docker again.
                _logger.LogError(ex, "Runner supervisor startup check failed.");
                Fault(ex is ContainerEngineException ? ex.Message : $"Unexpected error: {ex.Message}");
                if (_settings.Current.AutoStart)
                {
                    RequestAutoStart();
                }
            }

            while (true)
            {
                var command = await NextCommandAsync(stoppingToken).ConfigureAwait(false);
                try
                {
                    await HandleAsync(command, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Runner supervision step failed.");
                    Fault(ex is ContainerEngineException ? ex.Message : $"Unexpected error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The app is closing.
        }
        finally
        {
            _settings.Changed -= OnSettingsChanged;
            await ShutdownAsync().ConfigureAwait(false);
            _instanceLock?.Dispose();
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        switch (await CheckRememberedEngineAsync(cancellationToken).ConfigureAwait(false))
        {
            case RememberedRunner.Adopted:
                return;
            case RememberedRunner.Unreachable:
                // Nothing is started on another engine while the runner may still run on this one;
                // an automatic start keeps checking it, and Start overrides the hint.
                Fault($"{UnreachableEngineMessage()} Start it, or press Start to run the runner on the engine in Settings.");
                if (_settings.Current.AutoStart)
                {
                    RequestAutoStart();
                }

                return;
        }

        var engine = await ProbeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        if (engine.Available)
        {
            // The runner found here is adopted on the context it was looked for in.
            await PinEngineContextAsync(cancellationToken).ConfigureAwait(false);
            var existing = await _engine.InspectContainerAsync(_options.ContainerName, cancellationToken).ConfigureAwait(false);
            if (existing is not null && IsOwned(existing))
            {
                if (existing.Running)
                {
                    await AdoptAsync(existing, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await _engine.RemoveAsync(Target(existing), cancellationToken).ConfigureAwait(false);
            }
        }

        if (_settings.Current.AutoStart)
        {
            RequestAutoStart();
        }
    }

    /// <summary>
    /// After a crash, looks for the runner on the engine it was started on, which Settings may no
    /// longer name. Found, it is adopted there and a switch to the new engine waits, as usual, until
    /// no session is active; otherwise startup continues with the engine Settings name.
    /// </summary>
    private async Task<RememberedRunner> CheckRememberedEngineAsync(CancellationToken cancellationToken)
    {
        if (!_engineSelection.TryRestore())
        {
            return RememberedRunner.None;
        }

        ContainerInfo? container;
        try
        {
            var engine = await _engine.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (!engine.Available)
            {
                // Not answering is no proof the runner is gone (a remote engine, or one restarting).
                return RememberedRunner.Unreachable;
            }

            // By the saved ID first: the runner may have been renamed before the crash.
            container = (_engineSelection.RememberedContainerId is { } rememberedId
                            ? await _engine.InspectContainerAsync(rememberedId, cancellationToken).ConfigureAwait(false)
                            : null)
                        ?? await _engine.InspectContainerAsync(_options.ContainerName, cancellationToken).ConfigureAwait(false);
            if (container is { Running: true } && IsOwned(container))
            {
                var gpus = await _gpus.ProbeAsync(cancellationToken).ConfigureAwait(false);
                Update(view => view with { Engine = engine, HostGpus = gpus });
                await AdoptAsync(container, cancellationToken).ConfigureAwait(false);
                return RememberedRunner.Adopted;
            }

            if (container is not null && IsOwned(container))
            {
                // A stopped runner there still holds the runner key in its environment; it goes
                // before the hint does, since nothing would look on that engine again.
                await _engine.RemoveAsync(Target(container), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ContainerEngineException ex)
        {
            _logger.LogWarning(ex, "Could not look for the runner on the engine it was started on.");
            return RememberedRunner.Unreachable;
        }

        // The engine answered and runs no runner of this app: the hint has done its job.
        _engineSelection.Forget();
        return RememberedRunner.None;
    }

    private enum RememberedRunner
    {
        /// <summary>No hint, or the remembered engine definitely runs no runner.</summary>
        None,

        /// <summary>The runner was found there and adopted.</summary>
        Adopted,

        /// <summary>The remembered engine does not answer, so the runner may still run there.</summary>
        Unreachable,
    }

    private string UnreachableEngineMessage()
        => $"The runner may still be running on {_engineSelection.Executable ?? "the engine it was started on"}, which does not answer.";

    /// <summary>
    /// Pins the engine's commands to the Docker context (or Podman connection, or endpoint the
    /// environment chooses) they reach now, unless already pinned (a restart, or a hint restored
    /// after a crash), so `docker context use` elsewhere cannot move them.
    /// </summary>
    private async Task PinEngineContextAsync(CancellationToken cancellationToken)
    {
        if (_engineSelection.Context is null)
        {
            _engineSelection.PinContext(await _engine.CurrentContextAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// Reads what an adopted runner was started with from its container's environment: the key (so
    /// its log can be followed redacted) and the Offloadr address that key belongs to (so a restart
    /// can fall back to the pair). Throws on an engine error, leaving the rest pending for the next
    /// check; a missing value ends the attempt, and a key is never paired with another address.
    /// </summary>
    private async Task CompleteAdoptedLaunchAsync(CancellationToken cancellationToken)
    {
        if (_activeRunnerKey is null)
        {
            var key = await _engine.ReadEnvironmentVariableAsync(OurContainer, "RUNNER_SECRET", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(key))
            {
                _adoptedLaunchPending = false;
                return;
            }

            _activeRunnerKey = key.Trim();
            if (_logFollower is null)
            {
                StartLogFollower();
            }
        }

        var origin = await _engine.ReadEnvironmentVariableAsync(OurContainer, "OFFLOADR_API_GRPC", cancellationToken).ConfigureAwait(false);
        _adoptedLaunchPending = false;
        _lastLaunchSettings = string.IsNullOrWhiteSpace(origin)
            ? null
            : _settings.Current with { RunnerKey = _activeRunnerKey, OffloadrApiOrigin = origin.Trim() };
    }

    private async Task AdoptAsync(ContainerInfo container, CancellationToken cancellationToken)
    {
        await PinEngineContextAsync(cancellationToken).ConfigureAwait(false);
        if (!_engineSelection.Remember(NormalizeContainerId(container.Id), out var rememberError))
        {
            // The runner already runs; it is supervised all the same, only less safely across a crash.
            AppendLog($"supervisor: {rememberError}");
        }

        var plan = await GetPlanOrNullAsync(cancellationToken).ConfigureAwait(false);
        container.Labels.TryGetValue(TemplateLabel, out var templateId);
        container.Labels.TryGetValue(SpecLabel, out var fingerprint);
        var template = plan?.Find(templateId) ?? BuiltInLaunchPlanCatalog.Plan().Find(templateId);
        var now = Now();
        var settings = _settings.Current;

        // The key is part of the fingerprint, so the container was started with the current key
        // exactly when its own image and template under the current settings give its fingerprint.
        var startedWithCurrentSettings = settings.HasRunnerKey &&
            string.Equals(
                Fingerprint(BuildSpec(settings, new LaunchTemplate(templateId ?? string.Empty, string.Empty, container.Image, string.Empty))),
                fingerprint,
                StringComparison.Ordinal);
        _ownsContainer = true;
        _activeFingerprint = fingerprint;
        _containerId = NormalizeContainerId(container.Id);
        _launchId = container.Labels.TryGetValue(LaunchLabel, out var launchId) ? launchId : null;
        _activePlanSource = null;
        _runningImageId = container.ImageId;
        _newerImageId = null;
        _imageCheckedAt = now;
        _runningTemplate = new LaunchTemplate(
            templateId ?? string.Empty,
            template?.RuntimeKind ?? string.Empty,
            container.Image,
            template?.Label ?? templateId ?? container.Image);
        // Started with another key (one Offloadr later refused, say): the container's own environment
        // holds it, so its log can still be redacted and a restart after an exit can fall back to it.
        _activeRunnerKey = startedWithCurrentSettings ? settings.RunnerKey!.Trim() : null;
        _lastLaunchSettings = startedWithCurrentSettings ? settings : null;
        _adoptedLaunchPending = !startedWithCurrentSettings;
        _runningSince = now;
        ResetStatusTracking();
        if (plan is not null)
        {
            _planRefreshedAt = now;
        }

        // A container left by an earlier run may predate a template or settings change. It is not
        // stopped on sight, because it may be serving a session; it switches the way a plan change
        // does, once its status shows no session.
        var wanted = plan is { Source: not LaunchPlanSource.Unreachable } && settings.Validate().Count == 0
            ? plan.Choose(settings)
            : null;
        var outdated = !startedWithCurrentSettings ||
                       !_engineSelection.Matches(settings.ContainerEngineExecutable) ||
                       (wanted is not null && !string.Equals(Fingerprint(BuildSpec(settings, wanted)), fingerprint, StringComparison.Ordinal));
        var reconnected = $"Reconnected to the runner that was already running{(template is null ? "." : $" ({template.Label}).")}";

        if (startedWithCurrentSettings)
        {
            StartLogFollower();
        }
        else
        {
            try
            {
                await CompleteAdoptedLaunchAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ContainerEngineException ex)
            {
                // The engine did not answer; monitoring reads it again until it does.
                _logger.LogWarning(ex, "Could not read what the runner that was already running was started with.");
            }

            if (_activeRunnerKey is null)
            {
                // It may have been started with an earlier key, which its log could print and which
                // the log cannot be redacted against until that key is known.
                AppendLog(_adoptedLaunchPending
                    ? "supervisor: the runner that was already running uses earlier settings; its log is shown once they can be read."
                    : "supervisor: the runner that was already running uses earlier settings; its log is shown after it restarts.");
            }
        }

        Update(view => view with
        {
            State = outdated ? SupervisorState.RestartPending : SupervisorState.Running,
            Message = outdated
                ? $"{reconnected} It will restart with {(wanted is null ? "the current settings" : wanted.Label)} when no session is active."
                : reconnected,
            Plan = plan ?? view.Plan,
            ActiveTemplate = template,
        });
    }

    private async Task<Request?> NextCommandAsync(CancellationToken cancellationToken)
    {
        if (_commands.Reader.TryRead(out var command))
        {
            return command;
        }

        var view = View;
        var wait = view.State switch
        {
            SupervisorState.WaitingToRestart when _restartDueAt is { } due => Max(due - Now(), TimeSpan.Zero),
            SupervisorState.Running or SupervisorState.RestartPending when _monitorFailures > 0
                => RegistrationPolicy.ComputeBackoff(_monitorFailures - 1, _options.MonitorInterval, MonitorRetryMaxBackoff),
            SupervisorState.Running or SupervisorState.RestartPending => _options.MonitorInterval,
            _ => IdleWait,
        };

        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readable = _commands.Reader.WaitToReadAsync(waitCancellation.Token).AsTask();
        var delay = Task.Delay(wait, _time, waitCancellation.Token);
        await Task.WhenAny(readable, delay).ConfigureAwait(false);
        await waitCancellation.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return _commands.Reader.TryRead(out command) ? command : null;
    }

    private async Task HandleAsync(Request? request, CancellationToken stoppingToken)
    {
        switch (request?.Kind)
        {
            case Command.Start or Command.AutoStart:
                // A Start pressed before a later Stop does not run. An auto-start retries Docker
                // trouble with the restart backoff (Docker is often still starting at sign-in); a
                // Start pressed while such a retry waits runs now, as a start the person asked for.
                var waitingToRetry = request.Value.Kind == Command.Start && View.State == SupervisorState.WaitingToRestart;
                if ((!View.IsActive || waitingToRetry) && request.Value.StopGeneration == CurrentStopGeneration())
                {
                    await StartRunnerAsync(
                        resetAttempts: true,
                        automatic: request.Value.Kind == Command.AutoStart,
                        request.Value.StopGeneration,
                        stoppingToken).ConfigureAwait(false);
                }

                break;
            case Command.Stop:
                await StopRunnerAsync(stoppingToken).ConfigureAwait(false);
                break;
            case Command.Refresh:
                if (View.State is SupervisorState.Running or SupervisorState.RestartPending)
                {
                    await WatchAsync(() => RefreshPlanAsync(request.Value.StopGeneration, stoppingToken)).ConfigureAwait(false);
                }
                else if (!View.IsActive)
                {
                    await ProbeEnvironmentAsync(stoppingToken).ConfigureAwait(false);
                }

                break;
            case null:
                if (View.State == SupervisorState.WaitingToRestart && _restartDueAt <= Now())
                {
                    await StartRunnerAsync(resetAttempts: false, automatic: true, CurrentStopGeneration(), stoppingToken).ConfigureAwait(false);
                }
                else if (View.State is SupervisorState.Running or SupervisorState.RestartPending)
                {
                    // Read before the check starts: a Stop pressed while it runs must win over a
                    // switch the check decides on.
                    var stopGeneration = CurrentStopGeneration();
                    await WatchAsync(() => MonitorAsync(stopGeneration, stoppingToken)).ConfigureAwait(false);
                }

                break;
        }
    }

    /// <summary>
    /// Runs one check of a running runner. Docker failing to answer (while Docker Desktop restarts,
    /// say) says nothing about the container, so the runner stays supervised and the check is
    /// retried with a backoff rather than faulting and losing track of it.
    /// </summary>
    private async Task WatchAsync(Func<Task> check)
    {
        try
        {
            await check().ConfigureAwait(false);
        }
        catch (ContainerEngineException ex)
        {
            _logger.LogWarning(ex, "Docker did not answer while checking the runner.");
            if (_monitorFailures++ == 0)
            {
                _messageBeforeMonitorFailure = View.Message;
                AppendLog($"supervisor: Docker did not answer ({ex.Message}); still watching the runner.");
                Update(view => view with { Message = "Docker is not answering. The runner is still watched and checked again shortly." });
            }

            return;
        }

        if (_monitorFailures > 0)
        {
            _monitorFailures = 0;
            AppendLog("supervisor: Docker is answering again.");
            var message = _messageBeforeMonitorFailure;
            if (message is not null && View.State is SupervisorState.Running or SupervisorState.RestartPending)
            {
                Update(view => view with { Message = message });
            }
        }
    }

    private async Task<ContainerEngineInfo> ProbeEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (!_ownsContainer)
        {
            _engineSelection.Select(_settings.Current.ContainerEngineExecutable);
        }

        var engine = await _engine.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var gpus = await _gpus.ProbeAsync(cancellationToken).ConfigureAwait(false);
        Update(view => view with { Engine = engine, HostGpus = gpus });
        return engine;
    }

    /// <param name="automatic">
    /// A restart after an exit or a switch, not a person pressing Start. Docker trouble then schedules
    /// another attempt with the restart backoff instead of faulting, so the runner comes back on
    /// its own once Docker does (after a Docker Desktop restart, say).
    /// </param>
    /// <param name="stopGeneration">The Stop generation this start was decided under; a later Stop cancels it.</param>
    /// <param name="settingsSnapshot">
    /// The settings a switch validated before it stopped the old runner; a later edit is applied by
    /// its own refresh, not by this start. Null reads the current settings.
    /// </param>
    private async Task StartRunnerAsync(
        bool resetAttempts,
        bool automatic,
        long stopGeneration,
        CancellationToken stoppingToken,
        RunnerSettings? settingsSnapshot = null,
        PreparedLaunch? prepared = null)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_gate)
        {
            _operation = operation;
            if (_stopGeneration != stopGeneration)
            {
                // Stop was pressed after this start was decided but before it could be cancelled.
                operation.Cancel();
            }
        }

        var cancellationToken = operation.Token;
        RunnerSettings? usedSettings = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A start downloads what it needs itself; a background download for a switch or an
            // update check is no longer wanted.
            CancelImageDownload();
            if (resetAttempts)
            {
                _restartAttempt = 0;
                Update(view => view with { Restarts = 0 });
            }

            _restartDueAt = null;
            var requested = settingsSnapshot ?? _settings.Current;
            var settings = requested;
            if (automatic && settingsSnapshot is null && IsRejected(settings) &&
                _lastLaunchSettings is { } lastLaunch && !IsRejected(lastLaunch))
            {
                // Offloadr refused the key now in Settings; a restart after an unrelated exit keeps the
                // key the runner last ran with rather than staying down.
                // The whole launch it had, so the key goes back to the Offloadr address that accepted it.
                AppendLog("supervisor: Offloadr did not accept the runner key in Settings; restarting with the settings the runner had.");
                settings = lastLaunch;
            }

            usedSettings = settings;
            Set(SupervisorState.Checking, "Checking Docker and your settings…");
            var problems = settings.Validate();
            if (problems.Count > 0)
            {
                Fault(problems[0]);
                return;
            }

            if (!_ownsContainer)
            {
                switch (await CheckRememberedEngineAsync(cancellationToken).ConfigureAwait(false))
                {
                    case RememberedRunner.Adopted:
                        return;
                    case RememberedRunner.Unreachable when automatic:
                        FailStart($"{UnreachableEngineMessage()} Press Start to run the runner on the engine in Settings instead.", automatic);
                        return;
                    case RememberedRunner.Unreachable:
                        // The person pressed Start: run on the engine Settings name.
                        AppendLog($"supervisor: {UnreachableEngineMessage()} Starting on the engine in Settings, as asked.");
                        _engineSelection.Forget();
                        break;
                }

                if (prepared is not null)
                {
                    // A switch downloaded and checked the image on exactly this engine and context;
                    // the new runner starts there too, whatever discovery or `docker context use`
                    // would pick now.
                    _engineSelection.SelectExactly(prepared.Target);
                }
                else
                {
                    // Resolved again only while no container runs, so discovery cannot move a running
                    // one to another engine.
                    _engineSelection.Select(settings.ContainerEngineExecutable);
                }
            }
            else if (!_engineSelection.Matches(settings.ContainerEngineExecutable))
            {
                // A container this supervisor still runs is stopped through the engine that
                // started it, before commands move to the engine Settings now name.
                try
                {
                    await StopContainerAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ContainerEngineException ex) when (!automatic)
                {
                    // That engine does not answer (quit or uninstalled). The person pressed Start,
                    // so the runner moves to the engine in Settings, as after a restart of the app.
                    AppendLog($"supervisor: {ex.Message} The runner may still be running there; starting on the engine in Settings, as asked.");
                    await StopLogFollowerAsync().ConfigureAwait(false);
                    AbandonContainer();
                }

                _engineSelection.Select(settings.ContainerEngineExecutable);
            }

            var engine = await _engine.ProbeAsync(cancellationToken).ConfigureAwait(false);
            Update(view => view with { Engine = engine });
            if (!engine.Available)
            {
                FailStart(engine.Error ?? "Docker is not available.", automatic);
                return;
            }

            // Pinned as soon as the probe identified the engine (Podman installed as `docker`, say), so
            // the inspections, the download and the launch all reach the same daemon, whatever
            // `docker context use` changes meanwhile.
            await PinEngineContextAsync(cancellationToken).ConfigureAwait(false);

            // The runner that exited may no longer have the name (renamed meanwhile); found by its ID,
            // it goes before another starts, since it still holds the runner key.
            if (_ownsContainer && _containerId is not null &&
                await _engine.InspectContainerAsync(_containerId, cancellationToken).ConfigureAwait(false) is { Running: false } retained &&
                IsOwned(retained))
            {
                await _engine.RemoveAsync(Target(retained), cancellationToken).ConfigureAwait(false);
            }

            var left = await _engine.InspectContainerAsync(_options.ContainerName, cancellationToken).ConfigureAwait(false);
            if (left is not null && IsRejectedContainer(left))
            {
                FaultNameTaken();
                return;
            }

            if (left is { Running: true } && IsOwned(left))
            {
                // A runner this app started is still running and was not adopted at startup (Docker
                // did not answer then, say). It may be serving a session, so it is adopted now
                // rather than replaced; a needed switch then waits until no session is active.
                await AdoptAsync(left, cancellationToken).ConfigureAwait(false);
                return;
            }

            LaunchPlan? plan = null;
            LaunchPlanSource? planSource;
            LaunchTemplate? template;
            if (prepared is not null)
            {
                // A switch already chose, downloaded and checked this image while the old runner
                // served; asking again or pulling again would only lengthen the gap.
                template = prepared.Template;
                planSource = prepared.Source;
            }
            else
            {
                Set(SupervisorState.ResolvingPlan, "Finding the runner image for your editor…");
                plan = await _plans.GetAsync(settings, cancellationToken).ConfigureAwait(false);
                _planRefreshedAt = Now();
                planSource = plan.Source;
                if (plan.Source == LaunchPlanSource.ControlPlane)
                {
                    NoteControlPlaneAnswer(settings);
                }

                if (plan.Source is LaunchPlanSource.Unreachable or LaunchPlanSource.BuiltIn &&
                    View.Plan is { Source: LaunchPlanSource.ControlPlane } known &&
                    IsPlanFrom(settings))
                {
                    // A restart during an outage (or a passing "not offered" answer) keeps the image
                    // and template Offloadr chose last, rather than the stand-in catalog's, which the
                    // next answer would switch back.
                    AppendLog("supervisor: Offloadr cannot be reached; starting with the image it chose last.");
                    plan = known;
                    planSource = known.Source;
                    template = plan.Choose(settings);
                }
                else if (plan.Source == LaunchPlanSource.Unreachable && automatic && _runningTemplate is { } running)
                {
                    // Without an answer from Offloadr in this run of the app (an adopted runner), an
                    // automatic restart keeps the image that was running; only settings changes apply.
                    AppendLog("supervisor: Offloadr cannot be reached; restarting with the image that was running.");
                    planSource = null;
                    template = running;
                }
                else
                {
                    template = plan.Choose(settings);
                }
            }

            Update(view => view with { Plan = plan ?? view.Plan, ActiveTemplate = template });
            if (template is null)
            {
                Fault("No runner image is available for your editor.");
                return;
            }

            ContainerEngineException? pullFailure = null;
            if (prepared is null)
            {
                Set(SupervisorState.Pulling, $"Downloading {template.Image}. The first download is large and can take a while.");
                try
                {
                    await _engine.PullAsync(template.Image, line => AppendLog("pull: " + line), cancellationToken).ConfigureAwait(false);
                }
                catch (ContainerEngineException ex)
                {
                    pullFailure = ex;
                }
            }

            var image = await _engine.InspectImageAsync(template.Image, cancellationToken).ConfigureAwait(false);
            if (prepared is not null &&
                (image is null || (_preparedImageId is not null && !string.Equals(image.Id, _preparedImageId, StringComparison.Ordinal))))
            {
                // Only the exact image the switch downloaded is started; anything else is fetched
                // again by the normal start the backoff retries with.
                _preparedImage = null;
                _preparedImageId = null;
                FailStart(image is null
                    ? $"{template.Image} is no longer on this machine."
                    : $"{template.Image} changed since it was downloaded.", automatic);
                return;
            }

            if (pullFailure is not null)
            {
                if (image is null)
                {
                    FailStart(pullFailure.Message, automatic);
                    return;
                }

                // Offline or registry trouble: run the copy this machine already has.
                AppendLog($"warning: {pullFailure.Message} Using the copy already on this machine.");
            }

            Update(view => view with { Image = image });

            Set(SupervisorState.Starting, "Starting the runner…");
            var existing = await _engine.InspectContainerAsync(_options.ContainerName, cancellationToken).ConfigureAwait(false);
            if (existing is not null && (!IsOwned(existing) || IsRejectedContainer(existing)))
            {
                FaultNameTaken(existing);
                return;
            }

            if (existing is { Running: true })
            {
                // Started meanwhile, during the download (by another copy of this app, say): the same
                // rule applies, it is adopted rather than removed.
                await AdoptAsync(existing, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (existing is not null)
            {
                await _engine.RemoveAsync(Target(existing), cancellationToken).ConfigureAwait(false);
            }

            // Settings saved during the download would otherwise only reach a refresh, which leaves a
            // running container alone when they are invalid; so the launch checks them itself.
            var current = _settings.Current;
            var templateChanged = planSource is not null && plan is not null &&
                !string.Equals(plan.Choose(current)?.EditorTemplate, template.EditorTemplate, StringComparison.OrdinalIgnoreCase);
            if (!Equals(current, requested) && (templateChanged || !SameContainer(requested, current, template)))
            {
                var changedProblems = current.Validate();
                if (changedProblems.Count > 0)
                {
                    Fault(changedProblems[0]);
                    return;
                }

                AppendLog("supervisor: Settings changed while the runner was starting; starting again with them.");
                Set(SupervisorState.Stopped, "Starting again with the changed settings…");
                Enqueue(automatic ? Command.AutoStart : Command.Start);
                return;
            }

            if (ModelsFolderProblem(settings, _engineSelection.Pin) is { } modelsProblem)
            {
                Fault(modelsProblem);
                return;
            }

            var spec = BuildSpec(settings, template);

            // Marks the container as this launch's, so a failed launch removes only what it created.
            var launchId = Guid.NewGuid().ToString("N");
            spec = spec with { Labels = new Dictionary<string, string>(spec.Labels, StringComparer.Ordinal) { [LaunchLabel] = launchId } };

            // Saved before the launch: after a crash it is the only pointer to the engine this runner
            // runs on, and without it a later start could run a second runner elsewhere.
            if (!_engineSelection.Remember(out var rememberError))
            {
                FailStart(rememberError!, automatic);
                return;
            }

            // Owned before the launch returns: a cancelled `docker run` may already have created the
            // container, and shutdown cleans up only what this supervisor owns.
            _ownsContainer = true;
            _activeRunnerKey = settings.RunnerKey!.Trim();

            // The previous container (after an exit) is gone; until `docker run` returns the new ID,
            // a container with this launch's label counts as this runner, so a cancelled launch is
            // still stopped and removed, and one another launch put under the name is not.
            _containerId = null;
            _launchId = launchId;
            try
            {
                _containerId = NormalizeContainerId(await _engine.RunDetachedAsync(spec, cancellationToken).ConfigureAwait(false));
            }
            catch (ContainerEngineException)
            {
                await CleanUpFailedLaunchAsync(launchId, cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch (ProcessStartException)
            {
                // The engine executable did not even start, so no container was created.
                AbandonContainer();
                throw;
            }

            // `docker run --pull never` starts whatever the tag names at that moment: a pull or tag in
            // between starts another image than the one checked above.
            var startedImageId = await StartedImageIdAsync(cancellationToken).ConfigureAwait(false);
            if (startedImageId is not null && image is not null && !string.Equals(startedImageId, image.Id, StringComparison.Ordinal))
            {
                if (prepared is not null)
                {
                    // A switch starts only the exact image it downloaded; the normal start the backoff
                    // retries with checks the tag again.
                    AppendLog($"supervisor: {template.Image} changed while the runner was starting; starting again.");
                    await StopContainerAsync(cancellationToken).ConfigureAwait(false);
                    FailStart($"{template.Image} changed while the runner was starting.", automatic);
                    return;
                }

                AppendLog($"supervisor: {template.Image} changed while the runner was starting; it runs the image the tag names now.");
            }

            // Saved with the engine, so that after a crash the runner is found by ID even if renamed.
            if (!_engineSelection.Remember(_containerId, out var idError))
            {
                AppendLog($"supervisor: {idError}");
            }

            _activeFingerprint = spec.Labels[SpecLabel];
            _activePlanSource = planSource;
            _runningTemplate = template;
            _lastLaunchSettings = settings;
            _switchTarget = null;
            _preparedImage = null;
            _preparedImageId = null;
            _runningImageId = startedImageId ?? image?.Id;
            _newerImageId = null;
            _imageCheckedAt = Now();
            _runningSince = Now();
            ResetStatusTracking();
            StartLogFollower();
            Update(view => view with
            {
                State = SupervisorState.Running,
                Message = $"Running {template.Label}. It shows up as your personal runner in Offloadr.",
                RunnerStatus = null,
            });
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Cancelled by RequestStop; the queued Stop command cleans up.
            Set(SupervisorState.Stopping, "Stopping the runner…");
        }
        catch (Exception ex) when (ex is ContainerEngineException or ProcessStartException)
        {
            FailStart(ex.Message, automatic);
        }
        catch (LaunchPlanRejectedException ex)
        {
            _rejectedCredentials = usedSettings is null ? null : Credentials(usedSettings);
            if (automatic && _lastLaunchSettings is { } lastLaunch && !IsRejected(lastLaunch))
            {
                // The next attempt restarts with the key the runner last ran with.
                FailStart(ex.Message, automatic);
            }
            else
            {
                Fault(ex.Message);
            }
        }
        finally
        {
            lock (_gate)
            {
                _operation = null;
            }
        }
    }

    private async Task MonitorAsync(long stopGeneration, CancellationToken cancellationToken)
    {
        // Watched by ID once known: a renamed runner is still the runner (a second one is not started
        // under the freed name), and a container that takes the name is never taken for it.
        var container = await _engine.InspectContainerAsync(OurContainer, cancellationToken).ConfigureAwait(false);
        var now = Now();
        if (container is not null && !IsOurContainer(container))
        {
            // Another container took the name between checks: ours is gone, and that one is not
            // followed, read or stopped as if it were. The restart below sorts it out.
            AppendLog("supervisor: a different container now has the runner's name.");
            _rejectedContainerId = NormalizeContainerId(container.Id);
            container = null;
        }
        else if (container is null && _containerId is not null &&
                 await _engine.InspectContainerAsync(_options.ContainerName, cancellationToken).ConfigureAwait(false) is { } named &&
                 !IsOurContainer(named))
        {
            // Ours is gone and another container has the name: it is not adopted on the restart.
            AppendLog("supervisor: a different container now has the runner's name.");
            _rejectedContainerId = NormalizeContainerId(named.Id);
        }

        if (_adoptedLaunchPending && container is not null)
        {
            // What the adopted runner was started with could not all be read yet; a failure here
            // counts as a passing engine outage, and the read is tried again on the next check. A
            // runner that has just exited still holds it, and the restart below may need it.
            await CompleteAdoptedLaunchAsync(cancellationToken).ConfigureAwait(false);
        }

        if (container is null || !container.Running)
        {
            await StopLogFollowerAsync().ConfigureAwait(false);
            if (now - _runningSince >= _options.StableAfter)
            {
                _restartAttempt = 0;
            }

            var delay = RegistrationPolicy.ComputeBackoff(_restartAttempt++, _options.RestartMinBackoff, _options.RestartMaxBackoff);
            _restartDueAt = now + delay;
            var exit = container?.ExitCode is { } code ? $" with exit code {code}" : string.Empty;
            AppendLog($"supervisor: the runner stopped{exit}; restarting in {delay.TotalSeconds:0} s.");
            Update(view => view with
            {
                State = SupervisorState.WaitingToRestart,
                Message = $"The runner stopped{exit}. Restarting in {delay.TotalSeconds:0} seconds.",
                RunnerStatus = null,
                Restarts = view.Restarts + 1,
            });
            return;
        }

        ResumeLogFollowerIfStopped(now);
        if (_statusReadTimestamp is not { } lastRead || _time.GetElapsedTime(lastRead) >= _options.StatusInterval)
        {
            await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        }

        var refreshInterval = View.Plan?.RefreshInterval ?? BuiltInLaunchPlanCatalog.RefreshInterval;
        // An image check is due, or a background download finished and its result is waiting.
        var imageCheckDue = (_runningImageId is not null && now - _imageCheckedAt >= _options.ImageUpdateInterval) ||
                            _imageDownload is { Task.IsCompleted: true } ||
                            (_runningImageId is not null && _imageFailure is { } imageFailure && now >= imageFailure.RetryAt);
        var planDue = _planRefreshedAt is not { } refreshed || now - refreshed >= refreshInterval;
        if (View.State == SupervisorState.RestartPending || planDue || imageCheckDue)
        {
            await RefreshPlanAsync(stopGeneration, cancellationToken, fetch: planDue).ConfigureAwait(false);
        }
    }

    /// <summary>Compares the wanted container with the running one and restarts when it is safe to.</summary>
    /// <param name="stopGeneration">
    /// The Stop generation when the request or check that led here was made. A Stop pressed since
    /// then cancels the switch; the queued Stop stops the runner instead.
    /// </param>
    private async Task RefreshPlanAsync(long stopGeneration, CancellationToken cancellationToken, bool fetch = true)
    {
        var settings = _settings.Current;
        var plan = View.Plan;
        if (fetch)
        {
            var fetched = await GetPlanOrNullAsync(cancellationToken).ConfigureAwait(false);
            _planRefreshedAt = Now();
            // Only a plan from the Offloadr address and key in Settings is kept: after Settings point
            // elsewhere, the old server's selection no longer applies.
            if (plan is not null &&
                (plan.Source != LaunchPlanSource.ControlPlane || IsPlanFrom(settings)) &&
                (fetched is { Source: LaunchPlanSource.Unreachable } ||
                 (fetched is { Source: LaunchPlanSource.BuiltIn } && plan.Source == LaunchPlanSource.ControlPlane)))
            {
                // An outage keeps the plan the runner was started with: the stand-in catalog would
                // pick another image (and template), and the next answer would switch back. Once
                // Offloadr has answered with a plan, a "not offered" answer (a 404 from a proxy or
                // a deploy in progress) is treated the same way.
                _logger.LogInformation("Keeping the current launch plan: {Notice}", fetched.Notice);
                fetched = null;
            }

            plan = fetched ?? plan;
            Update(view => view with { Plan = plan });
        }

        // The stand-in catalog only decides for a runner that was itself started from it. Any other
        // runner keeps its image and template during an outage; settings changes still apply.
        var keepRunningTemplate = plan is { Source: LaunchPlanSource.Unreachable } && _activePlanSource != LaunchPlanSource.Unreachable;
        var template = keepRunningTemplate ? _runningTemplate : plan?.Choose(settings);
        var planSource = keepRunningTemplate ? null : plan?.Source;
        if (template is null || settings.Validate().Count > 0)
        {
            return;
        }

        // A different engine in Settings also needs a new container, started there, and so does a
        // tag such as :latest that now names a newer image than the one running.
        var sameEngine = _engineSelection.Matches(settings.ContainerEngineExecutable);
        if (sameEngine)
        {
            // Settings point back at the running engine: a target an abandoned switch prepared (with
            // the context it had then) must not be reused by a later switch.
            _switchTarget = null;
        }

        if (string.Equals(Fingerprint(BuildSpec(settings, template)), _activeFingerprint, StringComparison.Ordinal) &&
            sameEngine &&
            !HasNewerImage(template))
        {
            if (_activeRunnerKey is null && _logFollower is null && settings.HasRunnerKey)
            {
                // An adopted runner whose key was unknown matches these settings, key included, so
                // its log can be shown (and redacted) now.
                _activeRunnerKey = settings.RunnerKey!.Trim();
                StartLogFollower();
            }

            if (View.State == SupervisorState.RestartPending)
            {
                Set(SupervisorState.Running, $"Running {template.Label}.");
            }

            return;
        }

        if (IsRejected(settings))
        {
            // A key Offloadr refuses would only replace a working runner with one that cannot register.
            Set(SupervisorState.RestartPending, "Offloadr did not accept the runner key in Settings. The runner keeps running with the key it started with; check the key.");
            return;
        }

        // A Stop pressed since this refresh was asked for wins; the queued Stop stops the runner.
        if (CurrentStopGeneration() != stopGeneration)
        {
            return;
        }

        // The new image is downloaded in the background while the current runner keeps serving and
        // stays supervised: a long download is no outage, and an image that cannot be downloaded
        // leaves the working runner in place. A switch to another engine downloads on that engine.
        var target = sameEngine ? _engineSelection.Current : await SwitchTargetAsync(settings, cancellationToken).ConfigureAwait(false);
        if (ModelsFolderProblem(settings, target.Context) is { } modelsProblem)
        {
            // Checked before anything is downloaded or stopped: the new runner could not start.
            Set(SupervisorState.RestartPending, $"{modelsProblem} The runner keeps running as it is.");
            return;
        }

        if (!PrepareImage(template, target))
        {
            return;
        }

        // Read now, not from the last periodic read: a session assigned since then must not be cut off.
        var status = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status is null)
        {
            Set(SupervisorState.RestartPending, $"Restart the runner to switch to {template.Label}; it cannot report whether a session is active.");
            return;
        }

        if (!IsStatusCurrent())
        {
            // A stalled snapshot proves nothing either way, so the switch waits for a current one.
            Set(SupervisorState.RestartPending, $"Will switch to {template.Label} once the runner reports that no session is active.");
            return;
        }

        if (!string.IsNullOrEmpty(status.ActiveSessionId))
        {
            Set(SupervisorState.RestartPending, $"Will switch to {template.Label} when the current session ends.");
            return;
        }

        // A download may have finished long ago (a session kept the runner busy): the target engine
        // must still answer and still have the image. Checked only once the runner looks idle, since
        // it runs several engine commands and a session can keep a switch waiting for hours.
        if (!await TargetReadyAsync(template, target, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // The agent publishes a session change within about a second; idle again after that (and
        // after the target check, which can take a while), a session accepted just before the first
        // read would have shown. Only the moment of the stop itself remains, which only the control
        // plane could close by holding back assignments.
        if (_options.StatusConfirmDelay > TimeSpan.Zero)
        {
            await Task.Delay(_options.StatusConfirmDelay, _time, cancellationToken).ConfigureAwait(false);
        }

        var confirmed = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        if (confirmed is null || !string.IsNullOrEmpty(confirmed.ActiveSessionId))
        {
            Set(SupervisorState.RestartPending, $"Will switch to {template.Label} when the current session ends.");
            return;
        }

        if (!IsStatusCurrent())
        {
            // The wait may have carried the snapshot past its freshness limit.
            Set(SupervisorState.RestartPending, $"Will switch to {template.Label} once the runner reports that no session is active.");
            return;
        }

        // A Stop pressed, or Settings edited, while the checks above ran still wins; the old runner is
        // only stopped for the settings that were checked.
        if (CurrentStopGeneration() != stopGeneration || !Equals(_settings.Current, settings))
        {
            return;
        }

        AppendLog($"supervisor: switching to {template.Label}.");
        await StopContainerAsync(cancellationToken).ConfigureAwait(false);
        await StartRunnerAsync(resetAttempts: true, automatic: true, stopGeneration, cancellationToken, settings, new PreparedLaunch(template, planSource, target)).ConfigureAwait(false);
    }

    /// <summary>
    /// The engine a switch moves to, pinned to the Docker context (or Podman connection) it has when
    /// the switch starts preparing it. The download, the check before the stop and the start all use
    /// that context, whatever `docker context use` changes while the old runner stops.
    /// </summary>
    private async Task<ContainerEngineSelection.Selected> SwitchTargetAsync(RunnerSettings settings, CancellationToken cancellationToken)
    {
        var resolved = _engineSelection.Resolve(settings.ContainerEngineExecutable);
        if (_switchTarget is { } known && known.Configured == resolved.Configured && known.Executable == resolved.Executable)
        {
            return known;
        }

        ContainerEnginePin? context;
        using (_engineSelection.Use(resolved))
        {
            // Probed first: only the probe recognizes Podman installed as `docker`, whose connection
            // is found another way than a Docker context.
            await _engine.ProbeAsync(cancellationToken).ConfigureAwait(false);
            context = await _engine.CurrentContextAsync(cancellationToken).ConfigureAwait(false);
        }

        var target = resolved with { Context = context };

        // Kept only once a context is known; an engine without one is asked again next time.
        _switchTarget = context is null ? null : target;
        return target;
    }

    /// <summary>
    /// For a mutable reference (a tag, not a digest), downloads it in the background now and then, and
    /// reports whether it names a newer image than the running one. Digest-pinned images never change.
    /// </summary>
    private bool HasNewerImage(LaunchTemplate template)
    {
        if (template.Image.Contains('@', StringComparison.Ordinal) || _runningImageId is null)
        {
            return false;
        }

        if (_newerImageId is not null)
        {
            return true;
        }

        var target = _engineSelection.Current;
        var key = DownloadKey(target, template.Image);
        if (_imageDownload is { } download && download.Key == key)
        {
            if (!download.Task.IsCompleted)
            {
                return false;
            }

            var local = CompleteDownload(download, template, quiet: true);
            if (local is null || string.Equals(local.Id, _runningImageId, StringComparison.Ordinal))
            {
                return false;
            }

            _newerImageId = local.Id;
            AppendLog($"supervisor: a newer {template.Image} is available; switching to it when no session is active.");
            return true;
        }

        var now = Now();
        // Checked on the update interval, and again as soon as a failed check's backoff runs out.
        var retryDue = _imageFailure is { } failure && failure.Image == key && now >= failure.RetryAt;
        if (now - _imageCheckedAt >= _options.ImageUpdateInterval || retryDue)
        {
            _imageCheckedAt = now;
            StartDownload(template.Image, target, key);
        }

        return false;
    }

    /// <summary>
    /// True once the image a switch needs is on the target engine. Until then a background download
    /// runs (or waits out the backoff after a failure) and the reason is shown; the loop keeps
    /// supervising the current runner meanwhile.
    /// </summary>
    private bool PrepareImage(LaunchTemplate template, ContainerEngineSelection.Selected target)
    {
        var key = DownloadKey(target, template.Image);
        if (string.Equals(_preparedImage, key, StringComparison.Ordinal))
        {
            return true;
        }

        if (_imageDownload is { } download && download.Key == key)
        {
            return download.Task.IsCompleted && CompleteDownload(download, template, quiet: false) is not null;
        }

        if (string.Equals(_imageFailure?.Image, key, StringComparison.Ordinal) && Now() < _imageFailure!.RetryAt)
        {
            return false;
        }

        StartDownload(template.Image, target, key);
        Set(SupervisorState.RestartPending, $"Downloading {template.Image} for {template.Label}. The runner keeps running meanwhile.");
        return false;
    }

    /// <summary>Identifies a download by engine, Docker context (or Podman connection) and image reference.</summary>
    private static string DownloadKey(ContainerEngineSelection.Selected target, string image) => $"{target.Executable}|{target.Context}|{image}";

    /// <summary>Starts downloading an image on an engine as background work that Stop and shutdown cancel.</summary>
    private void StartDownload(string image, ContainerEngineSelection.Selected target, string key)
    {
        CancelImageDownload();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        var task = Task.Run(() => DownloadAsync(image, target, cancellation.Token));
        lock (_gate)
        {
            _imageDownload = new ImageDownload(key, task, cancellation);
        }
    }

    private async Task<ImageInfo> DownloadAsync(string image, ContainerEngineSelection.Selected target, CancellationToken cancellationToken)
    {
        using var engineScope = _engineSelection.Use(target);

        // Probed first: a switch to another engine must find that engine working.
        var engine = await _engine.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!engine.Available)
        {
            throw new ContainerEngineException(engine.Error ?? "The container engine is not available.");
        }

        ContainerEngineException? pullFailure = null;
        try
        {
            await _engine.PullAsync(image, line => AppendLog("pull: " + line), cancellationToken).ConfigureAwait(false);
        }
        catch (ContainerEngineException ex)
        {
            // A copy already on that engine is enough.
            pullFailure = ex;
        }

        return await _engine.InspectImageAsync(image, cancellationToken).ConfigureAwait(false)
            ?? throw pullFailure ?? new ContainerEngineException($"{image} is not on this machine.");
    }

    /// <summary>Takes the result of a finished download; a failure starts the backoff and is shown.</summary>
    private ImageInfo? CompleteDownload(ImageDownload download, LaunchTemplate template, bool quiet)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_imageDownload, download))
            {
                _imageDownload = null;
            }
        }

        download.Cancellation.Dispose();
        if (download.Task.IsCompletedSuccessfully)
        {
            _imageFailure = null;
            _preparedImage = download.Key;
            _preparedImageId = download.Task.Result.Id;
            return download.Task.Result;
        }

        var reason = download.Task.Exception?.InnerException is ContainerEngineException failure
            ? failure.Message
            : $"Downloading {template.Image} stopped.";
        var attempt = _imageFailure is { } previous && previous.Image == download.Key ? previous.Attempt + 1 : 0;
        var delay = RegistrationPolicy.ComputeBackoff(attempt, _options.RestartMinBackoff, _options.RestartMaxBackoff);
        _imageFailure = new ImageFailure(download.Key, attempt, Now() + delay);
        AppendLog($"supervisor: {reason} The runner keeps running; trying again in {delay.TotalSeconds:0} s.");
        if (!quiet)
        {
            Set(SupervisorState.RestartPending, $"{reason} The runner keeps running and tries again in {delay.TotalSeconds:0} seconds.");
        }

        return null;
    }

    private void CancelImageDownload()
    {
        ImageDownload? download;
        lock (_gate)
        {
            download = _imageDownload;
            _imageDownload = null;
        }

        download?.Cancellation.Cancel();
    }

    /// <summary>Right before a switch stops the runner: the target engine answers and has the image.</summary>
    private async Task<bool> TargetReadyAsync(LaunchTemplate template, ContainerEngineSelection.Selected target, CancellationToken cancellationToken)
    {
        string reason;
        using (_engineSelection.Use(target))
        {
            try
            {
                var engine = await _engine.ProbeAsync(cancellationToken).ConfigureAwait(false);
                var local = engine.Available ? await _engine.InspectImageAsync(template.Image, cancellationToken).ConfigureAwait(false) : null;

                // The exact image the switch downloaded: `docker run --pull never` uses whatever copy
                // the tag names there now, so a different (older) copy must not pass.
                if (local is not null && (_preparedImageId is null || string.Equals(local.Id, _preparedImageId, StringComparison.Ordinal)))
                {
                    return true;
                }

                reason = engine.Error ?? (local is null
                    ? $"{template.Image} is no longer on this machine."
                    : $"{template.Image} changed since it was downloaded; it is downloaded again.");
            }
            catch (ContainerEngineException ex)
            {
                reason = ex.Message;
            }
        }

        _preparedImage = null;
        _preparedImageId = null;
        AppendLog($"supervisor: {reason} The runner keeps running; the switch is tried again.");
        Set(SupervisorState.RestartPending, $"{reason} The runner keeps running; the switch is tried again.");
        return false;
    }

    /// <summary>
    /// The template a switch prepared, the source of the plan that chose it (null: kept during an
    /// outage) and the exact engine and context it was prepared on.
    /// </summary>
    private sealed record PreparedLaunch(LaunchTemplate Template, LaunchPlanSource? Source, ContainerEngineSelection.Selected Target);

    private sealed record ImageDownload(string Key, Task<ImageInfo> Task, CancellationTokenSource Cancellation);

    private sealed record ImageFailure(string Image, int Attempt, TimeSpan RetryAt);

    private async Task StopRunnerAsync(CancellationToken cancellationToken)
    {
        // Also covers a download a refresh started just after the Stop was counted.
        CancelImageDownload();
        Set(SupervisorState.Stopping, "Stopping the runner…");
        try
        {
            await StopContainerAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ContainerEngineException ex)
        {
            Fault(ex.Message);
            return;
        }

        _restartDueAt = null;
        Update(view => view with
        {
            State = SupervisorState.Stopped,
            Message = "The runner is stopped.",
            RunnerStatus = null,
        });
    }

    private async Task StopContainerAsync(CancellationToken cancellationToken)
    {
        var wasFollowing = _logFollower is not null;
        await StopLogFollowerAsync().ConfigureAwait(false);
        try
        {
            // A container this supervisor owns is always looked up: the last probe may be old, and
            // an engine that does not answer fails here instead of passing for a stopped runner.
            // By ID when it is known, so a renamed runner is still the one stopped.
            var existing = !_ownsContainer && View.Engine?.Available == false
                ? null
                : await _engine.InspectContainerAsync(_ownsContainer ? OurContainer : _options.ContainerName, cancellationToken).ConfigureAwait(false);
            if (existing is null && _ownsContainer && _containerId is null && _launchId is not null)
            {
                // A cancelled `docker run`: the engine may still be creating the container it asked for.
                var poll = TimeSpan.FromMilliseconds(Math.Min(250, _options.CancelledLaunchSettle.TotalMilliseconds / 4));
                var settled = Now() + _options.CancelledLaunchSettle;
                while (existing is null && Now() < settled)
                {
                    await Task.Delay(poll, _time, cancellationToken).ConfigureAwait(false);
                    existing = await _engine.InspectContainerAsync(_options.ContainerName, cancellationToken).ConfigureAwait(false);
                }
            }

            if (existing is not null && IsOwned(existing) && IsOurContainer(existing))
            {
                await _engine.StopAsync(Target(existing), _options.StopTimeout, cancellationToken).ConfigureAwait(false);
                await _engine.RemoveAsync(Target(existing), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ContainerEngineException) when (wasFollowing)
        {
            // The runner may still be running (and stays supervised), so its log keeps being shown.
            StartLogFollower(resume: true);
            throw;
        }

        if (_ownsContainer)
        {
            AbandonContainer();
        }
        else
        {
            // Nothing was owned, so a remembered engine (a runner that may still run there, not yet
            // checked) is kept: the next start looks there first.
            _activeFingerprint = null;
            _activePlanSource = null;
            _activeRunnerKey = null;
        }

        _engineSelection.Select(_settings.Current.ContainerEngineExecutable);
    }

    /// <summary>
    /// Stops treating the container as this supervisor's: after it was removed, or when the person
    /// chose to move on from an engine that no longer answers.
    /// </summary>
    private void AbandonContainer()
    {
        _containerId = null;
        _launchId = null;
        _adoptedLaunchPending = false;
        _ownsContainer = false;
        _activeFingerprint = null;
        _activePlanSource = null;
        _activeRunnerKey = null;
        _engineSelection.Forget();
    }

    private async Task<RunnerStatusSnapshot?> ReadStatusAsync(CancellationToken cancellationToken)
    {
        _statusReadTimestamp = _time.GetTimestamp();
        var status = ParseStatus(await _engine.ReadFileAsync(OurContainer, _options.StatusFilePath, cancellationToken).ConfigureAwait(false));
        // Freshness is judged by when this machine saw the agent's timestamp change, so clock
        // skew between the host and the container does not matter.
        if (status is not null && _statusUpdatedUtc is { } previous && status.UpdatedUtc != previous)
        {
            _statusAdvancedTimestamp = _time.GetTimestamp();
        }

        _statusUpdatedUtc = status?.UpdatedUtc;
        Update(view => view with { RunnerStatus = status });
        return status;
    }

    private void ResetStatusTracking()
    {
        _monitorFailures = 0;
        _statusReadTimestamp = null;
        _statusUpdatedUtc = null;
        _statusAdvancedTimestamp = null;
    }

    /// <summary>True once the agent's status has changed recently enough to describe the runner now.</summary>
    private bool IsStatusCurrent()
        => _statusAdvancedTimestamp is { } advanced &&
           _time.GetElapsedTime(advanced) <= _options.StatusStaleAfter;

    /// <summary>Time to stop the runner on shutdown: its stop grace period plus the inspect and remove around it.</summary>
    private TimeSpan ShutdownBudget => _options.StopTimeout + TimeSpan.FromSeconds(60);

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

    private async Task ShutdownAsync()
    {
        CancelImageDownload();
        await StopLogFollowerAsync().ConfigureAwait(false);
        if (!_options.StopContainerOnShutdown || !_ownsContainer)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(ShutdownBudget);
        try
        {
            await StopContainerAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ContainerEngineException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not stop the runner container while closing.");
        }
    }

    private async Task<LaunchPlan?> GetPlanOrNullAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        try
        {
            var plan = await _plans.GetAsync(settings, cancellationToken).ConfigureAwait(false);
            if (plan.Source == LaunchPlanSource.ControlPlane)
            {
                NoteControlPlaneAnswer(settings);
            }

            return plan;
        }
        catch (LaunchPlanRejectedException ex)
        {
            _logger.LogWarning("The control plane rejected the runner key in Settings: {Message}", ex.Message);
            _rejectedCredentials = Credentials(settings);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not refresh the launch plan.");
            return null;
        }
    }

    /// <summary>
    /// A failed `docker run` may still have created the container (left "Created" when the GPU cannot
    /// be attached, say). It is removed, and this supervisor then owns no container and remembers no
    /// engine for it. If removing fails too, ownership stays, so shutdown and the next start retry.
    /// A container of that name without this app's label (created by someone else in the meantime,
    /// which makes `docker run` fail on the name) is never touched.
    /// </summary>
    private async Task CleanUpFailedLaunchAsync(string launchId, CancellationToken cancellationToken)
    {
        try
        {
            // Only the container this launch created carries its launch label: one another copy of the
            // app started meanwhile (the reason `docker run` failed on the name) keeps running.
            if (await _engine.InspectContainerAsync(_options.ContainerName, cancellationToken).ConfigureAwait(false) is { } left &&
                IsOwned(left) &&
                left.Labels.TryGetValue(LaunchLabel, out var leftLaunch) &&
                string.Equals(leftLaunch, launchId, StringComparison.Ordinal))
            {
                await _engine.RemoveAsync(Target(left), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ContainerEngineException ex)
        {
            _logger.LogWarning(ex, "Could not remove the runner container after a failed launch.");
            return;
        }

        AbandonContainer();
    }

    /// <summary>
    /// Why the models folder in Settings cannot be mounted on this engine, or null. An engine on another
    /// computer resolves a bind mount on that computer, where the folder does not exist.
    /// </summary>
    private static string? ModelsFolderProblem(RunnerSettings settings, ContainerEnginePin? engine)
        => engine is { Remote: true } && !string.IsNullOrWhiteSpace(settings.LocalModelsDirectory)
            ? "The models folder in Settings is on this computer, but the container engine runs on another one, which cannot see it. Clear the models folder in Settings, or use a container engine on this computer."
            : null;

    /// <summary>True when <paramref name="changed"/> is valid and asks for the same container on the same engine.</summary>
    private bool SameContainer(RunnerSettings started, RunnerSettings changed, LaunchTemplate template)
        => changed.Validate().Count == 0 &&
           _engineSelection.Matches(changed.ContainerEngineExecutable) &&
           string.Equals(Fingerprint(BuildSpec(started, template)), Fingerprint(BuildSpec(changed, template)), StringComparison.Ordinal);

    internal ContainerSpec BuildSpec(RunnerSettings settings, LaunchTemplate template)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RUNNER_SECRET"] = settings.RunnerKey!.Trim(),
            ["OFFLOADR_API_GRPC"] = settings.OffloadrApiOrigin,
        };
        var volumes = new List<VolumeMount>();
        if (!string.IsNullOrWhiteSpace(settings.LocalModelsDirectory))
        {
            volumes.Add(new VolumeMount(settings.LocalModelsDirectory, "/local", ReadOnly: true));
            environment["RUNNER_LOCAL_MODELS_DIR"] = "/local";
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ManagedLabel] = "true",
            [OwnerLabel] = _owner.Id,
            [TemplateLabel] = template.EditorTemplate,
        };
        var spec = new ContainerSpec(_options.ContainerName, template.Image, environment, volumes, labels, Gpus: true);
        labels[SpecLabel] = Fingerprint(spec);
        return spec;
    }

    /// <summary>Identifies everything that requires a new container when it changes. Labels are not part of it.</summary>
    internal static string Fingerprint(ContainerSpec spec)
    {
        var text = new StringBuilder()
            .Append(spec.Image).Append('\n')
            .Append(spec.Gpus).Append('\n');
        foreach (var (name, value) in spec.Environment.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            text.Append(name).Append('=').Append(value).Append('\n');
        }

        foreach (var volume in spec.Volumes)
        {
            text.Append(volume.HostPath).Append(':').Append(volume.ContainerPath).Append(':').Append(volume.ReadOnly).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    internal static RunnerStatusSnapshot? ParseStatus(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RunnerStatusSnapshot>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string Origin, string? Key) Credentials(RunnerSettings settings)
        => (settings.OffloadrApiOrigin.Trim(), settings.RunnerKey?.Trim());

    /// <summary>True when the control plane refused this Offloadr address and key together.</summary>
    private bool IsRejected(RunnerSettings settings) => _rejectedCredentials == Credentials(settings);

    /// <summary>True when the plan Offloadr last answered with was asked for with these credentials.</summary>
    private bool IsPlanFrom(RunnerSettings settings) => _planCredentials == Credentials(settings);

    /// <summary>The control plane answered a plan for these credentials, so they are accepted.</summary>
    private void NoteControlPlaneAnswer(RunnerSettings settings)
    {
        _planCredentials = Credentials(settings);
        if (IsRejected(settings))
        {
            _rejectedCredentials = null;
        }
    }

    private long CurrentStopGeneration()
    {
        lock (_gate)
        {
            return _stopGeneration;
        }
    }

    /// <summary>
    /// True unless the container has another ID than the one this supervisor launched or adopted (a
    /// container that took the name meanwhile), or, while that ID is unknown, another launch label.
    /// Unknown IDs never count against it.
    /// </summary>
    private bool IsOurContainer(ContainerInfo container)
    {
        if (_containerId is not { } ours)
        {
            return _launchId is not { } launch ||
                   (container.Labels.TryGetValue(LaunchLabel, out var label) && string.Equals(label, launch, StringComparison.Ordinal));
        }

        return NormalizeContainerId(container.Id) is not { } id ||
               id.StartsWith(ours, StringComparison.OrdinalIgnoreCase) ||
               ours.StartsWith(id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A container monitoring found in place of the runner; it is never adopted as the runner.</summary>
    private bool IsRejectedContainer(ContainerInfo container)
        => _rejectedContainerId is { } rejected &&
           NormalizeContainerId(container.Id) is { } id &&
           (id.StartsWith(rejected, StringComparison.OrdinalIgnoreCase) || rejected.StartsWith(id, StringComparison.OrdinalIgnoreCase));

    private void FaultNameTaken(ContainerInfo? container = null)
        => Fault(container is not null && IsManaged(container) && !IsOwned(container)
            ? $"The runner container '{_options.ContainerName}' belongs to another copy of this app (another user account, or another computer using the same container engine). Stop it there, then start again."
            : $"A container named '{_options.ContainerName}' already exists and was not started by this app. Remove or rename it, then start again.");

    /// <summary>The last line `docker run --detach` printed (the ID), or null when there is none.</summary>
    private static string? NormalizeContainerId(string? output)
        => output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() is { Length: >= 12 } id ? id : null;

    /// <summary>The image the just-started runner runs, or null when the engine does not say (the checked one is assumed).</summary>
    private async Task<string?> StartedImageIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            return _containerId is null
                ? null
                : (await _engine.InspectContainerAsync(_containerId, cancellationToken).ConfigureAwait(false))?.ImageId;
        }
        catch (ContainerEngineException ex)
        {
            _logger.LogWarning(ex, "Could not read which image the runner started from.");
            return null;
        }
    }

    /// <summary>
    /// The exact container that was checked, by ID: the name may pass to another container between
    /// the check and the stop or removal, and that one must not be touched.
    /// </summary>
    private string Target(ContainerInfo container)
        => string.IsNullOrWhiteSpace(container.Id) ? _options.ContainerName : container.Id;

    /// <summary>
    /// The runner this supervisor launched or adopted, by ID once known, for reading its status,
    /// environment and log: a container that takes the name after a check is never read as the runner.
    /// </summary>
    private string OurContainer => _containerId ?? _options.ContainerName;

    private static bool IsManaged(ContainerInfo container)
        => container.Labels.TryGetValue(ManagedLabel, out var value) && value == "true";

    /// <summary>
    /// A runner this copy of the app started (now or before a crash). One another copy started, with
    /// another data folder or on another computer sharing the engine, is never adopted or stopped.
    /// </summary>
    private bool IsOwned(ContainerInfo container)
        => IsManaged(container) &&
           container.Labels.TryGetValue(OwnerLabel, out var owner) &&
           string.Equals(owner, _owner.Id, StringComparison.Ordinal);

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

        _logFollower?.Cancel();
        _logFollower?.Dispose();
        var follower = new CancellationTokenSource();
        _logFollower = follower;
        var tail = resume ? 0 : 200;
        _logTask = Task.Run(() => _engine.FollowLogsAsync(OurContainer, tail, AppendLog, follower.Token));
    }

    /// <summary>
    /// Follows the log of a still-running container again when following stopped on its own (the
    /// engine restarted, say), with a backoff so a stream that keeps failing does not flood the log.
    /// </summary>
    private void ResumeLogFollowerIfStopped(TimeSpan now)
    {
        if (_logFollower is null || !_logTask.IsCompleted || now < _logRetryAt)
        {
            return;
        }

        var reason = _logTask.Exception?.InnerException?.Message;
        AppendLog($"supervisor: the runner log stopped{(reason is null ? string.Empty : $" ({reason})")}; following it again. Lines written meanwhile are not shown.");
        _logRetryAt = now + RegistrationPolicy.ComputeBackoff(_logRestarts++, _options.MonitorInterval, LogRetryMaxBackoff);
        StartLogFollower(resume: true);
    }

    private async Task StopLogFollowerAsync()
    {
        var follower = _logFollower;
        _logFollower = null;
        if (follower is null)
        {
            return;
        }

        await follower.CancelAsync().ConfigureAwait(false);
        try
        {
            await _logTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ContainerEngineException)
        {
            // The log stream ends with the container.
        }
        finally
        {
            follower.Dispose();
        }
    }

    private void AppendLog(string line)
    {
        // The running container keeps the key it was started with after Settings change to
        // another one, so both are hidden.
        line = RedactKey(line, Volatile.Read(ref _activeRunnerKey));
        line = RedactKey(line, _settings.Current.RunnerKey?.Trim());
        _logs.Add(line);
        RaiseChanged();
    }

    private static string RedactKey(string line, string? key)
        => key is { Length: >= 8 } ? line.Replace(key, "[runner key]", StringComparison.Ordinal) : line;

    private void OnSettingsChanged(RunnerSettings settings) => RequestRefresh();

    private void Set(SupervisorState state, string message)
        => Update(view => view with { State = state, Message = message });

    /// <summary>Faults a start the person asked for; schedules another attempt for an automatic one.</summary>
    private void FailStart(string message, bool automatic)
    {
        if (!automatic)
        {
            Fault(message);
            return;
        }

        var delay = RegistrationPolicy.ComputeBackoff(_restartAttempt++, _options.RestartMinBackoff, _options.RestartMaxBackoff);
        _restartDueAt = Now() + delay;
        _logger.LogWarning("Runner restart failed: {Message}", message);
        AppendLog($"supervisor: {message} Trying again in {delay.TotalSeconds:0} s.");
        Set(SupervisorState.WaitingToRestart, $"{message} Trying again in {delay.TotalSeconds:0} seconds.");
    }

    private void Fault(string message)
    {
        _logger.LogWarning("Runner supervisor fault: {Message}", message);
        AppendLog("supervisor: " + message);
        Set(SupervisorState.Faulted, message);
    }

    private void Update(Func<SupervisorView, SupervisorView> change)
    {
        lock (_gate)
        {
            Volatile.Write(ref _view, change(_view) with { UpdatedUtc = _time.GetUtcNow() });
        }

        RaiseChanged();
    }

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

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
