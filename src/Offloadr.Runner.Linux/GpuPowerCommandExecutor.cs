using Grpc.Core;
using Offloadr.Common.V1;
using Offloadr.Runner.Core;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Linux;

/// <summary>Retries fenced grants and retains the original execution result for acknowledgement recovery.</summary>
public sealed class GpuPowerCommandExecutor(GpuPowerLimitService service)
{
    /// <summary>
    /// Completed deliveries retained to absorb redelivery of the same command id
    /// without re-execution. Older ones are dropped; a later redelivery of a dropped
    /// command is still fenced by the control plane's execution grant.
    /// </summary>
    internal const int MaxRetainedCompletedDeliveries = 256;

    private sealed record Delivery(SetGpuPowerLimitCommand Command, Task Work);
    private readonly Dictionary<string, Delivery> _deliveries = new(StringComparer.Ordinal);
    private readonly Queue<string> _completedDeliveries = new();
    private readonly SemaphoreSlim _physicalExecution = new(1, 1);
    private readonly object _admission = new();
    private readonly OwnedCancellation _stopping = new(CancellationToken.None);
    private readonly HashSet<Task> _operations = [];
    private Task? _drain;
    private bool _closed;
    private bool _operationFailed;

    public Task ActivateSessionAsync(Action activate, CancellationToken shutdown)
    {
        TaskCompletionSource start;
        Task original;
        lock (_admission)
        {
            if (_closed) return Task.FromCanceled(new CancellationToken(canceled: true));
            if (shutdown.IsCancellationRequested) return Task.FromCanceled(shutdown);
            start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            original = ActivateCoreAsync(activate, shutdown, start.Task);
            TrackOriginal(original, shutdown, start.Task);
        }
        start.SetResult();
        return original;
    }

    private async Task ActivateCoreAsync(Action activate, CancellationToken shutdown, Task start)
    {
        await start.ConfigureAwait(false);
        var cancellation = new OwnedCancellation(shutdown);
        var stopping = _stopping.Token.Register(() => _ = cancellation.RequestAsync());
        shutdown = cancellation.Token;
        try
        {
            await _physicalExecution.WaitAsync(shutdown).ConfigureAwait(false);
            try { shutdown.ThrowIfCancellationRequested(); activate(); }
            finally { _physicalExecution.Release(); }
        }
        finally
        {
            activate = null!;
            stopping.Dispose();
            await cancellation.CompleteAsync().ConfigureAwait(false);
        }
    }

    public Task HandleAsync(SetGpuPowerLimitCommand command, string runnerId,
        Func<string> activeSession, Func<ulong> activeGeneration,
        Func<GrantGpuPowerExecutionRequest, CancellationToken, Task<GrantGpuPowerExecutionResponse>> grant,
        Func<AcknowledgeGpuPowerLimitRequest, CancellationToken, Task> acknowledge,
        CancellationToken shutdown)
    {
        TaskCompletionSource start;
        Task original;
        lock (_admission)
        {
            if (_closed || shutdown.IsCancellationRequested) return Task.CompletedTask;
            var captured = command.Clone();
            if (_deliveries.TryGetValue(captured.CommandId, out var delivery))
            {
                if (delivery.Command.Equals(captured)) return delivery.Work;
                RunnerLog.Error(nameof(GpuPowerCommandExecutor), $"Conflicting delivery for power command {captured.CommandId}");
                return Task.CompletedTask;
            }
            start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            original = ExecuteAsync(captured, runnerId, activeSession, activeGeneration, grant, acknowledge, shutdown, start.Task);
            _deliveries.Add(captured.CommandId, new(captured, original));
            TrackOriginal(original, shutdown, start.Task);
        }
        start.SetResult();
        return original;
    }

    internal int RetainedDeliveryCount { get { lock (_admission) return _deliveries.Count; } }

    // Caller holds admission; neither original work nor its observer can run
    // until publication finishes and the caller opens their shared start gate.
    private void TrackOriginal(Task original, CancellationToken shutdown, Task start)
    {
        _operations.RemoveWhere(task => task.IsCompleted);
        _operations.Add(ObserveOriginalAsync(original, shutdown, start));
    }

