using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Offloadr.Common.V1;

/// <summary>Pure native-protocol correlation; never authorization or runtime deduplication.</summary>
public static class PromptRuntimeMarker
{
    public const string ExtraDataKey = "offloadr_submission";

    public static string TargetFingerprint(PromptExecutionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        string GuidText(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id.ToString("n") : throw new ArgumentException("A nonempty runtime/executor UUID is required.", nameof(target));
        string Positive(ulong value) => value is > 0 and <= long.MaxValue
            ? value.ToString(CultureInfo.InvariantCulture) : throw new ArgumentException("A positive database generation is required.", nameof(target));
        string Required(string value) => !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException("A lifecycle/allocation identity is required.", nameof(target));
        string[] fields = ["offloadr.prompt-target.v1", Required(target.EditorLifecycleId), Positive(target.EditorGeneration),
            Positive(target.CpuRuntimeGeneration), GuidText(target.CpuRuntimeInstanceId), Required(target.GpuLifecycleId),
            Positive(target.GpuGeneration), Required(target.AllocationId), Positive(target.RuntimeEpoch), GuidText(target.RuntimeInstanceId),
            GuidText(target.RunnerId), GuidText(target.RunnerSessionId)];
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
            foreach (var field in fields) writer.Write(field);
        return Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
    }

    public static string AttachToComfyRequest(string normalizedBody, string submissionId, PromptExecutionTarget target)
    {
        var id = NormalizeSubmissionId(submissionId);
        var root = JsonNode.Parse(normalizedBody) as JsonObject
            ?? throw new ArgumentException("A ComfyUI generation body must be a JSON object.", nameof(normalizedBody));
        if (root["extra_data"] is not null && root["extra_data"] is not JsonObject)
            throw new ArgumentException("ComfyUI extra_data must be an object.", nameof(normalizedBody));
        var extra = root["extra_data"] as JsonObject ?? new JsonObject();
        if (root["extra_data"] is null) root["extra_data"] = extra;
        extra[ExtraDataKey] = new JsonObject { ["submission_id"] = id, ["target_sha256"] = TargetFingerprint(target) };
        if (root["prompt_id"] is null) root["prompt_id"] = Guid.Parse(id).ToString("D");
        else if (root["prompt_id"] is not JsonValue value || !value.TryGetValue<string>(out var promptId)
            || !Guid.TryParse(promptId, out var parsed) || parsed == Guid.Empty || promptId != parsed.ToString("D"))
            throw new ArgumentException("ComfyUI prompt_id must be a canonical lowercase hyphenated UUID.", nameof(normalizedBody));
        return root.ToJsonString();
    }

    public static bool Matches(JsonElement extraData, string submissionId, PromptExecutionTarget target)
        => extraData.ValueKind == JsonValueKind.Object
            && extraData.TryGetProperty(ExtraDataKey, out var marker) && marker.ValueKind == JsonValueKind.Object
            && marker.TryGetProperty("submission_id", out var id) && id.ValueKind == JsonValueKind.String
            && id.GetString() == NormalizeSubmissionId(submissionId)
            && marker.TryGetProperty("target_sha256", out var fingerprint) && fingerprint.ValueKind == JsonValueKind.String
            && fingerprint.GetString() == TargetFingerprint(target);

    private static string NormalizeSubmissionId(string value)
        => Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id.ToString("n") : throw new ArgumentException("A nonempty submission UUID is required.", nameof(value));
}
