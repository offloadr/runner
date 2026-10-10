
namespace Offloadr.Runner.Tests;

public class SessionProcessManagerLinuxTests
{
    [Test]
    public void BuildUserName_SanitizesAndTruncates()
    {
        var userName = LinuxSessionIdentity.BuildUserName(" Session-ABC_1234567890 ");

        Assert.That(userName, Is.EqualTo("sess_sessionabc12"));
    }

    [Test]
    public void BuildUserName_UsesFallbackForNonAlnumIds()
    {
        var userName = LinuxSessionIdentity.BuildUserName("---");

        Assert.That(userName, Is.EqualTo("sess_job"));
    }

    [Test]
    public void Build_IncludesSessionRootsAndExpandedExtensions()
    {
        var env = new Dictionary<string, string?>
        {
            ["RUNNER_VFS_ROOTS"] = "/comfyui/models:/opt/custom",
            ["RUNNER_VFS_EXTS"] = ".safetensors,.foo",
            ["RUNNER_MODEL_FETCH_SOCKET"] = "/tmp/vfs.sock",
            ["RUNNER_VFS_LOG"] = "1"
        };

        var builder = new RunnerVfsEnvironmentBuilder();
        var result = builder.Build(
            new RunnerVfsSessionPaths("/sessions/abc/models", "/sessions/abc/output", "/sessions/abc/input", "/sessions/abc/tmp"),
            getEnv: name => env.TryGetValue(name, out var value) ? value : null,
            fileExists: _ => false);

        var roots = result["VFS_ROOTS"].Split(':', StringSplitOptions.RemoveEmptyEntries);
        var exts = result["VFS_EXTS"].Split(',', StringSplitOptions.RemoveEmptyEntries);

        Assert.That(result["VFS_SOCKET"], Is.EqualTo("/tmp/vfs.sock"));
        Assert.That(result["VFS_LOG"], Is.EqualTo("1"));
        Assert.That(roots, Does.Contain("/comfyui/models"));
        Assert.That(roots, Does.Contain("/opt/custom"));
        Assert.That(roots, Does.Contain("/sessions/abc/models"));
        Assert.That(roots, Does.Contain("/sessions/abc/output"));
        Assert.That(roots, Does.Contain("/sessions/abc/input"));
        Assert.That(roots, Does.Contain("/sessions/abc/tmp"));
        Assert.That(exts, Does.Contain(".safetensors"));
        Assert.That(exts, Does.Contain(".foo"));
        Assert.That(exts, Does.Contain(".pt"));
        Assert.That(exts, Does.Contain(".pth"));
        Assert.That(exts, Does.Contain(".bin"));
        Assert.That(exts, Does.Contain(".ckpt"));
        Assert.That(exts, Does.Contain(".onnx"));
        Assert.That(exts, Does.Contain(".emb"));
        Assert.That(exts, Does.Contain(".gguf"));
        Assert.That(exts, Does.Contain(".yaml"));
        Assert.That(exts, Does.Contain(".png"));
        Assert.That(exts, Does.Contain(".mp4"));
        Assert.That(exts, Does.Contain(".wav"));
        Assert.That(exts, Does.Contain(".mp3"));
    }

    [Test]
    public void Build_SetsLdPreload_WhenLibraryExists()
    {
        var env = new Dictionary<string, string?>
        {
            ["RUNNER_VFS_LIB"] = "/opt/offloadr/lib/liboffloadr_model_vfs.so",
            ["LD_PRELOAD"] = "libexisting.so"
        };

        var builder = new RunnerVfsEnvironmentBuilder();
        var result = builder.Build(
            new RunnerVfsSessionPaths("/models", "/out", "/tmp"),
            getEnv: name => env.TryGetValue(name, out var value) ? value : null,
            fileExists: path => path == "/opt/offloadr/lib/liboffloadr_model_vfs.so");

        Assert.That(result.ContainsKey("LD_PRELOAD"), Is.True);
        Assert.That(result["LD_PRELOAD"], Is.EqualTo("/opt/offloadr/lib/liboffloadr_model_vfs.so:libexisting.so"));
    }

    [Test]
    public void Build_RejectsPersistentRangeHydrationWhenLibraryIsMissing()
    {
        var env = new Dictionary<string, string?>
        {
            ["RUNNER_PERSISTENT_RANGE_HYDRATION"] = "1",
            ["RUNNER_VFS_ROOTS"] = "/persistent/models",
            ["RUNNER_VFS_LIB"] = "/missing/liboffloadr_model_vfs.so"
        };

        var builder = new RunnerVfsEnvironmentBuilder();

        Assert.That(
            () => builder.Build(
                new RunnerVfsSessionPaths("/models", "/out", "/input"),
                getEnv: name => env.TryGetValue(name, out var value) ? value : null,
                fileExists: static _ => false),
            Throws.InvalidOperationException.With.Message.Contains("requires the model VFS library"));
    }

