namespace Offloadr.Runner.Core;

/// <summary>Closes admission before joining original operations and their cancellation callbacks.</summary>
internal sealed class OwnedOperationScope
{
    private readonly object gate = new();
    private readonly OwnedCancellation cancellation = new(CancellationToken.None);
    // Successful observation tasks do not retain caller results or arbitrary exception data.
    private readonly HashSet<Task> roots = [];
    private Task? completion;
    private bool closed;
    private bool cancellationFailed;

    public CancellationToken Token => cancellation.Token;
    public bool IsClosed { get { lock (gate) return closed; } }

    public Task Run(Func<CancellationToken, Task> operation, CancellationToken caller = default)
        => Run(async token => { await operation(token).ConfigureAwait(false); return true; }, caller);

    public Task<T> Run<T>(Func<CancellationToken, Task<T>> operation, CancellationToken caller = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        TaskCompletionSource start;
        Task<T> original;
        lock (gate)
        {
            if (closed) return Task.FromCanceled<T>(new CancellationToken(true));
            roots.RemoveWhere(task => task.IsCompleted);
            start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            original = InvokeAsync(start.Task, operation, caller);
            roots.Add(ObserveAsync(original));
        }
        // A factory may block synchronously before returning its task. Its root
        // is already published, and no user code runs under the admission gate.
        start.SetResult();
        return original;
    }

    private async Task<T> InvokeAsync<T>(Task start, Func<CancellationToken, Task<T>> operation, CancellationToken caller)
    {
        await start.ConfigureAwait(false);
        var owned = new OwnedCancellation(caller);
        var registration = Token.Register(() => _ = owned.RequestAsync());
        try
        {
            owned.Token.ThrowIfCancellationRequested();
            return await operation(owned.Token).ConfigureAwait(false);
        }
        finally
        {
            operation = null!;
            // Never unregister while holding the callback's gate. Completion
            // includes the first CancelAsync task, even when execution has ended.
            registration.Dispose();
            await owned.CompleteAsync().ConfigureAwait(false);
            if (owned.CallbackFailed) lock (gate) cancellationFailed = true;
        }
    }

    private static async Task ObserveAsync(Task original)
    {
        try { await original.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            RunnerLog.Warning(nameof(OwnedOperationScope), $"Operation ended with {error.GetType().Name}.");
        }
        finally { original = null!; }
    }

    // Synchronous filesystem operations and watcher callbacks also borrow the
    // resource. A callback admitted before Close must leave before disposal.
    public IDisposable? TryEnter()
    {
        lock (gate)
        {
            if (closed) return null;
            roots.RemoveWhere(task => task.IsCompleted);
            var lease = new Lease();
            roots.Add(lease.Completion.Task);
            return lease;
        }
    }

    private sealed class Lease : IDisposable
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() => Completion.TrySetResult();
    }

    public void CloseAdmission()
    {
        lock (gate) closed = true;
    }

    public Task RequestCancellation()
    {
        CloseAdmission();
        return cancellation.RequestAsync();
    }

    public Task CompleteAsync()
    {
        TaskCompletionSource start;
        Task result;
        lock (gate)
        {
            if (completion is not null) return completion;
            closed = true;
            start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            result = completion = CompleteCoreAsync(start.Task, roots.ToArray());
        }
        start.SetResult();
        return result;
    }

    private async Task CompleteCoreAsync(Task start, Task[] admitted)
    {
        await start.ConfigureAwait(false);
        var callbacks = cancellation.RequestAsync();
        // Operation errors belong to their callers. Observation still joins
        // every original; a normal failed download does not poison shutdown.
        await Task.WhenAll(admitted).ConfigureAwait(false);
        try { await callbacks.ConfigureAwait(false); }
        catch (Exception error)
        {
            RunnerLog.Warning(nameof(OwnedOperationScope), $"Cancellation ended with {error.GetType().Name}.");
        }
        await cancellation.CompleteAsync().ConfigureAwait(false);
        bool failed;
        lock (gate)
        {
            roots.Clear();
            failed = cancellationFailed || cancellation.CallbackFailed;
        }
        if (failed) throw new InvalidOperationException("Operation cancellation failed during shutdown.");
    }
}
