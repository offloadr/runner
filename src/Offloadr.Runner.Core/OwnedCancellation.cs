namespace Offloadr.Runner.Core;

internal sealed class OwnedCancellation
{
    private readonly object cancellationGate = new();
    private readonly CancellationTokenSource source = new();
    private readonly CancellationTokenRegistration hostRegistration;
    private Task? cancellation;
    private bool closing;
    private bool closed;

    public OwnedCancellation(CancellationToken hostShutdown)
    {
        Token = source.Token;
        hostRegistration = hostShutdown.Register(() => _ = RequestAsync());
    }

    public CancellationToken Token { get; }

    public Task RequestAsync()
    {
        lock (cancellationGate)
        {
            if (closed || (closing && cancellation is null)) return Task.CompletedTask;
            // A second CancelAsync can return before the first callback
            // task ends. Keep and join that original until its owner closes.
            return cancellation ??= source.CancelAsync();
        }
    }

    // Exactly one operation owns completion, including when ownership
    // transfers to demand that remains after execution.
    public async Task CompleteAsync()
    {
        // Never wait for the host registration while holding its callback's
        // gate. Its callback only requests the independently owned task.
        hostRegistration.Dispose();
        Task? pending;
        lock (cancellationGate)
        {
            closing = true;
            pending = cancellation;
        }
        try
        {
            if (pending is not null) await pending.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A concurrent Stop owns the same original faulted task. A
            // fire-and-forget interrupt still observes it without content.
            RunnerLog.Warning(nameof(OwnedCancellation), $"Cancellation callbacks ended with {error.GetType().Name}.");
        }
        finally
        {
            source.Dispose();
            lock (cancellationGate)
            {
                closed = true;
                cancellation = null;
            }
        }
    }
}
