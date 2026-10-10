
namespace Offloadr.Runner.Tests;

public class Aria2SettingsTests
{
    [Test]
    public void FromEnvironment_UsesDefaults_WhenVarsMissing()
    {
        var settings = Aria2Settings.FromEnvironment(_ => null, () => "generated-secret");

        Assert.That(settings.BinaryPath, Is.EqualTo("aria2c"));
        Assert.That(settings.DownloadDirectory, Is.EqualTo("/models"));
        Assert.That(settings.StateDirectory, Is.EqualTo("/var/lib/aria2"));
        Assert.That(settings.SessionFilePath, Is.EqualTo("/var/lib/aria2/session.txt"));
        Assert.That(settings.RpcPort, Is.EqualTo(6801));
        Assert.That(settings.RpcSecret, Is.EqualTo("generated-secret"));
        Assert.That(settings.MaxConnectionPerServer, Is.EqualTo(16));
        Assert.That(settings.Split, Is.EqualTo(16));
        Assert.That(settings.MinSplitSize, Is.EqualTo("8M"));
        Assert.That(settings.FileAllocation, Is.EqualTo("falloc"));
        Assert.That(settings.ModelDownloadRetryInitialDelay, Is.EqualTo(TimeSpan.FromSeconds(2)));
        Assert.That(settings.ModelDownloadRetryMaxDelay, Is.EqualTo(TimeSpan.FromSeconds(60)));
        Assert.That(settings.CheckIntegrity, Is.True);
        Assert.That(settings.DisableIpv6, Is.True);
        Assert.That(settings.User, Is.EqualTo("offloadr-aria2"));
        Assert.That(settings.RequireUser, Is.False);
        Assert.That(settings.StagingDirectory, Is.EqualTo("/var/lib/aria2/staging"));
    }

    [Test]
    public void FromEnvironment_ReadsServiceAccountSettings()
    {
        var env = new Dictionary<string, string?>
        {
            ["ARIA2_USER"] = " downloader ",
            ["ARIA2_REQUIRE_USER"] = "1",
            ["ARIA2_STATE_DIR"] = "/state/"
        };

        var settings = Aria2Settings.FromEnvironment(name => env.TryGetValue(name, out var value) ? value : null, () => "generated-secret");

        Assert.Multiple(() =>
        {
            Assert.That(settings.User, Is.EqualTo("downloader"));
            Assert.That(settings.RequireUser, Is.True);
            Assert.That(settings.StagingDirectory, Is.EqualTo("/state/staging"));
        });
    }

