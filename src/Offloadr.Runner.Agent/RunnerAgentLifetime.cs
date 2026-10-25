using System.Runtime.ExceptionServices;

namespace Offloadr.Runner.Agent;

/// <summary>
/// Owns the agent's original producers and shutdown callbacks. Session cleanup
/// finishes before RunAsync returns to the scope that owns sidecars and channels.
/// </summary>
internal sealed class RunnerAgentLifetime : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private Task? _cancellation;
    private Task? _run;
    private Task? _disposal;
    private bool _closing;

    public RunnerAgentLifetime() => Token = _shutdown.Token;

    public CancellationToken Token { get; }
    public bool IsCancellationRequested => Token.IsCancellationRequested;

    public Task RequestShutdownAsync()
    {
        lock (_gate)
        {
            // A later CancelAsync can complete while the first one's callbacks
            // are still running. Every caller must join this original task.
            return _cancellation ??= _shutdown.CancelAsync();
        }
    }

    public Task RunAsync(
        Func<CancellationToken, Task> commandListener,
        Func<CancellationToken, Task> runtimeTelemetry,
        Func<CancellationToken, Task> sealedStorageEviction,
        Func<CancellationToken, Task> statusFile,
        Func<ValueTask> stopSessions)
    {
        ArgumentNullException.ThrowIfNull(commandListener);
        ArgumentNullException.ThrowIfNull(runtimeTelemetry);
        ArgumentNullException.ThrowIfNull(sealedStorageEviction);
        ArgumentNullException.ThrowIfNull(statusFile);
        ArgumentNullException.ThrowIfNull(stopSessions);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_run is not null) throw new InvalidOperationException("Agent producers already started.");
            run = _run = RunCoreAsync(start.Task, commandListener, runtimeTelemetry, sealedStorageEviction, statusFile, stopSessions);
        }

        // Factories can execute synchronously. Never invoke one under _gate.
        start.SetResult();
        return run;
    }

    private async Task RunCoreAsync(
        Task start,
        Func<CancellationToken, Task> commandListener,
        Func<CancellationToken, Task> runtimeTelemetry,
        Func<CancellationToken, Task> sealedStorageEviction,
        Func<CancellationToken, Task> statusFile,
        Func<ValueTask> stopSessions)
    {
        await start.ConfigureAwait(false);
        var observers = new List<Task<IReadOnlyList<Exception>>>(4);
        var failures = new List<Exception>();
        try
        {
            Start(commandListener, mustRemainRunning: true);
            Start(runtimeTelemetry);
            Start(sealedStorageEviction);
            Start(statusFile);
        }
        catch (OperationCanceledException) when (IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            failures.Add(error);
            _ = RequestShutdownAsync();
        }

        // Observers await the original tasks, even when cancellation is ignored.
        // One failed lane requests peer cancellation without waiting for its peers.
        foreach (var outcome in await Task.WhenAll(observers).ConfigureAwait(false))
            failures.AddRange(outcome);

        try { await RequestShutdownAsync().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }

        // Even failed cancellation callbacks or producer setup cannot skip this
        // cleanup, or move it after the caller's sidecar/channel disposal.
        try { await stopSessions().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }

        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
        return;

        void Start(Func<CancellationToken, Task> factory, bool mustRemainRunning = false)
        {
            if (IsCancellationRequested) return;
            var producer = factory(Token) ?? throw new InvalidOperationException("Agent producer returned no task.");
            observers.Add(ObserveAsync(producer, mustRemainRunning));
        }
    }

    private async Task<IReadOnlyList<Exception>> ObserveAsync(Task producer, bool mustRemainRunning)
    {
        try
        {
            await producer.ConfigureAwait(false);
            if (!mustRemainRunning || IsCancellationRequested) return [];
            _ = RequestShutdownAsync();
            return [new InvalidOperationException("Agent command listener ended before shutdown.")];
        }
        catch (OperationCanceledException) when (producer.IsCanceled && IsCancellationRequested)
        {
            return [];
        }
        catch (Exception error)
        {
            _ = RequestShutdownAsync();
            return producer.Exception?.InnerExceptions ?? (IReadOnlyList<Exception>)[error];
        }
    }

    public ValueTask DisposeAsync()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task disposal;
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _closing = true;
            disposal = _disposal = DisposeCoreAsync(start.Task, _run);
        }

        start.SetResult();
        return new(disposal);
    }

    private async Task DisposeCoreAsync(Task start, Task? run)
    {
        await start.ConfigureAwait(false);
        var cancellation = RequestShutdownAsync();
        try
        {
            if (run is not null) await run.ConfigureAwait(false);
            else await cancellation.ConfigureAwait(false);
        }
        finally
        {
            // RunCore joins the same cancellation task on every exit path.
            _shutdown.Dispose();
        }
    }
}
