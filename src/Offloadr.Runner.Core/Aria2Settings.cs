using System.Globalization;
using System.Text.RegularExpressions;

namespace Offloadr.Runner.Core;

public sealed class Aria2Settings
{
    private const string DefaultBinary = "aria2c";
    private const string DefaultDownloadDir = "/models";
    private const string DefaultStateDir = "/var/lib/aria2";
    private const int DefaultRpcPort = 6801;
    private const int DefaultSaveInterval = 5;
    private const int DefaultAutoSaveInterval = 5;
    internal const int DefaultMaxConnectionPerServer = 16;
    internal const int DefaultSplit = 16;
    internal const string DefaultMinSplitSize = "8M";
    internal const string DefaultFileAllocation = "falloc";
    private static readonly TimeSpan DefaultModelDownloadRetryInitialDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultModelDownloadRetryMaxDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultProbeInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly HashSet<string> AllowedFileAllocationModes = new(StringComparer.Ordinal)
    {
        "none",
        "prealloc",
        "trunc",
        "falloc"
    };
    private static readonly Regex MinSplitSizePattern = new(
        @"^(?<value>\d+)(?<unit>[KM])$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private Aria2Settings(
        string binaryPath,
        string downloadDirectory,
        string stateDirectory,
        string sessionFilePath,
        int rpcPort,
        string rpcSecret,
        TimeSpan readyTimeout,
        TimeSpan probeInterval,
        int saveSessionInterval,
        int autoSaveInterval,
        int maxConnectionPerServer,
        int split,
        string minSplitSize,
        string fileAllocation,
        TimeSpan modelDownloadRetryInitialDelay,
        TimeSpan modelDownloadRetryMaxDelay,
        bool checkIntegrity,
        TimeSpan shutdownTimeout,
        bool disableIpv6)
    {
        BinaryPath = binaryPath;
        DownloadDirectory = downloadDirectory;
        StateDirectory = stateDirectory;
        SessionFilePath = sessionFilePath;
        RpcPort = rpcPort;
        RpcSecret = rpcSecret;
        ReadyTimeout = readyTimeout;
        ProbeInterval = probeInterval;
        SaveSessionInterval = saveSessionInterval;
        AutoSaveInterval = autoSaveInterval;
        MaxConnectionPerServer = maxConnectionPerServer;
        Split = split;
        MinSplitSize = minSplitSize;
        FileAllocation = fileAllocation;
        ModelDownloadRetryInitialDelay = modelDownloadRetryInitialDelay;
        ModelDownloadRetryMaxDelay = modelDownloadRetryMaxDelay;
        CheckIntegrity = checkIntegrity;
        ShutdownTimeout = shutdownTimeout;
        DisableIpv6 = disableIpv6;
    }

    public string BinaryPath { get; }
    public string DownloadDirectory { get; }
    public string StateDirectory { get; }
    public string SessionFilePath { get; }
    public int RpcPort { get; }
    public string RpcSecret { get; }
    public TimeSpan ReadyTimeout { get; }
    public TimeSpan ProbeInterval { get; }
    public int SaveSessionInterval { get; }
    public int AutoSaveInterval { get; }
    public int MaxConnectionPerServer { get; }
    public int Split { get; }
    public string MinSplitSize { get; }
    public string FileAllocation { get; }
    public TimeSpan ModelDownloadRetryInitialDelay { get; }
    public TimeSpan ModelDownloadRetryMaxDelay { get; }
    public bool CheckIntegrity { get; }
    public TimeSpan ShutdownTimeout { get; }
    public bool DisableIpv6 { get; }

    public string RpcEndpoint => $"http://127.0.0.1:{RpcPort}/jsonrpc";

