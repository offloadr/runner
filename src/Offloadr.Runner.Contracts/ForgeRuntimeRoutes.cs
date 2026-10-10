namespace Offloadr.EditorRuntime.V1;

/// <summary>Native Forge route matching shared by submission correlation and transport.</summary>
public static class ForgeRuntimeRoutes
{
    public static bool Matches(string? path, string route)
    {
        var normalized = (path ?? string.Empty).Trim().Trim('/');
        return normalized.Equals(route, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(route + "/", StringComparison.OrdinalIgnoreCase);
    }
}