    [Test]
    public void LinuxCommandFactory_ProducesExpectedCommands()
    {
        var check = LinuxCommandFactory.CheckUserExists("sess_demo");
        var create = LinuxCommandFactory.CreateUser("sess_demo", "/sessions/demo");
        var chown = LinuxCommandFactory.ChownRecursive("sess_demo", "/sessions/demo");
        var kill = LinuxCommandFactory.SendSignal(42, "TERM");
        var remove = LinuxCommandFactory.RemoveUser("sess_demo");

        Assert.That(check.FileName, Is.EqualTo("/usr/bin/id"));
        Assert.That(check.Arguments, Is.EqualTo(new[] { "-u", "sess_demo" }));
        Assert.That(check.ThrowOnError, Is.False);

        Assert.That(create.FileName, Is.EqualTo("/usr/sbin/useradd"));
        Assert.That(create.Arguments, Is.EqualTo(new[] { "--home-dir", "/sessions/demo", "-m", "-U", "--shell", "/usr/sbin/nologin", "sess_demo" }));

        Assert.That(chown.FileName, Is.EqualTo("/bin/chown"));
        Assert.That(chown.Arguments, Is.EqualTo(new[] { "-R", "-h", "sess_demo:sess_demo", "/sessions/demo" }));

        Assert.That(kill.FileName, Is.EqualTo("/bin/kill"));
        Assert.That(kill.Arguments, Is.EqualTo(new[] { "-TERM", "42" }));
        Assert.That(kill.ThrowOnError, Is.False);

        Assert.That(remove.FileName, Is.EqualTo("/usr/sbin/userdel"));
        Assert.That(remove.Arguments, Is.EqualTo(new[] { "--force", "--remove", "sess_demo" }));
    }

    [Test]
    public async Task LinuxCommandRunner_RunsCommandAndCapturesOutput()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireCommand("/bin/sh");

        var runner = new LinuxCommandRunner();
        var result = await runner.RunAsync(new LinuxCommand("/bin/sh", ["-lc", "printf linux-ok"]), CancellationToken.None);

        Assert.That(result.ExitCode, Is.EqualTo(0));
        Assert.That(result.Stdout, Is.EqualTo("linux-ok"));
    }

    [Test]
    public async Task LinuxCommandRunner_RespectsThrowOnErrorFalse()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireCommand("/bin/sh");

        var runner = new LinuxCommandRunner();
        var result = await runner.RunAsync(new LinuxCommand("/bin/sh", ["-lc", "exit 7"], ThrowOnError: false), CancellationToken.None);

        Assert.That(result.ExitCode, Is.EqualTo(7));
    }

    [Test]
    public async Task StopSessionAsync_InvokesBeforeCleanup_ForUnknownSession()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-stop-unknown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var options = new SessionProcessOptions
            {
                SessionRoot = Path.Combine(tempRoot, "sessions"),
                EntryPointPath = Path.Combine(tempRoot, "missing-entrypoint.sh"),
                WorkingDirectory = tempRoot,
                BundledCustomNodesSeedPath = Path.Combine(tempRoot, "seed-custom_nodes"),
                BundledInputSeedPath = Path.Combine(tempRoot, "seed-input"),
                ComfyPort = 8188,
                ShutdownGracePeriod = TimeSpan.FromSeconds(5),
                ReadyTimeout = TimeSpan.FromSeconds(5),
                ReadyHost = "127.0.0.1"
            };

            Directory.CreateDirectory(options.BundledCustomNodesSeedPath);
            Directory.CreateDirectory(options.BundledInputSeedPath);

            using var manager = new SessionProcessManager(
                options,
                new NoOpIsolationStrategy(),
                new RunnerVfsEnvironmentBuilder(),
                new RecordingCommandRunner());

            string? cleanedSession = null;
            await manager.StopSessionAsync(
                "session-missing",
                (sessionId, _) =>
                {
                    cleanedSession = sessionId;
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            Assert.That(cleanedSession, Is.EqualTo("session-missing"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class NoOpIsolationStrategy : ISessionIsolationStrategy
    {
        public void ValidatePrerequisites(ICollection<string> errors)
        {
        }

        public Task<PreparedSessionIdentity> PrepareAsync(string sessionId, string sessionRoot, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task CleanupAsync(PreparedSessionIdentity identity, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class RecordingCommandRunner : ILinuxCommandRunner
    {
        public Task<LinuxCommandResult> RunAsync(LinuxCommand command, CancellationToken cancellationToken)
            => Task.FromResult(new LinuxCommandResult(0, string.Empty, string.Empty));
    }
}
