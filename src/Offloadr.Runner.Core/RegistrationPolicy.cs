using System.Text.Json;

namespace Offloadr.Runner.Core;

public static class RegistrationPolicy
{
    public static TimeSpan ComputeBackoff(int attempt, TimeSpan minBackoff, TimeSpan maxBackoff, double? randomSample = null)
    {
        if (minBackoff <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minBackoff), "Minimum backoff must be positive.");
        }

        if (maxBackoff < minBackoff)
        {
            maxBackoff = minBackoff;
        }

        var normalizedAttempt = Math.Max(0, attempt);
        var expSecs = Math.Min(maxBackoff.TotalSeconds, minBackoff.TotalSeconds * Math.Pow(2, normalizedAttempt));
        var jitter = randomSample ?? Random.Shared.NextDouble();
        if (!double.IsFinite(jitter))
        {
            jitter = 0d;
        }

        jitter = Math.Clamp(jitter, 0d, 1d);
        var delay = TimeSpan.FromSeconds(jitter * expSecs);
        if (delay < minBackoff)
        {
            delay = minBackoff;
        }

        return delay;
    }

    public static string? TryExtractClientId(string? promptJson)
    {
        if (string.IsNullOrWhiteSpace(promptJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(promptJson);
            if (doc.RootElement.TryGetProperty("client_id", out var node) && node.ValueKind == JsonValueKind.String)
            {
                return node.GetString();
            }
        }
        catch (JsonException)
        {
            // Invalid payload should be treated as missing metadata.
        }

        return null;
    }

    public static string? TryExtractPromptId(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("prompt_id", out var node) && node.ValueKind == JsonValueKind.String)
            {
                return node.GetString();
            }
        }
        catch (JsonException)
        {
            // Invalid payload should be treated as missing metadata.
        }

        return null;
    }
}
