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
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Work> active = new(StringComparer.Ordinal);

    public bool Start(SubmitPromptCommand command, Func<SubmitPromptCommand, CancellationToken, Task> execute)
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

    public void Cancel(Func<SubmitPromptCommand, bool> matches)
    {
        lock (gate)
            foreach (var work in active.Values.Where(work => matches(work.Command)).ToArray())
                work.Cancellation.Cancel();
    }

    public Task CancelAndWaitAsync(Func<SubmitPromptCommand, bool> matches)
    {
        Task[] tasks;
        lock (gate)
        {
            var matching = active.Values.Where(work => matches(work.Command)).ToArray();
            tasks = matching.Select(work => work.Completion.Task).ToArray();
            foreach (var work in matching) work.Cancellation.Cancel();
        }
        return Task.WhenAll(tasks);
    }

    private async Task RunAsync(Work work, Func<SubmitPromptCommand, CancellationToken, Task> execute)
    {
        try
        {
            await execute(work.Command, work.Cancellation.Token).ConfigureAwait(false);
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
            lock (gate)
            {
                active.Remove(work.Command.CommandId);
                work.Cancellation.Dispose();
            }
            work.Completion.TrySetResult();
        }
    }
}
