
namespace Offloadr.Runner.Tests;

public class RunnerStartupValidatorTests
{
    [Test]
    public void ValidateOrThrow_ThrowsWhenAriaBinaryMissing()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Runner startup validation is Linux-specific.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-validator-{Guid.NewGuid():N}");
        try
        {
            var options = BuildOptions(tempRoot, "/definitely/missing/aria2c");
            var validator = new RunnerStartupValidator(new NoopIsolationStrategy());

            var ex = Assert.Throws<InvalidOperationException>(() => validator.ValidateOrThrow(options));
            Assert.That(ex!.Message, Does.Contain("ARIA2_BINARY"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void ValidateOrThrow_PassesForWritableDirectoriesAndExistingBinary()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Runner startup validation is Linux-specific.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-validator-{Guid.NewGuid():N}");
        try
        {
            var options = BuildOptions(tempRoot, "/bin/sh");
            var validator = new RunnerStartupValidator(new NoopIsolationStrategy());

            Assert.DoesNotThrow(() => validator.ValidateOrThrow(options));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void ValidateOrThrow_ThrowsWhenRunnerSecretMissing()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Runner startup validation is Linux-specific.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-validator-{Guid.NewGuid():N}");
        try
        {
            var options = BuildOptions(tempRoot, "/bin/sh", runnerSecret: string.Empty);
            var validator = new RunnerStartupValidator(new NoopIsolationStrategy());

            var ex = Assert.Throws<InvalidOperationException>(() => validator.ValidateOrThrow(options));
            Assert.That(ex!.Message, Does.Contain("RUNNER_SECRET"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void ValidateOrThrow_ThrowsWhenOffloadrApiUrlNotHttps()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Runner startup validation is Linux-specific.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-validator-{Guid.NewGuid():N}");
        try
        {
            var options = BuildOptions(tempRoot, "/bin/sh", offloadrApiUrl: "http://offloadr-api:31080");
            var validator = new RunnerStartupValidator(new NoopIsolationStrategy());

            var ex = Assert.Throws<InvalidOperationException>(() => validator.ValidateOrThrow(options));
            Assert.That(ex!.Message, Does.Contain("offloadr-api URL must use https scheme"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void ValidateOrThrow_ThrowsWhenConfiguredRunnerIdIsNotUuid()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Runner startup validation is Linux-specific.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-validator-{Guid.NewGuid():N}");
        try
        {
            var options = BuildOptions(tempRoot, "/bin/sh", runnerId: "runner-test");
            var validator = new RunnerStartupValidator(new NoopIsolationStrategy());

            var ex = Assert.Throws<InvalidOperationException>(() => validator.ValidateOrThrow(options));
            Assert.That(ex!.Message, Does.Contain("RUNNER_ID must be a UUID"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void ValidateOrThrow_ThrowsWhenConfiguredLocalModelDirectoryMissing()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Runner startup validation is Linux-specific.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-validator-{Guid.NewGuid():N}");
        try
        {
            var options = BuildOptions(
                tempRoot,
                "/bin/sh",
                localModelsDirectory: Path.Combine(tempRoot, "missing-local-models"));
            var validator = new RunnerStartupValidator(new NoopIsolationStrategy());

            var ex = Assert.Throws<InvalidOperationException>(() => validator.ValidateOrThrow(options));
            Assert.That(ex!.Message, Does.Contain("RUNNER_LOCAL_MODELS_DIR"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void ValidateOrThrow_ThrowsWhenSeedVirtualEnvDirectoryMissing()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Runner startup validation is Linux-specific.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-validator-{Guid.NewGuid():N}");
        try
        {
            var options = BuildOptions(
                tempRoot,
                "/bin/sh",
                seedVirtualEnvPath: Path.Combine(tempRoot, "missing-seed-venv"));
            var validator = new RunnerStartupValidator(new NoopIsolationStrategy());

            var ex = Assert.Throws<InvalidOperationException>(() => validator.ValidateOrThrow(options));
            Assert.That(ex!.Message, Does.Contain("RUNNER_SEED_VENV_PATH"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void ValidateOrThrow_ThrowsWhenSeedVirtualEnvExecutablesMissing()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Runner startup validation is Linux-specific.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-validator-{Guid.NewGuid():N}");
        var seedVirtualEnvPath = Path.Combine(tempRoot, "seed-venv");
        try
        {
            Directory.CreateDirectory(seedVirtualEnvPath);
            var options = BuildOptions(tempRoot, "/bin/sh", seedVirtualEnvPath: seedVirtualEnvPath);
            var validator = new RunnerStartupValidator(new NoopIsolationStrategy());

            var ex = Assert.Throws<InvalidOperationException>(() => validator.ValidateOrThrow(options));
            Assert.That(ex!.Message, Does.Contain("RUNNER_SEED_VENV_PATH/bin/python"));
            Assert.That(ex.Message, Does.Not.Contain("RUNNER_SEED_VENV_PATH/bin/uv"));
            Assert.That(ex.Message, Does.Not.Contain("COMFY_SEED_VENV_PATH"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void ValidateOrThrow_ThrowsWhenRunnerUvBinaryMissing()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Runner startup validation is Linux-specific.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-validator-{Guid.NewGuid():N}");
        try
        {
            var options = BuildOptions(tempRoot, "/bin/sh", uvBinaryPath: Path.Combine(tempRoot, "missing-uv"));
            var validator = new RunnerStartupValidator(new NoopIsolationStrategy());

            var ex = Assert.Throws<InvalidOperationException>(() => validator.ValidateOrThrow(options));
            Assert.That(ex!.Message, Does.Contain("RUNNER_UV_BINARY"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void ValidateOrThrow_ThrowsWhenSeedVirtualEnvRootIsWritableBySessions()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Runner startup validation is Linux-specific.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-validator-{Guid.NewGuid():N}");
        var seedVirtualEnvPath = Path.Combine(tempRoot, "seed-venv");
        try
        {
            CreateFakeSeedVirtualEnv(seedVirtualEnvPath);
            MakeSeedDirectoryWritableBySessions(seedVirtualEnvPath);

            var options = BuildOptions(tempRoot, "/bin/sh", seedVirtualEnvPath: seedVirtualEnvPath);
            var validator = new RunnerStartupValidator(new NoopIsolationStrategy());

            var ex = Assert.Throws<InvalidOperationException>(() => validator.ValidateOrThrow(options));
            Assert.That(ex!.Message, Does.Contain("RUNNER_SEED_VENV_PATH"));
            Assert.That(ex.Message, Does.Contain("must not be writable by group or other users"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void ValidateOrThrow_ThrowsWhenSeedBinExecutableIsWritableBySessions()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Runner startup validation is Linux-specific.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-validator-{Guid.NewGuid():N}");
        var seedVirtualEnvPath = Path.Combine(tempRoot, "seed-venv");
        try
        {
            CreateFakeSeedVirtualEnv(seedVirtualEnvPath);
            var seedPythonPath = Path.Combine(seedVirtualEnvPath, "bin", "python");
            MakeSeedFileWritableBySessions(seedPythonPath);

            var options = BuildOptions(tempRoot, "/bin/sh", seedVirtualEnvPath: seedVirtualEnvPath);
            var validator = new RunnerStartupValidator(new NoopIsolationStrategy());

            var ex = Assert.Throws<InvalidOperationException>(() => validator.ValidateOrThrow(options));
            Assert.That(ex!.Message, Does.Contain("RUNNER_SEED_VENV_PATH/bin entry 'python'"));
            Assert.That(ex.Message, Does.Contain(seedPythonPath));
            Assert.That(ex.Message, Does.Contain("must not be writable by group or other users"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void ValidateOrThrow_ThrowsWhenSeedPayloadDirectoryIsWritableBySessions()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Runner startup validation is Linux-specific.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-validator-{Guid.NewGuid():N}");
        var seedVirtualEnvPath = Path.Combine(tempRoot, "seed-venv");
        try
        {
            CreateFakeSeedVirtualEnv(seedVirtualEnvPath);
            var payloadDirectory = Path.Combine(seedVirtualEnvPath, "lib", "python3.12", "site-packages", "PIL");
            Directory.CreateDirectory(payloadDirectory);
            MakeSeedDirectoryWritableBySessions(payloadDirectory);

            var options = BuildOptions(tempRoot, "/bin/sh", seedVirtualEnvPath: seedVirtualEnvPath);
            var validator = new RunnerStartupValidator(new NoopIsolationStrategy());

            var ex = Assert.Throws<InvalidOperationException>(() => validator.ValidateOrThrow(options));
            Assert.That(ex!.Message, Does.Contain("site-packages payload 'PIL'"));
            Assert.That(ex.Message, Does.Contain("must not be writable by group or other users"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void ValidateOrThrow_ThrowsWhenNestedSeedPayloadFileIsWritableBySessions()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Runner startup validation is Linux-specific.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-validator-{Guid.NewGuid():N}");
        var seedVirtualEnvPath = Path.Combine(tempRoot, "seed-venv");
        try
        {
            CreateFakeSeedVirtualEnv(seedVirtualEnvPath);
            var payloadDirectory = Path.Combine(seedVirtualEnvPath, "lib", "python3.12", "site-packages", "PIL");
            Directory.CreateDirectory(payloadDirectory);
            MakeSeedDirectoryNonWritableBySessions(payloadDirectory);
            var payloadFile = Path.Combine(payloadDirectory, "AvifImagePlugin.py");
            File.WriteAllText(payloadFile, "# test payload\n");
            MakeSeedFileWritableBySessions(payloadFile);

            var options = BuildOptions(tempRoot, "/bin/sh", seedVirtualEnvPath: seedVirtualEnvPath);
            var validator = new RunnerStartupValidator(new NoopIsolationStrategy());

            var ex = Assert.Throws<InvalidOperationException>(() => validator.ValidateOrThrow(options));
            Assert.That(ex!.Message, Does.Contain(payloadFile));
            Assert.That(ex.Message, Does.Contain("site-packages payload 'PIL'"));
            Assert.That(ex.Message, Does.Contain("must not be writable by group or other users"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void ValidateOrThrow_ThrowsWhenNestedSymlinkedSeedPayloadFileIsWritableBySessions()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Runner startup validation is Linux-specific.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-validator-{Guid.NewGuid():N}");
        var seedVirtualEnvPath = Path.Combine(tempRoot, "seed-venv");
        try
        {
            CreateFakeSeedVirtualEnv(seedVirtualEnvPath);
            var seedSitePackages = Path.Combine(seedVirtualEnvPath, "lib", "python3.12", "site-packages");
            var sharedPayloadDirectory = Path.Combine(tempRoot, "shared-payload", "PIL");
            Directory.CreateDirectory(sharedPayloadDirectory);
            MakeSeedDirectoryNonWritableBySessions(sharedPayloadDirectory);
            var payloadFile = Path.Combine(sharedPayloadDirectory, "AvifImagePlugin.py");
            File.WriteAllText(payloadFile, "# test payload\n");
            MakeSeedFileWritableBySessions(payloadFile);
            Directory.CreateSymbolicLink(Path.Combine(seedSitePackages, "PIL"), sharedPayloadDirectory);

            var options = BuildOptions(tempRoot, "/bin/sh", seedVirtualEnvPath: seedVirtualEnvPath);
            var validator = new RunnerStartupValidator(new NoopIsolationStrategy());

            var ex = Assert.Throws<InvalidOperationException>(() => validator.ValidateOrThrow(options));
            Assert.That(ex!.Message, Does.Contain(payloadFile));
            Assert.That(ex.Message, Does.Contain("site-packages payload 'PIL'"));
            Assert.That(ex.Message, Does.Contain("must not be writable by group or other users"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    private static RunnerAgentOptions BuildOptions(
        string tempRoot,
        string ariaBinaryPath,
        string runnerSecret = "0123456789abcdef0123456789abcdef0123456789ab",
        string offloadrApiUrl = "https://api.offloadr.app",
        string? localModelsDirectory = null,
        string? runnerId = null,
        string? seedVirtualEnvPath = null,
        string? uvBinaryPath = null)
    {
        var sessionRoot = Path.Combine(tempRoot, "sessions");
        var workingDirectory = Path.Combine(tempRoot, "comfy");
        var entryPointPath = Path.Combine(tempRoot, "entrypoint.sh");
        var stateDirectory = Path.Combine(tempRoot, "aria2-state");
        var downloadDirectory = Path.Combine(tempRoot, "models");
        var shouldCreateSeedVirtualEnv = seedVirtualEnvPath is null;
        seedVirtualEnvPath ??= Path.Combine(tempRoot, "seed-venv");

        Directory.CreateDirectory(sessionRoot);
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(stateDirectory);
        Directory.CreateDirectory(downloadDirectory);
        if (shouldCreateSeedVirtualEnv)
        {
            CreateFakeSeedVirtualEnv(seedVirtualEnvPath);
        }
        File.WriteAllText(entryPointPath, "#!/bin/sh\n");

        return new RunnerAgentOptions
        {
            RunnerSecret = runnerSecret,
            OffloadrApiUrl = offloadrApiUrl,
            TraceGrpcHttp = false,
            RunnerId = runnerId ?? Guid.NewGuid().ToString(),
            RegistrationMinBackoff = TimeSpan.FromSeconds(1),
            RegistrationMaxBackoff = TimeSpan.FromSeconds(2),
            RegistrationRpcTimeout = TimeSpan.FromSeconds(10),
            ModelFetchSocket = Path.Combine(tempRoot, "ipc", "model-fetch.sock"),
            LocalModelsDirectory = localModelsDirectory,
            LocalModelsScanInterval = TimeSpan.FromSeconds(15),
            RuntimeTelemetryInterval = TimeSpan.FromSeconds(5),
            Session = new SessionProcessOptions
            {
                SessionRoot = sessionRoot,
                EntryPointPath = entryPointPath,
                WorkingDirectory = workingDirectory,
                BundledCustomNodesSeedPath = Path.Combine(tempRoot, "seed-custom_nodes"),
                BundledInputSeedPath = Path.Combine(tempRoot, "seed-input"),
                SeedVirtualEnvPath = seedVirtualEnvPath,
                UvBinaryPath = uvBinaryPath ?? ariaBinaryPath,
                ComfyPort = 8188,
                ShutdownGracePeriod = TimeSpan.FromSeconds(20),
                ReadyTimeout = TimeSpan.FromSeconds(120),
                ReadyHost = "127.0.0.1"
            },
            Aria2 = Aria2Settings.FromEnvironment(name => name switch
            {
                "ARIA2_BINARY" => ariaBinaryPath,
                "ARIA2_DOWNLOAD_DIR" => downloadDirectory,
                "ARIA2_STATE_DIR" => stateDirectory,
                _ => null
            })
        };
    }

    private static void CreateFakeSeedVirtualEnv(string seedVirtualEnvPath)
    {
        var seedBinDirectory = Path.Combine(seedVirtualEnvPath, "bin");
        var seedSitePackagesDirectory = Path.Combine(seedVirtualEnvPath, "lib", "python3.12", "site-packages");
        Directory.CreateDirectory(seedBinDirectory);
        Directory.CreateDirectory(seedSitePackagesDirectory);
        File.WriteAllText(Path.Combine(seedBinDirectory, "python"), "#!/bin/sh\nexit 0\n");
        File.WriteAllText(Path.Combine(seedBinDirectory, "uv"), "#!/bin/sh\nexit 0\n");
        MakeSeedDirectoryNonWritableBySessions(seedVirtualEnvPath);
        MakeSeedDirectoryNonWritableBySessions(seedBinDirectory);
        MakeSeedDirectoryNonWritableBySessions(seedSitePackagesDirectory);
        MakeSeedFileNonWritableBySessions(Path.Combine(seedBinDirectory, "python"));
        MakeSeedFileNonWritableBySessions(Path.Combine(seedBinDirectory, "uv"));
    }

    private static void MakeSeedDirectoryNonWritableBySessions(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static void MakeSeedDirectoryWritableBySessions(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static void MakeSeedFileWritableBySessions(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
    }

    private static void MakeSeedFileNonWritableBySessions(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead |
            UnixFileMode.OtherRead);
    }

    private sealed class NoopIsolationStrategy : ISessionIsolationStrategy
    {
        public void ValidatePrerequisites(ICollection<string> errors)
        {
        }

        public Task<PreparedSessionIdentity> PrepareAsync(string sessionId, string sessionRoot, CancellationToken cancellationToken)
        {
            return Task.FromResult(new PreparedSessionIdentity("sess_test", Path.Combine(sessionRoot, sessionId), CleanupIdentity: false));
        }

        public Task CleanupAsync(PreparedSessionIdentity identity, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup in tests.
        }
    }
}
