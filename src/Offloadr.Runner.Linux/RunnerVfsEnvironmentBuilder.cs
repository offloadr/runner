namespace Offloadr.Runner.Linux;

public readonly record struct RunnerVfsSessionPaths(
    string ModelsDirectory,
    string OutputDirectory,
    string InputDirectory,
    string? TempDirectory = null);

public sealed class RunnerVfsEnvironmentBuilder
{
    private static readonly string[] AdditionalExtensions =
    [
        ".safetensors",
        ".pt",
        ".pth",
        ".bin",
        ".ckpt",
        ".onnx",
        ".emb",
        ".gguf",
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".webp",
        ".bmp",
        ".tif",
        ".tiff",
        ".mp4",
        ".mov",
        ".mkv",
        ".wav",
        ".mp3",
        ".flac",
        ".ogg",
        ".m4a",
        ".aac",
        ".json",
        ".txt",
        ".svg",
        ".yaml",
        ".yml",
        ".toml",
        ".ini",
        ".csv",
        ".md",
        ".zip",
        ".tar"
    ];

    public string DefaultVfsLibraryPath { get; init; } = "/opt/offloadr/lib/liboffloadr_model_vfs.so";
    public string DefaultSocketPath { get; init; } = "/tmp/runner-agent/model-fetch.sock";
    public string DefaultRoots { get; init; } = string.Empty;
    public string DefaultExtensions { get; init; } = ".safetensors,.pt,.pth,.bin,.ckpt,.onnx,.emb,.gguf";
    public string DefaultLogValue { get; init; } = "0";

    public IReadOnlyDictionary<string, string> Build(
        RunnerVfsSessionPaths sessionPaths,
        Func<string, string?>? getEnv = null,
        Func<string, bool>? fileExists = null,
        string? existingLdPreload = null,
        string? sessionId = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        fileExists ??= File.Exists;

        var persistentRoots = getEnv("RUNNER_VFS_ROOTS") ?? DefaultRoots;
        var updates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VFS_SOCKET"] = getEnv("RUNNER_MODEL_FETCH_SOCKET") ?? DefaultSocketPath,
            ["VFS_ROOTS"] = BuildRootsValue(persistentRoots, sessionPaths),
            ["VFS_EXTS"] = BuildExtensionsValue(getEnv("RUNNER_VFS_EXTS") ?? DefaultExtensions),
            ["VFS_LOG"] = getEnv("RUNNER_VFS_LOG") ?? DefaultLogValue,
            ["VFS_SESSION_ID"] = sessionId?.Trim() ?? string.Empty
        };

        var vfsLib = getEnv("RUNNER_VFS_LIB") ?? DefaultVfsLibraryPath;
        if (fileExists(vfsLib))
        {
            var inheritedPreload = existingLdPreload;
            if (string.IsNullOrWhiteSpace(inheritedPreload))
            {
                inheritedPreload = getEnv("LD_PRELOAD");
            }

            updates["LD_PRELOAD"] = string.IsNullOrWhiteSpace(inheritedPreload)
                ? vfsLib
                : string.Concat(vfsLib, ":", inheritedPreload);
        }
        else if (!string.IsNullOrWhiteSpace(persistentRoots) &&
                 ParseEnabled(getEnv("RUNNER_PERSISTENT_RANGE_HYDRATION")))
        {
            throw new InvalidOperationException(
                $"Persistent range hydration requires the model VFS library at '{vfsLib}'.");
        }

        return updates;
    }

    private static string BuildRootsValue(string rootsRaw, RunnerVfsSessionPaths sessionPaths)
    {
        var roots = rootsRaw
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();

        AddDistinct(roots, sessionPaths.ModelsDirectory);
        AddDistinct(roots, sessionPaths.OutputDirectory);
        AddDistinct(roots, sessionPaths.InputDirectory);
        AddDistinct(roots, sessionPaths.TempDirectory);

        return string.Join(':', roots);
    }

    private static string BuildExtensionsValue(string extensionsRaw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        foreach (var token in extensionsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            AddExtension(ordered, seen, token.Trim());
        }

        foreach (var extension in AdditionalExtensions)
        {
            AddExtension(ordered, seen, extension);
        }

        return string.Join(',', ordered);
    }

    private static void AddDistinct(List<string> roots, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        var normalized = candidate.Trim();
        if (normalized.Length == 0)
        {
            return;
        }

        if (!roots.Any(existing => string.Equals(existing, normalized, StringComparison.Ordinal)))
        {
            roots.Add(normalized);
        }
    }

    private static void AddExtension(List<string> ordered, HashSet<string> seen, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return;
        }

        var normalized = extension.Trim();
        if (normalized.Length == 0)
        {
            return;
        }

        if (seen.Add(normalized))
        {
            ordered.Add(normalized);
        }
    }

    private static bool ParseEnabled(string? value)
        => string.Equals(value?.Trim(), "1", StringComparison.Ordinal)
           || bool.TryParse(value, out var enabled) && enabled;
}
