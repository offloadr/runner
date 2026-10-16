using Offloadr.Runner.V1;
using SealedStorageMode = Offloadr.Runner.Core.SealedStorageMode;

namespace Offloadr.Runner.Tests;

public partial class SessionProcessManagerOwnershipTests
{
    private const UnixFileMode Mode0700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode Mode0711 = Mode0700 | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
    private const UnixFileMode Mode0755 = Mode0711 | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task StartSessionAsync_SealedKeepsDataOnTheSealedRootAndRemovesItAfterAFailedLaunch()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireRoot();

        var (tempRoot, sealedRoot, options) = CreateSealedOptions("comfyui");
        const string sessionId = "session-sealed";
        var homeDirectory = Path.Combine(options.SessionRoot, sessionId);
        var sealedDirectory = Path.Combine(sealedRoot, sessionId);
        var commands = new RecordingCommandRunner(string.Empty);
        SessionProcessManager.SessionPaths? captured = null;
        var layout = new Dictionary<string, UnixFileMode>(StringComparer.Ordinal);

        try
        {
            using var manager = new SessionProcessManager(options, new RecordingIsolationStrategy("sess_sealed", homeDirectory), SealedVfsBuilder(), commands);

            // The entry point does not exist, so the launch fails after provisioning.
            Assert.CatchAsync(() => manager.StartSessionAsync(
                SealedStart(sessionId),
                (paths, _) =>
                {
                    captured = paths;
                    foreach (var entry in Directory.EnumerateFileSystemEntries(sealedDirectory).Append(sealedDirectory).Append(sealedRoot))
                    {
                        layout[entry] = File.GetUnixFileMode(entry);
                    }

                    return Task.CompletedTask;
                },
                CancellationToken.None));

            var paths = captured!.Value;
            Assert.Multiple(() =>
            {
                Assert.That(paths.SealedDirectory, Is.EqualTo(sealedDirectory));
                Assert.That(paths.InputDirectory, Is.EqualTo(Path.Combine(sealedDirectory, "input")));
                Assert.That(paths.OutputDirectory, Is.EqualTo(Path.Combine(sealedDirectory, "output")));
                Assert.That(paths.TempDirectory, Is.EqualTo(Path.Combine(sealedDirectory, "temp")));
                Assert.That(paths.UserDirectory, Is.EqualTo(Path.Combine(sealedDirectory, "user")));
                Assert.That(paths.ScratchDirectory, Is.EqualTo(Path.Combine(sealedDirectory, "tmp")));
                Assert.That(paths.ProfileDirectory, Is.EqualTo(Path.Combine(sealedDirectory, "home")));
                Assert.That(paths.SealedAgentDirectory, Is.EqualTo(Path.Combine(sealedDirectory, ".agent")));
                Assert.That(paths.VirtualEnvDirectory, Is.EqualTo(Path.Combine(homeDirectory, ".venv")));
                Assert.That(paths.CustomNodesDirectory, Is.EqualTo(Path.Combine(homeDirectory, "custom_nodes")));
                Assert.That(paths.ModelsDirectory, Is.EqualTo(Path.Combine(homeDirectory, "models")));
                Assert.That(paths.TorchInductorCacheDirectory, Is.EqualTo(Path.Combine(homeDirectory, ".cache", "torchinductor")));

                Assert.That(layout[sealedRoot], Is.EqualTo(Mode0711));
                Assert.That(layout[sealedDirectory], Is.EqualTo(Mode0755));
                foreach (var name in SessionProcessManager.SealedSessionDirectoryNames.Append(".agent"))
                {
                    Assert.That(layout[Path.Combine(sealedDirectory, name)], Is.EqualTo(Mode0700), name);
                }

                // Only the session's own directories are handed over; the parent and .agent stay root's.
                var chownTargets = commands.Commands.Where(command => command.Arguments.Contains("-R")).Select(command => command.Arguments[^1]).ToArray();
                Assert.That(chownTargets, Does.Contain(homeDirectory));
                Assert.That(chownTargets, Is.SupersetOf(paths.SealedSessionOwnedDirectories));
                Assert.That(chownTargets, Does.Not.Contain(sealedDirectory));
                Assert.That(chownTargets, Does.Not.Contain(paths.SealedAgentDirectory));

                // A failed launch removes the sealed data along with the home.
                Assert.That(Directory.Exists(sealedDirectory), Is.False);
                Assert.That(Directory.Exists(homeDirectory), Is.False);
            });
        }
        finally
        {
            TryDelete(tempRoot);
            TryDelete(sealedRoot);
        }
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void StartSessionAsync_SealedRefusesBeforeProvisioningWhenTheRootIsUnusable()
    {
        LinuxTestPrerequisites.RequireLinux();

        var (tempRoot, _, options) = CreateSealedOptions("comfyui");
        // A root on the disk-backed temp directory is not memory-backed.
        options = WithSealing(options, new SealedStorageOptions
        {
            Mode = SealedStorageMode.Required,
            Root = LinuxPathCanonicalizer.ResolveExistingPath(tempRoot) + "/sealed",
            MinimumBytes = 1 << 20,
        });
        const string sessionId = "session-refused";
        var homeDirectory = Path.Combine(options.SessionRoot, sessionId);
        var isolation = new RecordingIsolationStrategy("sess_refused", homeDirectory);

        try
        {
            using var manager = new SessionProcessManager(options, isolation, SealedVfsBuilder(), new RecordingCommandRunner(string.Empty));

            var ex = Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartSessionAsync(SealedStart(sessionId), CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain("Sealed storage is unavailable"));
            Assert.That(Directory.Exists(homeDirectory), Is.False);
            Assert.That(isolation.CleanupCallCount, Is.Zero);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void StartSessionAsync_SealedRefusesARootSomeoneElseOwns()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireRoot();

        var (tempRoot, sealedRoot, options) = CreateSealedOptions("comfyui");
        Directory.CreateDirectory(sealedRoot);
        File.SetUnixFileMode(sealedRoot, Mode0755 | UnixFileMode.OtherWrite);
        const string sessionId = "session-planted";

        try
        {
            using var manager = new SessionProcessManager(options, new RecordingIsolationStrategy("sess_planted", Path.Combine(options.SessionRoot, sessionId)), SealedVfsBuilder(), new RecordingCommandRunner(string.Empty));

            var ex = Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartSessionAsync(SealedStart(sessionId), CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain("writable only by root"));
            Assert.That(Directory.Exists(Path.Combine(sealedRoot, sessionId)), Is.False);
        }
        finally
        {
            TryDelete(tempRoot);
            TryDelete(sealedRoot);
        }
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task StartSessionAsync_SealedForgeGetsADataDirectoryOnTheSealedRoot()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireRoot();

        var (tempRoot, sealedRoot, options) = CreateSealedOptions("forge-neo");
        const string sessionId = "session-forge-sealed";
        var homeDirectory = Path.Combine(options.SessionRoot, sessionId);
        string? extensionsTarget = null;
        string? outputTarget = null;
        SessionProcessManager.SessionPaths? captured = null;

        try
        {
            using var manager = new SessionProcessManager(options, new RecordingIsolationStrategy("sess_forge", homeDirectory), SealedVfsBuilder(), new RecordingCommandRunner(string.Empty));

            try
            {
                await manager.StartSessionAsync(
                    SealedStart(sessionId, "forge-neo"),
                    (paths, _) =>
                    {
                        captured = paths;
                        extensionsTarget = new DirectoryInfo(Path.Combine(paths.ForgeDataDirectory, "extensions")).LinkTarget;
                        outputTarget = new DirectoryInfo(Path.Combine(paths.ForgeDataDirectory, "output")).LinkTarget;
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);
            }
            catch (Exception)
            {
                // The entry point does not exist, so the launch fails after provisioning.
            }

            var paths = captured!.Value;
            Assert.Multiple(() =>
            {
                Assert.That(paths.ForgeDataDirectory, Is.EqualTo(Path.Combine(sealedRoot, sessionId, "forge")));
                Assert.That(extensionsTarget, Is.EqualTo(paths.CustomNodesDirectory));
                Assert.That(outputTarget, Is.EqualTo(paths.OutputDirectory));
                Assert.That(
                    SessionProcessManager.BuildRuntimeArguments("forge-neo", 7860, paths, paths.ModelsDirectory),
                    Is.EqualTo(new[] { "--port", "7860", "--api", "--data-dir", paths.ForgeDataDirectory, "--model-ref", paths.ModelsDirectory }));
            });
        }
        finally
        {
            TryDelete(tempRoot);
            TryDelete(sealedRoot);
        }
    }

    [Test]
    public void BuildRuntimeArguments_SealedComfyNamesEachDataDirectory()
    {
        LinuxTestPrerequisites.RequireLinux();

        var paths = SealedPaths("/sessions/s", "/dev/shm/offloadr/s");

        var args = SessionProcessManager.BuildRuntimeArguments("comfyui", 8188, paths, "/comfyui/models");

        Assert.That(args, Is.EqualTo(new[]
        {
            "--port", "8188", "--disable-auto-launch",
            "--base-directory", "/sessions/s",
            "--input-directory", "/dev/shm/offloadr/s/input",
            "--output-directory", "/dev/shm/offloadr/s/output",
            "--temp-directory", "/dev/shm/offloadr/s",
            "--user-directory", "/dev/shm/offloadr/s/user",
        }));
    }

    [Test]
    public void BuildSessionEnvironment_SealedMovesHomeAndNamesCompiledCaches()
    {
        LinuxTestPrerequisites.RequireLinux();

        var paths = SealedPaths("/sessions/s", "/dev/shm/offloadr/s");
        var identity = new PreparedSessionIdentity("sess_s", "/sessions/s", CleanupIdentity: true);

        var env = SessionProcessManager.BuildSessionEnvironment("s", identity, paths);

        Assert.Multiple(() =>
        {
            Assert.That(env["HOME"], Is.EqualTo("/dev/shm/offloadr/s/home"));
            Assert.That(env["TMPDIR"], Is.EqualTo("/dev/shm/offloadr/s/tmp"));
            Assert.That(env["GRADIO_TEMP_DIR"], Is.EqualTo("/dev/shm/offloadr/s/tmp/gradio"));
            Assert.That(env["XDG_CACHE_HOME"], Is.EqualTo("/sessions/s/.cache"));
            Assert.That(env["TORCHINDUCTOR_CACHE_DIR"], Is.EqualTo("/sessions/s/.cache/torchinductor"));
            Assert.That(env["TRITON_CACHE_DIR"], Is.EqualTo("/sessions/s/.cache/triton"));
            Assert.That(env["CUDA_CACHE_PATH"], Is.EqualTo("/sessions/s/.cache/nv"));
        });
    }

    [Test]
    public void BuildSessionEnvironment_UnsealedSetsNoSealedVariables()
    {
        LinuxTestPrerequisites.RequireLinux();

        var paths = SealedPaths("/sessions/s", sealedDirectory: null);
        var identity = new PreparedSessionIdentity("sess_s", "/sessions/s", CleanupIdentity: true);

        var env = SessionProcessManager.BuildSessionEnvironment("s", identity, paths);

        Assert.That(env["HOME"], Is.EqualTo("/sessions/s"));
        Assert.That(env.Keys, Has.None.AnyOf("TRITON_CACHE_DIR", "CUDA_CACHE_PATH", "GRADIO_TEMP_DIR"));
    }

    [Test]
    public void BuildManagerUvSyncStartInfo_SealedBuildsInDiskScratch()
    {
        LinuxTestPrerequisites.RequireLinux();

        var (tempRoot, _, options) = CreateSealedOptions("comfyui");
        var paths = SealedPaths("/sessions/s", "/dev/shm/offloadr/s");
        var identity = new PreparedSessionIdentity("sess_s", "/sessions/s", CleanupIdentity: true);

        try
        {
            using var manager = new SessionProcessManager(options, new RecordingIsolationStrategy(identity.UserName, identity.HomeDirectory), SealedVfsBuilder(), new RecordingCommandRunner(string.Empty));

            var psi = manager.BuildManagerUvSyncStartInfo("s", identity, paths);

            Assert.That(psi.Environment["TMPDIR"], Is.EqualTo("/sessions/s/tmp"));
            Assert.That(psi.Environment["HOME"], Is.EqualTo("/dev/shm/offloadr/s/home"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task CleanupStaleSessionsAsync_RemovesSealedDataOfAnEarlierRun()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireRoot();

        var (tempRoot, sealedRoot, options) = CreateSealedOptions("comfyui");
        Directory.CreateDirectory(Path.Combine(sealedRoot, "old-session", "output"));
        File.WriteAllText(Path.Combine(sealedRoot, "old-session", "output", "image.png"), "left behind");
        File.SetUnixFileMode(sealedRoot, Mode0711);

        try
        {
            using var manager = new SessionProcessManager(options, new RecordingIsolationStrategy("unused", Path.Combine(options.SessionRoot, "unused")), SealedVfsBuilder(), new RecordingCommandRunner(string.Empty));

            await manager.CleanupStaleSessionsAsync(CancellationToken.None);

            Assert.That(Directory.Exists(sealedRoot), Is.True);
            Assert.That(Directory.EnumerateFileSystemEntries(sealedRoot), Is.Empty);
        }
        finally
        {
            TryDelete(tempRoot);
            TryDelete(sealedRoot);
        }
    }

    [Test]
    public void SealedHostLockdown_RemovesSharedWriteOnlyFromDiskBackedDirectories()
    {
        var modes = new Dictionary<string, UnixFileMode>(StringComparer.Ordinal);
        var sticky = UnixFileMode.StickyBit | Mode0755 | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        var entries = new Dictionary<string, LinuxEntryStatus>(StringComparer.Ordinal)
        {
            ["/tmp"] = new(true, false, 0, sticky),
            ["/var/tmp"] = new(true, false, 0, sticky),
            ["/run/shm"] = new(true, false, 0, sticky),
            ["/already"] = new(true, false, 0, Mode0755),
            ["/link"] = new(false, true, 0, sticky),
            ["/tmp/shared.log"] = new(false, false, 0, Mode0755 | UnixFileMode.OtherWrite, IsRegularFile: true),
            // The agent's VFS socket must stay reachable for session processes.
            ["/tmp/agent.sock"] = new(false, false, 0, Mode0755 | UnixFileMode.OtherWrite),
        };

        var result = SealedHostLockdown.RemoveSharedWriteAccess(
            entries.Keys,
            path => entries.TryGetValue(path, out var entry) ? entry with { Mode = modes.GetValueOrDefault(path, entry.Mode) } : null,
            path => new LinuxFileSystemStats(path == "/run/shm" ? 0x01021994 : 0xEF53, 4096, 1, 1),
            (path, mode) => modes[path] = mode);

        Assert.That(result.Closed, Is.EqualTo(new[] { "/tmp", "/var/tmp", "/tmp/shared.log" }));
        Assert.That(result.StillWritable, Is.Empty);
        Assert.That(modes["/tmp"], Is.EqualTo(UnixFileMode.StickyBit | Mode0755));
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task SealedSession_EditorWritesStayOffDiskAndAreRemovedAtStop()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireRoot();

        var (tempRoot, sealedRoot, options) = CreateSealedOptions("comfyui");
        // The script names the canary in two halves, so only files the editor writes contain it whole.
        var first = Guid.NewGuid().ToString("n");
        var second = Guid.NewGuid().ToString("n");
        var canary = first + second;
        var entryPoint = Path.Combine(tempRoot, "editor.sh");
        File.WriteAllText(entryPoint, string.Join('\n',
            "#!/bin/sh",
            $"first={first}",
            $"second={second}",
            "while [ $# -gt 0 ]; do",
            "  case \"$1\" in",
            "    --input-directory) input=\"$2\"; shift ;;",
            "    --output-directory) output=\"$2\"; shift ;;",
            "    --temp-directory) temp=\"$2/temp\"; shift ;;",
            "    --user-directory) user=\"$2\"; shift ;;",
            "  esac",
            "  shift",
            "done",
            "mkdir -p \"$temp\" \"$HOME\"",
            "for directory in \"$input\" \"$output\" \"$temp\" \"$user\" \"$HOME\" \"$TMPDIR\"; do",
            "  printf '%s%s' \"$first\" \"$second\" > \"$directory/canary.txt\"",
            "done",
            "echo \"editor output $first$second\"",
            "touch \"$output/.written\"",
            "exec /bin/sleep 600",
            string.Empty));
        File.SetUnixFileMode(entryPoint, Mode0755);
        options = WithEntryPoint(options, entryPoint);
        const string sessionId = "session-canary";
        var homeDirectory = Path.Combine(options.SessionRoot, sessionId);
        var sealedDirectory = Path.Combine(sealedRoot, sessionId);

        try
        {
            using var manager = new SessionProcessManager(options, new RecordingIsolationStrategy(string.Empty, homeDirectory), SealedVfsBuilder(), new RecordingCommandRunner(string.Empty));
            await manager.StartSessionAsync(SealedStart(sessionId), CancellationToken.None);
            await WaitForFileAsync(Path.Combine(sealedDirectory, "output", ".written"));

            var sealedHits = FindFilesContaining(sealedDirectory, canary);
            var diskHits = new[] { options.SessionRoot, "/var/tmp", Path.GetTempPath() }
                .SelectMany(directory => FindFilesContaining(directory, canary))
                .ToArray();
            Assert.That(sealedHits, Has.Length.EqualTo(6), string.Join(", ", sealedHits));
            Assert.That(diskHits, Is.Empty);

            await manager.StopSessionAsync(sessionId, CancellationToken.None);

            Assert.That(Directory.Exists(sealedDirectory), Is.False);
            Assert.That(Directory.Exists(homeDirectory), Is.False);
        }
        finally
        {
            TryDelete(tempRoot);
            TryDelete(sealedRoot);
        }
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task SealedSession_UnexpectedEditorExitRemovesTheSealedDirectory()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireRoot();

        var (tempRoot, sealedRoot, options) = CreateSealedOptions("comfyui");
        var entryPoint = Path.Combine(tempRoot, "crash.sh");
        File.WriteAllText(entryPoint, "#!/bin/sh\nsleep 1\nexit 3\n");
        File.SetUnixFileMode(entryPoint, Mode0755);
        options = WithEntryPoint(options, entryPoint);
        const string sessionId = "session-crash";
        var sealedDirectory = Path.Combine(sealedRoot, sessionId);

        try
        {
            using var manager = new SessionProcessManager(options, new RecordingIsolationStrategy(string.Empty, Path.Combine(options.SessionRoot, sessionId)), SealedVfsBuilder(), new RecordingCommandRunner(string.Empty));
            await manager.StartSessionAsync(SealedStart(sessionId), CancellationToken.None);
            Assert.That(Directory.Exists(sealedDirectory), Is.True);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (Directory.Exists(sealedDirectory) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            Assert.That(Directory.Exists(sealedDirectory), Is.False);
        }
        finally
        {
            TryDelete(tempRoot);
            TryDelete(sealedRoot);
        }
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task SealedProfile_StartGetsItsTransferSealingAndReleasesItWithTheData()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireRoot();

        var (tempRoot, sealedRoot, options) = CreateSealedOptions("comfyui");
        options = WithSealing(options, new SealedStorageOptions { Mode = SealedStorageMode.Auto, Root = sealedRoot, MinimumBytes = 1 << 20 });
        const string sessionId = "session-profile";
        using var recipient = Offloadr.Runner.Core.Sealing.SealingKeyPair.Generate("output-1");
        var start = SealedStart(sessionId);
        start.Sealing = new SessionSealing { Required = true, OutputRecipient = SessionTransferSealingTests.ToMessage(recipient.PublicKey) };
        SessionSealingStatus? statusDuringStart = null;
        SessionTransferSealing? captured = null;

        try
        {
            using var manager = new SessionProcessManager(options, new RecordingIsolationStrategy("sess_profile", Path.Combine(options.SessionRoot, sessionId)), SealedVfsBuilder(), new RecordingCommandRunner(string.Empty));

            Assert.CatchAsync(() => manager.StartSessionAsync(
                start,
                (paths, _) =>
                {
                    captured = paths.TransferSealing;
                    statusDuringStart = manager.GetSessionSealingStatus(sessionId);
                    return Task.CompletedTask;
                },
                CancellationToken.None));

            Assert.Multiple(() =>
            {
                Assert.That(captured, Is.Not.Null);
                Assert.That(statusDuringStart, Is.EqualTo(new SessionSealingStatus { MemoryWorkspace = true, EncryptedUploads = true, EncryptedReadsRequired = true }));
                // The failed launch removed the data, then released the reader key.
                Assert.That(manager.GetSessionSealingStatus(sessionId), Is.Null);
                Assert.That(Directory.Exists(Path.Combine(sealedRoot, sessionId)), Is.False);
            });
        }
        finally
        {
            TryDelete(tempRoot);
            TryDelete(sealedRoot);
        }
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void SealedProfile_IsRefusedBeforeProvisioningWithoutSealingOrARecipient()
    {
        LinuxTestPrerequisites.RequireLinux();

        var (tempRoot, sealedRoot, options) = CreateSealedOptions("comfyui");
        const string sessionId = "session-profile-refused";
        var homeDirectory = Path.Combine(options.SessionRoot, sessionId);
        var isolation = new RecordingIsolationStrategy("sess_refused", homeDirectory);

        try
        {
            var off = WithSealing(options, new SealedStorageOptions { Mode = SealedStorageMode.Off, Root = sealedRoot });
            using (var manager = new SessionProcessManager(off, isolation, SealedVfsBuilder(), new RecordingCommandRunner(string.Empty)))
            {
                var start = SealedStart(sessionId);
                start.Sealing = new SessionSealing { Required = true };
                var ex = Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartSessionAsync(start, CancellationToken.None));
                Assert.That(ex!.Message, Does.Contain("does not offer sealed sessions"));
            }

            if (OperatingSystem.IsLinux() && Environment.UserName == "root")
            {
                using var manager = new SessionProcessManager(options, isolation, SealedVfsBuilder(), new RecordingCommandRunner(string.Empty));
                var start = SealedStart(sessionId);
                start.Sealing = new SessionSealing { Required = true };
                var ex = Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartSessionAsync(start, CancellationToken.None));
                Assert.That(ex!.Message, Does.Contain("output recipient"));
                Assert.That(manager.GetSessionSealingStatus(sessionId), Is.Null);
            }

            Assert.That(Directory.Exists(homeDirectory), Is.False);
            Assert.That(isolation.CleanupCallCount, Is.Zero);
        }
        finally
        {
            TryDelete(tempRoot);
            TryDelete(sealedRoot);
        }
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void DescribeSealedStorage_ReportsModeBudgetAndAlgorithm()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireRoot();

        var (tempRoot, sealedRoot, options) = CreateSealedOptions("comfyui");
        try
        {
            using var manager = new SessionProcessManager(options, new RecordingIsolationStrategy("unused", Path.Combine(options.SessionRoot, "unused")), SealedVfsBuilder(), new RecordingCommandRunner(string.Empty));
            using var off = new SessionProcessManager(
                WithSealing(options, new SealedStorageOptions { Mode = SealedStorageMode.Off, Root = sealedRoot }),
                new RecordingIsolationStrategy("unused", Path.Combine(options.SessionRoot, "unused")),
                SealedVfsBuilder(),
                new RecordingCommandRunner(string.Empty));

            var described = manager.DescribeSealedStorage();
            var disabled = off.DescribeSealedStorage();

            Assert.Multiple(() =>
            {
                Assert.That(described.Mode, Is.EqualTo(Offloadr.Runner.V1.SealedStorageMode.Required));
                Assert.That(described.MemoryWorkspaceAvailable, Is.True);
                Assert.That(described.BudgetBytes, Is.GreaterThan(0));
                Assert.That(described.Algorithms, Is.EqualTo(new[] { Offloadr.Runner.Core.Sealing.SealedEnvelopeFormat.AlgorithmId }));
                Assert.That(disabled.Mode, Is.EqualTo(Offloadr.Runner.V1.SealedStorageMode.Off));
                Assert.That(disabled.MemoryWorkspaceAvailable, Is.False);
                Assert.That(disabled.Algorithms, Is.Empty);
            });
        }
        finally
        {
            TryDelete(tempRoot);
            TryDelete(sealedRoot);
        }
    }

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for '{path}'.");
            }

            await Task.Delay(50);
        }
    }

    private static string[] FindFilesContaining(string directory, string text)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var needle = System.Text.Encoding.UTF8.GetBytes(text);
        var hits = new List<string>();
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(directory, "*", enumeration))
        {
            try
            {
                // FIFOs (the runtime's debugger pipes live in /tmp) and sockets report no length;
                // opening one could block, and none can hold the canary.
                var length = new FileInfo(file).Length;
                if (length > 0 && length < 64 * 1024 * 1024 && File.ReadAllBytes(file).AsSpan().IndexOf(needle) >= 0)
                {
                    hits.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sockets and files that vanish while scanning cannot hold the canary.
            }
        }

        return [.. hits];
    }

    private static SessionProcessOptions WithEntryPoint(SessionProcessOptions options, string entryPoint)
        => new()
        {
            SessionRoot = options.SessionRoot,
            EntryPointPath = entryPoint,
            WorkingDirectory = options.WorkingDirectory,
            BundledCustomNodesSeedPath = options.BundledCustomNodesSeedPath,
            BundledInputSeedPath = options.BundledInputSeedPath,
            SeedVirtualEnvPath = options.SeedVirtualEnvPath,
            UvBinaryPath = options.UvBinaryPath,
            RuntimeKind = options.RuntimeKind,
            ComfyPort = options.ComfyPort,
            ShutdownGracePeriod = TimeSpan.FromSeconds(1),
            ReadyTimeout = options.ReadyTimeout,
            ReadyHost = options.ReadyHost,
            Sealing = options.Sealing,
        };

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void StartSessionAsync_SealedRefusesWithoutTheShimThatPreventsCoreDumps()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireRoot();

        var (tempRoot, sealedRoot, options) = CreateSealedOptions("comfyui");
        const string sessionId = "session-no-shim";
        var homeDirectory = Path.Combine(options.SessionRoot, sessionId);
        var missingShim = new RunnerVfsEnvironmentBuilder { DefaultVfsLibraryPath = Path.Combine(tempRoot, "missing.so") };

        try
        {
            using var manager = new SessionProcessManager(options, new RecordingIsolationStrategy("sess_no_shim", homeDirectory), missingShim, new RecordingCommandRunner(string.Empty));

            var ex = Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartSessionAsync(SealedStart(sessionId), CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain("non-dumpable"));
            Assert.That(Directory.Exists(homeDirectory), Is.False);
            Assert.That(manager.DescribeSealedStorage().MemoryWorkspaceAvailable, Is.False);
        }
        finally
        {
            TryDelete(tempRoot);
            TryDelete(sealedRoot);
        }
    }

