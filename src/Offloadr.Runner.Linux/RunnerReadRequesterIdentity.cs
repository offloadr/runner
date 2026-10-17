using Offloadr.Runner.V1;

namespace Offloadr.Runner.Linux;

/// <summary>
/// The identity a read request names: this runner process and the session runtime it reads for.
/// The control plane authorizes the read against exactly this identity, and takes the session id
/// for a sealed response's envelope identity.
/// </summary>
internal sealed class RunnerReadRequesterIdentity(string? runnerId, string? instanceId, RuntimeIdentityRegistry? runtimeIdentities)
{
    private readonly string _runnerId = runnerId?.Trim() ?? string.Empty;
    private readonly string _instanceId = instanceId?.Trim() ?? string.Empty;

    /// <summary>
    /// The requester for reads of <paramref name="sessionId"/>, with the runtime it has now.
    /// Taken once, when the session's uploader or mirror is created for that runtime, and kept:
    /// a newer runtime registered later must never lend its identity to an older one's reads.
    /// </summary>
    public RunnerReadRequester For(string sessionId)
    {
        var requester = new RunnerReadRequester
        {
            RunnerId = _runnerId,
            InstanceId = _instanceId,
            SessionId = sessionId,
        };
        if (runtimeIdentities?.TryGet(sessionId, out var runtime) == true && runtime.IsValid)
        {
            requester.LifecycleGeneration = runtime.LifecycleGeneration;
            requester.RuntimeEpoch = runtime.RuntimeEpoch;
            requester.RuntimeInstanceId = runtime.RuntimeInstanceId;
        }

        return requester;
    }
}
