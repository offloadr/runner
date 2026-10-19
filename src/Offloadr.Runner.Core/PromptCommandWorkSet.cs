using Offloadr.Runner.V1;

namespace Offloadr.Runner.Core;

/// <summary>
/// Tracks physical execution/observation only. Acknowledgement delivery has its
/// own shutdown lifetime and must never hold Stop or a replacement runtime open.
/// Durable execution permission, not this process-local set, prevents replay.
/// </summary>
internal sealed class PromptCommandWorkSet(CancellationToken shutdown)
{
    private sealed class Work(SubmitPromptCommand command, CancellationToken shutdown)
    {
        public SubmitPromptCommand Command { get; } = command.Clone();
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        public CancellationTokenSource Demand { get; } = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? HeldDemand { get; set; }
    }

    /// <summary>The model demand of an accepted prompt after its work ended.</summary>
    private sealed record HeldDemand(SubmitPromptCommand Command, CancellationTokenSource Demand, Task Settling);

    private readonly object gate = new();
    private readonly Dictionary<string, Work> active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HeldDemand> held = new(StringComparer.Ordinal);

    public bool Start(SubmitPromptCommand command, Func<SubmitPromptCommand, CancellationToken, Task> execute)
        => Start(command, (captured, physical, _) => execute(captured, physical));

    /// <summary>
    /// Starts prompt work with two tokens: the physical one, cancelled by
    /// <see cref="Cancel"/> (interrupt) and <see cref="CancelAndWaitAsync"/>, and
    /// the demand one for the prompt's model demand once the runtime accepted it,
    /// cancelled only by <see cref="CancelAndWaitAsync"/> and <see cref="WithdrawDemand"/>.
    /// </summary>
    public bool Start(SubmitPromptCommand command, Func<SubmitPromptCommand, CancellationToken, CancellationToken, Task> execute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CommandId);
        Work work;
        lock (gate)
        {
            if (active.ContainsKey(command.CommandId)) return false;
            work = new(command, shutdown);
            active.Add(command.CommandId, work);
        }
        _ = RunAsync(work, execute);
        return true;
    }

    /// <summary>
    /// Keeps the work's demand token alive after its work ends, until
    /// <paramref name="settling"/> completes. The command itself is finished, so a
    /// later delivery of it (recovery) starts as new work.
    /// </summary>
    public void HoldDemand(string commandId, Task settling)
    {
        lock (gate)
        {
            if (active.TryGetValue(commandId, out var work))
                work.HeldDemand = settling;
        }
    }

    /// <summary>
    /// Cancels physical work. A runtime interrupt stops only the prompt it is
    /// executing, so accepted prompts that wait in its queue keep their demand.
    /// </summary>
    public void Cancel(Func<SubmitPromptCommand, bool> matches)
    {
        lock (gate)
            foreach (var work in active.Values.Where(work => matches(work.Command)).ToArray())
                work.Cancellation.Cancel();
    }

    public void WithdrawDemand(Func<SubmitPromptCommand, bool> matches)
    {
        CancellationTokenSource[] demands;
        lock (gate)
            demands = active.Values.Where(work => matches(work.Command)).Select(work => work.Demand)
                .Concat(held.Values.Where(demand => matches(demand.Command)).Select(demand => demand.Demand))
                .ToArray();
        CancelDemands(demands);
    }

    public Task CancelAndWaitAsync(Func<SubmitPromptCommand, bool> matches)
    {
        Task[] tasks;
        CancellationTokenSource[] heldDemands;
        lock (gate)
        {
            var matching = active.Values.Where(work => matches(work.Command)).ToArray();
            var matchingHeld = held.Values.Where(demand => matches(demand.Command)).ToArray();
            tasks = matching.Select(work => work.Completion.Task)
                .Concat(matchingHeld.Select(demand => demand.Settling))
                .ToArray();
            heldDemands = matchingHeld.Select(demand => demand.Demand).ToArray();
            foreach (var work in matching)
            {
                work.Cancellation.Cancel();
                work.Demand.Cancel();
            }
        }
        CancelDemands(heldDemands);
        return Task.WhenAll(tasks.Select(static task => task.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default)));
    }

    private static void CancelDemands(IEnumerable<CancellationTokenSource> demands)
    {
        foreach (var demand in demands)
        {
            try { demand.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task RunAsync(Work work, Func<SubmitPromptCommand, CancellationToken, CancellationToken, Task> execute)
    {
        try
        {
            await execute(work.Command, work.Cancellation.Token, work.Demand.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (work.Cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Native payloads and transport exceptions can contain protected data.
            // The durable receipt retains uncertainty; logs need only a diagnostic.
            RunnerLog.Warning(nameof(PromptCommandWorkSet), $"Prompt work {work.Command.CommandId} ended with {ex.GetType().Name}; recover its retained receipt.");
        }
        finally
        {
            HeldDemand? holding = null;
            HeldDemand? replaced = null;
            lock (gate)
            {
                active.Remove(work.Command.CommandId);
                work.Cancellation.Dispose();
                if (work.HeldDemand is { IsCompleted: false } settling)
                {
                    holding = new(work.Command, work.Demand, settling);
                    held.Remove(work.Command.CommandId, out replaced);
                    held.Add(work.Command.CommandId, holding);
                }
                else
                {
                    work.Demand.Dispose();
                }
            }
            if (replaced is not null) CancelDemands([replaced.Demand]);
            if (holding is not null)
                _ = holding.Settling.ContinueWith(
                    (_, state) => Release((HeldDemand)state!),
                    holding,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            work.Completion.TrySetResult();
        }
    }

    private void Release(HeldDemand demand)
    {
        lock (gate)
        {
            if (held.TryGetValue(demand.Command.CommandId, out var current) && ReferenceEquals(current, demand))
                held.Remove(demand.Command.CommandId);
        }
        demand.Demand.Dispose();
    }
}
