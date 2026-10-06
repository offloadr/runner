namespace Offloadr.EditorRuntime.V1;

public static class ComfyRuntimeTransportLimits
{
    public const int MaxResponseBodyBytes = 8 * 1024 * 1024;

    // Leave room for protobuf field framing around the maximum response body.
    public const int MaxGrpcResponseMessageBytes = MaxResponseBodyBytes + (64 * 1024);
}
