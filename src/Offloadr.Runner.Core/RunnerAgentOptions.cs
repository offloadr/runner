using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Offloadr.Runner.Core;

public sealed class SessionProcessOptions
{
    private const string DefaultSessionRoot = "/sessions";
    private const string DefaultEntryPoint = "/comfyui/entrypoint.base.sh";
    private const string DefaultWorkingDirectory = "/comfyui";
    private const string DefaultBundledCustomNodesSeedPath = "/opt/offloadr/seed/custom_nodes";
    private const string DefaultBundledInputSeedPath = "/opt/offloadr/seed/input";
    private const string DefaultSeedVirtualEnvPath = "/opt/venv";
    private const string DefaultUvBinaryPath = "/usr/local/bin/uv";
    private const string DefaultRuntimeKind = "comfyui";
    private const string DefaultReadyPath = "/system_stats";
    private const int DefaultPort = 8188;
    private const int DefaultShutdownSeconds = 20;
    private const int DefaultReadyTimeoutSeconds = 120;

    public required string SessionRoot { get; init; }
    public required string EntryPointPath { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string BundledCustomNodesSeedPath { get; init; }
    public required string BundledInputSeedPath { get; init; }
    public string SeedVirtualEnvPath { get; init; } = DefaultSeedVirtualEnvPath;
    public string UvBinaryPath { get; init; } = DefaultUvBinaryPath;
    public string RuntimeKind { get; init; } = DefaultRuntimeKind;
    public string ReadyPath { get; init; } = DefaultReadyPath;
    public required int ComfyPort { get; init; }
    public required TimeSpan ShutdownGracePeriod { get; init; }
    public required TimeSpan ReadyTimeout { get; init; }
    public required string ReadyHost { get; init; }

    public static SessionProcessOptions FromEnvironment(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;

        var shutdownSeconds = ParseInt(env, "COMFY_SHUTDOWN_TIMEOUT_SEC", DefaultShutdownSeconds, 5, 120);
        var readySeconds = ParseInt(env, "COMFY_READY_TIMEOUT_SEC", DefaultReadyTimeoutSeconds, 5, 600);

        return new SessionProcessOptions
        {
            SessionRoot = GetTrimmedOrDefault(env("SESSION_ROOT"), DefaultSessionRoot),
            EntryPointPath = GetTrimmedOrDefault(env("COMFY_ENTRYPOINT"), DefaultEntryPoint),
            WorkingDirectory = GetTrimmedOrDefault(env("COMFY_WORKDIR"), DefaultWorkingDirectory),
            BundledCustomNodesSeedPath = GetTrimmedOrDefault(env("COMFY_CUSTOM_NODES_SEED_PATH"), DefaultBundledCustomNodesSeedPath),
            BundledInputSeedPath = GetTrimmedOrDefault(env("COMFY_INPUT_SEED_PATH"), DefaultBundledInputSeedPath),
            SeedVirtualEnvPath = GetTrimmedOrDefault(env("RUNNER_SEED_VENV_PATH"), DefaultSeedVirtualEnvPath),
            UvBinaryPath = GetTrimmedOrDefault(env("RUNNER_UV_BINARY"), DefaultUvBinaryPath),
            RuntimeKind = GetTrimmedOrDefault(env("EDITOR_RUNTIME"), DefaultRuntimeKind).ToLowerInvariant(),
            ReadyPath = GetTrimmedOrDefault(env("EDITOR_READY_PATH"), DefaultReadyPath),
            ComfyPort = ParseInt(env, "COMFY_PORT", DefaultPort, 1, 65535),
            ShutdownGracePeriod = TimeSpan.FromSeconds(shutdownSeconds),
            ReadyTimeout = TimeSpan.FromSeconds(readySeconds),
            ReadyHost = GetTrimmedOrDefault(env("COMFY_READY_HOST"), "127.0.0.1")
        };
    }

    private static int ParseInt(Func<string, string?> env, string name, int fallback, int min, int max)
    {
        var raw = env(name);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return fallback;
        }

        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static string GetTrimmedOrDefault(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim();
    }
}

public sealed class RunnerAgentOptions
{
    private const string DefaultOffloadrApiUrl = "https://offloadr.studio";

    public required string RunnerSecret { get; init; }
    public required string OffloadrApiUrl { get; init; }
    public required bool TraceGrpcHttp { get; init; }
    public required string RunnerId { get; init; }
    public required TimeSpan RegistrationMinBackoff { get; init; }
    public required TimeSpan RegistrationMaxBackoff { get; init; }
    public required TimeSpan RegistrationRpcTimeout { get; init; }
    public required string ModelFetchSocket { get; init; }
    public string? LocalModelsDirectory { get; init; }
    public IReadOnlyList<string> SupportedEditorTemplates { get; init; } = ["comfyui-latest", "comfyui-master"];
    public required TimeSpan LocalModelsScanInterval { get; init; }
    public required TimeSpan RuntimeTelemetryInterval { get; init; }
    public required SessionProcessOptions Session { get; init; }
    public required Aria2Settings Aria2 { get; init; }

