using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Offloadr.Runner.Linux;

public sealed class RunnerStartupValidator
{
    private readonly ISessionIsolationStrategy _sessionIsolationStrategy;
    private readonly string _passwdPath;

    public RunnerStartupValidator(ISessionIsolationStrategy sessionIsolationStrategy, string passwdPath = "/etc/passwd")
    {
        _sessionIsolationStrategy = sessionIsolationStrategy ?? throw new ArgumentNullException(nameof(sessionIsolationStrategy));
        _passwdPath = passwdPath;
    }

    public void ValidateOrThrow(RunnerAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            errors.Add("RunnerAgent currently supports Linux only.");
        }

        if (string.IsNullOrWhiteSpace(options.RunnerSecret))
        {
            errors.Add("RUNNER_SECRET is required.");
        }
        else if (options.RunnerSecret.Trim().Length < 43)
        {
            errors.Add("RUNNER_SECRET appears too short. Use at least 32 random bytes (for example base64-encoded).");
        }

        if (!Guid.TryParse(options.RunnerId, out _))
        {
            errors.Add("RUNNER_ID must be a UUID when provided.");
        }

        ValidateHttpsUrl(options.OffloadrApiUrl, "offloadr-api", errors);

        ValidateDirectoryWritable(options.Session.SessionRoot, "SESSION_ROOT", errors);
        ValidateDirectoryExists(options.Session.WorkingDirectory, "COMFY_WORKDIR", errors);
        ValidateFileExists(options.Session.EntryPointPath, "COMFY_ENTRYPOINT", errors);
        if (ValidateDirectoryExists(options.Session.SeedVirtualEnvPath, "RUNNER_SEED_VENV_PATH", errors))
        {
            ValidateFileExists(Path.Combine(options.Session.SeedVirtualEnvPath, "bin", "python"), "RUNNER_SEED_VENV_PATH/bin/python", errors);
            if (OperatingSystem.IsLinux())
            {
                ValidateSeedVirtualEnvPermissions(options.Session.SeedVirtualEnvPath, errors);
            }
        }
        if (!string.IsNullOrWhiteSpace(options.LocalModelsDirectory))
        {
            ValidateDirectoryExists(options.LocalModelsDirectory, "RUNNER_LOCAL_MODELS_DIR", errors);
        }

        var socketDirectory = Path.GetDirectoryName(options.ModelFetchSocket);
        if (!string.IsNullOrWhiteSpace(socketDirectory))
        {
            ValidateDirectoryWritable(socketDirectory, "RUNNER_MODEL_FETCH_SOCKET parent", errors);
        }

        ValidateBinary(options.Aria2.BinaryPath, "ARIA2_BINARY", errors);
        if (options.Aria2.RequireUser &&
            LinuxAria2FileAccess.TryReadAccount(options.Aria2.User, _passwdPath) is null)
        {
            errors.Add($"ARIA2_USER account '{options.Aria2.User}' does not exist; ARIA2_REQUIRE_USER forbids running aria2 as root.");
        }

