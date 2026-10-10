namespace Offloadr.EditorRuntime.V1;

public enum ComfyRuntimeRouteOwnership
{
    CpuEditor,
    DurableGeneration,
    TransientControl,
    TransientRead,
    RuntimeObservability
}

public static class ComfyRuntimeRoutes
{
    public static ComfyRuntimeRouteOwnership Classify(string? method, string? rawPath)
    {
        var path = NormalizePath(rawPath);
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            if (IsTraditionalRoute(path, "prompt"))
            {
                return ComfyRuntimeRouteOwnership.DurableGeneration;
            }

            if (IsTraditionalRoute(path, "interrupt") ||
                IsTraditionalRoute(path, "queue") ||
                IsTraditionalRoute(path, "free") ||
                IsTraditionalRoute(path, "history") ||
                IsJobCancelRoute(path))
            {
                return ComfyRuntimeRouteOwnership.TransientControl;
            }
        }

        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
            (IsTraditionalRoute(path, "prompt") ||
             IsTraditionalRoute(path, "queue") ||
             IsTraditionalRoute(path, "history") ||
             IsTraditionalResourceRoute(path, "history") ||
             IsJobReadRoute(path)))
        {
            return ComfyRuntimeRouteOwnership.TransientRead;
        }

        if ((string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && IsRuntimeLogReadRoute(path)) ||
            (string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase) && IsRuntimeLogSubscriptionRoute(path)))
        {
            return ComfyRuntimeRouteOwnership.RuntimeObservability;
        }

        return ComfyRuntimeRouteOwnership.CpuEditor;
    }

    public static bool IsUrgentControl(string? rawPath)
    {
        var path = NormalizePath(rawPath);
        return IsTraditionalRoute(path, "interrupt") || IsJobCancelRoute(path);
    }

    public static bool IsGlobalInterrupt(string? rawPath, ReadOnlySpan<byte> body)
        => IsTraditionalRoute(NormalizePath(rawPath), "interrupt") &&
           (body.IsEmpty || !ContainsPromptId(body));

    public static string NormalizePath(string? rawPath)
    {
        var path = (rawPath ?? string.Empty).Trim().Trim('/');
        var suffix = path.IndexOfAny(['?', '#']);
        return suffix < 0 ? path : path[..suffix].TrimEnd('/');
    }

    private static bool IsTraditionalRoute(string path, string route)
        => path.Equals(route, StringComparison.OrdinalIgnoreCase) ||
           path.Equals($"api/{route}", StringComparison.OrdinalIgnoreCase);

    private static bool IsTraditionalResourceRoute(string path, string route)
    {
        var normalized = path.StartsWith("api/", StringComparison.OrdinalIgnoreCase) ? path[4..] : path;
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 2 &&
               segments[0].Equals(route, StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(segments[1]);
    }

    private static bool IsJobCancelRoute(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return (segments.Length == 3 &&
                segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
                segments[1].Equals("jobs", StringComparison.OrdinalIgnoreCase) &&
                segments[2].Equals("cancel", StringComparison.OrdinalIgnoreCase)) ||
               (segments.Length == 4 &&
                segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
                segments[1].Equals("jobs", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(segments[2]) &&
                segments[3].Equals("cancel", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsJobReadRoute(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return (segments.Length == 2 &&
                segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
                segments[1].Equals("jobs", StringComparison.OrdinalIgnoreCase)) ||
               (segments.Length == 3 &&
                segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
                segments[1].Equals("jobs", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(segments[2]));
    }

    private static bool IsRuntimeLogReadRoute(string path)
        => path.Equals("internal/logs", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("internal/logs/raw", StringComparison.OrdinalIgnoreCase);

    private static bool IsRuntimeLogSubscriptionRoute(string path)
        => path.Equals("internal/logs/subscribe", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsPromptId(ReadOnlySpan<byte> body)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body.ToArray());
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("prompt_id", out var promptId) &&
                   promptId.ValueKind == System.Text.Json.JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(promptId.GetString());
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
