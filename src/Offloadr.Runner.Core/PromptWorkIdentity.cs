using Offloadr.Runner.V1;

namespace Offloadr.Runner.Core;

/// <summary>Cancellation and model demand retain identity, never prompt content.</summary>
internal readonly record struct PromptWorkIdentity(
    string HandoffId,
    string SessionId,
    RuntimeIdentity Runtime,
    bool StreamsHttpResponse)
{
    internal static PromptWorkIdentity Capture(SubmitPromptCommand command, bool streamsHttpResponse)
        => new(command.HandoffId, command.SessionId, PromptHandoff.Runtime(command), streamsHttpResponse);
}

/// <summary>Plain wire checks for a prompt handoff; they never authorize execution.</summary>
internal static class PromptHandoff
{
    /// <summary>The runtime the handoff was accepted for.</summary>
    internal static RuntimeIdentity Runtime(SubmitPromptCommand command)
        => RuntimeIdentity.From(command.Runtime);

    /// <summary>
    /// True when the handoff, session and runtime id are nonempty UUIDs and the runtime
    /// order is a positive database value.
    /// </summary>
    internal static bool HasValidIdentity(SubmitPromptCommand command)
        => IsUuid(command.HandoffId) && IsUuid(command.SessionId)
           && command.Runtime is { RuntimeOrder: > 0 and <= long.MaxValue } runtime && IsUuid(runtime.RuntimeId);

    private static bool IsUuid(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty;
}
