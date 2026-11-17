using Offloadr.Runner.V1;

namespace Offloadr.Runner.Core;

/// <summary>
/// Tracks physical execution/observation only, keyed by handoff. Acknowledgement
/// delivery has its own shutdown lifetime and must never hold Stop or a replacement
/// runtime open. A handoff runs at most once per process: a redelivery is refused
/// while its handler or held model demand is active, and after a grant until the
/// handoff can no longer be granted, because the platform answers a retried grant
/// from this instance with GRANTED again.
/// </summary>
internal sealed class PromptCommandWorkSet(CancellationToken shutdown, TimeProvider? timeProvider = null)
{
    // Bounds the granted-handoff memory. A remembered handoff is never forgotten early:
    // at the bound, new handoffs are refused (the platform delivers them again) until
    // the oldest is past its retention.
    internal const int MaxRetiredHandoffs = 16384;
    // The platform keeps a handoff for 60 minutes from acceptance, without renewal, and
    // never grants it after that. Remembering a granted handoff that long from this
    // process's own grant, on a monotonic clock, covers its whole life whatever the
    // wall clocks on either side say.
    internal static readonly TimeSpan GrantedHandoffRetention = TimeSpan.FromMinutes(65);

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
        public bool Granted { get; set; }
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
    // Handoff -> monotonic timestamp after which it is forgotten.
    private readonly Dictionary<string, long> retired = new(StringComparer.Ordinal);
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(command.HandoffId);
        ArgumentNullException.ThrowIfNull(execute);
        Work work;
        SubmitPromptCommand captured;
        lock (gate)
        {
            PruneCompleted();
            PruneRetired(time.GetTimestamp());
            if (shutdown.IsCancellationRequested || active.ContainsKey(command.HandoffId)
                || held.ContainsKey(command.HandoffId) || retired.ContainsKey(command.HandoffId)) return false;
            // Every active handoff may still be granted and remembered, so admission leaves room for it.
            if (retired.Count + active.Count >= MaxRetiredHandoffs)
            {
                RunnerLog.Warning(nameof(PromptCommandWorkSet), $"Prompt handoff {command.HandoffId} refused: {retired.Count} granted handoffs are still remembered.");
                return false;
            }
            captured = command.Clone();
            var identity = PromptWorkIdentity.Capture(captured, streamsHttpResponse);
            if (stopping.Any(boundary => boundary.Matches(identity))) return false;
            work = new(identity, new(shutdown), new(shutdown));
            active.Add(work.Identity.HandoffId, work);
            operations.Add(work);
        }
        // Preserve synchronous admission/execution when the callback can finish
        // immediately, without invoking user work under the admission lock.
        work.Original.SetResult(RunAsync(work, captured, execute));
        return true;
    }

    /// <summary>
    /// Records that the platform granted this handoff to this process. Once its handler
    /// ends, the handoff is remembered until it expires so a redelivery cannot run it again.
    /// </summary>
    public void MarkGranted(string handoffId)
    {
        lock (gate)
        {
            if (active.TryGetValue(handoffId, out var work)) work.Granted = true;
        }
    }

    public void HoldDemand(string handoffId, Task settling)
    {
        ArgumentNullException.ThrowIfNull(settling);
        lock (gate)
        {
            if (active.TryGetValue(handoffId, out var work)) work.HeldDemand = settling;
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

    private void PruneRetired(long now)
    {
        foreach (var expired in retired.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray())
            retired.Remove(expired);
    }

    // Admission keeps the count within the bound, so nothing is evicted here.
    private void Retire(Work work)
        => retired[work.Identity.HandoffId] = time.GetTimestamp() + (long)(GrantedHandoffRetention.TotalSeconds * time.TimestampFrequency);

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
            RunnerLog.Warning(nameof(PromptCommandWorkSet), $"Prompt handoff {work.Identity.HandoffId} ended with {error.GetType().Name}.");
        }
        finally
        {
            // Cleanup callbacks may remain pending after execution. Neither the
            // registry nor that join may retain completed prompt content.
            captured = null!;
            execute = null!;
            await work.Physical.CompleteAsync().ConfigureAwait(false);
            HeldDemand? holding = null;
            Task? settling;
            lock (gate)
            {
                if (active.TryGetValue(work.Identity.HandoffId, out var current) && ReferenceEquals(current, work))
                    active.Remove(work.Identity.HandoffId);
                if (work.Granted) Retire(work);
                settling = work.HeldDemand;
                work.HeldDemand = null;
                if (settling is { IsCompleted: false })
                {
                    // Admission refuses a handoff that is active or held, so none is replaced here.
                    holding = new(work.Identity, work.Demand, settling);
                    work.TransferredDemand = holding;
                    held.Add(work.Identity.HandoffId, holding);
                    operations.Add(holding);
                }
            }
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
                if (held.TryGetValue(demand.Identity.HandoffId, out var current) && ReferenceEquals(current, demand))
                    held.Remove(demand.Identity.HandoffId);
            }
        }
    }
}
