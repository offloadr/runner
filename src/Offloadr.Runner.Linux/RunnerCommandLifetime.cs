using Offloadr.Runner.Core;

namespace Offloadr.Runner.Linux;

/// <summary>Owns command descendants across stream reconnects and shutdown.</summary>
internal sealed class RunnerCommandLifetime(CancellationToken shutdown, Func<Task> requestParentShutdown) : IAsyncDisposable
{
    private sealed class Work
    {
        // Publication wakes joiners; they then join the actual observation root,
        // which awaits the original factory task without cancelling its wait.
        public TaskCompletionSource<Task> Original { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _gate = new();
    private readonly OwnedCancellation _cancellation = new(shutdown);
    private readonly HashSet<Work> _work = [];
    private Task? _stop;
    private bool _closed;
    private bool _failed;

    public CancellationToken Token => _cancellation.Token;

    public Task Start(Func<Task> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Work work;
        lock (_gate)
        {
            _work.RemoveWhere(item => item.Original.Task.IsCompletedSuccessfully && item.Original.Task.Result.IsCompleted);
            if (_closed || Token.IsCancellationRequested) return Task.CompletedTask;
            work = new();
            _work.Add(work);
        }

        // Do not invoke a user factory under the admission lock. Stop also owns
        // a factory that is still executing synchronously before returning a task.
        var original = ObserveAsync(factory);
        work.Original.SetResult(original);
        return original;
    }

    private async Task ObserveAsync(Func<Task> factory)
    {
        try
        {
            Token.ThrowIfCancellationRequested();
            await (factory() ?? throw new InvalidOperationException("Command factory returned no task.")).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_gate) _failed = true;
            _ = _cancellation.RequestAsync();
            // The enclosing control lifetime owns this cancellation task. It
            // must cancel peer lanes while we are still joining descendants.
            _ = requestParentShutdown();
            // Keep the failure fact, without retaining arbitrary exception data
            // in an idle registry or copying its message to another command.
            RunnerLog.Warning(nameof(RunnerCommandLifetime), $"Command work failed; errorClass={error.GetType().Name}.");
        }
        finally { factory = null!; }
    }

    public Task StopAsync()
    {
        TaskCompletionSource start;
        Task stop;
        lock (_gate)
        {
            if (_stop is not null) return _stop;
            _closed = true;
            start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stop = _stop = StopCoreAsync(start.Task, _work.ToArray());
        }
        start.SetResult();
        return stop;
    }

    private async Task StopCoreAsync(Task start, Work[] work)
    {
        await start.ConfigureAwait(false);
        var cancellation = _cancellation.RequestAsync();
        try { await Task.WhenAll(work.Select(JoinAsync)).ConfigureAwait(false); }
        catch { lock (_gate) _failed = true; }
        try { await cancellation.ConfigureAwait(false); }
        catch { lock (_gate) _failed = true; }
        try { await _cancellation.CompleteAsync().ConfigureAwait(false); }
        catch { lock (_gate) _failed = true; }
        bool failed;
        lock (_gate)
        {
            _work.Clear();
            failed = _failed;
        }
        if (failed) throw new InvalidOperationException("Command work failed during shutdown.");
    }

    private static async Task JoinAsync(Work work)
        => await (await work.Original.Task.ConfigureAwait(false)).ConfigureAwait(false);

    public ValueTask DisposeAsync() => new(StopAsync());
}
