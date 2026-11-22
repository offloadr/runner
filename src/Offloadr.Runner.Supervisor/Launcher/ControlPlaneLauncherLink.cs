using System.Reflection;
using System.Text;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Settings;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Supervisor.Launcher;

/// <summary>Timings of the launcher link; the defaults are the protocol's.</summary>
public sealed record LauncherLinkOptions
{
    /// <summary>A stream that sent nothing for this long is broken (the server sends at least every 15 s).</summary>
    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How often the latest progress of a launch in flight is sent again. The protocol asks for at
    /// least every 30 s; 15 leaves room for one lost report's timeout and retry.
    /// </summary>
    public TimeSpan ProgressRepeat { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan RegistrationMinBackoff { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RegistrationMaxBackoff { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan StreamMinBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan StreamMaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Backoff of a report that failed; a failed progress report is also tried again this soon.</summary>
    public TimeSpan ReportMinBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between sends of an outcome: with its 5-second call, inside the protocol's 30 seconds.</summary>
    public TimeSpan ReportMaxBackoff { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// The launcher link over Offloadr's runner control service. Its instance id lasts as long as the
/// object, so restarting <see cref="RunAsync"/> (after a settings change, say) is the same launcher
/// coming back, not a takeover; only a launcher that was superseded starts again as a new instance.
/// </summary>
public sealed class ControlPlaneLauncherLink : ILauncherLink, IDisposable
{
    internal const int MaxReportMessageBytes = 512;

    private static readonly string AppVersion = typeof(ControlPlaneLauncherLink).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    private readonly ILauncherControlPlane _controlPlane;
    private readonly LauncherLinkOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private LauncherLinkState _state = LauncherLinkState.Offline;
    // The instance of the target the link runs for, or ran for last.
    private string _instanceId = Guid.NewGuid().ToString();
    private CancellationTokenSource? _run;
    private bool _disposed;
    private Session? _session;

    // One book per target, each with its own instance, so switching the address or key away and
    // back forgets nothing, and a takeover on one target changes nothing on another.
    private readonly Dictionary<string, LaunchBook> _books = new(StringComparer.Ordinal);

    // The stream whose messages count; 0 when none does. A stream that broke or went silent is
    // revoked here before anything else, so a message it still delivers changes nothing.
    private long _liveStream;
    private long _streamCount;
    private CancellationTokenSource? _streamCall;
    private long _lastStreamMessage;

    // The latest progress of the launch in flight, sent again once _inFlightWait has passed since
    // _inFlightMark (monotonic). A launch's reports go out one at a time, through its gate.
    // Across every link, so a launch request from one link's run is never taken for another's.
    private static long s_runCount;
    private LaunchReport? _inFlight;
    private long _inFlightMark;
    private TimeSpan _inFlightWait;
    private readonly Dictionary<string, SemaphoreSlim> _reportGates = new(StringComparer.OrdinalIgnoreCase);
    private int _running;

    public ControlPlaneLauncherLink(ILogger<ControlPlaneLauncherLink> logger)
        : this(new GrpcLauncherControlPlane(), new LauncherLinkOptions(), TimeProvider.System, logger)
    {
    }

    internal ControlPlaneLauncherLink(ILauncherControlPlane controlPlane, LauncherLinkOptions options, TimeProvider time, ILogger logger)
    {
        _controlPlane = controlPlane;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>This launcher's registered instance.</summary>
    public string InstanceId
    {
        get
        {
            lock (_gate)
            {
                return _instanceId;
            }
        }
    }

    public LauncherLinkState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public event EventHandler? StateChanged;

    public async Task RunAsync(RunnerSettings settings, Func<LauncherProfile> profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            throw new InvalidOperationException("The launcher link is already running.");
        }

        try
        {
            // The key travels as call metadata, so nothing is sent without a usable key and an https address.
            if (!settings.HasHttpsOrigin || settings.RunnerKeyProblem is not null)
            {
                Update(_ => new LauncherLinkState(LauncherConnection.Refused, "Check the runner key and the Offloadr address in Settings.", false, null));
                return;
            }

            var key = settings.RunnerKey!.Trim();
            using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Session session;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var runnerId = RunnerAgentOptions.ResolveRunnerId(key, configuredRunnerId: null);
                var target = LaunchTargetOf(settings.OffloadrApiOrigin, runnerId);
                if (!_books.TryGetValue(target, out var book) || book.Superseded)
                {
                    // A new target, or one whose instance Offloadr refuses for good after a takeover:
                    // starting there again is a new instance, which takes over in turn. The first
                    // book takes the id the link was created with.
                    book = new LaunchBook(target, _books.Count == 0 && book is null ? _instanceId : Guid.NewGuid().ToString());
                    _books[target] = book;
                }

                _instanceId = book.InstanceId;
                session = new Session(settings with { RunnerKey = key }, runnerId, book.InstanceId, run.Token, book, Interlocked.Increment(ref s_runCount));
                _session = session;
                _run = run;
            }

            Update(_ => new LauncherLinkState(LauncherConnection.Connecting, null, false, null));
            await RunSessionAsync(session, run, profile).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    public string? LaunchTarget
    {
        get
        {
            lock (_gate)
            {
                return _session?.Target;
            }
        }
    }

    public LaunchReport? OutcomeOf(string launchId)
    {
        lock (_gate)
        {
            return _session is { } session && session.Book.Finished.TryGetValue(launchId?.Trim() ?? string.Empty, out var outcome) ? outcome : null;
        }
    }

    public bool MayStart(LaunchRequest launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        lock (_gate)
        {
            // The very request this run's latest state named: one an earlier run received is not it.
            // A refused heartbeat withdraws permission until one is accepted again (a heartbeat that
            // only failed to get through does not).
            return _session is { Lifetime.IsCancellationRequested: false, Registered: true, RefusedSinceAccepted: false } session &&
                   launch.Run == session.Run &&
                   !session.Book.Finished.ContainsKey(launch.LaunchId) &&
                   _liveStream != 0 &&
                   _state.StreamLive &&
                   _state.Launch == launch &&
                   _time.GetElapsedTime(_lastStreamMessage) < _options.KeepAliveTimeout;
        }
    }

    public async Task ReportAsync(LaunchReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!Guid.TryParse(report.LaunchId, out _))
        {
            throw new ArgumentException("A launch report needs the launch's UUID.", nameof(report));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // A cancelled submission records nothing, so it is never replayed as an outcome.
            return;
        }

        var launchId = report.LaunchId.Trim();
        Session? session;
        string? dropped = null;
        Task? delivery = null;
        lock (_gate)
        {
            session = _session;
            if (session is null || !session.Named.TryGetValue(launchId, out var issued) || issued != report.Launch)
            {
                // A launch belongs to the run (and launcher instance) whose stream named it.
                dropped = "this run of the launcher was not sent that launch";
            }
            else if (session.Lifetime.IsCancellationRequested)
            {
                // Shutting down: no outcome is recorded, so none is replayed as if it had been reported.
                dropped = "the launcher is stopping";
            }
            else if (!report.IsTerminal &&
                     (session.Book.Finished.ContainsKey(launchId) || !string.Equals(_state.Launch?.LaunchId, launchId, StringComparison.OrdinalIgnoreCase)))
            {
                // Progress only for the launch named now, and never after its outcome.
                dropped = "that launch is no longer in flight";
            }
            else if (!report.IsTerminal)
            {
                _inFlight = report;
                MarkInFlight(_options.ProgressRepeat);
            }
            else if (session.Book.Finished.TryGetValue(launchId, out var first) && first != report)
            {
                // One outcome per launch: the first one is the acknowledgement, resent as it was.
                dropped = $"its outcome was already reported as {first.Phase}";
            }
            else if (session.Book.Finished.ContainsKey(launchId) && !session.Book.Unacknowledged.Contains(launchId))
            {
                // Offloadr already answered this outcome: nothing to send again.
                return;
            }
            else
            {
                session.Book.Finished[launchId] = report;
                session.Book.Unacknowledged.Add(launchId);
                if (string.Equals(_inFlight?.LaunchId.Trim(), launchId, StringComparison.OrdinalIgnoreCase))
                {
                    _inFlight = null;
                }

                // Delivered for the life of the run, whatever becomes of this caller; a repeat joins it.
                if (!session.Deliveries.TryGetValue(launchId, out delivery) || delivery.IsCompleted)
                {
                    var owner = session;
                    delivery = Task.Run(() => DeliverOutcomeAsync(owner, report, owner.Lifetime), CancellationToken.None);
                    session.Deliveries[launchId] = delivery;
                }
            }
        }

        if (dropped is not null)
        {
            _logger.LogWarning("Launch report {Phase} for {LaunchId} dropped: {Reason}.", report.Phase, launchId, dropped);
            return;
        }

        if (delivery is not null)
        {
            try
            {
                await delivery.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The caller stopped waiting; the delivery goes on.
            }

            return;
        }

        // Nothing is sent once the run that owns the report has ended, whatever the caller's token says.
        if (session!.Lifetime.IsCancellationRequested)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Lifetime);
        await SendInTurnAsync(session, report, repeat: true, linked.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a terminal report, the acknowledgement, with the same content until Offloadr takes it or
    /// refuses it for good. One that is still unanswered when the run ends is sent again by the next
    /// run of the same launcher identity.
    /// </summary>
    private async Task DeliverOutcomeAsync(Session session, LaunchReport report, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (cancellationToken.IsCancellationRequested || !IsCurrent(session))
            {
                return;
            }

            if (await SendInTurnAsync(session, report, repeat: false, cancellationToken).ConfigureAwait(false) != SendOutcome.Retry)
            {
                lock (_gate)
                {
                    Settle(session, report.LaunchId.Trim());
                }

                return;
            }

            try
            {
                await Task.Delay(RegistrationPolicy.ComputeBackoff(attempt, _options.ReportMinBackoff, _options.ReportMaxBackoff), _time, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Offloadr answered the outcome of <paramref name="launchId"/>. Kept for a while, so the stream
    /// naming it again is still recognized, then forgotten, so a launcher that stays on for weeks does
    /// not keep every launch. Call under the lock.
    /// </summary>
    private void Settle(Session session, string launchId)
    {
        const int Kept = 32;
        var book = session.Book;
        if (!book.Unacknowledged.Remove(launchId))
        {
            return;
        }

        book.Answered.Enqueue(launchId);
        var protectedOnes = new List<string>();
        while (book.Answered.Count > Kept)
        {
            var old = book.Answered.Dequeue();
            // The launch the stream names now, or named last (it may be named again after a
            // reconnect), is not forgotten yet; it stays queued for a later pass.
            if (string.Equals(old, _state.Launch?.LaunchId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(old, book.HighestLaunchId, StringComparison.OrdinalIgnoreCase) ||
                book.Unacknowledged.Contains(old))
            {
                protectedOnes.Add(old);
                continue;
            }

            book.Finished.Remove(old);
            session.Named.Remove(old);
            _reportGates.Remove(old);
            if (session.Deliveries.TryGetValue(old, out var delivery) && delivery.IsCompleted)
            {
                session.Deliveries.Remove(old);
            }
        }

        foreach (var kept in protectedOnes)
        {
            book.Answered.Enqueue(kept);
        }
    }

    /// <summary>Sends again the outcomes an earlier run of this launcher identity left unanswered.</summary>
    /// <param name="pending">The outcomes earlier runs left unanswered, taken before this run received anything.</param>
    private async Task ResumeOutcomesAsync(Session session, IReadOnlyList<LaunchReport> pending, Task firstAccepted, CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            return;
        }

        try
        {
            await firstAccepted.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Each on its own retries (sends still go one at a time), so one that keeps failing holds up
        // no other; one this run is already delivering is joined, not sent twice.
        var deliveries = new List<Task>();
        lock (_gate)
        {
            foreach (var report in pending)
            {
                var launchId = report.LaunchId.Trim();
                if (!session.Book.Unacknowledged.Contains(launchId))
                {
                    continue;
                }

                if (!session.Deliveries.TryGetValue(launchId, out var delivery) || delivery.IsCompleted)
                {
                    delivery = Task.Run(() => DeliverOutcomeAsync(session, report, cancellationToken), CancellationToken.None);
                    session.Deliveries[launchId] = delivery;
                }

                deliveries.Add(delivery);
            }
        }

        await Task.WhenAll(deliveries).ConfigureAwait(false);
    }

    public async Task<ReleaseResult> ReleaseAsync(string launchId, string? executorInstanceId, string launchTarget, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(launchId, out _))
        {
            throw new ArgumentException("A release names the launch the container was started for.", nameof(launchId));
        }

        Session? session;
        lock (_gate)
        {
            // Not once the run is stopping: whatever would stop the container is being cancelled too.
            session = _session is { Registered: true, Lifetime.IsCancellationRequested: false } registered ? registered : null;
        }

        if (session is null)
        {
            // Asked again once registered: Offloadr checks a release against a registered launcher.
            return new ReleaseResult(false, false, "The launcher is not connected to Offloadr.");
        }

        if (!string.Equals(session.Target, launchTarget, StringComparison.Ordinal))
        {
            // Only the Offloadr that asked for the container knows whether something is assigned to it.
            return new ReleaseResult(false, false,
                "This container was started for another Offloadr address or runner key. Connect with those settings to release it, or stop it yourself.");
        }

        var request = new ReleaseRunnerRequest
        {
            RunnerId = session.RunnerId,
            InstanceId = session.InstanceId,
            LaunchId = launchId.Trim(),
            ExecutorInstanceId = executorInstanceId?.Trim() ?? string.Empty,
        };
        // Ended with the run as well: a release asked while availability is switched off is not sent.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Lifetime);
        try
        {
            var response = await _controlPlane.ReleaseAsync(session.Settings, request, linked.Token).ConfigureAwait(false);
            _logger.LogInformation("Release of launch {LaunchId}: released={Released}", request.LaunchId, response.Released);
            return new ReleaseResult(response.Released, true, response.Message);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The release was cancelled.", ex, cancellationToken);
        }
        catch (RpcException ex) when (IsTransient(ex.StatusCode) || ex.StatusCode is StatusCode.Unimplemented or StatusCode.NotFound)
        {
            // A missing route (a proxy's 404 during a rollout) did not ask Offloadr either.
            return new ReleaseResult(false, false, $"Could not reach Offloadr ({ex.StatusCode}).");
        }
        catch (RpcException ex)
        {
            return new ReleaseResult(false, true, $"Offloadr refused the release ({ex.StatusCode}).");
        }
        catch (HttpRequestException ex)
        {
            return new ReleaseResult(false, false, $"Could not reach Offloadr ({Describe(ex)}).");
        }
        catch (Exception ex) when (ex is ObjectDisposedException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The link stopped or was disposed meanwhile: nothing was confirmed, so keep the container.
            return new ReleaseResult(false, false, "The launcher stopped before Offloadr answered.");
        }
    }

    /// <summary>Ends a run in progress (it stops registering at once) and releases the connections.</summary>
    public void Dispose()
    {
        CancellationTokenSource? run;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            run = _run;
        }

        try
        {
            run?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run ended meanwhile.
        }

        (_controlPlane as IDisposable)?.Dispose();
    }

    private async Task RunSessionAsync(Session session, CancellationTokenSource run, Func<LauncherProfile> profile)
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisterRunnerResponse? final = null;
        List<LaunchReport> pending;
        lock (_gate)
        {
            pending = [.. session.Book.Unacknowledged.Select(id => session.Book.Finished[id])];
        }

        var stream = Task.Run(() => StreamLoopAsync(session, accepted.Task, run.Token), CancellationToken.None);
        var repeater = Task.Run(() => RepeatProgressAsync(session, run.Token), CancellationToken.None);
        var resumed = Task.Run(() => ResumeOutcomesAsync(session, pending, accepted.Task, run.Token), CancellationToken.None);
        try
        {
            await RunnerRegistrationLoop.RunAsync(
                (request, token) => RegisterOnceAsync(session, request, token),
                () =>
                {
                    var current = profile();
                    return RunnerRegistrationLoop.BuildLauncherRequest(session.RunnerId, session.InstanceId, AppVersion, current.Resources, current.EditorTemplates);
                },
                _options.RegistrationMinBackoff,
                _options.RegistrationMaxBackoff,
                accepted,
                _logger,
                run.Token,
                response => final = response).ConfigureAwait(false);
        }
        finally
        {
            await run.CancelAsync().ConfigureAwait(false);
            await QuietlyAsync(stream).ConfigureAwait(false);
            await QuietlyAsync(repeater).ConfigureAwait(false);
            await QuietlyAsync(resumed).ConfigureAwait(false);
            Task[] deliveries;
            lock (_gate)
            {
                deliveries = [.. session.Deliveries.Values];
            }

            foreach (var delivery in deliveries)
            {
                await QuietlyAsync(delivery).ConfigureAwait(false);
            }

            Mutate(_ =>
            {
                _session = null;
                _run = null;
                _inFlight = null;
                _liveStream = 0;
                // Gates no send holds any more go; a held one stays, so a later run's report on the
                // same launch still waits for the send in progress.
                foreach (var (launch, gate) in _reportGates.ToList())
                {
                    if (gate.CurrentCount == 1)
                    {
                        _reportGates.Remove(launch);
                    }
                }
                // Refused for good: the next start on this target is a new instance, however many
                // runs end before one registers.
                session.Book.Superseded |= final?.Rejection == RegistrationRejection.LauncherSuperseded;
                return final is null
                    ? LauncherLinkState.Offline
                    : new LauncherLinkState(FinalConnection(final), FinalMessage(final), false, null);
            });
        }
    }

    private async Task<RegisterRunnerResponse> RegisterOnceAsync(Session session, RegisterRunnerRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _controlPlane.RegisterAsync(session.Settings, request, cancellationToken).ConfigureAwait(false);
            if (response.Accepted)
            {
                Mutate(state =>
                {
                    session.Registered = true;
                    session.RefusedSinceAccepted = false;
                    return state with { Connection = LauncherConnection.Online, Message = null };
                });
            }
            else if (RunnerProtocolVersion.IsFinalRejection(response))
            {
                // Refused for good: nothing may start from here on, even before the run winds down.
                AbandonStream(state =>
                {
                    session.RefusedSinceAccepted = true;
                    return state;
                });
            }
            else
            {
                // Refused: nothing starts until a heartbeat is accepted and a fresh stream names the launch.
                AbandonStream(state =>
                {
                    session.RefusedSinceAccepted = true;
                    return state with
                    {
                        Connection = LauncherConnection.Refused,
                        Message = string.IsNullOrWhiteSpace(response.Message) ? "Offloadr did not accept this computer as a launcher." : response.Message.Trim(),
                    };
                });
            }

            // The text went to the state, for the person; the shared loop logs the reply, so it gets
            // the fields only. Free text from a server is not written to the logs.
            var logged = response.Clone();
            logged.Message = string.Empty;
            return logged;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The registration was cancelled.", ex, cancellationToken);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
        {
            // The key no longer counts: an open stream gives no permission any more.
            AbandonStream(state =>
            {
                session.RefusedSinceAccepted = true;
                return state with
                {
                    Connection = LauncherConnection.Refused,
                    Message = "Offloadr did not accept the runner key. Check it in Settings, or create a new one in the Offloadr web app.",
                };
            });
            throw new LauncherRegistrationFailedException(Describe(ex));
        }
        catch (RpcException ex)
        {
            Update(state => state with { Connection = LauncherConnection.Unreachable, Message = $"Could not reach Offloadr ({ex.StatusCode})." });
            throw new LauncherRegistrationFailedException(Describe(ex));
        }
        catch (HttpRequestException ex)
        {
            Update(state => state with { Connection = LauncherConnection.Unreachable, Message = $"Could not reach Offloadr ({Describe(ex)})." });
            throw new LauncherRegistrationFailedException(Describe(ex));
        }
    }

    /// <summary>
    /// A failed registration as the shared loop logs it: its status only, never the remote text,
    /// which may carry a signed URL or a credential.
    /// </summary>
    private sealed class LauncherRegistrationFailedException(string status) : Exception($"Registration failed ({status}).");

    private async Task StreamLoopAsync(Session session, Task firstAccepted, CancellationToken cancellationToken)
    {
        try
        {
            // The stream is fenced on a registered launcher instance.
            await firstAccepted.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            // After a refused heartbeat, a stream opens only once one is accepted again, so the state
            // that may let a launch start was sent after the recovery.
            while (IsRefused(session))
            {
                try
                {
                    await Task.Delay(_options.StreamMinBackoff, _time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            long id = 0;
            Mutate(state =>
            {
                // Checked again where the stream is installed: a refusal in between leaves it out.
                if (session.RefusedSinceAccepted)
                {
                    return null;
                }

                id = ++_streamCount;
                _liveStream = id;
                _streamCall = call;
                _lastStreamMessage = _time.GetTimestamp();
                return state with { StreamLive = false };
            });

            if (id == 0)
            {
                continue;
            }

            var watchdog = WatchStreamAsync(id, call);
            var received = false;
            try
            {
                var request = new LauncherStreamRequest
                {
                    RunnerId = session.RunnerId,
                    InstanceId = session.InstanceId,
                    Version = AppVersion,
                    ProtocolVersion = RunnerProtocolVersion.Current,
                };
                await foreach (var message in _controlPlane.StreamAsync(session.Settings, request, call.Token).WithCancellation(call.Token).ConfigureAwait(false))
                {
                    received = true;
                    if (!Receive(session, id, message))
                    {
                        break;
                    }
                }

                // Permission ends before anything else happens, a log write included.
                Revoke(id);
                if (!call.IsCancellationRequested)
                {
                    _logger.LogInformation("The launcher stream ended; reconnecting.");
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                Revoke(id);
                return;
            }
            catch (Exception) when (call.IsCancellationRequested)
            {
                // Ended on purpose: silence (the watchdog logs that) or an abandoned stream.
                Revoke(id);
                _logger.LogInformation("The launcher stream was closed; reconnecting.");
            }
            catch (Exception ex)
            {
                Revoke(id);
                _logger.LogWarning("The launcher stream failed: {Error}", Describe(ex));
            }
            finally
            {
                // Until the next stream's first state, nothing may start.
                Revoke(id);
                lock (_gate)
                {
                    if (ReferenceEquals(_streamCall, call))
                    {
                        _streamCall = null;
                    }
                }

                await call.CancelAsync().ConfigureAwait(false);
                await QuietlyAsync(watchdog).ConfigureAwait(false);
            }

            attempt = received ? 1 : attempt + 1;
            try
            {
                await Task.Delay(RegistrationPolicy.ComputeBackoff(attempt, _options.StreamMinBackoff, _options.StreamMaxBackoff), _time, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Takes one message of stream <paramref name="id"/>; false when that stream no longer counts.</summary>
    private bool Receive(Session session, long id, LauncherStreamResponse message)
    {
        var current = true;
        Mutate(state =>
        {
            if (_liveStream != id)
            {
                current = false;
                return null;
            }

            if (_time.GetElapsedTime(_lastStreamMessage) >= _options.KeepAliveTimeout)
            {
                // Silent for too long already: the stream is broken even if this message got through.
                // Only the first state of a new stream may let a launch start again.
                current = false;
                _liveStream = 0;
                return state with { StreamLive = false };
            }

            _lastStreamMessage = _time.GetTimestamp();
            if (message.PayloadCase != LauncherStreamResponse.PayloadOneofCase.State)
            {
                return null;
            }

            var launch = message.State.CurrentLaunch is { } command ? LaunchRequest.From(command, session.Run) : null;
            var book = session.Book;
            if (launch is not null && !Guid.TryParse(launch.LaunchId, out _))
            {
                // Not obeyed. Reconnecting gets a fresh state; keepalives alone would leave it standing.
                _logger.LogWarning("Ignored a launch without a valid id from Offloadr; reconnecting.");
                current = false;
                _liveStream = 0;
                return state with { StreamLive = false };
            }

            if (launch is not null)
            {
                var same = string.Equals(launch.LaunchId, book.HighestLaunchId, StringComparison.OrdinalIgnoreCase);
                if (launch.Sequence < book.HighestSequence ||
                    (launch.Sequence == book.HighestSequence && book.HighestLaunchId is not null && (!same || book.HighestWithdrawn)))
                {
                    // Only a higher sequence replaces a launch, and a withdrawn one never comes back. A
                    // state that breaks this is not obeyed, and leaves no valid latest state: nothing
                    // may start, and the stream reconnects for a fresh one.
                    _logger.LogWarning("Ignored launch {LaunchId} at sequence {Sequence}: launch {Highest} has sequence {HighestSequence}; reconnecting.",
                        launch.LaunchId, launch.Sequence, book.HighestLaunchId, book.HighestSequence);
                    current = false;
                    _liveStream = 0;
                    return state with { StreamLive = false };
                }

                book.HighestSequence = launch.Sequence;
                book.HighestLaunchId = launch.LaunchId;
                book.HighestWithdrawn = false;
                session.Named[launch.LaunchId] = launch;
            }
            else if (book.HighestLaunchId is not null)
            {
                book.HighestWithdrawn = true;
            }

            if (_inFlight is { } progress && !string.Equals(progress.LaunchId.Trim(), launch?.LaunchId, StringComparison.OrdinalIgnoreCase))
            {
                // Withdrawn by an authoritative state: its progress is not repeated any more.
                _inFlight = null;
            }

            return state with { StreamLive = true, Launch = launch };
        });
        return current;
    }

    /// <summary>
    /// The current stream no longer counts, whatever it sends: permission to start ends now, and its
    /// call is ended so the stream reconnects for a fresh state.
    /// </summary>
    private void AbandonStream(Func<LauncherLinkState, LauncherLinkState> change)
    {
        CancellationTokenSource? call = null;
        Mutate(state =>
        {
            _liveStream = 0;
            call = _streamCall;
            return change(state) with { StreamLive = false };
        });

        try
        {
            call?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // That call has ended already.
        }
    }

    /// <summary>Stream <paramref name="id"/> no longer counts: it broke, ended or went silent.</summary>
    private void Revoke(long id)
        => Mutate(state =>
        {
            if (_liveStream != id)
            {
                return null;
            }

            _liveStream = 0;
            return state with { StreamLive = false };
        });

    /// <summary>Ends stream <paramref name="id"/> once it has been silent for the keepalive timeout.</summary>
    private async Task WatchStreamAsync(long id, CancellationTokenSource call)
    {
        var check = Clamp(_options.KeepAliveTimeout / 9, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1));
        while (!call.IsCancellationRequested)
        {
            await Task.Delay(check, _time, call.Token).ConfigureAwait(false);
            bool silent;
            lock (_gate)
            {
                silent = _time.GetElapsedTime(_lastStreamMessage) >= _options.KeepAliveTimeout;
            }

            if (silent)
            {
                // Revoked first, so a message arriving while the call is torn down changes nothing.
                Revoke(id);
                _logger.LogWarning("The launcher stream sent nothing for {Seconds:0} s; reconnecting.", _options.KeepAliveTimeout.TotalSeconds);
                await call.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>Sends the latest progress of a launch in flight again, so Offloadr can tell slow from gone.</summary>
    private async Task RepeatProgressAsync(Session session, CancellationToken cancellationToken)
    {
        var check = Clamp(
            TimeSpan.FromTicks(Math.Min(_options.ProgressRepeat.Ticks / 4, _options.ReportMinBackoff.Ticks)),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(1));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(check, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            LaunchReport? due = null;
            lock (_gate)
            {
                if (_inFlight is { } report && _time.GetElapsedTime(_inFlightMark) >= _inFlightWait)
                {
                    due = report;
                    MarkInFlight(_options.ProgressRepeat);
                }
            }

            if (due is not null)
            {
                await SendInTurnAsync(session, due, repeat: true, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Sends one report after any other for the same launch (other launches' retries never hold it
    /// up). A progress report (<paramref name="repeat"/>) is sent only while it is still the launch's
    /// latest one in flight, so it never follows the terminal report; when it fails it is tried again
    /// soon, not a whole repeat interval later.
    /// </summary>
    private async Task<SendOutcome> SendInTurnAsync(Session session, LaunchReport report, bool repeat, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate;
        lock (_gate)
        {
            var launchId = report.LaunchId.Trim();
            if (!_reportGates.TryGetValue(launchId, out gate!))
            {
                gate = new SemaphoreSlim(1, 1);
                _reportGates[launchId] = gate;
            }
        }

        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return SendOutcome.Retry;
        }

        try
        {
            if (repeat && !IsInFlight(report))
            {
                return SendOutcome.Sent;
            }

            var outcome = await SendReportAsync(session, BuildReport(session, report), cancellationToken).ConfigureAwait(false);
            if (repeat && outcome != SendOutcome.Sent)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_inFlight, report))
                    {
                        if (outcome == SendOutcome.Final)
                        {
                            _inFlight = null;
                        }
                        else
                        {
                            MarkInFlight(_options.ReportMinBackoff);
                        }
                    }
                }
            }

            return outcome;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool IsInFlight(LaunchReport report)
    {
        lock (_gate)
        {
            return ReferenceEquals(_inFlight, report);
        }
    }

    /// <summary>The in-flight report is due again after <paramref name="wait"/>. Call under the lock.</summary>
    private void MarkInFlight(TimeSpan wait)
    {
        _inFlightMark = _time.GetTimestamp();
        _inFlightWait = wait;
    }

    private async Task<SendOutcome> SendReportAsync(Session session, ReportRunnerLaunchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _controlPlane.ReportAsync(session.Settings, request, cancellationToken).ConfigureAwait(false);
            return SendOutcome.Sent;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            return SendOutcome.Retry;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        {
            // Offloadr cannot take this report as it is. Any other failure may pass (a proxy's 404,
            // a registration Offloadr has not seen yet), so the report stays pending while the run lasts.
            _logger.LogWarning("Offloadr refused the {Phase} report for launch {LaunchId} ({Status}).", request.Phase, request.LaunchId, ex.StatusCode);
            return SendOutcome.Final;
        }
        catch (Exception ex) when (ex is RpcException or HttpRequestException or ObjectDisposedException)
        {
            _logger.LogWarning("Could not send the {Phase} report for launch {LaunchId}: {Error}", request.Phase, request.LaunchId, Describe(ex));
            return SendOutcome.Retry;
        }
    }

    private static ReportRunnerLaunchRequest BuildReport(Session session, LaunchReport report)
    {
        var request = new ReportRunnerLaunchRequest
        {
            RunnerId = session.RunnerId,
            InstanceId = session.InstanceId,
            LaunchId = report.LaunchId.Trim(),
            Phase = report.Phase,
            // The protocol carries a message only with FAILED.
            Message = report.Phase == RunnerLaunchPhase.Failed ? Truncate(report.Message, MaxReportMessageBytes) : string.Empty,
        };
        if (report.Phase == RunnerLaunchPhase.Downloading)
        {
            request.DownloadCompletedBytes = report.DownloadedBytes;
            request.DownloadTotalBytes = report.TotalBytes;
        }

        return request;
    }

    private bool IsRefused(Session session)
    {
        lock (_gate)
        {
            return session.RefusedSinceAccepted;
        }
    }

    private bool IsCurrent(Session session)
    {
        lock (_gate)
        {
            return ReferenceEquals(_session, session);
        }
    }

    private void Update(Func<LauncherLinkState, LauncherLinkState> change) => Mutate(change);

    /// <summary>Changes the state under the lock (null: no change), then tells listeners outside it.</summary>
    private void Mutate(Func<LauncherLinkState, LauncherLinkState?> change)
    {
        LauncherLinkState? next;
        lock (_gate)
        {
            next = change(_state);
            if (next is null || next == _state)
            {
                return;
            }

            _state = next;
        }

        if (StateChanged is not { } handlers)
        {
            return;
        }

        // A listener's failure must not stop the lane that changed the state (the stream, say).
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // Its type only: the exception's text could carry anything the listener had.
                _logger.LogError("A launcher state listener failed: {Error}", Describe(ex));
            }
        }
    }

    /// <summary>A failure as its kind and status only: remote text may carry a signed URL or a credential.</summary>
    private static string Describe(Exception ex) => ex is RpcException rpc ? rpc.StatusCode.ToString() : ex.GetType().Name;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
        => value < min ? min : value > max ? max : value;

    private static bool IsTransient(StatusCode status)
        => status is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Unknown or StatusCode.Internal
            or StatusCode.ResourceExhausted or StatusCode.Aborted or StatusCode.Cancelled;

    private static LauncherConnection FinalConnection(RegisterRunnerResponse response)
        => RunnerProtocolVersion.IsProtocolRejection(response) ? LauncherConnection.Outdated : LauncherConnection.Superseded;

    private static string FinalMessage(RegisterRunnerResponse response) => response.Rejection switch
    {
        RegistrationRejection.RunnerOutdated => "Offloadr needs a newer version of this app. Update it to offer this computer's GPU again.",
        RegistrationRejection.ServerOutdated => "This app is newer than Offloadr supports yet. Use an earlier version of the app until Offloadr is updated.",
        _ => "Another Offloadr Runner app took over this runner, perhaps on another computer. Start it here to offer this computer's GPU again.",
    };

    /// <summary>At most <paramref name="maxBytes"/> of UTF-8, cut at a character boundary.</summary>
    internal static string Truncate(string? text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text ?? string.Empty;
        }

        var length = 0;
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes)
            {
                break;
            }

            bytes += rune.Utf8SequenceLength;
            length += rune.Utf16SequenceLength;
        }

        return text[..length];
    }

    private static async Task QuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private enum SendOutcome
    {
        Sent,
        Retry,
        Final,
    }

    /// <param name="Lifetime">Cancelled when this run ends; nothing is sent for it afterwards.</param>
    private sealed record Session(RunnerSettings Settings, string RunnerId, string InstanceId, CancellationToken Lifetime, LaunchBook Book, long Run)
    {
        public string Target => LaunchTargetOf(Settings.OffloadrApiOrigin, RunnerId);

        /// <summary>The delivery of each outcome this run sends. Guarded by the link's lock.</summary>
        public Dictionary<string, Task> Deliveries { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every launch this run's stream named, withdrawn ones included. Guarded by the link's lock.</summary>
        public Dictionary<string, LaunchRequest> Named { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Offloadr accepted a registration of this run: its instance is registered.</summary>
        public bool Registered { get; set; }

        /// <summary>Offloadr refused a heartbeat after the last one it accepted.</summary>
        public bool RefusedSinceAccepted { get; set; }
    }

    /// <summary>
    /// What one launcher identity (address, runner, instance) was sent and reported. Kept across runs
    /// of that identity, so a restart of the link does not forget an outcome or start a launch again.
    /// Guarded by the link's lock.
    /// </summary>
    private sealed class LaunchBook(string target, string instanceId)
    {
        public string Target { get; } = target;

        /// <summary>The launcher instance that registers for this target.</summary>
        public string InstanceId { get; } = instanceId;

        /// <summary>Offloadr superseded that instance: it is refused for good.</summary>
        public bool Superseded { get; set; }

        /// <summary>The terminal report submitted for each launch; no progress or other outcome follows.</summary>
        public Dictionary<string, LaunchReport> Finished { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Launches whose outcome Offloadr has not answered yet.</summary>
        public HashSet<string> Unacknowledged { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Launches whose outcome Offloadr answered, oldest first; only the latest few are kept.</summary>
        public Queue<string> Answered { get; } = new();

        /// <summary>The highest launch sequence named, and that launch: only a higher sequence replaces it.</summary>
        public ulong HighestSequence { get; set; }
        public string? HighestLaunchId { get; set; }

        /// <summary>A state named no launch since that one: it is withdrawn and never comes back.</summary>
        public bool HighestWithdrawn { get; set; }
    }

    /// <summary>The canonical origin (no default port, lower case) and the runner id.</summary>
    private static string LaunchTargetOf(string origin, string runnerId)
    {
        // Scheme, host and port only, never user information a URL might carry. An IPv6 host keeps
        // its brackets and its scope id (fe80::1%3 and fe80::1%7 can be different servers).
        string canonical;
        if (Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri))
        {
            var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.DnsSafeHost}]" : uri.IdnHost;
            canonical = $"{uri.Scheme}://{host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}";
        }
        else
        {
            canonical = origin.Trim().TrimEnd('/');
        }
        return $"{canonical.ToLowerInvariant()}#{runnerId}";
    }
}
