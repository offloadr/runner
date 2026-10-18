using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

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

    /// <summary>
    /// True when the shim asks the agent before opening <paramref name="path"/> under an artifact
    /// root, so an empty placeholder there is downloaded again on open.
    /// </summary>
    public static bool IsArtifactReadbackPath(string path)
        => AdditionalExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public string DefaultVfsLibraryPath { get; init; } = "/opt/offloadr/lib/liboffloadr_model_vfs.so";
    public string DefaultSocketPath { get; init; } = "/tmp/runner-agent/model-fetch.sock";
    public string DefaultRoots { get; init; } = string.Empty;
    public string DefaultExtensions { get; init; } = ".safetensors,.pt,.pth,.bin,.ckpt,.onnx,.emb,.gguf";
    public string DefaultLogValue { get; init; } = "0";

    /// <summary>
    /// Proves that <c>LD_PRELOAD</c> of the given library makes a process non-dumpable. The loader
    /// skips an unloadable preload (corrupt, wrong architecture) with only a warning, so the file
    /// existing is not enough. Replaceable for tests whose shim is a placeholder file.
    /// </summary>
    public Func<string, bool> CoreDumpHardeningProbe { get; init; } = ProbeCoreDumpHardening;

    private readonly ConcurrentDictionary<string, bool> _coreDumpHardeningProbes = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the shim that makes session processes non-dumpable is installed and works; probed
    /// once per library path.
    /// </summary>
    public bool CanDisableCoreDumps(Func<string, string?>? getEnv = null, Func<string, bool>? fileExists = null)
    {
        var vfsLib = (getEnv ?? Environment.GetEnvironmentVariable)("RUNNER_VFS_LIB") ?? DefaultVfsLibraryPath;
        return (fileExists ?? File.Exists)(vfsLib) && _coreDumpHardeningProbes.GetOrAdd(vfsLib, CoreDumpHardeningProbe);
    }

    internal static bool ProbeCoreDumpHardening(string vfsLib)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var startInfo = new ProcessStartInfo("/bin/true")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["LD_PRELOAD"] = vfsLib;
        startInfo.Environment["VFS_ROOTS"] = string.Empty;
        startInfo.Environment["VFS_LOG"] = "0";
        startInfo.Environment["VFS_NO_DUMP"] = "1";
        startInfo.Environment["VFS_NO_DUMP_REPORT"] = "1";
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var stderr = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.Kill();
                return false;
            }

            return process.ExitCode == 0 &&
                stderr.Wait(TimeSpan.FromSeconds(5)) &&
                stderr.Result.Split('\n').Contains("offloadr-vfs-no-dump=0", StringComparer.Ordinal) &&
                stderr.Result.Split('\n').Contains("offloadr-vfs-core-locked=1", StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Loads the shim into a process that reads no models or artifacts, only so it becomes
    /// non-dumpable: no roots are managed. Empty when the library is missing.
    /// </summary>
    public IReadOnlyDictionary<string, string> BuildCoreDumpHardening(
        Func<string, string?>? getEnv = null,
        Func<string, bool>? fileExists = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        fileExists ??= File.Exists;
        var vfsLib = getEnv("RUNNER_VFS_LIB") ?? DefaultVfsLibraryPath;
        if (!fileExists(vfsLib))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var inheritedPreload = getEnv("LD_PRELOAD");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LD_PRELOAD"] = string.IsNullOrWhiteSpace(inheritedPreload) ? vfsLib : string.Concat(vfsLib, ":", inheritedPreload),
            ["VFS_ROOTS"] = string.Empty,
            ["VFS_LOG"] = "0",
            ["VFS_NO_DUMP"] = "1",
        };
    }

    public IReadOnlyDictionary<string, string> Build(
        RunnerVfsSessionPaths sessionPaths,
        Func<string, string?>? getEnv = null,
        Func<string, bool>? fileExists = null,
        string? existingLdPreload = null,
        string? sessionId = null,
        bool sealedSession = false)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        fileExists ??= File.Exists;

        var persistentRoots = getEnv("RUNNER_VFS_ROOTS") ?? DefaultRoots;
        var updates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VFS_SOCKET"] = getEnv("RUNNER_MODEL_FETCH_SOCKET") ?? DefaultSocketPath,
            ["VFS_ROOTS"] = BuildRootsValue(persistentRoots, sessionPaths),
            ["VFS_EXTS"] = BuildExtensionsValue(getEnv("RUNNER_VFS_EXTS") ?? DefaultExtensions),
            // The shim's trace prints session paths to the console: never for a sealed session,
            // whatever the runner's mode.
            ["VFS_LOG"] = sealedSession ? "0" : getEnv("RUNNER_VFS_LOG") ?? DefaultLogValue,
            ["VFS_SESSION_ID"] = sessionId?.Trim() ?? string.Empty
        };
        if (sealedSession)
        {
            // The shim marks every process that loads it non-dumpable again after each exec.
            updates["VFS_NO_DUMP"] = "1";
        }

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