    /// <summary>A builder whose shim exists, as in the images; the file is not a real library.</summary>
    private static RunnerVfsEnvironmentBuilder SealedVfsBuilder()
    {
        var shim = Path.Combine(Path.GetTempPath(), "runner-sealed-tests-shim.so");
        if (!File.Exists(shim))
        {
            File.WriteAllText(shim, string.Empty);
        }

        return new RunnerVfsEnvironmentBuilder { DefaultVfsLibraryPath = shim };
    }

    [Test]
    public void SealedHostLockdown_ReportsWhatStaysWritableButNotReadOnlyMounts()
    {
        var shared = UnixFileMode.StickyBit | Mode0755 | UnixFileMode.OtherWrite;
        var entries = new Dictionary<string, LinuxEntryStatus>(StringComparer.Ordinal)
        {
            ["/fuse"] = new(true, false, 0, shared),
            ["/refuses"] = new(true, false, 0, shared),
            ["/readonly"] = new(true, false, 0, shared),
        };

        var result = SealedHostLockdown.RemoveSharedWriteAccess(
            entries.Keys,
            path => entries[path],
            path => new LinuxFileSystemStats(0xEF53, 4096, 1, 1, ReadOnly: path == "/readonly"),
            (path, _) =>
            {
                // A FUSE mount accepts chmod and ignores it; another refuses it outright.
                if (path == "/refuses")
                {
                    throw new UnauthorizedAccessException("Operation not permitted");
                }
            });

        Assert.That(result.Closed, Is.Empty);
        Assert.That(result.StillWritable, Is.EqualTo(new[] { "/fuse", "/refuses" }));
    }

