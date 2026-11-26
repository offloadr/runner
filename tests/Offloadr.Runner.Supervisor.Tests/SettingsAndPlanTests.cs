using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hardware;
using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor.Tests;

public class RunnerSettingsTests
{
    private const string ValidKey = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";

    [TestCase(0, false)]
    [TestCase(1, true)]
    [TestCase(10, true)]
    [TestCase(RunnerSettings.MaximumWarmMinutes, true)]
    [TestCase(RunnerSettings.MaximumWarmMinutes + 1, false)]
    public void Validate_KeepsTheWarmTimeWithinItsRange(int minutes, bool valid)
    {
        var settings = new RunnerSettings { RunnerKey = ValidKey, WarmMinutes = minutes };

        Assert.That(settings.Validate(_ => true), valid ? Is.Empty : Has.Some.Contains("idle editor"));
    }

    [Test]
    public void SupervisorView_NamesTheEditorItIsBusyWith()
    {
        var view = SupervisorView.Initial(DateTimeOffset.UnixEpoch);
        var comfyUi = new LaunchTemplate("comfyui-latest", RunnerSettings.ComfyUiRuntimeKind, "image", "ComfyUI - current release");
        var forge = new LaunchTemplate("forge-neo", RunnerSettings.ForgeNeoRuntimeKind, "image", "Forge Neo - current release");
        var online = new Offloadr.Runner.Supervisor.Launcher.LauncherLinkState(Offloadr.Runner.Supervisor.Launcher.LauncherConnection.Online, null, true, null);
        var session = new Offloadr.Runner.Core.RunnerStatusSnapshot("runner", "instance", "1.0", [], "session-1", DateTimeOffset.UnixEpoch);

        Assert.Multiple(() =>
        {
            Assert.That((view with { State = SupervisorState.Pulling, LaunchingEditor = "ComfyUI" }).StateTitle, Is.EqualTo("Downloading ComfyUI…"));
            Assert.That((view with { State = SupervisorState.Pulling }).StateTitle, Is.EqualTo("Downloading the image…"), "For the GPU check.");
            Assert.That((view with { State = SupervisorState.Starting, LaunchingEditor = "Forge Neo" }).StateTitle, Is.EqualTo("Starting Forge Neo…"));
            Assert.That((view with { State = SupervisorState.Running, ActiveTemplate = forge, RunnerStatus = session }).StateTitle, Is.EqualTo("Forge Neo session running"));
            Assert.That((view with { State = SupervisorState.Running, ActiveTemplate = comfyUi, Launcher = online }).StateTitle, Is.EqualTo("Ready for sessions"));
            Assert.That((view with { State = SupervisorState.Running, ActiveTemplate = comfyUi, Launcher = online }).IsWarm, Is.True);
            Assert.That((view with { State = SupervisorState.Running, ActiveTemplate = comfyUi, Launcher = online, ReleaseDue = true }).IsWarm, Is.False, "Released as soon as it is idle.");
            Assert.That((view with { State = SupervisorState.Running, ActiveTemplate = comfyUi }).StateTitle, Is.EqualTo("Running"), "Offloadr cannot start sessions here now.");
            Assert.That((view with { State = SupervisorState.Faulted, Fault = new SupervisorFault(FaultKind.AppOutdated, "Update the app", "Offloadr needs a newer app.", null) }).StateTitle, Is.EqualTo("Update needed"));
            Assert.That(SupervisorView.EditorName(new LaunchTemplate("other", "other", "image", "Other editor - nightly")), Is.EqualTo("Other editor"));
        });
    }

    [Test]
    public void Validate_AcceptsCompleteSettings()
    {
        var settings = new RunnerSettings { RunnerKey = ValidKey };

        Assert.That(settings.Validate(_ => true), Is.Empty);
    }

    [Test]
    public void Validate_RejectsAKeyShorterThanTheAgentAccepts()
    {
        var settings = new RunnerSettings { RunnerKey = ValidKey[..^1] };

        Assert.That(settings.Validate(_ => true), Has.Some.Contains("too short"));
    }

