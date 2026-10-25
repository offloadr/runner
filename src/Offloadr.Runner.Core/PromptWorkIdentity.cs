using Offloadr.Runner.V1;

namespace Offloadr.Runner.Core;

/// <summary>Cancellation and model demand retain identity, never prompt content.</summary>
internal readonly record struct PromptWorkIdentity(
    string CommandId,
    string SessionId,
    string SubmissionId,
    string NativePromptId,
    RuntimeIdentity? Runtime,
    bool StreamsHttpResponse)
{
    internal static PromptWorkIdentity Capture(SubmitPromptCommand command, bool streamsHttpResponse)
        => new(command.CommandId, command.SessionId, command.SubmissionId,
            command.NativeIdentifiers?.PromptId ?? string.Empty,
            command.Target is { } target
                ? new(target.GpuGeneration, target.RuntimeEpoch, target.RuntimeInstanceId) : null,
            streamsHttpResponse);
}