    private static StartSessionCommand SealedStart(string sessionId, string runtimeKind = "comfyui")
        => new()
        {
            SessionId = sessionId,
            User = "user-1",
            EditorRuntimeKind = runtimeKind,
            LifecycleGeneration = 1,
            RuntimeEpoch = 1,
            RuntimeInstanceId = "44444444444444448444444444444444",
        };

    private static (string TempRoot, string SealedRoot, SessionProcessOptions Options) CreateSealedOptions(string runtimeKind)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-session-sealed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "seed-custom_nodes"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "seed-input"));
        var sealedRoot = $"/dev/shm/runner-sealed-tests-{Guid.NewGuid():N}";
        var options = new SessionProcessOptions
        {
            SessionRoot = Path.Combine(tempRoot, "sessions"),
            EntryPointPath = Path.Combine(tempRoot, "missing-entrypoint.sh"),
            WorkingDirectory = tempRoot,
            BundledCustomNodesSeedPath = Path.Combine(tempRoot, "seed-custom_nodes"),
            BundledInputSeedPath = Path.Combine(tempRoot, "seed-input"),
            SeedVirtualEnvPath = CreateEmptySeedVirtualEnv(tempRoot),
            UvBinaryPath = FakeUvBinaryPath(tempRoot),
            RuntimeKind = runtimeKind,
            ComfyPort = 8188,
            ShutdownGracePeriod = TimeSpan.FromSeconds(5),
            ReadyTimeout = TimeSpan.FromSeconds(5),
            ReadyHost = "127.0.0.1",
            // The test container's /dev/shm is Docker's default 64 MiB.
            Sealing = new SealedStorageOptions { Mode = SealedStorageMode.Required, Root = sealedRoot, MinimumBytes = 1 << 20 },
        };
        return (tempRoot, sealedRoot, options);
    }

    private static SessionProcessOptions WithSealing(SessionProcessOptions options, SealedStorageOptions sealing)
        => new()
        {
            SessionRoot = options.SessionRoot,
            EntryPointPath = options.EntryPointPath,
            WorkingDirectory = options.WorkingDirectory,
            BundledCustomNodesSeedPath = options.BundledCustomNodesSeedPath,
            BundledInputSeedPath = options.BundledInputSeedPath,
            SeedVirtualEnvPath = options.SeedVirtualEnvPath,
            UvBinaryPath = options.UvBinaryPath,
            RuntimeKind = options.RuntimeKind,
            ComfyPort = options.ComfyPort,
            ShutdownGracePeriod = options.ShutdownGracePeriod,
            ReadyTimeout = options.ReadyTimeout,
            ReadyHost = options.ReadyHost,
            Sealing = sealing,
        };

    private static SessionProcessManager.SessionPaths SealedPaths(string home, string? sealedDirectory)
    {
        var data = sealedDirectory ?? home;
        return new SessionProcessManager.SessionPaths
        {
            HomeDirectory = home,
            SealedDirectory = sealedDirectory,
            UserDirectory = $"{data}/user",
            CustomNodesDirectory = $"{home}/custom_nodes",
            ModelsDirectory = $"{home}/models",
            VirtualEnvDirectory = $"{home}/.venv",
            OutputDirectory = $"{data}/output",
            InputDirectory = $"{data}/input",
            TempDirectory = $"{data}/temp",
            ScratchDirectory = $"{data}/tmp",
            CacheDirectory = $"{home}/.cache",
            UvCacheDirectory = $"{home}/.cache/uv",
            TorchInductorCacheDirectory = sealedDirectory is null ? $"{home}/tmp/torchinductor" : $"{home}/.cache/torchinductor",
            LogsDirectory = $"{home}/logs",
        };
    }
}
