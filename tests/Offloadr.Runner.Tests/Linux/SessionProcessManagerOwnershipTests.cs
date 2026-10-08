using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public partial class SessionProcessManagerOwnershipTests
{
    [Test]
    public async Task StartSessionAsync_CopiesBundledInputSeedsBeforeInitializerCreatesInputPlaceholders()
    {
        LinuxTestPrerequisites.RequireLinux();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-session-input-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var customNodesSeedPath = Path.Combine(tempRoot, "seed-custom_nodes");
        var inputSeedPath = Path.Combine(tempRoot, "seed-input");
        Directory.CreateDirectory(customNodesSeedPath);
        Directory.CreateDirectory(Path.Combine(inputSeedPath, "nested"));
        File.WriteAllText(Path.Combine(inputSeedPath, "nested", "seed.png"), "seed");

        var options = new SessionProcessOptions
        {
            SessionRoot = Path.Combine(tempRoot, "sessions"),
            EntryPointPath = Path.Combine(tempRoot, "missing-entrypoint.sh"),
            WorkingDirectory = tempRoot,
            BundledCustomNodesSeedPath = customNodesSeedPath,
            BundledInputSeedPath = inputSeedPath,
            SeedVirtualEnvPath = CreateEmptySeedVirtualEnv(tempRoot),
            UvBinaryPath = FakeUvBinaryPath(tempRoot),
            ComfyPort = 8188,
            ShutdownGracePeriod = TimeSpan.FromSeconds(5),
            ReadyTimeout = TimeSpan.FromSeconds(5),
            ReadyHost = "127.0.0.1"
        };

        const string sessionId = "session-input-seed";
        var homeDirectory = Path.Combine(options.SessionRoot, sessionId);
        var isolation = new RecordingIsolationStrategy("sess_seed", homeDirectory);
        var sawCopiedInputSeed = false;

        try
        {
            using var manager = new SessionProcessManager(options, isolation, new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));

            Exception? launchError = null;
            try
            {
                await manager.StartSessionAsync(
                    new StartSessionCommand { SessionId = sessionId, User = "user-1", LifecycleGeneration = 1, RuntimeEpoch = 1, RuntimeInstanceId = "33333333333343338333333333333333" },
                    (paths, _) =>
                    {
                        using var placeholder = File.Create(Path.Combine(paths.InputDirectory, "uploaded-placeholder.png"));
                        return Task.CompletedTask;
                    },
                    (paths, _) =>
                    {
                        sawCopiedInputSeed = File.Exists(Path.Combine(paths.InputDirectory, "nested", "seed.png"));
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                launchError = ex;
            }

            Assert.That(launchError, Is.Not.Null);
            Assert.That(sawCopiedInputSeed, Is.True);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task StartSessionAsync_DoesNotCopyBundledCustomNodesWhenInitializerHydratesCustomNodes()
    {
        LinuxTestPrerequisites.RequireLinux();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-session-custom-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var customNodesSeedPath = Path.Combine(tempRoot, "seed-custom_nodes");
        var inputSeedPath = Path.Combine(tempRoot, "seed-input");
        Directory.CreateDirectory(customNodesSeedPath);
        Directory.CreateDirectory(inputSeedPath);
        File.WriteAllText(Path.Combine(customNodesSeedPath, "bundled-node.py"), "bundled");

        var options = new SessionProcessOptions
        {
            SessionRoot = Path.Combine(tempRoot, "sessions"),
            EntryPointPath = Path.Combine(tempRoot, "missing-entrypoint.sh"),
            WorkingDirectory = tempRoot,
            BundledCustomNodesSeedPath = customNodesSeedPath,
            BundledInputSeedPath = inputSeedPath,
            SeedVirtualEnvPath = CreateEmptySeedVirtualEnv(tempRoot),
            UvBinaryPath = FakeUvBinaryPath(tempRoot),
            ComfyPort = 8188,
            ShutdownGracePeriod = TimeSpan.FromSeconds(5),
            ReadyTimeout = TimeSpan.FromSeconds(5),
            ReadyHost = "127.0.0.1"
        };

        const string sessionId = "session-custom-seed";
        var homeDirectory = Path.Combine(options.SessionRoot, sessionId);
        var isolation = new RecordingIsolationStrategy("sess_custom", homeDirectory);
        var sawOwnerCustomNode = false;
        var sawBundledCustomNode = false;

        try
        {
            using var manager = new SessionProcessManager(options, isolation, new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));

            Exception? launchError = null;
            try
            {
                await manager.StartSessionAsync(
                    new StartSessionCommand { SessionId = sessionId, User = "user-1", LifecycleGeneration = 1, RuntimeEpoch = 1, RuntimeInstanceId = "33333333333343338333333333333333" },
                    (paths, _) =>
                    {
                        File.WriteAllText(Path.Combine(paths.CustomNodesDirectory, "owner-node.py"), "owner");
                        return Task.CompletedTask;
                    },
                    (paths, _) =>
                    {
                        sawOwnerCustomNode = File.Exists(Path.Combine(paths.CustomNodesDirectory, "owner-node.py"));
                        sawBundledCustomNode = File.Exists(Path.Combine(paths.CustomNodesDirectory, "bundled-node.py"));
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                launchError = ex;
            }

            Assert.That(launchError, Is.Not.Null);
            Assert.That(sawOwnerCustomNode, Is.True);
            Assert.That(sawBundledCustomNode, Is.False);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task StartSessionAsync_ReappliesOwnershipAfterInitializerCreatesSeededPaths()
    {
        LinuxTestPrerequisites.RequireLinux();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-session-own-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var customNodesSeedPath = Path.Combine(tempRoot, "seed-custom_nodes");
        var inputSeedPath = Path.Combine(tempRoot, "seed-input");
        Directory.CreateDirectory(customNodesSeedPath);
        Directory.CreateDirectory(inputSeedPath);
        File.WriteAllText(Path.Combine(customNodesSeedPath, "seeded-node.py"), "print('ok')");

        var options = new SessionProcessOptions
        {
            SessionRoot = Path.Combine(tempRoot, "sessions"),
            EntryPointPath = Path.Combine(tempRoot, "missing-entrypoint.sh"),
            WorkingDirectory = tempRoot,
            BundledCustomNodesSeedPath = customNodesSeedPath,
            BundledInputSeedPath = inputSeedPath,
            SeedVirtualEnvPath = CreateEmptySeedVirtualEnv(tempRoot),
            UvBinaryPath = FakeUvBinaryPath(tempRoot),
            ComfyPort = 8188,
            ShutdownGracePeriod = TimeSpan.FromSeconds(5),
            ReadyTimeout = TimeSpan.FromSeconds(5),
            ReadyHost = "127.0.0.1"
        };

        const string sessionId = "session-abc";
        const string userName = "sess_test";
        var homeDirectory = Path.Combine(options.SessionRoot, sessionId);
        var seededPath = Path.Combine(homeDirectory, "output", "seeded", "artifact.png");

        var isolation = new RecordingIsolationStrategy(userName, homeDirectory);
        var commandRunner = new RecordingCommandRunner(seededPath);

        try
        {
            using var manager = new SessionProcessManager(options, isolation, new RunnerVfsEnvironmentBuilder(), commandRunner);

            Exception? launchError = null;
            try
            {
                await manager.StartSessionAsync(
                    new StartSessionCommand { SessionId = sessionId, User = "user-1", LifecycleGeneration = 1, RuntimeEpoch = 1, RuntimeInstanceId = "33333333333343338333333333333333" },
                    async (paths, _) =>
                    {
                        var seededDirectory = Path.Combine(paths.OutputDirectory, "seeded");
                        Directory.CreateDirectory(seededDirectory);
                        await File.WriteAllBytesAsync(Path.Combine(seededDirectory, "artifact.png"), [1, 2, 3]).ConfigureAwait(false);
                    },
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                launchError = ex;
            }

            Assert.That(launchError, Is.Not.Null);

            Assert.That(commandRunner.Commands.Count, Is.EqualTo(1));
            var chown = commandRunner.Commands[0];
            Assert.That(chown.FileName, Is.EqualTo("/bin/chown"));
            Assert.That(chown.Arguments, Is.EqualTo(new[] { "-R", "-h", $"{userName}:{userName}", homeDirectory }));
            Assert.That(commandRunner.SeededPathExistedAtChown, Is.True);

            Assert.That(isolation.CleanupCallCount, Is.EqualTo(1));
            Assert.That(Directory.Exists(homeDirectory), Is.False);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task StartSessionAsync_PreparesSessionLocalModelsRootForBaseDirectory()
    {
        LinuxTestPrerequisites.RequireLinux();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-session-models-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var customNodesSeedPath = Path.Combine(tempRoot, "seed-custom_nodes");
        var inputSeedPath = Path.Combine(tempRoot, "seed-input");
        Directory.CreateDirectory(customNodesSeedPath);
        Directory.CreateDirectory(inputSeedPath);

        var options = new SessionProcessOptions
        {
            SessionRoot = Path.Combine(tempRoot, "sessions"),
            EntryPointPath = Path.Combine(tempRoot, "missing-entrypoint.sh"),
            WorkingDirectory = tempRoot,
            BundledCustomNodesSeedPath = customNodesSeedPath,
            BundledInputSeedPath = inputSeedPath,
            SeedVirtualEnvPath = CreateEmptySeedVirtualEnv(tempRoot),
            UvBinaryPath = FakeUvBinaryPath(tempRoot),
            ComfyPort = 8188,
            ShutdownGracePeriod = TimeSpan.FromSeconds(5),
            ReadyTimeout = TimeSpan.FromSeconds(5),
            ReadyHost = "127.0.0.1"
        };

        const string sessionId = "session-models";
        var homeDirectory = Path.Combine(options.SessionRoot, sessionId);
        var isolation = new RecordingIsolationStrategy("sess_models", homeDirectory);

        string? modelsLinkTarget = null;

        try
        {
            using var manager = new SessionProcessManager(options, isolation, new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));

            Exception? launchError = null;
            try
            {
                await manager.StartSessionAsync(
                    new StartSessionCommand { SessionId = sessionId, User = "user-1", LifecycleGeneration = 1, RuntimeEpoch = 1, RuntimeInstanceId = "33333333333343338333333333333333" },
                    (_, _) =>
                    {
                        var modelsDirectory = Path.Combine(homeDirectory, "models");
                        modelsLinkTarget = Directory.ResolveLinkTarget(modelsDirectory, returnFinalTarget: false)?.FullName;
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                launchError = ex;
            }

            Assert.That(launchError, Is.Not.Null);
            Assert.That(modelsLinkTarget, Is.EqualTo("/comfyui/models"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task StartSessionAsync_ForgeNeoDoesNotCreateModelShimLoop()
    {
        LinuxTestPrerequisites.RequireLinux();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-session-forge-models-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var customNodesSeedPath = Path.Combine(tempRoot, "seed-custom_nodes");
        var inputSeedPath = Path.Combine(tempRoot, "seed-input");
        Directory.CreateDirectory(customNodesSeedPath);
        Directory.CreateDirectory(inputSeedPath);

        var options = new SessionProcessOptions
        {
            SessionRoot = Path.Combine(tempRoot, "sessions"),
            EntryPointPath = Path.Combine(tempRoot, "missing-entrypoint.sh"),
            WorkingDirectory = tempRoot,
            BundledCustomNodesSeedPath = customNodesSeedPath,
            BundledInputSeedPath = inputSeedPath,
            SeedVirtualEnvPath = CreateEmptySeedVirtualEnv(tempRoot),
            UvBinaryPath = FakeUvBinaryPath(tempRoot),
            RuntimeKind = "forge-neo",
            ComfyPort = 7860,
            ShutdownGracePeriod = TimeSpan.FromSeconds(5),
            ReadyTimeout = TimeSpan.FromSeconds(5),
            ReadyHost = "127.0.0.1"
        };

        const string sessionId = "session-forge-models";
        var homeDirectory = Path.Combine(options.SessionRoot, sessionId);
        var isolation = new RecordingIsolationStrategy("sess_forge_models", homeDirectory);

        string? sessionModelsLinkTarget = null;
        var sessionModelsDirectoryExists = false;

        try
        {
            using var manager = new SessionProcessManager(options, isolation, new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));

            Exception? launchError = null;
            try
            {
                await manager.StartSessionAsync(
                    new StartSessionCommand { SessionId = sessionId, User = "user-1", LifecycleGeneration = 1, RuntimeEpoch = 1, RuntimeInstanceId = "33333333333343338333333333333333" },
                    (paths, _) =>
                    {
                        sessionModelsLinkTarget = Directory.ResolveLinkTarget(paths.ModelsDirectory, returnFinalTarget: false)?.FullName;
                        sessionModelsDirectoryExists = Directory.Exists(paths.ModelsDirectory);
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                launchError = ex;
            }

            Assert.That(launchError, Is.Not.Null);
            Assert.That(sessionModelsLinkTarget, Is.Null);
            Assert.That(sessionModelsDirectoryExists, Is.True);
            Assert.That(Directory.ResolveLinkTarget(Path.Combine(tempRoot, "extensions"), returnFinalTarget: false)?.FullName, Is.EqualTo(Path.Combine(homeDirectory, "custom_nodes")));
            Assert.That(Directory.ResolveLinkTarget(Path.Combine(tempRoot, "output"), returnFinalTarget: false)?.FullName, Is.EqualTo(Path.Combine(homeDirectory, "output")));
            Assert.That(Directory.ResolveLinkTarget(Path.Combine(tempRoot, "cache"), returnFinalTarget: false)?.FullName, Is.EqualTo(Path.Combine(homeDirectory, ".cache")));
            Assert.That(
                Directory.EnumerateFileSystemEntries(tempRoot).Select(Path.GetFileName),
                Does.Not.Contain("models"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void BuildRuntimeArguments_ForgeNeoUsesDataDirAndModelRef()
    {
        var paths = new SessionProcessManager.SessionPaths
        {
            HomeDirectory = "/sessions/session-forge",
            ModelsDirectory = "/sessions/session-forge/models"
        };

        var args = SessionProcessManager.BuildRuntimeArguments(
            "forge-neo",
            7860,
            paths,
            "/sessions/session-forge/models");

        Assert.That(
            args,
            Is.EqualTo(new[]
            {
                "--port",
                "7860",
                "--api",
                "--data-dir",
                "/sessions/session-forge",
                "--model-ref",
                "/sessions/session-forge/models"
            }));
    }

    [Test]
    public async Task StartSessionAsync_SeparatesComfyTempFromProcessScratch()
    {
        LinuxTestPrerequisites.RequireLinux();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-session-temp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var customNodesSeedPath = Path.Combine(tempRoot, "seed-custom_nodes");
        var inputSeedPath = Path.Combine(tempRoot, "seed-input");
        Directory.CreateDirectory(customNodesSeedPath);
        Directory.CreateDirectory(inputSeedPath);

        var options = new SessionProcessOptions
        {
            SessionRoot = Path.Combine(tempRoot, "sessions"),
            EntryPointPath = Path.Combine(tempRoot, "missing-entrypoint.sh"),
            WorkingDirectory = tempRoot,
            BundledCustomNodesSeedPath = customNodesSeedPath,
            BundledInputSeedPath = inputSeedPath,
            SeedVirtualEnvPath = CreateEmptySeedVirtualEnv(tempRoot),
            UvBinaryPath = FakeUvBinaryPath(tempRoot),
            ComfyPort = 8188,
            ShutdownGracePeriod = TimeSpan.FromSeconds(5),
            ReadyTimeout = TimeSpan.FromSeconds(5),
            ReadyHost = "127.0.0.1"
        };

        const string sessionId = "session-temp";
        var homeDirectory = Path.Combine(options.SessionRoot, sessionId);
        var isolation = new RecordingIsolationStrategy("sess_temp", homeDirectory);

        string? comfyTempDirectory = null;
        string? scratchDirectory = null;
        string? virtualEnvDirectory = null;
        string? uvCacheDirectory = null;
        string? torchInductorCacheDirectory = null;

        try
        {
            using var manager = new SessionProcessManager(options, isolation, new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));

            Exception? launchError = null;
            try
            {
                await manager.StartSessionAsync(
                    new StartSessionCommand { SessionId = sessionId, User = "user-1", LifecycleGeneration = 1, RuntimeEpoch = 1, RuntimeInstanceId = "33333333333343338333333333333333" },
                    (paths, _) =>
                    {
                        comfyTempDirectory = paths.TempDirectory;
                        scratchDirectory = paths.ScratchDirectory;
                        virtualEnvDirectory = paths.VirtualEnvDirectory;
                        uvCacheDirectory = paths.UvCacheDirectory;
                        torchInductorCacheDirectory = paths.TorchInductorCacheDirectory;
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                launchError = ex;
            }

            Assert.That(launchError, Is.Not.Null);
            Assert.That(comfyTempDirectory, Is.EqualTo(Path.Combine(homeDirectory, "temp")));
            Assert.That(scratchDirectory, Is.EqualTo(Path.Combine(homeDirectory, "tmp")));
            Assert.That(virtualEnvDirectory, Is.EqualTo(Path.Combine(homeDirectory, ".venv")));
            Assert.That(uvCacheDirectory, Is.EqualTo(Path.Combine(homeDirectory, ".cache", "uv")));
            Assert.That(torchInductorCacheDirectory, Is.EqualTo(Path.Combine(homeDirectory, "tmp", "torchinductor")));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task StartSessionAsync_MaterializesSeedVirtualEnvOverlayIntoSessionVirtualEnv()
    {
        LinuxTestPrerequisites.RequireLinux();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-session-seed-venv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var customNodesSeedPath = Path.Combine(tempRoot, "seed-custom_nodes");
        var inputSeedPath = Path.Combine(tempRoot, "seed-input");
        Directory.CreateDirectory(customNodesSeedPath);
        Directory.CreateDirectory(inputSeedPath);
        var seedVirtualEnvPath = CreateEmptySeedVirtualEnv(tempRoot);
        var seedSitePackages = Path.Combine(seedVirtualEnvPath, "lib", "python3.12", "site-packages");
        var seedPackageDirectory = Path.Combine(seedSitePackages, "heavy_pkg");
        var seedNestedDirectory = Path.Combine(seedPackageDirectory, "sub");
        var seedMetadataDirectory = Path.Combine(seedSitePackages, "heavy_pkg-1.2.3.dist-info");
        Directory.CreateDirectory(seedNestedDirectory);
        Directory.CreateDirectory(seedMetadataDirectory);
        File.WriteAllText(Path.Combine(seedPackageDirectory, "__init__.py"), "VALUE = 42");
        File.WriteAllText(Path.Combine(seedNestedDirectory, "data.txt"), "seed-data");
        File.WriteAllText(
            Path.Combine(seedMetadataDirectory, "METADATA"),
            """
            Metadata-Version: 2.1
            Name: heavy-pkg
            Version: 1.2.3
            """);
        File.WriteAllText(
            Path.Combine(seedMetadataDirectory, "RECORD"),
            """
            heavy_pkg/__init__.py,,
            heavy_pkg/sub/data.txt,,
            heavy_pkg-1.2.3.dist-info/METADATA,,
            heavy_pkg-1.2.3.dist-info/RECORD,,
            """);

        var options = new SessionProcessOptions
        {
            SessionRoot = Path.Combine(tempRoot, "sessions"),
            EntryPointPath = Path.Combine(tempRoot, "missing-entrypoint.sh"),
            WorkingDirectory = tempRoot,
            BundledCustomNodesSeedPath = customNodesSeedPath,
            BundledInputSeedPath = inputSeedPath,
            SeedVirtualEnvPath = seedVirtualEnvPath,
            UvBinaryPath = Path.Combine(seedVirtualEnvPath, "bin", "uv"),
            ComfyPort = 8188,
            ShutdownGracePeriod = TimeSpan.FromSeconds(5),
            ReadyTimeout = TimeSpan.FromSeconds(5),
            ReadyHost = "127.0.0.1"
        };

        const string sessionId = "session-seed-venv";
        var homeDirectory = Path.Combine(options.SessionRoot, sessionId);
        var isolation = new RecordingIsolationStrategy("sess_seed", homeDirectory);

        var sessionVenvIsDirectory = false;
        var metadataWasCopied = false;
        var packageDirectoryLinksToSeed = false;
        var seedPayloadIsVisible = false;
        var sessionPipWasMaterialized = false;
        var sessionUvWasMaterialized = false;

        try
        {
            using var manager = new SessionProcessManager(options, isolation, new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));

            Exception? launchError = null;
            try
            {
                await manager.StartSessionAsync(
                    new StartSessionCommand { SessionId = sessionId, User = "user-1", LifecycleGeneration = 1, RuntimeEpoch = 1, RuntimeInstanceId = "33333333333343338333333333333333" },
                    (paths, _) =>
                    {
                        var sessionSitePackages = Path.Combine(paths.VirtualEnvDirectory, "lib", "python3.12", "site-packages");
                        var sessionPackageDirectory = Path.Combine(sessionSitePackages, "heavy_pkg");
                        var sessionMetadataDirectory = Path.Combine(sessionSitePackages, "heavy_pkg-1.2.3.dist-info");
                        var sessionInit = Path.Combine(sessionPackageDirectory, "__init__.py");
                        var sessionData = Path.Combine(sessionPackageDirectory, "sub", "data.txt");

                        sessionVenvIsDirectory = Directory.Exists(paths.VirtualEnvDirectory) &&
                            new DirectoryInfo(paths.VirtualEnvDirectory).LinkTarget is null;
                        metadataWasCopied = Directory.Exists(sessionMetadataDirectory) &&
                            new DirectoryInfo(sessionMetadataDirectory).LinkTarget is null &&
                            File.ReadAllText(Path.Combine(sessionMetadataDirectory, "METADATA")).Contains("Name: heavy-pkg", StringComparison.Ordinal);
                        packageDirectoryLinksToSeed =
                            string.Equals(Directory.ResolveLinkTarget(sessionPackageDirectory, returnFinalTarget: true)?.FullName, seedPackageDirectory, StringComparison.Ordinal);

                        seedPayloadIsVisible = File.ReadAllText(sessionInit) == "VALUE = 42" &&
                            File.ReadAllText(sessionData) == "seed-data";
                        sessionPipWasMaterialized = File.Exists(Path.Combine(paths.VirtualEnvDirectory, "bin", "pip"));
                        sessionUvWasMaterialized = File.Exists(Path.Combine(paths.VirtualEnvDirectory, "bin", "uv"));
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                launchError = ex;
            }

            Assert.That(launchError, Is.Not.Null);
            Assert.That(sessionVenvIsDirectory, Is.True);
            Assert.That(metadataWasCopied, Is.True);
            Assert.That(packageDirectoryLinksToSeed, Is.True);
            Assert.That(seedPayloadIsVisible, Is.True);
            Assert.That(sessionPipWasMaterialized, Is.True);
            Assert.That(sessionUvWasMaterialized, Is.True);
            Assert.That(File.Exists(Path.Combine(seedNestedDirectory, "data.txt")), Is.True);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void EnsureManagerConfig_ForcesUvAndOfflineModeWithoutDroppingOtherSettings()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-manager-config-{Guid.NewGuid():N}");
        var userDirectory = Path.Combine(tempRoot, "user");
        Directory.CreateDirectory(Path.Combine(userDirectory, "__manager"));
        var configPath = Path.Combine(userDirectory, "__manager", "config.ini");
        File.WriteAllText(
            configPath,
            """
            [default]
            use_uv = False
            some_other_setting = keep

            [extra]
            hello = world
            """);

        try
        {
            SessionProcessManager.EnsureManagerConfig(userDirectory);

            var content = File.ReadAllText(configPath);
            Assert.That(content, Does.Contain("[default]"));
            Assert.That(content, Does.Contain("use_uv = True"));
            Assert.That(content, Does.Contain("network_mode = offline"));
            Assert.That(content, Does.Contain("some_other_setting = keep"));
            Assert.That(content, Does.Contain("[extra]"));
            Assert.That(content, Does.Contain("hello = world"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void TryDetachSeedPayloadForFailedUvRemoval_RemovesSessionSeedLinksForRetry()
    {
        LinuxTestPrerequisites.RequireLinux();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-detach-seed-payload-{Guid.NewGuid():N}");
        var seedSitePackages = Path.Combine(tempRoot, "seed", "lib", "python3.12", "site-packages");
        var sessionVenv = Path.Combine(tempRoot, "session", ".venv");
        var sessionSitePackages = Path.Combine(sessionVenv, "lib", "python3.12", "site-packages");
        var seedPackageDirectory = Path.Combine(seedSitePackages, "PIL");
        var seedFile = Path.Combine(seedPackageDirectory, "AvifImagePlugin.py");
        var sessionPackageDirectory = Path.Combine(sessionSitePackages, "PIL");
        var sessionMetadataDirectory = Path.Combine(sessionSitePackages, "pillow-11.0.0.dist-info");
        var sessionEggInfoDirectory = Path.Combine(sessionSitePackages, "pillow-11.0.0.egg-info");
        var sessionEggInfoFile = Path.Combine(sessionSitePackages, "PIL.egg-info");
        var unrelatedEggInfoDirectory = Path.Combine(sessionSitePackages, "unrelated.egg-info");

        try
        {
            Directory.CreateDirectory(seedPackageDirectory);
            Directory.CreateDirectory(sessionSitePackages);
            Directory.CreateDirectory(sessionMetadataDirectory);
            Directory.CreateDirectory(sessionEggInfoDirectory);
            Directory.CreateDirectory(unrelatedEggInfoDirectory);
            File.WriteAllText(seedFile, "# seed");
            File.WriteAllText(Path.Combine(seedPackageDirectory, "__init__.py"), "# package");
            Directory.CreateSymbolicLink(sessionPackageDirectory, seedPackageDirectory);
            File.WriteAllText(
                Path.Combine(sessionMetadataDirectory, "RECORD"),
                """
                PIL/AvifImagePlugin.py,,
                pillow-11.0.0.dist-info/METADATA,,
                pillow-11.0.0.dist-info/RECORD,,
                """);
            File.WriteAllText(Path.Combine(sessionEggInfoDirectory, "top_level.txt"), "PIL\n");
            File.WriteAllText(sessionEggInfoFile, "Metadata-Version: 2.1\n");
            File.WriteAllText(Path.Combine(unrelatedEggInfoDirectory, "top_level.txt"), "unrelated\n");

            var paths = new SessionProcessManager.SessionPaths
            {
                VirtualEnvDirectory = sessionVenv
            };
            var output = $"error: failed to remove file{Environment.NewLine}`{Path.Combine(sessionPackageDirectory, "AvifImagePlugin.py")}`: Permission denied";

            Assert.That(
                SessionProcessManager.TryDetachSeedPayloadForFailedUvRemoval(paths, $"`{Path.Combine(sessionPackageDirectory, "AvifImagePlugin.py")}`: some other error"),
                Is.False);

            var detached = SessionProcessManager.TryDetachSeedPayloadForFailedUvRemoval(paths, output);

            Assert.That(detached, Is.True);
            Assert.That(Directory.Exists(sessionPackageDirectory), Is.False);
            Assert.That(Directory.Exists(sessionMetadataDirectory), Is.False);
            Assert.That(Directory.Exists(sessionEggInfoDirectory), Is.False);
            Assert.That(File.Exists(sessionEggInfoFile), Is.False);
            Assert.That(Directory.Exists(unrelatedEggInfoDirectory), Is.True);
            Assert.That(File.Exists(seedFile), Is.True);
            Assert.That(File.ReadAllText(seedFile), Is.EqualTo("# seed"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void TryDetachSeedPayloadForFailedUvRemoval_PreservesUnrelatedNamespaceMetadata()
    {
        LinuxTestPrerequisites.RequireLinux();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-detach-namespace-payload-{Guid.NewGuid():N}");
        var seedSitePackages = Path.Combine(tempRoot, "seed", "lib", "python3.12", "site-packages");
        var sessionVenv = Path.Combine(tempRoot, "session", ".venv");
        var sessionSitePackages = Path.Combine(sessionVenv, "lib", "python3.12", "site-packages");
        var seedNamespaceDirectory = Path.Combine(seedSitePackages, "google");
        var seedAuthFile = Path.Combine(seedNamespaceDirectory, "auth", "transport.py");
        var seedApiCoreFile = Path.Combine(seedNamespaceDirectory, "api_core", "client.py");
        var sessionNamespaceDirectory = Path.Combine(sessionSitePackages, "google");
        var authMetadataDirectory = Path.Combine(sessionSitePackages, "google_auth-2.0.0.dist-info");
        var authEggInfoDirectory = Path.Combine(sessionSitePackages, "google_auth-2.0.0.egg-info");
        var apiCoreMetadataDirectory = Path.Combine(sessionSitePackages, "google_api_core-2.0.0.dist-info");
        var namespaceEggInfoDirectory = Path.Combine(sessionSitePackages, "google.egg-info");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(seedAuthFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(seedApiCoreFile)!);
            Directory.CreateDirectory(sessionSitePackages);
            Directory.CreateDirectory(authMetadataDirectory);
            Directory.CreateDirectory(authEggInfoDirectory);
            Directory.CreateDirectory(apiCoreMetadataDirectory);
            Directory.CreateDirectory(namespaceEggInfoDirectory);
            File.WriteAllText(seedAuthFile, "# auth");
            File.WriteAllText(seedApiCoreFile, "# api core");
            Directory.CreateSymbolicLink(sessionNamespaceDirectory, seedNamespaceDirectory);
            File.WriteAllText(
                Path.Combine(authMetadataDirectory, "RECORD"),
                """
                google/auth/transport.py,,
                google_auth-2.0.0.dist-info/METADATA,,
                google_auth-2.0.0.dist-info/RECORD,,
                """);
            File.WriteAllText(Path.Combine(authEggInfoDirectory, "SOURCES.txt"), "google/auth/transport.py\n");
            File.WriteAllText(
                Path.Combine(apiCoreMetadataDirectory, "RECORD"),
                """
                google/api_core/client.py,,
                google_api_core-2.0.0.dist-info/METADATA,,
                google_api_core-2.0.0.dist-info/RECORD,,
                """);
            File.WriteAllText(Path.Combine(namespaceEggInfoDirectory, "top_level.txt"), "google\n");

            var paths = new SessionProcessManager.SessionPaths
            {
                VirtualEnvDirectory = sessionVenv
            };
            var output = $"error: failed to remove file{Environment.NewLine}`{Path.Combine(sessionNamespaceDirectory, "auth", "transport.py")}`: Permission denied";

            var detached = SessionProcessManager.TryDetachSeedPayloadForFailedUvRemoval(paths, output);

            Assert.That(detached, Is.True);
            Assert.That(Directory.Exists(sessionNamespaceDirectory), Is.True);
            Assert.That(new DirectoryInfo(sessionNamespaceDirectory).LinkTarget, Is.Null);
            Assert.That(Directory.Exists(Path.Combine(sessionNamespaceDirectory, "auth")), Is.False);
            Assert.That(Directory.Exists(Path.Combine(sessionNamespaceDirectory, "api_core")), Is.True);
            Assert.That(Directory.ResolveLinkTarget(Path.Combine(sessionNamespaceDirectory, "api_core"), returnFinalTarget: true)?.FullName, Is.EqualTo(Path.GetDirectoryName(seedApiCoreFile)));
            Assert.That(Directory.Exists(authMetadataDirectory), Is.False);
            Assert.That(Directory.Exists(authEggInfoDirectory), Is.False);
            Assert.That(Directory.Exists(apiCoreMetadataDirectory), Is.True);
            Assert.That(Directory.Exists(namespaceEggInfoDirectory), Is.True);
            Assert.That(File.Exists(seedAuthFile), Is.True);
            Assert.That(File.Exists(seedApiCoreFile), Is.True);

            var secondOutput = $"error: failed to remove file{Environment.NewLine}`{Path.Combine(sessionNamespaceDirectory, "api_core", "client.py")}`: Permission denied";
            var secondDetached = SessionProcessManager.TryDetachSeedPayloadForFailedUvRemoval(paths, secondOutput);

            Assert.That(secondDetached, Is.True);
            Assert.That(Directory.Exists(sessionNamespaceDirectory), Is.True);
            Assert.That(new DirectoryInfo(sessionNamespaceDirectory).LinkTarget, Is.Null);
            Assert.That(Directory.Exists(Path.Combine(sessionNamespaceDirectory, "api_core")), Is.False);
            Assert.That(Directory.Exists(apiCoreMetadataDirectory), Is.False);
            Assert.That(Directory.Exists(namespaceEggInfoDirectory), Is.True);
            Assert.That(File.Exists(seedApiCoreFile), Is.True);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task TryRunManagerUvSyncAsync_RestoresDetachedSeedPayloadWhenRetryFails()
    {
        LinuxTestPrerequisites.RequireLinux();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-manager-sync-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var options = new SessionProcessOptions
        {
            SessionRoot = Path.Combine(tempRoot, "sessions"),
            EntryPointPath = Path.Combine(tempRoot, "entrypoint.base.sh"),
            WorkingDirectory = tempRoot,
            BundledCustomNodesSeedPath = Path.Combine(tempRoot, "seed-custom_nodes"),
            BundledInputSeedPath = Path.Combine(tempRoot, "seed-input"),
            SeedVirtualEnvPath = CreateEmptySeedVirtualEnv(tempRoot),
            UvBinaryPath = FakeUvBinaryPath(tempRoot),
            ComfyPort = 8188,
            ShutdownGracePeriod = TimeSpan.FromSeconds(5),
            ReadyTimeout = TimeSpan.FromSeconds(5),
            ReadyHost = "127.0.0.1"
        };
        var sessionVenv = Path.Combine(tempRoot, "session", ".venv");
        var sessionSitePackages = Path.Combine(sessionVenv, "lib", "python3.12", "site-packages");
        var seedSitePackages = Path.Combine(tempRoot, "seed", "lib", "python3.12", "site-packages");
        var seedPackageDirectory = Path.Combine(seedSitePackages, "PIL");
        var seedFile = Path.Combine(seedPackageDirectory, "AvifImagePlugin.py");
        var sessionPackageDirectory = Path.Combine(sessionSitePackages, "PIL");
        var sessionMetadataDirectory = Path.Combine(sessionSitePackages, "pillow-11.0.0.dist-info");
        var pythonPath = Path.Combine(sessionVenv, "bin", "python");
        var attemptFile = Path.Combine(tempRoot, "uv-sync-attempt");
        var failedPath = Path.Combine(sessionPackageDirectory, "AvifImagePlugin.py");
        // The fake shell helper runs as the test account, without changing Unix credentials.
        var identity = new PreparedSessionIdentity(string.Empty, Path.Combine(tempRoot, "session"), CleanupIdentity: true);
        var paths = new SessionProcessManager.SessionPaths
        {
            HomeDirectory = identity.HomeDirectory,
            VirtualEnvDirectory = sessionVenv,
            UserDirectory = Path.Combine(identity.HomeDirectory, "user"),
            CacheDirectory = Path.Combine(identity.HomeDirectory, ".cache"),
            UvCacheDirectory = Path.Combine(identity.HomeDirectory, ".cache", "uv"),
            ScratchDirectory = Path.Combine(identity.HomeDirectory, "tmp"),
            TorchInductorCacheDirectory = Path.Combine(identity.HomeDirectory, "tmp", "torchinductor")
        };
        var commandRunner = new RecordingCommandRunner(string.Empty);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pythonPath)!);
            Directory.CreateDirectory(seedPackageDirectory);
            Directory.CreateDirectory(sessionSitePackages);
            Directory.CreateDirectory(sessionMetadataDirectory);
            File.WriteAllText(seedFile, "# seed");
            File.WriteAllText(Path.Combine(seedPackageDirectory, "__init__.py"), "# package");
            Directory.CreateSymbolicLink(sessionPackageDirectory, seedPackageDirectory);
            File.WriteAllText(
                Path.Combine(sessionMetadataDirectory, "RECORD"),
                """
                PIL/AvifImagePlugin.py,,
                pillow-11.0.0.dist-info/METADATA,,
                pillow-11.0.0.dist-info/RECORD,,
                """);
            File.WriteAllText(
                pythonPath,
                $$"""
                #!/bin/sh
                count=0
                if [ -f "{{attemptFile}}" ]; then
                  count=$(cat "{{attemptFile}}")
                fi
                count=$((count + 1))
                printf '%s' "$count" > "{{attemptFile}}"
                if [ "$count" -eq 1 ]; then
                  printf '%s\n' 'error: failed to remove file' >&2
                  printf '%s\n' '`{{failedPath}}`: Permission denied' >&2
                  exit 1
                fi
                printf '%s\n' 'resolver failed after detach' >&2
                exit 2
                """);
            MakeExecutable(pythonPath);

            using var manager = new SessionProcessManager(options, new RecordingIsolationStrategy(identity.UserName, identity.HomeDirectory), new RunnerVfsEnvironmentBuilder(), commandRunner);

            var synced = await manager.TryRunManagerUvSyncAsync("session-sync-rollback", identity, paths, CancellationToken.None);

            Assert.That(synced, Is.False);
            Assert.That(File.ReadAllText(attemptFile), Is.EqualTo("2"));
            Assert.That(commandRunner.Commands, Has.Count.EqualTo(1));
            Assert.That(Directory.Exists(sessionPackageDirectory), Is.True);
            Assert.That(Directory.ResolveLinkTarget(sessionPackageDirectory, returnFinalTarget: true)?.FullName, Is.EqualTo(seedPackageDirectory));
            Assert.That(Directory.Exists(sessionMetadataDirectory), Is.True);
            Assert.That(File.Exists(Path.Combine(sessionMetadataDirectory, "RECORD")), Is.True);
            Assert.That(File.Exists(seedFile), Is.True);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void BuildSessionEnvironment_UsesSessionLocalRuntimeCachePaths()
    {
        const string sessionId = "session-env";
        const string userName = "sess_env";
        const string homeDirectory = "/sessions/session-env";
        var identity = new PreparedSessionIdentity(userName, homeDirectory, CleanupIdentity: true);
        var paths = new SessionProcessManager.SessionPaths
        {
            HomeDirectory = homeDirectory,
            VirtualEnvDirectory = Path.Combine(homeDirectory, ".venv"),
            CacheDirectory = Path.Combine(homeDirectory, ".cache"),
            UvCacheDirectory = Path.Combine(homeDirectory, ".cache", "uv"),
            ScratchDirectory = Path.Combine(homeDirectory, "tmp"),
            TorchInductorCacheDirectory = Path.Combine(homeDirectory, "tmp", "torchinductor")
        };

        var env = SessionProcessManager.BuildSessionEnvironment(sessionId, identity, paths);

        Assert.That(env["HOME"], Is.EqualTo(homeDirectory));
        Assert.That(env["VIRTUAL_ENV"], Is.EqualTo(Path.Combine(homeDirectory, ".venv")));
        Assert.That(env["USER"], Is.EqualTo(userName));
        Assert.That(env["LOGNAME"], Is.EqualTo(userName));
        Assert.That(env["PATH"], Does.StartWith(Path.Combine(homeDirectory, ".venv", "bin") + Path.PathSeparator));
        Assert.That(env["XDG_CACHE_HOME"], Is.EqualTo(Path.Combine(homeDirectory, ".cache")));
        Assert.That(env["UV_CACHE_DIR"], Is.EqualTo(Path.Combine(homeDirectory, ".cache", "uv")));
        Assert.That(env["UV_PROJECT_ENVIRONMENT"], Is.EqualTo(Path.Combine(homeDirectory, ".venv")));
        Assert.That(env["TORCHINDUCTOR_CACHE_DIR"], Is.EqualTo(Path.Combine(homeDirectory, "tmp", "torchinductor")));
        Assert.That(env["TMPDIR"], Is.EqualTo(Path.Combine(homeDirectory, "tmp")));
        Assert.That(env["COMFYUI_SESSION_ID"], Is.EqualTo(sessionId));
        Assert.That(env["PYTHONUNBUFFERED"], Is.EqualTo("1"));
    }

    [Test]
    public void BuildManagerUvSyncStartInfo_TargetsSessionComfyPath()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-manager-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var options = new SessionProcessOptions
        {
            SessionRoot = Path.Combine(tempRoot, "sessions"),
            EntryPointPath = Path.Combine(tempRoot, "entrypoint.base.sh"),
            WorkingDirectory = tempRoot,
            BundledCustomNodesSeedPath = Path.Combine(tempRoot, "seed-custom_nodes"),
            BundledInputSeedPath = Path.Combine(tempRoot, "seed-input"),
            SeedVirtualEnvPath = CreateEmptySeedVirtualEnv(tempRoot),
            UvBinaryPath = FakeUvBinaryPath(tempRoot),
            ComfyPort = 8188,
            ShutdownGracePeriod = TimeSpan.FromSeconds(5),
            ReadyTimeout = TimeSpan.FromSeconds(5),
            ReadyHost = "127.0.0.1"
        };

        var identity = new PreparedSessionIdentity("sess_sync", "/sessions/session-sync", CleanupIdentity: true);
        var paths = new SessionProcessManager.SessionPaths
        {
            HomeDirectory = "/sessions/session-sync",
            UserDirectory = "/sessions/session-sync/user",
            CustomNodesDirectory = "/sessions/session-sync/custom_nodes",
            ModelsDirectory = "/sessions/session-sync/models",
            VirtualEnvDirectory = "/sessions/session-sync/.venv",
            OutputDirectory = "/sessions/session-sync/output",
            InputDirectory = "/sessions/session-sync/input",
            TempDirectory = "/sessions/session-sync/temp",
            ScratchDirectory = "/sessions/session-sync/tmp",
            CacheDirectory = "/sessions/session-sync/.cache",
            UvCacheDirectory = "/sessions/session-sync/.cache/uv",
            TorchInductorCacheDirectory = "/sessions/session-sync/tmp/torchinductor",
            LogsDirectory = "/sessions/session-sync/logs"
        };

        try
        {
            using var manager = new SessionProcessManager(options, new RecordingIsolationStrategy(identity.UserName, identity.HomeDirectory), new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));

            var psi = manager.BuildManagerUvSyncStartInfo("session-sync", identity, paths);

            Assert.That(psi.FileName, Is.EqualTo("/sessions/session-sync/.venv/bin/python"));
            Assert.That(psi.WorkingDirectory, Is.EqualTo(tempRoot));
            Assert.That(
                psi.ArgumentList,
                Is.EqualTo(new[]
                {
                    "-m",
                    "cm_cli",
                    "uv-sync",
                    "--user-directory",
                    "/sessions/session-sync/user"
                }));
            Assert.That(psi.Environment["COMFYUI_PATH"], Is.EqualTo(tempRoot));
            Assert.That(psi.Environment["HOME"], Is.EqualTo(paths.HomeDirectory));
            Assert.That(psi.Environment["VIRTUAL_ENV"], Is.EqualTo(paths.VirtualEnvDirectory));
            Assert.That(psi.Environment["UV_PROJECT_ENVIRONMENT"], Is.EqualTo(paths.VirtualEnvDirectory));
            Assert.That(
                psi.Environment["PATH"],
                Does.StartWith($"/sessions/session-sync/.venv/bin{Path.PathSeparator}"));
            Assert.That(psi.Environment["PATH"], Does.Not.Contain(Path.Combine(options.SeedVirtualEnvPath, "bin")));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void SessionStartInfos_DoNotInheritAgentSecretsOrHostVariables()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-session-env-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        string[] withheld = ["RUNNER_SECRET", "ARIA2_RPC_SECRET", "OFFLOADR_API_GRPC", "HOSTING_PROVIDER_API_KEY", "HF_TOKEN"];
        string[] inherited = ["NVIDIA_VISIBLE_DEVICES", "CUDA_VERSION", "LD_LIBRARY_PATH", "UV_LINK_MODE", "LC_ALL", "GRADIO_ANALYTICS_ENABLED"];
        var previous = withheld.Concat(inherited).ToDictionary(name => name, Environment.GetEnvironmentVariable);

        var options = new SessionProcessOptions
        {
            SessionRoot = Path.Combine(tempRoot, "sessions"),
            EntryPointPath = Path.Combine(tempRoot, "entrypoint.base.sh"),
            WorkingDirectory = tempRoot,
            BundledCustomNodesSeedPath = Path.Combine(tempRoot, "seed-custom_nodes"),
            BundledInputSeedPath = Path.Combine(tempRoot, "seed-input"),
            SeedVirtualEnvPath = CreateEmptySeedVirtualEnv(tempRoot),
            UvBinaryPath = FakeUvBinaryPath(tempRoot),
            ComfyPort = 8188,
            ShutdownGracePeriod = TimeSpan.FromSeconds(5),
            ReadyTimeout = TimeSpan.FromSeconds(5),
            ReadyHost = "127.0.0.1"
        };
        var identity = new PreparedSessionIdentity("sess_env", "/sessions/session-env", CleanupIdentity: true);
        var paths = new SessionProcessManager.SessionPaths
        {
            HomeDirectory = "/sessions/session-env",
            UserDirectory = "/sessions/session-env/user",
            CustomNodesDirectory = "/sessions/session-env/custom_nodes",
            ModelsDirectory = "/sessions/session-env/models",
            VirtualEnvDirectory = "/sessions/session-env/.venv",
            OutputDirectory = "/sessions/session-env/output",
            InputDirectory = "/sessions/session-env/input",
            TempDirectory = "/sessions/session-env/temp",
            ScratchDirectory = "/sessions/session-env/tmp",
            CacheDirectory = "/sessions/session-env/.cache",
            UvCacheDirectory = "/sessions/session-env/.cache/uv",
            TorchInductorCacheDirectory = "/sessions/session-env/tmp/torchinductor",
            LogsDirectory = "/sessions/session-env/logs"
        };

        try
        {
            foreach (var name in withheld.Concat(inherited))
            {
                Environment.SetEnvironmentVariable(name, $"value-of-{name}");
            }

            using var manager = new SessionProcessManager(options, new RecordingIsolationStrategy(identity.UserName, identity.HomeDirectory), new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));

            foreach (var psi in new[]
            {
                manager.BuildEditorStartInfo("session-env", identity, paths),
                manager.BuildManagerUvSyncStartInfo("session-env", identity, paths),
            })
            {
                Assert.Multiple(() =>
                {
                    foreach (var name in withheld)
                    {
                        Assert.That(psi.Environment.ContainsKey(name), Is.False, name);
                    }

                    foreach (var name in inherited)
                    {
                        Assert.That(psi.Environment[name], Is.EqualTo($"value-of-{name}"), name);
                    }

                    Assert.That(psi.Environment["HOME"], Is.EqualTo(paths.HomeDirectory));
                    Assert.That(psi.Environment["COMFYUI_SESSION_ID"], Is.EqualTo("session-env"));
                });
            }
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task RunManagerUvSyncOnceAsync_WaitsForRedirectedOutputBeforeReturning()
    {
        LinuxTestPrerequisites.RequireLinux();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-manager-sync-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var options = new SessionProcessOptions
        {
            SessionRoot = Path.Combine(tempRoot, "sessions"),
            EntryPointPath = Path.Combine(tempRoot, "entrypoint.base.sh"),
            WorkingDirectory = tempRoot,
            BundledCustomNodesSeedPath = Path.Combine(tempRoot, "seed-custom_nodes"),
            BundledInputSeedPath = Path.Combine(tempRoot, "seed-input"),
            SeedVirtualEnvPath = CreateEmptySeedVirtualEnv(tempRoot),
            UvBinaryPath = FakeUvBinaryPath(tempRoot),
            ComfyPort = 8188,
            ShutdownGracePeriod = TimeSpan.FromSeconds(5),
            ReadyTimeout = TimeSpan.FromSeconds(5),
            ReadyHost = "127.0.0.1"
        };
        var sessionVenv = Path.Combine(tempRoot, "session", ".venv");
        var pythonPath = Path.Combine(sessionVenv, "bin", "python");
        var failedPath = Path.Combine(sessionVenv, "lib", "python3.12", "site-packages", "PIL", "AvifImagePlugin.py");
        // The fake shell helper runs as the test account, without changing Unix credentials.
        var identity = new PreparedSessionIdentity(string.Empty, Path.Combine(tempRoot, "session"), CleanupIdentity: true);
        var paths = new SessionProcessManager.SessionPaths
        {
            HomeDirectory = identity.HomeDirectory,
            VirtualEnvDirectory = sessionVenv,
            UserDirectory = Path.Combine(identity.HomeDirectory, "user"),
            CacheDirectory = Path.Combine(identity.HomeDirectory, ".cache"),
            UvCacheDirectory = Path.Combine(identity.HomeDirectory, ".cache", "uv"),
            ScratchDirectory = Path.Combine(identity.HomeDirectory, "tmp"),
            TorchInductorCacheDirectory = Path.Combine(identity.HomeDirectory, "tmp", "torchinductor")
        };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pythonPath)!);
            File.WriteAllText(
                pythonPath,
                $$"""
                #!/bin/sh
                i=0
                while [ "$i" -lt 5000 ]; do
                  printf 'uv-sync filler %s\n' "$i" >&2
                  i=$((i + 1))
                done
                printf '%s\n' 'error: failed to remove file' >&2
                printf '%s\n' '`{{failedPath}}`: Permission denied' >&2
                exit 17
                """);
            MakeExecutable(pythonPath);

            using var manager = new SessionProcessManager(options, new RecordingIsolationStrategy(identity.UserName, identity.HomeDirectory), new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));

            var result = await manager.RunManagerUvSyncOnceAsync("session-sync", identity, paths, CancellationToken.None);

            Assert.That(result.ExitCode, Is.EqualTo(17));
            Assert.That(result.Output.Length, Is.LessThanOrEqualTo(64 * 1024));
            Assert.That(result.Output, Does.Not.Contain("uv-sync filler 0"));
            Assert.That(result.Output, Does.Contain("error: failed to remove file"));
            Assert.That(result.Output, Does.Contain($"`{failedPath}`: Permission denied"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task StartSessionAsync_PreparesManagerPathShimsForBootstrap()
    {
        LinuxTestPrerequisites.RequireLinux();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-session-manager-shims-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var customNodesSeedPath = Path.Combine(tempRoot, "seed-custom_nodes");
        var inputSeedPath = Path.Combine(tempRoot, "seed-input");
        Directory.CreateDirectory(customNodesSeedPath);
        Directory.CreateDirectory(inputSeedPath);

        var options = new SessionProcessOptions
        {
            SessionRoot = Path.Combine(tempRoot, "sessions"),
            EntryPointPath = Path.Combine(tempRoot, "missing-entrypoint.sh"),
            WorkingDirectory = tempRoot,
            BundledCustomNodesSeedPath = customNodesSeedPath,
            BundledInputSeedPath = inputSeedPath,
            SeedVirtualEnvPath = CreateEmptySeedVirtualEnv(tempRoot),
            UvBinaryPath = FakeUvBinaryPath(tempRoot),
            ComfyPort = 8188,
            ShutdownGracePeriod = TimeSpan.FromSeconds(5),
            ReadyTimeout = TimeSpan.FromSeconds(5),
            ReadyHost = "127.0.0.1"
        };

        const string sessionId = "session-manager-shims";
        var homeDirectory = Path.Combine(options.SessionRoot, sessionId);
        var isolation = new RecordingIsolationStrategy("sess_mgr", homeDirectory);
        var webDirectoryExistedDuringStartup = false;

        try
        {
            using var manager = new SessionProcessManager(options, isolation, new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));

            Exception? launchError = null;
            try
            {
                await manager.StartSessionAsync(
                    new StartSessionCommand { SessionId = sessionId, User = "user-1", LifecycleGeneration = 1, RuntimeEpoch = 1, RuntimeInstanceId = "33333333333343338333333333333333" },
                    (_, _) =>
                    {
                        webDirectoryExistedDuringStartup = Directory.Exists(Path.Combine(homeDirectory, "web"));
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                launchError = ex;
            }

            Assert.That(launchError, Is.Not.Null);
            Assert.That(webDirectoryExistedDuringStartup, Is.True);
            Assert.That(Directory.ResolveLinkTarget(Path.Combine(tempRoot, "user"), returnFinalTarget: false)?.FullName, Is.EqualTo(Path.Combine(homeDirectory, "user")));
            Assert.That(Directory.ResolveLinkTarget(Path.Combine(tempRoot, "custom_nodes"), returnFinalTarget: false)?.FullName, Is.EqualTo(Path.Combine(homeDirectory, "custom_nodes")));
            Assert.That(Directory.ResolveLinkTarget(Path.Combine(tempRoot, "web"), returnFinalTarget: false)?.FullName, Is.EqualTo(Path.Combine(homeDirectory, "web")));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task StartSessionAsync_ThrowsOperationCanceled_WhenManagerSyncIsCanceled()
    {
        LinuxTestPrerequisites.RequireLinux();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-session-manager-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var customNodesSeedPath = Path.Combine(tempRoot, "seed-custom_nodes");
        var inputSeedPath = Path.Combine(tempRoot, "seed-input");
        Directory.CreateDirectory(customNodesSeedPath);
        Directory.CreateDirectory(inputSeedPath);

        var options = new SessionProcessOptions
        {
            SessionRoot = Path.Combine(tempRoot, "sessions"),
            EntryPointPath = Path.Combine(tempRoot, "missing-entrypoint.sh"),
            WorkingDirectory = tempRoot,
            BundledCustomNodesSeedPath = customNodesSeedPath,
            BundledInputSeedPath = inputSeedPath,
            SeedVirtualEnvPath = CreateEmptySeedVirtualEnv(tempRoot),
            UvBinaryPath = FakeUvBinaryPath(tempRoot),
            ComfyPort = 8188,
            ShutdownGracePeriod = TimeSpan.FromSeconds(5),
            ReadyTimeout = TimeSpan.FromSeconds(5),
            ReadyHost = "127.0.0.1"
        };

        const string sessionId = "session-manager-cancel";
        var homeDirectory = Path.Combine(options.SessionRoot, sessionId);
        var startedMarker = Path.Combine(homeDirectory, "uv-sync-started");
        var isolation = new RecordingIsolationStrategy(string.Empty, homeDirectory);

        try
        {
            using var manager = new SessionProcessManager(options, isolation, new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var startTask = manager.StartSessionAsync(
                new StartSessionCommand { SessionId = sessionId, User = "user-1", LifecycleGeneration = 1, RuntimeEpoch = 1, RuntimeInstanceId = "33333333333343338333333333333333" },
                (paths, _) =>
                {
                    var pythonPath = Path.Combine(paths.VirtualEnvDirectory, "bin", "python");
                    Directory.CreateDirectory(Path.GetDirectoryName(pythonPath)!);
                    File.WriteAllText(
                        pythonPath,
                        $$"""
                        #!/bin/sh
                        touch "{{startedMarker}}"
                        trap 'exit 143' TERM INT
                        while true; do
                          sleep 1
                        done
                        """);
                    if (OperatingSystem.IsLinux())
                    {
                        File.SetUnixFileMode(
                            pythonPath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    }
                    return Task.CompletedTask;
                },
                cts.Token);

            var started = false;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                if (File.Exists(startedMarker))
                {
                    started = true;
                    break;
                }

                await Task.Delay(100, CancellationToken.None);
            }

            Assert.That(started, Is.True, "Expected fake uv-sync python to start before cancellation.");

            cts.Cancel();

            Assert.That(async () => await startTask, Throws.InstanceOf<OperationCanceledException>());
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public async Task StartSessionAsync_ReplacesDanglingManagerPathShims()
    {
        LinuxTestPrerequisites.RequireLinux();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-session-dangling-shims-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var customNodesSeedPath = Path.Combine(tempRoot, "seed-custom_nodes");
        var inputSeedPath = Path.Combine(tempRoot, "seed-input");
        Directory.CreateDirectory(customNodesSeedPath);
        Directory.CreateDirectory(inputSeedPath);

        var staleRoot = Path.Combine(tempRoot, "stale-session");
        var staleUserPath = Path.Combine(staleRoot, "user");
        var staleCustomNodesPath = Path.Combine(staleRoot, "custom_nodes");
        Directory.CreateDirectory(staleRoot);
        Directory.CreateSymbolicLink(Path.Combine(tempRoot, "user"), staleUserPath);
        Directory.CreateSymbolicLink(Path.Combine(tempRoot, "custom_nodes"), staleCustomNodesPath);
        Directory.Delete(staleRoot, recursive: true);

        var options = new SessionProcessOptions
        {
            SessionRoot = Path.Combine(tempRoot, "sessions"),
            EntryPointPath = Path.Combine(tempRoot, "missing-entrypoint.sh"),
            WorkingDirectory = tempRoot,
            BundledCustomNodesSeedPath = customNodesSeedPath,
            BundledInputSeedPath = inputSeedPath,
            SeedVirtualEnvPath = CreateEmptySeedVirtualEnv(tempRoot),
            UvBinaryPath = FakeUvBinaryPath(tempRoot),
            ComfyPort = 8188,
            ShutdownGracePeriod = TimeSpan.FromSeconds(5),
            ReadyTimeout = TimeSpan.FromSeconds(5),
            ReadyHost = "127.0.0.1"
        };

        const string sessionId = "session-dangling-shims";
        var homeDirectory = Path.Combine(options.SessionRoot, sessionId);
        var isolation = new RecordingIsolationStrategy("sess_dangling", homeDirectory);

        try
        {
            using var manager = new SessionProcessManager(options, isolation, new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));

            Exception? launchError = null;
            try
            {
                await manager.StartSessionAsync(
                    new StartSessionCommand { SessionId = sessionId, User = "user-1", LifecycleGeneration = 1, RuntimeEpoch = 1, RuntimeInstanceId = "33333333333343338333333333333333" },
                    (_, _) => Task.CompletedTask,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                launchError = ex;
            }

            Assert.That(launchError, Is.Not.Null);
            Assert.That(Directory.ResolveLinkTarget(Path.Combine(tempRoot, "user"), returnFinalTarget: false)?.FullName, Is.EqualTo(Path.Combine(homeDirectory, "user")));
            Assert.That(Directory.ResolveLinkTarget(Path.Combine(tempRoot, "custom_nodes"), returnFinalTarget: false)?.FullName, Is.EqualTo(Path.Combine(homeDirectory, "custom_nodes")));
        }
        finally
        {
            TryDelete(tempRoot);
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
            // Best-effort test cleanup.
        }
    }

    private static string CreateEmptySeedVirtualEnv(string tempRoot)
    {
        var seedVirtualEnvPath = Path.Combine(tempRoot, "seed-venv");
        var seedBinDirectory = Path.Combine(seedVirtualEnvPath, "bin");
        Directory.CreateDirectory(seedBinDirectory);
        Directory.CreateDirectory(Path.Combine(seedVirtualEnvPath, "lib", "python3.12", "site-packages"));
        File.WriteAllText(
            Path.Combine(seedBinDirectory, "python"),
            """
            #!/bin/sh
            exit 0
            """);
        File.WriteAllText(
            Path.Combine(seedBinDirectory, "pip"),
            """
            #!/bin/sh
            exit 0
            """);
        File.WriteAllText(
            Path.Combine(seedBinDirectory, "uv"),
            """
            #!/bin/sh
            set -eu
            target=""
            for arg in "$@"; do
              if [ "$arg" = "--seed" ]; then
                echo "--seed should not be used with a session-local cache" >&2
                exit 42
              fi
              target="$arg"
            done
            mkdir -p "$target/bin" "$target/lib/python3.12/site-packages"
            cat > "$target/bin/python" <<'PY'
            #!/bin/sh
            exit 0
            PY
            chmod +x "$target/bin/python"
            printf "home = fake\n" > "$target/pyvenv.cfg"
            """);
        MakeExecutable(Path.Combine(seedBinDirectory, "python"));
        MakeExecutable(Path.Combine(seedBinDirectory, "pip"));
        MakeExecutable(Path.Combine(seedBinDirectory, "uv"));
        return seedVirtualEnvPath;
    }

    private static string FakeUvBinaryPath(string tempRoot)
        => Path.Combine(tempRoot, "seed-venv", "bin", "uv");

    private static void MakeExecutable(string path)
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

    private sealed class RecordingIsolationStrategy(string userName, string homeDirectory) : ISessionIsolationStrategy
    {
        public int CleanupCallCount { get; private set; }
        public Func<Task>? Cleanup { get; init; }

        public void ValidatePrerequisites(ICollection<string> errors)
        {
        }

        public Task<PreparedSessionIdentity> PrepareAsync(string sessionId, string sessionRoot, CancellationToken cancellationToken)
        {
            return Task.FromResult(new PreparedSessionIdentity(userName, homeDirectory, CleanupIdentity: true));
        }

        public Task CleanupAsync(PreparedSessionIdentity identity, CancellationToken cancellationToken)
        {
            CleanupCallCount++;
            return Cleanup?.Invoke() ?? Task.CompletedTask;
        }
    }

    private sealed class RecordingCommandRunner(string seededPath) : ILinuxCommandRunner
    {
        public List<LinuxCommand> Commands { get; } = new();
        public bool SeededPathExistedAtChown { get; private set; }

        public Task<LinuxCommandResult> RunAsync(LinuxCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            SeededPathExistedAtChown = File.Exists(seededPath);
            return Task.FromResult(new LinuxCommandResult(0, string.Empty, string.Empty));
        }
    }
}