        ValidateBinary(options.Session.UvBinaryPath, "RUNNER_UV_BINARY", errors);
        _sessionIsolationStrategy.ValidatePrerequisites(errors);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Runner startup validation failed:{Environment.NewLine} - {string.Join($"{Environment.NewLine} - ", errors)}");
        }
    }

    private static void ValidateHttpsUrl(string rawValue, string label, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            errors.Add($"Missing {label} URL.");
            return;
        }

        if (!Uri.TryCreate(rawValue, UriKind.Absolute, out var uri))
        {
            errors.Add($"Invalid {label} URL '{rawValue}'.");
            return;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{label} URL must use https scheme.");
        }
    }

    private static void ValidateFileExists(string path, string settingName, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add($"{settingName} is empty.");
            return;
        }

        if (!File.Exists(path))
        {
            errors.Add($"{settingName} file not found at '{path}'.");
        }
    }

    private static bool ValidateDirectoryExists(string path, string settingName, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add($"{settingName} is empty.");
            return false;
        }

        if (!Directory.Exists(path))
        {
            errors.Add($"{settingName} directory not found at '{path}'.");
            return false;
        }

        return true;
    }

    private static void ValidateDirectoryWritable(string path, string settingName, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add($"{settingName} directory is empty.");
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            var probePath = Path.Combine(path, $".runner-write-probe-{Guid.NewGuid():N}");
            using (File.Create(probePath))
            {
            }
            File.Delete(probePath);
        }
        catch (Exception ex)
        {
            errors.Add($"{settingName} directory '{path}' is not writable: {ex.Message}");
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateSeedVirtualEnvPermissions(string seedVirtualEnvPath, ICollection<string> errors)
    {
        ValidateNotGroupOrOtherWritable(seedVirtualEnvPath, "RUNNER_SEED_VENV_PATH", errors);
        var seedBinDirectory = Path.Combine(seedVirtualEnvPath, "bin");
        ValidateNotGroupOrOtherWritable(seedBinDirectory, "RUNNER_SEED_VENV_PATH/bin", errors);
        ValidateSeedBinPermissions(seedBinDirectory, errors);

        foreach (var sitePackages in EnumerateSeedSitePackages(seedVirtualEnvPath))
        {
            ValidateNotGroupOrOtherWritable(sitePackages, "RUNNER_SEED_VENV_PATH site-packages", errors);
            foreach (var payloadEntry in Directory.EnumerateFileSystemEntries(sitePackages))
            {
                var entryName = Path.GetFileName(payloadEntry);
                if (string.IsNullOrWhiteSpace(entryName) || IsPythonMetadataEntry(entryName))
                {
                    continue;
                }

                ValidateTreeNotGroupOrOtherWritable(
                    payloadEntry,
                    $"RUNNER_SEED_VENV_PATH site-packages payload '{entryName}'",
                    errors);
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateSeedBinPermissions(string seedBinDirectory, ICollection<string> errors)
    {
        if (!Directory.Exists(seedBinDirectory))
        {
            return;
        }

        foreach (var binEntry in Directory.EnumerateFileSystemEntries(seedBinDirectory))
        {
            var entryName = Path.GetFileName(binEntry);
            ValidateTreeNotGroupOrOtherWritable(
                binEntry,
                $"RUNNER_SEED_VENV_PATH/bin entry '{entryName}'",
                errors);
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateTreeNotGroupOrOtherWritable(string path, string settingName, ICollection<string> errors)
        => ValidateTreeNotGroupOrOtherWritable(
            path,
            settingName,
            errors,
            new HashSet<string>(StringComparer.Ordinal));

    [SupportedOSPlatform("linux")]
    private static void ValidateTreeNotGroupOrOtherWritable(
        string path,
        string settingName,
        ICollection<string> errors,
        ISet<string> visitedDirectories)
    {
        ValidateNotGroupOrOtherWritable(path, settingName, errors);

        if (!Directory.Exists(path))
        {
            return;
        }

        string traversalRoot;
        try
        {
            traversalRoot = Path.GetFullPath(ResolvePermissionTarget(path));
        }
        catch (Exception ex)
        {
            errors.Add($"Could not resolve permissions under {settingName} at '{path}': {ex.Message}");
            return;
        }

        if (!Directory.Exists(traversalRoot) || !visitedDirectories.Add(traversalRoot))
        {
            return;
        }

        string[] childEntries;
        try
        {
            childEntries = Directory.GetFileSystemEntries(traversalRoot);
        }
        catch (Exception ex)
        {
            errors.Add($"Could not enumerate permissions under {settingName} at '{path}': {ex.Message}");
            return;
        }

        foreach (var childEntry in childEntries)
        {
            ValidateTreeNotGroupOrOtherWritable(childEntry, settingName, errors, visitedDirectories);
        }
    }

    private static IEnumerable<string> EnumerateSeedSitePackages(string seedVirtualEnvPath)
    {
        foreach (var libDirectoryName in new[] { "lib", "lib64" })
        {
            var libDirectory = Path.Combine(seedVirtualEnvPath, libDirectoryName);
            if (!Directory.Exists(libDirectory))
            {
                continue;
            }

            foreach (var pythonDirectory in Directory.EnumerateDirectories(libDirectory, "python*", SearchOption.TopDirectoryOnly))
            {
                var sitePackages = Path.Combine(pythonDirectory, "site-packages");
                if (Directory.Exists(sitePackages))
                {
                    yield return sitePackages;
                }
            }
        }
    }

    private static bool IsPythonMetadataEntry(string entryName)
        => entryName.EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase) ||
            entryName.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase);

    [SupportedOSPlatform("linux")]
    private static void ValidateNotGroupOrOtherWritable(string path, string settingName, ICollection<string> errors)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        try
        {
            var modePath = ResolvePermissionTarget(path);
            var mode = File.GetUnixFileMode(modePath);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                errors.Add($"{settingName} at '{path}' must not be writable by group or other users because session virtualenvs symlink seeded package payloads.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Could not validate permissions for {settingName} at '{path}': {ex.Message}");
        }
    }

    private static string ResolvePermissionTarget(string path)
    {
        if (!IsSymbolicLink(path))
        {
            return path;
        }

        var resolved = Directory.Exists(path)
            ? Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName
            : File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
        return string.IsNullOrWhiteSpace(resolved) ? path : resolved;
    }

    private static bool IsSymbolicLink(string path)
        => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static void ValidateBinary(string binary, string settingName, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(binary))
        {
            errors.Add($"{settingName} is empty.");
            return;
        }

        if (ContainsDirectorySeparator(binary))
        {
            if (!File.Exists(binary))
            {
                errors.Add($"{settingName} binary not found at '{binary}'.");
            }

            return;
        }

        if (!TryResolveInPath(binary))
        {
            errors.Add($"{settingName} binary '{binary}' was not found in PATH.");
        }
    }

    private static bool ContainsDirectorySeparator(string value)
    {
        return value.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || value.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool TryResolveInPath(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(segment, executableName);
            if (File.Exists(candidate))
            {
                return true;
            }
        }

        return false;
    }
}
