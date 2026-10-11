using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Processes;
using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor.Tests;

public class RunnerSupervisorTests
{
    private const string Key = "runner-key-0123456789abcdefghijklmnopqrstuvwxyz";
    private const string Name = "offloadr-runner";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private FakeContainerEngine _engine = null!;
    private FakeLaunchPlanProvider _plans = null!;
    private FakeSettingsStore _settings = null!;
    private ContainerEngineSelection _selection = null!;
    private RunnerSupervisor? _supervisor;

    [SetUp]
    public void SetUp()
    {
        // Settings name the executable in these tests; nothing is discovered on this machine.
        _selection = new ContainerEngineSelection(configured => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker));
        _engine = new FakeContainerEngine { Executable = () => _selection.Executable };
        _plans = new FakeLaunchPlanProvider();
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key });
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_supervisor is not null)
        {
            await _supervisor.StopAsync(CancellationToken.None);
            _supervisor.Dispose();
        }
    }

    [Test]
    public async Task Start_PullsAndRunsTheSelectedImage_WithTheKeyOnlyInTheEnvironment()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, SelectedEditorTemplate = "forge-neo:neo" });
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        var run = _engine.Runs.Single();
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Pulls, Is.EqualTo(new[] { "ghcr.io/offloadr/runner-forge-neo:neo" }));
            Assert.That(run.Image, Is.EqualTo("ghcr.io/offloadr/runner-forge-neo:neo"));
            Assert.That(run.Gpus, Is.True);
            Assert.That(run.Environment["RUNNER_SECRET"], Is.EqualTo(Key));
            Assert.That(run.Environment["OFFLOADR_API_GRPC"], Is.EqualTo(RunnerSettings.DefaultOffloadrApiOrigin));
            Assert.That(run.Labels[RunnerSupervisor.ManagedLabel], Is.EqualTo("true"));
            Assert.That(run.Labels[RunnerSupervisor.TemplateLabel], Is.EqualTo("forge-neo:neo"));
            Assert.That(run.Labels[RunnerSupervisor.SpecLabel], Does.Not.Contain(Key));
            Assert.That(DockerCliArguments.Run(run, ContainerEngineKind.Docker), Has.None.Contains(Key));
            Assert.That(supervisor.View.ActiveTemplate?.EditorTemplate, Is.EqualTo("forge-neo:neo"));
            Assert.That(supervisor.View.Image?.RepoDigest, Does.EndWith("@sha256:digest"));
        });
    }

    [Test]
    public async Task Start_MountsTheModelsFolderReadOnly()
    {
        var models = Path.GetTempPath();
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, LocalModelsDirectory = models });
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        var run = _engine.Runs.Single();
        Assert.Multiple(() =>
        {
            Assert.That(run.Volumes, Is.EqualTo(new[] { new VolumeMount(models, "/local", true) }));
            Assert.That(run.Environment["RUNNER_LOCAL_MODELS_DIR"], Is.EqualTo("/local"));
        });
    }

    [Test]
    public async Task Start_WithoutARunnerKey_Faults()
    {
        _settings = new FakeSettingsStore(new RunnerSettings());
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Message, Does.Contain("runner key"));
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task Start_WhenDockerIsNotRunning_Faults()
    {
        _engine.Info = ContainerEngineInfo.Unavailable("Docker is installed but its engine is not running.");
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        Assert.That(supervisor.View.Message, Does.Contain("not running"));
    }

    [Test]
    public async Task Start_WhenThePullFailsWithoutALocalImage_Faults()
    {
        _engine.PullFails = true;
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Message, Does.Contain("Could not pull"));
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task Start_WhenThePullFailsWithALocalImage_RunsTheLocalCopy()
    {
        _engine.PullFails = true;
        _engine.AddImage("ghcr.io/offloadr/runner-comfyui:latest");
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        Assert.That(supervisor.LogLines, Has.Some.Contains("Using the copy already on this machine"));
    }

    [Test]
    public async Task Start_RefusesToReplaceAContainerItDidNotCreate()
    {
        _engine.AddContainer(Name, "someone/else", running: true, new Dictionary<string, string>());
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Message, Does.Contain("was not started by this app"));
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task Stop_StopsAndRemovesTheContainer()
    {
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Stops, Is.EqualTo(1));
            Assert.That(_engine.Exists(Name), Is.False);
        });
    }

    [Test]
    public async Task Stop_DuringAPull_CancelsTheDownload()
    {
        var pullStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnPull = async token =>
        {
            pullStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await pullStarted.Task.WaitAsync(WaitLimit);

        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);

        Assert.That(_engine.Runs, Is.Empty);
    }

    [Test]
    public async Task UnexpectedExit_RestartsAfterABackoff()
    {
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        _engine.Exit(Name, 137);

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Restarts, Is.EqualTo(1));
            Assert.That(supervisor.LogLines, Has.Some.Contains("exit code 137"));
        });
    }

    [Test]
    public async Task Startup_AdoptsARunningContainerItCreatedEarlier()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, SelectedEditorTemplate = "comfyui-master" });
        var template = BuiltInLaunchPlanCatalog.Plan().Find("comfyui-master")!;
        var earlier = new RunnerSupervisor(_settings, _engine, _plans, new FakeHostGpuProbe(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance);
        var spec = earlier.BuildSpec(_settings.Current, template);
        _engine.AddContainer(Name, spec.Image, running: true, spec.Labels);

        var supervisor = await StartSupervisorAsync();

        await WaitForStateAsync(SupervisorState.Running);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(supervisor.View.Message, Does.Contain("Reconnected"));
            Assert.That(supervisor.View.ActiveTemplate?.EditorTemplate, Is.EqualTo("comfyui-master"));
        });
    }

    [Test]
    public async Task Startup_RemovesAStoppedContainerItCreatedEarlier()
    {
        _engine.AddContainer(Name, "image", running: false, new Dictionary<string, string>
        {
            [RunnerSupervisor.ManagedLabel] = "true",
            [RunnerSupervisor.OwnerLabel] = SupervisorOwner.Default.Id,
        });

        var supervisor = await StartSupervisorAsync();

        await WaitUntilAsync(() => !_engine.Exists(Name));
        Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Stopped));
    }

    [Test]
    public async Task Startup_WithAutoStart_StartsTheRunner()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, AutoStart = true });

        await StartSupervisorAsync();

        await WaitForStateAsync(SupervisorState.Running);
    }

    [Test]
    public async Task Start_WithARunningContainerMissedAtStartup_AdoptsIt()
    {
        var earlier = new RunnerSupervisor(_settings, _engine, _plans, new FakeHostGpuProbe(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance);
        var spec = earlier.BuildSpec(_settings.Current, BuiltInLaunchPlanCatalog.Plan().Find("comfyui-latest")!);
        _engine.AddContainer(Name, spec.Image, running: true, spec.Labels);
        _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
        var supervisor = await StartSupervisorAsync();
        await WaitForStateAsync(SupervisorState.Faulted);

        _engine.InspectFailure = null;
        supervisor.RequestStart();

        await WaitForStateAsync(SupervisorState.Running);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(supervisor.View.Message, Does.Contain("Reconnected"));
        });
    }

    [Test]
    public async Task SettingsChange_ForARunnerAdoptedDuringAnOutage_KeepsItsImage()
    {
        _plans.Plan = BuiltInLaunchPlanCatalog.Plan("Could not reach Offloadr.", LaunchPlanSource.Unreachable);
        var running = new LaunchTemplate("comfyui-master", "comfyui", "ghcr.io/offloadr/runner-comfyui@sha256:master", "ComfyUI - master branch");
        var earlier = new RunnerSupervisor(_settings, _engine, _plans, new FakeHostGpuProbe(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance);
        var spec = earlier.BuildSpec(_settings.Current, running);
        _engine.AddContainer(Name, spec.Image, running: true, spec.Labels);
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);

        var models = Path.GetTempPath();
        await _settings.SaveAsync(_settings.Current with { LocalModelsDirectory = models });

        await WaitUntilAsync(() => _engine.Runs.Count == 1 && supervisor.View.State == SupervisorState.Running);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs[0].Image, Is.EqualTo(running.Image));
            Assert.That(_engine.Runs[0].Labels[RunnerSupervisor.TemplateLabel], Is.EqualTo("comfyui-master"));
            Assert.That(_engine.Runs[0].Volumes, Is.EqualTo(new[] { new VolumeMount(models, "/local", true) }));
        });
    }

    [Test]
    public async Task Logs_WhenFollowingStops_AreFollowedAgainFromNewLines()
    {
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => _engine.LogTails.Count == 1);

        _engine.DropLogs(new ContainerEngineException("error during connect"));

        await WaitUntilAsync(() => _engine.LogTails.Count == 2);
        _engine.WriteLog("after the reconnect");
        await WaitUntilAsync(() => supervisor.LogLines.Contains("after the reconnect"));
        Assert.Multiple(() =>
        {
            Assert.That(_engine.LogTails, Is.EqualTo(new[] { 200, 0 }));
            Assert.That(supervisor.LogLines, Has.Some.Contains("error during connect"));
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));
        });
    }

    [Test]
    public async Task StopPressedBeforeAQueuedStartRuns_KeepsTheRunnerStopped()
    {
        var supervisor = await StartSupervisorAsync();
        await WaitUntilAsync(() => supervisor.View.Engine is not null);
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnProbe = async _ =>
        {
            checking.TrySetResult();
            await release.Task;
        };
        supervisor.RequestRefresh();
        await checking.Task.WaitAsync(WaitLimit);

        supervisor.RequestStart();
        supervisor.RequestStop();
        _engine.OnProbe = null;
        release.TrySetResult();
        await Task.Delay(300);

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Pulls, Is.Empty);
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Stopped));
        });
    }

    [Test]
    public async Task Start_WhenAnotherCopyStartsTheRunnerDuringTheDownload_AdoptsIt()
    {
        var other = new RunnerSupervisor(_settings, _engine, _plans, new FakeHostGpuProbe(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance);
        var spec = other.BuildSpec(_settings.Current, BuiltInLaunchPlanCatalog.Plan().Find("comfyui-latest")!);
        _engine.OnPull = _ =>
        {
            _engine.AddContainer(Name, spec.Image, running: true, spec.Labels);
            return Task.CompletedTask;
        };
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();

        await WaitForStateAsync(SupervisorState.Running);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(supervisor.View.Message, Does.Contain("Reconnected"));
        });
    }

    [Test]
    public async Task DiscoveredEngine_StaysPinnedWhileTheRunnerRuns()
    {
        var discovered = "/usr/bin/podman";
        _selection = new ContainerEngineSelection(configured => new LocatedEngine(configured ?? Volatile.Read(ref discovered), ContainerEngineKind.Podman));
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        // Another engine appears on PATH while the runner runs.
        Volatile.Write(ref discovered, "/usr/bin/docker");
        supervisor.RequestRefresh();
        await Task.Delay(200);
        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);

        Assert.Multiple(() =>
        {
            Assert.That(_engine.RunExecutables, Is.EqualTo(new[] { "/usr/bin/podman" }));
            Assert.That(_engine.StopExecutables, Is.EqualTo(new[] { "/usr/bin/podman" }));
            Assert.That(_engine.ExistsOn("/usr/bin/podman", Name), Is.False);
        });
    }

    [Test]
    public async Task StopPressedAfterASettingsChange_WinsOverTheSwitch()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);
        var refreshing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _plans.OnGet = async _ =>
        {
            refreshing.TrySetResult();
            await release.Task;
        };
        supervisor.RequestRefresh();
        await refreshing.Task.WaitAsync(WaitLimit);

        // Queued behind the refresh in progress: a settings change that needs a switch, then Stop.
        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });
        supervisor.RequestStop();
        _plans.OnGet = null;
        release.TrySetResult();

        await WaitForStateAsync(SupervisorState.Stopped);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
            Assert.That(_engine.Pulls, Has.Count.EqualTo(1));
            Assert.That(_engine.Exists(Name), Is.False);
        });
    }

    [Test]
    public async Task PlanChange_WhenTheNewImageCannotBeDownloaded_KeepsTheRunnerRunning()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);

        _engine.PullFails = true;
        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });
        await WaitUntilAsync(() => supervisor.View.Message?.Contains("keeps running and tries again", StringComparison.Ordinal) == true);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.RestartPending));
        });

        _engine.PullFails = false;

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.That(_engine.Runs[1].Image, Is.EqualTo("ghcr.io/offloadr/runner-comfyui:master"));
    }

    [Test]
    public async Task KeyChange_RejectedByOffloadr_KeepsTheRunnerRunning()
    {
        const string rejectedKey = "rejected-runner-key-0123456789abcdefghijklmnop";
        _engine.StatusJson = Status(activeSessionId: "");
        _plans.Rejects = settings => settings.RunnerKey == rejectedKey;
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);

        await _settings.SaveAsync(_settings.Current with { RunnerKey = rejectedKey });

        await WaitUntilAsync(() => supervisor.View.Message?.Contains("did not accept", StringComparison.Ordinal) == true);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.RestartPending));
        });
    }

    [Test]
    public async Task SecondSupervisor_WithTheSameLock_LeavesTheRunnerAlone()
    {
        var lockPath = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n") + ".lock");
        using var firstLock = new SupervisorInstanceLock(lockPath);
        using var secondLock = new SupervisorInstanceLock(lockPath);
        var first = await StartSupervisorAsync(instanceLock: firstLock);
        first.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        using var second = new RunnerSupervisor(_settings, _engine, _plans, new FakeHostGpuProbe(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance, _selection, secondLock);
        await second.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => second.View.State == SupervisorState.Faulted);
        second.RequestStart();
        await second.StopAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(second.View.Message, Does.Contain("Another copy"));
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(first.View.State, Is.EqualTo(SupervisorState.Running));
        });
    }

    [Test]
    public async Task Startup_AfterACrash_FindsTheRunnerOnTheEngineItWasStartedOn()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);

            // The earlier run started the runner on engine A and remembered it.
            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
            _selection = new ContainerEngineSelection(Locate, statePath);
            _selection.Select("docker-a");
            _selection.Remember();
            var earlier = new RunnerSupervisor(_settings, _engine, _plans, new FakeHostGpuProbe(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance);
            var spec = earlier.BuildSpec(_settings.Current, BuiltInLaunchPlanCatalog.Plan().Find("comfyui-latest")!);
            _engine.AddContainer(Name, spec.Image, running: true, spec.Labels);

            // Settings moved to engine B while a session kept the runner on A; then the app crashed.
            await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
            _selection = new ContainerEngineSelection(Locate, statePath);
            _engine.StatusJson = Status(activeSessionId: "session-1");
            var supervisor = await StartSupervisorAsync();

            await WaitForStateAsync(SupervisorState.RestartPending);
            Assert.Multiple(() =>
            {
                Assert.That(_engine.Runs, Is.Empty);
                Assert.That(_engine.Stops, Is.Zero);
                Assert.That(_engine.ExistsOn("docker-a", Name), Is.True);
            });

            _engine.StatusJson = Status(activeSessionId: "");

            await WaitUntilAsync(() => _engine.Runs.Count == 1 && supervisor.View.State == SupervisorState.Running);
            Assert.Multiple(() =>
            {
                Assert.That(_engine.StopExecutables, Is.EqualTo(new[] { "docker-a" }));
                Assert.That(_engine.RunExecutables, Is.EqualTo(new[] { "docker-b" }));
                Assert.That(_engine.ExistsOn("docker-a", Name), Is.False);
            });
        }
        finally
        {
            DeleteQuietly(statePath);
        }
    }

    [Test]
    public async Task AutoStart_WhileDockerIsStillStarting_KeepsTrying()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, AutoStart = true });
        var available = _engine.Info;
        _engine.Info = ContainerEngineInfo.Unavailable("Docker Desktop is starting.");
        var supervisor = await StartSupervisorAsync();

        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("Trying again", StringComparison.Ordinal)));
        Assert.That(supervisor.View.State, Is.Not.EqualTo(SupervisorState.Faulted));
        _engine.Info = available;

        await WaitForStateAsync(SupervisorState.Running);
        Assert.That(_engine.Runs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Startup_WhenTheRememberedEngineDoesNotAnswer_StartsNothingElsewhere()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);

            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
            _selection = new ContainerEngineSelection(Locate, statePath);
            _selection.Select("docker-a");
            _selection.Remember();
            var earlier = new RunnerSupervisor(_settings, _engine, _plans, new FakeHostGpuProbe(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance);
            var spec = earlier.BuildSpec(_settings.Current, BuiltInLaunchPlanCatalog.Plan().Find("comfyui-latest")!);
            _engine.AddContainer(Name, spec.Image, running: true, spec.Labels);

            // Settings moved to engine B, the app crashed, and engine A does not answer at the next start.
            await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b", AutoStart = true });
            _selection = new ContainerEngineSelection(Locate, statePath);
            _engine.UnavailableEngines["docker-a"] = true;
            _engine.StatusJson = Status(activeSessionId: "");
            var supervisor = await StartSupervisorAsync();
            await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("may still be running on docker-a", StringComparison.Ordinal)));
            await Task.Delay(200);
            Assert.Multiple(() =>
            {
                Assert.That(_engine.Runs, Is.Empty);
                Assert.That(File.Exists(statePath), Is.True);
            });

            // Engine A answers again: the runner is found there and moved to B once idle.
            _engine.UnavailableEngines.TryRemove("docker-a", out _);

            await WaitUntilAsync(() => _engine.Runs.Count == 1 && supervisor.View.State == SupervisorState.Running);
            Assert.Multiple(() =>
            {
                Assert.That(_engine.StopExecutables, Is.EqualTo(new[] { "docker-a" }));
                Assert.That(_engine.RunExecutables, Is.EqualTo(new[] { "docker-b" }));
            });
        }
        finally
        {
            DeleteQuietly(statePath);
        }
    }

    [Test]
    public async Task SettingsEditedDuringASwitchDownload_LeaveTheRunnerRunning()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);
        _engine.OnPull = async _ =>
        {
            // The person changes Settings again, to an address the runner cannot use, while the
            // switch downloads its image.
            _engine.OnPull = null;
            await _settings.SaveAsync(_settings.Current with { OffloadrApiOrigin = "http://offloadr.example" });
        };

        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });
        await WaitUntilAsync(() => _engine.Pulls.Count == 2);
        await Task.Delay(300);

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task MovedTag_SwitchesToTheNewerImageOnceIdle()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(imageUpdateInterval: TimeSpan.FromMilliseconds(50));
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);

        _engine.ImageIds["ghcr.io/offloadr/runner-comfyui:latest"] = "sha256:newer";

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        await Task.Delay(300);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Has.Count.EqualTo(2));
            Assert.That(_engine.Runs[1].Image, Is.EqualTo("ghcr.io/offloadr/runner-comfyui:latest"));
            Assert.That(supervisor.LogLines, Has.Some.Contains("a newer ghcr.io/offloadr/runner-comfyui:latest"));
        });
    }

    [Test]
    public async Task TagMovedDuringTheLaunch_IsRecordedAsTheImageThatRuns()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        _engine.BeforeRun = () => _engine.ImageIds["ghcr.io/offloadr/runner-comfyui:latest"] = "sha256:newer";
        var supervisor = await StartSupervisorAsync(imageUpdateInterval: TimeSpan.FromMilliseconds(50));

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await Task.Delay(500);

        // The runner already runs the image the tag names, so the update check finds nothing newer.
        Assert.That(_engine.Runs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task SwitchWhoseImageChangesDuringTheLaunch_StartsAgainWithTheCheckedTag()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);

        // Another pull moves the tag between the switch's checks and its `docker run`.
        _engine.BeforeRun = () => _engine.ImageIds["ghcr.io/offloadr/runner-comfyui:master"] = "sha256:moved";
        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });

        await WaitUntilAsync(() => _engine.Runs.Count == 3 && supervisor.View.State == SupervisorState.Running);
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.LogLines, Has.Some.Contains("changed while the runner was starting"));
            Assert.That(_engine.Runs[2].Image, Is.EqualTo("ghcr.io/offloadr/runner-comfyui:master"));
        });
    }

    [Test]
    public async Task SwitchWhoseStopFails_KeepsFollowingTheRunnerLog()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null && _engine.LogTails.Count == 1);
        _engine.NextStopFailure = new ContainerEngineException("Docker did not stop container offloadr-runner within 60 seconds.");

        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });

        // The failed stop resumes following the still-running runner; the retried switch then follows the new one.
        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.LogTails, Is.EqualTo(new[] { 200, 0, 200 }));
            Assert.That(supervisor.LogLines, Has.Some.Contains("did not stop container"));
        });
    }

    [Test]
    public async Task Startup_FindsARunnerRenamedBeforeACrash()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);
            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, AutoStart = true });
            var spec = new RunnerSupervisor(_settings, _engine, _plans, new FakeHostGpuProbe(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance)
                .BuildSpec(_settings.Current, BuiltInLaunchPlanCatalog.Plan().Find("comfyui-latest")!);

            // The runner was renamed, then the app crashed; the hint holds its ID.
            _engine.AddContainer("something-else", spec.Image, running: true, spec.Labels, spec.Environment);
            var id = (await _engine.InspectContainerAsync("something-else", CancellationToken.None))!.Id;
            var remembered = new ContainerEngineSelection(Locate, statePath);
            remembered.Select(null);
            Assert.That(remembered.Remember(id, out _), Is.True);
            _selection = new ContainerEngineSelection(Locate, statePath);

            var supervisor = await StartSupervisorAsync();

            await WaitForStateAsync(SupervisorState.Running);
            await Task.Delay(200);
            Assert.That(_engine.Runs, Is.Empty);
        }
        finally
        {
            DeleteQuietly(statePath);
        }
    }

    [Test]
    public async Task Startup_WhenTheRememberedEngineIsNoLongerInstalled_StartsNothingElsewhere()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            var remembered = new ContainerEngineSelection(configured => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker), statePath);
            remembered.Select("docker-a");
            remembered.Remember();

            // The CLI of engine A is gone; its daemon may still run the runner.
            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-b", AutoStart = true });
            _selection = new ContainerEngineSelection(configured => configured == "docker-b" ? new LocatedEngine(configured, ContainerEngineKind.Docker) : null, statePath);
            _engine.UnavailableEngines["docker-a"] = true;
            var supervisor = await StartSupervisorAsync();

            await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("may still be running on docker-a", StringComparison.Ordinal)));
            await Task.Delay(200);
            Assert.Multiple(() =>
            {
                Assert.That(_engine.Runs, Is.Empty);
                Assert.That(File.Exists(statePath), Is.True);
            });
        }
        finally
        {
            DeleteQuietly(statePath);
        }
    }

    [Test]
    public async Task StartPressed_WhileAutoStartWaitsForTheRememberedEngine_RunsOnTheEngineInSettings()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);

            var remembered = new ContainerEngineSelection(Locate, statePath);
            remembered.Select("docker-a");
            remembered.Remember();
            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-b", AutoStart = true });
            _selection = new ContainerEngineSelection(Locate, statePath);
            _engine.UnavailableEngines["docker-a"] = true;
            var supervisor = await StartSupervisorAsync();
            await WaitForStateAsync(SupervisorState.WaitingToRestart);
            Assert.That(supervisor.View.Message, Does.Contain("Press Start"));

            supervisor.RequestStart();

            await WaitForStateAsync(SupervisorState.Running);
            Assert.That(_engine.RunExecutables, Is.EqualTo(new[] { "docker-b" }));
        }
        finally
        {
            DeleteQuietly(statePath);
        }
    }

    [Test]
    public async Task SettingsChangedDuringTheFirstDownload_StartWithTheNewSettings()
    {
        var models = Path.GetTempPath();
        _engine.OnPull = async _ =>
        {
            _engine.OnPull = null;
            await _settings.SaveAsync(_settings.Current with { LocalModelsDirectory = models });
        };
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();

        await WaitUntilAsync(() => _engine.Runs.Count == 1 && supervisor.View.State == SupervisorState.Running);
        Assert.That(_engine.Runs[0].Volumes, Is.EqualTo(new[] { new VolumeMount(models, "/local", true) }));
    }

    [Test]
    public async Task KeyClearedDuringTheFirstDownload_DoesNotStartTheRunner()
    {
        _engine.OnPull = async _ =>
        {
            _engine.OnPull = null;
            await _settings.SaveAsync(_settings.Current with { RunnerKey = null });
        };
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();

        await WaitForStateAsync(SupervisorState.Faulted);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(supervisor.View.Message, Does.Contain("runner key"));
        });
    }

    [Test]
    public async Task EngineChange_ToAnEngineThatDoesNotAnswer_KeepsTheRunnerRunning()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);
        _engine.UnavailableEngines["docker-b"] = true;

        await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });

        await WaitUntilAsync(() => supervisor.View.Message?.Contains("docker-b does not answer", StringComparison.Ordinal) == true);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(_engine.ExistsOn("docker-a", Name), Is.True);
        });

        _engine.UnavailableEngines.TryRemove("docker-b", out _);

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.RunExecutables, Is.EqualTo(new[] { "docker-a", "docker-b" }));
            Assert.That(_engine.StopExecutables, Is.EqualTo(new[] { "docker-a" }));
        });
    }

    [Test]
    public async Task FailedLaunch_RemovesTheCreatedContainerAndForgetsTheEngine()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
            _selection = new ContainerEngineSelection(configured => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker), statePath);
            _engine.NextRunFailure = new ContainerEngineException("Could not start the runner container.", "could not select device driver \"nvidia\"");
            var supervisor = await StartSupervisorAsync();

            supervisor.RequestStart();
            await WaitForStateAsync(SupervisorState.Faulted);

            Assert.Multiple(() =>
            {
                Assert.That(_engine.Exists(Name), Is.False);
                Assert.That(File.Exists(statePath), Is.False);
            });

            // Nothing is owned any more, so a check after an engine change uses the new engine.
            await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
            await WaitUntilAsync(() => _selection.Executable == "docker-b");
        }
        finally
        {
            DeleteQuietly(statePath);
        }
    }

    [Test]
    public async Task FailedLaunch_OnAContainerSomeoneElseCreated_LeavesItAlone()
    {
        _engine.NextRunConflict = true;
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        var other = await _engine.InspectContainerAsync(Name, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(other?.Image, Is.EqualTo("someone-elses-image"));
            Assert.That(other?.Running, Is.True);
            Assert.That(_engine.Stops, Is.Zero);
        });
    }

    [Test]
    public async Task RunnerExit_DuringASwitchDownload_IsNoticedRightAway()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnPull = async token =>
        {
            _engine.OnPull = null;
            downloading.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });
        await downloading.Task.WaitAsync(WaitLimit);

        _engine.Exit(Name, 137);

        // Seen while the download still hangs: monitoring does not wait for it.
        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("exit code 137", StringComparison.Ordinal)));
        Assert.That(release.Task.IsCompleted, Is.False);
        release.TrySetResult();
        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.That(_engine.Runs[1].Image, Is.EqualTo("ghcr.io/offloadr/runner-comfyui:master"));
    }

    [Test]
    public async Task TemplateChangedDuringTheFirstDownload_StartsThatTemplate()
    {
        _engine.OnPull = async _ =>
        {
            _engine.OnPull = null;
            await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });
        };
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();

        await WaitUntilAsync(() => _engine.Runs.Count == 1 && supervisor.View.State == SupervisorState.Running);
        Assert.That(_engine.Runs[0].Image, Is.EqualTo("ghcr.io/offloadr/runner-comfyui:master"));
    }

    [Test]
    public async Task PendingSwitch_DuringASession_LeavesTheTargetEngineAlone()
    {
        _engine.StatusJson = Status(activeSessionId: "session-1");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.HasActiveSession);

        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });
        await WaitUntilAsync(() => supervisor.View.Message?.Contains("current session ends", StringComparison.Ordinal) == true);
        var probes = 0;
        _engine.OnProbe = _ =>
        {
            Interlocked.Increment(ref probes);
            return Task.CompletedTask;
        };
        await Task.Delay(300);

        Assert.That(Volatile.Read(ref probes), Is.Zero);
    }

    [Test]
    public async Task EngineSwitch_ProbesTheNewEngineBeforeAskingWhereItsCommandsGo()
    {
        _engine.StatusJson = Status(activeSessionId: "session-1");
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.HasActiveSession);

        // Podman installed as `docker` is only recognized by the probe.
        await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
        await WaitUntilAsync(() => supervisor.View.Message?.Contains("current session ends", StringComparison.Ordinal) == true);

        Assert.That(_engine.ContextsReadUnprobed.Keys, Is.Empty);
    }

    [Test]
    public async Task EnginePreparedDuringASession_IsCheckedAgainBeforeTheSwitch()
    {
        _engine.StatusJson = Status(activeSessionId: "session-1");
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.HasActiveSession);

        // Engine B is prepared while the session keeps the runner on A ...
        await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
        await WaitUntilAsync(() => supervisor.View.Message?.Contains("current session ends", StringComparison.Ordinal) == true);

        // ... then stops answering before the session ends.
        _engine.UnavailableEngines["docker-b"] = true;
        _engine.StatusJson = Status(activeSessionId: "");

        await WaitUntilAsync(() => supervisor.View.Message?.Contains("docker-b does not answer", StringComparison.Ordinal) == true);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(_engine.ExistsOn("docker-a", Name), Is.True);
        });

        _engine.UnavailableEngines.TryRemove("docker-b", out _);

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.That(_engine.RunExecutables, Is.EqualTo(new[] { "docker-a", "docker-b" }));
    }

    [Test]
    public async Task StartPressed_AfterTheOldEngineQuit_RunsOnTheEngineInSettings()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        // Docker quits, the person picks another engine and presses Stop, then Start.
        _engine.UnavailableEngines["docker-a"] = true;
        await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Faulted);

        supervisor.RequestStart();

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.RunExecutables, Is.EqualTo(new[] { "docker-a", "docker-b" }));
            Assert.That(supervisor.LogLines, Has.Some.Contains("starting on the engine in Settings, as asked"));
        });
    }

    [Test]
    public async Task CheckGpuAccess_WithoutDocker_ReportsAFailedCheck()
    {
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        _engine.InspectImageFailure = new ContainerEngineException("Docker was not found.");

        var check = await supervisor.CheckGpuAccessAsync();

        Assert.Multiple(() =>
        {
            Assert.That(check.Succeeded, Is.False);
            Assert.That(check.Output, Does.Contain("Docker was not found"));
            Assert.That(supervisor.View.GpuAccess, Is.EqualTo(check));
        });
    }

    [Test]
    public async Task Stop_DuringASwitchDownload_CancelsTheDownload()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnPull = async token =>
        {
            _engine.OnPull = null;
            downloading.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        };
        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });
        await downloading.Task.WaitAsync(WaitLimit);

        supervisor.RequestStop();

        await WaitForStateAsync(SupervisorState.Stopped);
        await cancelled.Task.WaitAsync(WaitLimit);
        Assert.That(_engine.Runs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task CheckGpuAccess_WhenTheEngineCannotStart_ReportsAFailedCheck()
    {
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        _engine.InspectImageFailure = new ProcessStartException("docker", new UnauthorizedAccessException("Access is denied."));

        var check = await supervisor.CheckGpuAccessAsync();

        Assert.Multiple(() =>
        {
            Assert.That(check.Succeeded, Is.False);
            Assert.That(check.Output, Does.Contain("Access is denied"));
            Assert.That(supervisor.View.GpuAccess, Is.EqualTo(check));
        });
    }

    [Test]
    public async Task Startup_WithAStoppedRunnerOnTheRememberedEngine_RemovesIt()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);

            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
            _selection = new ContainerEngineSelection(Locate, statePath);
            _selection.Select("docker-a");
            _selection.Remember();
            var earlier = new RunnerSupervisor(_settings, _engine, _plans, new FakeHostGpuProbe(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance);
            var spec = earlier.BuildSpec(_settings.Current, BuiltInLaunchPlanCatalog.Plan().Find("comfyui-latest")!);
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels);

            await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
            _selection = new ContainerEngineSelection(Locate, statePath);
            var supervisor = await StartSupervisorAsync();

            await WaitUntilAsync(() => !File.Exists(statePath));
            Assert.Multiple(() =>
            {
                Assert.That(_engine.ExistsOn("docker-a", Name), Is.False);
                Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Stopped));
            });
        }
        finally
        {
            DeleteQuietly(statePath);
        }
    }

    [Test]
    public async Task AdoptedRunner_WhenItsKeyIsBackInSettings_ShowsItsLogAgain()
    {
        const string earlierKey = "earlier-runner-key-0123456789abcdefghijklmnop";
        var earlierSettings = new FakeSettingsStore(new RunnerSettings { RunnerKey = earlierKey });
        var earlier = new RunnerSupervisor(earlierSettings, _engine, _plans, new FakeHostGpuProbe(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance);
        var spec = earlier.BuildSpec(earlierSettings.Current, BuiltInLaunchPlanCatalog.Plan().Find("comfyui-latest")!);
        _engine.AddContainer(Name, spec.Image, running: true, spec.Labels);
        _engine.StatusJson = Status(activeSessionId: "session-1");
        _engine.LogLine = $"echo {earlierKey}";
        var supervisor = await StartSupervisorAsync();
        await WaitForStateAsync(SupervisorState.RestartPending);
        Assert.That(_engine.LogTails, Is.Empty);

        await _settings.SaveAsync(_settings.Current with { RunnerKey = earlierKey });

        await WaitUntilAsync(() => _engine.LogTails.Count == 1 && supervisor.View.State == SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.StartsWith("echo", StringComparison.Ordinal)));
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.LogLines, Has.None.Contains(earlierKey));
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task Launch_WhenTheEngineCannotStart_DropsOwnershipAndTheEngineHint()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
            _selection = new ContainerEngineSelection(configured => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker), statePath);
            _engine.NextRunStartFailure = new ProcessStartException("docker-a", new UnauthorizedAccessException("Access is denied."));
            var supervisor = await StartSupervisorAsync();

            supervisor.RequestStart();
            await WaitForStateAsync(SupervisorState.Faulted);

            Assert.Multiple(() =>
            {
                Assert.That(supervisor.View.Message, Does.Contain("Access is denied"));
                Assert.That(File.Exists(statePath), Is.False);
            });

            await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
            await WaitUntilAsync(() => _selection.Executable == "docker-b");
        }
        finally
        {
            DeleteQuietly(statePath);
        }
    }

    [Test]
    public async Task Switch_StartsThePreparedImageWithoutPullingAgain()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);

        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.Multiple(() =>
        {
            // One pull for the first start, one in the background before the switch; none after the stop.
            Assert.That(_engine.Pulls, Is.EqualTo(new[] { "ghcr.io/offloadr/runner-comfyui:latest", "ghcr.io/offloadr/runner-comfyui:master" }));
            Assert.That(_engine.Runs[1].Image, Is.EqualTo("ghcr.io/offloadr/runner-comfyui:master"));
        });
    }

    [Test]
    public async Task Stop_AfterAnOldProbeSaidDockerWasDown_StillStopsTheRunner()
    {
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        var available = _engine.Info;

        // Docker hiccups on the first Stop, and a Refresh while it is down records it as unavailable.
        _engine.NextStopFailure = new ContainerEngineException("Docker did not answer.");
        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Faulted);
        _engine.Info = ContainerEngineInfo.Unavailable("Docker Desktop is restarting.");
        supervisor.RequestRefresh();
        await WaitUntilAsync(() => supervisor.View.Engine?.Available == false);

        _engine.Info = available;
        supervisor.RequestStop();

        await WaitForStateAsync(SupervisorState.Stopped);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.False);
            Assert.That(_engine.Stops, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SettingsEditedDuringTheFinalEngineCheck_LeaveTheRunnerRunning()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);
        var probes = 0;
        _engine.OnProbe = async _ =>
        {
            // The first probe is the background download's; the second is the check right before the stop.
            if (Interlocked.Increment(ref probes) == 2)
            {
                _engine.OnProbe = null;
                await _settings.SaveAsync(_settings.Current with { OffloadrApiOrigin = "http://offloadr.example" });
            }
        };

        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });
        await WaitUntilAsync(() => Volatile.Read(ref probes) >= 2);
        await Task.Delay(300);

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task PlanRefresh_WhenOffloadrBrieflyDoesNotOfferPlans_KeepsTheRunningImage()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        _plans.Plan = ControlPlanePlan(selected: "comfyui-master");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);

        // A 404 from a proxy or a deploy in progress reads as "not offered".
        _plans.Plan = BuiltInLaunchPlanCatalog.Plan("This Offloadr server does not suggest a runner image yet.");
        supervisor.RequestRefresh();
        await Task.Delay(300);

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
            Assert.That(_engine.Pulls, Has.Count.EqualTo(1));
            Assert.That(supervisor.View.Plan?.Source, Is.EqualTo(LaunchPlanSource.ControlPlane));
        });
    }

    [Test]
    public async Task StopPressedDuringTheStartupCheck_WinsOverAutoStart()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, AutoStart = true });
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnProbe = async _ =>
        {
            _engine.OnProbe = null;
            checking.TrySetResult();
            await release.Task;
        };
        var supervisor = await StartSupervisorAsync();
        await checking.Task.WaitAsync(WaitLimit);

        // Docker is slow at sign-in; the person presses Stop before the check finishes.
        supervisor.RequestStop();
        release.TrySetResult();
        await Task.Delay(300);

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Pulls, Is.Empty);
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Stopped));
        });
    }

    [Test]
    public async Task Stop_BeforeTheRememberedEngineWasChecked_KeepsTheEngineHint()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);

            var remembered = new ContainerEngineSelection(Locate, statePath);
            remembered.Select("docker-a");
            remembered.Remember();
            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-b" });
            _selection = new ContainerEngineSelection(Locate, statePath);
            _engine.UnavailableEngines["docker-a"] = true;
            var supervisor = await StartSupervisorAsync();
            await WaitForStateAsync(SupervisorState.Faulted);

            // A Refresh moves commands to the engine in Settings; then the person presses Stop.
            supervisor.RequestRefresh();
            await WaitUntilAsync(() => _selection.Executable == "docker-b");
            supervisor.RequestStop();
            await WaitForStateAsync(SupervisorState.Stopped);

            Assert.That(File.Exists(statePath), Is.True);
        }
        finally
        {
            DeleteQuietly(statePath);
        }
    }

    [Test]
    public async Task Launch_WhenTheEngineHintCannotBeSaved_DoesNotStartTheRunner()
    {
        // The hint's folder cannot be created because a file has that name.
        var blocker = Path.Combine(Path.GetTempPath(), "offloadr-blocker-" + Guid.NewGuid().ToString("n"));
        await File.WriteAllTextAsync(blocker, string.Empty);
        try
        {
            _selection = new ContainerEngineSelection(configured => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker), Path.Combine(blocker, "engine.json"));
            var supervisor = await StartSupervisorAsync();

            supervisor.RequestStart();
            await WaitForStateAsync(SupervisorState.Faulted);

            Assert.Multiple(() =>
            {
                Assert.That(supervisor.View.Message, Does.Contain("Could not save which container engine runs the runner"));
                Assert.That(_engine.Runs, Is.Empty);
            });
        }
        finally
        {
            DeleteQuietly(blocker);
        }
    }

    [Test]
    public async Task AdoptedRunner_WithARejectedKeyInSettings_DoesNotAskForThePlanEveryTick()
    {
        const string earlierKey = "earlier-runner-key-0123456789abcdefghijklmnop";
        var earlierSettings = new FakeSettingsStore(new RunnerSettings { RunnerKey = earlierKey });
        var earlier = new RunnerSupervisor(earlierSettings, _engine, _plans, new FakeHostGpuProbe(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance);
        var spec = earlier.BuildSpec(earlierSettings.Current, BuiltInLaunchPlanCatalog.Plan().Find("comfyui-latest")!);
        _engine.AddContainer(Name, spec.Image, running: true, spec.Labels);
        _engine.StatusJson = Status(activeSessionId: "session-1");
        _plans.Rejects = settings => settings.RunnerKey == Key;
        var supervisor = await StartSupervisorAsync();
        await WaitForStateAsync(SupervisorState.RestartPending);

        await Task.Delay(500);

        // About 25 monitor ticks passed; the plan is asked for at adoption and on the refresh interval only.
        Assert.That(_plans.Calls, Is.LessThanOrEqualTo(3));
    }

    [Test]
    public async Task RestartAfterAnExit_WhileTheNewKeyIsRejected_UsesTheKeyTheRunnerHad()
    {
        const string rejectedKey = "rejected-runner-key-0123456789abcdefghijklmnop";
        _engine.StatusJson = Status(activeSessionId: "");
        _plans.Rejects = settings => settings.RunnerKey == rejectedKey;
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await _settings.SaveAsync(_settings.Current with { RunnerKey = rejectedKey });
        await WaitUntilAsync(() => supervisor.View.Message?.Contains("did not accept", StringComparison.Ordinal) == true);

        _engine.Exit(Name, 137);

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State is SupervisorState.Running or SupervisorState.RestartPending);
        Assert.That(_engine.Runs[1].Environment["RUNNER_SECRET"], Is.EqualTo(Key));
    }

    [Test]
    public async Task InstanceLock_ThatCannotBeOpened_FaultsWithTheReason()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("A read-only file stops only a non-root process from opening it for writing.");
        }

        var lockPath = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n") + ".lock");
        await File.WriteAllTextAsync(lockPath, string.Empty);
        File.SetAttributes(lockPath, FileAttributes.ReadOnly);
        try
        {
            using var instanceLock = new SupervisorInstanceLock(lockPath);
            var supervisor = await StartSupervisorAsync(instanceLock: instanceLock);

            await WaitForStateAsync(SupervisorState.Faulted);
            Assert.That(supervisor.View.Message, Does.Contain("cannot use its data folder"));
        }
        finally
        {
            File.SetAttributes(lockPath, FileAttributes.Normal);
            DeleteQuietly(lockPath);
        }
    }

    [Test]
    public async Task Shutdown_WhenTheHostWillNotWait_StillRemovesTheRunner()
    {
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        _engine.StopDuration = TimeSpan.FromMilliseconds(300);

        // The host's shutdown timeout has already passed.
        await supervisor.StopAsync(new CancellationToken(canceled: true));
        _supervisor = null;
        supervisor.Dispose();

        Assert.That(_engine.Exists(Name), Is.False);
    }

    [Test]
    public async Task Launch_PinsTheDockerContextInTheEngineHint()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);

            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
            _selection = new ContainerEngineSelection(Locate, statePath);
            _engine.CurrentContext = "desktop-linux";
            var supervisor = await StartSupervisorAsync();
            supervisor.RequestStart();
            await WaitForStateAsync(SupervisorState.Running);

            // `docker context use` elsewhere changes the current context; the runner's commands stay put.
            _engine.CurrentContext = "remote";
            var restored = new ContainerEngineSelection(Locate, statePath);

            Assert.Multiple(() =>
            {
                Assert.That(_selection.Context, Is.EqualTo("desktop-linux"));
                Assert.That(restored.TryRestore(), Is.True);
                Assert.That(restored.Context, Is.EqualTo("desktop-linux"));
            });
        }
        finally
        {
            DeleteQuietly(statePath);
        }
    }

    [Test]
    public async Task AdoptedRunner_WithAnotherKey_IsFollowedRedactedAndRestartsWithItsKey()
    {
        const string rejectedKey = "rejected-runner-key-0123456789abcdefghijklmnop";
        var spec = new RunnerSupervisor(_settings, _engine, _plans, new FakeHostGpuProbe(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance)
            .BuildSpec(_settings.Current, BuiltInLaunchPlanCatalog.Plan().Find("comfyui-latest")!);
        _engine.AddContainer(Name, spec.Image, running: true, spec.Labels, spec.Environment);
        _engine.StatusJson = Status(activeSessionId: "");
        _engine.LogLine = $"echo {Key}";

        // Key B was saved and rejected; then the app crashed and starts again with B in Settings.
        _settings = new FakeSettingsStore(_settings.Current with { RunnerKey = rejectedKey });
        _plans.Rejects = settings => settings.RunnerKey == rejectedKey;
        var supervisor = await StartSupervisorAsync();
        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.StartsWith("echo", StringComparison.Ordinal)));
        Assert.That(supervisor.LogLines, Has.None.Contains(Key));

        _engine.Exit(Name, 137);

        await WaitUntilAsync(() => _engine.Runs.Count == 1);
        Assert.That(_engine.Runs[0].Environment["RUNNER_SECRET"], Is.EqualTo(Key));
    }

    [Test]
    public async Task Launch_PinsTheDockerContextBeforeDownloadingTheImage()
    {
        _engine.CurrentContext = "desktop-linux";
        string? downloadedIn = null;
        _engine.OnPull = _ =>
        {
            downloadedIn = _selection.Context;

            // `docker context use remote` in a terminal while the image downloads.
            _engine.CurrentContext = "remote";
            return Task.CompletedTask;
        };
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        Assert.Multiple(() =>
        {
            Assert.That(downloadedIn, Is.EqualTo("desktop-linux"));
            Assert.That(_selection.Context, Is.EqualTo("desktop-linux"));
        });
    }

    [Test]
    public async Task Switch_OnTheSameEngine_KeepsTheRunnersDockerContext()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        _engine.CurrentContext = "desktop-linux";
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);

        // `docker context use remote` in a terminal, then a plan change switches the runner.
        _engine.CurrentContext = "remote";
        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.That(_selection.Context, Is.EqualTo("desktop-linux"));
    }

    [Test]
    public async Task AdoptedRunner_WhenReadingItsKeyFailsOnce_ReadsItAgain()
    {
        const string rejectedKey = "rejected-runner-key-0123456789abcdefghijklmnop";
        var spec = new RunnerSupervisor(_settings, _engine, _plans, new FakeHostGpuProbe(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance)
            .BuildSpec(_settings.Current, BuiltInLaunchPlanCatalog.Plan().Find("comfyui-latest")!);
        _engine.AddContainer(Name, spec.Image, running: true, spec.Labels, spec.Environment);
        _engine.StatusJson = Status(activeSessionId: "");
        _engine.EnvironmentReadFailures = 1;
        _settings = new FakeSettingsStore(_settings.Current with { RunnerKey = rejectedKey });
        _plans.Rejects = settings => settings.RunnerKey == rejectedKey;
        var supervisor = await StartSupervisorAsync();

        // The first read fails; monitoring reads the key again and follows the log.
        await WaitUntilAsync(() => _engine.LogTails.Count == 1);
        _engine.Exit(Name, 137);

        await WaitUntilAsync(() => _engine.Runs.Count == 1);
        Assert.That(_engine.Runs[0].Environment["RUNNER_SECRET"], Is.EqualTo(Key));
    }

    [Test]
    public async Task EngineSwitch_StartsInTheContextTheTargetWasPreparedIn()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        _engine.CurrentContext = "a-context";
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);

        // The switch to engine B begins in context b-context; `docker context use` changes it while
        // the image downloads there.
        _engine.CurrentContext = "b-context";
        _engine.OnPull = _ =>
        {
            _engine.OnPull = null;
            _engine.CurrentContext = "elsewhere";
            return Task.CompletedTask;
        };
        await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.RunExecutables, Is.EqualTo(new[] { "docker-a", "docker-b" }));
            Assert.That(_selection.Context, Is.EqualTo("b-context"));
        });
    }

    [Test]
    public async Task Switch_WhenTheImageChangedAfterItsDownload_DownloadsItAgainFirst()
    {
        _engine.StatusJson = Status(activeSessionId: "session-1");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.HasActiveSession);

        // The new image is downloaded while the session runs ...
        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });
        await WaitUntilAsync(() => supervisor.View.Message?.Contains("current session ends", StringComparison.Ordinal) == true);

        // ... then the tag names another copy on this engine before the session ends.
        _engine.ImageIds["ghcr.io/offloadr/runner-comfyui:master"] = "sha256:other";
        _engine.StatusJson = Status(activeSessionId: "");

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.LogLines, Has.Some.Contains("changed since it was downloaded"));
            Assert.That(_engine.Pulls.Count(image => image == "ghcr.io/offloadr/runner-comfyui:master"), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task RestartWhileANewKeyAndAddressAreRejected_UsesTheAddressThatAcceptedTheOldKey()
    {
        const string rejectedKey = "rejected-runner-key-0123456789abcdefghijklmnop";
        _engine.StatusJson = Status(activeSessionId: "");
        _plans.Rejects = settings => settings.RunnerKey == rejectedKey;
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await _settings.SaveAsync(_settings.Current with { RunnerKey = rejectedKey, OffloadrApiOrigin = "https://other.example" });
        await WaitUntilAsync(() => supervisor.View.Message?.Contains("did not accept", StringComparison.Ordinal) == true);

        _engine.Exit(Name, 137);

        await WaitUntilAsync(() => _engine.Runs.Count == 2);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs[1].Environment["RUNNER_SECRET"], Is.EqualTo(Key));
            Assert.That(_engine.Runs[1].Environment["OFFLOADR_API_GRPC"], Is.EqualTo(RunnerSettings.DefaultOffloadrApiOrigin));
        });
    }

    [Test]
    public async Task ContainerThatTookTheName_IsNotStoppedAsTheRunner()
    {
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        // Between two checks the runner is gone and someone else's container has its name.
        await _engine.RemoveAsync(Name, CancellationToken.None);
        _engine.AddContainer(Name, "someone-elses-image", running: true, new Dictionary<string, string>());

        await WaitForStateAsync(SupervisorState.Faulted);
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.LogLines, Has.Some.Contains("a different container now has the runner's name"));
            Assert.That(supervisor.View.Message, Does.Contain("was not started by this app"));
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task PreparedSwitch_LaunchesOnTheEngineItWasPreparedOn()
    {
        var discovered = "/usr/bin/docker-x";
        _selection = new ContainerEngineSelection(configured => new LocatedEngine(configured ?? Volatile.Read(ref discovered), ContainerEngineKind.Docker));
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);

        // Discovery would find another engine once the switch has prepared the image.
        _engine.OnPull = _ =>
        {
            _engine.OnPull = null;
            Volatile.Write(ref discovered, "/usr/bin/docker-y");
            return Task.CompletedTask;
        };
        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.That(_engine.RunExecutables, Is.EqualTo(new[] { "/usr/bin/docker-x", "/usr/bin/docker-x" }));
    }

    [Test]
    public async Task Stop_DuringARestartsLaunch_RemovesTheNewContainer()
    {
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        var launching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnRun = async token =>
        {
            _engine.OnRun = null;
            launching.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        // The runner exits; its restart's `docker run` has created the new container when Stop comes.
        _engine.Exit(Name, 137);
        await launching.Task.WaitAsync(WaitLimit);
        supervisor.RequestStop();

        await WaitForStateAsync(SupervisorState.Stopped);
        Assert.That(_engine.Exists(Name), Is.False);
    }

    [Test]
    public async Task SessionStartedDuringTheFinalEngineCheck_IsNotInterrupted()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);
        var probes = 0;
        _engine.OnProbe = _ =>
        {
            // The first probe is the background download's; the second is the slow check before the stop.
            if (Interlocked.Increment(ref probes) == 2)
            {
                _engine.OnProbe = null;
                _engine.StatusJson = Status(activeSessionId: "session-1");
            }

            return Task.CompletedTask;
        };

        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });

        await WaitUntilAsync(() => supervisor.View.Message?.Contains("current session ends", StringComparison.Ordinal) == true);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task FailedImageUpdateCheck_IsRetriedAfterItsBackoff()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(imageUpdateInterval: TimeSpan.FromSeconds(3));
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        _engine.PullFails = true;
        _engine.InspectImageFailure = new ContainerEngineException("docker image inspect timed out.");

        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("trying again", StringComparison.Ordinal)));
        var failedAt = DateTime.UtcNow;
        _engine.PullFails = false;
        _engine.InspectImageFailure = null;
        _engine.ImageIds["ghcr.io/offloadr/runner-comfyui:latest"] = "sha256:newer";

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.That(DateTime.UtcNow - failedAt, Is.LessThan(TimeSpan.FromSeconds(2.5)), "retried after the backoff, not the update interval");
    }

    [Test]
    public async Task ContainerThatTookTheName_IsNotAdoptedEvenWithTheAppsLabel()
    {
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        // Another copy of the app (another data folder, say) put its own runner under the name.
        await _engine.RemoveAsync(Name, CancellationToken.None);
        _engine.AddContainer(Name, "ghcr.io/offloadr/runner-comfyui:latest", running: true, new Dictionary<string, string> { [RunnerSupervisor.ManagedLabel] = "true" });

        await WaitForStateAsync(SupervisorState.Faulted);
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Message, Does.Contain("was not started by this app"));
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task FailedLaunch_LosingTheNameToAnotherCopy_LeavesItsRunnerAlone()
    {
        _engine.NextRunConflict = true;
        _engine.ConflictLabels = new Dictionary<string, string> { [RunnerSupervisor.ManagedLabel] = "true" };
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        Assert.That(_engine.Exists(Name), Is.True);
    }

    [Test]
    public async Task AdoptedRunner_ThatExitsBeforeItsAddressIsRead_RestartsWithWhatItRan()
    {
        const string rejectedKey = "rejected-runner-key-0123456789abcdefghijklmnop";
        var spec = new RunnerSupervisor(_settings, _engine, _plans, new FakeHostGpuProbe(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance)
            .BuildSpec(_settings.Current, BuiltInLaunchPlanCatalog.Plan().Find("comfyui-latest")!);
        _engine.AddContainer(Name, spec.Image, running: true, spec.Labels, spec.Environment);
        _engine.StatusJson = Status(activeSessionId: "");

        // The runner exits while what it was started with is being read, and the read of its
        // address fails: the restart is decided before the next check could read it again.
        _engine.EnvironmentReadFailures = 1;
        _engine.EnvironmentReadFailuresFor = "OFFLOADR_API_GRPC";
        _engine.BeforeEnvironmentRead = () => _engine.Exit(Name, 137);
        _settings = new FakeSettingsStore(_settings.Current with { RunnerKey = rejectedKey, OffloadrApiOrigin = "https://other.example" });
        _plans.Rejects = settings => settings.RunnerKey == rejectedKey;
        await StartSupervisorAsync();

        await WaitUntilAsync(() => _engine.Runs.Count == 1);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs[0].Environment["RUNNER_SECRET"], Is.EqualTo(Key));
            Assert.That(_engine.Runs[0].Environment["OFFLOADR_API_GRPC"], Is.EqualTo(RunnerSettings.DefaultOffloadrApiOrigin));
        });
    }

    [Test]
    public async Task AdoptedRunner_WhenReadingItsAddressFailsOnce_ReadsItAgain()
    {
        const string rejectedKey = "rejected-runner-key-0123456789abcdefghijklmnop";
        var spec = new RunnerSupervisor(_settings, _engine, _plans, new FakeHostGpuProbe(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance)
            .BuildSpec(_settings.Current, BuiltInLaunchPlanCatalog.Plan().Find("comfyui-latest")!);
        _engine.AddContainer(Name, spec.Image, running: true, spec.Labels, spec.Environment);
        _engine.StatusJson = Status(activeSessionId: "");
        _engine.EnvironmentReadFailures = 1;
        _engine.EnvironmentReadFailuresFor = "OFFLOADR_API_GRPC";
        _settings = new FakeSettingsStore(_settings.Current with { RunnerKey = rejectedKey, OffloadrApiOrigin = "https://other.example" });
        _plans.Rejects = settings => settings.RunnerKey == rejectedKey;
        var supervisor = await StartSupervisorAsync();
        await WaitUntilAsync(() => _engine.EnvironmentReadFailures == 0);
        await Task.Delay(200);

        _engine.Exit(Name, 137);

        await WaitUntilAsync(() => _engine.Runs.Count == 1);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs[0].Environment["RUNNER_SECRET"], Is.EqualTo(Key));
            Assert.That(_engine.Runs[0].Environment["OFFLOADR_API_GRPC"], Is.EqualTo(RunnerSettings.DefaultOffloadrApiOrigin));
        });
    }

    [Test]
    public async Task StalledStatus_StaysStale_WhenTheClockGoesBack()
    {
        var time = new ShiftedTimeProvider();
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(time: time, statusStaleAfter: TimeSpan.FromMilliseconds(300));
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);
        await Task.Delay(100);

        // The agent stops publishing, and the clock is set back an hour (after a resume, say).
        _engine.StatusJson = Status(activeSessionId: "", updatedUtc: "2026-10-06T12:00:00+00:00");
        time.Offset = TimeSpan.FromHours(-1);
        await Task.Delay(500);

        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });

        await WaitUntilAsync(() => supervisor.View.Message?.Contains("once the runner reports", StringComparison.Ordinal) == true);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task NewOffloadrAddress_WithoutPlans_UsesTheEditorInSettings()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        _plans.Plan = ControlPlanePlan(selected: "comfyui-master");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);

        // Settings move to another Offloadr server, which does not offer plans yet.
        _plans.Plan = BuiltInLaunchPlanCatalog.Plan("This Offloadr server does not suggest a runner image yet.");
        await _settings.SaveAsync(_settings.Current with { OffloadrApiOrigin = "https://other.example", SelectedEditorTemplate = "forge-neo:neo" });

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs[1].Image, Is.EqualTo("ghcr.io/offloadr/runner-forge-neo:neo"));
            Assert.That(supervisor.View.Plan?.Source, Is.EqualTo(LaunchPlanSource.BuiltIn));
        });
    }

    [Test]
    public async Task RestartWhileTheKeyIsRejectedAtANewAddress_UsesTheAddressThatAcceptedIt()
    {
        const string otherOrigin = "https://other.example";
        _engine.StatusJson = Status(activeSessionId: "");
        _plans.Rejects = settings => settings.OffloadrApiOrigin == otherOrigin;
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        // Same key, another address, which refuses it.
        await _settings.SaveAsync(_settings.Current with { OffloadrApiOrigin = otherOrigin });
        await WaitUntilAsync(() => supervisor.View.Message?.Contains("did not accept", StringComparison.Ordinal) == true);

        _engine.Exit(Name, 137);

        await WaitUntilAsync(() => _engine.Runs.Count == 2);
        Assert.That(_engine.Runs[1].Environment["OFFLOADR_API_GRPC"], Is.EqualTo(RunnerSettings.DefaultOffloadrApiOrigin));
    }

    [Test]
    public async Task AbandonedEngineSwitch_IsPreparedAgainInTheCurrentContext()
    {
        _engine.StatusJson = Status(activeSessionId: "session-1");
        _engine.CurrentContext = "a-context";
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.HasActiveSession);

        // A switch to B begins in context b1, then Settings go back to A.
        _engine.CurrentContext = "b1";
        await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
        await WaitUntilAsync(() => supervisor.View.Message?.Contains("current session ends", StringComparison.Ordinal) == true);
        await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-a" });
        await WaitForStateAsync(SupervisorState.Running);

        // B's default context changes; a new switch to B must use it.
        _engine.CurrentContext = "b2";
        await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
        _engine.StatusJson = Status(activeSessionId: "");

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.That(_selection.Context, Is.EqualTo("b2"));
    }

    [Test]
    public async Task RestartBackoff_IsNotStretched_WhenTheClockGoesBack()
    {
        var time = new ShiftedTimeProvider();
        var supervisor = await StartSupervisorAsync(time: time, restartBackoff: TimeSpan.FromSeconds(1));
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        _engine.Exit(Name, 137);
        await WaitForStateAsync(SupervisorState.WaitingToRestart);
        time.Offset = TimeSpan.FromHours(-1);

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
    }

    [Test]
    public async Task RunnerOfAnotherCopyOfTheApp_IsNeitherAdoptedNorStopped()
    {
        // Another copy (another data folder, or another computer on the same engine) runs its runner.
        var other = new RunnerSupervisor(_settings, _engine, _plans, new FakeHostGpuProbe(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance, owner: new SupervisorOwner("another-copy"));
        var spec = other.BuildSpec(_settings.Current, BuiltInLaunchPlanCatalog.Plan().Find("comfyui-latest")!);
        _engine.AddContainer(Name, spec.Image, running: true, spec.Labels, spec.Environment);
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);
        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task StopDuringALaunchThatLostTheName_LeavesTheOtherLaunchsRunnerAlone()
    {
        var supervisor = await StartSupervisorAsync();
        var replaced = new TaskCompletionSource();
        _engine.OnRun = async cancellationToken =>
        {
            // Another launch with this app's labels ends up with the name, and this `docker run` hangs.
            var labels = new Dictionary<string, string>(_engine.Runs[0].Labels) { [RunnerSupervisor.LaunchLabel] = "another-launch" };
            await _engine.RemoveAsync(Name, CancellationToken.None);
            _engine.AddContainer(Name, _engine.Runs[0].Image, running: true, labels);
            replaced.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        };

        supervisor.RequestStart();
        await replaced.Task.WaitAsync(WaitLimit);
        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task Stop_LeavesAContainerThatTookTheNameAfterTheCheck()
    {
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        // Between the check and the stop, the runner goes and another one takes the name.
        var labels = _engine.Runs[0].Labels;
        _engine.BeforeStop = () =>
        {
            _engine.RemoveAsync(Name, CancellationToken.None).GetAwaiter().GetResult();
            _engine.AddContainer(Name, _engine.Runs[0].Image, running: true, labels);
        };
        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);

        Assert.That(_engine.Exists(Name), Is.True);
    }

    [Test]
    public async Task StatusRead_NeverReadsAContainerThatTookTheNameAfterTheCheck()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);
        var sawTheOtherSession = false;
        supervisor.Changed += () => sawTheOtherSession |= supervisor.View.RunnerStatus?.ActiveSessionId == "someone-elses-session";

        // Between the check and the status read, the runner goes and another one takes the name.
        var replaced = new TaskCompletionSource();
        var labels = _engine.Runs[0].Labels;
        _engine.BeforeRead = () =>
        {
            _engine.RemoveAsync(Name, CancellationToken.None).GetAwaiter().GetResult();
            _engine.AddContainer(Name, _engine.Runs[0].Image, running: true, labels);
            _engine.StatusJson = Status(activeSessionId: "someone-elses-session");
            replaced.SetResult();
        };
        await replaced.Task.WaitAsync(WaitLimit);
        await Task.Delay(300);

        Assert.That(sawTheOtherSession, Is.False);
    }

    [Test]
    public async Task ModelsFolder_WithAnEngineOnAnotherComputer_IsRefused()
    {
        _engine.CurrentContext = "build-server";
        _engine.CurrentContextRemote = true;
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, LocalModelsDirectory = Path.GetTempPath() });
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Message, Does.Contain("models folder"));
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task RenamedRunner_IsStillTheRunner()
    {
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        // `docker rename offloadr-runner something-else`: no second runner starts under the freed name.
        _engine.Rename(Name, "something-else");
        await Task.Delay(300);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));
        });

        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);
        Assert.That(_engine.Exists("something-else"), Is.False);
    }

    [Test]
    public async Task RenamedRunnerThatExits_IsRemovedBeforeTheRestart()
    {
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        _engine.Rename(Name, "something-else");
        _engine.Exit("something-else", 137);

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.That(_engine.Exists("something-else"), Is.False);
    }

    [Test]
    public async Task StopDuringALaunch_RemovesTheContainerTheEngineCreatesAfterTheCancel()
    {
        var supervisor = await StartSupervisorAsync();
        var running = new TaskCompletionSource();
        _engine.RunOverride = async (spec, cancellationToken) =>
        {
            running.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The engine finishes creating the container a moment after the command is gone.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(150);
                    _engine.AddContainer(Name, spec.Image, running: true, spec.Labels, spec.Environment);
                });
                throw;
            }

            return string.Empty;
        };

        supervisor.RequestStart();
        await running.Task.WaitAsync(WaitLimit);
        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);
        await Task.Delay(300);

        Assert.That(_engine.Exists(Name), Is.False);
    }

    [Test]
    public async Task Start_WhenTheEngineCannotTellItsContext_LaunchesNothing()
    {
        _engine.CurrentContextFailure = new ContainerEngineException("Could not read the current Docker context.");
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Message, Does.Contain("current Docker context"));
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task PlanChange_WithoutASession_SwitchesImagesRightAway()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.That(_engine.Runs[1].Image, Is.EqualTo("ghcr.io/offloadr/runner-comfyui:master"));
    }

    [Test]
    public async Task PlanChange_DuringASession_WaitsUntilTheSessionEnds()
    {
        _engine.StatusJson = Status(activeSessionId: "session-1");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.HasActiveSession);

        _plans.Plan = BuiltInLaunchPlanCatalog.Plan() with { SelectedEditorTemplate = "comfyui-master", RefreshInterval = TimeSpan.FromMilliseconds(20) };
        supervisor.RequestRefresh();
        await WaitForStateAsync(SupervisorState.RestartPending);
        await Task.Delay(200);
        Assert.That(_engine.Runs, Has.Count.EqualTo(1));

        _engine.StatusJson = Status(activeSessionId: "");

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.That(_engine.Runs[1].Image, Is.EqualTo("ghcr.io/offloadr/runner-comfyui:master"));
    }

    [Test]
    public async Task PlanChange_WithoutAStatusFile_LeavesTheRestartToThePerson()
    {
        _engine.StatusJson = null;
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });
        await WaitForStateAsync(SupervisorState.RestartPending);
        await Task.Delay(200);

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
            Assert.That(supervisor.View.Message, Does.Contain("Restart the runner"));
        });
    }

    [Test]
    public async Task Shutdown_StopsTheContainerItOwns()
    {
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        await supervisor.StopAsync(CancellationToken.None);
        _supervisor = null;
        supervisor.Dispose();

        Assert.That(_engine.Exists(Name), Is.False);
    }

    [Test]
    public async Task Logs_HideTheRunnerKey()
    {
        _engine.LogLine = $"echo {Key}";
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.StartsWith("echo", StringComparison.Ordinal)));
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.LogLines, Has.None.Contains(Key));
            Assert.That(supervisor.LogLines, Has.Some.EqualTo("echo [runner key]"));
        });
    }

    [Test]
    public async Task Status_IsReadFromTheContainer()
    {
        _engine.StatusJson = Status(activeSessionId: "session-9");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();

        await WaitUntilAsync(() => supervisor.View.RunnerStatus?.ActiveSessionId == "session-9");
        Assert.That(supervisor.View.HasActiveSession, Is.True);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Startup_WhenDockerFails_KeepsHandlingCommands(bool probeFails)
    {
        var failure = new ContainerEngineException("Docker is still starting.");
        if (probeFails)
        {
            _engine.ProbeFailure = failure;
        }
        else
        {
            _engine.InspectFailure = failure;
        }

        var supervisor = await StartSupervisorAsync();
        await WaitForStateAsync(SupervisorState.Faulted);
        Assert.That(supervisor.View.Message, Does.Contain("still starting"));

        _engine.ProbeFailure = null;
        _engine.InspectFailure = null;
        supervisor.RequestStart();

        await WaitForStateAsync(SupervisorState.Running);
    }

    [Test]
    public async Task Startup_WithAnOutdatedContainer_SwitchesOnceNoSessionIsActive()
    {
        var earlier = new RunnerSupervisor(_settings, _engine, _plans, new FakeHostGpuProbe(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance);
        var spec = earlier.BuildSpec(_settings.Current, BuiltInLaunchPlanCatalog.Plan().Find("comfyui-master")!);
        _engine.AddContainer(Name, spec.Image, running: true, spec.Labels);
        _engine.StatusJson = Status(activeSessionId: "session-1");

        var supervisor = await StartSupervisorAsync();
        await WaitForStateAsync(SupervisorState.RestartPending);
        await Task.Delay(200);
        Assert.That(_engine.Runs, Is.Empty);

        _engine.StatusJson = Status(activeSessionId: "");

        await WaitUntilAsync(() => _engine.Runs.Count == 1 && supervisor.View.State == SupervisorState.Running);
        Assert.That(_engine.Runs[0].Image, Is.EqualTo("ghcr.io/offloadr/runner-comfyui:latest"));
    }

    [Test]
    public async Task PlanRefresh_WhenOffloadrIsUnreachable_KeepsTheRunningImage()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        _plans.Plan = ControlPlanePlan(selected: "comfyui-master");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);

        _plans.Plan = BuiltInLaunchPlanCatalog.Plan("Could not reach Offloadr.", LaunchPlanSource.Unreachable);
        supervisor.RequestRefresh();
        await Task.Delay(300);

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
            Assert.That(_engine.Runs[0].Image, Is.EqualTo("ghcr.io/offloadr/runner-comfyui@sha256:master"));
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));
            Assert.That(supervisor.View.Plan?.Source, Is.EqualTo(LaunchPlanSource.ControlPlane));
        });
    }

    [Test]
    public async Task PlanChange_WithAStaleStatus_WaitsForACurrentOne()
    {
        _engine.StatusJson = Status(activeSessionId: "", updatedUtc: "2026-10-06T12:00:00+00:00");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);

        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });
        await WaitForStateAsync(SupervisorState.RestartPending);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
            Assert.That(supervisor.View.Message, Does.Contain("once the runner reports"));
        });

        _engine.StatusJson = Status(activeSessionId: "");

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.That(_engine.Runs[1].Image, Is.EqualTo("ghcr.io/offloadr/runner-comfyui:master"));
    }

    [Test]
    public async Task Shutdown_DuringALaunch_RemovesTheContainerItCreated()
    {
        var launching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnRun = async token =>
        {
            launching.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await launching.Task.WaitAsync(WaitLimit);

        await supervisor.StopAsync(CancellationToken.None);
        _supervisor = null;
        supervisor.Dispose();

        Assert.That(_engine.Exists(Name), Is.False);
    }

    [Test]
    public async Task Logs_HideTheRunningContainersKey_AfterTheKeyChanges()
    {
        const string newKey = "another-runner-key-0123456789abcdefghijklmnop";
        _engine.StatusJson = Status(activeSessionId: "session-1");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.HasActiveSession);

        await _settings.SaveAsync(_settings.Current with { RunnerKey = newKey });
        await WaitForStateAsync(SupervisorState.RestartPending);
        _engine.WriteLog($"old {Key}");

        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.StartsWith("old", StringComparison.Ordinal)));
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.LogLines, Has.None.Contains(Key));
            Assert.That(supervisor.LogLines, Has.Some.EqualTo("old [runner key]"));
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task Monitoring_WhenDockerStopsAnswering_KeepsWatchingTheRunner()
    {
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("did not answer", StringComparison.Ordinal)));
        Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));

        _engine.InspectFailure = null;
        _engine.Exit(Name, 137);

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.That(supervisor.LogLines, Has.Some.Contains("answering again"));
    }

    [Test]
    public async Task RestartAfterAnExit_WhenOffloadrIsUnreachable_KeepsTheChosenImage()
    {
        _plans.Plan = ControlPlanePlan(selected: "comfyui-master");
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        _plans.Plan = BuiltInLaunchPlanCatalog.Plan("Could not reach Offloadr.", LaunchPlanSource.Unreachable);
        _engine.Exit(Name, 137);

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.That(_engine.Runs[1].Image, Is.EqualTo("ghcr.io/offloadr/runner-comfyui@sha256:master"));
    }

    [Test]
    public async Task EngineChange_StopsTheRunnerOnTheEngineThatStartedIt()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);

        await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.RunExecutables, Is.EqualTo(new[] { "docker-a", "docker-b" }));
            Assert.That(_engine.StopExecutables, Is.EqualTo(new[] { "docker-a" }));
            Assert.That(_engine.ExistsOn("docker-a", Name), Is.False);
            Assert.That(_engine.ExistsOn("docker-b", Name), Is.True);
        });
    }

    [Test]
    public async Task Startup_WithAContainerStartedWithAnotherKey_DoesNotShowItsLog()
    {
        const string earlierKey = "earlier-runner-key-0123456789abcdefghijklmnop";
        var earlierSettings = new FakeSettingsStore(new RunnerSettings { RunnerKey = earlierKey });
        var earlier = new RunnerSupervisor(earlierSettings, _engine, _plans, new FakeHostGpuProbe(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance);
        var spec = earlier.BuildSpec(earlierSettings.Current, BuiltInLaunchPlanCatalog.Plan().Find("comfyui-latest")!);
        _engine.AddContainer(Name, spec.Image, running: true, spec.Labels);
        _engine.StatusJson = Status(activeSessionId: "session-1");
        _engine.LogLine = $"echo {earlierKey}";

        var supervisor = await StartSupervisorAsync();
        await WaitForStateAsync(SupervisorState.RestartPending);
        await Task.Delay(200);

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.LogLines, Has.None.Contains(earlierKey));
            Assert.That(supervisor.LogLines, Has.Some.Contains("earlier settings"));
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task RestartAfterAnExit_WhileDockerIsDown_KeepsTrying()
    {
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        var available = _engine.Info;

        _engine.Info = ContainerEngineInfo.Unavailable("Docker Desktop is restarting.");
        _engine.Exit(Name, 1);
        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("Trying again", StringComparison.Ordinal)));
        Assert.That(supervisor.View.State, Is.Not.EqualTo(SupervisorState.Faulted));

        _engine.Info = available;

        await WaitUntilAsync(() => _engine.Runs.Count == 2 && supervisor.View.State == SupervisorState.Running);
    }

    [Test]
    public async Task PlanChange_RightAfterASessionStarts_DoesNotStopTheRunner()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(statusInterval: TimeSpan.FromMilliseconds(400));
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);
        var firstRead = supervisor.View.RunnerStatus!.UpdatedUtc;
        await WaitUntilAsync(() => supervisor.View.RunnerStatus?.UpdatedUtc != firstRead);

        // The session starts after the last periodic read; the switch must notice it anyway.
        _engine.StatusJson = Status(activeSessionId: "session-1");
        await _settings.SaveAsync(_settings.Current with { SelectedEditorTemplate = "comfyui-master" });

        await WaitForStateAsync(SupervisorState.RestartPending);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(supervisor.View.Message, Does.Contain("current session ends"));
        });
    }

    [Test]
    public async Task CheckGpuAccess_BeforeTheFirstStart_UsesTheConfiguredImage()
    {
        _engine.AddImage("ghcr.io/offloadr/runner-comfyui:latest");
        var supervisor = await StartSupervisorAsync();

        var check = await supervisor.CheckGpuAccessAsync();

        Assert.Multiple(() =>
        {
            Assert.That(check.Succeeded, Is.True);
            Assert.That(supervisor.View.GpuAccess, Is.EqualTo(check));
        });
    }

    [Test]
    public async Task CheckGpuAccess_WithoutTheImage_AsksForAFirstStart()
    {
        var supervisor = await StartSupervisorAsync();

        var check = await supervisor.CheckGpuAccessAsync();

        Assert.That(check.Output, Does.Contain("Start the runner once"));
    }

    /// <summary>Test cleanup; a scanner may briefly hold a file the test just wrote.</summary>
    private static void DeleteQuietly(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static LaunchPlan ControlPlanePlan(string selected)
        => new(
            [
                new LaunchTemplate("comfyui-latest", "comfyui", "ghcr.io/offloadr/runner-comfyui@sha256:latest", "ComfyUI - current release"),
                new LaunchTemplate("comfyui-master", "comfyui", "ghcr.io/offloadr/runner-comfyui@sha256:master", "ComfyUI - master branch"),
            ],
            selected,
            TimeSpan.FromMilliseconds(20),
            LaunchPlanSource.ControlPlane);

    private async Task<RunnerSupervisor> StartSupervisorAsync(
        TimeSpan? statusInterval = null,
        SupervisorInstanceLock? instanceLock = null,
        TimeSpan? imageUpdateInterval = null,
        TimeProvider? time = null,
        TimeSpan? statusStaleAfter = null,
        TimeSpan? restartBackoff = null)
    {
        var options = new SupervisorOptions
        {
            MonitorInterval = TimeSpan.FromMilliseconds(20),
            StatusInterval = statusInterval ?? TimeSpan.FromMilliseconds(20),
            ImageUpdateInterval = imageUpdateInterval ?? TimeSpan.FromHours(1),
            RestartMinBackoff = restartBackoff ?? TimeSpan.FromMilliseconds(50),
            RestartMaxBackoff = restartBackoff ?? TimeSpan.FromMilliseconds(100),
            StopTimeout = TimeSpan.FromSeconds(1),
            StatusConfirmDelay = TimeSpan.FromMilliseconds(20),
            CancelledLaunchSettle = TimeSpan.FromSeconds(1),
            StatusStaleAfter = statusStaleAfter ?? TimeSpan.FromMinutes(1),
        };
        _supervisor = new RunnerSupervisor(_settings, _engine, _plans, new FakeHostGpuProbe(), options, time ?? TimeProvider.System, NullLogger<RunnerSupervisor>.Instance, _selection, instanceLock);
        await _supervisor.StartAsync(CancellationToken.None);
        return _supervisor;
    }

    private Task WaitForStateAsync(SupervisorState state) => WaitUntilAsync(() => _supervisor!.View.State == state, $"state {state}");

    private async Task WaitUntilAsync(Func<bool> condition, string? what = null)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for {what ?? "condition"}; supervisor is {_supervisor?.View.State}: {_supervisor?.View.Message}");
            }

            await Task.Delay(10);
        }
    }

    private static string Status(string activeSessionId, string updatedUtc = FakeContainerEngine.StatusNow)
        => $$"""
            {
              "runner_id": "runner",
              "instance_id": "instance",
              "version": "0.1.0",
              "supported_editor_templates": ["comfyui-latest", "comfyui-master"],
              "active_session_id": "{{activeSessionId}}",
              "updated_utc": "{{updatedUtc}}"
            }
            """;
}
