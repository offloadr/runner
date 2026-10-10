using System.Collections.Concurrent;
using Grpc.Core;
using Offloadr.Common.V1;
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

    private sealed record Delivery(SetGpuPowerLimitCommand Command, Lazy<Task> Work);
    private readonly ConcurrentDictionary<string, Delivery> _deliveries = new(StringComparer.Ordinal);
    private readonly Queue<string> _completedDeliveries = new();
    private readonly SemaphoreSlim _physicalExecution = new(1, 1);
    private readonly object _admission = new();
    private readonly CancellationTokenSource _stopping = new();
    private bool _closed;

    public async Task ActivateSessionAsync(Action activate, CancellationToken shutdown)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown, _stopping.Token);
        shutdown = cancellation.Token;
        await _physicalExecution.WaitAsync(shutdown).ConfigureAwait(false);
        try { shutdown.ThrowIfCancellationRequested(); activate(); }
        finally { _physicalExecution.Release(); }
    }

    public Task HandleAsync(SetGpuPowerLimitCommand command, string runnerId,
        Func<string> activeSession, Func<ulong> activeGeneration,
        Func<GrantGpuPowerExecutionRequest, CancellationToken, Task<GrantGpuPowerExecutionResponse>> grant,
        Func<AcknowledgeGpuPowerLimitRequest, CancellationToken, Task> acknowledge,
        CancellationToken shutdown)
    {
        lock (_admission)
        {
            if (_closed) return Task.CompletedTask;
            var captured = command.Clone();
            var delivery = _deliveries.GetOrAdd(captured.CommandId, _ => new(captured,
                new Lazy<Task>(() => Task.Run(async () =>
                {
                    try { await ExecuteAsync(captured, runnerId, activeSession, activeGeneration, grant, acknowledge, shutdown).ConfigureAwait(false); }
                    finally { RetireDelivery(captured.CommandId); }
                }))));
            if (!delivery.Command.Equals(captured))
            {
                RunnerLog.Error(nameof(GpuPowerCommandExecutor), $"Conflicting delivery for power command {captured.CommandId}");
                return Task.CompletedTask;
            }
            return delivery.Work.Value;
        }
    }

    internal int RetainedDeliveryCount => _deliveries.Count;

    private void RetireDelivery(string commandId)
    {
        lock (_admission)
        {
            _completedDeliveries.Enqueue(commandId);
            while (_completedDeliveries.Count > MaxRetainedCompletedDeliveries)
            {
                _deliveries.TryRemove(_completedDeliveries.Dequeue(), out _);
            }
        }
    }

    public async Task CancelAndDrainAsync()
    {
        Task[] work;
        lock (_admission)
        {
            _closed = true;
            work = _deliveries.Values.Select(delivery => delivery.Work.Value).ToArray();
        }
        try { _stopping.Cancel(); }
        finally
        {
            // Shutdown cancellation initiates native termination; it must not
            // cancel the join before child-process cleanup actually completes.
            await Task.WhenAll(work).ConfigureAwait(false);
        }
    }

    private async Task ExecuteAsync(SetGpuPowerLimitCommand command, string runnerId,
        Func<string> activeSession, Func<ulong> activeGeneration,
        Func<GrantGpuPowerExecutionRequest, CancellationToken, Task<GrantGpuPowerExecutionResponse>> grant,
        Func<AcknowledgeGpuPowerLimitRequest, CancellationToken, Task> acknowledge,
        CancellationToken shutdown)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown, _stopping.Token);
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
