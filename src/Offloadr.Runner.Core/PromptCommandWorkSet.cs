using Offloadr.Runner.V1;

namespace Offloadr.Runner.Core;

/// <summary>
/// Tracks physical execution/observation only. Acknowledgement delivery has its
/// own shutdown lifetime and must never hold Stop or a replacement runtime open.
/// Durable execution permission, not this process-local set, prevents replay.
/// </summary>
internal sealed class PromptCommandWorkSet(CancellationToken shutdown)
{
    private abstract class Operation(PromptWorkIdentity identity, OwnedCancellation demand)
    {
        public PromptWorkIdentity Identity { get; } = identity;
        public OwnedCancellation Demand { get; } = demand;
        // Publication is notification only. Joining always awaits the original
        // task it publishes, including factories that have not returned yet.
        public TaskCompletionSource<Task> Original { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Work(PromptWorkIdentity identity, OwnedCancellation physical, OwnedCancellation demand)
        : Operation(identity, demand)
    {
        public OwnedCancellation Physical { get; } = physical;
        public Task? HeldDemand { get; set; }
        public HeldDemand? TransferredDemand { get; set; }
    }

    private sealed class HeldDemand(PromptWorkIdentity identity, OwnedCancellation demand, Task settling)
        : Operation(identity, demand)
    {
        public Task Settling { get; set; } = settling;
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Work> active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HeldDemand> held = new(StringComparer.Ordinal);
    private readonly HashSet<Operation> operations = [];
    private readonly HashSet<StopBoundary> stopping = [];

    private sealed class StopBoundary(Func<PromptWorkIdentity, bool> matches)
    {
        public Func<PromptWorkIdentity, bool> Matches { get; } = matches;
    }

    public bool Start(SubmitPromptCommand command, bool streamsHttpResponse, Func<SubmitPromptCommand, CancellationToken, Task> execute)
        => Start(command, streamsHttpResponse, (captured, physical, _) => execute(captured, physical));

    /// <summary>
    /// Physical cancellation interrupts only execution. Demand remains until
    /// terminal withdrawal or Stop, including after the command has finished.
    /// </summary>
    public bool Start(SubmitPromptCommand command, bool streamsHttpResponse, Func<SubmitPromptCommand, CancellationToken, CancellationToken, Task> execute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CommandId);
        ArgumentNullException.ThrowIfNull(execute);
        Work work;
        SubmitPromptCommand captured;
        lock (gate)
        {
            PruneCompleted();
            if (shutdown.IsCancellationRequested || active.ContainsKey(command.CommandId)) return false;
            captured = command.Clone();
            var identity = PromptWorkIdentity.Capture(captured, streamsHttpResponse);
            if (stopping.Any(boundary => boundary.Matches(identity))) return false;
            work = new(identity, new(shutdown), new(shutdown));
            active.Add(work.Identity.CommandId, work);
            operations.Add(work);
        }
        // Preserve synchronous admission/execution when the callback can finish
        // immediately, without invoking user work under the admission lock.
        work.Original.SetResult(RunAsync(work, captured, execute));
        return true;
    }

    public void HoldDemand(string commandId, Task settling)
    {
        ArgumentNullException.ThrowIfNull(settling);
        lock (gate)
        {
            if (active.TryGetValue(commandId, out var work)) work.HeldDemand = settling;
        }
    }

    public void Cancel(Func<PromptWorkIdentity, bool> matches)
    {
        Work[] matching;
        lock (gate) matching = active.Values.Where(work => matches(work.Identity)).ToArray();
        foreach (var work in matching) _ = work.Physical.RequestAsync();
    }

    public void WithdrawDemand(Func<PromptWorkIdentity, bool> matches)
    {
        OwnedCancellation[] demands;
        lock (gate)
        {
            PruneCompleted();
            demands = operations.Where(operation => matches(operation.Identity)).Select(operation => operation.Demand).Distinct().ToArray();
        }
        foreach (var demand in demands) _ = demand.RequestAsync();
    }

    public async Task CancelAndWaitAsync(Func<PromptWorkIdentity, bool> matches)
    {
        Operation[] matching;
        var boundary = new StopBoundary(matches);
        lock (gate)
        {
            PruneCompleted();
            matching = operations.Where(operation => matches(operation.Identity)).ToArray();
            stopping.Add(boundary);
        }
        try
        {
            var cancellations = matching.Select(operation => operation.Demand)
                .Concat(matching.OfType<Work>().Select(work => work.Physical)).Distinct()
                .Select(cancellation => cancellation.RequestAsync()).ToArray();
            var failures = new List<Exception>();
            // Callback failure cannot skip another cancellation or release the join.
            var originals = matching.Select(JoinOriginalAsync).ToArray();
            try { await Task.WhenAll(originals).ConfigureAwait(false); }
            catch
            {
                foreach (var original in originals.Where(task => task.IsFaulted)) failures.AddRange(original.Exception!.InnerExceptions);
            }
            try { await Task.WhenAll(cancellations).ConfigureAwait(false); }
            catch
            {
                foreach (var cancellation in cancellations.Where(task => task.IsFaulted)) failures.AddRange(cancellation.Exception!.InnerExceptions);
            }
            if (failures.Count > 0) throw new AggregateException(failures);
        }
        finally { lock (gate) stopping.Remove(boundary); }
    }

    private static async Task JoinOriginalAsync(Operation operation)
    {
        await (await operation.Original.Task.ConfigureAwait(false)).ConfigureAwait(false);
        if (operation is Work { TransferredDemand: { } demand }) await JoinOriginalAsync(demand).ConfigureAwait(false);
    }

    // Read-only observation: unlike Stop this neither cancels nor prunes the
    // registry. It joins the actual snapshot, including a later demand handoff.
    internal Task JoinCurrentOperationsAsync()
    {
        Operation[] snapshot;
        lock (gate) snapshot = operations.ToArray();
        return Task.WhenAll(snapshot.Select(JoinOriginalAsync));
    }

    private void PruneCompleted()
        => operations.RemoveWhere(operation => operation.Original.Task.IsCompletedSuccessfully && operation.Original.Task.Result.IsCompleted);

    private async Task RunAsync(Work work, SubmitPromptCommand captured,
        Func<SubmitPromptCommand, CancellationToken, CancellationToken, Task> execute)
    {
        try
        {
            work.Physical.Token.ThrowIfCancellationRequested();
            await execute(captured, work.Physical.Token, work.Demand.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (work.Physical.Token.IsCancellationRequested) { }
        catch (Exception error)
        {
            RunnerLog.Warning(nameof(PromptCommandWorkSet), $"Prompt work {work.Identity.CommandId} ended with {error.GetType().Name}; recover its retained receipt.");
        }
        finally
        {
            // Cleanup callbacks may remain pending after execution. Neither the
            // registry nor that join may retain completed prompt content.
            captured = null!;
            execute = null!;
            await work.Physical.CompleteAsync().ConfigureAwait(false);
            HeldDemand? holding = null;
            HeldDemand? replaced = null;
            Task? settling;
            lock (gate)
            {
                if (active.TryGetValue(work.Identity.CommandId, out var current) && ReferenceEquals(current, work))
                    active.Remove(work.Identity.CommandId);
                settling = work.HeldDemand;
                work.HeldDemand = null;
                if (settling is { IsCompleted: false })
                {
                    holding = new(work.Identity, work.Demand, settling);
                    work.TransferredDemand = holding;
                    held.Remove(work.Identity.CommandId, out replaced);
                    held.Add(work.Identity.CommandId, holding);
                    operations.Add(holding);
                }
            }
            if (replaced is not null) _ = replaced.Demand.RequestAsync();
            if (holding is not null) holding.Original.SetResult(ReleaseAsync(holding));
            else
            {
                if (settling is not null) _ = settling.Exception;
                settling = null;
                await work.Demand.CompleteAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ReleaseAsync(HeldDemand demand)
    {
        try { await demand.Settling.ConfigureAwait(false); }
        catch (OperationCanceledException) when (demand.Demand.Token.IsCancellationRequested) { }
        catch (Exception error)
        {
            RunnerLog.Warning(nameof(PromptCommandWorkSet), $"Prompt model demand ended with {error.GetType().Name}.");
        }
        finally
        {
            // A completed faulted Task can retain arbitrary exception data.
            // Keep metadata in the registry, without that finished content.
            demand.Settling = Task.CompletedTask;
            await demand.Demand.CompleteAsync().ConfigureAwait(false);
            lock (gate)
            {
                if (held.TryGetValue(demand.Identity.CommandId, out var current) && ReferenceEquals(current, demand))
                    held.Remove(demand.Identity.CommandId);
            }
        }
    }
}