    public static Aria2Settings FromEnvironment(Func<string, string?>? env = null, Func<string>? secretFactory = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        secretFactory ??= () => Guid.NewGuid().ToString("N");

        var binary = GetEnv(env, "ARIA2_BINARY") ?? DefaultBinary;
        var downloadDir = NormalizeDirectory(GetEnv(env, "ARIA2_DOWNLOAD_DIR"), DefaultDownloadDir);
        var stateDir = NormalizeDirectory(GetEnv(env, "ARIA2_STATE_DIR"), DefaultStateDir);
        var sessionFile = GetEnv(env, "ARIA2_SESSION_FILE") ?? CombinePosixPath(stateDir, "session.txt");
        var rpcPort = ParseInt(env, "ARIA2_RPC_PORT", DefaultRpcPort, 1, 65535);
        var secret = BuildSecret(GetEnv(env, "ARIA2_RPC_SECRET"), secretFactory);
        var readyTimeout = ParseTimeSpan(env, "ARIA2_READY_TIMEOUT_SEC", DefaultReadyTimeout, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2));
        var probeInterval = ParseTimeSpan(env, "ARIA2_READY_PROBE_MS", DefaultProbeInterval, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));
        var saveInterval = ParseInt(env, "ARIA2_SAVE_SESSION_INTERVAL", DefaultSaveInterval, 1, 300);
        var autoSaveInterval = ParseInt(env, "ARIA2_AUTO_SAVE_INTERVAL", DefaultAutoSaveInterval, 1, 300);
        var maxConnPerServer = ParseInt(env, "ARIA2_MAX_CONNECTION_PER_SERVER", DefaultMaxConnectionPerServer, 1, 32);
        var split = ParseInt(env, "ARIA2_SPLIT", DefaultSplit, 1, 32);
        var minSplitSize = ParseSizeToken(env, "ARIA2_MIN_SPLIT_SIZE", DefaultMinSplitSize);
        var fileAllocation = ParseFileAllocation(env, "ARIA2_FILE_ALLOCATION", DefaultFileAllocation);
        var modelDownloadRetryInitialDelay = ParseTimeSpan(
            env,
            "RUNNER_MODEL_DOWNLOAD_RETRY_INITIAL_SEC",
            DefaultModelDownloadRetryInitialDelay,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(5));
        var modelDownloadRetryMaxDelay = ParseTimeSpan(
            env,
            "RUNNER_MODEL_DOWNLOAD_RETRY_MAX_SEC",
            DefaultModelDownloadRetryMaxDelay,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(15));
        if (modelDownloadRetryMaxDelay < modelDownloadRetryInitialDelay)
        {
            modelDownloadRetryMaxDelay = modelDownloadRetryInitialDelay;
        }
        var checkIntegrity = ParseBool(env, "ARIA2_CHECK_INTEGRITY", defaultValue: true);
        var shutdownTimeout = ParseTimeSpan(env, "ARIA2_SHUTDOWN_TIMEOUT_SEC", DefaultShutdownTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        var disableIpv6 = ParseBool(env, "ARIA2_DISABLE_IPV6", defaultValue: true);

        return new Aria2Settings(
            binary,
            downloadDir,
            stateDir,
            sessionFile,
            rpcPort,
            secret,
            readyTimeout,
            probeInterval,
            saveInterval,
            autoSaveInterval,
            maxConnPerServer,
            split,
            minSplitSize,
            fileAllocation,
            modelDownloadRetryInitialDelay,
            modelDownloadRetryMaxDelay,
            checkIntegrity,
            shutdownTimeout,
            disableIpv6);
    }

    private static string NormalizeDirectory(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        var trimmed = value.Trim().TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    private static string CombinePosixPath(string directory, string fileName)
        => directory == "/" ? $"/{fileName}" : $"{directory.TrimEnd('/')}/{fileName}";

    private static string? GetEnv(Func<string, string?> env, string name) => env(name);

    private static int ParseInt(Func<string, string?> env, string name, int fallback, int min, int max)
    {
        var raw = GetEnv(env, name);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return fallback;
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static string ParseString(Func<string, string?> env, string name, string fallback)
    {
        var raw = GetEnv(env, name);
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
    }

    private static string ParseSizeToken(Func<string, string?> env, string name, string fallback)
    {
        var value = ParseString(env, name, fallback);
        var match = MinSplitSizePattern.Match(value);
        if (!match.Success)
        {
            return fallback;
        }

        if (!int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue))
        {
            return fallback;
        }

        var unit = match.Groups["unit"].Value.ToUpperInvariant();
        var isValid = unit switch
        {
            "M" => parsedValue is >= 1 and <= 1024,
            "K" => parsedValue is >= 1024 and <= 1024 * 1024,
            _ => false
        };

        if (!isValid)
        {
            return fallback;
        }

        return $"{parsedValue}{unit}";
    }

    private static string ParseFileAllocation(Func<string, string?> env, string name, string fallback)
    {
        var value = ParseString(env, name, fallback).ToLowerInvariant();
        return AllowedFileAllocationModes.Contains(value) ? value : fallback;
    }

    private static bool ParseBool(Func<string, string?> env, string name, bool defaultValue)
    {
        var raw = GetEnv(env, name);
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;

        if (bool.TryParse(raw, out var parsed)) return parsed;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric)) return numeric != 0;
        return defaultValue;
    }

    private static TimeSpan ParseTimeSpan(Func<string, string?> env, string name, TimeSpan fallback, TimeSpan min, TimeSpan max)
    {
        var raw = GetEnv(env, name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return fallback;
        if (seconds <= 0) return fallback;

        var result = TimeSpan.FromSeconds(seconds);
        if (result < min) return min;
        if (result > max) return max;
        return result;
    }

    private static string BuildSecret(string? raw, Func<string> secretFactory)
    {
        if (!string.IsNullOrWhiteSpace(raw)) return raw.Trim();
        return secretFactory();
    }
}
