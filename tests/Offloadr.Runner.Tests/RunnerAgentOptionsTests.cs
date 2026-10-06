
namespace Offloadr.Runner.Tests;

public class RunnerAgentOptionsTests
{
    [Test]
    public void FromEnvironment_UsesDirectGrpcUrl_WhenConfigured()
    {
        var env = new Dictionary<string, string?>
        {
            ["OFFLOADR_API_GRPC"] = "http://proxy:31081"
        };

        var options = RunnerAgentOptions.FromEnvironment(GetEnv(env));

        Assert.That(options.OffloadrApiUrl, Is.EqualTo("http://proxy:31081"));
    }

    [Test]
    public void FromEnvironment_UsesConfiguredDirectGrpcEndpoint()
    {
        var env = new Dictionary<string, string?>
        {
            ["OFFLOADR_API_GRPC"] = "http://offloadr-api:31080"
        };

        var options = RunnerAgentOptions.FromEnvironment(GetEnv(env));

        Assert.That(options.OffloadrApiUrl, Is.EqualTo("http://offloadr-api:31080"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void FromEnvironment_UsesProductionOffloadrApiUrl_WhenUnsetOrBlank(string? offloadrApiUrl)
    {
        var env = new Dictionary<string, string?>
        {
            ["OFFLOADR_API_GRPC"] = offloadrApiUrl
        };

        var options = RunnerAgentOptions.FromEnvironment(GetEnv(env));

        Assert.That(options.OffloadrApiUrl, Is.EqualTo("https://offloadr.studio"));
    }

    [Test]
    public void FromEnvironment_ClampsRegistrationSettings()
    {
        var env = new Dictionary<string, string?>
        {
            ["REG_MIN_BACKOFF_SEC"] = "-5",
            ["REG_MAX_BACKOFF_SEC"] = "0.25",
            ["REG_RPC_TIMEOUT_SEC"] = "1"
        };

        var options = RunnerAgentOptions.FromEnvironment(GetEnv(env));

        Assert.That(options.RegistrationMinBackoff, Is.EqualTo(TimeSpan.FromSeconds(0.5)));
        Assert.That(options.RegistrationMaxBackoff, Is.EqualTo(TimeSpan.FromSeconds(0.5)));
        Assert.That(options.RegistrationRpcTimeout, Is.EqualTo(TimeSpan.FromSeconds(3)));
    }

    [Test]
    public void FromEnvironment_ParsesSessionProcessOptions()
    {
        var env = new Dictionary<string, string?>
        {
            ["RUNNER_SECRET"] = "runner-secret-value",
            ["SESSION_ROOT"] = "/tmp/sessions",
            ["COMFY_ENTRYPOINT"] = "/tmp/comfy-entrypoint.sh",
            ["COMFY_WORKDIR"] = "/tmp/comfy",
            ["RUNNER_SEED_VENV_PATH"] = "/opt/test-venv",
            ["RUNNER_UV_BINARY"] = "/opt/tools/uv",
            ["COMFY_PORT"] = "9999",
            ["COMFY_SHUTDOWN_TIMEOUT_SEC"] = "30",
            ["COMFY_READY_TIMEOUT_SEC"] = "240",
            ["COMFY_READY_HOST"] = "0.0.0.0"
        };

        var options = RunnerAgentOptions.FromEnvironment(GetEnv(env));

        Assert.That(options.RunnerSecret, Is.EqualTo("runner-secret-value"));
        Assert.That(options.Session.SessionRoot, Is.EqualTo("/tmp/sessions"));
        Assert.That(options.Session.EntryPointPath, Is.EqualTo("/tmp/comfy-entrypoint.sh"));
        Assert.That(options.Session.WorkingDirectory, Is.EqualTo("/tmp/comfy"));
        Assert.That(options.Session.SeedVirtualEnvPath, Is.EqualTo("/opt/test-venv"));
        Assert.That(options.Session.UvBinaryPath, Is.EqualTo("/opt/tools/uv"));
        Assert.That(options.Session.ComfyPort, Is.EqualTo(9999));
        Assert.That(options.Session.ShutdownGracePeriod, Is.EqualTo(TimeSpan.FromSeconds(30)));
        Assert.That(options.Session.ReadyTimeout, Is.EqualTo(TimeSpan.FromSeconds(240)));
        Assert.That(options.Session.ReadyHost, Is.EqualTo("0.0.0.0"));
    }

    [Test]
    public void FromEnvironment_UsesWrapperComfyEntrypointByDefault()
    {
        var env = new Dictionary<string, string?>();

        var options = RunnerAgentOptions.FromEnvironment(GetEnv(env));

        Assert.That(options.Session.EntryPointPath, Is.EqualTo("/comfyui/entrypoint.base.sh"));
    }

    [Test]
    public void FromEnvironment_UsesOptVirtualEnvSeedByDefault()
    {
        var env = new Dictionary<string, string?>();

        var options = RunnerAgentOptions.FromEnvironment(GetEnv(env));

        Assert.That(options.Session.SeedVirtualEnvPath, Is.EqualTo("/opt/venv"));
        Assert.That(options.Session.UvBinaryPath, Is.EqualTo("/usr/local/bin/uv"));
    }

    [Test]
    public void FromEnvironment_UsesEmptyRunnerSecret_WhenUnset()
    {
        var env = new Dictionary<string, string?>
        {
            ["OFFLOADR_API_GRPC"] = "https://proxy:31081"
        };

        var options = RunnerAgentOptions.FromEnvironment(GetEnv(env));

        Assert.That(options.RunnerSecret, Is.EqualTo(string.Empty));
    }

    [Test]
    public void FromEnvironment_DerivesStableRunnerIdFromSecret_WhenRunnerIdUnset()
    {
        var env = new Dictionary<string, string?>
        {
            ["RUNNER_SECRET"] = "stable-secret"
        };

        var first = RunnerAgentOptions.FromEnvironment(GetEnv(env));
        var second = RunnerAgentOptions.FromEnvironment(GetEnv(env));

        Assert.That(first.RunnerId, Is.EqualTo(second.RunnerId));
    }

    [Test]
    public void FromEnvironment_DerivesDifferentRunnerIds_WhenSecretsDiffer()
    {
        var firstEnv = new Dictionary<string, string?>
        {
            ["RUNNER_SECRET"] = "secret-one"
        };
        var secondEnv = new Dictionary<string, string?>
        {
            ["RUNNER_SECRET"] = "secret-two"
        };

        var first = RunnerAgentOptions.FromEnvironment(GetEnv(firstEnv));
        var second = RunnerAgentOptions.FromEnvironment(GetEnv(secondEnv));

        Assert.That(first.RunnerId, Is.Not.EqualTo(second.RunnerId));
    }

    [Test]
    public void FromEnvironment_PreservesMalformedConfiguredRunnerId_ForStartupValidation()
    {
        var env = new Dictionary<string, string?>
        {
            ["RUNNER_SECRET"] = "stable-secret",
            ["RUNNER_ID"] = "not-a-guid"
        };

        var options = RunnerAgentOptions.FromEnvironment(GetEnv(env));

        Assert.That(options.RunnerId, Is.EqualTo("not-a-guid"));
    }

    [Test]
    public void FromEnvironment_ParsesLocalModelSettings()
    {
        var env = new Dictionary<string, string?>
        {
            ["RUNNER_LOCAL_MODELS_DIR"] = "/models/local",
            ["RUNNER_LOCAL_MODELS_SCAN_INTERVAL_SEC"] = "45"
        };

        var options = RunnerAgentOptions.FromEnvironment(GetEnv(env));

        Assert.That(options.LocalModelsDirectory, Is.EqualTo("/models/local"));
        Assert.That(options.LocalModelsScanInterval, Is.EqualTo(TimeSpan.FromSeconds(45)));
    }

    [Test]
    public void FromEnvironment_AutoDetectsDefaultLocalModelsMount_WhenUnset()
    {
        var env = new Dictionary<string, string?>
        {
            ["OFFLOADR_API_GRPC"] = "https://proxy:31081"
        };

        var options = RunnerAgentOptions.FromEnvironment(
            GetEnv(env),
            path => string.Equals(path, "/local", StringComparison.Ordinal));

        Assert.That(options.LocalModelsDirectory, Is.EqualTo("/local"));
    }

    [Test]
    public void FromEnvironment_ParsesRuntimeTelemetryInterval()
    {
        var env = new Dictionary<string, string?>
        {
            ["RUNNER_GPU_STATS_INTERVAL_SEC"] = "12"
        };

        var options = RunnerAgentOptions.FromEnvironment(GetEnv(env));

        Assert.That(options.RuntimeTelemetryInterval, Is.EqualTo(TimeSpan.FromSeconds(12)));
    }

    [Test]
    public void FromEnvironment_AllowsDisablingRuntimeTelemetryInterval()
    {
        var env = new Dictionary<string, string?>
        {
            ["RUNNER_GPU_STATS_INTERVAL_SEC"] = "0"
        };

        var options = RunnerAgentOptions.FromEnvironment(GetEnv(env));

        Assert.That(options.RuntimeTelemetryInterval, Is.EqualTo(TimeSpan.Zero));
    }

    private static Func<string, string?> GetEnv(IReadOnlyDictionary<string, string?> values)
    {
        return key => values.TryGetValue(key, out var value) ? value : null;
    }
}
