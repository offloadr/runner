using System.Runtime.ExceptionServices;

namespace Offloadr.Runner.Linux;

/// <summary>Owns one command listener's lanes, progress callbacks and cleanup.</summary>
internal sealed class RunnerControlLifetime : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationTokenRegistration _hostShutdown;
    private readonly HashSet<ProgressCall> _progress = [];
    private readonly List<Exception> _progressFailures = [];
    private Task? _cancellation;
    private Task? _run;
    private Task? _disposal;
    private bool _progressClosed;
    private bool _disposing;

    public RunnerControlLifetime(CancellationToken hostShutdown)
    {
        Token = _shutdown.Token;
        _hostShutdown = hostShutdown.Register(() => _ = RequestShutdownAsync());
    }

    public CancellationToken Token { get; }

    public Task RequestShutdownAsync()
    {
        lock (_gate)
        {
            _progressClosed = true;
            return _cancellation ??= _shutdown.CancelAsync();
        }
    }

    public Task ReportProgressAsync(Func<CancellationToken, Task> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var call = new ProgressCall();
        lock (_gate)
        {
            // Producers may already have captured the old reporter when it is
            // detached. Their late invocations cannot reach a retired sink.
            if (_progressClosed) return Task.CompletedTask;
            _progress.RemoveWhere(previous => previous.Task.IsCompleted);
            call.Task = ReportCoreAsync(call, report);
            _progress.Add(call);
        }

        call.Start.SetResult();
        return call.Task;
    }

    private async Task ReportCoreAsync(ProgressCall call, Func<CancellationToken, Task> report)
    {
        await call.Start.Task.ConfigureAwait(false);
        try { await report(Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_gate) _progressFailures.Add(error);
            _ = RequestShutdownAsync();
            throw;
        }
    }

    public Task RunAsync(
        Action attach,
        Func<CancellationToken, Task> status,
        Func<CancellationToken, Task> localModels,
        Func<CancellationToken, Task> commands,
        Func<Task> drainGpu,
        Func<Task> drainPrompts,
        Func<Task> drainStartup,
        IReadOnlyList<Action> detach)
    {
        ArgumentNullException.ThrowIfNull(attach);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(localModels);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(drainGpu);
        ArgumentNullException.ThrowIfNull(drainPrompts);
        ArgumentNullException.ThrowIfNull(drainStartup);
        ArgumentNullException.ThrowIfNull(detach);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            if (_run is not null) throw new InvalidOperationException("Control lanes already started.");
            run = _run = RunCoreAsync(start.Task, attach, status, localModels, commands, drainGpu, drainPrompts, drainStartup, detach);
        }

        start.SetResult();
        return run;
    }

    private async Task RunCoreAsync(
        Task start,
        Action attach,
        Func<CancellationToken, Task> status,
        Func<CancellationToken, Task> localModels,
        Func<CancellationToken, Task> commands,
        Func<Task> drainGpu,
        Func<Task> drainPrompts,
        Func<Task> drainStartup,
        IReadOnlyList<Action> detach)
    {
        await start.ConfigureAwait(false);
        var lanes = new List<Task<IReadOnlyList<Exception>>>(3);
        var failures = new List<Exception>();
        try
        {
            if (!Token.IsCancellationRequested) attach();
            Start(status, required: true);
            Start(localModels, required: false);
            Start(commands, required: true);
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        catch (Exception error)
        {
            failures.Add(error);
            _ = RequestShutdownAsync();
        }

        foreach (var outcome in await Task.WhenAll(lanes).ConfigureAwait(false)) failures.AddRange(outcome);
        var cancellation = RequestShutdownAsync();

        // Each drain is started even if another factory throws or its original
        // work is still pending. A failure cannot skip a different owner.
        var drains = new List<Task>(3);
        Drain(drainGpu);
        Drain(drainPrompts);
        Drain(drainStartup);
        Task[] progress;
        lock (_gate) progress = _progress.Select(call => call.Task).ToArray();
        try { await Task.WhenAll(progress).ConfigureAwait(false); }
        catch { /* The original callback failures are captured below. */ }
        foreach (var task in drains)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception error) { failures.AddRange(task.Exception?.InnerExceptions ?? (IReadOnlyList<Exception>)[error]); }
        }
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        lock (_gate)
        {
            _progress.Clear();
            failures.AddRange(_progressFailures);
        }

        // Property removal and relay detachment also tolerate partial setup.
        foreach (var action in detach)
        {
            try { action(); }
            catch (Exception error) { failures.Add(error); }
        }
        ThrowFailures(failures);
        return;

        void Start(Func<CancellationToken, Task> factory, bool required)
        {
            if (Token.IsCancellationRequested) return;
            var original = factory(Token) ?? throw new InvalidOperationException("Control lane returned no task.");
            lanes.Add(ObserveLaneAsync(original, required));
        }

        void Drain(Func<Task> factory)
        {
            try { drains.Add(factory() ?? throw new InvalidOperationException("Control drain returned no task.")); }
            catch (Exception error) { failures.Add(error); }
        }
    }

    private async Task<IReadOnlyList<Exception>> ObserveLaneAsync(Task original, bool required)
    {
        try
        {
            await original.ConfigureAwait(false);
            if (!required || Token.IsCancellationRequested) return [];
            _ = RequestShutdownAsync();
            return [new InvalidOperationException("Required control lane ended before shutdown.")];
        }
        catch (OperationCanceledException) when (original.IsCanceled && Token.IsCancellationRequested) { return []; }
        catch (Exception error)
        {
            _ = RequestShutdownAsync();
            return original.Exception?.InnerExceptions ?? (IReadOnlyList<Exception>)[error];
        }
    }

    public ValueTask DisposeAsync()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task disposal;
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _disposing = true;
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
            else
            {
                var failures = new List<Exception>();
                Task[] progress;
                lock (_gate) progress = _progress.Select(call => call.Task).ToArray();
                try { await Task.WhenAll(progress).ConfigureAwait(false); }
                catch { /* Original failures remain owned by this lifetime. */ }
                try { await cancellation.ConfigureAwait(false); }
                catch (Exception error) { failures.Add(error); }
                lock (_gate)
                {
                    _progress.Clear();
                    failures.AddRange(_progressFailures);
                }
                ThrowFailures(failures);
            }
        }
        finally
        {
            _hostShutdown.Dispose();
            _shutdown.Dispose();
        }
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    private sealed class ProgressCall
    {
        public TaskCompletionSource Start { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Task { get; set; } = Task.CompletedTask;
    }
}
