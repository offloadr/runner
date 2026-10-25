using System.Diagnostics;

namespace Offloadr.Runner.Tests;

public partial class SessionProcessManagerOwnershipTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task RunManagerUvSyncOnceAsync_JoinsQuiescenceBeforeReturning(bool cancel)
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireCommand("/bin/sh");
        LinuxTestPrerequisites.RequireCommand("/bin/sleep");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-sync-lifetime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        using var stopping = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pidPath = Path.Combine(tempRoot, "started.pid");
        Process? helper = null;
        try
        {
            var (options, identity, paths) = PrepareDependencySyncFixture(tempRoot,
                $$"""
                printf '%s' "$$" > '{{pidPath}}'
                printf 'sync output\n'
                {{(cancel ? "exec /bin/sleep 300" : "exit 17")}}
                """);
            var isolation = new RecordingIsolationStrategy(identity.UserName, identity.HomeDirectory)
            {
                Quiesce = async () =>
                {
                    entered.TrySetResult();
                    await release.Task;
                }
            };
            using var manager = new SessionProcessManager(options, isolation, new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));
            var sync = manager.RunManagerUvSyncOnceAsync("sync-lifetime", identity, paths, stopping.Token);
            try
            {
                if (cancel)
                {
                    await WaitForDependencySyncPidAsync(pidPath);
                    helper = Process.GetProcessById(int.Parse(File.ReadAllText(pidPath), System.Globalization.CultureInfo.InvariantCulture));
                    await stopping.CancelAsync();
                }

                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Multiple(() =>
                {
                    Assert.That(sync.IsCompleted, Is.False, "the actual process-quiescence task is still held");
                    Assert.That(isolation.CleanupCallCount, Is.Zero, "the account and data must remain owned");
                    Assert.That(Directory.Exists(identity.HomeDirectory), Is.True);
                });
            }
            finally
            {
                if (!entered.Task.IsCompleted) await stopping.CancelAsync();
                release.TrySetResult();
                // Always join the original operation, including when an earlier assertion fails.
                try { await sync; }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            }

            if (cancel)
            {
                Assert.That(sync.IsCanceled, Is.True);
                Assert.That(helper!.HasExited, Is.True);
            }
            else
            {
                Assert.Multiple(() =>
                {
                    Assert.That(sync.Result.ExitCode, Is.EqualTo(17));
                    Assert.That(sync.Result.Output, Does.Contain("sync output"));
                });
            }

            Assert.Multiple(() =>
            {
                Assert.That(isolation.QuiescenceCallCount, Is.EqualTo(1));
                Assert.That(isolation.CleanupCallCount, Is.Zero);
            });
        }
        finally
        {
            release.TrySetResult();
            if (helper is not null)
            {
                try
                {
                    if (!helper.HasExited) helper.Kill();
                    await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { helper.Dispose(); }
            }
            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task RunManagerUvSyncOnceAsync_QuiescenceFailureCannotBecomeSuccessfulStartup()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireCommand("/bin/sh");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-sync-reap-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var (options, identity, paths) = PrepareDependencySyncFixture(tempRoot, "printf 'sync output\\n'\nexit 0\n");
            var isolation = new RecordingIsolationStrategy(identity.UserName, identity.HomeDirectory)
            {
                Quiesce = () => throw new InvalidOperationException("controlled surviving process")
            };
            using var manager = new SessionProcessManager(options, isolation, new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));

            Assert.That(async () => await manager.RunManagerUvSyncOnceAsync("sync-reap-failure", identity, paths, CancellationToken.None),
                Throws.InvalidOperationException.With.Message.EqualTo("controlled surviving process"));
            Assert.Multiple(() =>
            {
                Assert.That(isolation.QuiescenceCallCount, Is.EqualTo(1), "a failed original reaper must not be replaced or retried here");
                Assert.That(isolation.CleanupCallCount, Is.Zero);
                Assert.That(Directory.Exists(identity.HomeDirectory), Is.True);
            });
        }
        finally { TryDelete(tempRoot); }
    }

    private static (SessionProcessOptions Options, PreparedSessionIdentity Identity, SessionProcessManager.SessionPaths Paths)
        PrepareDependencySyncFixture(string root, string script)
    {
        var home = Path.Combine(root, "session");
        var venv = Path.Combine(home, ".venv");
        var python = Path.Combine(venv, "bin", "python");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "#!/bin/sh\n" + script);
        MakeExecutable(python);
        var options = new SessionProcessOptions
        {
            SessionRoot = Path.Combine(root, "sessions"),
            EntryPointPath = Path.Combine(root, "unused-entrypoint"),
            WorkingDirectory = root,
            BundledCustomNodesSeedPath = Path.Combine(root, "seed-custom_nodes"),
            BundledInputSeedPath = Path.Combine(root, "seed-input"),
            SeedVirtualEnvPath = CreateEmptySeedVirtualEnv(root),
            UvBinaryPath = FakeUvBinaryPath(root),
            ComfyPort = 8188,
            ShutdownGracePeriod = TimeSpan.FromSeconds(5),
            ReadyTimeout = TimeSpan.FromSeconds(5),
            ReadyHost = "127.0.0.1"
        };
        var identity = new PreparedSessionIdentity(string.Empty, home, CleanupIdentity: true);
        var paths = new SessionProcessManager.SessionPaths
        {
            HomeDirectory = home,
            VirtualEnvDirectory = venv,
            UserDirectory = Path.Combine(home, "user"),
            CacheDirectory = Path.Combine(home, ".cache"),
            UvCacheDirectory = Path.Combine(home, ".cache", "uv"),
            ScratchDirectory = Path.Combine(home, "tmp"),
            TorchInductorCacheDirectory = Path.Combine(home, "tmp", "torchinductor")
        };
        return (options, identity, paths);
    }

    private static async Task WaitForDependencySyncPidAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(path) || new FileInfo(path).Length == 0)
            await Task.Delay(10, timeout.Token);
    }
}