    [Test]
    public void FromEnvironment_ClampsIntegerAndTimeValues()
    {
        var env = new Dictionary<string, string?>
        {
            ["ARIA2_RPC_PORT"] = "70000",
            ["ARIA2_SAVE_SESSION_INTERVAL"] = "0",
            ["ARIA2_AUTO_SAVE_INTERVAL"] = "999",
            ["ARIA2_MAX_CONNECTION_PER_SERVER"] = "100",
            ["ARIA2_SPLIT"] = "0",
            ["ARIA2_READY_TIMEOUT_SEC"] = "0.1",
            ["ARIA2_READY_PROBE_MS"] = "99",
            ["ARIA2_SHUTDOWN_TIMEOUT_SEC"] = "120",
            ["RUNNER_MODEL_DOWNLOAD_RETRY_INITIAL_SEC"] = "600",
            ["RUNNER_MODEL_DOWNLOAD_RETRY_MAX_SEC"] = "10"
        };

        var settings = Aria2Settings.FromEnvironment(name => env.TryGetValue(name, out var value) ? value : null, () => "generated-secret");

        Assert.That(settings.RpcPort, Is.EqualTo(65535));
        Assert.That(settings.SaveSessionInterval, Is.EqualTo(1));
        Assert.That(settings.AutoSaveInterval, Is.EqualTo(300));
        Assert.That(settings.MaxConnectionPerServer, Is.EqualTo(32));
        Assert.That(settings.Split, Is.EqualTo(1));
        Assert.That(settings.ReadyTimeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(settings.ProbeInterval, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(settings.ShutdownTimeout, Is.EqualTo(TimeSpan.FromSeconds(30)));
        Assert.That(settings.ModelDownloadRetryInitialDelay, Is.EqualTo(TimeSpan.FromMinutes(5)));
        Assert.That(settings.ModelDownloadRetryMaxDelay, Is.EqualTo(TimeSpan.FromMinutes(5)));
    }

    [TestCase("true", true)]
    [TestCase("false", false)]
    [TestCase("1", true)]
    [TestCase("0", false)]
    public void FromEnvironment_ParsesBooleanValues(string raw, bool expected)
    {
        var env = new Dictionary<string, string?>
        {
            ["ARIA2_CHECK_INTEGRITY"] = raw,
            ["ARIA2_DISABLE_IPV6"] = raw
        };

        var settings = Aria2Settings.FromEnvironment(name => env.TryGetValue(name, out var value) ? value : null, () => "generated-secret");

        Assert.That(settings.CheckIntegrity, Is.EqualTo(expected));
        Assert.That(settings.DisableIpv6, Is.EqualTo(expected));
    }

    [Test]
    public void FromEnvironment_NormalizesDirectories_AndPrefersExplicitSecret()
    {
        var env = new Dictionary<string, string?>
        {
            ["ARIA2_DOWNLOAD_DIR"] = " /mnt/models/// ",
            ["ARIA2_STATE_DIR"] = "/tmp/aria2///",
            ["ARIA2_RPC_SECRET"] = "  secret-value  ",
            ["ARIA2_MIN_SPLIT_SIZE"] = " 16M ",
            ["ARIA2_FILE_ALLOCATION"] = " PREALLOC "
        };

        var settings = Aria2Settings.FromEnvironment(name => env.TryGetValue(name, out var value) ? value : null, () => "generated-secret");

        Assert.That(settings.DownloadDirectory, Is.EqualTo("/mnt/models"));
        Assert.That(settings.StateDirectory, Is.EqualTo("/tmp/aria2"));
        Assert.That(settings.SessionFilePath, Is.EqualTo("/tmp/aria2/session.txt"));
        Assert.That(settings.RpcSecret, Is.EqualTo("secret-value"));
        Assert.That(settings.MinSplitSize, Is.EqualTo("16M"));
        Assert.That(settings.FileAllocation, Is.EqualTo("prealloc"));
    }

    [Test]
    public void FromEnvironment_FallsBackForEmptyMinSplitSize_AndInvalidFileAllocation()
    {
        var env = new Dictionary<string, string?>
        {
            ["ARIA2_MIN_SPLIT_SIZE"] = "   ",
            ["ARIA2_FILE_ALLOCATION"] = "bogus"
        };

        var settings = Aria2Settings.FromEnvironment(name => env.TryGetValue(name, out var value) ? value : null, () => "generated-secret");

        Assert.That(settings.MinSplitSize, Is.EqualTo("8M"));
        Assert.That(settings.FileAllocation, Is.EqualTo("falloc"));
    }

    [Test]
    public void FromEnvironment_FallsBackForInvalidMinSplitSizeToken()
    {
        var env = new Dictionary<string, string?>
        {
            ["ARIA2_MIN_SPLIT_SIZE"] = "16MB"
        };

        var settings = Aria2Settings.FromEnvironment(name => env.TryGetValue(name, out var value) ? value : null, () => "generated-secret");

        Assert.That(settings.MinSplitSize, Is.EqualTo("8M"));
    }

    [TestCase("1023K")]
    [TestCase("2G")]
    [TestCase("2000M")]
    [TestCase("1048577K")]
    [TestCase("0M")]
    public void FromEnvironment_FallsBackForOutOfRangeOrUnsupportedMinSplitSize(string raw)
    {
        var env = new Dictionary<string, string?>
        {
            ["ARIA2_MIN_SPLIT_SIZE"] = raw
        };

        var settings = Aria2Settings.FromEnvironment(name => env.TryGetValue(name, out var value) ? value : null, () => "generated-secret");

        Assert.That(settings.MinSplitSize, Is.EqualTo("8M"));
    }

    [TestCase("1m", "1M")]
    [TestCase("1024M", "1024M")]
    [TestCase("1024k", "1024K")]
    [TestCase("2048K", "2048K")]
    public void FromEnvironment_AcceptsSupportedMinSplitSizeRange(string raw, string expected)
    {
        var env = new Dictionary<string, string?>
        {
            ["ARIA2_MIN_SPLIT_SIZE"] = raw
        };

        var settings = Aria2Settings.FromEnvironment(name => env.TryGetValue(name, out var value) ? value : null, () => "generated-secret");

        Assert.That(settings.MinSplitSize, Is.EqualTo(expected));
    }
}
