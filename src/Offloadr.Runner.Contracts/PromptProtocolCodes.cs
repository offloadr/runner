namespace Offloadr.Common.V1;

/// <summary>Wire codes shared by the API, proxy and runner prompt paths.</summary>
public static class PromptProtocolCodes
{
    // Trailer set only by the API's pre-handler admission gate. It proves the
    // delivery never reached acceptance, unlike an Unavailable from the handler.
    public const string AdmissionTrailer = "offloadr-admission";
    public const string RejectedBeforeHandler = "rejected-before-handler";

    // A completed native response whose body exceeded the retained limit. The
    // status and content type are exact; the body is intentionally absent.
    public const string NativeResponseBodyOmitted = "prompt_native_response_body_omitted";
}
