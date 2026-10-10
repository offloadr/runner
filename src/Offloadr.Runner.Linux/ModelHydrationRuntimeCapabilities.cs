namespace Offloadr.Runner.Linux;

public sealed record ModelHydrationRuntimeCapabilities(
    string RuntimeKind,
    bool SupportsPersistentRangeHydration,
    IReadOnlyList<string> PersistentModelRoots)
{
    public static ModelHydrationRuntimeCapabilities FromEnvironment(
        string? runtimeKind,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string, bool>? fileExists = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        fileExists ??= File.Exists;
        var normalizedRuntime = string.IsNullOrWhiteSpace(runtimeKind)
            ? "unknown"
            : runtimeKind.Trim().ToLowerInvariant();
        var supportsPersistentRangeHydration = ParseEnabled(
            getEnvironmentVariable("RUNNER_PERSISTENT_RANGE_HYDRATION"));
        var configuredRoots = getEnvironmentVariable("RUNNER_VFS_ROOTS");
        if (!supportsPersistentRangeHydration || string.IsNullOrWhiteSpace(configuredRoots))
        {
            return new ModelHydrationRuntimeCapabilities(normalizedRuntime, false, []);
        }

        var vfsLibrary = getEnvironmentVariable("RUNNER_VFS_LIB")
            ?? new RunnerVfsEnvironmentBuilder().DefaultVfsLibraryPath;
        if (string.IsNullOrWhiteSpace(vfsLibrary) || !fileExists(vfsLibrary))
        {
            throw new InvalidOperationException(
                $"Persistent range hydration requires the model VFS library at '{vfsLibrary}'.");
        }

        var roots = configuredRoots
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new ModelHydrationRuntimeCapabilities(normalizedRuntime, true, roots);
    }

    public bool OwnsPersistentModelPath(string? path)
    {
        if (!SupportsPersistentRangeHydration || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        return PersistentModelRoots.Any(root => IsWithinRoot(normalized, root));
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(path, normalizedRoot, StringComparison.Ordinal)
            || path.StartsWith(
                string.Concat(normalizedRoot, Path.DirectorySeparatorChar),
                StringComparison.Ordinal);
    }

    private static bool ParseEnabled(string? value)
        => string.Equals(value?.Trim(), "1", StringComparison.Ordinal)
           || bool.TryParse(value, out var enabled) && enabled;
}