    private async Task ObserveOriginalAsync(Task original, CancellationToken shutdown, Task start)
    {
        await start.ConfigureAwait(false);
        try { await original.ConfigureAwait(false); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested || _stopping.Token.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_admission) _operationFailed = true;
            RunnerLog.Warning(nameof(GpuPowerCommandExecutor), $"GPU work failed; errorClass={error.GetType().Name}.");
        }
        finally { original = null!; }
    }

    private void RetireDelivery(string commandId)
    {
        lock (_admission)
        {
            _completedDeliveries.Enqueue(commandId);
            while (_completedDeliveries.Count > MaxRetainedCompletedDeliveries)
            {
                _deliveries.Remove(_completedDeliveries.Dequeue());
            }
        }
    }

    public Task CancelAndDrainAsync()
    {
        TaskCompletionSource start;
        Task drain;
        lock (_admission)
        {
            if (_drain is not null) return _drain;
            _closed = true;
            start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            drain = _drain = DrainCoreAsync(start.Task, _operations.ToArray());
        }
        start.SetResult();
        return drain;
    }

    private async Task DrainCoreAsync(Task start, Task[] work)
    {
        await start.ConfigureAwait(false);
        var cancellation = _stopping.RequestAsync();
        var failed = false;
        // These successful observation roots join the actual activation and
        // delivery tasks, including each operation's original callback task.
        try { await Task.WhenAll(work).ConfigureAwait(false); }
        catch { failed = true; }
        try { await cancellation.ConfigureAwait(false); }
        catch { failed = true; }
        try { await _stopping.CompleteAsync().ConfigureAwait(false); }
        catch { failed = true; }
        lock (_admission)
        {
            _operations.Clear();
            failed |= _operationFailed;
        }
        if (failed) throw new InvalidOperationException("GPU work failed during shutdown.");
    }

    private async Task ExecuteAsync(SetGpuPowerLimitCommand command, string runnerId,
        Func<string> activeSession, Func<ulong> activeGeneration,
        Func<GrantGpuPowerExecutionRequest, CancellationToken, Task<GrantGpuPowerExecutionResponse>> grant,
        Func<AcknowledgeGpuPowerLimitRequest, CancellationToken, Task> acknowledge,
        CancellationToken shutdown, Task start)
    {
        await start.ConfigureAwait(false);
        var cancellation = new OwnedCancellation(shutdown);
        var stopping = _stopping.Token.Register(() => _ = cancellation.RequestAsync());
        shutdown = cancellation.Token;
        try
        {
            // Retry the fenced grant, never the physical operation. If a prior
            // attempt committed, the store denies execution on the next attempt.
            var mayExecute = await GetExecutionPermissionAsync(new()
            {
                CommandId = command.CommandId,
                RunnerId = runnerId,
                SessionId = command.SessionId,
                Target = command.Target?.Clone(),
            }, grant, shutdown).ConfigureAwait(false);
            if (!mayExecute) return;
            GpuPowerLimitApplyResult result;
            try
            {
                await _physicalExecution.WaitAsync(shutdown).ConfigureAwait(false);
                try { result = await service.ApplyAsync(command, activeSession, activeGeneration, shutdown).ConfigureAwait(false); }
                finally { _physicalExecution.Release(); }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return; }
            catch (Exception)
            {
                result = GpuPowerLimitApplyResult.Failure("Physical execution could not be confirmed.", outcome: GpuPowerOutcome.Unknown);
            }
            var original = new AcknowledgeGpuPowerLimitRequest
            {
                CommandId = command.CommandId,
                RunnerId = runnerId,
                SessionId = command.SessionId,
                Target = command.Target?.Clone(),
                Outcome = result.Outcome,
                Message = result.Message,
                RequestedWatts = command.TargetWatts,
                AppliedWatts = result.AppliedWatts,
                Telemetry = result.Telemetry,
            };
            var delay = TimeSpan.FromMilliseconds(250);
            while (!shutdown.IsCancellationRequested)
            {
                try
                {
                    await acknowledge(original.Clone(), shutdown).ConfigureAwait(false);
                    return;
                }
                catch (RpcException ex) when (ex.StatusCode is StatusCode.InvalidArgument or StatusCode.FailedPrecondition
                    or StatusCode.NotFound or StatusCode.PermissionDenied or StatusCode.Unauthenticated)
                {
                    RunnerLog.Error(nameof(GpuPowerCommandExecutor), ex, $"Power acknowledgement rejected for {command.CommandId}");
                    return;
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    RunnerLog.Error(nameof(GpuPowerCommandExecutor), ex, $"Retrying original power acknowledgement for {command.CommandId}");
                }
                await Task.Delay(delay, shutdown).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(10_000, delay.TotalMilliseconds * 2));
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            RunnerLog.Error(nameof(GpuPowerCommandExecutor), ex, $"Power execution permission unconfirmed for {command.CommandId}");
        }
        finally
        {
            stopping.Dispose();
            try { await cancellation.CompleteAsync().ConfigureAwait(false); }
            finally { RetireDelivery(command.CommandId); }
        }
    }

    private static async Task<bool> GetExecutionPermissionAsync(GrantGpuPowerExecutionRequest original,
        Func<GrantGpuPowerExecutionRequest, CancellationToken, Task<GrantGpuPowerExecutionResponse>> grant,
        CancellationToken shutdown)
    {
        var delay = TimeSpan.FromMilliseconds(250);
        while (!shutdown.IsCancellationRequested)
        {
            try { return (await grant(original.Clone(), shutdown).ConfigureAwait(false)).MayExecute; }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.InvalidArgument or StatusCode.FailedPrecondition
                or StatusCode.NotFound or StatusCode.PermissionDenied or StatusCode.Unauthenticated)
            {
                RunnerLog.Error(nameof(GpuPowerCommandExecutor), ex, $"Power execution permission rejected for {original.CommandId}");
                return false;
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return false; }
            catch (Exception ex)
            {
                RunnerLog.Error(nameof(GpuPowerCommandExecutor), ex, $"Retrying power execution permission for {original.CommandId}");
            }
            await Task.Delay(delay, shutdown).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(10_000, delay.TotalMilliseconds * 2));
        }
        return false;
    }
}
