using System.Text.Json;
using Offloadr.EditorRuntime.V1;

namespace Offloadr.Runner.Linux;

/// <summary>
/// Prompt ids in ComfyUI's queue (<c>GET /api/queue</c>). A prompt stays in it
/// from submission until it finishes, fails, is interrupted or is removed, so an
/// accepted prompt missing from it no longer needs its models.
/// </summary>
internal sealed record ComfyPromptQueue(IReadOnlySet<string> Running, IReadOnlySet<string> Pending)
{
    public bool Contains(string promptId) => Running.Contains(promptId) || Pending.Contains(promptId);

    /// <summary>Returns null unless both queue lists are present.</summary>
    public static ComfyPromptQueue? TryParse(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var running = ReadPromptIds(document.RootElement, "queue_running");
            var pending = ReadPromptIds(document.RootElement, "queue_pending");
            return running is null || pending is null ? null : new(running, pending);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Each entry is [number, prompt_id, prompt, extra_data, outputs_to_execute].
    private static HashSet<string>? ReadPromptIds(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var promptIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() > 1 &&
                entry[1].ValueKind == JsonValueKind.String && entry[1].GetString() is { Length: > 0 } promptId)
            {
                promptIds.Add(promptId);
            }
        }

        return promptIds;
    }
}

/// <summary>
/// Prompt ids a ComfyUI cancel control names: <c>POST /interrupt</c> with a
/// <c>prompt_id</c>, <c>POST /api/jobs/{id}/cancel</c> and <c>POST /api/jobs/cancel</c>.
/// A global interrupt names none.
/// </summary>
internal static class ComfyCancelTargets
{
    public static IReadOnlyList<string> PromptIds(string? rawPath, ReadOnlySpan<byte> body)
    {
        var path = ComfyRuntimeRoutes.NormalizePath(rawPath);
        if (path.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
        {
            path = path[4..];
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 3 &&
            segments[0].Equals("jobs", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            return [segments[1]];
        }

        var namesPrompts = segments.Length == 1 && segments[0].Equals("interrupt", StringComparison.OrdinalIgnoreCase) ||
                           segments.Length == 2 &&
                           segments[0].Equals("jobs", StringComparison.OrdinalIgnoreCase) &&
                           segments[1].Equals("cancel", StringComparison.OrdinalIgnoreCase);
        return namesPrompts ? ReadBodyPromptIds(body) : [];
    }

    private static List<string> ReadBodyPromptIds(ReadOnlySpan<byte> body)
    {
        List<string> promptIds = [];
        if (body.IsEmpty)
        {
            return promptIds;
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return promptIds;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name is "prompt_id" or "job_id" && property.Value.ValueKind == JsonValueKind.String)
                {
                    AddPromptId(promptIds, property.Value);
                }
                else if (property.Name is "prompt_ids" or "job_ids" && property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        AddPromptId(promptIds, item);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return promptIds;
    }

    private static void AddPromptId(List<string> promptIds, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } promptId)
        {
            promptIds.Add(promptId);
        }
    }
}