    [TestCase("http://offloadr.studio")]
    [TestCase("https://offloadr.studio/app")]
    [TestCase("offloadr.studio")]
    public void Validate_RequiresAnHttpsOrigin(string origin)
    {
        var settings = new RunnerSettings { RunnerKey = ValidKey, OffloadrApiOrigin = origin };

        Assert.That(settings.Validate(_ => true), Has.Some.Contains("https://"));
    }

    [Test]
    public void Validate_ReportsAMissingKeyAndModelsFolder()
    {
        var settings = new RunnerSettings { LocalModelsDirectory = "/missing" };

        var problems = settings.Validate(_ => false);

        Assert.Multiple(() =>
        {
            Assert.That(problems, Has.Some.Contains("runner key"));
            Assert.That(problems, Has.Some.Contains("/missing"));
        });
    }

    [Test]
    public void Validate_DownloadsNeedAModelsFolder()
    {
        var withoutFolder = new RunnerSettings { DownloadModels = true };
        var withFolder = new RunnerSettings { DownloadModels = true, LocalModelsDirectory = Path.GetTempPath() };

        Assert.Multiple(() =>
        {
            Assert.That(withoutFolder.Validate(_ => true), Has.Member(RunnerSettings.DownloadsNeedFolderProblem));
            Assert.That(withFolder.Validate(_ => true), Has.No.Member(RunnerSettings.DownloadsNeedFolderProblem));
        });
    }
}

public class FileRunnerSettingsStoreTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "runner-settings-tests-" + Guid.NewGuid().ToString("n"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Save_StoresTheKeyOnlyInProtectedForm_AndLoadsItBack()
    {
        var paths = new SupervisorPaths(_root);
        var store = new FileRunnerSettingsStore(paths, new ReversingSecretProtector(), NullLogger<FileRunnerSettingsStore>.Instance);
        RunnerSettings? changed = null;
        store.Changed += settings => changed = settings;

        await store.SaveAsync(new RunnerSettings
        {
            OffloadrApiOrigin = " https://example.test/ ",
            RunnerKey = " plain-runner-key ",
            SelectedEditorTemplate = "comfyui-master",
            AutoStart = true,
            DownloadModels = true,
        });

        var reloaded = new FileRunnerSettingsStore(paths, new ReversingSecretProtector(), NullLogger<FileRunnerSettingsStore>.Instance).Current;
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(paths.SettingsFile), Does.Not.Contain("plain-runner-key"));
            Assert.That(changed?.RunnerKey, Is.EqualTo("plain-runner-key"));
            Assert.That(reloaded.RunnerKey, Is.EqualTo("plain-runner-key"));
            Assert.That(reloaded.OffloadrApiOrigin, Is.EqualTo("https://example.test"));
            Assert.That(reloaded.SelectedEditorTemplate, Is.EqualTo("comfyui-master"));
            Assert.That(reloaded.AutoStart, Is.True);
            Assert.That(reloaded.DownloadModels, Is.True);
        });
    }

    [Test]
    public async Task TheWarmTime_IsTenMinutesUnlessSaved_EvenInSettingsFromBeforeTheOption()
    {
        var paths = new SupervisorPaths(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(paths.SettingsFile, """{"OffloadrApiOrigin":"https://offloadr.studio","AutoStart":true}""");
        var store = new FileRunnerSettingsStore(paths, new ReversingSecretProtector(), NullLogger<FileRunnerSettingsStore>.Instance);
        var older = store.Current.WarmMinutes;

        await store.SaveAsync(store.Current with { WarmMinutes = 45 });
        var reloaded = new FileRunnerSettingsStore(paths, new ReversingSecretProtector(), NullLogger<FileRunnerSettingsStore>.Instance).Current;

        Assert.Multiple(() =>
        {
            Assert.That(older, Is.EqualTo(10));
            Assert.That(reloaded.WarmMinutes, Is.EqualTo(45));
        });
    }

    [Test]
    public async Task KeepAwakeDuringSessions_IsOnUnlessSavedOff_EvenInSettingsFromBeforeTheOption()
    {
        var paths = new SupervisorPaths(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(paths.SettingsFile, """{"OffloadrApiOrigin":"https://offloadr.studio","AutoStart":true}""");
        var store = new FileRunnerSettingsStore(paths, new ReversingSecretProtector(), NullLogger<FileRunnerSettingsStore>.Instance);
        var older = store.Current.KeepAwakeDuringSessions;

        await store.SaveAsync(store.Current with { KeepAwakeDuringSessions = false });
        var reloaded = new FileRunnerSettingsStore(paths, new ReversingSecretProtector(), NullLogger<FileRunnerSettingsStore>.Instance).Current;

        Assert.Multiple(() =>
        {
            Assert.That(new RunnerSettings().KeepAwakeDuringSessions, Is.True);
            Assert.That(older, Is.True);
            Assert.That(reloaded.KeepAwakeDuringSessions, Is.False);
        });
    }

    [Test]
    public async Task EditorArguments_AreSavedPerFamilyAndTrimmed()
    {
        var paths = new SupervisorPaths(_root);
        var store = new FileRunnerSettingsStore(paths, new ReversingSecretProtector(), NullLogger<FileRunnerSettingsStore>.Instance);
        var editor = new RunnerSettingsEditor(store);

        await editor.SaveAsync(editor.Load() with { ComfyUiArguments = " --lowvram ", ForgeNeoArguments = "   " });
        var reloaded = new FileRunnerSettingsStore(paths, new ReversingSecretProtector(), NullLogger<FileRunnerSettingsStore>.Instance).Current;

        Assert.Multiple(() =>
        {
            Assert.That(reloaded.ComfyUiArguments, Is.EqualTo("--lowvram"));
            Assert.That(reloaded.ForgeNeoArguments, Is.Null);
            Assert.That(editor.Load().ComfyUiArguments, Is.EqualTo("--lowvram"));
            Assert.That(reloaded.EditorArguments, Is.EqualTo(new[] { ("comfyui", "--lowvram") }));
        });
    }

    [Test]
    public async Task GpuChoice_IsSavedTrimmed_AndBlankMeansEveryGpu()
    {
        var paths = new SupervisorPaths(_root);
        var store = new FileRunnerSettingsStore(paths, new ReversingSecretProtector(), NullLogger<FileRunnerSettingsStore>.Instance);
        var editor = new RunnerSettingsEditor(store);

        await editor.SaveAsync(editor.Load() with { GpuUuid = " GPU-aaaaaaaa-1111-2222-3333-444444444444 " });
        var chosen = new FileRunnerSettingsStore(paths, new ReversingSecretProtector(), NullLogger<FileRunnerSettingsStore>.Instance).Current;
        await editor.SaveAsync(editor.Load() with { GpuUuid = "  " });
        var cleared = new FileRunnerSettingsStore(paths, new ReversingSecretProtector(), NullLogger<FileRunnerSettingsStore>.Instance).Current;

        Assert.Multiple(() =>
        {
            Assert.That(chosen.GpuUuid, Is.EqualTo("GPU-aaaaaaaa-1111-2222-3333-444444444444"));
            Assert.That(chosen.SelectedGpuUuid, Is.EqualTo("GPU-aaaaaaaa-1111-2222-3333-444444444444"));
            Assert.That(cleared.GpuUuid, Is.Null);
            Assert.That(cleared.SelectedGpuUuid, Is.Null);
            Assert.That(new SettingsForm("key", "comfyui-latest", null, null, null, false, GpuUuid: " ").SameAs(
                new SettingsForm("key", "comfyui-latest", null, null, null, false)), Is.True);
        });
    }

    [TestCase(null, null)]
    [TestCase("GPU-AAAAAAAA-1111-2222-3333-444444444444", null)]
    [TestCase("GPU-0000", "The GPU 'GPU-0000' is not a complete NVIDIA GPU UUID")]
    [TestCase("00000000-1111-2222-3333-444444444444", "The GPU '00000000-1111-2222-3333-444444444444' is not a complete NVIDIA GPU UUID")]
    public void Validate_AcceptsOnlyACompleteGpuUuid(string? gpu, string? expected)
    {
        var problems = new RunnerSettings
        {
            RunnerKey = new string('k', RunnerSettings.MinimumRunnerKeyLength),
            GpuUuid = gpu,
        }.Validate();

        Assert.That(problems, expected is null ? Is.Empty : Has.One.StartsWith(expected));
    }

    [Test]
    public void SettingsForm_TreatsBlankAndSpacedArgumentsAsUnchanged()
    {
        var saved = new SettingsForm("key", "comfyui-latest", null, null, null, false, ComfyUiArguments: "--lowvram");

        Assert.Multiple(() =>
        {
            Assert.That((saved with { ComfyUiArguments = " --lowvram ", ForgeNeoArguments = "" }).SameAs(saved), Is.True);
            Assert.That((saved with { ForgeNeoArguments = "--sage" }).SameAs(saved), Is.False);
        });
    }

    [TestCase("--lowvram", null, null)]
    [TestCase("--port 9000", null, "Extra ComfyUI arguments: --port is one the runner sets")]
    [TestCase(null, "--theme 'dark", "Extra Forge Neo arguments: A single quote")]
    [TestCase(null, "--cuda-device 1", "Extra Forge Neo arguments: --cuda-device chooses the GPU")]
    public void Validate_ChecksEachFamilysArgumentsWithItsOwnFlags(string? comfyUi, string? forgeNeo, string? expected)
    {
        var problems = new RunnerSettings
        {
            RunnerKey = new string('k', RunnerSettings.MinimumRunnerKeyLength),
            ComfyUiArguments = comfyUi,
            ForgeNeoArguments = forgeNeo,
        }.Validate();

        Assert.That(problems, expected is null ? Is.Empty : Has.One.StartsWith(expected));
    }

    [Test]
    public async Task SettingsEditor_KeepsTheKeepAwakeChoice()
    {
        var store = new FileRunnerSettingsStore(new SupervisorPaths(_root), new ReversingSecretProtector(), NullLogger<FileRunnerSettingsStore>.Instance);
        var editor = new RunnerSettingsEditor(store);

        await editor.SaveAsync(editor.Load() with { KeepAwakeDuringSessions = false });

        Assert.Multiple(() =>
        {
            Assert.That(store.Current.KeepAwakeDuringSessions, Is.False);
            Assert.That(editor.Load().KeepAwakeDuringSessions, Is.False);
        });
    }

    [Test]
    public void Load_DropsAKeyThatCanNoLongerBeDecrypted()
    {
        var paths = new SupervisorPaths(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(paths.SettingsFile, """{"OffloadrApiOrigin":"https://offloadr.studio","ProtectedRunnerKey":"from-another-machine"}""");

        var store = new FileRunnerSettingsStore(paths, new ReversingSecretProtector(), NullLogger<FileRunnerSettingsStore>.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(store.Current.RunnerKey, Is.Null);
            Assert.That(store.Current.OffloadrApiOrigin, Is.EqualTo("https://offloadr.studio"));
        });
    }

    [Test]
    public void Load_UsesDefaultsForACorruptFile()
    {
        var paths = new SupervisorPaths(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(paths.SettingsFile, "{ not json");

        var store = new FileRunnerSettingsStore(paths, new ReversingSecretProtector(), NullLogger<FileRunnerSettingsStore>.Instance);

        Assert.That(store.Current, Is.EqualTo(new RunnerSettings()));
    }
}

public class LaunchPlanTests
{
    [Test]
    public void Choose_PrefersTheControlPlaneSelection_ThenSettings_ThenTheFirstTemplate()
    {
        var plan = BuiltInLaunchPlanCatalog.Plan();
        var settings = new RunnerSettings { SelectedEditorTemplate = "forge-neo:neo" };

        Assert.Multiple(() =>
        {
            Assert.That((plan with { SelectedEditorTemplate = "comfyui-master" }).Choose(settings)?.EditorTemplate, Is.EqualTo("comfyui-master"));
            Assert.That(plan.Choose(settings)?.EditorTemplate, Is.EqualTo("forge-neo:neo"));
            Assert.That(plan.Choose(new RunnerSettings())?.EditorTemplate, Is.EqualTo("comfyui-latest"));
            Assert.That(plan.Choose(new RunnerSettings { SelectedEditorTemplate = "retired" })?.EditorTemplate, Is.EqualTo("comfyui-latest"));
        });
    }
}

public class HostGpuProbeTests
{
    [Test]
    public void Parse_ReadsUuidNameAndMemory()
    {
        var gpus = NvidiaSmiHostGpuProbe.Parse(
            "GPU-00000000-1111-2222-3333-444444444444, NVIDIA GeForce RTX 4090, 24564\n" +
            "GPU-AAAAAAAA-1111-2222-3333-444444444444, NVIDIA RTX A6000, 49140\n\ngarbage\nNVIDIA RTX 3090, 24576\n");

        Assert.That(gpus, Is.EqualTo(new[]
        {
            new HostGpu("NVIDIA GeForce RTX 4090", 24564, "GPU-00000000-1111-2222-3333-444444444444"),
            new HostGpu("NVIDIA RTX A6000", 49140, "GPU-aaaaaaaa-1111-2222-3333-444444444444"),
        }));
    }

    [Test]
    public void Parse_ListsAGpuWithoutAUuidButCannotChooseIt()
    {
        var gpus = NvidiaSmiHostGpuProbe.Parse("[N/A], NVIDIA GeForce RTX 4090, 24564\n");

        Assert.That(gpus, Is.EqualTo(new[] { new HostGpu("NVIDIA GeForce RTX 4090", 24564) }));
    }
}

public class ContainerEngineSelectionTests
{
    [Test]
    public void Select_KeepsTheDiscoveredExecutableUntilSelectedAgain()
    {
        var found = "/usr/bin/podman";
        var selection = new ContainerEngineSelection(_ => new LocatedEngine(found, ContainerEngineKind.Podman));

        selection.Select(null);
        found = "/usr/bin/docker";

        Assert.Multiple(() =>
        {
            Assert.That(selection.Executable, Is.EqualTo("/usr/bin/podman"));
            Assert.That(selection.Matches(" "), Is.True);
            Assert.That(selection.Matches("docker"), Is.False);
        });

        selection.Select(null);
        Assert.That(selection.Executable, Is.EqualTo("/usr/bin/docker"));
    }

    [Test]
    public void Select_KeepsAConfiguredNameThatIsNotFound()
    {
        var selection = new ContainerEngineSelection(_ => null);

        selection.Select(" docker-custom ");

        Assert.That(selection.Executable, Is.EqualTo("docker-custom"));
    }
}

public class ContainerEngineHintTests
{
    [Test]
    public void SupervisorOwner_IsKeptAcrossRestartsOfTheApp()
    {
        var directory = Directory.CreateTempSubdirectory("offloadr-owner-");
        try
        {
            var path = Path.Combine(directory.FullName, "owner");
            var first = SupervisorOwner.LoadOrCreate(path);
            var again = SupervisorOwner.LoadOrCreate(path);
            var elsewhere = SupervisorOwner.LoadOrCreate(Path.Combine(directory.FullName, "other", "owner"));

            Assert.Multiple(() =>
            {
                Assert.That(again.Id, Is.EqualTo(first.Id));
                Assert.That(elsewhere.Id, Is.Not.EqualTo(first.Id));
                Assert.That(Directory.GetFiles(directory.FullName), Is.EquivalentTo(new[] { path, path + ".lock" }));
            });
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task SupervisorOwner_CopiesOpeningAtTheSameMoment_ShareTheSavedId()
    {
        var directory = Directory.CreateTempSubdirectory("offloadr-owner-");
        try
        {
            for (var round = 0; round < 50; round++)
            {
                var path = Path.Combine(directory.FullName, $"owner-{round}");
                using var start = new Barrier(16);
                // A thread each, like separate processes: blocking pool threads would only slow the pool down.
                var owners = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Factory.StartNew(
                    () =>
                    {
                        start.SignalAndWait();
                        return SupervisorOwner.LoadOrCreate(path).Id;
                    },
                    TaskCreationOptions.LongRunning)));

                Assert.That(owners, Is.All.EqualTo(File.ReadAllText(path)), $"round {round}");
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task SupervisorOwner_FileAnotherCopyIsStillWriting_IsLeftToIt()
    {
        var directory = Directory.CreateTempSubdirectory("offloadr-owner-");
        try
        {
            var path = Path.Combine(directory.FullName, "owner");
            var theirs = Guid.NewGuid().ToString("N");
            Task<SupervisorOwner> loading;

            // Another copy holds the lock and has created the file, but is slow to write it.
            using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                await File.WriteAllTextAsync(path, string.Empty);
                loading = Task.Run(() => SupervisorOwner.LoadOrCreate(path));
                await Task.Delay(1000);
                await File.WriteAllTextAsync(path, theirs);
            }

            var owner = await loading;
            Assert.Multiple(() =>
            {
                Assert.That(owner.Id, Is.EqualTo(theirs));
                Assert.That(File.ReadAllText(path), Is.EqualTo(theirs));
            });
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public void SupervisorOwner_FileWithoutAnId_IsReplaced()
    {
        var directory = Directory.CreateTempSubdirectory("offloadr-owner-");
        try
        {
            var path = Path.Combine(directory.FullName, "owner");
            File.WriteAllText(path, "not an id");

            var owner = SupervisorOwner.LoadOrCreate(path);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(path), Is.EqualTo(owner.Id));
                Assert.That(SupervisorOwner.LoadOrCreate(path).Id, Is.EqualTo(owner.Id));
            });
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public void Remember_LeavesPasswordsInEngineAddressesOut()
    {
        var directory = Directory.CreateTempSubdirectory("offloadr-hint-");
        try
        {
            var statePath = Path.Combine(directory.FullName, "engine.json");
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Podman);
            var selection = new ContainerEngineSelection(Locate, statePath);
            selection.Select("podman");
            selection.PinContext(new ContainerEnginePin(null, ContainerEngineKind.Podman)
            {
                Endpoint = new Dictionary<string, string> { ["CONTAINER_HOST"] = "ssh://builder:hunter2@build-server.example:22/run/podman/podman.sock" },
                Remote = true,
            });
            selection.Remember();

            var restored = new ContainerEngineSelection(Locate, statePath);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(statePath), Does.Not.Contain("hunter2"));
                Assert.That(selection.Pin?.Endpoint?["CONTAINER_HOST"], Does.Contain("hunter2"));
                Assert.That(restored.TryRestore(), Is.True);
                Assert.That(restored.Pin?.Endpoint?["CONTAINER_HOST"], Is.EqualTo("ssh://builder@build-server.example:22/run/podman/podman.sock"));
                Assert.That(restored.Pin?.Remote, Is.True);
            });
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public void Remember_KeepsWhetherThePinIsAPodmanConnection()
    {
        var directory = Directory.CreateTempSubdirectory("offloadr-hint-");
        try
        {
            var statePath = Path.Combine(directory.FullName, "engine.json");
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);
            var selection = new ContainerEngineSelection(Locate, statePath);
            selection.Select("docker");
            selection.PinContext(new ContainerEnginePin("podman-machine-default", ContainerEngineKind.Podman));
            selection.Remember();

            var restored = new ContainerEngineSelection(Locate, statePath);
            Assert.Multiple(() =>
            {
                Assert.That(restored.TryRestore(), Is.True);
                Assert.That(restored.Pin, Is.EqualTo(new ContainerEnginePin("podman-machine-default", ContainerEngineKind.Podman)));
            });

            // An endpoint the environment chose is kept with all its variables.
            var endpoint = new ContainerEnginePin(null, ContainerEngineKind.Docker)
            {
                Endpoint = new Dictionary<string, string> { ["DOCKER_HOST"] = "tcp://build-host:2376", ["DOCKER_TLS_VERIFY"] = "1" },
            };
            selection.PinContext(endpoint);
            selection.Remember();
            Assert.Multiple(() =>
            {
                Assert.That(restored.TryRestore(), Is.True);
                Assert.That(restored.Pin, Is.EqualTo(endpoint));
                Assert.That(restored.Pin?.Endpoint?["DOCKER_TLS_VERIFY"], Is.EqualTo("1"));
            });
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public void Remember_ReplacesTheHintWithoutLeavingATemporaryFile()
    {
        var directory = Directory.CreateTempSubdirectory("offloadr-hint-");
        try
        {
            var statePath = Path.Combine(directory.FullName, "engine.json");
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);
            var selection = new ContainerEngineSelection(Locate, statePath);

            selection.Select("docker-a");
            selection.Remember();
            selection.Select("docker-b");
            selection.Remember();

            var restored = new ContainerEngineSelection(Locate, statePath);
            Assert.Multiple(() =>
            {
                Assert.That(restored.TryRestore(), Is.True);
                Assert.That(restored.Executable, Is.EqualTo("docker-b"));
                Assert.That(Directory.GetFiles(directory.FullName), Is.EqualTo(new[] { statePath }));
            });
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}

public class ModelsFolderValidationTests
{
    [Test]
    public void Validate_RejectsAModelsFolderThatIsNotAFullPath()
    {
        var settings = new RunnerSettings
        {
            RunnerKey = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG",
            LocalModelsDirectory = "models",
        };

        Assert.That(settings.Validate(_ => true), Has.Some.Contains("must be a full path"));
    }

    [TestCase("/srv/models:v2", true)]
    [TestCase(@"D:\models", false)]
    [TestCase(@"D:\models:v2", true)]
    public void Validate_RejectsAColonInTheModelsFolder(string folder, bool rejected)
    {
        var settings = new RunnerSettings
        {
            RunnerKey = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG",
            LocalModelsDirectory = folder,
        };

        Assert.That(settings.Validate(_ => true).Any(error => error.Contains("cannot contain ':'", StringComparison.Ordinal)), Is.EqualTo(rejected));
    }
}

public class RunnerSettingsEditorTests
{
    [Test]
    public void Load_StartsTheFormWithTheFirstEditorWhenNoneIsChosen()
    {
        var editor = new RunnerSettingsEditor(new FakeSettingsStore(new RunnerSettings()));

        Assert.That(editor.Load().SelectedEditorTemplate, Is.EqualTo(BuiltInLaunchPlanCatalog.Templates[0].EditorTemplate));
    }

    [Test]
    public async Task Save_UsesTheDefaultAddressForABlankOne_AndReportsWhatIsStillWrong()
    {
        var store = new FakeSettingsStore(new RunnerSettings());
        var editor = new RunnerSettingsEditor(store);

        var result = await editor.SaveAsync(new SettingsForm(null, "comfyui-latest", null, "  ", null, AutoStart: true, DownloadModels: true));

        Assert.Multiple(() =>
        {
            Assert.That(result.Saved, Is.True);
            Assert.That(store.Current.OffloadrApiOrigin, Is.EqualTo(RunnerSettings.DefaultOffloadrApiOrigin));
            Assert.That(store.Current.AutoStart, Is.True);
            Assert.That(store.Current.DownloadModels, Is.True);
            Assert.That(editor.Load().DownloadModels, Is.True);
            Assert.That(result.Problems, Is.Not.Empty);
        });
    }

    [Test]
    public async Task Save_ThatCannotBeWritten_SaysWhy()
    {
        var editor = new RunnerSettingsEditor(new UnwritableSettingsStore());

        var result = await editor.SaveAsync(new SettingsForm(null, "comfyui-latest", null, null, null, AutoStart: false));

        Assert.Multiple(() =>
        {
            Assert.That(result.Saved, Is.False);
            Assert.That(result.Error, Does.Contain("Access to the settings file is denied"));
        });
    }

    private sealed class UnwritableSettingsStore : IRunnerSettingsStore
    {
        public RunnerSettings Current { get; } = new();

        public event Action<RunnerSettings>? Changed
        {
            add { }
            remove { }
        }

        public Task SaveAsync(RunnerSettings settings, CancellationToken cancellationToken = default)
            => Task.FromException(new UnauthorizedAccessException("Access to the settings file is denied."));
    }
}
