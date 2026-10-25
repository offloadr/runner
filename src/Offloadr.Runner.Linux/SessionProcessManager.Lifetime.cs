using Offloadr.Runner.V1;

namespace Offloadr.Runner.Linux;

internal sealed partial class SessionProcessManager
{
    private readonly object _lifetimeGate = new();
    private readonly CancellationTokenSource _lifetimeStopping = new();
    private readonly SemaphoreSlim _startSerial = new(1, 1);
    private readonly List<OwnedStart> _starts = [];
    private readonly List<Task> _stops = [];
    private readonly List<SessionContext> _runtimeRoots = [];
    private readonly Dictionary<string, int> _stoppingSessions = new(StringComparer.Ordinal);
    private bool _closing;
    private bool _stopsClosed;
    private Task? _disposal;

    public Task StartSessionAsync(
        StartSessionCommand command,
        Func<SessionPaths, CancellationToken, Task>? initializer,
        Func<SessionPaths, CancellationToken, Task>? afterWorkspaceRootsPrepared,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var captured = command.Clone();
        var sessionId = captured.SessionId.Trim();
        var runtime = new RuntimeIdentity(captured.LifecycleGeneration, captured.RuntimeEpoch, captured.RuntimeInstanceId).Normalized();
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_stoppingSessions.ContainsKey(sessionId))
                throw new InvalidOperationException("The session is still being stopped.");
            PruneCompletedRoots();
            var start = new OwnedStart(sessionId, runtime, cancellationToken, _lifetimeStopping.Token);
            start.Task = RunOwnedStartAsync(start, captured, initializer, afterWorkspaceRootsPrepared);
            _starts.Add(start);
            return start.Task;
        }
    }

    private async Task RunOwnedStartAsync(OwnedStart start, StartSessionCommand command,
        Func<SessionPaths, CancellationToken, Task>? initializer,
        Func<SessionPaths, CancellationToken, Task>? afterWorkspaceRootsPrepared)
    {
        var acquired = false;
        try
        {
            await _startSerial.WaitAsync(start.Token).ConfigureAwait(false);
            acquired = true;
            await StartSessionCoreAsync(command, initializer, afterWorkspaceRootsPrepared, start.Token).ConfigureAwait(false);
        }
        finally
        {
            try { await start.ReleaseAsync().ConfigureAwait(false); }
            finally { if (acquired) _startSerial.Release(); }
        }
    }

    private Task<bool> StopSessionCoreAsync(string sessionId, RuntimeIdentity? expectedRuntime,
        Func<string, CancellationToken, Task>? beforeCleanup, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return Task.FromResult(true);
        sessionId = sessionId.Trim();
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_stopsClosed, this);
            PruneCompletedRoots();
            _stoppingSessions.TryGetValue(sessionId, out var count);
            _stoppingSessions[sessionId] = count + 1;
            var stop = RunOwnedStopAsync(sessionId, expectedRuntime, beforeCleanup, cancellationToken);
            _stops.Add(stop);
            return stop;
        }
    }

    private async Task<bool> RunOwnedStopAsync(string sessionId, RuntimeIdentity? expectedRuntime,
        Func<string, CancellationToken, Task>? beforeCleanup, CancellationToken cancellationToken)
    {
        try
        {
            OwnedStart[] starts;
            lock (_lifetimeGate)
                starts = _starts.Where(start => start.SessionId == sessionId &&
                    (expectedRuntime is null || start.Runtime.SameRuntime(expectedRuntime.Value))).ToArray();
            foreach (var start in starts) await start.CancelAsync().ConfigureAwait(false);
            // A failed or cancelled start still owns cleanup until its original task ends.
            foreach (var start in starts)
            {
                try { await start.Task.ConfigureAwait(false); }
                catch (Exception ex) { LogSessionDebug(sessionId, $"Start ended during Stop; errorClass={ex.GetType().Name}."); }
            }
            return await StopSessionRuntimeCoreAsync(sessionId, expectedRuntime, beforeCleanup, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_lifetimeGate)
            {
                if (_stoppingSessions[sessionId] == 1) _stoppingSessions.Remove(sessionId);
                else _stoppingSessions[sessionId]--;
            }
        }
    }

    private void PruneCompletedRoots()
    {
        _starts.RemoveAll(start => start.Task.IsCompleted);
        _stops.RemoveAll(task => task.IsCompleted);
        _runtimeRoots.RemoveAll(context => context.MonitorTask.IsCompleted && context.Untracked.IsCompleted);
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            _closing = true;
            return new(_disposal ??= StopAndJoinRootsAsync());
        }
    }

    private async Task StopAndJoinRootsAsync()
    {
        await _lifetimeStopping.CancelAsync().ConfigureAwait(false);
        OwnedStart[] starts;
        lock (_lifetimeGate) starts = _starts.ToArray();
        foreach (var start in starts)
        {
            try { await start.Task.ConfigureAwait(false); }
            catch (Exception ex) { RunnerLog.Debug<SessionProcessManager>($"Start ended during shutdown; errorClass={ex.GetType().Name}."); }
        }

        Exception? stopFailure = null;
        try { await StopAllAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { stopFailure = ex; }
        Task[] roots;
        lock (_lifetimeGate)
        {
            // Completed operations have already reported their result to their caller.
            // Keep joining in-flight work without replaying an earlier failed Stop.
            PruneCompletedRoots();
            _stopsClosed = true;
            roots = _stops.Concat(_runtimeRoots.Select(JoinRuntimeMonitorAsync)).ToArray();
        }
        // WhenAll waits for every original root even if another root has already faulted.
        await Task.WhenAll(roots).ConfigureAwait(false);
        if (stopFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopFailure).Throw();
        lock (_lifetimeGate)
        {
            _starts.Clear();
            _stops.Clear();
            _runtimeRoots.Clear();
        }
        _httpClient.Dispose();
        _startSerial.Dispose();
        _lifetimeStopping.Dispose();
    }

    public void SignalRuntimeActivity(string sessionId, RuntimeIdentity runtime)
    {
        if (_sessions.TryGetValue(sessionId, out var context) && context.RuntimeIdentity.SameRuntime(runtime) &&
            !context.Cancellation.IsCancellationRequested && !context.StopInProgress)
        {
            try { if (!context.Process.HasExited) context.Activity.Signal(); }
            catch (InvalidOperationException) { } // Cleanup already released this exact process handle.
        }
    }

    private async Task SuperviseActivityAsync(SessionContext context)
    {
        var disposition = await context.Activity.Decision.ConfigureAwait(false);
        if (disposition is RuntimeActivityDisposition.Retired or RuntimeActivityDisposition.Rejected or RuntimeActivityDisposition.Expired)
        {
            LogSessionWarning(context.SessionId, $"Runtime activity admission ended: {disposition}.");
            // Only this captured process. Cleanup owns this supervisor and must never be called here.
            await TerminateProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class OwnedStart
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly CancellationTokenRegistration _caller;
        private readonly CancellationTokenRegistration _root;
        private Task? _cancelTask;
        private bool _released;

        public OwnedStart(string sessionId, RuntimeIdentity runtime, CancellationToken caller, CancellationToken root)
        {
            SessionId = sessionId;
            Runtime = runtime;
            _caller = caller.Register(() => CancelAsync());
            _root = root.Register(() => CancelAsync());
        }
        public string SessionId { get; }
        public RuntimeIdentity Runtime { get; }
        public Task Task { get; set; } = System.Threading.Tasks.Task.CompletedTask;
        public CancellationToken Token => _cancellation.Token;
        public Task CancelAsync()
        {
            lock (_gate) return _released ? System.Threading.Tasks.Task.CompletedTask : _cancelTask ??= _cancellation.CancelAsync();
        }
        public async Task ReleaseAsync()
        {
            await _caller.DisposeAsync().ConfigureAwait(false);
            await _root.DisposeAsync().ConfigureAwait(false);
            Task cancellation;
            lock (_gate)
            {
                _released = true;
                cancellation = _cancelTask ?? System.Threading.Tasks.Task.CompletedTask;
            }
            try { await cancellation.ConfigureAwait(false); }
            finally { _cancellation.Dispose(); }
        }
    }
}