    public static RunnerAgentOptions FromEnvironment(
        Func<string, string?>? env = null,
        Func<string, bool>? directoryExists = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        directoryExists ??= Directory.Exists;

        var offloadrApiUrl = GetTrimmedOrNull(env("OFFLOADR_API_GRPC")) ?? DefaultOffloadrApiUrl;
        var runnerSecret = GetTrimmedOrDefault(env("RUNNER_SECRET"), string.Empty);
        var runnerId = ResolveRunnerId(runnerSecret, GetTrimmedOrNull(env("RUNNER_ID")));

        var minBackoffSec = ParseDouble(env("REG_MIN_BACKOFF_SEC"), fallback: 1);
        var minBackoff = TimeSpan.FromSeconds(Math.Max(0.5, minBackoffSec));
        var maxBackoffSec = ParseDouble(env("REG_MAX_BACKOFF_SEC"), fallback: 60);
        var maxBackoff = TimeSpan.FromSeconds(Math.Max(minBackoff.TotalSeconds, maxBackoffSec));
        var rpcTimeoutSec = ParseDouble(env("REG_RPC_TIMEOUT_SEC"), fallback: 10);
        var rpcTimeout = TimeSpan.FromSeconds(Math.Max(3, rpcTimeoutSec));

        var localModelsDirectory = GetTrimmedOrNull(env("RUNNER_LOCAL_MODELS_DIR"));
        if (string.IsNullOrWhiteSpace(localModelsDirectory))
        {
            const string autoDetectedLocalModelsDirectory = "/local";
            if (directoryExists(autoDetectedLocalModelsDirectory))
            {
                localModelsDirectory = autoDetectedLocalModelsDirectory;
            }
        }

        return new RunnerAgentOptions
        {
            RunnerSecret = runnerSecret,
            OffloadrApiUrl = offloadrApiUrl,
            TraceGrpcHttp = ParseBool(env("RUNNER_GRPC_TRACE_HTTP"), defaultValue: false),
            RunnerId = runnerId,
            RegistrationMinBackoff = minBackoff,
            RegistrationMaxBackoff = maxBackoff,
            RegistrationRpcTimeout = rpcTimeout,
            ModelFetchSocket = GetTrimmedOrDefault(env("RUNNER_MODEL_FETCH_SOCKET"), "/tmp/runner-agent/model-fetch.sock"),
            LocalModelsDirectory = localModelsDirectory,
            SupportedEditorTemplates = ParseSupportedEditorTemplates(env),
            LocalModelsScanInterval = TimeSpan.FromSeconds(ParseInt(env, "RUNNER_LOCAL_MODELS_SCAN_INTERVAL_SEC", 15, 5, 600)),
            RuntimeTelemetryInterval = TimeSpan.FromSeconds(ParseInt(env, "RUNNER_GPU_STATS_INTERVAL_SEC", 5, 0, 300)),
            Session = SessionProcessOptions.FromEnvironment(env),
            Aria2 = Aria2Settings.FromEnvironment(env)
        };
    }

    private static IReadOnlyList<string> ParseSupportedEditorTemplates(Func<string, string?> env)
    {
        var raw = GetTrimmedOrNull(env("RUNNER_SUPPORTED_EDITOR_TEMPLATES"));
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var runtimeKind = GetTrimmedOrDefault(env("EDITOR_RUNTIME"), "comfyui").ToLowerInvariant();
        return runtimeKind switch
        {
            "forge-neo" => ["forge-neo:neo", "forge-neo:latest"],
            _ => ["comfyui-latest", "comfyui-master"],
        };
    }

    private static int ParseInt(Func<string, string?> env, string name, int fallback, int min, int max)
    {
        var raw = env(name);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return fallback;
        }

        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static double ParseDouble(string? raw, double fallback)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return fallback;
        }

        if (!double.IsFinite(value))
        {
            return fallback;
        }

        return value;
    }

    private static bool ParseBool(string? raw, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric != 0;
        }

        return defaultValue;
    }

    private static string ResolveRunnerId(string runnerSecret, string? configuredRunnerId)
    {
        if (!string.IsNullOrWhiteSpace(configuredRunnerId))
        {
            var trimmedRunnerId = configuredRunnerId.Trim();
            if (Guid.TryParse(trimmedRunnerId, out var parsedRunnerId))
            {
                return parsedRunnerId.ToString("n");
            }

            // Preserve malformed input so startup validation can fail loudly instead of
            // silently deriving a different identity from the runner secret.
            return trimmedRunnerId;
        }

        if (!string.IsNullOrWhiteSpace(runnerSecret))
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(runnerSecret));
            // RunnerId must remain UUID-shaped (128 bits). Fold both halves of SHA-256 so
            // all digest bytes influence the resulting deterministic identifier.
            var runnerBytes = new byte[16];
            for (var i = 0; i < runnerBytes.Length; i++)
            {
                runnerBytes[i] = (byte)(digest[i] ^ digest[i + 16]);
            }
            return new Guid(runnerBytes).ToString("n");
        }

        return Guid.NewGuid().ToString("n");
    }

    private static string GetTrimmedOrDefault(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim();
    }

    private static string? GetTrimmedOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}
