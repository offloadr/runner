namespace Offloadr.Common.V1;

public static class PromptTransportLimits
{
    // The browser ingress permits a native JSON body up to Kestrel's 30 MB
    // default. Acceptance retains both its immutable original and normalized
    // request, so the receiving RPC must accommodate both copies plus metadata.
    public const int MaxAcceptanceMessageBytes = 80 * 1024 * 1024;

    // FindForgeEditorActions carries two bounded receipts, each with at most an
    // 8 MiB native response, one 8 MiB terminal payload and one 8 MiB terminal
    // progress payload, plus protobuf framing.
    public const int MaxRecoveryMessageBytes = 56 * 1024 * 1024;

    // The runner command stream receives at most this many bytes per message.
    public const int MaxRunnerMessageBytes = 32 * 1024 * 1024;

    // A dispatched command must fit that stream with room for its envelope;
    // otherwise it could never be delivered and would hold the runner lane.
    public const int MaxRunnerCommandBytes = MaxRunnerMessageBytes - 1024 * 1024;
}
