using Offloadr.Runner.V1;

namespace Offloadr.Runner.Core;

internal sealed class GrpcRuntimeActivitySink(
    RunnerControlService.RunnerControlServiceClient client,
    string runnerId,
    string instanceId) : IRuntimeActivitySink
{
    private readonly RunnerControlService.RunnerControlServiceClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly string _runnerId = Guid.TryParse(runnerId, out var id) && id != Guid.Empty
        ? id.ToString("n") : throw new ArgumentException("A runner identity is required.", nameof(runnerId));
    private readonly string _instanceId = !string.IsNullOrWhiteSpace(instanceId)
        ? instanceId.Trim() : throw new ArgumentException("A process identity is required.", nameof(instanceId));

    public async Task<RuntimeActivityOutcome> ReportAsync(RuntimeActivityObservation observation, CancellationToken cancellationToken)
    {
        var request = new ReportRuntimeActivityRequest
        {
            RunnerId = _runnerId,
            SessionId = observation.SessionId,
            InstanceId = _instanceId,
            LifecycleGeneration = observation.Runtime.LifecycleGeneration,
            RuntimeEpoch = observation.Runtime.RuntimeEpoch,
            RuntimeInstanceId = observation.Runtime.RuntimeInstanceId,
            Sealing = new()
            {
                MemoryWorkspace = observation.MemoryWorkspace,
                EncryptedUploads = observation.EncryptedUploads,
                EncryptedReadsRequired = observation.EncryptedReadsRequired
            }
        };
        using var call = _client.ReportRuntimeActivityAsync(request,
            deadline: DateTime.UtcNow.Add(RuntimeActivityScope.AttemptTimeout), cancellationToken: cancellationToken);
        var response = await call.ResponseAsync.ConfigureAwait(false);
        if (response.Outcome is RuntimeActivityOutcome.Applied or RuntimeActivityOutcome.AlreadyAppliedExactly)
        {
            if (response.RuntimeStartedUtc is null)
                throw new InvalidDataException("The runtime activity acknowledgement has no accepted time.");
            // Reject malformed protocol timestamps without logging their contents.
            _ = response.RuntimeStartedUtc.ToDateTimeOffset();
        }
        return response.Outcome;
    }
}
