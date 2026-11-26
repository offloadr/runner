using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hardware;
using Offloadr.Runner.Supervisor.Launcher;
using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Settings;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Supervisor.Tests;

public class RunnerSupervisorTests
{
    private const string Key = "runner-key-0123456789abcdefghijklmnopqrstuvwxyz";
    private const string Name = "offloadr-runner";
    private const string ComfyUi = "ghcr.io/offloadr/runner-comfyui:latest";
    private const string ForgeNeo = "ghcr.io/offloadr/runner-forge-neo:neo";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private FakeContainerEngine _engine = null!;
    private FakeLaunchPlanProvider _plans = null!;
    private FakeSettingsStore _settings = null!;
    private FakeLauncherLink _link = null!;
    private FakeHostGpuProbe _hostGpus = null!;
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
        _link = new FakeLauncherLink();
        _hostGpus = new FakeHostGpuProbe();
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

    // Availability

    [Test]
    public async Task Start_MakesTheComputerAvailable_WithoutARunnerContainer()
    {
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Available);
        await WaitUntilAsync(() => supervisor.View.IsReady, "Offloadr to accept the launcher");

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty, "A container starts only for a launch.");
            Assert.That(_link.Running, Is.True);
            Assert.That(_link.Runs.Single().RunnerKey, Is.EqualTo(Key));
            Assert.That(_engine.GpuChecks, Is.EqualTo(1), "Containers must reach the GPU before the computer is offered.");
            Assert.That(_engine.Pulls, Is.EqualTo(new[] { ComfyUi }), "A first start downloads the image the GPU check runs in.");
            Assert.That(supervisor.View.Launcher?.Connection, Is.EqualTo(LauncherConnection.Online));
            Assert.That(supervisor.View.StateTitle, Is.EqualTo("Ready for sessions"));
        });
    }

    [Test]
    public async Task ALauncherOffloadrRefuses_IsNotShownAsReady()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await WaitUntilAsync(() => supervisor.View.IsReady);

        _link.SetConnection(LauncherConnection.Refused, "Offloadr did not accept the runner key.");

        await WaitUntilAsync(() => !supervisor.View.IsReady, "the refusal");
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Available), "The launcher keeps asking.");
            Assert.That(supervisor.View.StateTitle, Is.EqualTo("Not connected to Offloadr"));
            Assert.That(supervisor.View.Message, Does.Contain("did not accept the runner key").And.Contain("keeps trying"));
        });

        _link.SetConnection(LauncherConnection.Online, null);
        await WaitUntilAsync(() => supervisor.View.IsReady, "the recovery");
        Assert.That(supervisor.View.Message, Does.StartWith("Ready for sessions"));
    }

    [Test]
    public async Task ANewKey_OffersTheEditorsOfItsOwnOffloadr()
    {
        const string newKey = "another-runner-key-0123456789abcdefghijklmnop";
        _plans.Plan = ControlPlanePlan("comfyui-latest");
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        Assert.That(_link.Profile!().EditorTemplates, Is.EqualTo(new[] { "comfyui-latest", "comfyui-master" }));

        _plans.Plan = BuiltInLaunchPlanCatalog.Plan("This Offloadr server does not suggest a runner image yet.");
        await _settings.SaveAsync(_settings.Current with { RunnerKey = newKey });

        await WaitUntilAsync(() => _link.Runs.Count == 2 && _link.Running, "the launcher's run with the new key");
        Assert.That(_link.Profile!().EditorTemplates, Is.EqualTo(BuiltInLaunchPlanCatalog.Templates.Select(template => template.EditorTemplate)),
            "The first Offloadr's catalog does not apply to the second.");
    }

    [Test]
    public async Task Start_WithARunnerImageAlreadyHere_ChecksTheGpuInItAndDownloadsNothing()
    {
        _engine.ImageIds[ForgeNeo] = "sha256:forge";
        _engine.AddImage(ForgeNeo);
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Available);

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Pulls, Is.Empty);
            Assert.That(_engine.GpuCheckImages, Is.EqualTo(new[] { "sha256:forge" }));
        });
    }

    [Test]
    public async Task Start_ChecksTheGpuOnce_UntilTheGpuChoiceChanges()
    {
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Available);
        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Available);
        Assert.That(_engine.GpuChecks, Is.EqualTo(1));

        await _settings.SaveAsync(_settings.Current with { GpuUuid = "GPU-aaaaaaaa-1111-2222-3333-444444444444" });

        await WaitUntilAsync(() => _engine.GpuChecks == 2, "the GPU check for the chosen GPU");
    }

    [Test]
    public async Task Start_WhenContainersCannotUseTheGpu_OffersNothing()
    {
        _engine.GpuCheck = new GpuAccessCheck(false, "could not select device driver \"\" with capabilities: [[gpu]]");
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Fault?.Kind, Is.EqualTo(FaultKind.GpuUnavailable));
            Assert.That(supervisor.View.Fault?.Action, Is.EqualTo(FaultAction.SetUpGpu));
            Assert.That(_link.Runs, Is.Empty);
            Assert.That(supervisor.RunnerWanted, Is.False);
        });
    }

    [Test]
    public async Task AutoStart_WhenTheGpuCheckFails_KeepsTrying_AndBecomesAvailableOnceItPasses()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, AutoStart = true });
        _engine.GpuCheck = new GpuAccessCheck(false, "NVIDIA driver not loaded yet");
        var supervisor = await StartSupervisorAsync();

        await WaitForStateAsync(SupervisorState.WaitingToRetry);
        Assert.That(_link.Runs, Is.Empty);
        _engine.GpuCheck = new GpuAccessCheck(true, "GPU 0: NVIDIA GeForce RTX 4090");

        await WaitForStateAsync(SupervisorState.Available);
        Assert.That(_link.Running, Is.True);
    }

    [Test]
    public async Task Start_WithAKeyOffloadrRejects_SaysSoBeforeDownloadingAnything()
    {
        _plans.Rejects = _ => true;
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Fault?.Kind, Is.EqualTo(FaultKind.KeyRejected));
            Assert.That(_engine.Pulls, Is.Empty);
            Assert.That(_link.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task AContainerKeptRunning_IsNotReady_WhileItsLauncherIsRefused()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();
        Assert.That(supervisor.View.IsReady, Is.True);

        _link.SetConnection(LauncherConnection.Refused, "Offloadr did not accept the runner key.");

        await WaitUntilAsync(() => !supervisor.View.IsReady, "readiness to follow the launcher");
        Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));
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
            Assert.That(supervisor.View.Fault?.Kind, Is.EqualTo(FaultKind.KeyMissing));
            Assert.That(supervisor.View.Fault?.Action, Is.EqualTo(FaultAction.OpenSettings));
            Assert.That(_engine.Pulls, Is.Empty);
            Assert.That(_link.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task Start_WithoutAKeyAndWithABadAddress_AsksForTheKeyFirst()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { OffloadrApiOrigin = "http://offloadr.example" });
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Message, Is.EqualTo(RunnerSettings.MissingKeyProblem));
            Assert.That(supervisor.View.Fault?.Kind, Is.EqualTo(FaultKind.KeyMissing));
        });
    }

    [Test]
    public async Task Start_WithDownloadsButNoModelsFolder_AsksForOne()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, DownloadModels = true });
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Message, Does.Contain("Choose a models folder"));
            Assert.That(_link.Runs, Is.Empty);
        });
    }

    [TestCase("GPU-0000", "'GPU-0000' is not a complete NVIDIA GPU UUID")]
    [TestCase(null, "Extra Forge Neo arguments: --share")]
    public async Task Start_WithAnInvalidSetting_OffersNothing(string? gpu, string problem)
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, GpuUuid = gpu, ForgeNeoArguments = gpu is null ? "--share" : null });
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Message, Does.Contain(problem));
            Assert.That(_link.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task Start_WhenDockerIsNotRunning_Faults()
    {
        _engine.Info = ContainerEngineInfo.Unavailable("Docker is installed but its engine is not running.", errorKind: FaultKind.EngineNotRunning);
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Message, Does.Contain("not running"));
            Assert.That(supervisor.View.Fault?.Kind, Is.EqualTo(FaultKind.EngineNotRunning));
            Assert.That(supervisor.View.Fault?.Title, Is.EqualTo("Docker is not running"));
            Assert.That(supervisor.View.Fault?.Detail, Is.EqualTo(supervisor.View.Message));
            Assert.That(supervisor.View.Fault?.Action, Is.EqualTo(FaultAction.StartEngine));
            Assert.That(_link.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task Start_WhenPodmanIsNotRunning_AdvisesStartingPodman()
    {
        _engine.Info = ContainerEngineInfo.Unavailable(
            "Podman is installed but its engine is not running. Start podman and try again.",
            "podman",
            ContainerEngineKind.Podman,
            FaultKind.EngineNotRunning);
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Fault?.Title, Is.EqualTo("Podman is not running"));
            Assert.That(supervisor.View.Fault?.Message, Does.Contain("podman machine start"));
            Assert.That(supervisor.View.Fault?.Message, Does.Not.Contain("Docker"));
        });
    }

    [TestCase("failed to register layer: write /usr/lib/libcudnn.so.9: no space left on device", FaultKind.DiskFull)]
    [TestCase("Error response from daemon: Get \"https://ghcr.io/v2/\": net/http: TLS handshake timeout", FaultKind.RegistryUnreachable)]
    [TestCase("Error response from daemon: manifest unknown", FaultKind.ImageUnavailable)]
    public async Task Start_WhenTheFirstDownloadFails_SaysWhy(string output, FaultKind kind)
    {
        _engine.PullFails = true;
        _engine.PullFailureOutput = output;
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Fault?.Kind, Is.EqualTo(kind));
            Assert.That(supervisor.View.Fault?.Detail, Does.Contain(output));
            Assert.That(supervisor.View.Message, Does.Contain(output), "The message still says what happened.");
        });
    }

    [Test]
    public async Task AFault_IsGoneOnceTheComputerIsAvailable()
    {
        _engine.PullFails = true;
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);
        Assert.That(supervisor.View.Fault, Is.Not.Null);

        _engine.PullFails = false;
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Available);

        Assert.That(supervisor.View.Fault, Is.Null);
    }

    [Test]
    public async Task AutoStart_WhileDockerIsNotRunning_ShowsWhyItWaits()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, AutoStart = true });
        _engine.Info = ContainerEngineInfo.Unavailable("Docker is installed but its engine is not running.", errorKind: FaultKind.EngineNotRunning);
        var supervisor = await StartSupervisorAsync(retryBackoff: TimeSpan.FromMinutes(5));

        await WaitForStateAsync(SupervisorState.WaitingToRetry);

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Fault?.Kind, Is.EqualTo(FaultKind.EngineNotRunning));
            Assert.That(supervisor.View.Message, Does.Contain("Trying again"));
            Assert.That(supervisor.RunnerWanted, Is.True);
        });
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

        await WaitForStateAsync(SupervisorState.Available);
    }

    [Test]
    public async Task Startup_WithAutoStart_MakesTheComputerAvailable()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, AutoStart = true });

        await StartSupervisorAsync();

        await WaitForStateAsync(SupervisorState.Available);
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
        _engine.ProbeFailure = null;
        _engine.InspectFailure = null;

        supervisor.RequestStart();

        await WaitForStateAsync(SupervisorState.Available);
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
            Assert.That(supervisor.View.Fault?.Kind, Is.EqualTo(FaultKind.NameTaken));
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(_link.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task Pulling_TheFirstImage_ShowsHowManyLayersAreDone()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.PullLines = ["latest: Pulling from offloadr/runner-comfyui", "f18232174bc9: Pulling fs layer", "61ca4f733c80: Pulling fs layer", "f18232174bc9: Download complete"];
        _engine.OnPull = _ => release.Task;
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitUntilAsync(() => supervisor.View.PullProgress is { Layers: 2, Downloaded: 1 }, "pull progress");
        Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Pulling));

        release.SetResult();
        await WaitForStateAsync(SupervisorState.Available);
        Assert.That(supervisor.View.PullProgress, Is.Null, "Progress belongs to the download only.");
    }

    [Test]
    public async Task TheLauncher_OffersTheGpuContainersGet_AndTheEditorsOfThePlan()
    {
        _engine.Info = _engine.Info with { MemoryBytes = 64L << 30 };
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Available);

        var profile = _link.Profile!();

        Assert.Multiple(() =>
        {
            Assert.That(profile.Resources.GpuModelName, Is.EqualTo("NVIDIA GeForce RTX 4090"));
            Assert.That(profile.Resources.GpuCount, Is.EqualTo(1));
            Assert.That(profile.Resources.VramBytes, Is.EqualTo(24564UL * 1024 * 1024));
            Assert.That(profile.Resources.SystemRamBytes, Is.EqualTo(64UL << 30));
            Assert.That(profile.EditorTemplates, Is.EqualTo(BuiltInLaunchPlanCatalog.Templates.Select(template => template.EditorTemplate)));
        });
    }

    [Test]
    public async Task TheLauncher_OffersOnlyTheGpusContainersSee()
    {
        _hostGpus.Report = new HostGpuReport(
            [new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid), new HostGpu("NVIDIA GeForce RTX 3060", 12288, "GPU-cccccccc-1111-2222-3333-444444444444")],
            null);
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        var profile = _link.Profile!();

        Assert.Multiple(() =>
        {
            Assert.That(profile.Resources.GpuCount, Is.EqualTo(1), "The GPU check saw only the 4090 inside a container.");
            Assert.That(profile.Resources.GpuModelName, Is.EqualTo("NVIDIA GeForce RTX 4090"));
        });
    }

    [Test]
    public async Task TheLauncher_OffersTheContainersGpus_WhenTheySeeOtherGpusThanTheHostLists()
    {
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 3060", 12288, "GPU-cccccccc-1111-2222-3333-444444444444")], null);
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        var profile = _link.Profile!();
        Assert.Multiple(() =>
        {
            Assert.That(profile.Resources.GpuModelName, Is.EqualTo("NVIDIA GeForce RTX 4090"), "What the containers get, not the GPU listed here.");
            Assert.That(_engine.ContainerGpuLists, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ALaunch_WhosePreflightFindsOtherGpus_FailsAsChosenForAnOlderOffer()
    {
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid)], null);
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        // Chosen against the heartbeat's offer; the launch's own checks then list the GPU again.
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 12282, FakeContainerEngine.GpuUuid)], null);
        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Message, Does.Contain("changed"));
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task ALaunchWhosePreflightFindsOtherGpus_BeforeTheNewOfferIsAccepted_LeavesTheComputerReady()
    {
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid)], null);
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var before = _link.Profile!().OfferRevision;

        // The launch's own checks list the GPU again, but Offloadr does not answer the new offer in time.
        _link.HoldAcceptedOfferRevision = before;
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 12282, FakeContainerEngine.GpuUuid)], null);
        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.Profile!().OfferRevision > before && supervisor.View.State == SupervisorState.Available, "ready again");
        await Task.Delay(400);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Reports.Where(report => report.LaunchId == launch.LaunchId && report.IsTerminal), Is.Empty,
                "No FAILED before the new offer is accepted.");
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Available), "So a launch named against the new offer is started.");
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task AnOfferThatCameBackEmpty_IsCheckedAgain_WithTheContainersGpus()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();

        // This computer's own list comes back empty (nvidia-smi failing here, say).
        _hostGpus.Report = HostGpuReport.None("nvidia-smi failed.");
        supervisor.RequestRefresh();

        await WaitUntilAsync(() => _engine.ContainerGpuLists > 0 && _link.Profile!().Resources.GpuCount == 1, "the containers' GPUs offered");
    }

    [Test]
    public async Task ALaunchForAnOlderOffer_IsNotReportedFailed_BeforeTheNewOfferIsAccepted()
    {
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid)], null);
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var before = _link.Profile!().OfferRevision;
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 12282, FakeContainerEngine.GpuUuid)], null);
        supervisor.NotifyResumed();
        await WaitUntilAsync(() => _link.Profile!().OfferRevision > before && supervisor.View.IsReady, "the new offer");

        // The heartbeat with the new offer is not answered yet; Offloadr names a launch chosen for the old one.
        _link.HoldAcceptedOfferRevision = before;
        var stale = _link.Name("comfyui-latest", ComfyUi, offerRevision: before);
        await Task.Delay(300);
        Assert.That(_link.OutcomeOf(stale.LaunchId), Is.Null, "A FAILED now would fail the allocation instead of letting it launch again.");

        // Accepted: Offloadr retired that launch, and names it no more. Nothing is reported for it.
        _link.HoldAcceptedOfferRevision = null;
        _link.Withdraw();
        await Task.Delay(300);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(stale.LaunchId), Is.Null);
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task TheOfferRevision_Rises_WhenTheGpusOfferedChange_WithNoGpuChosen()
    {
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid)], null);
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var before = _link.Profile!().OfferRevision;
        var stale = before;

        // After sleep, the same GPU reports less memory (another process holds some, say).
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 12282, FakeContainerEngine.GpuUuid)], null);
        supervisor.NotifyResumed();

        await WaitUntilAsync(() => _link.Profile!().OfferRevision > before && supervisor.View.IsReady, "the new offer");
        var launch = _link.Name("comfyui-latest", ComfyUi, offerRevision: stale);
        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the stale launch's outcome");
        Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed), "Allocated against the old memory.");
    }

    [Test]
    public async Task AStartAfterAStop_UsesTheDockerContextOfNow()
    {
        _engine.CurrentContext = "desktop-linux";
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);
        _engine.CurrentContext = "other-daemon";

        await MakeAvailableAsync();
        string? contextAtRun = null;
        _engine.BeforeRun = () => contextAtRun = _selection.Context;
        await LaunchAsync();

        Assert.That(contextAtRun, Is.EqualTo("other-daemon"));
    }

    [Test]
    public async Task TheLauncher_OffersTheGpusTheContainersGet_ForAnEngineOnAnotherComputer()
    {
        _engine.CurrentContext = "build-server";
        _engine.CurrentContextRemote = true;
        _engine.ContainerGpus = [new HostGpu("NVIDIA RTX A6000", 49140, FakeContainerEngine.GpuUuid)];
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Available);

        var profile = _link.Profile!();

        Assert.Multiple(() =>
        {
            Assert.That(profile.Resources.GpuCount, Is.EqualTo(1), "What that engine's containers get, not this computer's GPUs.");
            Assert.That(profile.Resources.GpuModelName, Is.EqualTo("NVIDIA RTX A6000"));
            Assert.That(profile.EditorTemplates, Is.Not.Empty);
        });
    }

    [Test]
    public async Task ALaunch_OnARemoteEngineWhoseGpuWasReplaced_FailsAsChosenForAnOlderOffer()
    {
        _engine.CurrentContext = "build-server";
        _engine.CurrentContextRemote = true;
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitUntilAsync(() => supervisor.View.IsReady && _link.Running, "the offer");

        // Replaced on that computer: only the containers there can tell.
        _engine.ContainerGpus = [new HostGpu("NVIDIA RTX A4000", 16376, FakeContainerEngine.GpuUuid)];
        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.Profile!().Resources.GpuModelName, Is.EqualTo("NVIDIA RTX A4000"), "Offered as it is now.");
        });
    }

    [Test]
    public async Task ARemoteGpuReplacedWhileTheContainerIsCreated_FailsTheLaunchBeforeItStarts()
    {
        _engine.CurrentContext = "build-server";
        _engine.CurrentContextRemote = true;
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitUntilAsync(() => supervisor.View.IsReady && _link.Running, "the offer");

        // Replaced on that computer while Docker creates the container, the stream live throughout.
        _engine.OnCreate = (_, _) =>
        {
            _engine.ContainerGpus = [new HostGpu("NVIDIA RTX A4000", 16376, FakeContainerEngine.GpuUuid)];
            return Task.CompletedTask;
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        await WaitUntilAsync(() => !_engine.Exists(Name), "what was created to be removed");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_engine.Runs, Is.Empty, "Not started on hardware Offloadr did not choose.");
            Assert.That(_engine.Creations, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ARemoteGpuReplacedWhileTheStreamWasDown_FailsTheLaunchBeforeItStarts()
    {
        _engine.CurrentContext = "build-server";
        _engine.CurrentContextRemote = true;
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitUntilAsync(() => supervisor.View.IsReady && _link.Running, "the offer");
        var listed = _engine.ContainerGpuLists;
        _engine.OnPull = _ =>
        {
            _engine.OnPull = null;
            _link.SetStreamLive(false);
            return Task.CompletedTask;
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => _engine.ContainerGpuLists > listed && !_link.MayStart(launch), "downloaded and listed, the stream down");
        _engine.ContainerGpus = [new HostGpu("NVIDIA RTX A4000", 16376, FakeContainerEngine.GpuUuid)];
        _link.SetStreamLive(true);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_engine.Runs, Is.Empty, "Not started on hardware Offloadr did not choose.");
        });
    }

    [Test]
    public async Task ARemoteGpuReplacedWhileAReplacementWaitedForTheStream_KeepsTheWarmContainer()
    {
        _engine.CurrentContext = "build-server";
        _engine.CurrentContextRemote = true;
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        supervisor.RequestStart();
        await WaitUntilAsync(() => supervisor.View.IsReady && _link.Running, "the offer");
        await LaunchAsync();
        _engine.OnPull = _ =>
        {
            _engine.OnPull = null;
            _link.SetStreamLive(false);
            return Task.CompletedTask;
        };

        var second = _link.Name("forge-neo:neo", ForgeNeo);
        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains(second.LaunchId, StringComparison.Ordinal)) && !_link.MayStart(second), "the replacement, the stream down");
        await Task.Delay(300);

        // Replaced on that computer while the stream was down: only the containers there can tell.
        _engine.ContainerGpus = [new HostGpu("NVIDIA RTX A4000", 16376, FakeContainerEngine.GpuUuid)];
        _link.SetStreamLive(true);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.Releases, Is.Empty, "The warm container is kept: the replacement was chosen for hardware that is no longer there.");
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task ARemoteGpuReplacedAfterTheStreamDroppedDuringTheLastCheck_KeepsTheWarmContainer()
    {
        _engine.CurrentContext = "build-server";
        _engine.CurrentContextRemote = true;
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        supervisor.RequestStart();
        await WaitUntilAsync(() => supervisor.View.IsReady && _link.Running, "the offer");
        await LaunchAsync();

        // The stream drops while the containers' GPUs are listed for the last time before the release;
        // the listing still answers with what was there then.
        var listed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // (The replacement lists them once as it is prepared, and once more right before the release.)
        var before = _engine.ContainerGpuLists;
        _engine.OnContainerGpuList = async count =>
        {
            if (count == before + 2)
            {
                _link.SetStreamLive(false);
                listed.TrySetResult();
                await release.Task;
            }
        };

        var second = _link.Name("forge-neo:neo", ForgeNeo);
        await listed.Task.WaitAsync(WaitLimit);
        release.SetResult();
        await Task.Delay(300);

        // Replaced on that computer while the stream is down.
        _engine.ContainerGpus = [new HostGpu("NVIDIA RTX A4000", 16376, FakeContainerEngine.GpuUuid)];
        _link.SetStreamLive(true);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.Releases, Is.Empty, "The warm container is kept: what was listed before the stream dropped counts no more.");
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task AModelsFolderTheRemoteEngineCannotSee_SavedAsAReplacementsReleaseIsSent_SendsNone()
    {
        _engine.CurrentContext = "build-server";
        _engine.CurrentContextRemote = true;
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        supervisor.RequestStart();
        await WaitUntilAsync(() => supervisor.View.IsReady && _link.Running, "the offer");
        await LaunchAsync();

        // Saved after the release was decided, before it is sent: the replacement could not be started
        // with a models folder that is on this computer, not the engine's.
        var saved = 0;
        _link.BeforeNextRelease = () =>
        {
            _settings.SaveAsync(_settings.Current with { LocalModelsDirectory = Path.GetTempPath() }).GetAwaiter().GetResult();
            Interlocked.Exchange(ref saved, 1);
        };

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => Volatile.Read(ref saved) == 1, "the release to be about to be sent");
        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.Releases, Is.Empty, "The warm container is not given up for a replacement that cannot start.");
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task ARemoteGpuListedBeforeTheComputerWoke_DoesNotCountForTheStart()
    {
        _engine.CurrentContext = "build-server";
        _engine.CurrentContextRemote = true;
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitUntilAsync(() => supervisor.View.IsReady && _link.Running, "the offer");

        // The last listing before the start answers with what was there; then the computer wakes from
        // sleep, and that engine has another GPU. (A launch lists once as it is prepared, once before the start.)
        var before = _engine.ContainerGpuLists;
        _engine.OnContainerGpuList = count =>
        {
            if (count == before + 2)
            {
                _engine.ContainerGpus = [new HostGpu("NVIDIA RTX A4000", 16376, FakeContainerEngine.GpuUuid)];
                supervisor.NotifyResumed();
            }

            return Task.CompletedTask;
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_engine.Runs, Is.Empty, "Not started on hardware Offloadr did not choose: the listing from before the sleep counts no more.");
        });
    }

    [Test]
    public async Task TheComputerWakingAsAReplacementsReleaseIsSent_HoldsItBack_UntilTheGpuIsProvedAgain()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();

        // The computer wakes after the checks for the release, before it is sent: and the GPU is gone.
        var woke = 0;
        _link.BeforeNextRelease = () =>
        {
            _engine.GpuCheck = new GpuAccessCheck(false, "NVIDIA driver lost after sleep");
            supervisor.NotifyResumed();
            Interlocked.Exchange(ref woke, 1);
        };

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => Volatile.Read(ref woke) == 1, "the release to be about to be sent");
        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.Releases, Is.Empty, "The warm container is kept: the replacement could not use the GPU.");
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task AStreamThatDropsAndComesBackAsTheNameIsLookedAt_HasTheNameLookedAtAgain_BeforeTheRelease()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        supervisor.RequestStart();
        await WaitUntilAsync(() => supervisor.View.IsReady && _link.Running, "the offer");
        await LaunchAsync();

        // As the name the replacement needs is looked at, the stream drops and comes back: what that
        // look finds is from a stream that is no longer the one the release is sent on.
        var looks = 0;
        _engine.OnInspect = name =>
        {
            if (name == Name && Interlocked.Increment(ref looks) == 1)
            {
                _link.SetStreamLive(false);
                _link.SetStreamLive(true);
            }
        };
        var looksAtRelease = -1;
        _link.BeforeNextRelease = () => looksAtRelease = Volatile.Read(ref looks);

        _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => !_link.Releases.IsEmpty, "the release");
        Assert.That(looksAtRelease, Is.GreaterThanOrEqualTo(2), "The name is looked at again once the stream is back: another container can have taken it meanwhile.");
    }

    [Test]
    public async Task AReleaseAskedAgainAfterAWhile_LooksAtTheNameAgainFirst()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        supervisor.RequestStart();
        await WaitUntilAsync(() => supervisor.View.IsReady && _link.Running, "the offer");
        await LaunchAsync();

        var looks = 0;
        _engine.OnInspect = name =>
        {
            if (name == Name)
            {
                Interlocked.Increment(ref looks);
            }
        };

        // Offloadr does not answer the first request; the second is granted.
        var looksAtRequest = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var requests = 0;
        _link.ReleaseAnswer = _ =>
        {
            looksAtRequest.Enqueue(Volatile.Read(ref looks));
            return Interlocked.Increment(ref requests) == 1
                ? new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).")
                : new ReleaseResult(true, true, string.Empty);
        };

        _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => looksAtRequest.Count >= 2, "the release to be asked again");
        var counts = looksAtRequest.ToArray();
        Assert.That(counts[1], Is.GreaterThan(counts[0]), "While it waited to ask again, another container can have taken the name the replacement needs.");
    }

    [Test]
    public async Task ARemoteGpuReplacedWhileTheStreamDroppedAndCameBackDuringTheLastCheck_KeepsTheWarmContainer()
    {
        _engine.CurrentContext = "build-server";
        _engine.CurrentContextRemote = true;
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        supervisor.RequestStart();
        await WaitUntilAsync(() => supervisor.View.IsReady && _link.Running, "the offer");
        await LaunchAsync();

        // While the containers' GPUs are listed for the last time before the release, the stream drops
        // and comes back, and that computer's GPU is replaced: the listing answers with what was there before.
        var before = _engine.ContainerGpuLists;
        var held = 0;
        _engine.OnContainerGpuList = count =>
        {
            if (count == before + 2 && Interlocked.Exchange(ref held, 1) == 0)
            {
                _link.SetStreamLive(false);
                _engine.ContainerGpus = [new HostGpu("NVIDIA RTX A4000", 16376, FakeContainerEngine.GpuUuid)];
                _link.SetStreamLive(true);
            }

            return Task.CompletedTask;
        };

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.Releases, Is.Empty, "The warm container is kept: what was listed before the stream dropped counts no more.");
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task ARemoteGpuReplacedWhileTheStreamDroppedAndCameBackRightBeforeTheStart_KeepsItFromStarting()
    {
        _engine.CurrentContext = "build-server";
        _engine.CurrentContextRemote = true;
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitUntilAsync(() => supervisor.View.IsReady && _link.Running, "the offer");

        // After the last checks, as the start is asked for: the stream drops and comes back with the
        // same launch named, and that computer's GPU was replaced meanwhile.
        _link.OnNextTryStart = () =>
        {
            _link.SetStreamLive(false);
            _engine.ContainerGpus = [new HostGpu("NVIDIA RTX A4000", 16376, FakeContainerEngine.GpuUuid)];
            _link.SetStreamLive(true);
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_engine.Runs, Is.Empty, "Not started on hardware Offloadr did not choose: the checks were made on another stream.");
        });
    }

    [Test]
    public async Task ANameTakenWhileTheStreamDroppedAndCameBack_KeepsTheWarmContainer()
    {
        _engine.CurrentContext = "build-server";
        _engine.CurrentContextRemote = true;
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        supervisor.RequestStart();
        await WaitUntilAsync(() => supervisor.View.IsReady && _link.Running, "the offer");
        await LaunchAsync();

        // During the last check before the release, the stream drops and comes back; meanwhile the warm
        // runner was renamed, and someone else's container took the name the replacement needs.
        var before = _engine.ContainerGpuLists;
        var done = 0;
        _engine.OnContainerGpuList = count =>
        {
            if (count == before + 2 && Interlocked.Exchange(ref done, 1) == 0)
            {
                _link.SetStreamLive(false);
                _engine.Rename(Name, "renamed-runner");
                _engine.AddContainer(Name, "someone-elses-image", running: true, new Dictionary<string, string>());
                _link.SetStreamLive(true);
            }

            return Task.CompletedTask;
        };

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.Releases, Is.Empty, "The warm runner is not given up for a replacement whose name is taken.");
            Assert.That(_engine.Exists("renamed-runner"), Is.True);
        });
    }

    [Test]
    public async Task ANameTakenWhileTheStreamDroppedAndCameBackAsTheReleaseIsSent_KeepsTheWarmContainer()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();

        // As the release is about to be sent, the stream drops and comes back with the same launch
        // named; meanwhile the warm runner was renamed, and someone else's container took the name.
        _link.BeforeNextRelease = () =>
        {
            _link.SetStreamLive(false);
            _engine.Rename(Name, "renamed-runner");
            _engine.AddContainer(Name, "someone-elses-image", running: true, new Dictionary<string, string>());
            _link.SetStreamLive(true);
        };

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.Releases, Is.Empty, "Not sent on another stream than its checks were made on; and checked again, the name is taken.");
            Assert.That(_engine.Exists("renamed-runner"), Is.True, "The warm runner is not given up for a replacement that cannot start.");
        });
    }

    [Test]
    public async Task AReplacementWhoseOwnImageCannotUseTheGpu_KeepsTheWarmContainer()
    {
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid)], null);
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();

        // Containers of the image that runs use the GPU; the replacement's need a newer driver.
        _engine.ImageIds[ForgeNeo] = "sha256:forge";
        _engine.GpuCheckOf = image => image == "sha256:forge" ? new GpuAccessCheck(false, "nvidia-container-cli: requirement error: unsatisfied condition: cuda>=13.0") : null;

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.Releases, Is.Empty, "A usable container is not given up for one the engine would turn down.");
            Assert.That(_engine.Exists(Name), Is.True);
        });
        await WaitUntilAsync(() => _link.Profile!().EditorTemplates.Count > 0, "the computer to be offered again: the GPU itself works");
    }

    [Test]
    public async Task AReplacementWhoseTagIsMovedAfterItsCheck_RunsTheImageThatWasChecked()
    {
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid)], null);
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();

        // Someone updates the tag on this engine once the replacement's image was proved to use the GPU.
        _engine.ImageIds[ForgeNeo] = "sha256:forge";
        _link.BeforeNextRelease = () => _engine.ImageIds[ForgeNeo] = "sha256:other";

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started));
            Assert.That(_engine.GpuCheckImages.Last(), Is.EqualTo("sha256:forge"));
            Assert.That(_engine.Runs.Last().Image, Is.EqualTo("sha256:forge"), "The warm container was given up for the image that was checked.");
            Assert.That(_engine.Runs.Last().Labels[RunnerSupervisor.ImageLabel], Is.EqualTo(ForgeNeo), "As Offloadr named it.");
            Assert.That(_supervisor!.View.ActiveTemplate?.Image, Is.EqualTo(ForgeNeo));
        });
    }

    [Test]
    public async Task AnAdoptedContainer_CreatedFromAnImageId_IsKnownByTheImageOffloadrNamed()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var spec = Earlier().BuildSpec(_settings.Current, BuiltInLaunchPlanCatalog.Plan().Find("comfyui-latest")!);
        var labels = new Dictionary<string, string>(spec.Labels)
        {
            [RunnerSupervisor.LaunchLabel] = Guid.NewGuid().ToString("N"),
            [RunnerSupervisor.LaunchIdLabel] = Guid.NewGuid().ToString(),
            [RunnerSupervisor.LaunchTargetLabel] = FakeLauncherLink.TargetOf(_settings.Current),
            [RunnerSupervisor.ImageLabel] = spec.Image,
        };
        _engine.AddContainer(Name, "sha256:local", running: true, labels, spec.Environment);

        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await WaitForStateAsync(SupervisorState.Running);
        supervisor.RequestStart();
        await WaitUntilAsync(() => _link.Running, "the launcher");
        await Task.Delay(400);

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.ActiveTemplate?.Image, Is.EqualTo(spec.Image));
            Assert.That(_link.Releases, Is.Empty, "Started with the settings as they are: not released as outdated.");
        });
    }

    [Test]
    public async Task ALaunchWhoseOwnImageCannotUseTheGpu_IsAFailedLaunch_NotAnOfferThatChanged()
    {
        // (This computer lists its GPU without a UUID: the containers are asked what they get.)
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.ContainerGpus = [];
        _engine.GpuCheckOf = image => image == "sha256:local" ? new GpuAccessCheck(false, "nvidia-container-cli: requirement error: unsatisfied condition: cuda>=13.0") : null;

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Message, Does.Contain("cannot use the GPU"),
                "Told as an offer that changed, the allocation would launch the same image here again and again.");
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task AReplacement_WhenTheComputerSleptDuringItsDownload_ChecksTheGpuBeforeTheRelease()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        _engine.OnPull = _ =>
        {
            _engine.OnPull = null;
            _engine.GpuCheck = new GpuAccessCheck(false, "NVIDIA driver lost after sleep");
            supervisor.NotifyResumed();
            return Task.CompletedTask;
        };

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.Releases, Is.Empty, "The warm container is kept: the replacement could not use the GPU.");
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task WithSeveralGpusAndNoneChosen_AChangedOrderInContainers_FailsTheLaunchBeforeItStarts()
    {
        const string other = "GPU-cccccccc-1111-2222-3333-444444444444";
        var first = new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid);
        var second = new HostGpu("NVIDIA GeForce RTX 3060", 12288, other);
        _hostGpus.Report = new HostGpuReport([first, second], null);
        _engine.GpuCheck = new GpuAccessCheck(true, $"GPU 0: NVIDIA GeForce RTX 4090 (UUID: {FakeContainerEngine.GpuUuid})\nGPU 1: NVIDIA GeForce RTX 3060 (UUID: {other})");
        _engine.ContainerGpus = [first, second];
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        Assert.That(_link.Profile!().Resources.GpuModelName, Is.EqualTo("NVIDIA GeForce RTX 4090"));

        // The driver enumerates anew while the container is created: containers now list the 3060 first,
        // which is the GPU the executor would advertise. This computer's own list says nothing of it.
        _engine.OnCreate = (_, _) =>
        {
            _engine.ContainerGpus = [second, first];
            return Task.CompletedTask;
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_engine.Runs, Is.Empty, "Not started as another GPU than the one Offloadr chose this computer for.");
        });
    }

    [Test]
    public async Task TheContainersList_IsOffered_WhenThisComputerListsOnlySomeOfTheirGpus()
    {
        const string other = "GPU-cccccccc-1111-2222-3333-444444444444";
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 3060", 12288, other)], null);
        _engine.GpuCheck = new GpuAccessCheck(true, $"GPU 0: NVIDIA GeForce RTX 4090 (UUID: {FakeContainerEngine.GpuUuid})\nGPU 1: NVIDIA GeForce RTX 3060 (UUID: {other})");
        _engine.ContainerGpus = [new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid), new HostGpu("NVIDIA GeForce RTX 3060", 12288, other)];
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        var profile = _link.Profile!();

        Assert.Multiple(() =>
        {
            Assert.That(profile.Resources.GpuModelName, Is.EqualTo("NVIDIA GeForce RTX 4090"), "The executor's first GPU, which this computer did not list.");
            Assert.That(profile.Resources.GpuCount, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task TheGpuChosen_IsOffered_WhenOnlyTheContainersListIt()
    {
        // This computer lists another GPU; the containers see that one and the one chosen.
        const string other = "GPU-cccccccc-1111-2222-3333-444444444444";
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 3060", 12288, other)], null);
        _engine.GpuCheck = new GpuAccessCheck(true, $"GPU 0: NVIDIA GeForce RTX 3060 (UUID: {other})\nGPU 1: NVIDIA GeForce RTX 4090 (UUID: {FakeContainerEngine.GpuUuid})");
        _engine.ContainerGpus = [new HostGpu("NVIDIA GeForce RTX 3060", 12288, other), new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid)];
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, GpuUuid = FakeContainerEngine.GpuUuid });
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        var profile = _link.Profile!();

        Assert.Multiple(() =>
        {
            Assert.That(profile.Resources.GpuModelName, Is.EqualTo("NVIDIA GeForce RTX 4090"));
            Assert.That(profile.EditorTemplates, Is.Not.Empty);
        });
    }

    [Test]
    public async Task ARemoteLaunch_AsksTheContainersWithItsOwnImage_WhenTheCheckedOneWasRemoved()
    {
        _engine.ImageIds[ForgeNeo] = "sha256:forge";
        _engine.CurrentContext = "build-server";
        _engine.CurrentContextRemote = true;
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitUntilAsync(() => supervisor.View.IsReady && _link.Running, "the offer");
        var asked = _engine.ContainerGpuLists;

        var launch = await LaunchAsync("forge-neo:neo", ForgeNeo);

        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started), "The GPU did not change.");
            Assert.That(_engine.ContainerGpuLists, Is.GreaterThan(asked));
            Assert.That(_engine.ContainerGpuImages.Last(), Is.EqualTo("sha256:forge"), "Asked with the launch's own image, which is here.");
        });
    }

    [Test]
    public async Task TheLauncher_DoesNotRegister_WhenNoGpuCanBeListed()
    {
        _engine.CurrentContext = "build-server";
        _engine.CurrentContextRemote = true;
        _engine.ContainerGpus = [];
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();

        await WaitForStateAsync(SupervisorState.Faulted);
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Fault?.Kind, Is.EqualTo(FaultKind.GpuUnavailable));
            Assert.That(supervisor.View.Message, Does.Contain("name and memory"));
            Assert.That(_link.Runs, Is.Empty, "Offloadr allocates by the GPU's name and memory: nothing to offer.");
            Assert.That(supervisor.View.IsReady, Is.False);
        });
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
            Assert.That(_link.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task Stop_TakesTheComputerOffline()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();

        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);

        Assert.Multiple(() =>
        {
            Assert.That(_link.Running, Is.False);
            Assert.That(supervisor.RunnerWanted, Is.False);
        });
    }

    [Test]
    public async Task RunnerWanted_FollowsStartAndStop_NotTheState()
    {
        var supervisor = await StartSupervisorAsync();
        Assert.That(supervisor.RunnerWanted, Is.False, "Nothing was started.");

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Available);
        Assert.That(supervisor.RunnerWanted, Is.True);

        // Cleared when the Stop is asked for, before it runs: a restart to update that begins
        // now must not make the computer available again.
        supervisor.RequestStop();
        Assert.That(supervisor.RunnerWanted, Is.False);
        await WaitForStateAsync(SupervisorState.Stopped);
        Assert.That(supervisor.RunnerWanted, Is.False);
    }

    [Test]
    public async Task RunnerWanted_WhenStartingFailsForGood_IsCleared()
    {
        _settings = new FakeSettingsStore(new RunnerSettings());
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        Assert.That(supervisor.RunnerWanted, Is.False);
    }

    [Test]
    public async Task StopPressedBeforeAQueuedStartRuns_KeepsTheComputerOffline()
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
            Assert.That(_link.Runs, Is.Empty);
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Stopped));
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
            checking.TrySetResult();
            await release.Task;
        };
        var supervisor = await StartSupervisorAsync();
        await checking.Task.WaitAsync(WaitLimit);

        supervisor.RequestStop();
        _engine.OnProbe = null;
        release.TrySetResult();
        await Task.Delay(300);

        Assert.Multiple(() =>
        {
            Assert.That(_link.Runs, Is.Empty);
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Stopped));
        });
    }

    [Test]
    public async Task DockerStoppingWhileAvailable_StopsOfferingTheComputer_UntilItIsBack()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var available = _engine.Info;

        _engine.Info = ContainerEngineInfo.Unavailable("Docker Desktop quit.", errorKind: FaultKind.EngineNotRunning);

        await WaitUntilAsync(() => !_link.Running && supervisor.View.State == SupervisorState.WaitingToRetry, "the launcher to stop");
        Assert.That(supervisor.View.Fault?.Kind, Is.EqualTo(FaultKind.EngineNotRunning));
        _engine.Info = available;

        await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Available && _link.Running, "availability to come back");
    }

    [Test]
    public async Task AGpuChangedWhileDockerWasDown_RaisesTheOfferRevision_AndALaunchChosenForTheOldOneFails()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var before = _link.Profile!().OfferRevision;
        var available = _engine.Info;
        _engine.Info = ContainerEngineInfo.Unavailable("Docker Desktop quit.", errorKind: FaultKind.EngineNotRunning);
        await WaitUntilAsync(() => !_link.Running && supervisor.View.State == SupervisorState.WaitingToRetry, "the launcher to stop");

        await _settings.SaveAsync(_settings.Current with { GpuUuid = FakeContainerEngine.GpuUuid });
        _engine.Info = available;
        await WaitUntilAsync(() => _link.Running && supervisor.View.IsReady, "the offer back");
        var after = _link.Profile!().OfferRevision;

        // Offloadr chose this one on a heartbeat from before the change.
        var stale = _link.Name("comfyui-latest", ComfyUi, offerRevision: before);
        await WaitUntilAsync(() => _link.OutcomeOf(stale.LaunchId) is not null, "the stale launch's outcome");
        var current = await LaunchAsync();

        Assert.Multiple(() =>
        {
            Assert.That(after, Is.GreaterThan(before));
            Assert.That(_link.Runs, Has.Count.EqualTo(2), "The same launcher instance: no new one, which could take the runner from another computer.");
            Assert.That(_link.OutcomeOf(stale.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.OutcomeOf(stale.LaunchId)?.Message, Does.Contain("changed"));
            Assert.That(_link.OutcomeOf(current.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started));
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task TheOfferRevision_StaysTheSame_WhileTheOfferDoesNot()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var first = _link.Profile!().OfferRevision;

        supervisor.RequestRefresh();
        await Task.Delay(200);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.GreaterThan(0UL), "0 is what Offloadr would stamp without one.");
            Assert.That(_link.Profile!().OfferRevision, Is.EqualTo(first));
        });
    }

    [Test]
    public async Task AStopPressedBeforeTheLauncherIsAccepted_EndsItsRegistration()
    {
        var supervisor = await StartSupervisorAsync();
        _link.BeforeNextOnline = supervisor.RequestStop;

        supervisor.RequestStart();

        await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Stopped && _link.Runs.Count == 1 && !_link.Running, "the Stop");
        await Task.Delay(200);
        Assert.That(_link.Onlines, Is.Zero, "Not registered after the Stop: it could take the runner from another computer.");
    }

    [Test]
    public async Task TheAppClosingBeforeTheLauncherIsAccepted_EndsItsRegistration()
    {
        var supervisor = await StartSupervisorAsync();
        Task? closed = null;
        _link.BeforeNextOnline = () => closed = supervisor.StopAsync(CancellationToken.None);

        supervisor.RequestStart();

        await WaitUntilAsync(() => closed is not null, "the app to close");
        await closed!.WaitAsync(WaitLimit);
        Assert.That(_link.Onlines, Is.Zero, "Not registered as the app closes: it could take the runner from another computer, only to leave.");
    }

    [Test]
    public async Task AKeyChangedAndChangedBackBeforeTheLauncherIsAccepted_StillBringsTheLauncherOnline()
    {
        const string otherKey = "another-runner-key-0123456789abcdefghijklmnop";
        var supervisor = await StartSupervisorAsync();
        _link.BeforeNextOnline = () =>
        {
            // Both before the loop looks: the registration under way is ended all the same.
            var original = _settings.Current;
            _settings.SaveAsync(original with { RunnerKey = otherKey }).GetAwaiter().GetResult();
            _settings.SaveAsync(original).GetAwaiter().GetResult();
        };

        supervisor.RequestStart();

        await WaitUntilAsync(() => _link.Running && supervisor.View.State == SupervisorState.Available, "the launcher to come online");
        Assert.Multiple(() =>
        {
            Assert.That(_link.Runs.Last().RunnerKey, Is.EqualTo(Key));
            Assert.That(_link.Onlines, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task AKeyChangedAndChangedBackAMomentLater_BeforeTheLauncherIsAccepted_StillBringsTheLauncherOnline()
    {
        const string otherKey = "another-runner-key-0123456789abcdefghijklmnop";
        var supervisor = await StartSupervisorAsync();
        _link.BeforeNextOnline = () =>
        {
            // The loop sees the other key (and ends the registration under way) before it is changed back.
            var original = _settings.Current;
            _settings.SaveAsync(original with { RunnerKey = otherKey }).GetAwaiter().GetResult();
            Thread.Sleep(30);
            _settings.SaveAsync(original).GetAwaiter().GetResult();
        };

        supervisor.RequestStart();

        await WaitUntilAsync(() => _link.Running && supervisor.View.State == SupervisorState.Available && _link.Runs.Last().RunnerKey == Key,
            "the launcher to come online for the key Settings name");
        Assert.That(_link.Onlines, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task ANewKeySavedBeforeTheLauncherIsAccepted_RegistersOnlyForTheNewOne()
    {
        const string newKey = "another-runner-key-0123456789abcdefghijklmnop";
        var supervisor = await StartSupervisorAsync();
        _link.BeforeNextOnline = () => _settings.SaveAsync(_settings.Current with { RunnerKey = newKey }).GetAwaiter().GetResult();

        supervisor.RequestStart();

        await WaitUntilAsync(() => _link.Running && _link.Runs.Last().RunnerKey == newKey, "the launcher for the new key");
        Assert.That(_link.Onlines, Is.EqualTo(1), "The run for the old key never registered: it could take that runner from another computer.");
    }

    [Test]
    public async Task AGpuChangedWhileDockerWasDown_DoesNotTakeTheRunnerBackFromAnotherApp()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var available = _engine.Info;
        _engine.Info = ContainerEngineInfo.Unavailable("Docker Desktop quit.", errorKind: FaultKind.EngineNotRunning);
        await WaitUntilAsync(() => !_link.Running && supervisor.View.State == SupervisorState.WaitingToRetry, "the launcher to stop");

        // Another app took the runner over meanwhile: the old instance learns so at its first heartbeat.
        _link.FirstHeartbeats.Enqueue(new LauncherLinkState(LauncherConnection.Superseded, "Another Offloadr Runner app took over this runner.", false, null));
        await _settings.SaveAsync(_settings.Current with { GpuUuid = FakeContainerEngine.GpuUuid });
        _engine.Info = available;

        await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Stopped && _link.Runs.Count == 2, "the launcher to stop");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Runs, Has.Count.EqualTo(2));
            Assert.That(supervisor.View.Message, Does.Contain("took over"));
        });
    }

    [Test]
    public async Task ANewAddressOrKey_RegistersTheLauncherAgain()
    {
        const string newKey = "another-runner-key-0123456789abcdefghijklmnop";
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();

        await _settings.SaveAsync(_settings.Current with { RunnerKey = newKey });

        await WaitUntilAsync(() => _link.Runs.Count == 2 && _link.Running, "the launcher's run with the new key");
        Assert.Multiple(() =>
        {
            Assert.That(_link.Runs.Last().RunnerKey, Is.EqualTo(newKey));
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Available));
        });
    }

    [Test]
    public async Task ASettingsChangeThatKeepsTheAddressAndKey_LeavesTheLauncherRunning()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();

        await _settings.SaveAsync(_settings.Current with { ComfyUiArguments = "--lowvram" });
        await Task.Delay(200);

        Assert.Multiple(() =>
        {
            Assert.That(_link.Runs, Has.Count.EqualTo(1));
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Available));
        });
    }

    [Test]
    public async Task SettingsThatBecomeInvalid_TakeTheComputerOffline()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();

        await _settings.SaveAsync(_settings.Current with { OffloadrApiOrigin = "http://offloadr.example" });

        await WaitForStateAsync(SupervisorState.Faulted);
        Assert.That(_link.Running, Is.False);
    }

    // Launches

    [Test]
    public async Task ALaunch_StartsTheImageOffloadrNamed_ForThatLaunch_WithTheKeyOnlyInTheEnvironment()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();

        var launch = await LaunchAsync("forge-neo:neo", "ghcr.io/offloadr/runner-forge-neo@sha256:abc");

        var run = _engine.Runs.Single();
        Assert.Multiple(() =>
        {
            Assert.That(run.Image, Is.EqualTo("ghcr.io/offloadr/runner-forge-neo@sha256:abc"));
            Assert.That(run.Gpus, Is.True);
            Assert.That(run.Environment["RUNNER_SECRET"], Is.EqualTo(Key));
            Assert.That(run.Environment["OFFLOADR_API_GRPC"], Is.EqualTo(RunnerSettings.DefaultOffloadrApiOrigin));
            Assert.That(run.Environment[RunnerSupervisor.LaunchIdVariable], Is.EqualTo(launch.LaunchId));
            Assert.That(run.Labels[RunnerSupervisor.ManagedLabel], Is.EqualTo("true"));
            Assert.That(run.Labels[RunnerSupervisor.TemplateLabel], Is.EqualTo("forge-neo:neo"));
            Assert.That(run.Labels[RunnerSupervisor.LaunchIdLabel], Is.EqualTo(launch.LaunchId));
            Assert.That(run.Labels[RunnerSupervisor.LaunchTargetLabel], Is.EqualTo(FakeLauncherLink.TargetOf(_settings.Current)));
            Assert.That(run.Labels.Values, Has.None.Contains(Key));
            Assert.That(DockerCliArguments.Create(run, ContainerEngineKind.Docker), Has.None.Contains(Key));
            Assert.That(_link.PhasesOf(launch), Is.EqualTo(new[]
            {
                RunnerLaunchPhase.Preparing, RunnerLaunchPhase.Downloading, RunnerLaunchPhase.Starting, RunnerLaunchPhase.Started,
            }));
            Assert.That(supervisor.View.ActiveTemplate?.EditorTemplate, Is.EqualTo("forge-neo:neo"));
            Assert.That(supervisor.View.ActiveTemplate?.Label, Is.EqualTo("Forge Neo - neo branch"));
            Assert.That(supervisor.View.Image?.RepoDigest, Does.EndWith("@sha256:digest"));
        });
    }

    [Test]
    public async Task ALaunch_IsNotPartOfTheContainersFingerprint()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        await LaunchAsync();

        var run = _engine.Runs.Single();
        var withoutLaunch = run with { Image = run.Labels[RunnerSupervisor.ImageLabel], Environment = run.Environment.Where(entry => entry.Key != RunnerSupervisor.LaunchIdVariable).ToDictionary() };
        Assert.That(run.Labels[RunnerSupervisor.SpecLabel], Is.EqualTo(RunnerSupervisor.Fingerprint(withoutLaunch)));
    }

    [Test]
    public async Task ALaunch_MountsTheModelsFolderReadOnly()
    {
        var models = Path.GetTempPath();
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, LocalModelsDirectory = models });
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        await LaunchAsync();

        var run = _engine.Runs.Single();
        Assert.Multiple(() =>
        {
            Assert.That(run.Volumes, Is.EqualTo(new[] { new VolumeMount(models, "/local", true) }));
            Assert.That(run.Environment["RUNNER_LOCAL_MODELS_DIR"], Is.EqualTo("/local"));
            Assert.That(run.Environment.ContainsKey("RUNNER_MODEL_LIBRARY_DIR"), Is.False);
            Assert.That(run.Environment["RUNNER_MODEL_DOWNLOADS"], Is.EqualTo("off"));
        });
    }

    [Test]
    public async Task ALaunch_WithDownloads_SavesThemInTheModelsFolder()
    {
        var models = Path.GetTempPath();
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, LocalModelsDirectory = models, DownloadModels = true });
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        await LaunchAsync();

        var run = _engine.Runs.Single();
        Assert.Multiple(() =>
        {
            Assert.That(run.Volumes, Is.EqualTo(new[]
            {
                new VolumeMount(models, "/local", true),
                new VolumeMount(models, RunnerSupervisor.ModelLibraryContainerPath, false),
            }), "Read-only for sessions, writable where only the agent reaches it.");
            Assert.That(run.Environment["RUNNER_MODEL_LIBRARY_DIR"], Is.EqualTo(RunnerSupervisor.ModelLibraryContainerPath));
            Assert.That(run.Environment["RUNNER_MODEL_DOWNLOADS"], Is.EqualTo("on"));
        });
    }

    [Test]
    public async Task ALaunch_PassesEachEditorFamilysArgumentsUnderItsOwnName_AndTheChosenGpu()
    {
        _settings = new FakeSettingsStore(new RunnerSettings
        {
            RunnerKey = Key,
            ComfyUiArguments = "  --lowvram --preview-method auto ",
            ForgeNeoArguments = "--use-ck-attention",
            GpuUuid = " GPU-AAAAAAAA-1111-2222-3333-444444444444 ",
        });
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        await LaunchAsync();

        var environment = _engine.Runs.Single().Environment;
        Assert.Multiple(() =>
        {
            Assert.That(environment["RUNNER_EDITOR_ARGS_COMFYUI"], Is.EqualTo("--lowvram --preview-method auto"));
            Assert.That(environment["RUNNER_EDITOR_ARGS_FORGE_NEO"], Is.EqualTo("--use-ck-attention"));
            Assert.That(environment["OFFLOADR_GPU_UUID"], Is.EqualTo("GPU-aaaaaaaa-1111-2222-3333-444444444444"));
        });
    }

    [Test]
    public async Task ALaunch_WithoutEditorArgumentsOrAChosenGpu_SetsNoSuchVariables()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ComfyUiArguments = "   ", GpuUuid = "  " });
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        await LaunchAsync();

        var environment = _engine.Runs.Single().Environment;
        Assert.Multiple(() =>
        {
            Assert.That(environment.Keys, Has.None.StartsWith("RUNNER_EDITOR_ARGS_"));
            Assert.That(environment.Keys, Has.No.Member("OFFLOADR_GPU_UUID"));
        });
    }

    [Test]
    public async Task SettingsSavedDuringALaunchDownload_ApplyToItsContainer()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.OnPull = async _ =>
        {
            _engine.OnPull = null;
            await _settings.SaveAsync(_settings.Current with { ComfyUiArguments = "--lowvram" });
        };

        await LaunchAsync();

        Assert.That(_engine.Runs.Single().Environment["RUNNER_EDITOR_ARGS_COMFYUI"], Is.EqualTo("--lowvram"));
    }

    [Test]
    public async Task AKeyClearedDuringALaunchDownload_StartsNothing()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.OnPull = async _ =>
        {
            _engine.OnPull = null;
            await _settings.SaveAsync(_settings.Current with { RunnerKey = null });
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task ALaunchWhoseImageCannotBeDownloaded_ReportsFailed_AndStaysAvailable()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.PullFails = true;

        var launch = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        await WaitForStateAsync(SupervisorState.Available);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Message, Does.Contain("Could not pull"));
            Assert.That(supervisor.View.LaunchFailure?.Message, Does.Contain("Could not pull"));
            Assert.That(supervisor.View.Fault, Is.Null, "Availability itself is fine.");
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(_link.Running, Is.True);
        });
    }

    [Test]
    public async Task ALaunchFailure_TellsOffloadrNoEngineOutput()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.PullFails = true;
        _engine.PullFailureOutput = "Get \"https://blobs.example/sha256:abc?X-Amz-Signature=secret\": dial tcp: i/o timeout";

        var launch = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Message, Does.Contain("Could not pull").And.Not.Contain("Signature"));
            Assert.That(supervisor.View.LaunchFailure?.Detail ?? supervisor.View.LaunchFailure?.Message, Does.Contain("i/o timeout"), "The app itself still shows what happened.");
        });
    }

    [Test]
    public async Task SignedUrls_StayOutOfTheLogAndTheView()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.PullFails = true;
        _engine.PullFailureOutput = "Get \"https://blobs.example/sha256:abc?X-Amz-Signature=secret&X-Amz-Expires=300\": i/o timeout";

        var launch = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.LogLines, Has.None.Contains("Signature"));
            Assert.That(supervisor.LogLines, Has.Some.Contains("https://blobs.example/sha256:abc?[redacted]").And.Contains("i/o timeout"));
            Assert.That(supervisor.View.LaunchFailure?.Detail ?? supervisor.View.LaunchFailure?.Message, Does.Not.Contain("Signature"));
        });
    }

    [Test]
    public void SignedUrls_WithAnApostropheInTheirPath_AreRedactedToo()
    {
        var line = RunnerSupervisor.RedactUrlQueries("GET https://blobs.example/alex's-model/file?X-Amz-Signature=secret&X-Amz-Expires=300 failed");

        Assert.That(line, Is.EqualTo("GET https://blobs.example/alex's-model/file?[redacted] failed"));
    }

    [Test]
    public async Task ALaunchWhoseContainerStartedAndExited_IsReportedGone_NotFailed()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = (spec, _) =>
        {
            // The daemon started it and it exited; the CLI then failed (its answer lost, say).
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment);
            throw new ContainerEngineException("Could not start the runner container.", "context deadline exceeded");
        };
        // Offloadr retires the launch when it takes the exit report, and stops naming it.
        _link.ExitReportAnswer = _ =>
        {
            _link.Withdraw();
            return new ReleaseResult(true, true, string.Empty);
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the exit report");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null,
                "Its executor may have registered: the exit report lets the allocation launch again, where FAILED would fail it.");
            Assert.That(_engine.Exists(Name), Is.False, "It still held the runner key.");
        });
    }

    [Test]
    public async Task ALaunchWhoseContainerStartedAndExited_AndCannotBeRemoved_IsStillReportedGone()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = (spec, _) =>
        {
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment);
            _engine.RemoveStoppedFailure = new ContainerEngineException("Could not remove the container.", "device or resource busy");
            throw new ContainerEngineException("Could not start the runner container.", "context deadline exceeded");
        };
        _link.ExitReportAnswer = _ =>
        {
            _link.Withdraw();
            return new ReleaseResult(true, true, string.Empty);
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the exit report");
        await Task.Delay(200);
        Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "It was seen stopped after it ran: that stands, whatever its removal does.");

        // Removed once the engine lets go of it: it still holds the runner key.
        _engine.RemoveStoppedFailure = null;
        await WaitUntilAsync(() => !_engine.Exists(Name), "the stopped container to be removed");
    }

    [Test]
    public async Task ALaunchWhoseContainerCannotBeLookedAt_ReportsNothing_UntilTheEngineSays_ThenFailed()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = (spec, _) =>
        {
            // The start fails, and the engine then does not say what became of the container.
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
            _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
            throw new ContainerEngineException("Could not start the runner container.", "Error response from daemon: could not select device driver \"nvidia\" with capabilities: [[gpu]]");
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _supervisor!.LogLines.Any(line => line.Contains("did not say whether", StringComparison.Ordinal)), "the unsettled launch");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "FAILED for a container that ran would fail an allocation its exit report lets retry.");
            Assert.That(_link.ExitReports, Is.Empty, "Nor an exit for one that never started.");
        });

        // The engine answers: the container never started.
        _engine.InspectFailure = null;
        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId)?.Phase == RunnerLaunchPhase.Failed, "the launch to fail once the engine said so");
        Assert.That(_link.ExitReports, Is.Empty);
    }

    [Test]
    public async Task ALaunchWhoseStartIsNotKnown_IsNotRetiredByAHigherOfferRevision_ThoughNoEditorIsOfferedMeanwhile()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = (spec, _) =>
        {
            // The engine takes the start, its answer is lost, and it does not answer the look after it.
            _engine.AddContainer(Name, spec.Image, running: true, spec.Labels, spec.Environment);
            _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
            throw new ContainerEngineException("Could not start the runner container.", "error during connect: read tcp: connection reset by peer");
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);
        var revision = launch.OfferRevision;

        await WaitUntilAsync(() => _link.Profile!().EditorTemplates.Count == 0, "no editor to be offered");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Profile!().OfferRevision, Is.EqualTo(revision),
                "A heartbeat with a higher one retires the launch, and fences a container that did start before its executor registers.");
            Assert.That(_link.Running, Is.True);
        });

        // The engine answers: it did start. Its executor may not have registered yet.
        var highest = revision;
        _engine.InspectFailure = null;
        await WaitUntilAsync(() =>
        {
            highest = Math.Max(highest, _link.Profile!().OfferRevision);
            return _link.OutcomeOf(launch.LaunchId)?.Phase == RunnerLaunchPhase.Started && _link.Profile!().EditorTemplates.Count > 0;
        }, "the launch to be told as started, and the offer to come back");
        Assert.That(highest, Is.EqualTo(revision), "The same hardware throughout: the same revision.");
    }

    /// <summary>Launches with a start the engine took and never answered, and an engine that does not answer looks: no editor is offered.</summary>
    private async Task<LaunchRequest> LaunchUnsettledAsync()
    {
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid)], null);
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = (spec, _) =>
        {
            _engine.AddContainer(Name, spec.Image, running: true, spec.Labels, spec.Environment);
            _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
            throw new ContainerEngineException("Could not start the runner container.", "error during connect: read tcp: connection reset by peer");
        };
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => _link.Profile!().EditorTemplates.Count == 0, "no editor to be offered");
        return launch;
    }

    [Test]
    public async Task AFailedCheckOfAnotherGpu_DoesNotRaiseTheRevision_WhileAStartIsNotKnown()
    {
        var launch = await LaunchUnsettledAsync();
        var checks = _engine.GpuCheckImages.Count;

        // Another GPU is saved, which containers do not see: that says nothing of the one the launch was chosen for.
        await _settings.SaveAsync(_settings.Current with { GpuUuid = "GPU-bbbbbbbb-1111-2222-3333-444444444444" });
        await WaitUntilAsync(() => _engine.GpuCheckImages.Count > checks, "the other GPU to be checked");
        await Task.Delay(300);

        Assert.Multiple(() =>
        {
            Assert.That(_link.Profile!().OfferRevision, Is.EqualTo(launch.OfferRevision),
                "A higher one retires the launch, and fences a container that did start before its executor registers.");
            Assert.That(_link.Profile!().EditorTemplates, Is.Empty);
        });
    }

    [Test]
    public async Task AGpuListThatComesBackEmpty_DoesNotRaiseTheRevision_WhileAStartIsNotKnown()
    {
        var launch = await LaunchUnsettledAsync();
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnNextGpuCheck = async () =>
        {
            checking.TrySetResult();
            await answer.Task;
        };

        // This computer's own list fails for a moment, as everything is checked again after a wake.
        _hostGpus.Report = HostGpuReport.None("nvidia-smi did not answer.");
        _supervisor!.NotifyResumed();
        await checking.Task.WaitAsync(WaitLimit);
        await Task.Delay(200);
        var profile = _link.Profile!();
        answer.TrySetResult();

        Assert.Multiple(() =>
        {
            Assert.That(profile.OfferRevision, Is.EqualTo(launch.OfferRevision), "A list that fails for a moment is no reason to retire a launch whose container may have started.");
            Assert.That(profile.EditorTemplates, Is.Empty);
            Assert.That(profile.Resources.GpuCount, Is.EqualTo(1), "What was offered, as it was.");
        });
    }

    [Test]
    public async Task AGpuCheckTheEngineDidNotAnswer_DoesNotRaiseTheRevision_WhileAStartIsNotKnown_OneThatFindsNoGpuDoes()
    {
        var launch = await LaunchUnsettledAsync();
        var checks = _engine.GpuChecks;
        _engine.GpuCheck = new GpuAccessCheck(false, "error during connect: read tcp 10.0.0.2:2375: connection reset by peer");
        _supervisor!.NotifyResumed();

        await WaitUntilAsync(() => _engine.GpuChecks > checks, "the GPU check");
        await Task.Delay(300);
        Assert.That(_link.Profile!().OfferRevision, Is.EqualTo(launch.OfferRevision), "The engine did not answer: that says nothing of the GPU.");

        _engine.GpuCheck = new GpuAccessCheck(false, "No devices were found");
        _supervisor.NotifyResumed();
        await WaitUntilAsync(() => _link.Profile!().OfferRevision > launch.OfferRevision, "nothing to be offered, as an offer of its own");
    }

    [Test]
    public async Task AContainerRemovedBeforeItsStart_WhichTheEngineThenRefuses_IsToldAsGone()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        // Pruned between its creation and its start: the engine refuses the start, and nothing is there
        // to show that it never ran.
        _engine.RunOverride = (_, _) => throw new ContainerEngineException("Could not start the runner container.", "Error response from daemon: No such container: abc", EngineOperation.Run);
        _link.ExitReportAnswer = _ =>
        {
            _link.Withdraw();
            return new ReleaseResult(true, true, string.Empty);
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the container to be told as gone");
        await Task.Delay(200);
        Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "Told as started, the allocation would wait for an executor that cannot come.");
    }

    [Test]
    public async Task AContainerRemovedBeforeAStartTheEngineWasNeverAskedFor_IsALaunchThatFailed()
    {
        await StartSupervisorAsync(runTimeout: TimeSpan.FromMilliseconds(200));
        await MakeAvailableAsync();

        // The start is given up before the engine's command line runs, and someone removed the container.
        _engine.BeforeRun = () =>
        {
            Thread.Sleep(600);
            _engine.RemoveOn(null, Name);
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed), "Its start never reached the engine: it ran nothing.");
            Assert.That(_link.ExitReports, Is.Empty);
        });
    }

    [Test]
    public Task AContainerWhoseStartWasRefusedBeforeACrash_IsNotReportedGone_WhenItIsSeenCreated()
        => WithDataFolderAsync(async paths =>
        {
            var launchId = Guid.NewGuid().ToString();
            AddLaunchedContainer(launchId);
            _engine.MarkNeverStarted(Name);
            File.WriteAllText(paths.LaunchedContainers, $"refused {launchId} {FakeLauncherLink.TargetOf(_settings.Current)} -");

            await StartSupervisorAsync(paths: paths);

            await WaitUntilAsync(() => !_engine.Exists(Name), "the created container to be removed");
            await WaitUntilAsync(() => !File.Exists(paths.LaunchedContainers), "its record to go");
            await Task.Delay(200);
            Assert.That(_link.ExitReports, Is.Empty, "The engine refused its start: it ran nothing.");
        });

    [Test]
    public Task AContainerWhoseStartWasRefusedBeforeACrash_IsReportedGone_WhenItIsNotThere()
        => WithDataFolderAsync(async paths =>
        {
            var launchId = Guid.NewGuid().ToString();
            var engine = ContainerEngineSelection.Describe(new ContainerEngineSelection.Selected(null, null));
            File.WriteAllText(paths.LaunchedContainers, $"refused {launchId} {FakeLauncherLink.TargetOf(_settings.Current)} {engine}");

            await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();

            await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launchId), "the container to be told as gone");
        });

    [Test]
    public async Task APauseOfTheOffer_RaisesItsRevision_OnceOffloadrNoLongerNamesTheUnsettledLaunch()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = (spec, _) =>
        {
            _engine.AddContainer(Name, spec.Image, running: true, spec.Labels, spec.Environment);
            _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
            throw new ContainerEngineException("Could not start the runner container.", "error during connect: read tcp: connection reset by peer");
        };
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => _link.Profile!().EditorTemplates.Count == 0, "no editor to be offered");
        Assert.That(_link.Profile!().OfferRevision, Is.EqualTo(launch.OfferRevision));

        _link.Withdraw();

        await WaitUntilAsync(() => _link.Profile!().OfferRevision > launch.OfferRevision, "nothing to be offered, as an offer of its own");
        Assert.That(_link.Profile!().Resources.GpuCount, Is.Zero);
    }

    [Test]
    public async Task ALaunchWhoseContainerCannotBeLookedAt_ReportsItGone_OnceTheEngineShowsItRanAndExited()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = (spec, _) =>
        {
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment);
            _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
            throw new ContainerEngineException("Could not start the runner container.", "context deadline exceeded");
        };
        _link.ExitReportAnswer = _ =>
        {
            _link.Withdraw();
            return new ReleaseResult(true, true, string.Empty);
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _supervisor!.LogLines.Any(line => line.Contains("did not say whether", StringComparison.Ordinal)), "the unsettled launch");
        _engine.InspectFailure = null;

        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the exit report");
        await Task.Delay(200);
        Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "It ran: its exit is reported, not a failed launch.");
    }

    [Test]
    public async Task ACreationUnderWayWhenTheStreamDrops_IsLetFinish_AndItsContainerIsTheOneStarted()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        var creating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var givenUp = false;
        _engine.OnCreate = async (_, token) =>
        {
            creating.TrySetResult();
            await finish.Task;
            givenUp = token.IsCancellationRequested;
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);
        await creating.Task.WaitAsync(WaitLimit);

        // The stream drops while Docker creates the container, and the launch is looked at meanwhile.
        _link.StartsAllowed = false;
        _link.Touch();
        await Task.Delay(200);
        _link.Touch();
        await Task.Delay(100);
        finish.SetResult();
        await Task.Delay(200);
        Assert.That(_engine.Runs, Is.Empty, "Created, and not started while the stream is down.");

        _link.StartsAllowed = true;
        _link.Touch();

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(givenUp, Is.False, "Cancelled and asked again, the second creation would find the name taken by the first.");
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started));
            Assert.That(_engine.Creations, Is.EqualTo(1));
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ALaunchWhoseContainerIsGoneAfterAFailedStart_IsToldAsGone_NeverAsFailed()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = (_, _) =>
        {
            // The start fails, and the container is not there afterwards (someone removed it): nothing
            // shows it never ran.
            _engine.RunOverride = null;
            throw new ContainerEngineException("Could not start the runner container.", "context deadline exceeded");
        };
        _link.ExitReportAnswer = _ =>
        {
            _link.Withdraw();
            return new ReleaseResult(true, true, string.Empty);
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the exit report");
        await Task.Delay(200);
        Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "FAILED for a container that may have run would fail an allocation its exit report lets retry.");
    }

    [Test]
    public async Task AStopWhileAFailedStartIsLookedAt_ReportsNoExit_ForAContainerThatNeverStarted()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var looked = 0;

        // (Offloadr is not reached for a release: whatever the Stop lets go of, it accounts for itself.)
        _link.ReleaseAnswer = _ => new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).");
        _engine.RunOverride = (spec, _) =>
        {
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);

            // Stop is pressed as the look after the failed start removes the container it found.
            _engine.BeforeNextRemoveStopped = () =>
            {
                Interlocked.Exchange(ref looked, 1);
                supervisor.RequestStop();
            };
            throw new ContainerEngineException("Could not start the runner container.", "Error response from daemon: could not select device driver \"nvidia\" with capabilities: [[gpu]]");
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => Volatile.Read(ref looked) == 1, "the look after the failed start");
        await WaitForStateAsync(SupervisorState.Stopped);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.False);
            Assert.That(_link.ExitReports, Is.Empty, "It was seen created and never started: there is no exit to report.");
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "A Stop is no outcome Offloadr asked for.");
        });
    }

    [Test]
    public async Task TheAppClosingWhileAFailedStartIsLookedAt_ReportsNoExit_ForAContainerThatNeverStarted()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        Task? closed = null;
        _link.ReleaseAnswer = _ => new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).");
        _engine.RunOverride = (spec, _) =>
        {
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);

            // The app closes as the look after the failed start removes the container it found.
            _engine.BeforeNextRemoveStopped = () => closed = supervisor.StopAsync(CancellationToken.None);
            throw new ContainerEngineException("Could not start the runner container.", "Error response from daemon: could not select device driver \"nvidia\" with capabilities: [[gpu]]");
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => closed is not null, "the app to close");
        await closed!.WaitAsync(WaitLimit);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.False);
            Assert.That(_link.ExitReports, Is.Empty, "It was seen created and never started: there is no exit to report.");
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "Host shutdown is never a result.");
        });
    }

    [Test]
    public async Task AStopPressedWhileAnOutcomeWaitsToBeSubmitted_KeepsItBack()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();

        // Stop is pressed after the outcome was decided, while it waits for the link to take it.
        _link.OnNextOutcomeSubmitted = supervisor.RequestStop;

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitForStateAsync(SupervisorState.Stopped);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "Nothing is acknowledged for a container that is about to be removed.");
            Assert.That(_engine.Exists(Name), Is.False);
        });
    }

    [Test]
    public async Task TheAppClosingAsTheContainerIsStarted_KeepsItFromStarting()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        Task? closed = null;

        // The app closes after the start was decided, before the engine's process starts.
        _engine.BeforeRun = () => closed = supervisor.StopAsync(CancellationToken.None);

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => closed is not null, "the app to close");
        await closed!.WaitAsync(WaitLimit);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty, "It would register just as the app tears it down.");
            Assert.That(_engine.Exists(Name), Is.False, "What was created is removed.");
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null);
        });
    }

    [Test]
    public async Task AContainerSeenRunningWhileAFailedStartIsCleanedUp_IsNeverToldAsFailed()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = (spec, _) =>
        {
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);

            // The daemon starts it after all, between the look and the removal, which it refuses for a
            // container that runs; then it stops answering.
            _engine.BeforeNextRemoveStopped = () =>
            {
                _engine.Restart(Name);
                _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
            };
            throw new ContainerEngineException("Could not start the runner container.", "context deadline exceeded");
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("did not say whether", StringComparison.Ordinal)), "the unsettled launch");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "The engine saw it run: FAILED would forget a live executor.");
            Assert.That(_link.ExitReports, Is.Empty);
            Assert.That(_engine.Exists(Name), Is.True);
        });

        _engine.InspectFailure = null;
        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId)?.Phase == RunnerLaunchPhase.Started, "the launch to be told as started");
        Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running), "Supervised: released before it is stopped.");
    }

    [Test]
    public Task TheJournalSaysTheStartWasAskedFor_BeforeTheEngineIsAsked()
        => WithDataFolderAsync(async paths =>
        {
            await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            string? whileCreating = null;
            string? whileStarting = null;
            _engine.OnCreate = (_, _) =>
            {
                whileCreating = File.ReadAllText(paths.LaunchedContainers);
                return Task.CompletedTask;
            };
            _engine.BeforeRun = () => whileStarting = File.ReadAllText(paths.LaunchedContainers);

            var launch = await LaunchAsync();

            Assert.Multiple(() =>
            {
                Assert.That(whileCreating, Does.StartWith($"starting {launch.LaunchId} "), "Only created so far: gone unseen, it ran nothing.");
                Assert.That(whileStarting, Does.StartWith($"running {launch.LaunchId} "),
                    "From the start on, a crash must not take a container that may have run for one that was only created.");
            });
        });

    [Test]
    public async Task AStop_ForACreatedContainerThatWasNeverStartedAndIsGone_ReportsNoExit()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).");

        // The stream drops after the container was created, before its start is asked for.
        _link.OnNextTryStart = () => _link.StartsAllowed = false;
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => _engine.Creations == 1 && !_link.StartsAllowed, "the container to be created");
        await Task.Delay(100);

        // Someone removes it while it waits, and then the person presses Stop.
        await _engine.RemoveAsync(Name, CancellationToken.None);
        supervisor.RequestStop();

        await WaitForStateAsync(SupervisorState.Stopped);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(_link.ExitReports, Is.Empty, "This app never started it: there is no exit to report.");
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null);
        });
    }

    [Test]
    public async Task TheAppClosingForAnUpdate_StillStopsTheContainer_WhenAStopIsAskedAsItGoesDown()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();
        supervisor.KeepRunnerOnShutdown();

        // Stop is pressed (from the tray) after the app decided to leave the container for the update,
        // while it still ends its launcher.
        _link.OnNextCancel = supervisor.RequestStop;
        await supervisor.StopAsync(CancellationToken.None);

        Assert.That(_engine.Exists(Name), Is.False, "The updated app would reconnect to a container the person asked to stop.");
    }

    [Test]
    public async Task AContainerThatRanAndExitedWhileAFailedStartIsCleanedUp_IsToldAsGone_NeverAsFailed()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = (spec, _) =>
        {
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);

            // Seen created; then the daemon starts it after all, it exits, and the engine keeps it.
            _engine.BeforeNextRemoveStopped = () =>
            {
                _engine.Restart(Name);
                _engine.Exit(Name, 1);
                _engine.RemoveStoppedFailure = new ContainerEngineException("Could not remove container.", "device or resource busy");
            };
            throw new ContainerEngineException("Could not start the runner container.", "context deadline exceeded");
        };
        _link.ExitReportAnswer = _ =>
        {
            _link.Withdraw();
            return new ReleaseResult(true, true, string.Empty);
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the exit report");
        await Task.Delay(200);
        Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "It ran: what it was when it was first seen does not make its launch a failed one.");

        _engine.RemoveStoppedFailure = null;
        await WaitUntilAsync(() => !_engine.Exists(Name), "the stopped container to be removed");
    }

    [Test]
    public async Task ACreatedContainerSomeoneElseStarted_StaysSupervised_WhenItsLaunchIsWithdrawn()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();

        // The stream drops after the container was created, before its start is asked for.
        _link.OnNextTryStart = () => _link.StartsAllowed = false;
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => _engine.Creations == 1 && !_link.StartsAllowed, "the container to be created");
        await Task.Delay(100);

        // Someone starts it in Docker Desktop while it waits, and Offloadr withdraws the launch.
        _engine.Restart(Name);
        _link.Withdraw();

        await WaitForStateAsync(SupervisorState.Running);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.True, "Never removed by force.");
            Assert.That(_engine.Runs, Is.Empty, "This app did not start it.");
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.Not.EqualTo(RunnerLaunchPhase.Started));
        });

        // Still this app's container: a Stop releases and stops it.
        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.False);
            Assert.That(_link.Releases.Select(release => release.LaunchId), Does.Contain(launch.LaunchId));
        });
    }

    [Test]
    public async Task AGpuCheckAfterSleepThatTheStreamCutShort_IsMadeAgainBeforeTheStart()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.StartsAllowed = false;
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => supervisor.View.Message.StartsWith("Starting ", StringComparison.Ordinal), "the launch to wait for the stream");
        await Task.Delay(100);

        // The computer sleeps while the launch waits; back, the GPU is checked before the start, and the
        // stream drops while that check is under way.
        supervisor.NotifyResumed();
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnNextGpuCheck = async () =>
        {
            checking.TrySetResult();
            await finish.Task;
        };
        var before = _engine.GpuChecks;
        var atTheStart = 0;
        _engine.BeforeRun = () => atTheStart = _engine.GpuChecks;
        _link.StartsAllowed = true;
        _link.Touch();
        await checking.Task.WaitAsync(WaitLimit);
        _link.StartsAllowed = false;
        _link.Touch();
        await Task.Delay(200);
        finish.SetResult();
        await Task.Delay(100);
        Assert.That(_engine.Runs, Is.Empty);

        _link.StartsAllowed = true;
        _link.Touch();

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started));
            Assert.That(atTheStart, Is.GreaterThanOrEqualTo(before + 2), "The check that was cut short proved nothing: it is made again before the start.");
        });
    }

    [Test]
    public Task ALaunchNotStartedAfterAll_IsNotedAsOnlyCreatedAgain()
        => WithDataFolderAsync(async paths =>
        {
            await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();

            // The stream drops between the last check and the start.
            _link.OnNextTryStart = () => _link.StartsAllowed = false;
            var launch = _link.Name("comfyui-latest", ComfyUi);
            await WaitUntilAsync(() => _engine.Creations == 1 && !_link.StartsAllowed, "the container to be created");
            await Task.Delay(200);

            Assert.Multiple(() =>
            {
                Assert.That(_engine.Runs, Is.Empty);
                Assert.That(File.ReadAllText(paths.LaunchedContainers), Does.StartWith($"starting {launch.LaunchId} "),
                    "Its start was not asked for: gone unseen, it ran nothing.");
            });
        });

    [Test]
    public async Task AStop_OffersNothing_FromTheMomentItIsAsked()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();
        Assert.That(_link.Profile!().EditorTemplates, Is.Not.Empty);

        // Read on the loop's own thread right after the Stop is asked: before it can publish again.
        LauncherProfile? atStop = null;
        var asked = 0;
        supervisor.Changed += () =>
        {
            if (Interlocked.Exchange(ref asked, 1) == 0)
            {
                supervisor.RequestStop();
                atStop = _link.Profile?.Invoke();
            }
        };
        supervisor.RequestRefresh();

        await WaitUntilAsync(() => Volatile.Read(ref asked) == 1 && atStop is not null, "the heartbeat at the Stop");
        await WaitForStateAsync(SupervisorState.Stopped);
        Assert.Multiple(() =>
        {
            Assert.That(atStop!.EditorTemplates, Is.Empty, "Offloadr could choose this computer for a launch it will not start.");
            Assert.That(atStop.Resources.GpuCount, Is.Zero);
        });
    }

    [Test]
    public async Task AStop_KeepsSupervisingAContainerThatStartsAsItIsRemoved_AndIsKept()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).");
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.RunOverride = async (spec, token) =>
        {
            // The start is asked for and never answered: the container is still only created.
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
            starting.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return string.Empty;
        };

        _link.Name("comfyui-latest", ComfyUi);
        await starting.Task.WaitAsync(WaitLimit);

        // Stop: seen created, it is not stopped first. The engine starts it right then, refuses its removal
        // (it runs, and is never removed by force), and does not stop it either.
        _engine.BeforeNextRemoveStopped = () => _engine.Restart(Name);
        _engine.NextStopFailure = new ContainerEngineException("docker stop timed out.");
        supervisor.RequestStop();

        await WaitUntilAsync(() => supervisor.View.State is SupervisorState.Faulted or SupervisorState.Stopped, "the Stop to end");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Faulted), "It still runs: not let go of as stopped.");
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(_link.ExitReports, Is.Empty, "Nothing is told as gone while it runs.");
        });

        // The next Stop finds it running, and stops it.
        supervisor.RequestStop();
        await WaitUntilAsync(() => !_engine.Exists(Name), "the container to be stopped");
    }

    [Test]
    public async Task AStop_StopsAContainerThatStartsAsItIsRemoved_BeforeItRemovesIt()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).");
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.RunOverride = async (spec, token) =>
        {
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
            starting.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return string.Empty;
        };
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await starting.Task.WaitAsync(WaitLimit);
        var stops = _engine.Stops;
        bool? stoppedFirst = null;
        _engine.BeforeNextRemoveStopped = () => _engine.Restart(Name);
        _engine.BeforeNextRemove = () => stoppedFirst = _engine.Stops > stops;

        supervisor.RequestStop();

        await WaitForStateAsync(SupervisorState.Stopped);
        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the container to be told as gone");
        Assert.Multiple(() =>
        {
            Assert.That(stoppedFirst, Is.True, "It may serve by then: stopped, not killed.");
            Assert.That(_engine.Exists(Name), Is.False);
        });
    }

    [Test]
    public async Task AnIdleReleaseWhoseReasonIsGone_IsNotSent()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        var launch = await LaunchAsync();
        var original = _settings.Current;

        // Other settings would release the warm container; they are changed back as its release is
        // about to be sent, after the loop last looked.
        var reverted = 0;
        _link.BeforeNextRelease = () =>
        {
            _settings.SaveAsync(original).GetAwaiter().GetResult();
            Interlocked.Exchange(ref reverted, 1);
        };
        await _settings.SaveAsync(original with { ComfyUiArguments = "--lowvram" });

        await WaitUntilAsync(() => Volatile.Read(ref reverted) == 1, "the release to be about to be sent");
        await Task.Delay(300);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Releases.Select(release => release.LaunchId), Does.Not.Contain(launch.LaunchId),
                "A grant is for good: it would cost the next session a cold start for nothing.");
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));
        });
    }

    [Test]
    public async Task AStopRightAfterTheEngineRefusedTheStart_ReportsNoExit_ForTheContainerItFindsCreated()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).");
        _engine.RunOverride = (spec, _) =>
        {
            // The engine refuses the start; Stop is pressed before the app has looked at the container.
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
            supervisor.RequestStop();
            throw new ContainerEngineException("Could not start the runner container.", "Error response from daemon: could not select device driver \"nvidia\" with capabilities: [[gpu]]");
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitForStateAsync(SupervisorState.Stopped);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.False);
            Assert.That(_link.ExitReports, Is.Empty, "The engine refused its start: it never ran, and there is no exit to report.");
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "A Stop is no outcome Offloadr asked for.");
        });
    }

    [Test]
    public async Task AnUnsettledContainerWhoseRemovalLeavesItsStateUnknown_StaysSupervised()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = (spec, _) =>
        {
            // The start is given up on, and the engine does not say what became of the container.
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
            _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
            throw new ContainerEngineException("Could not start the runner container.", "context deadline exceeded");
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("did not say whether", StringComparison.Ordinal)), "the unsettled launch");

        // The watch sees it still created, and its removal fails with the engine saying nothing more:
        // the daemon may be starting it at this very moment.
        _engine.RemoveStoppedFailure = new ContainerEngineException("Could not remove container offloadr-runner.", "context deadline exceeded");
        _engine.BeforeNextRemoveStopped = () => _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
        _engine.InspectFailure = null;
        await WaitUntilAsync(() => _engine.BeforeNextRemoveStopped is null, "the removal");
        await Task.Delay(300);
        Assert.Multiple(() =>
        {
            Assert.That(_link.ExitReports, Is.Empty, "Not confirmed stopped or gone: telling Offloadr so would fence a container that may be starting.");
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null);
            Assert.That(_engine.Exists(Name), Is.True);
        });

        // It did start: once the engine answers, it is the runner.
        _engine.Restart(Name);
        _engine.RemoveStoppedFailure = null;
        _engine.InspectFailure = null;
        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId)?.Phase == RunnerLaunchPhase.Started, "the launch to be told as started");
        Assert.That(_link.ExitReports, Is.Empty);
    }

    [Test]
    public async Task AGpuRefusalConfirmedLate_HasTheGpuCheckedAgain()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = (spec, _) =>
        {
            // Docker cannot attach the GPU, and then does not say what became of the container.
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
            _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
            throw new ContainerEngineException("Could not start the runner container.", "Error response from daemon: could not select device driver \"nvidia\" with capabilities: [[gpu]]", EngineOperation.Run);
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("did not say whether", StringComparison.Ordinal)), "the unsettled launch");
        var checks = _engine.GpuChecks;
        _engine.GpuCheck = new GpuAccessCheck(false, "could not select device driver \"nvidia\" with capabilities: [[gpu]]");
        _engine.InspectFailure = null;

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId)?.Phase == RunnerLaunchPhase.Failed, "the launch to fail once the engine said so");
        await WaitUntilAsync(() => _engine.GpuChecks > checks, "the GPU to be checked again");
        await WaitUntilAsync(() => supervisor.View.State is SupervisorState.WaitingToRetry or SupervisorState.Faulted || OffersNothing(),
            "the computer to stop being offered until the GPU works");
    }

    [Test]
    public async Task AGpuRefusalConfirmedLate_KeepsTheLauncher_UntilItsFailureIsDelivered()
    {
        var supervisor = await StartSupervisorAsync(retryBackoff: TimeSpan.FromMinutes(5));
        await MakeAvailableAsync();
        _engine.RunOverride = (spec, _) =>
        {
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
            _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
            throw new ContainerEngineException("Could not start the runner container.", "Error response from daemon: could not select device driver \"nvidia\" with capabilities: [[gpu]]", EngineOperation.Run);
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("did not say whether", StringComparison.Ordinal)), "the unsettled launch");

        // The failure is submitted and not delivered yet (Offloadr is slow to answer), and the GPU check
        // that follows fails.
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _link.OutcomeGate = delivered.Task;
        _engine.GpuCheck = new GpuAccessCheck(false, "could not select device driver \"nvidia\" with capabilities: [[gpu]]");
        _engine.InspectFailure = null;

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId)?.Phase == RunnerLaunchPhase.Failed, "the launch to fail once the engine said so");
        await WaitForStateAsync(SupervisorState.WaitingToRetry);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Running, Is.True, "The launcher stays to deliver the failure: without it the allocation waits for its timeout.");
            Assert.That(OffersNothing(), Is.True, "Offering nothing meanwhile.");
        });

        delivered.SetResult();
        await WaitUntilAsync(() => !_link.Running, "the launcher to stop once the failure is delivered");
    }

    [Test]
    public async Task AStopWhileTheWatchRemovesAContainerWhoseStartWasRefused_ReportsNoExit()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).");
        _engine.RunOverride = (spec, _) =>
        {
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
            _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
            throw new ContainerEngineException("Could not start the runner container.", "Error response from daemon: could not select device driver \"nvidia\" with capabilities: [[gpu]]", EngineOperation.Run);
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("did not say whether", StringComparison.Ordinal)), "the unsettled launch");

        // The engine answers: the watch sees it created, and removes it. Stop is pressed as it does.
        var pressed = 0;
        _engine.BeforeNextRemoveStopped = () =>
        {
            supervisor.RequestStop();
            Interlocked.Exchange(ref pressed, 1);
        };
        _engine.InspectFailure = null;

        await WaitUntilAsync(() => Volatile.Read(ref pressed) == 1, "the Stop during the removal");
        await WaitForStateAsync(SupervisorState.Stopped);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.False);
            Assert.That(_link.ExitReports, Is.Empty, "The watch saw it created after a refused start: it never ran, whoever asks next.");
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "A Stop is no outcome Offloadr asked for.");
        });
    }

    [Test]
    public async Task AStop_ForAContainerThatWasOnlyCreated_AsksForNoRelease()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();

        // The stream drops after the container was created, before its start is asked for.
        _link.OnNextTryStart = () => _link.StartsAllowed = false;
        _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => _engine.Creations == 1 && !_link.StartsAllowed, "the container to be created");
        await Task.Delay(100);

        supervisor.RequestStop();

        await WaitForStateAsync(SupervisorState.Stopped);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.False, "Removed: it holds the runner key.");
            Assert.That(_link.Releases, Is.Empty, "It ran nothing to release: a grant would fence its launch for good.");
            Assert.That(_link.ExitReports, Is.Empty);
        });
    }

    [Test]
    public async Task AContainerThatStartsAgainAsItsSecondRemovalFails_StaysSupervised()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = (spec, _) =>
        {
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);

            // Seen created. It runs as its removal is tried (refused), is seen stopped again, and runs
            // once more as the second removal is tried, whose answer is lost.
            _engine.BeforeNextRemoveStopped = () =>
            {
                _engine.Restart(Name);
                _engine.OnNextInspect = () =>
                {
                    _engine.Exit(Name, 1);
                    _engine.BeforeNextRemoveStopped = () =>
                    {
                        _engine.Restart(Name);
                        _engine.RemoveStoppedFailure = new ContainerEngineException("Could not remove container offloadr-runner.", "unexpected EOF");
                    };
                };
            };
            throw new ContainerEngineException("Could not start the runner container.", "context deadline exceeded");
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null || !_link.ExitReports.IsEmpty, "the launch to be settled");
        _engine.RemoveStoppedFailure = null;
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.ExitReports, Is.Empty, "It runs: an error on its removal is no proof it stayed stopped.");
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started));
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task AContainerTheEngineSawRunning_IsNeverToldAsFailed_ThoughItLooksCreatedLater()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = (spec, _) =>
        {
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);

            // The engine refuses the start; yet the container runs when its removal is tried (refused for
            // that), and then the engine does not say what became of it.
            _engine.BeforeNextRemoveStopped = () =>
            {
                _engine.Restart(Name);
                _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
            };
            throw new ContainerEngineException("Could not start the runner container.", "Error response from daemon: could not select device driver \"nvidia\" with capabilities: [[gpu]]", EngineOperation.Run);
        };
        _link.ExitReportAnswer = _ =>
        {
            _link.Withdraw();
            return new ReleaseResult(true, true, string.Empty);
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("did not say whether", StringComparison.Ordinal)), "the unsettled launch");

        // Later the engine shows it as created and not running.
        _engine.MarkNeverStarted(Name);
        _engine.InspectFailure = null;

        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the container to be told as gone");
        await Task.Delay(200);
        Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "The engine saw it run: no later look makes it a launch that failed.");
    }

    [Test]
    public async Task AStartPressedWhileAStopIsStillCarriedOut_OffersNothingUntilItIsDone()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();
        _engine.StopDuration = TimeSpan.FromSeconds(1);

        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopping);
        supervisor.RequestStart();
        await Task.Delay(200);

        // The launcher is still there, to release the container: its heartbeat offers nothing meanwhile.
        var during = supervisor.View.State == SupervisorState.Stopping ? _link.Profile?.Invoke() : null;
        Assert.That(during, Is.Not.Null, "The Stop was still being carried out when the heartbeat was read.");
        Assert.Multiple(() =>
        {
            Assert.That(during!.EditorTemplates, Is.Empty, "No launch is taken until the Stop is done: Offloadr must not choose this computer for one.");
            Assert.That(during.Resources.GpuCount, Is.Zero);
        });

        // Offered again once the Stop is done and the checks have passed.
        await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Available && _link.Running && _link.Profile!().EditorTemplates.Count > 0, "the offer after the Start");
    }

    [Test]
    public Task ALaunchWhoseCredentialsCannotBeSaved_CreatesNothing()
        => WithDataFolderAsync(async paths =>
        {
            await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();

            // The note of what containers are started with cannot be written (a folder is in its place).
            Directory.CreateDirectory(paths.LaunchedCredentials);

            var launch = _link.Name("comfyui-latest", ComfyUi);

            await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
            Assert.Multiple(() =>
            {
                Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
                Assert.That(_engine.Creations, Is.Zero, "Without it, a report owed after Settings change and a restart could never be sent.");
                Assert.That(File.Exists(paths.LaunchedContainers), Is.False, "Nothing was made: nothing is noted.");
            });
        });

    [Test]
    public async Task ALaunchWhoseContainerRanAndWasStopped_IsToldAsStarted_WhenOffloadrStillNamesIt()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).");
        var finish = HoldNextRun(out var running);

        // The container runs (its executor may have registered and matched the launch) while the start
        // has not returned yet; Stop is pressed before the launch's outcome went out.
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await running.WaitAsync(WaitLimit);
        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);
        finish.TrySetResult();
        Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "A Stop is no outcome Offloadr asked for.");
        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the container to be told as gone");

        // Offloadr still names it when this computer is offered again.
        supervisor.RequestStart();
        await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Available && _link.Running, "the offer again");
        _link.NameAgain(launch);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started),
            "It ran: a launch that ran is never told as failed, whoever stopped its container.");
    }

    [Test]
    public async Task TheAppClosingWhileAStopRemovesAContainerThatNeverStarted_ReportsNoExit()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).");
        _engine.RunOverride = (spec, _) =>
        {
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
            _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
            throw new ContainerEngineException("Could not start the runner container.", "Error response from daemon: could not select device driver \"nvidia\" with capabilities: [[gpu]]", EngineOperation.Run);
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("did not say whether", StringComparison.Ordinal)), "the unsettled launch");

        // Stop is pressed; the app closes as the Stop removes the container, which it found created.
        Task? closed = null;
        _engine.BeforeNextRemoveStopped = () => closed = supervisor.StopAsync(CancellationToken.None);
        supervisor.RequestStop();
        _engine.InspectFailure = null;

        await WaitUntilAsync(() => closed is not null, "the app to close");
        await closed!.WaitAsync(WaitLimit);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.False);
            Assert.That(_link.ExitReports, Is.Empty, "The Stop saw it created after a refused start: it never ran, whoever finishes the Stop.");
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null);
        });
    }

    [Test]
    public async Task AGpuOfThisComputerThatGoesWhileTheContainerIsCreated_FailsTheLaunchBeforeItStarts()
    {
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid)], null);
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        // Changed while Docker creates the container, with no sleep to tell of it: not the hardware
        // that was offered any more.
        _engine.OnCreate = (_, _) =>
        {
            _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 12282, FakeContainerEngine.GpuUuid)], null);
            return Task.CompletedTask;
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        await WaitUntilAsync(() => !_engine.Exists(Name), "what was created to be removed");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_engine.Runs, Is.Empty, "Not started on hardware Offloadr did not choose this computer for.");
        });
    }

    [Test]
    public async Task ANewKeySavedAsTheContainerIsStarted_KeepsItFromStarting()
    {
        const string newKey = "another-runner-key-0123456789abcdefghijklmnop";
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        // Saved after the start was decided, as the engine is asked: the container holds the earlier key.
        _engine.BeforeRun = () => _settings.SaveAsync(_settings.Current with { RunnerKey = newKey }).GetAwaiter().GetResult();

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _engine.Creations == 1 && !_engine.Exists(Name), "what was created to be removed");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty, "Never started for a runner Settings no longer name.");
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.Not.EqualTo(RunnerLaunchPhase.Started));
        });
    }

    [Test]
    public async Task AnotherGpuSavedRightBeforeTheStart_DropsTheLaunch_WithoutFailingIt()
    {
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid)], null);
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();

        // Saved in the very step that makes the last checks before the start (as this computer's GPUs,
        // listed once more after the container was created, are recorded), after the loop last looked at
        // the settings: other hardware than Offloadr chose this computer for. Its heartbeat with the new
        // offer is not accepted yet.
        _link.HoldAcceptedOfferRevision = _link.Profile!().OfferRevision;
        var probesAtCreation = int.MaxValue;
        var saved = 0;
        _engine.OnCreate = (_, _) =>
        {
            probesAtCreation = _hostGpus.Probes;
            return Task.CompletedTask;
        };
        supervisor.Changed += () =>
        {
            if (_hostGpus.Probes > probesAtCreation && Interlocked.Exchange(ref saved, 1) == 0)
            {
                _settings.SaveAsync(_settings.Current with { GpuUuid = FakeContainerEngine.GpuUuid }).GetAwaiter().GetResult();
            }
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("stopped the launch of", StringComparison.Ordinal)), "the launch to be dropped");
        await WaitUntilAsync(() => !_engine.Exists(Name), "what was created to be removed");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null,
                "FAILED before Offloadr accepted the new offer would fail the allocation, where it gets a new launch otherwise.");
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public Task AStartTheEngineRefused_IsNotedAsNotMade_BeforeTheContainerIsLookedAt()
        => WithDataFolderAsync(async paths =>
        {
            await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            string? atTheLook = null;
            _engine.RunOverride = (spec, _) =>
            {
                _engine.RunOverride = null;
                _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
                _engine.OnNextInspect = () => atTheLook = File.ReadAllText(paths.LaunchedContainers);
                throw new ContainerEngineException("Could not start the runner container.", "Error response from daemon: could not select device driver \"nvidia\" with capabilities: [[gpu]]", EngineOperation.Run);
            };

            var launch = _link.Name("comfyui-latest", ComfyUi);

            await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
            Assert.Multiple(() =>
            {
                Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
                Assert.That(atTheLook, Does.StartWith($"refused {launch.LaunchId} "),
                    "After a crash right there, the container found created is one that never ran: no exit is reported for it.");
            });
        });

    [Test]
    public async Task AStopPressedAsTheStartIsAskedFor_StartsNothing_AsksForNoRelease_AndReportsNoExit()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();

        // Stop is pressed as the start is asked for: the engine is never asked to start the container.
        _link.OnNextTryStart = supervisor.RequestStop;

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitForStateAsync(SupervisorState.Stopped);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(_engine.Exists(Name), Is.False, "What was created is removed: it holds the runner key.");
            Assert.That(_link.Releases, Is.Empty, "It was only created: a grant would fence its launch for good.");
            Assert.That(_link.ExitReports.Where(report => report.LaunchId == launch.LaunchId), Is.Empty, "It never ran.");
        });
    }

    [Test]
    public async Task ASecondGpuAppearingBeforeTheStart_HasAContainerSayWhatContainersGet()
    {
        const string other = "GPU-cccccccc-1111-2222-3333-444444444444";
        var first = new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid);
        var second = new HostGpu("NVIDIA GeForce RTX 3060", 12288, other);
        _hostGpus.Report = new HostGpuReport([first], null);
        _engine.ContainerGpus = [first];
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        var listed = _engine.ContainerGpuLists;

        // A second GPU shows while the container is created; containers would get it too, and list it first.
        _engine.OnCreate = (_, _) =>
        {
            _hostGpus.Report = new HostGpuReport([first, second], null);
            _engine.ContainerGpus = [second, first];
            return Task.CompletedTask;
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_engine.ContainerGpuLists, Is.GreaterThan(listed), "This computer's list changed: a container says what containers get now.");
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_engine.Runs, Is.Empty, "Not started with another set of GPUs than Offloadr chose this computer for.");
        });
    }

    [Test]
    public async Task AStopThatCancelsTheStartBeforeItsCommandRuns_AsksForNoRelease_AndReportsNoExit()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).");

        // Stop is pressed after the start was dispatched, before the engine's command line is started:
        // the engine never hears of it.
        _engine.BeforeRun = supervisor.RequestStop;

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitForStateAsync(SupervisorState.Stopped);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(_engine.Exists(Name), Is.False, "What was created is removed: it holds the runner key.");
            Assert.That(_link.Releases, Is.Empty, "It was only created: a grant would fence its launch for good.");
            Assert.That(_link.ExitReports.Where(report => report.LaunchId == launch.LaunchId), Is.Empty, "It never ran.");
        });
    }

    [Test]
    public async Task WithSeveralGpusHere_AndContainersSeeingOne_AContainerSaysWhichBeforeTheStart()
    {
        const string other = "GPU-cccccccc-1111-2222-3333-444444444444";
        var first = new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid);
        var second = new HostGpu("NVIDIA GeForce RTX 3060", 12288, other);
        _hostGpus.Report = new HostGpuReport([first, second], null);
        _engine.ContainerGpus = [first];
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        Assert.That(_link.Profile!().Resources.GpuCount, Is.EqualTo(1), "Containers see only the 4090.");

        // The engine exposes the other GPU instead while the container is created; this computer's own
        // list says nothing of it.
        _engine.OnCreate = (_, _) =>
        {
            _engine.ContainerGpus = [second];
            return Task.CompletedTask;
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_engine.Runs, Is.Empty, "Not started on another GPU than Offloadr chose this computer for.");
        });
    }

    [Test]
    public async Task AStartGivenUpBeforeItsCommandRuns_FailsTheLaunch_AndReportsNoExit()
    {
        await StartSupervisorAsync(runTimeout: TimeSpan.FromMilliseconds(200));
        await MakeAvailableAsync();

        // The engine's command line is not started before the start is given up (the computer is busy):
        // the engine never hears of it.
        _engine.BeforeRun = () => Thread.Sleep(600);

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed), "It never ran: the launch failed.");
            Assert.That(_link.ExitReports, Is.Empty, "An exit report would cost the allocation a retry for a container that never ran.");
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(_engine.Exists(Name), Is.False);
        });
    }

    [Test]
    public Task ALaunchWhoseStartCannotBeNoted_ShowsNoContainer_OnceWhatItCreatedIsRemoved()
        => WithDataFolderAsync(async paths =>
        {
            var supervisor = await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();

            // The record cannot be written any more once the container is created (a folder is in its place).
            _engine.OnCreate = (_, _) =>
            {
                File.Delete(paths.LaunchedContainers);
                Directory.CreateDirectory(paths.LaunchedContainers);
                return Task.CompletedTask;
            };

            var launch = _link.Name("comfyui-latest", ComfyUi);

            await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
            await WaitUntilAsync(() => !_engine.Exists(Name), "what was created to be removed");
            await WaitUntilAsync(() => supervisor.View.State != SupervisorState.Running, "the view to show no container");
            Assert.Multiple(() =>
            {
                Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
                Assert.That(_engine.Runs, Is.Empty);
                Assert.That(supervisor.View.LaunchFailure, Is.Not.Null, "Why the launch failed stays shown.");
            });
            Directory.Delete(paths.LaunchedContainers);
        });

    [Test]
    public async Task AStopPressedAsALateStartIsSettled_KeepsItsOutcomeBack()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = (spec, _) =>
        {
            // The start fails while the container runs, and the engine does not say so yet.
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: true, spec.Labels, spec.Environment);
            _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
            throw new ContainerEngineException("Could not start the runner container.", "context deadline exceeded");
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("did not say whether", StringComparison.Ordinal)), "the unsettled launch");

        // Stop is pressed at the very moment the engine's answer is recorded: after the loop last looked
        // at what was asked, and before the outcome is submitted.
        var pressed = 0;
        supervisor.Changed += () =>
        {
            if (supervisor.LogLines.Any(line => line.Contains("started after all", StringComparison.Ordinal)) &&
                Interlocked.Exchange(ref pressed, 1) == 0)
            {
                supervisor.RequestStop();
            }
        };
        _engine.InspectFailure = null;

        await WaitUntilAsync(() => Volatile.Read(ref pressed) == 1, "the late start to be settled");
        await WaitForStateAsync(SupervisorState.Stopped);
        await Task.Delay(200);
        Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "Nothing is acknowledged for a container that is about to be removed.");
    }

    [Test]
    public async Task ACreationWhoseAnswerWasLost_IsStillLookedFor_AndWhatItMadeRemoved()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnCreate = (spec, token) =>
        {
            // The connection drops after the daemon took the request: the command line fails, and the
            // container shows only later.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
                created.TrySetResult();
            });
            throw new ContainerEngineException("Could not start the runner container.", "error during connect: read tcp: connection reset by peer");
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId)?.Phase == RunnerLaunchPhase.Failed, "the launch to fail");
        await created.Task.WaitAsync(WaitLimit);
        await WaitUntilAsync(() => !_engine.Exists(Name), "the late container to be removed");
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(_link.ExitReports, Is.Empty, "It never ran.");
        });
    }

    [Test]
    public async Task ACreationWhoseAnswerWasLost_AndWhoseContainerSomeoneStartedAndItExited_IsToldAsGone_NeverAsFailed()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.OnCreate = (spec, _) =>
        {
            // The daemon made the container, the answer was lost, and another client started it: it has
            // run and exited by the time it is looked for.
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment);
            _engine.Exit(Name, 1);
            throw new ContainerEngineException("Could not start the runner container.", "error during connect: read tcp: connection reset by peer");
        };
        _link.ExitReportAnswer = _ =>
        {
            _link.Withdraw();
            return new ReleaseResult(true, true, string.Empty);
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the container to be told as gone");
        await WaitUntilAsync(() => !_engine.Exists(Name), "its removal");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "It ran: FAILED arriving first would fail the allocation its exit report lets retry.");
            Assert.That(_engine.Runs, Is.Empty, "This app did not start it.");
        });
    }

    [Test]
    public async Task Stop_StillRemovesAContainerTheEngineCreatesLongAfterTheCancel()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var creating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnCreate = async (spec, token) =>
        {
            creating.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                // The daemon goes on, and creates the container well after the settle window.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
                    created.TrySetResult();
                });
                throw;
            }
        };

        _link.Name("comfyui-latest", ComfyUi);
        await creating.Task.WaitAsync(WaitLimit);
        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);

        await created.Task.WaitAsync(WaitLimit);
        await WaitUntilAsync(() => !_engine.Exists(Name), "the late container to be removed");
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty, "A container that is only created never runs: nothing starts unseen.");
            Assert.That(_link.ExitReports, Is.Empty);
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Stopped));
        });
    }

    [Test]
    public async Task Logs_HideAStoppedContainersKey_InLinesThatArriveLate()
    {
        const string newKey = "another-runner-key-0123456789abcdefghijklmnop";
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

        await _settings.SaveAsync(_settings.Current with { RunnerKey = newKey });
        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);

        // Buffered by the engine, and delivered after the container was let go of.
        _engine.WriteLog($"late {Key}");

        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.StartsWith("late", StringComparison.Ordinal)));
        Assert.That(supervisor.LogLines, Has.None.Contains(Key));
    }

    [Test]
    public async Task ALaunchWithdrawnWhileItsContainerCannotBeLookedAt_IsSettledAllTheSame()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = (spec, _) =>
        {
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
            _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
            throw new ContainerEngineException("Could not start the runner container.", "Error response from daemon: could not select device driver \"nvidia\" with capabilities: [[gpu]]");
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("did not say whether", StringComparison.Ordinal)), "the unsettled launch");
        _link.Withdraw();
        await Task.Delay(200);
        _engine.InspectFailure = null;

        // The engine answers: nothing was created, and the withdrawal is the launch's outcome.
        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId)?.Phase == RunnerLaunchPhase.Cancelled, "the withdrawal to be reported");
        await WaitForStateAsync(SupervisorState.Available);
        await WaitUntilAsync(() => _supervisor!.View.IsReady && _link.Running, "the launcher to be ready");

        // And the computer takes the next launch.
        await LaunchAsync();
    }

    [Test]
    public async Task ALaunchWhoseContainerExitedByItself_IsNeverFailed_WhenNamedAgainAfterItsExitReport()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = (spec, _) =>
        {
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment);
            throw new ContainerEngineException("Could not start the runner container.", "context deadline exceeded");
        };

        // Its executor had matched the launch: Offloadr takes the exit report and still names it.
        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the exit report");
        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's terminal report");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started),
                "It started: FAILED would fail an allocation over a container that ran, and whose exit Offloadr knows.");
            Assert.That(_engine.Runs, Is.Empty.Or.Count.EqualTo(0), "Never started twice with the same launch id.");
        });
    }

    [Test]
    public async Task Stop_DoesNotWaitForAnIdleReleaseOffloadrDoesNotAnswer()
    {
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromMilliseconds(100));
        await MakeAvailableAsync();
        await LaunchAsync();
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _link.OnRelease = async token =>
        {
            asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        await asked.Task.WaitAsync(WaitLimit);

        supervisor.RequestStop();

        // The Stop's own release is asked briefly; neither holds the Stop up for long.
        await WaitForStateAsync(SupervisorState.Stopped);
        Assert.That(_engine.Exists(Name), Is.False);
    }

    private async Task WithDataFolderAsync(Func<SupervisorPaths, Task> test)
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            await test(new SupervisorPaths(data));
        }
        finally
        {
            if (_supervisor is not null)
            {
                await _supervisor.StopAsync(CancellationToken.None);
                _supervisor.Dispose();
                _supervisor = null;
            }

            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public Task AContainerThatRanAndExited_BeforeItsStartWasConfirmed_IsReportedGoneAfterACrash()
        => WithDataFolderAsync(async paths =>
        {
            // The app closed after `docker run` started the container and before it noted that; it exited since.
            var launchId = Guid.NewGuid().ToString();
            AddLaunchedContainer(launchId);
            _engine.Exit(Name, 1);
            var engine = ContainerEngineSelection.Describe(new ContainerEngineSelection.Selected(null, null));
            File.WriteAllText(paths.LaunchedContainers, $"starting {launchId} {FakeLauncherLink.TargetOf(_settings.Current)} {engine}");

            await StartSupervisorAsync(paths: paths);

            await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launchId), "the exit report");
            Assert.That(_engine.Exists(Name), Is.False, "It still held the runner key.");
        });

    [Test]
    public Task AContainerStartedJustBeforeACrash_IsAdopted_ThoughItsStartWasNotConfirmed()
        => WithDataFolderAsync(async paths =>
        {
            // The app closed after starting the container and before noting that it had: it runs, and may serve.
            var launchId = Guid.NewGuid().ToString();
            AddLaunchedContainer(launchId);
            var engine = ContainerEngineSelection.Describe(new ContainerEngineSelection.Selected(null, null));
            File.WriteAllText(paths.LaunchedContainers, $"starting {launchId} {FakeLauncherLink.TargetOf(_settings.Current)} {engine}");

            var supervisor = await StartSupervisorAsync(paths: paths);

            await WaitForStateAsync(SupervisorState.Running);
            await Task.Delay(200);
            Assert.Multiple(() =>
            {
                Assert.That(_engine.Exists(Name), Is.True, "Supervised again, not stopped: nobody asked for that.");
                Assert.That(_link.ExitReports, Is.Empty);
                Assert.That(File.ReadAllText(paths.LaunchedContainers), Does.StartWith("running "), "Seen started: its absence would be an exit from now on.");
                Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));
            });
        });

    [Test]
    public Task ContainersKnownGone_AreReported_WhileStartupStillWaitsForDocker()
        => WithDataFolderAsync(async paths =>
        {
            var launchId = Guid.NewGuid().ToString();
            File.WriteAllText(paths.LaunchedContainers, $"gone {launchId} {FakeLauncherLink.TargetOf(_settings.Current)} -");
            _engine.OnProbe = token => Task.Delay(Timeout.Infinite, token);

            await StartSupervisorAsync(paths: paths);

            await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launchId),
                "the exit report, which needs neither Docker nor a registered launcher");
        });

    [Test]
    public async Task AContainerThatExitsAfterAStopThatFailed_IsStillReportedGone()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var launch = await LaunchAsync();

        // Offloadr does not release it (granted, it would know already, and need no report).
        _link.ReleaseAnswer = _ => new ReleaseResult(false, true, "A session is assigned.");
        _engine.NextStopFailure = new ContainerEngineException("docker stop timed out.");

        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Faulted);
        Assert.That(_engine.Exists(Name), Is.True, "Still there, and still this app's.");

        // It exits later, by itself or stopped by hand.
        _engine.Exit(Name, 137);

        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the exit report");
        await WaitUntilAsync(() => !_engine.Exists(Name), "the stopped container to be removed");
    }

    [Test]
    public async Task ALaunchWithdrawnForANewKey_WhoseContainerRanAndExited_IsReportedGone_NotFailed()
    {
        const string newKey = "another-runner-key-0123456789abcdefghijklmnop";
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = async (spec, _) =>
        {
            // The key changes while `docker run` is under way; the container it started exits, and the CLI fails.
            _engine.RunOverride = null;
            await _settings.SaveAsync(_settings.Current with { RunnerKey = newKey });
            await Task.Delay(200);
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment);
            throw new ContainerEngineException("Could not start the runner container.", "context deadline exceeded");
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        // (Its exit report goes out as the launcher that ran for the old key, which this fake does not model.)
        await WaitUntilAsync(() => !_engine.Exists(Name), "the exited container to be removed");
        await Task.Delay(300);
        Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "It ran and exited: told as an exit alone, never FAILED for the new settings.");
    }

    [Test]
    public async Task AQuit_StillReportsAContainerThatExitedAMomentBefore()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var launch = await LaunchAsync();
        var reporting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _link.ExitReportAnswer = _ =>
        {
            // Offloadr is slow to answer; the person quits meanwhile.
            reporting.TrySetResult();
            answer.Task.Wait(TimeSpan.FromSeconds(1));
            return new ReleaseResult(true, true, string.Empty);
        };
        _engine.Exit(Name, 1);
        await reporting.Task.WaitAsync(WaitLimit);

        await supervisor.StopAsync(CancellationToken.None);
        _supervisor = null;
        supervisor.Dispose();

        Assert.That(_link.ExitReports.Count(report => report.LaunchId == launch.LaunchId), Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task TheLaunchersFirstHeartbeat_OffersOnlyTheGpuChosen()
    {
        const string second = "GPU-bbbbbbbb-1111-2222-3333-444444444444";
        _hostGpus.Report = new HostGpuReport(
            [
                new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid),
                new HostGpu("NVIDIA GeForce RTX 3090", 24576, second),
            ],
            null);
        _engine.GpuCheck = new GpuAccessCheck(true, $"GPU 0: NVIDIA GeForce RTX 4090 (UUID: {FakeContainerEngine.GpuUuid})\nGPU 1: NVIDIA GeForce RTX 3090 (UUID: {second})");
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, GpuUuid = second });
        LauncherProfile? first = null;
        _link.BeforeNextOnline = () => first = _link.Profile!();
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitUntilAsync(() => first is not null, "the launcher's first heartbeat");

        Assert.That(first!.Resources.GpuModelName, Is.EqualTo("NVIDIA GeForce RTX 3090"), "Never the offer of before the check: Offloadr would allocate on a GPU this runner will not use.");
    }

    [Test]
    public Task ALaunchRecordedBeforeACrash_WhoseContainerShowsAfterTheRestart_IsRemoved_AndNotReported()
        => WithDataFolderAsync(async paths =>
        {
            // The app closed after asking for the container; the engine creates it after the restart.
            var launchId = Guid.NewGuid().ToString();
            var engine = ContainerEngineSelection.Describe(new ContainerEngineSelection.Selected(null, null));
            File.WriteAllText(paths.LaunchedContainers, $"starting {launchId} {FakeLauncherLink.TargetOf(_settings.Current)} {engine}");

            await StartSupervisorAsync(paths: paths);
            await Task.Delay(500);
            Assert.That(File.Exists(paths.LaunchedContainers), Is.True, "Not there yet is no proof it never will be.");
            AddLaunchedContainer(launchId);
            _engine.MarkNeverStarted(Name);

            await WaitUntilAsync(() => !_engine.Exists(Name), "the late container to be removed");
            await WaitUntilAsync(() => !File.Exists(paths.LaunchedContainers), "its record to go");
            Assert.That(_link.ExitReports, Is.Empty, "Created and never started: it ran nothing.");
        });

    [Test]
    public async Task AStopPressedAsDockerRunIsSpawned_StartsNoContainer()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();

        // Pressed after the last check on the loop, as the engine's process is about to start.
        _engine.BeforeRun = supervisor.RequestStop;

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitForStateAsync(SupervisorState.Stopped);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty, "The Stop reached the spawn itself.");
            Assert.That(_engine.Exists(Name), Is.False);
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null);
        });
    }

    [Test]
    public async Task ALaunchRecordedBeforeACrash_WhoseContainerWasNeverCreated_IsNotReportedGone()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var engine = ContainerEngineSelection.Describe(new ContainerEngineSelection.Selected(null, null));

            // The app closed after noting the launch and before the engine created its container.
            File.WriteAllText(paths.LaunchedContainers, $"starting {Guid.NewGuid()} {FakeLauncherLink.TargetOf(_settings.Current)} {engine}");

            await StartSupervisorAsync(paths: paths);

            await WaitUntilAsync(() => !File.Exists(paths.LaunchedContainers), "the record to go");
            await Task.Delay(200);
            Assert.That(_link.ExitReports, Is.Empty, "Nothing ran: an exit report would cost the allocation a retry.");
        }
        finally
        {
            if (_supervisor is not null)
            {
                await _supervisor.StopAsync(CancellationToken.None);
                _supervisor.Dispose();
                _supervisor = null;
            }

            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task ACreationGivenUp_FailsTheLaunchAtOnce_AndRemovesTheContainerTheEngineCreatesLater()
    {
        await StartSupervisorAsync(runTimeout: TimeSpan.FromMilliseconds(300));
        await MakeAvailableAsync();
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnCreate = async (spec, token) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                // The daemon goes on, and creates the container well after it was given up.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
                    created.TrySetResult();
                });
                throw;
            }
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        // Nothing unseen can run (only this app starts what it created): the failure needs no waiting.
        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId)?.Phase == RunnerLaunchPhase.Failed, "the launch to fail");
        Assert.That(created.Task.IsCompleted, Is.False, "Failed before the engine made anything.");
        _link.Withdraw();

        await created.Task.WaitAsync(WaitLimit);
        await WaitUntilAsync(() => !_engine.Exists(Name), "the late container to be removed");
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(_link.ExitReports, Is.Empty, "It never started: no exit to report.");
        });
    }

    [Test]
    public async Task AReplacementWithdrawnAfterTheWarmContainerExitedDuringItsRelease_LetsTheNextLaunchStart()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        var warm = await LaunchAsync();
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _link.OnRelease = async token =>
        {
            asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        var replacement = _link.Name("forge-neo:neo", ForgeNeo);
        await asked.Task.WaitAsync(WaitLimit);

        // The warm container exits while its release is asked, and the stream is down: the replacement
        // waits to start. Offloadr then withdraws it.
        _link.StartsAllowed = false;
        _engine.Exit(Name, 1);
        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == warm.LaunchId), "the exit to be recorded");
        _link.OnRelease = null;
        _link.Withdraw();
        _link.StartsAllowed = true;
        await WaitForStateAsync(SupervisorState.Available);
        await WaitUntilAsync(() => _supervisor!.View.IsReady && _link.Running, "the launcher to be ready");

        // Nothing is stuck on the withdrawn launch: the next one starts.
        await LaunchAsync();
        Assert.That(_link.OutcomeOf(replacement.LaunchId)?.Phase, Is.Not.EqualTo(RunnerLaunchPhase.Started));
    }

    [Test]
    public async Task AStopPressedAsTheContainerIsAboutToStart_StartsNone()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.OnNextTryStart = supervisor.RequestStop;

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitForStateAsync(SupervisorState.Stopped);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty, "No executor starts, or registers, after the Stop.");
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "A local stop is no launch outcome.");
        });
    }

    [Test]
    public async Task AContainerThatExitsAsItsLaunchFinishes_IsNotShownRunning()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.OnRun = _ =>
        {
            // It exits right after `docker run` returned, as the app first looks at it.
            _engine.OnRun = null;
            _engine.OnNextInspect = () => _engine.Exit(Name, 1);
            return Task.CompletedTask;
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the exit report");
        await WaitForStateAsync(SupervisorState.Available);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Available), "Nothing runs to watch.");
            Assert.That(_engine.Exists(Name), Is.False);
        });
    }

    [Test]
    public async Task AContainerCreatedButNeverStartedBeforeACrash_IsRemoved_AndNotReportedGone()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var launchId = Guid.NewGuid().ToString();
            AddLaunchedContainer(launchId);
            _engine.MarkNeverStarted(Name);
            File.WriteAllText(paths.LaunchedContainers, $"starting {launchId} {FakeLauncherLink.TargetOf(_settings.Current)} -");

            await StartSupervisorAsync(paths: paths);

            await WaitUntilAsync(() => !_engine.Exists(Name), "the created container to be removed");
            await WaitUntilAsync(() => !File.Exists(paths.LaunchedContainers), "its record to go");
            await Task.Delay(200);
            Assert.That(_link.ExitReports, Is.Empty, "It ran nothing: an exit report would cost the allocation a retry.");
        }
        finally
        {
            if (_supervisor is not null)
            {
                await _supervisor.StopAsync(CancellationToken.None);
                _supervisor.Dispose();
                _supervisor = null;
            }

            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public Task AContainerWhoseStartWasAskedBeforeACrash_IsReportedGone_ThoughItIsSeenCreated()
        => WithDataFolderAsync(async paths =>
        {
            // The app closed after it asked for the start: the engine may still have started the
            // container, and one that started and exited before it is removed here leaves nothing to say so.
            var launchId = Guid.NewGuid().ToString();
            AddLaunchedContainer(launchId);
            _engine.MarkNeverStarted(Name);
            File.WriteAllText(paths.LaunchedContainers, $"running {launchId} {FakeLauncherLink.TargetOf(_settings.Current)} -");

            await StartSupervisorAsync(paths: paths);

            await WaitUntilAsync(() => !_engine.Exists(Name), "the created container to be removed");
            await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launchId), "the container to be told as gone");
        });

    [Test]
    public async Task AnUnrelatedSettingsSave_KeepsTheCheckedDockerContext()
    {
        _engine.CurrentContext = "desktop-linux";
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.CurrentContext = "remote-builder";

        await _settings.SaveAsync(_settings.Current with { ComfyUiArguments = "--lowvram" });
        await Task.Delay(200);
        string? contextAtRun = null;
        _engine.BeforeRun = () => contextAtRun = _selection.Context;
        await LaunchAsync();

        Assert.That(contextAtRun, Is.EqualTo("desktop-linux"));
    }

    [Test]
    public async Task ALaunchFailingOnSettings_TellsOffloadrNoLocalPath()
    {
        var models = Path.Combine(Path.GetTempPath(), "offloadr-models-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(models);
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, LocalModelsDirectory = models });
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        Directory.Delete(models);

        var launch = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.That(_link.OutcomeOf(launch.LaunchId)?.Message, Does.Not.Contain(models).And.Contain("settings"));
    }

    [Test]
    public async Task AModelsFolderGoneAtALaunch_StopsOfferingTheComputer()
    {
        var models = Path.Combine(Path.GetTempPath(), "offloadr-models-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(models);
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, LocalModelsDirectory = models });
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        Directory.Delete(models);

        var launch = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitForStateAsync(SupervisorState.Faulted);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(supervisor.View.Fault?.Kind, Is.EqualTo(FaultKind.Settings));
            Assert.That(_link.Running, Is.False, "No settings change says so; the launch found it.");
        });
    }

    [Test]
    public async Task DockerGoingAwayDuringALaunchDownload_StopsOffering_UntilItIsBack()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.OnPull = _ =>
        {
            _engine.OnPull = null;
            _engine.InspectImageFailure = new ContainerEngineException("Docker is not running.", null, EngineOperation.Command, FaultKind.EngineNotRunning);
            return Task.CompletedTask;
        };

        var launch = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null && !_link.Running, "the failure and the pause");
        Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.WaitingToRetry));
        _engine.InspectImageFailure = null;

        await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Available && _link.Running, "availability to come back");
    }

    [Test]
    public async Task AStartGivenUp_WhileTheDaemonStartedTheContainer_KeepsIt()
    {
        await StartSupervisorAsync(runTimeout: TimeSpan.FromMilliseconds(200));
        await MakeAvailableAsync();

        // The daemon starts the container; the CLI never returns, and is given up.
        _engine.OnRun = token => Task.Delay(Timeout.Infinite, token);

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        await WaitForStateAsync(SupervisorState.Running);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started), "It is known by its ID, and it runs: it may already serve.");
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task ALaunch_WhenThePullFailsWithALocalImage_RunsTheLocalCopy()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.PullFails = true;

        await LaunchAsync();

        Assert.That(supervisor.LogLines, Has.Some.Contains("Using the copy already on this machine"));
    }

    [Test]
    public async Task ALaunch_WhenDockerCannotAttachTheGpu_ReportsFailed_AndCleansUp()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.NextRunFailure = new ContainerEngineException(
            "Could not start the runner container from ghcr.io/offloadr/runner-comfyui:latest.",
            "docker: Error response from daemon: could not select device driver \"\" with capabilities: [[gpu]].",
            EngineOperation.Run);

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        await WaitForStateAsync(SupervisorState.Available);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(supervisor.View.LaunchFailure?.Kind, Is.EqualTo(FaultKind.GpuUnavailable));
            Assert.That(_engine.Exists(Name), Is.False, "The failed launch is cleaned up.");
        });
        await WaitUntilAsync(() => _engine.GpuChecks == 2 && supervisor.View.State == SupervisorState.Available, "the GPU check again before it is offered");
        Assert.Multiple(() =>
        {
            Assert.That(_link.Running, Is.True);
        });
    }

    [Test]
    public async Task AStartTheEngineRefused_WhoseContainerSomeoneThenStarted_IsToldAsGone_OnceItExited()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.NextRunFailure = new ContainerEngineException(
            "Could not start the runner container from ghcr.io/offloadr/runner-comfyui:latest.",
            "docker: Error response from daemon: could not select device driver \"\" with capabilities: [[gpu]].",
            EngineOperation.Run);

        // The engine refused the start. Someone starts the container all the same: the engine refuses its
        // removal because it runs, and shows it exited when it is looked at next.
        _engine.BeforeNextRemoveStopped = () =>
        {
            _engine.Restart(Name);
            _engine.OnNextInspect = () => _engine.Exit(Name, 1);
        };
        _link.ExitReportAnswer = _ =>
        {
            _link.Withdraw();
            return new ReleaseResult(true, true, string.Empty);
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the container to be told as gone");
        await WaitUntilAsync(() => !_engine.Exists(Name), "the container to be removed");
        await Task.Delay(200);
        Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "It ran: its exit is told, and it is no launch that failed.");
    }

    [Test]
    public async Task AWakeFromSleepRightBeforeTheContainerIsStarted_HoldsTheStartBack_UntilTheGpuIsProvedAgain()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var checks = _engine.GpuChecks;

        // The computer wakes after the launch's last checks, right before the engine's process is
        // started, and containers there lost the GPU in that sleep.
        _engine.BeforeRun = () =>
        {
            _engine.GpuCheck = new GpuAccessCheck(false, "NVIDIA driver lost after sleep");
            supervisor.NotifyResumed();
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        await WaitUntilAsync(() => !_engine.Exists(Name), "what was created to be removed");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed), "Not started on a GPU check from before the sleep.");
            Assert.That(_engine.Runs, Is.Empty, "The engine was never asked to start it.");
            Assert.That(_engine.GpuChecks, Is.GreaterThan(checks), "The GPU is proved again first.");
            Assert.That(_link.ExitReports, Is.Empty, "It never ran: nothing is told as gone.");
        });
    }

    [Test]
    public async Task AWakeFromSleepRightBeforeTheContainerIsStarted_StartsIt_OnceTheGpuIsProvedAgain()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var checks = _engine.GpuChecks;
        _engine.BeforeRun = supervisor.NotifyResumed;
        var checksAtStart = -1;
        _engine.OnRun = _ =>
        {
            checksAtStart = _engine.GpuChecks;
            return Task.CompletedTask;
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started));
            Assert.That(_engine.Runs, Has.Count.EqualTo(1), "Started once, after the check.");
            Assert.That(checksAtStart, Is.GreaterThan(checks), "The GPU is proved again before the engine is asked to start it.");
            Assert.That(_link.ExitReports, Is.Empty);
        });
    }

    [Test]
    public async Task AFailedLaunch_OnAContainerSomeoneElseCreated_LeavesItAlone()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.NextRunConflict = true;

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        var other = await _engine.InspectContainerAsync(Name, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(other?.Image, Is.EqualTo("someone-elses-image"));
            Assert.That(other?.Running, Is.True);
            Assert.That(_engine.Stops, Is.Zero);
        });
    }

    [Test]
    public async Task AFailedLaunch_RemovesTheCreatedContainerAndForgetsTheEngine()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
            _selection = new ContainerEngineSelection(configured => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker), statePath);
            await StartSupervisorAsync();
            await MakeAvailableAsync();
            _engine.NextRunFailure = new ContainerEngineException("Could not start the runner container.", "could not select device driver \"nvidia\"");

            var launch = _link.Name("comfyui-latest", ComfyUi);

            await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
            Assert.Multiple(() =>
            {
                Assert.That(_engine.Exists(Name), Is.False);
                Assert.That(File.Exists(statePath), Is.False);
            });
        }
        finally
        {
            DeleteQuietly(statePath);
        }
    }

    [Test]
    public async Task ALaunchWhoseDockerRunFails_ButWhoseContainerRuns_KeepsIt()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.RunOverride = (spec, _) =>
        {
            // The daemon started it; the CLI lost the answer.
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: true, spec.Labels, spec.Environment);
            throw new ContainerEngineException("error during connect: the connection was reset.");
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitForStateAsync(SupervisorState.Running);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started));
            Assert.That(_engine.Exists(Name), Is.True, "It may already serve: it is never removed unreleased.");
            Assert.That(supervisor.LogLines, Has.Some.Contains("started all the same"));
        });
    }

    [Test]
    public async Task ALaunchWithdrawnWhileDockerRunFails_ReportsTheWithdrawal()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.RunOverride = async (_, _) =>
        {
            _engine.RunOverride = null;
            running.TrySetResult();
            await fail.Task;
            throw new ContainerEngineException("Could not start the runner container.", "daemon error");
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);
        await running.Task.WaitAsync(WaitLimit);
        _link.Withdraw();
        await Task.Delay(50);
        fail.SetResult();

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Cancelled));
    }

    [Test]
    public async Task ADockerRunThatHangs_IsGivenUp()
    {
        var supervisor = await StartSupervisorAsync(runTimeout: TimeSpan.FromMilliseconds(300));
        await MakeAvailableAsync();
        _engine.RunOverride = async (spec, token) =>
        {
            // Created, and its start never returns: it stays as it was made, never started.
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
            await Task.Delay(Timeout.Infinite, token);
            return string.Empty;
        };

        _link.ExitReportAnswer = _ =>
        {
            _link.Withdraw();
            return new ReleaseResult(true, true, string.Empty);
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        // Given up, the engine never said it refused: it may still start the container, and one that
        // started and exited before it was removed would leave nothing to say so. Told as gone, which
        // lets the allocation launch again; never as a launch that failed.
        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the container to be told as gone");
        await WaitForStateAsync(SupervisorState.Available);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null);
            Assert.That(supervisor.LogLines, Has.Some.Contains("did not start the runner container within"));
            Assert.That(_engine.Exists(Name), Is.False, "Removed.");
        });
    }

    [Test]
    public async Task AStartGivenUp_WhoseContainerStaysCreatedAndIsKept_IsNotToldGone_WhileTheEngineMayStartItYet()
    {
        var supervisor = await StartSupervisorAsync(runTimeout: TimeSpan.FromMilliseconds(300));
        await MakeAvailableAsync();
        _engine.RunOverride = async (spec, token) =>
        {
            // Created, and its start never returns: it stays as it was made, never started.
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
            await Task.Delay(Timeout.Infinite, token);
            return string.Empty;
        };

        // And the engine will not remove it.
        _engine.RemoveStoppedFailure = new ContainerEngineException("Could not remove container offloadr-runner.");

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("would not remove it", StringComparison.Ordinal)), "the start to be given up");
        await Task.Delay(500);
        Assert.Multiple(() =>
        {
            Assert.That(_link.ExitReports, Is.Empty, "Still only created, the engine may start it yet: told gone, the allocation would launch again beside it.");
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "Nor is it a launch that failed.");
            Assert.That(_engine.Exists(Name), Is.True);
        });

        // The engine completes the start after all.
        _engine.Restart(Name);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started));
            Assert.That(_link.ExitReports, Is.Empty);
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running), "Supervised all along.");
        });
    }

    [Test]
    public async Task AStartGivenUp_WhoseContainerStaysCreatedAndIsKept_IsToldGone_OnceItIsRemoved()
    {
        var supervisor = await StartSupervisorAsync(runTimeout: TimeSpan.FromMilliseconds(300));
        await MakeAvailableAsync();
        _engine.RunOverride = async (spec, token) =>
        {
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
            await Task.Delay(Timeout.Infinite, token);
            return string.Empty;
        };
        _engine.RemoveStoppedFailure = new ContainerEngineException("Could not remove container offloadr-runner.");
        _link.ExitReportAnswer = _ =>
        {
            _link.Withdraw();
            return new ReleaseResult(true, true, string.Empty);
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("would not remove it", StringComparison.Ordinal)), "the start to be given up");
        await Task.Delay(300);
        Assert.That(_link.ExitReports, Is.Empty);

        // The engine lets go of it: removed, it can no longer be started, and is told as gone.
        _engine.RemoveStoppedFailure = null;

        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the container to be told as gone");
        await WaitForStateAsync(SupervisorState.Available);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "Its start was never refused: it may have run, and is no launch that failed.");
            Assert.That(_engine.Exists(Name), Is.False);
        });
    }

    [Test]
    public async Task AStop_KeepsSupervisingAContainerStillOnlyCreated_WhoseStartWasAskedAndThatTheEngineKeeps()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).");
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.RunOverride = async (spec, token) =>
        {
            // The start is asked for and never answered: the container is still only created.
            _engine.RunOverride = null;
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
            starting.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return string.Empty;
        };

        _link.Name("comfyui-latest", ComfyUi);
        await starting.Task.WaitAsync(WaitLimit);

        // Stop: the engine will not remove it, and it is still only created. It may start it yet.
        _engine.RemoveStoppedFailure = new ContainerEngineException("Could not remove container offloadr-runner.");
        _engine.NextRemoveFailure = new ContainerEngineException("Could not remove container offloadr-runner.");
        supervisor.RequestStop();

        await WaitUntilAsync(() => supervisor.View.State is SupervisorState.Faulted or SupervisorState.Stopped, "the Stop to end");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Faulted), "It may start yet: not let go of as stopped.");
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(_link.ExitReports, Is.Empty, "Nothing is told as gone while it may come to run.");
        });

        // The next Stop removes it.
        _engine.RemoveStoppedFailure = null;
        supervisor.RequestStop();
        await WaitUntilAsync(() => !_engine.Exists(Name), "the container to be removed");
    }

    [Test]
    public Task AContainerWhoseStartWasAskedBeforeACrash_IsNotReportedGone_WhileItIsStillCreatedAndItsEngineKeepsIt()
        => WithDataFolderAsync(async paths =>
        {
            // The app closed after it asked for the start. The container is found (renamed, by its launch
            // alone) still only created, and its engine will not remove it: it may start it yet.
            var launchId = Guid.NewGuid().ToString();
            AddLaunchedContainer(launchId);
            _engine.MarkNeverStarted(Name);
            _engine.Rename(Name, "renamed-runner");
            var engine = ContainerEngineSelection.Describe(new ContainerEngineSelection.Selected(null, null));
            File.WriteAllText(paths.LaunchedContainers, $"running {launchId} {FakeLauncherLink.TargetOf(_settings.Current)} {engine}");
            _engine.RemoveStoppedFailure = new ContainerEngineException("Could not remove container renamed-runner.");

            var supervisor = await StartSupervisorAsync(paths: paths);
            supervisor.RequestStart();
            await WaitForStateAsync(SupervisorState.Faulted);
            await Task.Delay(600);
            Assert.Multiple(() =>
            {
                Assert.That(_link.Running, Is.False, "Not offered: a replacement could start beside a container the engine may still start.");
                Assert.That(_link.ExitReports, Is.Empty, "Told gone, the allocation would launch again beside a container that may come to run.");
                Assert.That(File.ReadAllText(paths.LaunchedContainers).Trim(), Does.StartWith($"running {launchId} "), "Its record stays as it is.");
                Assert.That(File.Exists(paths.LeftoverContainers), Is.False, "It is no leftover that only waits to be removed.");
            });

            // The engine lets go of it: removed, it is gone for good, and told so.
            _engine.RemoveStoppedFailure = null;
            await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launchId), "the container to be told as gone");
            Assert.That(_engine.Exists("renamed-runner"), Is.False);
            await MakeAvailableAsync();
        });

    [TestCase(true)]
    [TestCase(false)]
    public Task AContainerALookRemovesAsTheAppCloses_IsSettledByWhatWasSeenOfIt(bool byName)
        => WithDataFolderAsync(async paths =>
        {
            // The engine refused its start, before a crash. The look removes it, seen created, and the app
            // is closed before the look returns.
            var launchId = Guid.NewGuid().ToString();
            AddLaunchedContainer(launchId);
            _engine.MarkNeverStarted(Name);
            if (!byName)
            {
                _engine.Rename(Name, "renamed-runner");
            }

            var engine = ContainerEngineSelection.Describe(new ContainerEngineSelection.Selected(null, null));
            File.WriteAllText(paths.LaunchedContainers, $"refused {launchId} {FakeLauncherLink.TargetOf(_settings.Current)} {engine}");
            var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var say = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _engine.OnNextRemovedStopped = async () =>
            {
                removed.TrySetResult();
                await say.Task;
            };

            var supervisor = await StartSupervisorAsync(paths: paths);
            await removed.Task.WaitAsync(WaitLimit);
            var closing = supervisor.StopAsync(CancellationToken.None);
            await Task.Delay(200);
            say.TrySetResult();
            await closing;

            Assert.That(File.Exists(paths.LaunchedContainers), Is.False, "It ran nothing: the next start of the app finds nothing to say so, and must not take it for gone.");
        });

    [Test]
    public async Task AStartWhoseAnswerWasLost_IsToldAsGone_ThoughItsContainerWasSeenCreated()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.NextRunFailure = new ContainerEngineException("Could not start the runner container.", "error during connect: read tcp: connection reset by peer");
        _link.ExitReportAnswer = _ =>
        {
            _link.Withdraw();
            return new ReleaseResult(true, true, string.Empty);
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the container to be told as gone");
        await WaitForStateAsync(SupervisorState.Available);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "FAILED needs the engine's own refusal: without it the container may have run.");
            Assert.That(_engine.Exists(Name), Is.False);
            Assert.That(supervisor.View.LaunchFailure, Is.Not.Null, "The person still sees why.");
        });
    }

    [Test]
    public async Task TheComputerWakingRightBeforeTheStart_HasTheGpuCheckedFirst()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var before = _engine.GpuChecks;
        var atTheStart = 0;
        _engine.BeforeRun = () => atTheStart = _engine.GpuChecks;

        // Windows reports the resume after the last check, as the start is asked for.
        _link.OnNextTryStart = supervisor.NotifyResumed;
        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started));
            Assert.That(atTheStart, Is.GreaterThan(before), "A GPU lost in sleep must not cost an executor that cannot use it.");
        });
    }

    [Test]
    public async Task Start_WhenContainersCannotSeeTheChosenGpu_OffersNothing()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, GpuUuid = "GPU-bbbbbbbb-1111-2222-3333-444444444444" });
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Message, Does.Contain("GPU chosen in Settings"));
            Assert.That(_link.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task ALaunch_StartsOnlyWhileTheStreamIsLive()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.StartsAllowed = false;

        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => _link.PhasesOf(launch).Contains(RunnerLaunchPhase.Starting), "the download");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty, "The download may finish while the stream is down; the start waits.");
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Starting));
        });

        _link.StartsAllowed = true;

        await WaitForStateAsync(SupervisorState.Running);
        Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started));
    }

    [Test]
    public async Task ALaunchWithdrawnDuringItsDownload_StartsNothing_AndReportsCancelled()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var pulling = HoldNextPull();

        var launch = _link.Name("forge-neo:neo", ForgeNeo);
        await pulling.WaitAsync(WaitLimit);
        _link.Withdraw();

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        await WaitForStateAsync(SupervisorState.Available);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Cancelled));
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task ANewerLaunchDuringADownload_ReplacesIt()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        var pulling = HoldNextPull();

        var first = _link.Name("forge-neo:neo", ForgeNeo);
        await pulling.WaitAsync(WaitLimit);
        var second = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _engine.Runs.Count == 1 && _supervisor!.View.State == SupervisorState.Running, "the newer launch's container");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(first.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Superseded));
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started));
            Assert.That(_engine.Runs.Single().Labels[RunnerSupervisor.LaunchIdLabel], Is.EqualTo(second.LaunchId));
        });
    }

    [Test]
    public async Task ALaunchWithdrawnWhileItsContainerStarts_RemovesTheContainer()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        var finish = HoldNextRun(out var running);

        var launch = _link.Name("comfyui-latest", ComfyUi);
        await running.WaitAsync(WaitLimit);
        _link.Withdraw();
        await Task.Delay(100);
        Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "A withdrawal does not cut `docker run` off.");
        finish.SetResult();

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        await WaitForStateAsync(SupervisorState.Available);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Cancelled));
            Assert.That(_link.Releases.Single().LaunchId, Is.EqualTo(launch.LaunchId), "Stopped only once Offloadr released it.");
            Assert.That(_engine.Exists(Name), Is.False);
        });
    }

    [Test]
    public async Task SettingsChangedWhileALaunchWaitsForTheStream_FailTheLaunch()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.StartsAllowed = false;
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => _link.PhasesOf(launch).Contains(RunnerLaunchPhase.Starting), "the download");

        // Its container is built from the settings of the moment the download ended: only a change after
        // that, while it waits for the stream, makes it another container than the one asked for.
        await WaitUntilAsync(() => supervisor.View.Message.StartsWith("Starting ", StringComparison.Ordinal), "the launch to wait for the stream");
        await Task.Delay(100);

        await _settings.SaveAsync(_settings.Current with { ComfyUiArguments = "--lowvram" });
        await Task.Delay(100);
        _link.StartsAllowed = true;

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_engine.Runs, Is.Empty, "Its container would not have been the one Settings ask for.");
        });
    }

    [Test]
    public async Task ATakeoverWhileItsContainerStarts_KeepsTheContainer()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var finish = HoldNextRun(out var running);

        var launch = _link.Name("comfyui-latest", ComfyUi);
        await running.WaitAsync(WaitLimit);
        // The executor may have registered and matched the launch already: a takeover proves nothing.
        _link.End(LauncherConnection.Superseded, "Another Offloadr Runner app took over this runner.");
        finish.SetResult();

        await WaitForStateAsync(SupervisorState.Running);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(supervisor.View.Message, Does.Contain("keeps running until you stop it"));
        });
    }

    [Test]
    public async Task StopPressedWhileItsContainerStarts_ReportsNoStart()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var finish = HoldNextRun(out var running);
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await running.WaitAsync(WaitLimit);

        supervisor.RequestStop();
        finish.TrySetResult();

        await WaitForStateAsync(SupervisorState.Stopped);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null);
            Assert.That(_engine.Exists(Name), Is.False);
        });
    }

    [Test]
    public async Task ALaunchWhoseContainerAStopRemoved_IsNotStartedAgainWhenNamedAgain()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var finish = HoldNextRun(out var running);
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await running.WaitAsync(WaitLimit);
        supervisor.RequestStop();
        finish.TrySetResult();
        await WaitForStateAsync(SupervisorState.Stopped);

        // Its executor may have matched it: Offloadr still names it to this launcher.
        await MakeAvailableAsync();
        var again = _link.NameAgain(launch);

        await WaitUntilAsync(() => _link.OutcomeOf(again.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(again.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started),
                "Its container ran (and was released as it was stopped): that it started is the true answer, never that it failed.");
            Assert.That(_engine.Runs, Has.Count.EqualTo(1), "Never started twice with the same launch id.");
        });
    }

    [Test]
    public async Task AnEngineChangedDuringALaunchDownload_FailsTheLaunch_AndIsCheckedFirst()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.OnPull = async _ =>
        {
            _engine.OnPull = null;
            await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "podman" });
        };

        var launch = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _engine.GpuChecks == 2 && _link.Running, "the GPU check on the new engine");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Reports.Where(report => report.LaunchId == launch.LaunchId && report.IsTerminal), Is.Empty,
                "Left for Offloadr to retire once it accepts the new offer.");
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task ALaunchStillNamedAfterItsOutcome_IsNotTriedAgain()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.PullFails = true;
        var launch = _link.Name("forge-neo:neo", ForgeNeo);
        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");

        // The stream reconnects and names the same launch again.
        _link.SetStreamLive(false);
        _link.SetStreamLive(true);
        await Task.Delay(200);

        Assert.That(_link.PhasesOf(launch).Count(phase => phase == RunnerLaunchPhase.Preparing), Is.EqualTo(1));
    }

    [Test]
    public async Task ALaunchNamedAgainWhileItsContainerRuns_ChangesNothing()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

        _link.SetStreamLive(false);
        _link.SetStreamLive(true);
        await Task.Delay(200);

        Assert.That(_engine.Runs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Stop_DuringALaunchDownload_StartsNothing_AndReportsNothing()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var pulling = HoldNextPull();
        var launch = _link.Name("forge-neo:neo", ForgeNeo);
        await pulling.WaitAsync(WaitLimit);

        supervisor.RequestStop();

        await WaitForStateAsync(SupervisorState.Stopped);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "A Stop is no outcome Offloadr asked for.");
            Assert.That(_link.Running, Is.False);
        });
    }

    [Test]
    public async Task ANewKeyDuringALaunchDownload_FailsTheLaunchItCouldNoLongerServe()
    {
        const string newKey = "another-runner-key-0123456789abcdefghijklmnop";
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        var pulling = HoldNextPull();
        var launch = _link.Name("forge-neo:neo", ForgeNeo);
        await pulling.WaitAsync(WaitLimit);

        await _settings.SaveAsync(_settings.Current with { RunnerKey = newKey });

        await WaitUntilAsync(() => _link.Runs.Count == 2 && _link.Running, "the launcher's run with the new key");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty, "Its container would register with another key than the launch's.");
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed), "Told to the run that asked, so Offloadr need not wait for a timeout.");
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Message, Does.Contain("changed"));
        });
    }

    [Test]
    public async Task Stop_StopsAndRemovesTheContainer()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var launch = await LaunchAsync();

        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Stops, Is.EqualTo(1));
            Assert.That(_engine.Exists(Name), Is.False);
            Assert.That(_link.Releases.Single().LaunchId, Is.EqualTo(launch.LaunchId), "Asked first, so nothing more is assigned to it while it stops.");
        });
    }

    [Test]
    public async Task Stop_DuringACreation_RemovesTheContainerTheEngineCreatesAfterTheCancel()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var creating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnCreate = async (spec, cancellationToken) =>
        {
            creating.SetResult();
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
                    _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
                });
                throw;
            }
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);
        await creating.Task.WaitAsync(WaitLimit);
        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);
        await Task.Delay(300);

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.False, "It holds the runner key.");
            Assert.That(_engine.Runs, Is.Empty, "Created, never started: it ran nothing.");
            Assert.That(_link.ExitReports, Is.Empty, "So there is no exit to report.");
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null);
        });
    }

    [Test]
    public async Task Shutdown_DuringALaunch_RemovesTheContainerItCreated_AndReportsNothing()
    {
        var launching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.OnRun = async token =>
        {
            launching.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await launching.Task.WaitAsync(WaitLimit);

        await supervisor.StopAsync(CancellationToken.None);
        _supervisor = null;
        supervisor.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.False);
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "Host shutdown is never an outcome.");
        });
    }

    // Warm containers and releases

    [Test]
    public async Task AnIdleContainer_IsReleasedAndStoppedAfterTheWarmTime()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromMilliseconds(300));
        await MakeAvailableAsync();
        var launch = await LaunchAsync();

        await WaitForStateAsync(SupervisorState.Available);

        var release = _link.Releases.Single();
        Assert.Multiple(() =>
        {
            Assert.That(release.LaunchId, Is.EqualTo(launch.LaunchId));
            Assert.That(release.ExecutorInstanceId, Is.EqualTo("instance"));
            Assert.That(release.Target, Is.EqualTo(FakeLauncherLink.TargetOf(_settings.Current)));
            Assert.That(_engine.Exists(Name), Is.False);
            Assert.That(supervisor.LogLines, Has.Some.Contains("idle for the warm time"));
        });
    }

    [Test]
    public async Task AContainerServingASession_IsNotReleased()
    {
        _engine.StatusJson = Status(activeSessionId: "session-1");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromMilliseconds(100));
        await MakeAvailableAsync();
        await LaunchAsync();

        await WaitUntilAsync(() => supervisor.View.HasActiveSession);
        await Task.Delay(400);

        Assert.Multiple(() =>
        {
            Assert.That(_link.Releases, Is.Empty);
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(supervisor.View.StateTitle, Is.EqualTo("Session running"));
        });
    }

    [Test]
    public async Task AStatusTheAgentStoppedWriting_DoesNotKeepTheContainerWarm()
    {
        // The agent's last word names a session, and it writes nothing after (its disk full, say).
        _engine.StatusJson = Status(activeSessionId: "session-1", updatedUtc: "2026-01-01T00:00:00Z");
        await StartSupervisorAsync(warmTime: TimeSpan.FromMilliseconds(100), statusStaleAfter: TimeSpan.FromMilliseconds(300));
        await MakeAvailableAsync();
        await LaunchAsync();

        await WaitForStateAsync(SupervisorState.Available);
        Assert.That(_link.Releases, Has.Count.EqualTo(1), "Offloadr's release decides whether a session still runs.");
    }

    [Test]
    public async Task AReleaseOffloadrRefuses_KeepsTheContainer()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        _link.ReleaseAnswer = _ => new ReleaseResult(false, true, "A session is starting.");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromMilliseconds(100));
        await MakeAvailableAsync();
        await LaunchAsync();

        await WaitUntilAsync(() => !_link.Releases.IsEmpty);
        await Task.Delay(200);

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));
            Assert.That(supervisor.LogLines, Has.Some.Contains("keeps the runner container"));
            Assert.That(supervisor.LogLines, Has.None.Contains("A session is starting"), "Offloadr's own words stay out of the log.");
        });
    }

    [Test]
    public async Task AReleaseThatCannotReachOffloadr_KeepsTheContainer_AndAsksAgain()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        _link.ReleaseAnswer = _ => new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).");
        await StartSupervisorAsync(warmTime: TimeSpan.FromMilliseconds(100));
        await MakeAvailableAsync();
        await LaunchAsync();

        await WaitUntilAsync(() => _link.Releases.Count >= 2, "a second release");

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(_engine.Stops, Is.Zero);
        });
    }

    [Test]
    public async Task ChangedSettings_ReleaseTheIdleContainerAtOnce_AndApplyToTheNextLaunch()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null);

        await _settings.SaveAsync(_settings.Current with { ComfyUiArguments = "--lowvram" });

        await WaitForStateAsync(SupervisorState.Available);
        Assert.That(supervisor.LogLines, Has.Some.Contains("uses earlier settings"));
        await LaunchAsync();
        Assert.That(_engine.Runs[1].Environment["RUNNER_EDITOR_ARGS_COMFYUI"], Is.EqualTo("--lowvram"));
    }

    [Test]
    public async Task ChangedSettings_DuringASession_WaitUntilItEnds()
    {
        _engine.StatusJson = Status(activeSessionId: "session-1");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        await WaitUntilAsync(() => supervisor.View.HasActiveSession);

        await _settings.SaveAsync(_settings.Current with { ComfyUiArguments = "--lowvram" });
        await Task.Delay(300);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Releases, Is.Empty);
            Assert.That(_engine.Exists(Name), Is.True);
        });

        _engine.StatusJson = Status(activeSessionId: "");

        await WaitForStateAsync(SupervisorState.Available);
        Assert.That(_link.Releases, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ALaunchOfAnotherEditor_ReleasesAndStopsTheWarmContainerFirst()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        var first = await LaunchAsync();

        var second = await LaunchAsync("forge-neo:neo", ForgeNeo);

        Assert.Multiple(() =>
        {
            Assert.That(_link.Releases.Single().LaunchId, Is.EqualTo(first.LaunchId));
            Assert.That(_engine.Stops, Is.EqualTo(1));
            Assert.That(_engine.Runs.Select(run => run.Labels[RunnerSupervisor.ImageLabel]), Is.EqualTo(new[] { ComfyUi, ForgeNeo }));
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started));
            Assert.That(supervisor.View.ActiveTemplate?.EditorTemplate, Is.EqualTo("forge-neo:neo"));
        });
    }

    [Test]
    public async Task AGrantedRelease_StillStopsTheOldContainer_WhenTheNewLaunchIsWithdrawn()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        _engine.StopDuration = TimeSpan.FromMilliseconds(300);
        _engine.BeforeStop = () => _link.Withdraw();

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        await WaitForStateAsync(SupervisorState.Available);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Cancelled));
            Assert.That(_engine.Exists(Name), Is.False, "Released for good: it is stopped, not left half-way.");
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task AReplacementWhoseImageCannotBeDownloaded_KeepsTheWarmContainer()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        _engine.PullFails = true;

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.Releases, Is.Empty, "Not released for a launch that could not start anyway.");
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task AGpuChangedDuringALaunchDownload_CancelsItAtOnce()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        var pulling = HoldNextPull();
        var launch = _link.Name("forge-neo:neo", ForgeNeo);
        await pulling.WaitAsync(WaitLimit);

        await _settings.SaveAsync(_settings.Current with { GpuUuid = FakeContainerEngine.GpuUuid });

        // Dropped at once, without an outcome: Offloadr retires it once it accepts the new offer.
        await WaitUntilAsync(() => _engine.GpuChecks == 2 && _link.Running, "the new GPU checked and offered");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Reports.Where(report => report.LaunchId == launch.LaunchId && report.IsTerminal), Is.Empty,
                "A FAILED before the new offer is accepted would fail the allocation.");
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task AGrantedRelease_WhoseStopFailed_IsStoppedAgainWithoutAskingAgain()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromMilliseconds(200));
        await MakeAvailableAsync();
        await LaunchAsync();
        _engine.NextStopFailure = new ContainerEngineException("docker stop timed out.");

        await WaitForStateAsync(SupervisorState.Available);

        Assert.Multiple(() =>
        {
            Assert.That(_link.Releases, Has.Count.EqualTo(1), "A grant is for good.");
            Assert.That(_engine.Exists(Name), Is.False);
        });
    }

    [Test]
    public async Task AGpuChosenWhileAContainerRuns_IsCheckedBeforeItIsOffered()
    {
        _engine.StatusJson = Status(activeSessionId: "session-1");
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

        await _settings.SaveAsync(_settings.Current with { GpuUuid = "GPU-bbbbbbbb-1111-2222-3333-444444444444" });

        await WaitUntilAsync(OffersNothing, "the launcher to stop offering a GPU containers cannot use");
        Assert.Multiple(() =>
        {
            Assert.That(_link.Running, Is.True, "Still registered, so the container can be released once idle.");
            Assert.That(_engine.Exists(Name), Is.True, "The running container keeps serving.");
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(_link.Releases, Is.Empty);
        });
    }

    [Test]
    public async Task AWarmContainerThatExitsWhileItsReleaseIsRetried_MakesRoom()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).");

        var second = _link.Name("forge-neo:neo", ForgeNeo);
        await WaitUntilAsync(() => !_link.Releases.IsEmpty, "a release");
        _engine.Exit(Name, 0);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId)?.Phase == RunnerLaunchPhase.Started, "the second launch to start");
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs.Select(run => run.Labels[RunnerSupervisor.ImageLabel]), Is.EqualTo(new[] { ComfyUi, ForgeNeo }));
            Assert.That(_supervisor!.View.UnexpectedExits, Is.EqualTo(1), "Recorded as the monitor would.");
            Assert.That(_supervisor.LogLines, Has.Some.Contains("exit code 0"));
        });
    }

    [Test]
    public async Task AWarmContainerStartedAgainWhileItsReleaseIsRetried_IsReleasedNotRemovedByForce()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        var asked = 0;
        _link.OnRelease = _ =>
        {
            if (Interlocked.Increment(ref asked) == 1)
            {
                // Seen stopped next, but started again before it is removed.
                _engine.Exit(Name, 0);
                _engine.BeforeNextRemoveStopped = () => _engine.Restart(Name);
            }

            return Task.CompletedTask;
        };
        _link.ReleaseAnswer = _ => Volatile.Read(ref asked) == 1
            ? new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).")
            : new ReleaseResult(true, true, string.Empty);

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId)?.Phase == RunnerLaunchPhase.Started, "the second launch to start");
        Assert.Multiple(() =>
        {
            Assert.That(_link.Releases, Has.Count.EqualTo(2), "Running again: stopped only once Offloadr released it.");
            Assert.That(_engine.Stops, Is.EqualTo(1));
            Assert.That(_supervisor!.View.UnexpectedExits, Is.Zero, "It did not exit: it runs again.");
            Assert.That(_engine.Runs.Select(run => run.Labels[RunnerSupervisor.ImageLabel]), Is.EqualTo(new[] { ComfyUi, ForgeNeo }));
        });
    }

    [Test]
    public async Task AContainerStartedAgainAfterTheMonitorSawItStopped_IsNotRemovedByForce()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        var removing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.BeforeNextRemoveStopped = () =>
        {
            _engine.Restart(Name);
            removing.TrySetResult();
        };

        _engine.Exit(Name, 0);
        await removing.Task.WaitAsync(WaitLimit);
        await Task.Delay(200);

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.True, "It runs again: kept, and still supervised.");
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));
            Assert.That(supervisor.View.UnexpectedExits, Is.Zero);
        });
    }

    [Test]
    public async Task AReleaseRetriedWhileTheStreamIsDown_IsNotAskedAgainUntilItIsBack()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        var asked = 0;
        _link.OnRelease = _ =>
        {
            if (Interlocked.Increment(ref asked) == 1)
            {
                _link.SetStreamLive(false);
            }

            return Task.CompletedTask;
        };
        _link.ReleaseAnswer = _ => Volatile.Read(ref asked) == 1
            ? new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).")
            : new ReleaseResult(true, true, string.Empty);

        var second = _link.Name("forge-neo:neo", ForgeNeo);
        await WaitUntilAsync(() => !_link.Releases.IsEmpty, "a release");
        await Task.Delay(300);
        Assert.That(_link.Releases, Has.Count.EqualTo(1), "A grant fences the warm container for good: not asked for a launch the stream may no longer name.");

        _link.SetStreamLive(true);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId)?.Phase == RunnerLaunchPhase.Started, "the second launch to start");
        Assert.That(_link.Releases, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task AReplacementWithdrawnAsItsReleaseIsSent_SendsNone()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        _link.BeforeNextRelease = _link.Withdraw;

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Cancelled));
            Assert.That(_link.Releases, Is.Empty, "A grant would fence the warm container for nothing.");
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(_engine.Stops, Is.Zero);
        });
    }

    [Test]
    public async Task ANewKeySavedAsAReplacementsReleaseIsSent_SendsNone()
    {
        const string newKey = "another-runner-key-0123456789abcdefghijklmnop";
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        _link.BeforeNextRelease = () => _settings.SaveAsync(_settings.Current with { RunnerKey = newKey }).GetAwaiter().GetResult();

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Releases, Is.Empty, "The replacement is no longer wanted: the warm container is not fenced for it.");
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(_engine.Stops, Is.Zero);
        });
    }

    [Test]
    public async Task AnotherGpuChosenAsAReplacementsReleaseIsSent_SendsNone()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();

        // Saved after the release was decided, before it is sent: the replacement was checked for
        // another GPU, and would not start as it is.
        var checksAtTheSave = -1;
        var checksAtTheRelease = -1;
        _link.BeforeNextRelease = () =>
        {
            checksAtTheSave = _engine.GpuChecks;
            _settings.SaveAsync(_settings.Current with { GpuUuid = FakeContainerEngine.GpuUuid }).GetAwaiter().GetResult();
        };
        _link.ReleaseAnswer = _ =>
        {
            checksAtTheRelease = checksAtTheRelease < 0 ? _engine.GpuChecks : checksAtTheRelease;
            return new ReleaseResult(true, true, string.Empty);
        };

        _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => checksAtTheSave >= 0, "the release to be about to be sent");
        await Task.Delay(400);

        // That release is not sent. One may follow, for a launch checked for the GPU now chosen: never
        // one that fences the warm container for a replacement that was not.
        Assert.That(checksAtTheRelease < 0 || checksAtTheRelease > checksAtTheSave, Is.True,
            $"A release went out with the GPU checked {checksAtTheRelease} times, as at the save ({checksAtTheSave}).");
    }

    [Test]
    public async Task AReplacementImageRemovedAfterItsDownload_KeepsTheWarmContainer()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        _engine.PullLeavesNoImage = true;

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.Releases, Is.Empty);
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task SettingsThatBecomeInvalidDuringAReplacementsDownload_KeepTheWarmContainer()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        var pulling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnPull = async token =>
        {
            _engine.OnPull = null;
            pulling.TrySetResult();
            await finish.Task.WaitAsync(token);
        };

        var second = _link.Name("forge-neo:neo", ForgeNeo);
        await pulling.Task.WaitAsync(WaitLimit);
        await _settings.SaveAsync(_settings.Current with { ForgeNeoArguments = "--share" });
        finish.TrySetResult();

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.Releases, Is.Empty, "Not released for a replacement that could not start.");
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task AWarmContainerThatExitsDuringAReplacementsDownload_IsReportedGoneAtOnce()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        var first = await LaunchAsync();
        var pulling = HoldNextPull();
        _link.Name("forge-neo:neo", ForgeNeo);
        await pulling.WaitAsync(WaitLimit);

        _engine.Exit(Name, 137);

        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == first.LaunchId), "the exit report while the download goes on");
    }

    [Test]
    public async Task AWarmContainerThatExitedDuringTheDownload_MakesRoomWithoutARelease()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        var first = await LaunchAsync();
        _engine.OnPull = async token =>
        {
            _engine.OnPull = null;
            _engine.Exit(Name, 137);
            await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == first.LaunchId), "the exit report during the download");
        };

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId)?.Phase == RunnerLaunchPhase.Started, "the replacement");
        Assert.Multiple(() =>
        {
            Assert.That(_link.Releases, Is.Empty, "It exited, and Offloadr was told so: no normal release for it.");
            Assert.That(supervisor.View.UnexpectedExits, Is.EqualTo(1));
            Assert.That(supervisor.LogLines, Has.Some.Contains("exit code 137"));
        });
    }

    [Test]
    public async Task AWarmContainerRemovedOutsideTheAppDuringTheDownload_MakesRoomWithoutARelease()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        var first = await LaunchAsync();
        _engine.OnPull = async token =>
        {
            _engine.OnPull = null;
            await _engine.RemoveAsync(Name, token);
            await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == first.LaunchId), "the exit report during the download");
        };

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId)?.Phase == RunnerLaunchPhase.Started, "the replacement");
        Assert.That(_link.Releases, Is.Empty, "Gone, and Offloadr was told so: no normal release for it.");
    }

    [Test]
    public async Task AWarmContainerThatExitsDuringTheDownload_IsReportedGone_EvenIfTheEngineKeepsIt()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        var first = await LaunchAsync();
        var pulling = HoldNextPull();
        _link.Name("forge-neo:neo", ForgeNeo);
        await pulling.WaitAsync(WaitLimit);

        // Exited; the engine keeps refusing to remove it (every attempt, also two at once).
        _engine.RemoveStoppedFailure = new ContainerEngineException("Could not remove container offloadr-runner.");
        _engine.Exit(Name, 137);

        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == first.LaunchId), "the exit report while the download goes on");
        _engine.RemoveStoppedFailure = null;
    }

    [Test]
    public async Task AWarmContainerStartedAgainDuringAReplacementsDownload_IsNotReportedGone()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        var pulling = HoldNextPull();
        _link.Name("forge-neo:neo", ForgeNeo);
        await pulling.WaitAsync(WaitLimit);

        // Seen stopped, but started again before it could be removed.
        _engine.BeforeNextRemoveStopped = () => _engine.Restart(Name);
        _engine.Exit(Name, 137);
        await WaitUntilAsync(() => _engine.BeforeNextRemoveStopped is null, "the removal tried");
        await Task.Delay(200);

        Assert.Multiple(() =>
        {
            Assert.That(_link.ExitReports, Is.Empty, "It runs again: reporting it gone would end a live session.");
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task AReleaseRetriedAfterTheComputerSlept_ChecksTheGpuFirst()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).");
        _link.OnRelease = _ =>
        {
            // Asleep while the release went unanswered; the GPU is lost meanwhile.
            _link.OnRelease = null;
            _engine.GpuCheck = new GpuAccessCheck(false, "NVIDIA driver lost after sleep");
            supervisor.NotifyResumed();
            return Task.CompletedTask;
        };

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.Releases, Has.Count.EqualTo(1), "Not asked again for a replacement that could not use the GPU.");
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task ALaunchWhoseRecordCannotBeSaved_DoesNotStart()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);

            // Something in the way of the record (a folder of that name, say): it cannot be written.
            Directory.CreateDirectory(paths.LaunchedContainers);
            await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();

            var launch = _link.Name("comfyui-latest", ComfyUi);

            await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
            Assert.Multiple(() =>
            {
                Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
                Assert.That(_engine.Runs, Is.Empty, "After a crash, Offloadr could never learn it is gone.");
            });
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task AReplacementImageRemovedBeforeTheRelease_KeepsTheWarmContainer()
    {
        _engine.ImageIds[ForgeNeo] = "sha256:forge";
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();

        // Downloaded; the stream drops, and a cleanup outside the app removes the image meanwhile.
        _engine.OnPull = _ =>
        {
            _engine.OnPull = null;
            _link.SetStreamLive(false);
            return Task.CompletedTask;
        };
        var second = _link.Name("forge-neo:neo", ForgeNeo);
        await WaitUntilAsync(() => _engine.Pulls.Contains(ForgeNeo) && !_link.MayStart(second), "the download done, the stream down");
        await Task.Delay(100);
        _engine.RemoveImageNamed(ForgeNeo);
        _engine.RemovedImages.Add("sha256:forge");
        _link.SetStreamLive(true);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.Releases, Is.Empty, "Not released for a replacement that could not start.");
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task SettingsThatBecomeInvalidWhileAReleaseIsRetried_SendNoFurtherRelease()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).");
        _link.OnRelease = async _ =>
        {
            _link.OnRelease = null;
            await _settings.SaveAsync(_settings.Current with { ForgeNeoArguments = "--share" });
        };

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.Releases, Has.Count.EqualTo(1), "Not asked again for a replacement that could not start.");
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task Stop_OffersNothing_WhileItWaitsForTheRelease()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        var releasing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _link.OnRelease = async token =>
        {
            releasing.TrySetResult();
            await answer.Task.WaitAsync(token);
        };

        supervisor.RequestStop();
        await releasing.Task.WaitAsync(WaitLimit);

        Assert.That(OffersNothing(), Is.True, "A launch Offloadr chose now would not be handled.");
        answer.TrySetResult();
        await WaitForStateAsync(SupervisorState.Stopped);
    }

    [Test]
    public async Task APreflightThatThrows_FailsTheLaunch_AndStopsOffering()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.CurrentContextFailure = new ContainerEngineException("docker context show failed.");

        var launch = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null && !_link.Running, "the failure and the pause");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.WaitingToRetry));
        });
    }

    [Test]
    public async Task ALaunchWhileTheWarmContainerIsStillInUse_ReportsFailed()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, true, "In use.");

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));
        });
    }

    [Test]
    public async Task AReleasedContainer_FoundAtTheNextStart_IsStoppedNotReused()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var launchId = Guid.NewGuid().ToString();
            File.WriteAllText(paths.ReleasedLaunch, launchId);
            AddLaunchedContainer(launchId);

            var supervisor = await StartSupervisorAsync(paths: paths);

            await WaitUntilAsync(() => !_engine.Exists(Name), "the released container's removal");
            await Task.Delay(100);
            Assert.Multiple(() =>
            {
                Assert.That(_engine.Stops, Is.EqualTo(1));
                Assert.That(_engine.Runs, Is.Empty);
                Assert.That(File.Exists(paths.ReleasedLaunch), Is.False);
                Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Stopped));
            });
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task AReleaseAskedBeforeACrash_IsAskedAgainFirst()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var launchId = Guid.NewGuid().ToString();
            File.WriteAllText(paths.ReleasedLaunch, "asking:" + launchId);
            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, AutoStart = true });
            _engine.StatusJson = Status(activeSessionId: "");
            AddLaunchedContainer(launchId);

            await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1), paths: paths);

            await WaitForStateAsync(SupervisorState.Available);
            Assert.Multiple(() =>
            {
                Assert.That(_link.Releases.Single().LaunchId, Is.EqualTo(launchId), "Not kept warm: its answer may have been a grant.");
                Assert.That(File.Exists(paths.ReleasedLaunch), Is.False);
            });
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task AReleaseAskedBeforeACrash_WithoutAutoStart_RegistersNothing_AndEndsWithTheFencedAgent()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var launchId = Guid.NewGuid().ToString();
            File.WriteAllText(paths.ReleasedLaunch, "asking:" + launchId);
            _engine.StatusJson = Status(activeSessionId: "");
            AddLaunchedContainer(launchId);

            var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1), paths: paths);
            await WaitForStateAsync(SupervisorState.Running);
            await Task.Delay(200);
            Assert.That(_link.Runs, Is.Empty, "Not registered just to ask: that could take the runner from another computer.");

            // The release had been granted: Offloadr fenced the agent, which exits by itself.
            _engine.Exit(Name, RunnerProtocolVersion.ExecutorFencedExitCode);

            await WaitUntilAsync(() => !_engine.Exists(Name) && supervisor.View.State == SupervisorState.Stopped, "the fenced container's cleanup");
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(paths.ReleasedLaunch), Is.False);
                Assert.That(supervisor.View.UnexpectedExits, Is.Zero);
                Assert.That(_link.Runs, Is.Empty);
            });
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task AReleasedContainer_IsMarkedUntilItIsStopped()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            _engine.StatusJson = Status(activeSessionId: "");
            string? markedDuringTheStop = null;
            _engine.BeforeStop = () => markedDuringTheStop = File.Exists(paths.ReleasedLaunch) ? File.ReadAllText(paths.ReleasedLaunch) : null;
            await StartSupervisorAsync(warmTime: TimeSpan.FromMilliseconds(200), paths: paths);
            await MakeAvailableAsync();
            var launch = await LaunchAsync();

            await WaitForStateAsync(SupervisorState.Available);

            Assert.Multiple(() =>
            {
                Assert.That(markedDuringTheStop, Is.EqualTo(launch.LaunchId));
                Assert.That(File.Exists(paths.ReleasedLaunch), Is.False);
            });
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task AContainerAnEarlierAppVersionStarted_IsNeverReleased_AndNoLauncherRegistersUntilItIsGone()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, AutoStart = true });
        _engine.StatusJson = Status(activeSessionId: "");
        var spec = Earlier().BuildSpec(_settings.Current, BuiltInLaunchPlanCatalog.Plan().Find("comfyui-latest")!);
        _engine.AddContainer(Name, spec.Image, running: true, spec.Labels);
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromMilliseconds(100));
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => supervisor.View.Message?.Contains("earlier version", StringComparison.Ordinal) == true, "the note on the earlier container");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Runs, Is.Empty, "A launcher that could neither launch nor release would only take the runner from another computer.");
            Assert.That(_link.Releases, Is.Empty);
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(supervisor.View.IsReady, Is.False);
        });

        _engine.Exit(Name, 0);

        await WaitUntilAsync(() => _link.Running && supervisor.View.IsReady, "the offer once it is gone");
    }

    [Test]
    public async Task AContainerLaunchedForAnEarlierKey_IsNotReleasedWithTheNewOne()
    {
        const string newKey = "another-runner-key-0123456789abcdefghijklmnop";

        // In use until the new key is saved, so the warm time cannot release it before that.
        _engine.StatusJson = Status(activeSessionId: "session-1");
        await StartSupervisorAsync(warmTime: TimeSpan.FromMilliseconds(100));
        await MakeAvailableAsync();
        await LaunchAsync();

        await _settings.SaveAsync(_settings.Current with { RunnerKey = newKey });
        await WaitUntilAsync(() => !_link.Running, "the launcher for the old key to stop");
        _engine.StatusJson = Status(activeSessionId: "");
        await Task.Delay(300);

        Assert.Multiple(() =>
        {
            Assert.That(_link.Releases, Is.Empty, "Only the Offloadr and key that launched it can release it.");
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(_link.Runs, Has.Count.EqualTo(1), "Not registered for the new key while no launch could start: another computer may serve that runner.");
        });

        _engine.Exit(Name, 0);

        await WaitUntilAsync(() => _link.Running && _link.Runs.Count == 2, "the launcher for the new key");
        Assert.That(_link.Runs.Last().RunnerKey, Is.EqualTo(newKey));
    }

    // Exits

    [Test]
    public async Task AnExitedContainer_IsNotStartedAgain()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

        _engine.Exit(Name, 137);

        await WaitForStateAsync(SupervisorState.Available);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Has.Count.EqualTo(1), "Offloadr launches a new one when a session needs it.");
            Assert.That(_engine.Exists(Name), Is.False, "The exited container holds the runner key; it is removed.");
            Assert.That(supervisor.LogLines, Has.Some.Contains("exit code 137"));
            Assert.That(supervisor.View.UnexpectedExits, Is.EqualTo(1));
            Assert.That(supervisor.View.Interruption, Is.Null, "No session was running.");
        });
    }

    [Test]
    public async Task AnExitDuringASession_IsShownAsInterrupted_UntilDismissed()
    {
        _engine.StatusJson = Status(activeSessionId: "session-1");
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();
        await WaitUntilAsync(() => supervisor.View.HasActiveSession);

        _engine.Exit(Name, 137);

        await WaitForStateAsync(SupervisorState.Available);
        Assert.That(supervisor.View.Interruption, Does.Contain("interrupted").And.Contain("exit code 137"));

        supervisor.DismissInterruption();
        Assert.That(supervisor.View.Interruption, Is.Null);
    }

    [Test]
    public async Task AContainerOffloadrFenced_EndsQuietly()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

        _engine.Exit(Name, Offloadr.Runner.Core.RunnerProtocolVersion.ExecutorFencedExitCode);

        await WaitForStateAsync(SupervisorState.Available);
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.LogLines, Has.Some.Contains("Offloadr no longer needs it"));
            Assert.That(supervisor.View.UnexpectedExits, Is.Zero);
        });
    }

    [Test]
    public async Task Monitoring_WhenDockerStopsAnswering_KeepsWatchingTheRunner()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

        _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("did not answer", StringComparison.Ordinal)));
        Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));

        _engine.InspectFailure = null;
        _engine.Exit(Name, 137);

        await WaitForStateAsync(SupervisorState.Available);
        Assert.That(supervisor.LogLines, Has.Some.Contains("answering again"));
    }

    [Test]
    public async Task DockerNotAnsweringWhileAContainerRuns_StopsOfferingTheComputer_UntilItAnswers()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();
        // (Its executor matched the launch: named no more.)
        _link.Withdraw();
        await WaitUntilAsync(() => !_supervisor!.View.LaunchUnmatched && _link.State.Launch is null, "the launch to be done with");
        await Task.Delay(100);

        _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");

        await WaitUntilAsync(() => !_link.Running, "the launcher to stop");
        Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running), "The container is still watched.");
        _engine.InspectFailure = null;

        await WaitUntilAsync(() => _link.Running && _link.Runs.Count == 2, "the launcher to come back");
        Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));
    }

    [Test]
    public async Task AGpuChangedDuringALaunchDownload_FailsTheLaunch_AndIsCheckedFirst()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.OnPull = async _ =>
        {
            _engine.OnPull = null;
            await _settings.SaveAsync(_settings.Current with { GpuUuid = FakeContainerEngine.GpuUuid });
        };

        var launch = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _engine.GpuChecks == 2 && _link.Running, "the check of the chosen GPU");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Reports.Where(report => report.LaunchId == launch.LaunchId && report.IsTerminal), Is.Empty,
                "Left for Offloadr to retire once it accepts the new offer.");
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task AContainerThatTookTheName_IsNotStoppedAsTheRunner()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

        // Between two checks the runner is gone and someone else's container has its name.
        await _engine.RemoveAsync(Name, CancellationToken.None);
        _engine.AddContainer(Name, "someone-elses-image", running: true, new Dictionary<string, string>());

        // The check after the exit finds the name taken: nothing could be launched, so nothing is offered.
        await WaitForStateAsync(SupervisorState.Faulted);
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.LogLines, Has.Some.Contains("a different container now has the runner's name"));
            Assert.That(supervisor.View.Fault?.Kind, Is.EqualTo(FaultKind.NameTaken));
            Assert.That(supervisor.View.Message, Does.Contain("was not started by this app"));
            Assert.That(_link.Running, Is.False);
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task ALaunch_WhenAnotherContainerTakesTheName_ReportsFailed_ThenStopsOffering()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.OnPull = _ =>
        {
            _engine.OnPull = null;
            _engine.AddContainer(Name, "someone-elses-image", running: true, new Dictionary<string, string>());
            return Task.CompletedTask;
        };

        var launch = _link.Name("forge-neo:neo", ForgeNeo);

        // The failure goes out first; then nothing could be launched, so nothing is offered.
        await WaitForStateAsync(SupervisorState.Faulted);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(supervisor.View.Fault?.Kind, Is.EqualTo(FaultKind.NameTaken));
            Assert.That(_link.Running, Is.False);
            Assert.That(_engine.Stops, Is.Zero);
        });
    }

    [Test]
    public async Task ARenamedRunner_IsStillTheRunner()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

        // `docker rename offloadr-runner something-else`: it is still watched, and stopped.
        _engine.Rename(Name, "something-else");
        await Task.Delay(300);
        Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));

        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);
        Assert.That(_engine.Exists("something-else"), Is.False);
    }

    // The launcher refused for good

    [Test]
    public async Task AnOutdatedLauncher_AsksForAnAppUpdate()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();

        _link.End(LauncherConnection.Outdated, "Offloadr needs a newer version of this app.");

        await WaitForStateAsync(SupervisorState.Faulted);
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Fault?.Kind, Is.EqualTo(FaultKind.AppOutdated));
            Assert.That(supervisor.View.Message, Does.Contain("newer version"));
            Assert.That(supervisor.RunnerWanted, Is.False);
        });
    }

    [Test]
    public async Task ALauncherTakenOverWhileAvailable_StaysOff()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();

        _link.End(LauncherConnection.Superseded, "Another Offloadr Runner app took over this runner.");

        await WaitForStateAsync(SupervisorState.Stopped);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Message, Does.Contain("took over"));
            Assert.That(_link.Runs, Has.Count.EqualTo(1), "Two computers do not keep taking the runner from each other.");
            Assert.That(supervisor.RunnerWanted, Is.False);
        });
    }

    [Test]
    public async Task Start_AfterATakeoverWhileOff_TakesTheRunnerBackInOneGo()
    {
        _link.FirstHeartbeats.Enqueue(new LauncherLinkState(LauncherConnection.Superseded, "Another Offloadr Runner app took over this runner.", false, null));
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();

        await WaitForStateAsync(SupervisorState.Available);
        await WaitUntilAsync(() => _link.Running);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Runs, Has.Count.EqualTo(2));
            Assert.That(supervisor.LogLines, Has.Some.Contains("taking it back"));
        });
    }

    [Test]
    public async Task AutoStart_AfterATakeoverWhileOff_StaysOff()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, AutoStart = true });
        _link.FirstHeartbeats.Enqueue(new LauncherLinkState(LauncherConnection.Superseded, "Another Offloadr Runner app took over this runner.", false, null));
        var supervisor = await StartSupervisorAsync();

        await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Stopped && _link.Runs.Count == 1, "the launcher to stop");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Runs, Has.Count.EqualTo(1));
            Assert.That(supervisor.View.Message, Does.Contain("took over"));
        });
    }

    [Test]
    public async Task SettingsThatBecomeInvalid_WhileAContainerRuns_StopTheLauncher_AndFaultOnceItIsGone()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

        await _settings.SaveAsync(_settings.Current with { OffloadrApiOrigin = "http://offloadr.example" });

        await WaitUntilAsync(() => !_link.Running, "the launcher to stop");
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));
            Assert.That(_engine.Exists(Name), Is.True, "It keeps serving with the settings it has.");
        });

        _engine.Exit(Name, 0);

        await WaitForStateAsync(SupervisorState.Faulted);
        Assert.That(supervisor.View.Fault?.Kind, Is.EqualTo(FaultKind.Settings));
    }

    [Test]
    public async Task AnUpdateAfterATakeover_LeavesTheServingContainerRunning()
    {
        _engine.StatusJson = Status(activeSessionId: "session-1");
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();
        _link.End(LauncherConnection.Superseded, "Another Offloadr Runner app took over this runner.");
        await WaitUntilAsync(() => supervisor.View.Message.Contains("keeps running", StringComparison.Ordinal));

        supervisor.KeepRunnerOnShutdown();
        await supervisor.StopAsync(CancellationToken.None);
        _supervisor = null;
        supervisor.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(supervisor.RunnerWanted, Is.False, "The new version does not take the runner back by itself.");
        });
    }

    [Test]
    public async Task ATakeover_IsKeptAcrossAppStarts_UntilThePersonPressesStart()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, AutoStart = true });
            var first = await StartSupervisorAsync(paths: paths);
            await WaitForStateAsync(SupervisorState.Available);
            _link.End(LauncherConnection.Superseded, "Another Offloadr Runner app took over this runner.");
            await WaitForStateAsync(SupervisorState.Stopped);
            await first.StopAsync(CancellationToken.None);
            first.Dispose();
            Assert.That(File.ReadAllText(paths.TakenOver), Does.Not.Contain(Key));

            _link = new FakeLauncherLink();
            // The same runner, the default port written out: still the one taken over.
            await _settings.SaveAsync(_settings.Current with { OffloadrApiOrigin = RunnerSettings.DefaultOffloadrApiOrigin + ":443" });
            var next = await StartSupervisorAsync(paths: paths);
            await Task.Delay(300);
            Assert.Multiple(() =>
            {
                Assert.That(_link.Runs, Is.Empty, "A start with the app does not take the runner back.");
                Assert.That(next.View.State, Is.EqualTo(SupervisorState.Stopped));
                Assert.That(next.View.Message, Does.Contain("took over"));
            });

            next.RequestStart();

            await WaitForStateAsync(SupervisorState.Available);
            Assert.That(File.Exists(paths.TakenOver), Is.False);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task Start_AfterAStopDockerDidNotAnswer_WatchesTheContainerAgain()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();
        // Offloadr does not answer the release before the stop: nothing is granted, so the container
        // may be watched again (a granted one would be stopped once more instead).
        _link.ReleaseAnswer = _ => new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).");
        _engine.NextStopFailure = new ContainerEngineException("docker stop timed out.");
        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Faulted);

        supervisor.RequestStart();

        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => _link.Running);
        _engine.Exit(Name, 137);
        await WaitForStateAsync(SupervisorState.Available);
    }

    [Test]
    public async Task AfterSleep_AGpuCheckThatFails_StopsOfferingTheComputer()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();
        _engine.GpuCheck = new GpuAccessCheck(false, "NVIDIA driver lost after sleep");
        supervisor.NotifyResumed();
        await WaitUntilAsync(OffersNothing, "availability to pause");

        _engine.Exit(Name, 137);

        // Once the container is gone the checks keep being retried, and say why the computer is not offered.
        await WaitForStateAsync(SupervisorState.WaitingToRetry);
        Assert.That(supervisor.View.Fault?.Kind, Is.EqualTo(FaultKind.GpuUnavailable));
        Assert.That(supervisor.RunnerWanted, Is.True, "It comes back by itself once the GPU does.");
        _engine.GpuCheck = new GpuAccessCheck(true, "GPU 0: NVIDIA GeForce RTX 4090");
        await WaitForStateAsync(SupervisorState.Available);
    }

    [Test]
    public async Task ATakeoverWhileAContainerRuns_KeepsItUntilItIsStopped()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

        _link.End(LauncherConnection.Superseded, "Another Offloadr Runner app took over this runner.");

        await WaitUntilAsync(() => supervisor.View.Message.Contains("keeps running until you stop it", StringComparison.Ordinal));
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));
            Assert.That(_engine.Exists(Name), Is.True);
        });

        _engine.Exit(Name, 0);
        await WaitForStateAsync(SupervisorState.Stopped);
    }

    // Adoption, engines and crashes

    [Test]
    public async Task Startup_AdoptsARunningContainerItLaunchedEarlier_AndIsAvailableAgain()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, SelectedEditorTemplate = "comfyui-master", AutoStart = true });
        AddLaunchedContainer(Guid.NewGuid().ToString(), "comfyui-master");

        var supervisor = await StartSupervisorAsync();

        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => _link.Running);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(supervisor.View.Message, Does.Contain("Reconnected"));
            Assert.That(supervisor.View.ActiveTemplate?.EditorTemplate, Is.EqualTo("comfyui-master"));
            Assert.That(supervisor.RunnerWanted, Is.True);
        });
    }

    [Test]
    public async Task Startup_WithoutAutoStart_AdoptsAContainer_ButOffersNothing()
    {
        AddLaunchedContainer(Guid.NewGuid().ToString());

        var supervisor = await StartSupervisorAsync();

        await WaitForStateAsync(SupervisorState.Running);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Runs, Is.Empty, "As at any app start: offered only with AutoStart, or after an update that had it so.");
            Assert.That(supervisor.RunnerWanted, Is.False);
            Assert.That(_engine.Exists(Name), Is.True);
        });
    }

    [Test]
    public async Task AnUpdate_LeavesAnAdoptedContainerRunning_ThoughNothingIsOffered()
    {
        AddLaunchedContainer(Guid.NewGuid().ToString());
        var supervisor = await StartSupervisorAsync();
        await WaitForStateAsync(SupervisorState.Running);

        supervisor.KeepRunnerOnShutdown();
        await supervisor.StopAsync(CancellationToken.None);
        _supervisor = null;
        supervisor.Dispose();

        Assert.That(_engine.Exists(Name), Is.True, "It may serve a session; only a Stop ends it.");
    }

    [Test]
    public async Task AStop_WhoseStatusCannotBeRead_AsksForTheReleaseWithTheLaunchAlone()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var launch = await LaunchAsync();

        // Only the Stop's own read fails: a regular read failing first would be an engine outage, which
        // stops the launcher before the Stop could ask anything.
        _engine.BeforeRead = () =>
        {
            if (supervisor.View.State == SupervisorState.Stopping)
            {
                throw new ContainerEngineException("docker exec timed out.");
            }
        };

        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);

        var release = _link.Releases.Single();
        Assert.Multiple(() =>
        {
            Assert.That(release.LaunchId, Is.EqualTo(launch.LaunchId));
            Assert.That(release.ExecutorInstanceId, Is.Null);
        });
    }

    [Test]
    public async Task AnAdoptedContainer_ChecksTheGpuInItsOwnImage_WithoutADownload()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, AutoStart = true });
        AddLaunchedContainer(Guid.NewGuid().ToString());

        await StartSupervisorAsync();

        await WaitUntilAsync(() => _link.Running, "the launcher");
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Pulls, Is.Empty);
            Assert.That(_engine.GpuCheckImages.First(), Is.EqualTo("sha256:local"));
        });
    }

    [Test]
    public async Task StoppingAnAdoptedContainer_WithNoLauncher_RegistersNothing()
    {
        AddLaunchedContainer(Guid.NewGuid().ToString());
        var supervisor = await StartSupervisorAsync();
        await WaitForStateAsync(SupervisorState.Running);

        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Stopped);

        Assert.Multiple(() =>
        {
            Assert.That(_link.Runs, Is.Empty, "Registering only to ask could take the runner over from another computer.");
            Assert.That(_link.Releases, Has.Count.EqualTo(1), "Asked all the same, with the key in Settings: a release needs no registered launcher.");
            Assert.That(_engine.Exists(Name), Is.False, "The person asked for the stop.");
        });
    }

    [Test]
    public async Task ALaunchNamedAfterTheGpuChanged_IsNotServedOnTheNewGpu()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        // The loop is held in a check, so the launch is handled before the refresh that checks and
        // offers the new GPU.
        var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnProbe = async _ =>
        {
            _engine.OnProbe = null;
            probing.TrySetResult();
            await release.Task;
        };
        _supervisor!.RequestRefresh();
        await probing.Task.WaitAsync(WaitLimit);
        var launch = _link.Name("forge-neo:neo", ForgeNeo);
        await _settings.SaveAsync(_settings.Current with { GpuUuid = FakeContainerEngine.GpuUuid });
        release.TrySetResult();

        await WaitUntilAsync(() => _engine.GpuChecks == 2 && _link.Running, "the new GPU checked and offered");
        await Task.Delay(300);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty, "Not served on a GPU Offloadr did not choose it for.");
            Assert.That(_link.Reports.Where(report => report.LaunchId == launch.LaunchId && report.IsTerminal), Is.Empty,
                "Left for Offloadr to retire once it accepts the new offer.");
        });
    }

    [Test]
    public async Task AChangedGpu_PausesTheOfferWhileItIsChecked()
    {
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.OnProbe = async _ =>
        {
            checking.TrySetResult();
            await release.Task;
        };

        await _settings.SaveAsync(_settings.Current with { GpuUuid = FakeContainerEngine.GpuUuid });
        await checking.Task.WaitAsync(WaitLimit);
        await WaitUntilAsync(() => !_link.Running, "the offer to pause");
        _engine.OnProbe = null;
        release.TrySetResult();

        await WaitUntilAsync(() => _link.Running && _link.Runs.Count == 2 && _supervisor!.View.IsReady, "the offer back");
    }

    [Test]
    public async Task AGpuChosenDuringTheFirstChecks_IsCheckedBeforeTheLauncherRegisters()
    {
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var supervisor = await StartSupervisorAsync();
        _engine.OnNextGpuCheck = async () =>
        {
            checking.TrySetResult();
            await release.Task;
        };

        supervisor.RequestStart();
        await checking.Task.WaitAsync(WaitLimit);
        await _settings.SaveAsync(_settings.Current with { GpuUuid = FakeContainerEngine.GpuUuid });
        release.TrySetResult();

        await WaitUntilAsync(() => _link.Running && supervisor.View.IsReady, "the offer");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Runs, Has.Count.EqualTo(1), "Registered once, for the GPU now chosen, not first for the old one.");
        });
    }

    [Test]
    public async Task AQuit_AsksOffloadrToReleaseTheContainerFirst()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var launch = await LaunchAsync();

        await supervisor.StopAsync(CancellationToken.None);
        _supervisor = null;
        supervisor.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(_link.Releases.Single().LaunchId, Is.EqualTo(launch.LaunchId));
            Assert.That(_engine.Exists(Name), Is.False);
            Assert.That(_link.Running, Is.False);
        });
    }

    [Test]
    public async Task AnAdoptedContainer_IsReleasedWithTheLaunchItWasStartedFor()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, AutoStart = true });
        _engine.StatusJson = Status(activeSessionId: "");
        var launchId = Guid.NewGuid().ToString();
        AddLaunchedContainer(launchId);

        await StartSupervisorAsync(warmTime: TimeSpan.FromMilliseconds(200));

        await WaitForStateAsync(SupervisorState.Available);
        Assert.That(_link.Releases.Single().LaunchId, Is.EqualTo(launchId));
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
    public async Task Start_WithARunningContainerMissedAtStartup_AdoptsIt()
    {
        AddLaunchedContainer(Guid.NewGuid().ToString());
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
            Assert.That(_link.Running, Is.True);
        });
    }

    [Test]
    public async Task RunnerOfAnotherCopyOfTheApp_IsNeitherAdoptedNorStopped()
    {
        var spec = Earlier().BuildSpec(_settings.Current, BuiltInLaunchPlanCatalog.Plan().Find("comfyui-latest")!);
        var labels = new Dictionary<string, string>(spec.Labels) { [RunnerSupervisor.OwnerLabel] = "another-copy" };
        _engine.AddContainer(Name, spec.Image, running: true, labels);
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();
        await WaitForStateAsync(SupervisorState.Faulted);

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.Message, Does.Contain("belongs to another copy of this app"));
            Assert.That(_engine.Stops, Is.Zero);
            Assert.That(_engine.Exists(Name), Is.True);
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
            AddLaunchedContainer(Guid.NewGuid().ToString());

            // Settings moved to engine B while a session kept the runner on A; then the app crashed.
            await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b", AutoStart = true });
            _selection = new ContainerEngineSelection(Locate, statePath);
            _engine.StatusJson = Status(activeSessionId: "session-1");
            var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));

            await WaitForStateAsync(SupervisorState.Running);
            await WaitUntilAsync(() => supervisor.View.HasActiveSession);
            Assert.Multiple(() =>
            {
                Assert.That(_engine.Stops, Is.Zero);
                Assert.That(_engine.ExistsOn("docker-a", Name), Is.True);
            });

            // The session ends: the runner is released and stopped on A, and the next one runs on B.
            _engine.StatusJson = Status(activeSessionId: "");
            await WaitForStateAsync(SupervisorState.Available);
            await WaitUntilAsync(() => _supervisor!.View.IsReady && _link.Running, "the launcher to be ready");
            await LaunchAsync();
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
    public async Task AReplacementOnTheNewlyCheckedEngine_ReleasesTheContainerOnTheOldOne()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);

            // The runner runs on engine A after a crash; Settings name engine B, checked and offered.
            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
            _selection = new ContainerEngineSelection(Locate, statePath);
            _selection.Select("docker-a");
            _selection.Remember();
            AddLaunchedContainer(Guid.NewGuid().ToString());
            await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b", AutoStart = true });
            _selection = new ContainerEngineSelection(Locate, statePath);
            _engine.AddImage(ComfyUi);
            _engine.StatusJson = Status(activeSessionId: "session-1");
            var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
            await WaitUntilAsync(() => supervisor.View.HasActiveSession && supervisor.View.IsReady, "the offer on engine B");

            var launch = _link.Name("forge-neo:neo", ForgeNeo);

            await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
            Assert.Multiple(() =>
            {
                Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started), "Settings did not change: engine B is what was checked.");
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
    public async Task Quit_OffersNothing_WhileItWaitsForTheRelease()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        var releasing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _link.OnRelease = async token =>
        {
            releasing.TrySetResult();
            await answer.Task.WaitAsync(token);
        };

        var quitting = supervisor.StopAsync(CancellationToken.None);
        await releasing.Task.WaitAsync(WaitLimit);

        Assert.Multiple(() =>
        {
            Assert.That(OffersNothing(), Is.True, "The app is going: it can act on no new launch.");
            Assert.That(supervisor.RunnerWanted, Is.True, "Still wanted, for an update to hand on.");
        });
        answer.TrySetResult();
        await quitting;
        _supervisor = null;
        supervisor.Dispose();
    }

    [Test]
    public async Task ALeftoverContainerStartedAgainBeforeALaunch_IsNotRemovedByForce()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();

        // A stopped runner of this app turns up after the checks, and is started again by hand just as
        // the next launch would remove it.
        AddLaunchedContainer(Guid.NewGuid().ToString());
        _engine.Exit(Name, 1);
        _engine.BeforeNextRemoveStopped = () => _engine.Restart(Name);
        var launch = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_engine.Exists(Name), Is.True, "It runs without a release: not removed.");
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task Startup_AStoppedRunnerStartedAgainAsItIsRemoved_IsAdopted()
    {
        AddLaunchedContainer(Guid.NewGuid().ToString());
        _engine.Exit(Name, 0);
        _engine.BeforeNextRemoveStopped = () => _engine.Restart(Name);

        var supervisor = await StartSupervisorAsync();

        await WaitForStateAsync(SupervisorState.Running);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.True, "Never removed by force: it may serve a session.");
            Assert.That(_engine.Stops, Is.Zero);
        });
    }

    [Test]
    public async Task AContainerThatExitsDuringASession_IsReportedGone()
    {
        _engine.StatusJson = Status(activeSessionId: "session-1");
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        var launch = await LaunchAsync();
        await WaitUntilAsync(() => _supervisor!.View.HasActiveSession);

        _engine.Exit(Name, 137);

        await WaitUntilAsync(() => !_link.ExitReports.IsEmpty, "the exit report");
        Assert.Multiple(() =>
        {
            Assert.That(_link.ExitReports.Single().LaunchId, Is.EqualTo(launch.LaunchId));
            Assert.That(_link.ExitReports.Single().ExecutorInstanceId, Is.EqualTo("instance"), "So Offloadr ends exactly that executor's session.");
            Assert.That(_link.Releases, Is.Empty);
        });
    }

    [Test]
    public async Task ALaunchWhoseNameIsTaken_IsNotReportedAsAnExit()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.NextRunConflict = true;

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.ExitReports, Is.Empty, "No container was created for it: its FAILED says enough.");
        });
    }

    [Test]
    public async Task AReplacementWhoseNameAnotherContainerHolds_KeepsTheRenamedWarmContainer()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        _engine.Rename(Name, "renamed-runner");
        _engine.AddContainer(Name, "someone-elses-image", running: true, new Dictionary<string, string>());

        var second = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(second.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.Releases, Is.Empty, "Not released for a replacement that cannot take the name.");
            Assert.That(_engine.Exists("renamed-runner"), Is.True);
            Assert.That(_engine.Stops, Is.Zero);
        });
    }

    [Test]
    public async Task AContainerReleasedWhenIdle_IsNotReportedGone()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromMilliseconds(100));
        await MakeAvailableAsync();
        await LaunchAsync();

        await WaitUntilAsync(() => !_engine.Exists(Name), "the idle release");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Releases, Has.Count.EqualTo(1));
            Assert.That(_link.ExitReports, Is.Empty, "Offloadr released it: it knows.");
        });
    }

    [Test]
    public async Task Stop_WithoutAGrantedRelease_ReportsTheContainerGone_BeforeTheLauncherStops()
    {
        _engine.StatusJson = Status(activeSessionId: "session-1");
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var launch = await LaunchAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, true, "A session is open.");

        supervisor.RequestStop();

        await WaitForStateAsync(SupervisorState.Stopped);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.False, "The person asked: stopped all the same.");
            Assert.That(_link.ExitReports.Select(report => report.LaunchId), Is.EqualTo(new[] { launch.LaunchId }));
        });
    }

    [Test]
    public async Task AContainerGoneWhileTheAppWasClosed_IsReportedAtTheNextStart()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var first = await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            var launch = await LaunchAsync();

            // Left running for an update; it dies before the new version starts.
            first.KeepRunnerOnShutdown();
            await first.StopAsync(CancellationToken.None);
            _supervisor = null;
            first.Dispose();
            Assert.That(File.ReadAllText(paths.LaunchedContainers), Does.Contain(launch.LaunchId));
            await _engine.RemoveAsync(Name, CancellationToken.None);

            var runsBefore = _link.Runs.Count;
            await StartSupervisorAsync(paths: paths);

            // Reported at once, with AutoStart off: the report needs no registered launcher, and none registers.
            await WaitUntilAsync(() => !_link.ExitReports.IsEmpty, "the exit report");
            await WaitUntilAsync(() => !File.Exists(paths.LaunchedContainers), "the record cleared once answered");
            Assert.Multiple(() =>
            {
                Assert.That(_link.ExitReports.Single().LaunchId, Is.EqualTo(launch.LaunchId));
                Assert.That(_link.Runs, Has.Count.EqualTo(runsBefore), "Not registered just to report.");
            });
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task AContainerRenamedWhileTheAppWasClosed_IsAdoptedByItsLaunch_NotReportedGone()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var first = await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            await LaunchAsync();
            first.KeepRunnerOnShutdown();
            await first.StopAsync(CancellationToken.None);
            _supervisor = null;
            first.Dispose();

            // Renamed: neither its saved ID nor its name finds it, but it still runs, maybe serving a session.
            _engine.Rename(Name, "renamed-runner");
            var second = await StartSupervisorAsync(paths: paths);

            await WaitForStateAsync(SupervisorState.Running);
            await Task.Delay(300);
            Assert.Multiple(() =>
            {
                Assert.That(_link.ExitReports, Is.Empty, "Found by its launch: it runs.");
                Assert.That(second.View.State, Is.EqualTo(SupervisorState.Running), "Supervised again, so it is released before anything replaces it.");
                Assert.That(_engine.Exists("renamed-runner"), Is.True);
            });
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task ARunnerFoundByItsLaunchWhileAStopIsUnderWay_IsStoppedWithIt_NotAdoptedAfter()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var first = await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            var launch = await LaunchAsync();
            first.KeepRunnerOnShutdown();
            await first.StopAsync(CancellationToken.None);
            _supervisor = null;
            first.Dispose();

            // Renamed while the app was closed: only a look by its launch finds it. The look as the app
            // starts fails (Docker hiccups); Stop is pressed while the next one is under way.
            _engine.Rename(Name, "renamed-runner");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pressed = 0;
            _engine.FindByLabelFailures = 1;
            _engine.BeforeNextFindByLabel = () => _engine.BeforeNextFindByLabel = () =>
            {
                started.Task.Wait(WaitLimit);
                _supervisor!.RequestStop();
                Interlocked.Exchange(ref pressed, 1);
            };
            var second = await StartSupervisorAsync(paths: paths);
            started.SetResult();

            await WaitUntilAsync(() => Volatile.Read(ref pressed) == 1, "the Stop during the look");
            await WaitUntilAsync(() => !_engine.Exists("renamed-runner"), "the found runner to be stopped");
            await WaitForStateAsync(SupervisorState.Stopped);
            await Task.Delay(300);
            Assert.Multiple(() =>
            {
                Assert.That(second.View.State, Is.EqualTo(SupervisorState.Stopped), "Not adopted after the Stop, with no Start asked.");
                Assert.That(_link.Releases.Select(release => release.LaunchId), Does.Contain(launch.LaunchId), "Released before it is stopped.");
            });
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task ARunnerFoundOnlyAfterAStopFinished_IsStoppedThen_NotAdopted()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var first = await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            var launch = await LaunchAsync();
            first.KeepRunnerOnShutdown();
            await first.StopAsync(CancellationToken.None);
            _supervisor = null;
            first.Dispose();

            // Renamed while the app was closed, and Docker does not find it by its launch for a while:
            // the person presses Stop meanwhile, and it is found only after that Stop finished.
            _engine.Rename(Name, "renamed-runner");
            _engine.FindByLabelFailures = int.MaxValue;
            var second = await StartSupervisorAsync(paths: paths);
            await Task.Delay(200);
            second.RequestStop();
            await WaitForStateAsync(SupervisorState.Stopped);
            await Task.Delay(200);
            Assert.That(_engine.Exists("renamed-runner"), Is.True, "Not found yet.");

            _engine.FindByLabelFailures = 0;

            await WaitUntilAsync(() => !_engine.Exists("renamed-runner"), "the runner found later to be stopped");
            await WaitForStateAsync(SupervisorState.Stopped);
            await Task.Delay(300);
            Assert.Multiple(() =>
            {
                Assert.That(second.View.State, Is.EqualTo(SupervisorState.Stopped), "The Stop still stands: not adopted with no Start asked.");
                Assert.That(_link.Releases.Select(release => release.LaunchId), Does.Contain(launch.LaunchId), "Released before it is stopped.");
            });
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task TheAppClosingWhileAFoundRunnerIsBeingAdopted_StillStopsIt()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var first = await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            await LaunchAsync();
            first.KeepRunnerOnShutdown();
            await first.StopAsync(CancellationToken.None);
            _supervisor = null;
            first.Dispose();

            // Renamed while the app was closed, and found by its launch only after the app has started:
            // it is being taken under supervision (its catalog is asked for) when the app closes.
            _engine.Rename(Name, "renamed-runner");
            _engine.FindByLabelFailures = int.MaxValue;
            var second = await StartSupervisorAsync(paths: paths);
            await Task.Delay(300);
            var adopting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _plans.OnGet = async token =>
            {
                adopting.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            };
            _engine.FindByLabelFailures = 0;
            await adopting.Task.WaitAsync(WaitLimit);

            await second.StopAsync(CancellationToken.None);

            Assert.That(_engine.Exists("renamed-runner"), Is.False, "Known to run: quitting stops it, adopted yet or not.");
        }
        finally
        {
            _plans.OnGet = null;
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task TheAppClosingWhileTheRunnerFoundAsItStartsIsBeingAdopted_StillStopsIt()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var first = await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            await LaunchAsync();
            first.KeepRunnerOnShutdown();
            await first.StopAsync(CancellationToken.None);
            _supervisor = null;
            first.Dispose();

            // The next start finds it running, and is still taking it under supervision (its catalog is
            // asked for) when the app closes.
            var adopting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _plans.OnGet = async token =>
            {
                adopting.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            };
            var second = await StartSupervisorAsync(paths: paths);
            await adopting.Task.WaitAsync(WaitLimit);
            Assert.That(second.View.State, Is.Not.EqualTo(SupervisorState.Running), "Not adopted yet.");

            await second.StopAsync(CancellationToken.None);

            Assert.That(_engine.Exists(Name), Is.False, "Known to run: quitting stops it, adopted yet or not.");
        }
        finally
        {
            _plans.OnGet = null;
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task AnAdoptedContainerStartedForAnEarlierKey_IsReportedGone_AsTheRunnerItWasStartedFor()
    {
        const string newKey = "another-runner-key-0123456789abcdefghijklmnop";
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var first = await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            var launch = await LaunchAsync();
            var earlierTarget = FakeLauncherLink.TargetOf(_settings.Current);
            first.KeepRunnerOnShutdown();
            await first.StopAsync(CancellationToken.None);
            _supervisor = null;
            first.Dispose();

            // Settings name another runner by the next start; the container of the earlier one still runs.
            await _settings.SaveAsync(_settings.Current with { RunnerKey = newKey });
            await StartSupervisorAsync(paths: paths);
            await WaitForStateAsync(SupervisorState.Running);
            await Task.Delay(200);

            _engine.Exit(Name, 1);

            await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the exit report");
            Assert.That(_link.ExitReports.Single(report => report.LaunchId == launch.LaunchId).Target, Is.EqualTo(earlierTarget),
                "Only the Offloadr that launched it can be told: asked with the address and key its container was started with.");
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task AContainerOfAnEarlierKeyThatIsGoneWhenTheAppStartsAgain_IsStillReportedAsItsRunner()
    {
        const string newKey = "another-runner-key-0123456789abcdefghijklmnop";
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var first = await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            var launch = await LaunchAsync();
            var earlierTarget = FakeLauncherLink.TargetOf(_settings.Current);
            Assert.That(File.ReadAllText(paths.LaunchedCredentials), Does.Not.Contain(Key), "Saved encrypted, like the key in the settings.");
            first.KeepRunnerOnShutdown();
            await first.StopAsync(CancellationToken.None);
            _supervisor = null;
            first.Dispose();

            // Settings name another runner, and the container is gone (exited and removed) before the app
            // starts again: nothing is left on the engine to read what it was started with.
            await _settings.SaveAsync(_settings.Current with { RunnerKey = newKey });
            await _engine.RemoveAsync(Name, CancellationToken.None);
            await StartSupervisorAsync(paths: paths);

            await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the exit report");
            Assert.That(_link.ExitReports.Single(report => report.LaunchId == launch.LaunchId).Target, Is.EqualTo(earlierTarget),
                "Only the Offloadr that launched it can be told.");
            await WaitUntilAsync(() => !File.Exists(paths.LaunchedCredentials), "what it was started with to be forgotten once the report is taken");
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task AContainerOffloadrReleased_FoundStoppedAsTheAppStarts_IsNotReportedGone()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var first = await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            var launch = await LaunchAsync();
            first.KeepRunnerOnShutdown();
            await first.StopAsync(CancellationToken.None);
            _supervisor = null;
            first.Dispose();

            // Offloadr had granted its release, and it stopped (its agent exits once fenced) before the
            // app could clear the note.
            File.WriteAllText(paths.ReleasedLaunch, launch.LaunchId);
            _engine.Exit(Name, 4);
            await StartSupervisorAsync(paths: paths);

            await WaitUntilAsync(() => !_engine.Exists(Name), "the stopped container to be removed");
            await WaitUntilAsync(() => !File.Exists(paths.LaunchedContainers) && !File.Exists(paths.ReleasedLaunch), "its record and the note to go");
            await Task.Delay(300);
            Assert.That(_link.ExitReports, Is.Empty, "Offloadr released it: it knows, and nothing is reported.");
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task AReleasedRunnerFoundByItsLaunchAsTheAppStarts_IsStoppedOnce_AndNotTakenUpAgain()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var first = await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            var launch = await LaunchAsync();
            first.KeepRunnerOnShutdown();
            await first.StopAsync(CancellationToken.None);
            _supervisor = null;
            first.Dispose();

            // Released, and renamed while the app was closed: only its launch finds it.
            File.WriteAllText(paths.ReleasedLaunch, launch.LaunchId);
            _engine.Rename(Name, "renamed-runner");
            var second = await StartSupervisorAsync(paths: paths);

            await WaitUntilAsync(() => !_engine.Exists("renamed-runner"), "the released container to be stopped");
            await Task.Delay(400);
            Assert.Multiple(() =>
            {
                Assert.That(second.View.State, Is.Not.EqualTo(SupervisorState.Running), "Stopped, not taken up again from the look that found it.");
                Assert.That(second.View.UnexpectedExits, Is.Zero);
                Assert.That(_link.ExitReports, Is.Empty, "Offloadr released it: nothing is reported.");
            });
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task AContainerOffloadrReleased_StoppedAsTheAppStarts_IsNotReportedGone()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var first = await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            var launch = await LaunchAsync();
            first.KeepRunnerOnShutdown();
            await first.StopAsync(CancellationToken.None);
            _supervisor = null;
            first.Dispose();

            // Offloadr had granted its release, and the app closed before it stopped the container.
            File.WriteAllText(paths.ReleasedLaunch, launch.LaunchId);
            await StartSupervisorAsync(paths: paths);

            await WaitUntilAsync(() => !_engine.Exists(Name), "the released container to be stopped");
            await WaitUntilAsync(() => !File.Exists(paths.LaunchedContainers), "its record to go");
            await Task.Delay(300);
            Assert.That(_link.ExitReports, Is.Empty, "Offloadr released it: it knows, and nothing is reported.");
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task AStopWhileAFoundRunnerIsBeingAdopted_StopsIt()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var first = await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            var launch = await LaunchAsync();
            first.KeepRunnerOnShutdown();
            await first.StopAsync(CancellationToken.None);
            _supervisor = null;
            first.Dispose();

            // Renamed while the app was closed, and found by its launch only once Docker answers: it is
            // being taken under supervision (Offloadr's catalog is asked for, which hangs) when Stop is pressed.
            _engine.Rename(Name, "renamed-runner");
            _engine.FindByLabelFailures = int.MaxValue;
            var second = await StartSupervisorAsync(paths: paths);
            await Task.Delay(300);
            var adopting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _plans.OnGet = async token =>
            {
                adopting.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            };
            _engine.FindByLabelFailures = 0;
            await adopting.Task.WaitAsync(WaitLimit);

            second.RequestStop();

            await WaitUntilAsync(() => !_engine.Exists("renamed-runner"), "the runner being adopted to be stopped");
            await WaitForStateAsync(SupervisorState.Stopped);
            Assert.That(_link.Releases.Select(release => release.LaunchId), Does.Contain(launch.LaunchId), "Released before it is stopped.");
        }
        finally
        {
            _plans.OnGet = null;
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task TwoRunnersFoundRunningAtOnce_AreBothStopped_WhenTheAppCloses()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var first = Guid.NewGuid().ToString();
            var second = Guid.NewGuid().ToString();
            var target = FakeLauncherLink.TargetOf(_settings.Current);
            var engine = ContainerEngineSelection.Describe(_selection.Current);

            // Two runners of this app still run, neither under the runner's name (both were renamed): only
            // a look by their launches finds them, both in the same look.
            AddLaunchedContainer(first);
            _engine.Rename(Name, "renamed-one");
            AddLaunchedContainer(second);
            _engine.Rename(Name, "renamed-two");
            File.WriteAllText(paths.LaunchedContainers, $"running {first} {target} {engine}\nrunning {second} {target} {engine}");

            // Offloadr's catalog hangs, so the one taken up first is still being adopted when the app closes.
            var adopting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _plans.OnGet = async token =>
            {
                adopting.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            };
            var supervisor = await StartSupervisorAsync(paths: paths);
            await adopting.Task.WaitAsync(WaitLimit);

            await supervisor.StopAsync(CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(_engine.Exists("renamed-one"), Is.False);
                Assert.That(_engine.Exists("renamed-two"), Is.False, "Both hold the runner key: every runner known to run is stopped.");
            });
        }
        finally
        {
            _plans.OnGet = null;
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task ARenamedStoppedRunnerSeenRunningAsItIsRemoved_IsNotReportedGone()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var first = await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            await LaunchAsync();
            first.KeepRunnerOnShutdown();
            await first.StopAsync(CancellationToken.None);
            _supervisor = null;
            first.Dispose();

            // Renamed and stopped while the app was closed; started again as the look removes it, which
            // the engine refuses for a container that runs, and then the engine stops answering.
            _engine.Rename(Name, "renamed-runner");
            _engine.Exit("renamed-runner", 0);
            _engine.BeforeNextRemoveStopped = () =>
            {
                _engine.Restart("renamed-runner");
                _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
            };
            var second = await StartSupervisorAsync(paths: paths);

            await WaitUntilAsync(() => _engine.InspectFailure is not null, "the look");
            await Task.Delay(300);
            Assert.That(_link.ExitReports, Is.Empty, "The engine saw it run: it is not gone.");

            _engine.InspectFailure = null;
            await WaitForStateAsync(SupervisorState.Running);
            Assert.Multiple(() =>
            {
                Assert.That(second.View.State, Is.EqualTo(SupervisorState.Running), "Supervised again.");
                Assert.That(_link.ExitReports, Is.Empty);
                Assert.That(_engine.Exists("renamed-runner"), Is.True);
            });
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task AGoneContainerWhoseLookupFailedAtTheStart_IsReportedOnceItCanBeLookedFor()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var first = await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            var launch = await LaunchAsync();
            first.KeepRunnerOnShutdown();
            await first.StopAsync(CancellationToken.None);
            _supervisor = null;
            first.Dispose();
            await _engine.RemoveAsync(Name, CancellationToken.None);

            // Docker hiccups as the next app start looks for it; the Start that follows looks again.
            _engine.FindByLabelFailures = 1;
            await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();

            await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the exit report once it could be looked for");
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task AStoppedContainerFoundByItsLaunchAlone_IsReportedGoneAndRemoved()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var first = await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            var launch = await LaunchAsync();
            first.KeepRunnerOnShutdown();
            await first.StopAsync(CancellationToken.None);
            _supervisor = null;
            first.Dispose();

            // Renamed, then stopped, while the app was closed: it still holds the runner key.
            _engine.Rename(Name, "renamed-runner");
            _engine.Exit("renamed-runner", 0);
            await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();

            await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the exit report");
            await WaitUntilAsync(() => !_engine.Exists("renamed-runner"), "its removal");
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task AContainerStartedOnAnotherEngine_IsNotReportedGone_UntilThatEngineSaysSo()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            var launchId = Guid.NewGuid().ToString();
            var target = FakeLauncherLink.TargetOf(_settings.Current);

            // Started on an engine that does not answer now (another daemon): it may still serve a session.
            File.WriteAllText(paths.LaunchedContainers, $"running {launchId} {target} 0123456789ABCDEF");
            await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            await Task.Delay(300);

            Assert.Multiple(() =>
            {
                Assert.That(_link.ExitReports, Is.Empty, "An empty daemon proves nothing about another one.");
                Assert.That(File.ReadAllText(paths.LaunchedContainers), Does.Contain(launchId));
            });
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task AStoppedContainerTheEngineWouldNotRemove_IsStillReportedGone()
    {
        _engine.StatusJson = Status(activeSessionId: "session-1");
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var launch = await LaunchAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, true, "A session is open.");
        _engine.NextRemoveFailure = new ContainerEngineException("Could not remove container offloadr-runner.");

        supervisor.RequestStop();

        await WaitForStateAsync(SupervisorState.Stopped);
        Assert.That(_link.ExitReports.Select(report => report.LaunchId), Is.EqualTo(new[] { launch.LaunchId }),
            "Stopped: its session ended here, whatever became of the removal.");
    }

    [Test]
    public async Task AGoneReportOffloadrRefusesForItsExecutor_IsSentAgain_NamingTheLaunchAlone()
    {
        _engine.StatusJson = Status(activeSessionId: "session-1");
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        var launch = await LaunchAsync();
        await WaitUntilAsync(() => _supervisor!.View.RunnerStatus?.InstanceId is not null, "the executor's status");

        // The agent restarted inside the container: the instance read from its status is not the one
        // registered for the launch, and Offloadr refuses a report that names it.
        _link.ExitReportAnswer = _ => _link.ExitReports.Last().ExecutorInstanceId is null
            ? new ReleaseResult(true, true, string.Empty)
            : new ReleaseResult(false, true, "The executor instance is registered under another launch.");

        _engine.Exit(Name, 1);

        await WaitUntilAsync(() => _link.ExitReports.Count(report => report.LaunchId == launch.LaunchId) >= 2, "the report to be sent again");
        var reports = _link.ExitReports.Where(report => report.LaunchId == launch.LaunchId).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(reports[0].ExecutorInstanceId, Is.Not.Null);
            Assert.That(reports[^1].ExecutorInstanceId, Is.Null, "The launch is what names the container: asked again by it alone.");
        });
        await Task.Delay(300);
        Assert.That(_link.ExitReports.Count(report => report.LaunchId == launch.LaunchId), Is.EqualTo(reports.Count), "Taken: not sent a third time.");
    }

    [Test]
    public async Task AGoneReportWhoseCallWasRefused_IsKeptAndSentAgain()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        var answers = 0;
        _link.ExitReportAnswer = _ => Interlocked.Increment(ref answers) == 1
            ? new ReleaseResult(false, true, "Offloadr refused the release (PermissionDenied).") { Answered = false }
            : new ReleaseResult(true, true, string.Empty);

        _engine.Exit(Name, 1);

        await WaitUntilAsync(() => _link.ExitReports.Count >= 2, "the report sent again after a refused call");
    }

    [Test]
    public async Task AnIdleRelease_IsNotSent_OnceANewKeyIsSaved()
    {
        const string newKey = "another-runner-key-0123456789abcdefghijklmnop";
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromMilliseconds(100));
        await MakeAvailableAsync();
        await LaunchAsync();
        _link.BeforeNextRelease = () => _settings.SaveAsync(_settings.Current with { RunnerKey = newKey }).GetAwaiter().GetResult();

        await WaitUntilAsync(() => _link.BeforeNextRelease is null, "the idle release about to be sent");
        await Task.Delay(300);

        Assert.Multiple(() =>
        {
            Assert.That(_link.Releases, Is.Empty, "A container of the earlier key is never stopped automatically.");
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(_engine.Stops, Is.Zero);
        });
    }

    [Test]
    public async Task AGoneReportOffloadrDidNotAnswer_IsSentAgain()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        var answers = 0;
        _link.ExitReportAnswer = _ => Interlocked.Increment(ref answers) == 1
            ? new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).")
            : new ReleaseResult(true, true, string.Empty);

        _engine.Exit(Name, 1);

        await WaitUntilAsync(() => _link.ExitReports.Count >= 2, "the report sent again");
    }

    [Test]
    public async Task AnExitedContainerTheEngineWillNotRemove_IsRemovedLater()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        _engine.BeforeNextRemoveStopped = () => throw new ContainerEngineException("Could not remove container offloadr-runner.");

        _engine.Exit(Name, 1);

        await WaitForStateAsync(SupervisorState.Available);
        await WaitUntilAsync(() => !_engine.Exists(Name), "the removal retried");
    }

    [Test]
    public async Task AStoppedContainerTheEngineKept_IsRemovedAfterARestart()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);
            _engine.StatusJson = Status(activeSessionId: "");
            var first = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1), paths: paths);
            await MakeAvailableAsync();
            await LaunchAsync();

            // The engine keeps refusing to remove it until the app restarts (every attempt, also two at once).
            _engine.RemoveStoppedFailure = new ContainerEngineException("Could not remove container offloadr-runner.");
            _engine.Exit(Name, 1);
            await WaitUntilAsync(() => File.Exists(paths.LeftoverContainers), "the leftover noted");

            // The app closes before it could remove it; the next one does.
            await first.StopAsync(CancellationToken.None);
            _supervisor = null;
            first.Dispose();
            Assert.That(_engine.Exists(Name), Is.True);
            _engine.RemoveStoppedFailure = null;
            await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();

            await WaitUntilAsync(() => !_engine.Exists(Name) && !File.Exists(paths.LeftoverContainers), "the leftover removed");
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task ALeftoverContainer_IsRemovedWhileTheRunnerIsStopped()
    {
        var data = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(data);
        try
        {
            var paths = new SupervisorPaths(data);

            // Stopped before the app closed, but the engine kept it; it still holds the runner key.
            AddLaunchedContainer(Guid.NewGuid().ToString());
            _engine.Rename(Name, "kept-runner");
            _engine.Exit("kept-runner", 0);
            File.WriteAllText(paths.LeftoverContainers, $"{ContainerEngineSelection.Describe(_selection.Current)} kept-runner");

            var supervisor = await StartSupervisorAsync(paths: paths);

            await WaitUntilAsync(() => !_engine.Exists("kept-runner") && !File.Exists(paths.LeftoverContainers), "its removal");
            Assert.Multiple(() =>
            {
                Assert.That(supervisor.View.State, Is.Not.EqualTo(SupervisorState.Available));
                Assert.That(_link.Runs, Is.Empty, "Removed with no launcher registered for it.");
            });
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    public async Task AnExitedContainerTheEngineWillNotRemove_IsStillRecordedAsExited()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        _engine.BeforeNextRemoveStopped = () => throw new ContainerEngineException("Could not remove container offloadr-runner.");

        _engine.Exit(Name, 1);

        await WaitForStateAsync(SupervisorState.Available);
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.View.UnexpectedExits, Is.EqualTo(1));
            Assert.That(supervisor.LogLines, Has.Some.Contains("stays on the container engine"));
        });
    }

    [Test]
    public async Task AfterAStopThatFailed_AStartForAnotherEngine_MovesOn()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);

            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
            _selection = new ContainerEngineSelection(Locate, statePath);
            var supervisor = await StartSupervisorAsync();
            await MakeAvailableAsync();
            await LaunchAsync();

            // Engine A stops answering, so the Stop fails: the container may still run there.
            _engine.UnavailableEngines["docker-a"] = true;
            supervisor.RequestStop();
            await WaitForStateAsync(SupervisorState.Faulted);

            // The person points Settings at engine B, which works, and presses Start.
            await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
            supervisor.RequestStart();

            await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Available && _link.Running, "the computer to be offered on the other engine");
            Assert.Multiple(() =>
            {
                Assert.That(supervisor.LogLines, Has.Some.Contains("Starting on the engine in Settings, as asked"));
                Assert.That(_link.ExitReports, Is.Empty, "Engine A did not say the container is gone: nothing is told of it.");
                Assert.That(_engine.ExistsOn("docker-a", Name), Is.True);
            });
        }
        finally
        {
            DeleteQuietly(statePath);
        }
    }

    [Test]
    public Task ARunnerLeftOnAnEarlierEngine_HasItsReleaseNoted_BeforeItIsStopped()
        => WithDataFolderAsync(async paths =>
        {
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);

            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
            _selection = new ContainerEngineSelection(Locate, statePath: null);
            var supervisor = await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            var first = await LaunchAsync();
            _engine.UnavailableEngines["docker-a"] = true;
            supervisor.RequestStop();
            await WaitForStateAsync(SupervisorState.Faulted);
            await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
            supervisor.RequestStart();
            await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Available && _link.Running, "the computer to be offered on the other engine");
            await LaunchAsync();
            _engine.UnavailableEngines.TryRemove("docker-a", out _);
            await Task.Delay(500);

            var atStops = new List<string>();
            void Capture()
            {
                lock (atStops)
                {
                    atStops.Add(File.ReadAllText(paths.LaunchedContainers));
                }

                _engine.BeforeStop = Capture;
            }

            _engine.BeforeStop = Capture;
            await supervisor.StopAsync(CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(_engine.ExistsOn("docker-a", Name), Is.False);
                Assert.That(atStops, Has.Some.Contains($"released {first.LaunchId} "), "Should the app end before it is stopped, it is never taken up again.");
            });
        });

    [Test]
    public Task AContainerAnUninstallHadReleased_AndCouldNotStop_IsStopped_NotTakenUpAgain()
        => WithDataFolderAsync(async paths =>
        {
            var launchId = Guid.NewGuid().ToString();
            AddLaunchedContainer(launchId);
            var engine = ContainerEngineSelection.Describe(new ContainerEngineSelection.Selected(null, null));
            File.WriteAllText(paths.LaunchedContainers, $"released {launchId} {FakeLauncherLink.TargetOf(_settings.Current)} {engine}");

            await StartSupervisorAsync(paths: paths);

            await WaitUntilAsync(() => !_engine.Exists(Name), "the released container to be stopped");
            await WaitUntilAsync(() => !File.Exists(paths.LaunchedContainers), "its record to go");
            Assert.That(_link.ExitReports, Is.Empty, "Offloadr released it: it knows.");
        });

    [Test]
    public Task AReleasedContainerThatIsGone_IsNotReported()
        => WithDataFolderAsync(async paths =>
        {
            var launchId = Guid.NewGuid().ToString();
            var engine = ContainerEngineSelection.Describe(new ContainerEngineSelection.Selected(null, null));
            File.WriteAllText(paths.LaunchedContainers, $"released {launchId} {FakeLauncherLink.TargetOf(_settings.Current)} {engine}");

            await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();

            await WaitUntilAsync(() => !File.Exists(paths.LaunchedContainers), "its record to go");
            Assert.That(_link.ExitReports, Is.Empty, "Offloadr released it: it knows.");
        });

    [Test]
    public Task AContainerWhoseReleaseWasAskedBeforeACrash_IsAskedAgain_OnceItIsTakenUp()
        => WithDataFolderAsync(async paths =>
        {
            _engine.StatusJson = Status(activeSessionId: "");
            var launchId = Guid.NewGuid().ToString();
            AddLaunchedContainer(launchId);
            var engine = ContainerEngineSelection.Describe(new ContainerEngineSelection.Selected(null, null));
            File.WriteAllText(paths.LaunchedContainers, $"asking {launchId} {FakeLauncherLink.TargetOf(_settings.Current)} {engine}");

            var supervisor = await StartSupervisorAsync(paths: paths, warmTime: TimeSpan.FromHours(1));
            supervisor.RequestStart();

            await WaitUntilAsync(() => _link.Releases.Any(release => release.LaunchId == launchId), "the release to be asked again");
            await WaitUntilAsync(() => !_engine.Exists(Name), "the container to be stopped");
        });

    [Test]
    public Task AContainerOneLookRemoves_IsNotTakenForGone_ByAnotherThatRunsMeanwhile()
        => WithDataFolderAsync(async paths =>
        {
            // A start the engine refused, before a crash, on an engine that did not answer as the app
            // started. Once it answers, maintenance and a Start both look for it: the one that removes it
            // saw that it never ran, and is slow to say so; the other finds nothing.
            var launchId = Guid.NewGuid().ToString();
            AddLaunchedContainer(launchId);
            _engine.MarkNeverStarted(Name);
            _engine.Rename(Name, "renamed-runner");
            var engine = ContainerEngineSelection.Describe(new ContainerEngineSelection.Selected(null, null));
            File.WriteAllText(paths.LaunchedContainers, $"refused {launchId} {FakeLauncherLink.TargetOf(_settings.Current)} {engine}");
            var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var say = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _engine.OnNextRemovedStopped = async () =>
            {
                removed.TrySetResult();
                await say.Task;
            };

            _engine.FindByLabelFailures = int.MaxValue;
            var supervisor = await StartSupervisorAsync(paths: paths);
            await Task.Delay(300);
            _engine.FindByLabelFailures = 0;
            await removed.Task.WaitAsync(WaitLimit);
            supervisor.RequestStart();
            await Task.Delay(500);
            say.TrySetResult();

            await WaitUntilAsync(() => !File.Exists(paths.LaunchedContainers), "its record to go");
            await Task.Delay(200);
            Assert.That(_link.ExitReports, Is.Empty, "It ran nothing: an exit report would cost the allocation a retry.");
        });

    [Test]
    public Task AGrantForAContainerOnAnEngineThePersonMovedOnFrom_IsKept_ThroughAReleaseOnTheOtherEngine()
        => WithDataFolderAsync(async paths =>
        {
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);

            _engine.StatusJson = Status(activeSessionId: "");
            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
            _selection = new ContainerEngineSelection(Locate, statePath: null);
            var supervisor = await StartSupervisorAsync(paths: paths, warmTime: TimeSpan.FromMilliseconds(300));
            _link.ReleaseAnswer = _ => new ReleaseResult(false, true, "A session is assigned.");
            await MakeAvailableAsync();
            var first = await LaunchAsync();

            // Offloadr grants its release as the Stop asks for it, and engine A stops answering right then.
            _link.ReleaseAnswer = _ =>
            {
                _engine.UnavailableEngines["docker-a"] = true;
                return new ReleaseResult(true, true, string.Empty);
            };
            supervisor.RequestStop();
            await WaitForStateAsync(SupervisorState.Faulted);

            // The person moves on to engine B, where a container is started, and released once idle.
            _link.ReleaseAnswer = _ => new ReleaseResult(true, true, string.Empty);
            await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
            supervisor.RequestStart();
            await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Available && _link.Running, "the computer to be offered on the other engine");
            var second = await LaunchAsync();
            await WaitUntilAsync(() => _link.Releases.Any(release => release.LaunchId == second.LaunchId) && !_engine.ExistsOn("docker-b", Name), "the release on the other engine");

            Assert.That(File.ReadAllText(paths.LaunchedContainers), Does.Contain($"released {first.LaunchId} "),
                "Offloadr released the one on engine A: it is stopped when that engine answers, and never taken up again.");
            var asked = _link.Releases.Count(release => release.LaunchId == first.LaunchId);
            _engine.UnavailableEngines.TryRemove("docker-a", out _);
            await WaitUntilAsync(() => !_engine.ExistsOn("docker-a", Name), "it to be stopped");
            Assert.That(_link.ExitReports.Any(report => report.LaunchId == first.LaunchId), Is.False);
            Assert.That(_link.Releases.Count(release => release.LaunchId == first.LaunchId), Is.EqualTo(asked), "Stopped, not supervised and released again.");
        });

    [Test]
    public async Task AnOutcomeAStopKeptBack_IsNotSubmittedLater_ByTheLauncherThatStaysForIt()
    {
        var models = Path.Combine(Path.GetTempPath(), "offloadr-models-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(models);
        try
        {
            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, LocalModelsDirectory = models });
            _engine.StatusJson = Status(activeSessionId: "");
            var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
            await MakeAvailableAsync();
            await LaunchAsync();

            // The models folder goes (a drive removed, say); Stop is pressed as the failure that causes is
            // submitted. The Stop asks for the warm container's release, so the launcher stays meanwhile.
            _link.OnRelease = token => Task.Delay(300, token);
            Directory.Delete(models);
            _link.OnNextOutcomeSubmitted = supervisor.RequestStop;

            var second = _link.Name("forge-neo:neo", ForgeNeo);

            await WaitForStateAsync(SupervisorState.Stopped);
            await Task.Delay(200);
            Assert.That(_link.OutcomeOf(second.LaunchId), Is.Null, "A Stop is no outcome Offloadr asked for.");
        }
        finally
        {
            if (Directory.Exists(models))
            {
                Directory.Delete(models);
            }
        }
    }

    [Test]
    public Task AContainerSeenCreatedAndRemovedByALook_StaysSettled_ThoughALaterLookFails()
        => WithDataFolderAsync(async paths =>
        {
            // Two starts the engine refused, before a crash. The look removes the first (seen created);
            // the engine then does not answer the look for the second.
            var (first, second) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
            AddLaunchedContainer(first);
            _engine.MarkNeverStarted(Name);
            _engine.Rename(Name, "renamed-runner");
            var engine = ContainerEngineSelection.Describe(new ContainerEngineSelection.Selected(null, null));
            var target = FakeLauncherLink.TargetOf(_settings.Current);
            File.WriteAllLines(paths.LaunchedContainers, [$"refused {first} {target} {engine}", $"refused {second} {target} {engine}"]);
            _engine.BeforeNextFindByLabel = () => _engine.BeforeNextFindByLabel = () => _engine.FindByLabelFailures = 1;

            await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();

            await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == second), "the second, not there to say, to be told as gone");
            await Task.Delay(300);
            Assert.Multiple(() =>
            {
                Assert.That(_engine.Exists("renamed-runner"), Is.False);
                Assert.That(_link.ExitReports.Any(report => report.LaunchId == first), Is.False, "It was seen created after a refused start: it ran nothing.");
            });
        });

    [Test]
    public async Task ARunnerLeftOnAnEarlierEngine_IsStoppedToo_WhenTheAppCloses()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);

            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
            _selection = new ContainerEngineSelection(Locate, statePath);
            var supervisor = await StartSupervisorAsync();
            await MakeAvailableAsync();
            await LaunchAsync();

            // Engine A stops answering; the person moves on to engine B, and a session starts there.
            _engine.UnavailableEngines["docker-a"] = true;
            supervisor.RequestStop();
            await WaitForStateAsync(SupervisorState.Faulted);
            await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
            supervisor.RequestStart();
            await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Available && _link.Running, "the computer to be offered on the other engine");
            await LaunchAsync();
            Assert.That(_engine.ExistsOn("docker-b", Name), Is.True);

            // Engine A answers again while B's runner is supervised: its runner still runs there.
            _engine.UnavailableEngines.TryRemove("docker-a", out _);
            await Task.Delay(500);

            await supervisor.StopAsync(CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(_engine.ExistsOn("docker-b", Name), Is.False);
                Assert.That(_engine.ExistsOn("docker-a", Name), Is.False, "It holds the runner key too: quitting stops every runner this app knows to run.");
            });
        }
        finally
        {
            DeleteQuietly(statePath);
        }
    }

    [Test]
    public async Task ALaunchWhoseContainerIsOnAnEngineThePersonMovedOnFrom_IsNotAnswered_UntilThatEngineSays()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);

            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
            _selection = new ContainerEngineSelection(Locate, statePath);
            var supervisor = await StartSupervisorAsync();
            await MakeAvailableAsync();
            _engine.RunOverride = (spec, _) =>
            {
                // The container runs, the start's answer is lost, and engine A stops answering.
                _engine.RunOverride = null;
                _engine.AddContainer(Name, spec.Image, running: true, spec.Labels, spec.Environment);
                _engine.UnavailableEngines["docker-a"] = true;
                throw new ContainerEngineException("Could not start the runner container.", "error during connect: read tcp: connection reset by peer");
            };

            var launch = _link.Name("comfyui-latest", ComfyUi);
            await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("did not say whether", StringComparison.Ordinal)), "the unsettled launch");
            supervisor.RequestStop();
            await WaitForStateAsync(SupervisorState.Faulted);

            // The person moves on to engine B; Offloadr still names the launch (its executor had registered).
            await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
            supervisor.RequestStart();
            await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Available && _link.Running, "the computer to be offered on the other engine");
            _link.NameAgain(launch);
            await Task.Delay(500);

            Assert.Multiple(() =>
            {
                Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "Its container may still run on engine A: nothing is said of its launch until that engine answers.");
                Assert.That(_engine.Runs, Is.Empty, "Nor is it started a second time.");
            });
        }
        finally
        {
            DeleteQuietly(statePath);
        }
    }

    [Test]
    public async Task ARunnerLeftOnAnEngineThatAnswers_IsStopped_ThoughTheSupervisedOnesEngineDoesNot()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);

            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
            _selection = new ContainerEngineSelection(Locate, statePath);
            var supervisor = await StartSupervisorAsync();
            await MakeAvailableAsync();
            await LaunchAsync();

            // Engine A stops answering; the person moves on to engine B, and a session starts there.
            _engine.UnavailableEngines["docker-a"] = true;
            supervisor.RequestStop();
            await WaitForStateAsync(SupervisorState.Faulted);
            await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
            supervisor.RequestStart();
            await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Available && _link.Running, "the computer to be offered on the other engine");
            await LaunchAsync();

            // Engine A answers again (its runner still runs there), and then engine B stops answering.
            _engine.UnavailableEngines.TryRemove("docker-a", out _);
            await Task.Delay(500);
            _engine.UnavailableEngines["docker-b"] = true;

            supervisor.RequestStop();

            await WaitUntilAsync(() => !_engine.ExistsOn("docker-a", Name), "the runner on the engine that answers to be stopped");
            await WaitForStateAsync(SupervisorState.Faulted);
            Assert.That(_engine.ExistsOn("docker-b", Name), Is.True, "Engine B did not answer: its runner stays supervised, and the Stop says so.");
        }
        finally
        {
            DeleteQuietly(statePath);
        }
    }

    [Test]
    public async Task ALaunchWhoseContainerIsGoneFromTheEngineThePersonMovedOnFrom_IsToldAsStarted_NeverAsFailed()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);

            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
            _selection = new ContainerEngineSelection(Locate, statePath);
            var supervisor = await StartSupervisorAsync();
            await MakeAvailableAsync();

            // (Offloadr is not reached for a release as the Stop is tried: it still counts on the container.)
            _link.ReleaseAnswer = _ => new ReleaseResult(false, false, "Could not reach Offloadr (Unavailable).");
            _engine.RunOverride = (spec, _) =>
            {
                // The container runs, the start's answer is lost, and engine A stops answering.
                _engine.RunOverride = null;
                _engine.AddContainer(Name, spec.Image, running: true, spec.Labels, spec.Environment);
                _engine.UnavailableEngines["docker-a"] = true;
                throw new ContainerEngineException("Could not start the runner container.", "error during connect: read tcp: connection reset by peer");
            };

            var launch = _link.Name("comfyui-latest", ComfyUi);
            await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("did not say whether", StringComparison.Ordinal)), "the unsettled launch");
            supervisor.RequestStop();
            await WaitForStateAsync(SupervisorState.Faulted);
            await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
            supervisor.RequestStart();
            await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Available && _link.Running, "the computer to be offered on the other engine");
            _link.NameAgain(launch);

            // Engine A answers again, and the container is no longer there.
            _engine.RemoveOn("docker-a", Name);
            _engine.UnavailableEngines.TryRemove("docker-a", out _);

            await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the container to be told as gone");
            await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started),
                "Its start was asked for and it is not there to say it never ran: never told as failed.");
        }
        finally
        {
            DeleteQuietly(statePath);
        }
    }

    [Test]
    public Task AnExitReportOffloadrDoesNotAnswer_DoesNotHoldUpAnother()
        => WithDataFolderAsync(async paths =>
        {
            var slow = Guid.NewGuid().ToString();
            var quick = Guid.NewGuid().ToString();
            var target = FakeLauncherLink.TargetOf(_settings.Current);
            File.WriteAllText(paths.LaunchedContainers, $"gone {slow} {target} -\ngone {quick} {target} -");

            // The report of the first is never answered (until the app gives up on it).
            var givenUp = 0;
            _link.OnExitReport = async (launchId, token) =>
            {
                if (launchId == slow)
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref givenUp, 1);
                    }
                }
            };

            await StartSupervisorAsync(paths: paths);

            await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == quick), "the other report");
            Assert.That(Volatile.Read(ref givenUp), Is.Zero, "Sent on its own, not after the first was given up.");
        });

    [Test]
    public async Task AStopThatFailed_StillStopsARunnerFoundLater_OnAnEngineThatAnswers()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);

            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
            _selection = new ContainerEngineSelection(Locate, statePath);
            var supervisor = await StartSupervisorAsync();
            await MakeAvailableAsync();
            await LaunchAsync();

            // Engine A stops answering; the person moves on to engine B, and a session starts there.
            _engine.UnavailableEngines["docker-a"] = true;
            supervisor.RequestStop();
            await WaitForStateAsync(SupervisorState.Faulted);
            await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
            supervisor.RequestStart();
            await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Available && _link.Running, "the computer to be offered on the other engine");
            await LaunchAsync();

            // Engine B stops answering too, and the Stop fails; only afterwards does engine A answer again.
            _engine.UnavailableEngines["docker-b"] = true;
            supervisor.RequestStop();
            await WaitForStateAsync(SupervisorState.Faulted);
            Assert.That(_engine.ExistsOn("docker-a", Name), Is.True);
            _engine.UnavailableEngines.TryRemove("docker-a", out _);

            await WaitUntilAsync(() => !_engine.ExistsOn("docker-a", Name), "the runner found afterwards to be stopped");
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Faulted), "Engine B's runner could still not be stopped: that stays said.");
        }
        finally
        {
            DeleteQuietly(statePath);
        }
    }

    [Test]
    public Task ClosingTheApp_EndsWhatIsUnderWay_AtItsDeadline()
        => WithDataFolderAsync(async paths =>
        {
            var supervisor = await StartSupervisorAsync(paths: paths, shutdownMargin: TimeSpan.FromMilliseconds(500));
            await MakeAvailableAsync();
            var launch = await LaunchAsync();

            // Docker does not return from the stop.
            _engine.StopDuration = TimeSpan.FromMinutes(5);
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            await supervisor.StopAsync(CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(System.Diagnostics.Stopwatch.GetElapsedTime(began), Is.LessThan(TimeSpan.FromSeconds(8)), "Within its own deadline, not ended from outside with the cleanup still running.");
                Assert.That(supervisor.ExecuteTask?.IsCompletedSuccessfully, Is.True);
                Assert.That(File.ReadAllText(paths.LaunchedContainers), Does.Contain(launch.LaunchId), "What became of it is not known: looked for at the next start.");
            });
        });

    [Test]
    public Task ClosingTheApp_TellsOfAContainerItStopped_ThoughItsRemovalHangs()
        => WithDataFolderAsync(async paths =>
        {
            var supervisor = await StartSupervisorAsync(paths: paths, shutdownMargin: TimeSpan.FromMilliseconds(900));
            await MakeAvailableAsync();
            var launch = await LaunchAsync();
            _link.ReleaseAnswer = _ => new ReleaseResult(false, true, "A session is assigned.");

            // It stops, and Docker does not return from its removal.
            _engine.RemovalHangs = true;
            await supervisor.StopAsync(CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(_link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), Is.True, "It serves nothing any more, and this app may never run again to say so.");
                Assert.That(File.Exists(paths.LaunchedContainers), Is.False, "Told.");
                Assert.That(File.Exists(paths.LeftoverContainers), Is.True, "It still holds the runner key: removed at the next start.");
            });
        });

    [Test]
    public async Task AGpuCheck_TriesTheOtherImages_WhenOneCannotUseTheGpu()
    {
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid)], null);

        // Another editor's image is here, and needs a newer driver than there is; the one for the editor in
        // Settings is not here yet.
        _engine.AddImage(ForgeNeo);
        _engine.ImageIds[ForgeNeo] = "sha256:forge";
        _engine.GpuCheckOf = image => image == "sha256:forge" ? new GpuAccessCheck(false, "nvidia-container-cli: requirement error: unsatisfied condition: cuda>=13.0") : null;

        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();

        await WaitUntilAsync(() => supervisor.View.IsReady && _link.Running, "the computer to be offered");
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Pulls, Does.Contain(ComfyUi), "Downloaded to find out: the failure was of the one image.");
            Assert.That(_engine.GpuCheckImages, Is.EqualTo(new[] { "sha256:forge", "sha256:local" }));
        });
    }

    [Test]
    public async Task AGpuCheck_DownloadsNothing_WhenTheEngineCannotAttachTheGpuAtAll()
    {
        _engine.AddImage(ForgeNeo);
        _engine.GpuCheck = new GpuAccessCheck(false, "docker: Error response from daemon: could not select device driver \"\" with capabilities: [[gpu]].");

        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();

        await WaitForStateAsync(SupervisorState.Faulted);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Pulls, Is.Empty, "No image would get it.");
            Assert.That(supervisor.View.Fault?.Kind, Is.EqualTo(FaultKind.GpuUnavailable));
        });
    }

    [Test]
    public async Task AStopPressedWhileAReleaseIsAsked_ReadsNothingFromDisk_OnTheThreadThatAsks()
    {
        // (Finding the engine's executable looks at files: what a stalled drive would hold up.)
        var asking = 0;
        var looked = 0;
        LocatedEngine? Locate(string? configured)
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref asking))
            {
                Interlocked.Increment(ref looked);
            }

            return new LocatedEngine(configured ?? "docker", ContainerEngineKind.Docker);
        }

        _selection = new ContainerEngineSelection(Locate, statePath: null);
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _link.OnRelease = token =>
        {
            released.TrySetResult();
            return Task.Delay(300, token);
        };
        _link.Name("forge-neo:neo", ForgeNeo);
        await released.Task.WaitAsync(WaitLimit);

        var pressing = new Thread(() =>
        {
            Volatile.Write(ref asking, Environment.CurrentManagedThreadId);
            supervisor.RequestStop();
            Volatile.Write(ref asking, 0);
        });
        pressing.Start();
        pressing.Join();

        Assert.That(looked, Is.Zero, "The Stop control would freeze with a drive that does not answer.");
        await WaitForStateAsync(SupervisorState.Stopped);
    }

    [Test]
    public Task AStoppedContainerTheEngineKept_StaysNoted_WhileItRunsAgain_AndIsRemovedOnceItStopped()
        => WithDataFolderAsync(async paths =>
        {
            var engine = ContainerEngineSelection.Describe(new ContainerEngineSelection.Selected(null, null));
            _engine.AddContainer("renamed-runner", ComfyUi, running: true, new Dictionary<string, string>());
            File.WriteAllText(paths.LeftoverContainers, $"{engine} renamed-runner");

            var supervisor = await StartSupervisorAsync(paths: paths);
            await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("started again outside the app", StringComparison.Ordinal)), "it to be seen running");
            await Task.Delay(300);
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(paths.LeftoverContainers), Is.True, "It still holds the runner key, and nothing else would look for it.");
                Assert.That(_engine.Exists("renamed-runner"), Is.True, "Never removed by force.");
                Assert.That(supervisor.LogLines.Count(line => line.Contains("started again outside the app", StringComparison.Ordinal)), Is.EqualTo(1), "Said once.");
            });

            _engine.Exit("renamed-runner", 0);
            await WaitUntilAsync(() => !_engine.Exists("renamed-runner"), "it to be removed");
            await WaitUntilAsync(() => !File.Exists(paths.LeftoverContainers), "its note to go");
        });

    [Test]
    public async Task AnotherGpuSavedRightBeforeTheContainerIsStarted_HoldsTheStartBack()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        // Saved after the launch's last checks, right before the engine's process is started.
        _engine.BeforeRun = () => _settings.SaveAsync(_settings.Current with { GpuUuid = "GPU-bbbbbbbb-1111-2222-3333-444444444444" }).GetAwaiter().GetResult();

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _engine.BeforeRun is null, "the start to be asked for");
        await Task.Delay(500);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty, "Not started with the GPU chosen before.");
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.Not.EqualTo(RunnerLaunchPhase.Started));
            Assert.That(_engine.Exists(Name), Is.False, "What was created goes.");
            Assert.That(_link.ExitReports, Is.Empty, "It never ran.");
        });
    }

    [Test]
    public async Task AnUnrelatedSettingSavedRightBeforeTheContainerIsStarted_StartsItAllTheSame()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.BeforeRun = () => _settings.SaveAsync(_settings.Current with { AutoStart = !_settings.Current.AutoStart }).GetAwaiter().GetResult();

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started));
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ALauncherThatIsStillEnding_OffersTheEditorsOfItsOwnRunner()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        _plans.Plan = ControlPlanePlan("comfyui-latest") with { RefreshInterval = TimeSpan.FromMilliseconds(50) };
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        await LaunchAsync();
        var before = _link.Profile!().EditorTemplates;
        var target = _link.LaunchTarget;
        var releasing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _link.OnRelease = async _ =>
        {
            releasing.TrySetResult();
            await finish.Task;
        };
        _link.Name("forge-neo:neo", ForgeNeo);
        await releasing.Task.WaitAsync(WaitLimit);

        // Another key is saved, and its catalog arrives, while the old run's release is under way.
        _plans.Plan = new LaunchPlan([new LaunchTemplate("other-editor", "other", "ghcr.io/offloadr/runner-other:latest", "Other")], "other-editor", TimeSpan.FromMinutes(5), LaunchPlanSource.ControlPlane);
        await _settings.SaveAsync(_settings.Current with { RunnerKey = "another-runner-key-0123456789abcdefghijklmnop" });
        await Task.Delay(400);

        try
        {
            if (_link.Running && _link.LaunchTarget == target)
            {
                Assert.That(_link.Profile!().EditorTemplates, Does.Not.Contain("other-editor"), "Offered under the old runner, Offloadr could give it a launch for an editor it never listed.");
            }
            else
            {
                Assert.Fail("The old run ended before the catalog arrived: nothing was shown.");
            }
        }
        finally
        {
            finish.TrySetResult();
        }

        Assert.That(before, Does.Contain("comfyui-latest"));
    }

    [Test]
    public async Task AContainerStartedOutsideTheAppThatExited_IsNoFailedLaunch_WhenItsLaunchEndsForOtherSettings()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        // Created, and waiting for the stream before it is started. Someone starts it, and it exits again.
        _engine.OnCreate = (_, _) =>
        {
            _link.SetStreamLive(false);
            return Task.CompletedTask;
        };
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => _engine.Exists(Name), "the container to be created");
        await Task.Delay(100);
        _engine.OnInspect = name =>
        {
            _engine.OnInspect = null;
            _engine.Restart(Name);
            _engine.Exit(Name, 1);
        };

        // The key changes: the launch ends with it.
        await _settings.SaveAsync(_settings.Current with { RunnerKey = "another-runner-key-0123456789abcdefghijklmnop" });

        await WaitUntilAsync(() => !_engine.Exists(Name), "what was created to be removed");
        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the container to be told as gone");
        Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.Not.EqualTo(RunnerLaunchPhase.Failed), "FAILED would fail the allocation that exit lets launch again.");
    }

    [Test]
    public async Task ALaunchWhoseContainerWasCreated_FailsOnceItIsRemoved_WhenTheKeyChanges()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.OnCreate = (_, _) =>
        {
            _link.SetStreamLive(false);
            return Task.CompletedTask;
        };
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => _engine.Exists(Name), "the container to be created");
        bool? thereWhenTold = null;
        _link.ReportObserver = report => thereWhenTold ??= report.IsTerminal ? _engine.Exists(Name) : null;

        await _settings.SaveAsync(_settings.Current with { RunnerKey = "another-runner-key-0123456789abcdefghijklmnop" });

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(thereWhenTold, Is.False, "Known by then that it never ran.");
            Assert.That(_link.ExitReports, Is.Empty);
        });
    }

    [Test]
    public async Task ARunnersCatalog_IsKeptThroughAnOutage_WhenSettingsNameItAgain()
    {
        _plans.Plan = ControlPlanePlan("comfyui-latest") with { RefreshInterval = TimeSpan.FromMinutes(5) };
        var supervisor = await StartSupervisorAsync();
        supervisor.RequestStart();
        await WaitUntilAsync(() => supervisor.View.IsReady && _link.Running, "the offer");
        Assert.That(_link.Profile!().EditorTemplates, Is.EqualTo(new[] { "comfyui-latest", "comfyui-master" }));

        // Another runner is named, which Offloadr gives another catalog; then the first one again, while
        // Offloadr's catalog cannot be had.
        _plans.Plan = new LaunchPlan([new LaunchTemplate("other-editor", "other", "ghcr.io/offloadr/runner-other:latest", "Other")], "other-editor", TimeSpan.FromMinutes(5), LaunchPlanSource.ControlPlane);
        await _settings.SaveAsync(_settings.Current with { RunnerKey = "another-runner-key-0123456789abcdefghijklmnop" });
        await WaitUntilAsync(() => _link.Running && _link.Profile!().EditorTemplates.Contains("other-editor"), "the other runner's offer");
        _plans.Plan = BuiltInLaunchPlanCatalog.Plan(source: LaunchPlanSource.Unreachable);
        await _settings.SaveAsync(_settings.Current with { RunnerKey = Key });
        await WaitUntilAsync(() => supervisor.View.IsReady && _link.Running && _link.LaunchTarget == FakeLauncherLink.TargetOf(_settings.Current), "the first runner's offer again");

        Assert.Multiple(() =>
        {
            Assert.That(_link.Profile!().EditorTemplates, Is.EqualTo(new[] { "comfyui-latest", "comfyui-master" }), "What Offloadr gave this runner last.");
            Assert.That(supervisor.View.Plan?.Source, Is.EqualTo(LaunchPlanSource.ControlPlane));
        });
    }

    [Test]
    public void APausedOffer_IsNotShownAsReady()
    {
        var view = SupervisorView.Initial(DateTimeOffset.UtcNow) with
        {
            State = SupervisorState.Available,
            OfferPaused = true,
            Launcher = new LauncherLinkState(LauncherConnection.Online, null, true, null),
        };

        Assert.Multiple(() =>
        {
            Assert.That(view.IsReady, Is.False);
            Assert.That(view.StateTitle, Is.EqualTo("Checking…"));
            Assert.That((view with { OfferPaused = false }).StateTitle, Is.EqualTo("Ready for sessions"));
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AFailedLaunchWhoseCreatedContainerTheEngineDoesNotDescribe_IsAnsweredOnceItDoes(bool ran)
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.ExitReportAnswer = _ =>
        {
            _link.Withdraw();
            return new ReleaseResult(true, true, string.Empty);
        };

        // Created, and then a last check fails the launch (containers lost the GPU in a sleep), while the
        // engine stops describing containers.
        _engine.OnCreate = (_, _) =>
        {
            _engine.GpuCheck = new GpuAccessCheck(false, "NVIDIA driver lost after sleep");
            _supervisor!.NotifyResumed();
            return Task.CompletedTask;
        };
        _engine.OnNextGpuCheck = () =>
        {
            _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
            _engine.FindByLabelFailures = int.MaxValue;
            return Task.CompletedTask;
        };
        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _supervisor!.LogLines.Any(line => line.Contains("could not start comfyui-latest", StringComparison.Ordinal)), "the launch to fail");
        await Task.Delay(400);
        Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "It may have been started outside the app: the engine does not say.");

        if (ran)
        {
            _engine.Restart(Name);
            _engine.Exit(Name, 1);
        }

        _engine.InspectFailure = null;
        _engine.FindByLabelFailures = 0;
        if (ran)
        {
            await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the container to be told as gone");
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "FAILED would fail the allocation that exit lets launch again.");
        }
        else
        {
            await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId)?.Phase == RunnerLaunchPhase.Failed, "the launch to fail, once it is known that it never ran");
            Assert.That(_link.ExitReports, Is.Empty);
        }

        Assert.That(_engine.Runs, Is.Empty);
    }

    [Test]
    public async Task AStartWhoseProcessIsSlowToBeCreated_HoldsUpNeitherTheLoopNorTheLink()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        using var creating = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        _engine.BeforeRun = () =>
        {
            creating.Set();
            proceed.Wait(TimeSpan.FromSeconds(8));
        };
        var launch = _link.Name("comfyui-latest", ComfyUi);
        Assert.That(creating.Wait(TimeSpan.FromSeconds(8)), Is.True, "The start is asked for.");

        // The stream drops meanwhile: the link records that, the loop sees it, and the start is held back.
        var dropping = Task.Run(() => _link.SetStreamLive(false));
        try
        {
            await dropping.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() => supervisor.View.Launcher is { StreamLive: false }, "the loop to see it");
        }
        finally
        {
            proceed.Set();
        }

        await Task.Delay(200);
        Assert.That(_engine.Runs, Is.Empty, "Not started while the stream that named its launch is down.");
        _link.SetStreamLive(true);
        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId)?.Phase == RunnerLaunchPhase.Started, "the launch to start once it is back");
        Assert.That(_engine.Runs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ALaunchWithdrawnRightBeforeItsContainerIsStarted_IsNotStarted()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.BeforeRun = _link.Withdraw;

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        await WaitUntilAsync(() => !_engine.Exists(Name), "what was created to be removed");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Cancelled));
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(_link.ExitReports, Is.Empty);
        });
    }

    [Test]
    public async Task ALauncherThatStaysOnlyToDeliverAnOutcome_IsNotShownAsReady()
    {
        var models = Path.Combine(Path.GetTempPath(), "offloadr-models-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(models);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, LocalModelsDirectory = models });
            _engine.StatusJson = Status(activeSessionId: "");
            var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
            await MakeAvailableAsync();
            await LaunchAsync();
            Assert.That(supervisor.View.IsReady, Is.True);

            // The models folder goes, and Offloadr is slow to take the failure that causes.
            Directory.Delete(models);
            _link.OutcomeGate = delivered.Task;
            var second = _link.Name("forge-neo:neo", ForgeNeo);
            await WaitUntilAsync(() => _link.OutcomeOf(second.LaunchId) is not null, "the launch's outcome");
            await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("could not start", StringComparison.Ordinal)) && OffersNothing(), "nothing to be offered");
            await Task.Delay(200);

            Assert.Multiple(() =>
            {
                Assert.That(_link.Running, Is.True, "It stays until the outcome is delivered.");
                Assert.That(supervisor.View.IsReady, Is.False, "No session can start here.");
            });
        }
        finally
        {
            delivered.TrySetResult();
            if (Directory.Exists(models))
            {
                Directory.Delete(models);
            }
        }
    }

    [Test]
    public async Task AContainerJustStarted_IsShownAsUnmatched_UntilOffloadrNoLongerNamesItsLaunch()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

        await WaitUntilAsync(() => supervisor.View.LaunchUnmatched, "its launch to be shown as unmatched");

        // The engine does not answer for a moment, and the stream drops: nothing says that it was matched.
        var revision = _link.Profile!().OfferRevision;
        _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
        await WaitUntilAsync(() => _link.Profile!().EditorTemplates.Count == 0, "no editor to be offered");
        _link.SetStreamLive(false);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Running, Is.True, "The launcher stays: its report may still be on its way.");
            Assert.That(_link.Profile!().OfferRevision, Is.EqualTo(revision), "A higher one would retire the launch.");
            Assert.That(supervisor.View.LaunchUnmatched, Is.True, "An update now would have that launch retired, and its container fenced.");
        });
        _link.SetStreamLive(true);
        _engine.InspectFailure = null;
        await WaitUntilAsync(() => _link.Profile!().EditorTemplates.Count > 0, "the offer to be back");

        // Its executor registered for it: told as started, it is named no more.
        _link.Withdraw();
        await WaitUntilAsync(() => !supervisor.View.LaunchUnmatched, "its launch to be shown as done with");
        Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));
    }

    [Test]
    public async Task ARefusedStartWhoseContainerWasSeenCreated_FailsItsLaunch_ThoughItsRemovalWasLost()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.NextRunFailure = new ContainerEngineException(
            "Could not start the runner container from ghcr.io/offloadr/runner-comfyui:latest.",
            "docker: Error response from daemon: could not select device driver \"\" with capabilities: [[gpu]].",
            EngineOperation.Run);

        // Seen created; then it is removed, and neither that nor the look after it is answered.
        _engine.BeforeNextRemoveStopped = () =>
        {
            _engine.RemoveOn(null, Name);
            _engine.RemoveStoppedFailure = new ContainerEngineException("error during connect");
            _engine.InspectFailure = new ContainerEngineException("error during connect");
        };
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => _supervisor!.LogLines.Any(line => line.Contains("did not say whether", StringComparison.Ordinal)), "the unsettled launch");

        _engine.RemoveStoppedFailure = null;
        _engine.InspectFailure = null;
        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null || !_link.ExitReports.IsEmpty, "the launch to be settled");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.ExitReports, Is.Empty, "Refused, and seen created: it never ran.");
        });
    }

    [Test]
    public async Task ARefusedStartWhoseContainerWasSeenCreated_ReportsNoExit_ThoughItsRemovalWasLostAsStopWasPressed()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, true, "A session is assigned.");
        _engine.NextRunFailure = new ContainerEngineException(
            "Could not start the runner container from ghcr.io/offloadr/runner-comfyui:latest.",
            "docker: Error response from daemon: could not select device driver \"\" with capabilities: [[gpu]].",
            EngineOperation.Run);
        _engine.BeforeNextRemoveStopped = () =>
        {
            _engine.RemoveOn(null, Name);
            _engine.RemoveStoppedFailure = new ContainerEngineException("error during connect");
            _engine.InspectFailure = new ContainerEngineException("error during connect");
            supervisor.RequestStop();
        };
        _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => _engine.InspectFailure is not null, "the look after the start");
        await Task.Delay(100);
        _engine.RemoveStoppedFailure = null;
        _engine.InspectFailure = null;

        await WaitUntilAsync(() => supervisor.View.State is SupervisorState.Stopped or SupervisorState.Faulted, "the Stop to end");
        if (supervisor.View.State == SupervisorState.Faulted)
        {
            supervisor.RequestStop();
            await WaitForStateAsync(SupervisorState.Stopped);
        }

        await Task.Delay(200);
        Assert.That(_link.ExitReports, Is.Empty, "Refused, and seen created: it never ran.");
    }

    [Test]
    public async Task ARefusedStartWhoseContainerSomeoneStarted_IsToldAsGone_ThoughItsRemovalWasLost()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.ExitReportAnswer = _ =>
        {
            _link.Withdraw();
            return new ReleaseResult(true, true, string.Empty);
        };
        _engine.NextRunFailure = new ContainerEngineException(
            "Could not start the runner container from ghcr.io/offloadr/runner-comfyui:latest.",
            "docker: Error response from daemon: could not select device driver \"\" with capabilities: [[gpu]].",
            EngineOperation.Run);

        // Seen created; someone starts it, so its removal is refused; seen stopped; then it is removed, and
        // neither that nor the look after it is answered.
        var removals = 0;
        void Remove()
        {
            if (++removals == 1)
            {
                _engine.Restart(Name);
                _engine.OnNextInspect = () => _engine.Exit(Name, 1);
                _engine.BeforeNextRemoveStopped = Remove;
            }
            else
            {
                _engine.RemoveOn(null, Name);
                _engine.RemoveStoppedFailure = new ContainerEngineException("error during connect");
                _engine.InspectFailure = new ContainerEngineException("error during connect");
            }
        }

        _engine.BeforeNextRemoveStopped = Remove;
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => removals == 2 && _engine.InspectFailure is not null, "the second removal");
        await Task.Delay(100);
        _engine.RemoveStoppedFailure = null;
        _engine.InspectFailure = null;

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null || _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the launch to be settled");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.Not.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), Is.True, "It ran.");
        });
    }

    [Test]
    public async Task ARefusedStartWhoseContainerRanAndExitedLater_IsToldAsGone_ThoughItsRemovalWasLost()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.ExitReportAnswer = _ =>
        {
            _link.Withdraw();
            return new ReleaseResult(true, true, string.Empty);
        };
        _engine.NextRunFailure = new ContainerEngineException(
            "Could not start the runner container from ghcr.io/offloadr/runner-comfyui:latest.",
            "docker: Error response from daemon: could not select device driver \"\" with capabilities: [[gpu]].",
            EngineOperation.Run);

        // Seen created, and neither its removal nor the look after it is answered: it stays supervised.
        _engine.BeforeNextRemoveStopped = () =>
        {
            _engine.RemoveStoppedFailure = new ContainerEngineException("error during connect");
            _engine.InspectFailure = new ContainerEngineException("error during connect");
        };
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => _supervisor!.LogLines.Any(line => line.Contains("did not say whether", StringComparison.Ordinal)), "the unsettled launch");

        // Someone starts it, and it exits; seen exited, it is removed, and nothing of that is answered either.
        _engine.Restart(Name);
        _engine.Exit(Name, 1);
        var removed = false;
        _engine.BeforeNextRemoveStopped = () =>
        {
            _engine.RemoveOn(null, Name);
            _engine.RemoveStoppedFailure = new ContainerEngineException("error during connect");
            _engine.InspectFailure = new ContainerEngineException("error during connect");
            removed = true;
        };
        _engine.RemoveStoppedFailure = null;
        _engine.InspectFailure = null;
        await WaitUntilAsync(() => removed, "the removal of the exited container");
        await Task.Delay(100);
        _engine.RemoveStoppedFailure = null;
        _engine.InspectFailure = null;

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null || _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the launch to be settled");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.Not.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), Is.True, "It ran.");
        });
    }

    [Test]
    public async Task AStopThatFindsARunnerMissedAtStartup_AsksForItsReleaseFirst_AndTellsWhatBecameOfIt()
    {
        var launchId = Guid.NewGuid().ToString();
        AddLaunchedContainer(launchId);
        _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
        var supervisor = await StartSupervisorAsync();
        await WaitForStateAsync(SupervisorState.Faulted);
        var stopsWhenAsked = -1;
        _link.ReleaseAnswer = _ =>
        {
            stopsWhenAsked = _engine.Stops;
            return new ReleaseResult(false, true, "A session is assigned.");
        };

        _engine.InspectFailure = null;
        supervisor.RequestStop();

        await WaitForStateAsync(SupervisorState.Stopped);
        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launchId), "the container to be told as gone");
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.False);
            Assert.That(_link.Releases.Select(release => release.LaunchId), Is.EqualTo(new[] { launchId }));
            Assert.That(stopsWhenAsked, Is.Zero, "Asked before it is stopped.");
            Assert.That(_engine.Stops, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ARefusedStartWhoseContainerStopSawCreated_ReportsNoExit_ThoughItsRemovalWasLost()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.NextRunFailure = new ContainerEngineException(
            "Could not start the runner container from ghcr.io/offloadr/runner-comfyui:latest.",
            "docker: Error response from daemon: could not select device driver \"\" with capabilities: [[gpu]].",
            EngineOperation.Run);

        // The engine does not answer the look after the refused start, nor the first Stop.
        _engine.BeforeRun = () => _engine.InspectFailure = new ContainerEngineException("error during connect");
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("did not say whether", StringComparison.Ordinal)), "the unsettled launch");
        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Faulted);

        // Seen created by the next one; then it is removed, and neither that nor the look after it is answered.
        var removed = false;
        _engine.BeforeNextRemoveStopped = () =>
        {
            removed = true;
            _engine.RemoveOn(null, Name);
            _engine.RemoveStoppedFailure = new ContainerEngineException("error during connect");
            _engine.InspectFailure = new ContainerEngineException("error during connect");
        };
        _engine.InspectFailure = null;
        supervisor.RequestStop();
        await WaitUntilAsync(() => removed, "the removal of the created container");
        await WaitForStateAsync(SupervisorState.Faulted);
        _engine.RemoveStoppedFailure = null;
        _engine.InspectFailure = null;
        supervisor.RequestStop();

        await WaitForStateAsync(SupervisorState.Stopped);
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(_link.ExitReports, Is.Empty, "Refused, and seen created: it never ran.");
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.Not.EqualTo(RunnerLaunchPhase.Started));
        });
    }

    [Test]
    public Task AContainerNotedAsCreated_SeenRunningBesideTheRunner_IsToldAsGone_OnceItIs()
        => WithDataFolderAsync(async paths =>
        {
            var launchId = Guid.NewGuid().ToString();
            var engine = ContainerEngineSelection.Describe(new ContainerEngineSelection.Selected(null, null));
            File.WriteAllText(paths.LaunchedContainers, $"starting {launchId} {FakeLauncherLink.TargetOf(_settings.Current)} {engine}");
            await StartSupervisorAsync(paths: paths, lateContainerWatch: TimeSpan.FromSeconds(30));
            await MakeAvailableAsync();
            await LaunchAsync();

            // The engine made it after all, and someone started it: it runs beside the runner that is supervised.
            _engine.AddContainer("other", ComfyUi, running: true, new Dictionary<string, string>
            {
                [RunnerSupervisor.ManagedLabel] = "true",
                [RunnerSupervisor.OwnerLabel] = SupervisorOwner.Default.Id,
                [RunnerSupervisor.LaunchIdLabel] = launchId,
                [RunnerSupervisor.LaunchTargetLabel] = FakeLauncherLink.TargetOf(_settings.Current),
            });
            await WaitUntilAsync(() => File.ReadAllText(paths.LaunchedContainers).Contains($"running {launchId} ", StringComparison.Ordinal), "it to be noted as one that ran");

            _engine.RemoveOn(null, "other");

            await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launchId), "the exit report");
        });

    [Test]
    public async Task ProgressThatCannotBeSent_HoldsUpNeitherTheLaunchNorAStop()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        using var proceed = new ManualResetEventSlim();
        _link.OnProgress = _ => proceed.Wait(TimeSpan.FromSeconds(8));
        try
        {
            var launch = _link.Name("comfyui-latest", ComfyUi);
            await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId)?.Phase == RunnerLaunchPhase.Started, "the launch to start all the same");
            supervisor.RequestStop();
            await WaitForStateAsync(SupervisorState.Stopped);
            Assert.That(_engine.Exists(Name), Is.False);
        }
        finally
        {
            proceed.Set();
        }
    }

    [Test]
    public async Task AColdLaunchWhoseImageDoesNotGetTheGpuChosen_IsNotStarted()
    {
        const string other = "GPU-bbbbbbbb-1111-2222-3333-444444444444";
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, GpuUuid = FakeContainerEngine.GpuUuid });
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid)], null);
        _engine.ImageIds[ForgeNeo] = "sha256:forge";
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        // Checked with another image, whose containers get the GPU chosen; this one's get another.
        _engine.GpuCheckOf = image => image == "sha256:forge" ? new GpuAccessCheck(true, $"GPU 0: NVIDIA GeForce RTX 3060 (UUID: {other})") : null;
        var launch = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_engine.Runs, Is.Empty, "It could not serve on the GPU it was chosen for.");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ALaunchWhoseImageGetsAnotherGpuThanIsOffered_IsNotStarted_AndThatGpuIsOffered(bool warm)
    {
        const string other = "GPU-bbbbbbbb-1111-2222-3333-444444444444";
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid)], null);
        _engine.ImageIds[ForgeNeo] = "sha256:forge";
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        if (warm)
        {
            await LaunchAsync();
        }

        var runs = _engine.Runs.Count;

        // No GPU is chosen, and this computer lists one: not the one containers of this image get.
        _engine.GpuCheckOf = image => image == "sha256:forge" ? new GpuAccessCheck(true, $"GPU 0: NVIDIA GeForce RTX 3060 (UUID: {other})") : null;
        _engine.ContainerGpus = [new HostGpu("NVIDIA GeForce RTX 3060", 12288, other)];
        var launch = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_engine.Runs, Has.Count.EqualTo(runs), "Offloadr chose this computer for another GPU.");
            Assert.That(_link.Releases, Is.Empty);
            Assert.That(_link.Profile!().Resources.GpuModelName, Is.EqualTo("NVIDIA GeForce RTX 3060"));
            Assert.That(_link.Profile!().OfferRevision, Is.GreaterThan(launch.OfferRevision));
        });
    }

    [Test]
    public async Task ATagMovedDuringAGpuCheck_IsNotCreditedWithIt()
    {
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid)], null);
        _engine.AddImage(ComfyUi);
        _engine.ImageIds[ComfyUi] = "sha256:before";
        _engine.OnNextGpuCheck = () =>
        {
            _engine.ImageIds[ComfyUi] = "sha256:after";
            return Task.CompletedTask;
        };
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        Assert.That(_engine.GpuCheckImages, Is.EqualTo(new[] { "sha256:before" }));

        // Containers of the image the tag names now do not reach the GPU.
        _engine.GpuCheckOf = image => image == "sha256:after" ? new GpuAccessCheck(false, "nvidia-container-cli: requirement error: unsatisfied condition: cuda>=13.0") : null;
        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_engine.Runs, Is.Empty);
        });
    }

    [Test]
    public async Task AnImageProvedOnce_IsNotCheckedAgainBeforeItsNextStart()
    {
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid)], null);
        _engine.ImageIds[ForgeNeo] = "sha256:forge";
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromMilliseconds(200));
        await MakeAvailableAsync();
        await LaunchAsync("forge-neo:neo", ForgeNeo);
        await WaitForStateAsync(SupervisorState.Available);
        await WaitUntilAsync(() => _supervisor!.View.IsReady && _link.Running, "the launcher to be ready");
        var checks = _engine.GpuCheckImages.Count(image => image == "sha256:forge");

        await LaunchAsync("forge-neo:neo", ForgeNeo);

        Assert.Multiple(() =>
        {
            Assert.That(checks, Is.EqualTo(1));
            Assert.That(_engine.GpuCheckImages.Count(image => image == "sha256:forge"), Is.EqualTo(1));
        });
    }

    [Test]
    public Task WhatOffloadrAnswersToAnExitReport_IsNotLogged()
        => WithDataFolderAsync(async paths =>
        {
            const string earlierKey = "an-earlier-runner-key-0123456789abcdefghijklm";
            var launchId = Guid.NewGuid().ToString();
            _link.ExitReportAnswer = _ => new ReleaseResult(false, true, $"The key {earlierKey} is not known.");
            File.WriteAllText(paths.LaunchedContainers, $"gone {launchId} {FakeLauncherLink.TargetOf(_settings.Current)} -");

            var supervisor = await StartSupervisorAsync(paths: paths);
            await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("did not take the report", StringComparison.Ordinal)), "the refusal to be logged");

            Assert.That(supervisor.LogLines, Has.None.Contains(earlierKey));
        });

    [TestCase(false)]
    [TestCase(true)]
    public async Task AGpuListingThatFailsOnce_IsMadeAgain_AndChangesNoOffer(bool warm)
    {
        // (This computer lists its GPU without a UUID: the containers are asked what they get.)
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromHours(1));
        await MakeAvailableAsync();
        if (warm)
        {
            await LaunchAsync();
        }

        var gpus = _engine.ContainerGpus;
        var failing = true;
        _engine.ContainerGpus = [];
        _engine.OnContainerGpuList = _ =>
        {
            if (failing)
            {
                failing = false;
                _engine.ContainerGpus = gpus;
            }

            return Task.CompletedTask;
        };
        var launch = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(failing, Is.False);
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started));
            Assert.That(_link.Profile!().OfferRevision, Is.EqualTo(launch.OfferRevision), "The same hardware throughout.");
        });
    }

    [Test]
    public async Task AGpuListingThatKeepsFailing_FailsTheLaunch_AndChangesNoOffer()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.ContainerGpus = [];

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed));
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(_link.Profile!().OfferRevision, Is.EqualTo(launch.OfferRevision));
            Assert.That(_link.Profile!().EditorTemplates, Is.Not.Empty);
        });
    }

    [Test]
    public async Task AnUpdateAboutToRestart_TakesUpNoLaunch()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        Assert.That(await supervisor.TryHoldForUpdateAsync(view => view.State == SupervisorState.Available), Is.True);

        var launch = _link.Name("comfyui-latest", ComfyUi);
        await Task.Delay(300);
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Is.Empty, "Left for the version that starts next.");
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null);
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Available));
        });

        // The restart does not happen after all.
        supervisor.ReleaseUpdateHold();
        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId)?.Phase == RunnerLaunchPhase.Started, "the launch to start");
    }

    [Test]
    public async Task AnUpdateIsNotLetRestart_WhileALaunchIsInFlight()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var pulling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnPull = async _ =>
        {
            pulling.TrySetResult();
            await finish.Task;
        };
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await pulling.Task.WaitAsync(WaitLimit);

        Assert.That(await supervisor.TryHoldForUpdateAsync(_ => true), Is.False);

        finish.TrySetResult();
        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId)?.Phase == RunnerLaunchPhase.Started, "the launch to go on");
    }

    [Test]
    public async Task AHostGpuListThatCannotBeRead_DoesNotMakeALaunchStale()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, GpuUuid = FakeContainerEngine.GpuUuid });
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid)], null);
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        // nvidia-smi on this computer does not answer for the launch's last check; containers have the GPU.
        _engine.OnCreate = (_, _) =>
        {
            _hostGpus.Report = HostGpuReport.None("nvidia-smi did not answer.");
            return Task.CompletedTask;
        };
        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started));
            Assert.That(_link.Profile!().OfferRevision, Is.EqualTo(launch.OfferRevision));
        });
    }

    [Test]
    public async Task AStopThatFirstFindsItsCreatedContainerRunning_AsksForItsReleaseFirst()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.OnCreate = (_, _) =>
        {
            _link.SetStreamLive(false);
            return Task.CompletedTask;
        };
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => _engine.Exists(Name), "the container to be created");
        await Task.Delay(100);

        // Someone starts it: the Stop's own look is the first to see it run.
        _engine.OnInspect = name =>
        {
            _engine.OnInspect = null;
            _engine.Restart(Name);
        };
        bool? runningAtRelease = null;
        _link.OnRelease = _ =>
        {
            runningAtRelease ??= _engine.Stops == 0;
            return Task.CompletedTask;
        };
        supervisor.RequestStop();

        await WaitForStateAsync(SupervisorState.Stopped);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Releases.Select(release => release.LaunchId), Is.EqualTo(new[] { launch.LaunchId }));
            Assert.That(runningAtRelease, Is.True, "Before it is stopped.");
            Assert.That(_engine.Exists(Name), Is.False);
        });
    }

    [Test]
    public async Task AStopAsALaunchDiscardsItsCreatedContainer_TellsThatItRan_IfTheDiscardSawThat()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _engine.OnCreate = (_, _) =>
        {
            _link.SetStreamLive(false);
            return Task.CompletedTask;
        };
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => _engine.Exists(Name), "the container to be created");
        await Task.Delay(100);

        // Started outside the app, and exited. The key changes, so the launch discards it; Stop is pressed as
        // that has removed it, and has not said so yet.
        _engine.OnInspect = name =>
        {
            _engine.OnInspect = null;
            _engine.Restart(Name);
            _engine.Exit(Name, 1);
        };
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var say = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnNextRemovedStopped = async () =>
        {
            removed.TrySetResult();
            await say.Task;
        };
        await _settings.SaveAsync(_settings.Current with { RunnerKey = "another-runner-key-0123456789abcdefghijklmnop" });
        await removed.Task.WaitAsync(WaitLimit);
        supervisor.RequestStop();
        await Task.Delay(100);
        say.TrySetResult();

        await WaitForStateAsync(SupervisorState.Stopped);
        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the container to be told as gone");
    }

    [Test]
    public Task AStopTheEngineConfirmed_IsTold_ThoughTheEngineSaysNothingMore()
        => WithDataFolderAsync(async paths =>
        {
            var supervisor = await StartSupervisorAsync(paths: paths);
            await MakeAvailableAsync();
            var launch = await LaunchAsync();
            _link.ReleaseAnswer = _ => new ReleaseResult(false, true, "A session is assigned.");

            // Stopped, and then the engine goes away: neither removed nor looked at.
            _engine.BeforeNextRemove = () =>
            {
                _engine.NextRemoveFailure = new ContainerEngineException("error during connect");
                _engine.InspectFailure = new ContainerEngineException("error during connect");
            };
            supervisor.RequestStop();

            await WaitForStateAsync(SupervisorState.Stopped);
            await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the container to be told as gone");
            Assert.That(File.Exists(paths.LeftoverContainers), Is.True, "Removed later.");
        });

    [Test]
    public async Task AStartWhoseProcessWasCreated_IsNotCutShort_ByAStreamThatDropsRightThen()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        // Past the last check: the engine's command runs as the stream drops.
        _engine.OnRun = async token =>
        {
            _engine.OnRun = null;
            _link.SetStreamLive(false);
            await Task.Delay(100, token);
        };
        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null, "the launch's outcome");
        Assert.Multiple(() =>
        {
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started));
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
            Assert.That(_link.ExitReports, Is.Empty);
            Assert.That(_supervisor!.LogLines, Has.None.Contains("started all the same"), "Its answer was not lost.");
        });
    }

    [Test]
    public async Task AGpuChosenWhileTheEarlierOneIsChecked_IsTheOneAStartGoesBy()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, GpuUuid = "GPU-bbbbbbbb-1111-2222-3333-444444444444" });
        _hostGpus.Report = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564, FakeContainerEngine.GpuUuid)], null);
        _engine.OnNextGpuCheck = () => _settings.SaveAsync(_settings.Current with { GpuUuid = FakeContainerEngine.GpuUuid });
        var supervisor = await StartSupervisorAsync();

        supervisor.RequestStart();

        await WaitUntilAsync(() => supervisor.View.IsReady && _link.Running, "the computer to be offered, with no other press of Start");
        Assert.That(_link.Profile!().Resources.GpuCount, Is.EqualTo(1));
    }

    [Test]
    public async Task AStopThatFindsItsCreatedContainerRunning_AsksForItsReleaseFirst()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();

        // Created, and waiting for the stream before it is started. Someone starts it as Stop removes it.
        _engine.OnCreate = (_, _) =>
        {
            _link.SetStreamLive(false);
            return Task.CompletedTask;
        };
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => _engine.Exists(Name), "the container to be created");
        await Task.Delay(100);
        _engine.BeforeNextRemoveStopped = () => _engine.Restart(Name);
        bool? runningAtRelease = null;
        _link.OnRelease = _ =>
        {
            runningAtRelease ??= _engine.Stops == 0;
            return Task.CompletedTask;
        };
        supervisor.RequestStop();

        await WaitForStateAsync(SupervisorState.Stopped);
        Assert.Multiple(() =>
        {
            Assert.That(_link.Releases.Select(release => release.LaunchId), Is.EqualTo(new[] { launch.LaunchId }));
            Assert.That(runningAtRelease, Is.True, "Before it is stopped.");
            Assert.That(_engine.Exists(Name), Is.False);
            Assert.That(_link.ExitReports, Is.Empty, "Offloadr released it: it knows.");
        });
    }

    [Test]
    public async Task AStreamThatFallsSilentRightBeforeTheContainerIsStarted_HoldsTheStartBack()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();

        // Its keepalive runs out, and nothing said so yet.
        _engine.BeforeRun = () => _link.StartsAllowed = false;

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => !_link.StartsAllowed, "the start to be asked for");
        await Task.Delay(300);
        Assert.That(_engine.Runs, Is.Empty, "The stream that named its launch is gone.");

        _link.StartsAllowed = true;
        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId)?.Phase == RunnerLaunchPhase.Started, "the launch to start once it speaks again");
        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
            Assert.That(_link.ExitReports, Is.Empty);
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ACreationTheEngineDoesNotAnswerFor_FailsItsLaunch_OnlyOnceItIsKnownThatItNeverRan(bool ran)
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.ExitReportAnswer = _ =>
        {
            _link.Withdraw();
            return new ReleaseResult(true, true, string.Empty);
        };
        _engine.OnCreate = (spec, _) =>
        {
            // Created, the answer lost, and no look answered.
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
            _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
            _engine.FindByLabelFailures = int.MaxValue;
            throw new ContainerEngineException("Could not create the runner container.", "error during connect: read tcp: connection reset by peer");
        };

        var launch = _link.Name("comfyui-latest", ComfyUi);

        await WaitUntilAsync(() => _supervisor!.LogLines.Any(line => line.Contains("could not start comfyui-latest", StringComparison.Ordinal)), "the launch to fail");
        await Task.Delay(400);
        Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null, "The engine does not say whether it was created, and someone started it.");
        if (ran)
        {
            _engine.Restart(Name);
            _engine.Exit(Name, 1);
        }

        _engine.InspectFailure = null;
        _engine.FindByLabelFailures = 0;
        if (ran)
        {
            await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the container to be told as gone");
            Assert.That(_link.OutcomeOf(launch.LaunchId), Is.Null);
        }
        else
        {
            await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId)?.Phase == RunnerLaunchPhase.Failed, "the launch to fail");
            Assert.That(_link.ExitReports, Is.Empty);
        }
    }

    [Test]
    public async Task ALaunchWhoseContainerMaintenanceFoundExited_IsNotStartedAgain_WhileThatIsReported()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        var creations = 0;
        _engine.OnCreate = (spec, _) =>
        {
            Interlocked.Increment(ref creations);
            _engine.OnCreate = (_, _) =>
            {
                Interlocked.Increment(ref creations);
                return Task.CompletedTask;
            };

            // Created, the answer lost, and no look answered.
            _engine.AddContainer(Name, spec.Image, running: false, spec.Labels, spec.Environment, neverStarted: true);
            _engine.InspectFailure = new ContainerEngineException("docker inspect timed out.");
            _engine.FindByLabelFailures = int.MaxValue;
            throw new ContainerEngineException("Could not create the runner container.", "context deadline exceeded");
        };

        // Offloadr takes its time over the exit report, and goes on naming the launch meanwhile.
        var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _link.OnExitReport = (_, token) => taken.Task.WaitAsync(token);
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => _supervisor!.LogLines.Any(line => line.Contains("could not start comfyui-latest", StringComparison.Ordinal)), "the launch to fail");
        await Task.Delay(200);

        // Started outside the app and exited; the engine answers again.
        _engine.Restart(Name);
        _engine.Exit(Name, 1);
        _engine.InspectFailure = null;
        _engine.FindByLabelFailures = 0;
        await WaitUntilAsync(() => !_engine.Exists(Name), "what was left to be removed");
        await WaitUntilAsync(() => _supervisor!.View.IsReady && _link.Running, "the computer to be offered again");
        await Task.Delay(600);

        Assert.Multiple(() =>
        {
            Assert.That(creations, Is.EqualTo(1), "Never two containers for one launch.");
            Assert.That(_engine.Runs, Is.Empty);
            Assert.That(_link.OutcomeOf(launch.LaunchId)?.Phase, Is.Not.EqualTo(RunnerLaunchPhase.Failed));
        });
        taken.TrySetResult();
    }

    [Test]
    public async Task ARunnerTakenUpLateDuringAnOutage_KeepsTheCatalogOffloadrGave()
    {
        static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);

        _plans.Plan = ControlPlanePlan("comfyui-latest");
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
        _selection = new ContainerEngineSelection(Locate, statePath: null);
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, true, "A session is assigned.");
        await LaunchAsync();
        _engine.UnavailableEngines["docker-a"] = true;
        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Faulted);
        await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
        supervisor.RequestStart();
        await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Available && _link.Running, "the computer to be offered on the other engine");
        var offered = _link.Profile!().EditorTemplates;

        // Engine A answers again, and its runner is taken up, while Offloadr's catalog cannot be had.
        _plans.Plan = BuiltInLaunchPlanCatalog.Plan(source: LaunchPlanSource.Unreachable);
        _engine.UnavailableEngines.TryRemove("docker-a", out _);
        await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Running, "the runner there to be taken up");
        await Task.Delay(200);

        Assert.Multiple(() =>
        {
            Assert.That(offered, Is.EqualTo(new[] { "comfyui-latest", "comfyui-master" }));
            Assert.That(supervisor.View.Plan?.Source, Is.EqualTo(LaunchPlanSource.ControlPlane));
            Assert.That(supervisor.View.Plan?.Templates.Select(template => template.EditorTemplate), Is.EqualTo(offered));
        });
    }

    [Test]
    public async Task AContainerStartedOutsideTheAppBeforeItsStart_IsToldAsGone_WhenAStopFindsItRunning()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, true, "A session is assigned.");

        // Created, and waiting for the stream before it is started.
        _engine.OnCreate = (_, _) =>
        {
            _link.SetStreamLive(false);
            return Task.CompletedTask;
        };
        var launch = _link.Name("comfyui-latest", ComfyUi);
        await WaitUntilAsync(() => _engine.Exists(Name), "the container to be created");
        await Task.Delay(100);

        // Someone starts it, right as Stop is pressed: the Stop's own look is the first to see it run.
        _engine.OnInspect = name =>
        {
            _engine.OnInspect = null;
            _engine.Restart(Name);
        };
        supervisor.RequestStop();

        await WaitForStateAsync(SupervisorState.Stopped);
        await WaitUntilAsync(() => _link.ExitReports.Any(report => report.LaunchId == launch.LaunchId), "the container to be told as gone");
        Assert.That(_engine.Exists(Name), Is.False);
    }

    [Test]
    public async Task ARunnerOnAnEarlierEngineWhoseReleaseWasGranted_IsNotAskedForAgain_WhenItsStopFailedOnce()
    {
        static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);

        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
        _selection = new ContainerEngineSelection(Locate, statePath: null);
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        var first = await LaunchAsync();
        _link.ReleaseAnswer = _ => new ReleaseResult(false, true, "A session is assigned.");
        _engine.UnavailableEngines["docker-a"] = true;
        supervisor.RequestStop();
        await WaitForStateAsync(SupervisorState.Faulted);
        _link.ReleaseAnswer = _ => new ReleaseResult(true, true, string.Empty);
        await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
        supervisor.RequestStart();
        await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Available && _link.Running, "the computer to be offered on the other engine");
        await LaunchAsync();
        _engine.UnavailableEngines.TryRemove("docker-a", out _);
        await Task.Delay(500);
        var asked = _link.Releases.Count(release => release.LaunchId == first.LaunchId);

        // Stop: B's runner is stopped. A's is released, and its stop fails once.
        var stops = 0;
        void Count()
        {
            if (Interlocked.Increment(ref stops) == 2)
            {
                _engine.NextStopFailure = new ContainerEngineException("docker stop timed out.");
            }
            else
            {
                _engine.BeforeStop = Count;
            }
        }

        // (Maintenance finds it running meanwhile, again and again: it is being stopped already.)
        _link.OnRelease = token => Task.Delay(300, token);
        _engine.BeforeStop = Count;
        supervisor.RequestStop();

        await WaitUntilAsync(() => !_engine.ExistsOn("docker-a", Name), "the runner on the earlier engine to be stopped");
        await Task.Delay(200);
        Assert.Multiple(() =>
        {
            Assert.That(stops, Is.EqualTo(2));
            Assert.That(_link.Releases.Count(release => release.LaunchId == first.LaunchId), Is.EqualTo(asked + 1), "Granted for good: asked once.");
            Assert.That(_link.ExitReports.Any(report => report.LaunchId == first.LaunchId), Is.False, "Offloadr released it: it knows.");
        });
    }

    [Test]
    public async Task TheAppClosingWhileAStopReleasesARunnerOnAnEarlierEngine_StillStopsIt()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "offloadr-engine-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            static LocatedEngine? Locate(string? configured) => configured is null ? null : new LocatedEngine(configured, ContainerEngineKind.Docker);

            _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, ContainerEngineExecutable = "docker-a" });
            _selection = new ContainerEngineSelection(Locate, statePath);
            var supervisor = await StartSupervisorAsync();
            await MakeAvailableAsync();
            await LaunchAsync();

            // Engine A stops answering; the person moves on to engine B, and a session starts there.
            // (Offloadr does not release it then: a grant would not be asked for again.)
            _link.ReleaseAnswer = _ => new ReleaseResult(false, true, "A session is assigned.");
            _engine.UnavailableEngines["docker-a"] = true;
            supervisor.RequestStop();
            await WaitForStateAsync(SupervisorState.Faulted);
            _link.ReleaseAnswer = _ => new ReleaseResult(true, true, string.Empty);
            await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b" });
            supervisor.RequestStart();
            await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Available && _link.Running, "the computer to be offered on the other engine");
            await LaunchAsync();
            _engine.UnavailableEngines.TryRemove("docker-a", out _);
            await Task.Delay(500);

            // Stop: B's runner is stopped, and A's is being released when the app closes.
            Task? closed = null;
            var releases = 0;
            _link.OnRelease = async token =>
            {
                // (The first is for B's runner, the second for A's: Offloadr does not answer that one.)
                if (Interlocked.Increment(ref releases) == 2)
                {
                    closed = supervisor.StopAsync(CancellationToken.None);
                    await Task.Delay(Timeout.Infinite, token);
                }
            };
            supervisor.RequestStop();

            await WaitUntilAsync(() => closed is not null, "the app to close");
            await closed!.WaitAsync(WaitLimit);
            Assert.Multiple(() =>
            {
                Assert.That(_engine.ExistsOn("docker-b", Name), Is.False);
                Assert.That(_engine.ExistsOn("docker-a", Name), Is.False, "Taken up by the Stop, and carried on as the app closes: it holds the runner key.");
            });
        }
        finally
        {
            DeleteQuietly(statePath);
        }
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
            AddLaunchedContainer(Guid.NewGuid().ToString());

            // Settings moved to engine B, the app crashed, and engine A does not answer at the next start.
            await _settings.SaveAsync(_settings.Current with { ContainerEngineExecutable = "docker-b", AutoStart = true });
            _selection = new ContainerEngineSelection(Locate, statePath);
            _engine.UnavailableEngines["docker-a"] = true;
            _engine.StatusJson = Status(activeSessionId: "session-1");
            var supervisor = await StartSupervisorAsync();
            await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.Contains("may still be running on docker-a", StringComparison.Ordinal)));
            await Task.Delay(200);
            Assert.Multiple(() =>
            {
                Assert.That(_link.Runs, Is.Empty, "Nothing is offered while the runner may still run elsewhere.");
                Assert.That(File.Exists(statePath), Is.True);
            });

            // Engine A answers again: the runner is found there.
            _engine.UnavailableEngines.TryRemove("docker-a", out _);

            await WaitForStateAsync(SupervisorState.Running);
            Assert.Multiple(() =>
            {
                Assert.That(_engine.Runs, Is.Empty);
                Assert.That(_engine.ExistsOn("docker-a", Name), Is.True);
            });
        }
        finally
        {
            DeleteQuietly(statePath);
        }
    }

    [Test]
    public async Task DiscoveredEngine_StaysPinnedWhileTheRunnerRuns()
    {
        var discovered = "/usr/bin/podman";
        _selection = new ContainerEngineSelection(configured => new LocatedEngine(configured ?? Volatile.Read(ref discovered), ContainerEngineKind.Podman));
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

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
    public async Task ALaunch_RunsInTheDockerContextTheGpuCheckPassedIn()
    {
        _engine.CurrentContext = "desktop-linux";
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        string? contextAtRun = null;
        _engine.BeforeRun = () => contextAtRun = _selection.Context;

        // `docker context use` elsewhere, after the check.
        _engine.CurrentContext = "remote-builder";
        await LaunchAsync();

        Assert.Multiple(() =>
        {
            Assert.That(contextAtRun, Is.EqualTo("desktop-linux"));
            Assert.That(_engine.GpuChecks, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SecondSupervisor_WithTheSameLock_LeavesTheRunnerAlone()
    {
        var lockPath = Path.Combine(Path.GetTempPath(), "offloadr-supervisor-" + Guid.NewGuid().ToString("n") + ".lock");
        using var firstLock = new SupervisorInstanceLock(lockPath);
        using var secondLock = new SupervisorInstanceLock(lockPath);
        var first = await StartSupervisorAsync(instanceLock: firstLock);
        await MakeAvailableAsync();
        await LaunchAsync();

        var otherLink = new FakeLauncherLink();
        using var second = new RunnerSupervisor(_settings, _engine, _plans, new FakeHostGpuProbe(), otherLink, new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance, _selection, secondLock);
        await second.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => second.View.State == SupervisorState.Faulted);
        second.RequestStart();
        await second.StopAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(second.View.Message, Does.Contain("Another copy"));
            Assert.That(second.View.Fault?.Kind, Is.EqualTo(FaultKind.AnotherCopy));
            Assert.That(otherLink.Runs, Is.Empty);
            Assert.That(_engine.Runs, Has.Count.EqualTo(1));
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(first.View.State, Is.EqualTo(SupervisorState.Running));
        });
    }

    // Images

    [Test]
    public async Task ALaunchOfAnUpdatedTag_RemovesTheImageItReplaced()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromMilliseconds(200));
        await MakeAvailableAsync();
        await LaunchAsync();
        await WaitForStateAsync(SupervisorState.Available);

        // The tag moves to a newer image; the one the runner ran is then named by nothing.
        _engine.ImageIds[ComfyUi] = "sha256:newer";
        await LaunchAsync();

        await WaitUntilAsync(() => _engine.RemovedImages.Count > 0, "the removal");
        Assert.Multiple(() =>
        {
            Assert.That(_engine.RemovedImages, Is.EqualTo(new[] { "sha256:local" }));
            Assert.That(_engine.RemovalRequests, Is.EqualTo(new[] { FakeContainerEngine.DigestReference("sha256:local") }), "By its digest, not its ID.");
            Assert.That(supervisor.LogLines, Has.Some.Contains("removed the runner image this one replaced"));
        });
    }

    [Test]
    public async Task AnImageThatWasReplaced_IsNotRemoved_WhileAPullOrLaunchIsUnderWay()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromMilliseconds(200));
        await MakeAvailableAsync();
        await LaunchAsync();
        await WaitForStateAsync(SupervisorState.Available);
        using (await supervisor.ImageGate.EnterUseAsync(CancellationToken.None))
        {
            _engine.ImageIds[ComfyUi] = "sha256:newer";
            await LaunchAsync();
            await Task.Delay(400);
            Assert.That(_engine.RemovalRequests, Is.Empty, "What is under way may be about to use it, named by its digest.");
        }

        await WaitUntilAsync(() => _engine.RemovedImages.Count > 0, "the removal, once it is done");
    }

    [Test]
    public async Task ALaunchOfTheSameImage_RemovesNothing()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        await StartSupervisorAsync(warmTime: TimeSpan.FromMilliseconds(200));
        await MakeAvailableAsync();
        await LaunchAsync();
        await WaitForStateAsync(SupervisorState.Available);
        await WaitUntilAsync(() => _supervisor!.View.IsReady && _link.Running, "the launcher to be ready");

        await LaunchAsync();

        Assert.That(_engine.RemovalRequests, Is.Empty);
    }

    // Logs and status

    [Test]
    public async Task Logs_WhenFollowingStops_AreFollowedAgainFromNewLines()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();
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
    public async Task Logs_HideTheRunnerKey()
    {
        _engine.LogLine = $"echo {Key}";
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

        await WaitUntilAsync(() => supervisor.LogLines.Any(line => line.StartsWith("echo", StringComparison.Ordinal)));
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.LogLines, Has.None.Contains(Key));
            Assert.That(supervisor.LogLines, Has.Some.EqualTo("echo [runner key]"));
        });
    }

    [Test]
    public async Task Logs_HideTheRunningContainersKey_AfterTheKeyChanges()
    {
        const string newKey = "another-runner-key-0123456789abcdefghijklmnop";
        _engine.StatusJson = Status(activeSessionId: "session-1");
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();
        await WaitUntilAsync(() => supervisor.View.HasActiveSession);

        await _settings.SaveAsync(_settings.Current with { RunnerKey = newKey });
        await WaitUntilAsync(() => !_link.Running && supervisor.View.Message?.Contains("earlier settings", StringComparison.Ordinal) == true, "the new key to apply");
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
    public async Task Diagnostics_HideTheRunningContainersKey_AfterTheKeyChanges()
    {
        const string newKey = "another-runner-key-0123456789abcdefghijklmnop";
        _engine.StatusJson = Status(activeSessionId: "session-1");
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();
        await WaitUntilAsync(() => supervisor.View.HasActiveSession);
        await _settings.SaveAsync(_settings.Current with { RunnerKey = newKey });
        await WaitUntilAsync(() => !_link.Running && supervisor.View.Message?.Contains("earlier settings", StringComparison.Ordinal) == true, "the new key to apply");
        var logs = Path.Combine(Path.GetTempPath(), "runner-diagnostics-" + Guid.NewGuid().ToString("n"));
        var export = new Offloadr.Runner.Supervisor.Hosting.DiagnosticsExport(supervisor, _settings, new SupervisorPaths(logs), TimeProvider.System);

        var input = export.Gather();
        var (name, report) = export.Create();

        Assert.Multiple(() =>
        {
            Assert.That(input.OtherRunnerKeys, Is.EqualTo(new[] { Key }));
            Assert.That(Offloadr.Runner.Supervisor.Hosting.DiagnosticsReport.Redact($"old {Key} new {newKey}", input), Is.EqualTo("old [runner key] new [runner key]"));
            Assert.That(report, Does.Not.Contain(Key).And.Not.Contain(newKey));
            Assert.That(report, Does.Contain("State: Session running (Running)"));
            Assert.That(name, Does.StartWith("offloadr-runner-diagnostics-").And.EndWith(".txt"));
        });
    }

    [Test]
    public async Task Status_IsReadFromTheContainer()
    {
        _engine.StatusJson = Status(activeSessionId: "session-9");
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

        await WaitUntilAsync(() => supervisor.View.RunnerStatus?.ActiveSessionId == "session-9");
        Assert.That(supervisor.View.HasActiveSession, Is.True);
    }

    // Shutdown, updates and sleep

    [Test]
    public async Task Shutdown_StopsTheContainerItOwns()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

        await supervisor.StopAsync(CancellationToken.None);
        _supervisor = null;
        supervisor.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.False);
            Assert.That(_link.Running, Is.False);
        });
    }

    [Test]
    public async Task WindowsShuttingDown_StopsTheRunnerWithTheShortGracePeriod_AndRemovesIt()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

        supervisor.UseFastStop();
        await supervisor.StopAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_engine.StopTimeouts, Is.EqualTo(new[] { TimeSpan.FromSeconds(5) }));
            Assert.That(_engine.Exists(Name), Is.False, "The container, which holds the runner key, is removed.");
        });
    }

    [Test]
    public async Task AQuit_StopsTheRunnerWithItsUsualGracePeriod()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

        await supervisor.StopAsync(CancellationToken.None);

        Assert.That(_engine.StopTimeouts, Is.EqualTo(new[] { TimeSpan.FromSeconds(1) }));
    }

    [Test]
    public async Task Shutdown_ForAnUpdate_LeavesTheRunnerForTheNextCopyToAdopt()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

        supervisor.KeepRunnerOnShutdown();
        await supervisor.StopAsync(CancellationToken.None);
        _supervisor = null;
        supervisor.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Exists(Name), Is.True);
            Assert.That(_engine.Stops, Is.Zero);
        });

        _link = new FakeLauncherLink();
        var next = await StartSupervisorAsync();
        next.ResumeAfterUpdate();
        await WaitForStateAsync(SupervisorState.Running);
        await WaitUntilAsync(() => _link.Running);

        Assert.Multiple(() =>
        {
            Assert.That(_engine.Runs, Has.Count.EqualTo(1), "The updated app adopts the runner instead of starting another.");
            Assert.That(next.View.Message, Does.Contain("Reconnected"));
        });
    }

    [Test]
    public async Task ResumeAfterUpdate_WithoutAContainer_MakesTheComputerAvailableAgain()
    {
        var supervisor = await StartSupervisorAsync();

        supervisor.ResumeAfterUpdate();

        await WaitForStateAsync(SupervisorState.Available);
        Assert.That(_engine.Runs, Is.Empty);
    }

    [Test]
    public async Task AStopStillQueuedAsTheAppGoesDownToUpdate_StopsTheRunnerAnyway()
    {
        _plans.Plan = BuiltInLaunchPlanCatalog.Plan() with { RefreshInterval = TimeSpan.FromMilliseconds(20) };
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();

        // Hold the command loop in a plan refresh, which only the shutdown cancels, so the Stop
        // asked for next (from the tray, say) is still queued when the app goes down to update.
        var refreshing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _plans.OnGet = async cancellationToken =>
        {
            refreshing.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        };
        await refreshing.Task.WaitAsync(WaitLimit);
        supervisor.RequestStop();

        supervisor.KeepRunnerOnShutdown();
        await supervisor.StopAsync(CancellationToken.None);
        _supervisor = null;
        supervisor.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.RunnerWanted, Is.False);
            Assert.That(_engine.Exists(Name), Is.False, "The Stop wins: the new version must find no runner to reconnect to.");
        });
    }

    [Test]
    public async Task RunnerWanted_ForAStartStillQueuedAsTheAppGoesDownToUpdate_IsKept()
    {
        var supervisor = await StartSupervisorAsync();
        supervisor.KeepRunnerOnShutdown();

        supervisor.RequestStart();
        await supervisor.StopAsync(CancellationToken.None);
        _supervisor = null;
        supervisor.Dispose();

        Assert.That(supervisor.RunnerWanted, Is.True, "The new version makes it available, though this one never got to.");
    }

    [Test]
    public async Task WakingFromSleep_WhileAvailable_ChecksTheGpuAgain()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        Assert.That(_engine.GpuChecks, Is.EqualTo(1));

        supervisor.NotifySuspending();
        supervisor.NotifyResumed();

        await WaitUntilAsync(() => _engine.GpuChecks == 2);
        Assert.Multiple(() =>
        {
            Assert.That(supervisor.LogLines, Has.Some.Contains("going to sleep"));
            Assert.That(supervisor.LogLines, Has.Some.Contains("woke from sleep"));
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Available));
        });
    }

    [Test]
    public async Task WakingFromSleep_WhileRunning_ChecksTheRunnerAndTheGpuAgainAtOnce()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();
        var checks = _engine.GpuChecks;

        supervisor.NotifyResumed();

        await WaitUntilAsync(() => _engine.GpuChecks == checks + 1);
        Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));
    }

    [Test]
    public async Task WakingFromSleep_WithAContainer_AndTheGpuGone_StopsOfferingTheComputer()
    {
        var supervisor = await StartSupervisorAsync();
        await MakeAvailableAsync();
        await LaunchAsync();
        var revisionBefore = _link.Profile!().OfferRevision;
        _engine.GpuCheck = new GpuAccessCheck(false, "NVIDIA driver lost after sleep");

        supervisor.NotifyResumed();

        await WaitUntilAsync(OffersNothing, "the launcher to offer nothing");
        Assert.Multiple(() =>
        {
            Assert.That(_link.Profile!().OfferRevision, Is.GreaterThan(revisionBefore), "Offering nothing is an offer of its own: launches chosen before are retired.");
            Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running), "The container keeps serving.");
            Assert.That(supervisor.View.GpuAccess?.Succeeded, Is.False);
            Assert.That(_link.Profile!().Resources.GpuCount, Is.Zero);
            Assert.That(supervisor.View.IsReady, Is.False, "Not ready for new sessions while nothing is offered.");
        });

        // Checked again by itself: offered again once the GPU is back, and the view says so.
        _engine.GpuCheck = new GpuAccessCheck(true, "GPU 0: NVIDIA GeForce RTX 4090");
        await WaitUntilAsync(() => !OffersNothing(), "the offer to come back");
        await WaitUntilAsync(() => !supervisor.View.Message.Contains("not offered", StringComparison.Ordinal), "the note to go");
    }

    [Test]
    public async Task AContainerReleasedWhileTheOfferIsPaused_HasTheChecksRunAgain()
    {
        _engine.StatusJson = Status(activeSessionId: "");
        var supervisor = await StartSupervisorAsync(warmTime: TimeSpan.FromMilliseconds(300), retryBackoff: TimeSpan.FromMinutes(5));
        await MakeAvailableAsync();
        await LaunchAsync();
        _engine.GpuCheck = new GpuAccessCheck(false, "NVIDIA driver lost after sleep");
        supervisor.NotifyResumed();
        await WaitUntilAsync(OffersNothing, "the offer to pause");

        // Released while the next check is minutes away: the checks run again now, not "Ready" offering nothing.
        await WaitUntilAsync(() => !_engine.Exists(Name), "the release");
        await WaitForStateAsync(SupervisorState.WaitingToRetry);
        Assert.That(supervisor.View.Fault?.Kind, Is.EqualTo(FaultKind.GpuUnavailable));
    }

    [Test]
    public async Task AFailedLaunch_ReachesOffloadr_BeforeTheComputerStopsBeingOffered()
    {
        await StartSupervisorAsync();
        await MakeAvailableAsync();
        var running = true;
        _link.ReportObserver = report =>
        {
            if (report.Phase == RunnerLaunchPhase.Failed)
            {
                running = _link.Running;
            }
        };
        _engine.Info = ContainerEngineInfo.Unavailable("Docker Desktop quit.", errorKind: FaultKind.EngineNotRunning);

        var launch = _link.Name("forge-neo:neo", ForgeNeo);

        await WaitUntilAsync(() => _link.OutcomeOf(launch.LaunchId) is not null && !_link.Running, "the failure and the pause");
        Assert.That(running, Is.True, "Submitted while the launcher still ran.");
    }

    [Test]
    public async Task WakingFromSleep_RetriesAWaitingStartNow()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, AutoStart = true });
        var available = _engine.Info;
        _engine.Info = ContainerEngineInfo.Unavailable("Docker Desktop is starting.");
        var supervisor = await StartSupervisorAsync(retryBackoff: TimeSpan.FromMinutes(5));
        await WaitForStateAsync(SupervisorState.WaitingToRetry);
        _engine.Info = available;

        supervisor.NotifyResumed();

        await WaitForStateAsync(SupervisorState.Available);
    }

    // The GPU check the person asks for

    [Test]
    public async Task CheckGpuAccess_WithoutDocker_ReportsAFailedCheck()
    {
        _engine.InspectImageFailure = new ContainerEngineException("docker was not found.");
        var supervisor = await StartSupervisorAsync();

        var check = await supervisor.CheckGpuAccessAsync();

        Assert.Multiple(() =>
        {
            Assert.That(check.Succeeded, Is.False);
            Assert.That(check.Output, Does.Contain("docker was not found"));
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

    [Test]
    public async Task CheckGpuAccess_UsesTheImageOfTheEditorInSettings()
    {
        _settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, SelectedEditorTemplate = "forge-neo:neo" });
        _engine.AddImage(ForgeNeo);
        var supervisor = await StartSupervisorAsync();

        var check = await supervisor.CheckGpuAccessAsync();

        Assert.Multiple(() =>
        {
            Assert.That(check.Succeeded, Is.True);
            Assert.That(_engine.GpuCheckImages, Is.EqualTo(new[] { ForgeNeo }));
        });
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

    /// <summary>A supervisor that only builds container specs, as an earlier run of the app did.</summary>
    private RunnerSupervisor Earlier()
        => new(_settings, _engine, _plans, new FakeHostGpuProbe(), new FakeLauncherLink(), new SupervisorOptions(), TimeProvider.System, NullLogger<RunnerSupervisor>.Instance);

    /// <summary>Puts a running container under the runner's name, as an earlier run of the app launched it.</summary>
    private void AddLaunchedContainer(string launchId, string editorTemplate = "comfyui-latest")
    {
        var spec = Earlier().BuildSpec(_settings.Current, BuiltInLaunchPlanCatalog.Plan().Find(editorTemplate)!);
        var labels = new Dictionary<string, string>(spec.Labels)
        {
            [RunnerSupervisor.LaunchLabel] = Guid.NewGuid().ToString("N"),
            [RunnerSupervisor.LaunchIdLabel] = launchId,
            [RunnerSupervisor.LaunchTargetLabel] = FakeLauncherLink.TargetOf(_settings.Current),
        };
        _engine.AddContainer(Name, spec.Image, running: true, labels, spec.Environment);
    }

    private static LaunchPlan ControlPlanePlan(string selected)
        => new(
            [
                new LaunchTemplate("comfyui-latest", "comfyui", "ghcr.io/offloadr/runner-comfyui@sha256:latest", "ComfyUI - current release"),
                new LaunchTemplate("comfyui-master", "comfyui", "ghcr.io/offloadr/runner-comfyui@sha256:master", "ComfyUI - master branch"),
            ],
            selected,
            TimeSpan.FromMinutes(5),
            LaunchPlanSource.ControlPlane);

    /// <summary>Holds the next `docker run`, after it created the container, until the returned source is completed.</summary>
    private TaskCompletionSource HoldNextRun(out Task running)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnRun = async token =>
        {
            _engine.OnRun = null;
            started.TrySetResult();
            await finish.Task.WaitAsync(token);
        };
        running = started.Task;
        return finish;
    }

    /// <summary>The launcher runs but offers no editor: paused, while a container may still need releasing.</summary>
    private bool OffersNothing() => _link.Running && _link.Profile?.Invoke().EditorTemplates.Count == 0;

    /// <summary>Holds the next image pull until it is cancelled; completes once it has begun.</summary>
    private Task HoldNextPull()
    {
        var pulling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.OnPull = async token =>
        {
            _engine.OnPull = null;
            pulling.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        return pulling.Task;
    }

    /// <summary>Starts, and waits until Offloadr could name a launch: accepted, with a live stream.</summary>
    private async Task MakeAvailableAsync()
    {
        _supervisor!.RequestStart();
        await WaitForStateAsync(SupervisorState.Available);
        await WaitUntilAsync(() => _supervisor.View.IsReady && _link.Running, "the launcher to be ready");
    }

    /// <summary>Has Offloadr name a launch and waits until its container runs.</summary>
    private async Task<LaunchRequest> LaunchAsync(string editorTemplate = "comfyui-latest", string? image = null)
    {
        var launch = _link.Name(editorTemplate, image ?? BuiltInLaunchPlanCatalog.Plan().Find(editorTemplate)!.Image);
        await WaitUntilAsync(
            () => _supervisor!.View.State == SupervisorState.Running &&
                  _engine.Runs.Any(run => run.Labels.TryGetValue(RunnerSupervisor.LaunchIdLabel, out var id) && id == launch.LaunchId),
            $"the container for {editorTemplate}");
        return launch;
    }

    private async Task<RunnerSupervisor> StartSupervisorAsync(
        TimeSpan? statusInterval = null,
        SupervisorInstanceLock? instanceLock = null,
        TimeProvider? time = null,
        TimeSpan? statusStaleAfter = null,
        TimeSpan? retryBackoff = null,
        TimeSpan? warmTime = null,
        SupervisorPaths? paths = null,
        TimeSpan? runTimeout = null,
        TimeSpan? lateContainerWatch = null,
        TimeSpan? shutdownMargin = null)
    {
        var options = new SupervisorOptions
        {
            MonitorInterval = TimeSpan.FromMilliseconds(20),
            StatusInterval = statusInterval ?? TimeSpan.FromMilliseconds(20),
            RetryMinBackoff = retryBackoff ?? TimeSpan.FromMilliseconds(50),
            RetryMaxBackoff = retryBackoff ?? TimeSpan.FromMilliseconds(100),
            StopTimeout = TimeSpan.FromSeconds(1),
            CancelledLaunchSettle = TimeSpan.FromSeconds(1),
            StatusStaleAfter = statusStaleAfter ?? TimeSpan.FromMinutes(1),
            WarmTime = warmTime ?? TimeSpan.FromHours(1),
            ReleaseRetryInterval = TimeSpan.FromMilliseconds(50),
            StaleOfferWait = TimeSpan.FromMilliseconds(300),
            EngineCheckInterval = TimeSpan.FromMilliseconds(50),
            RunTimeout = runTimeout ?? TimeSpan.FromSeconds(30),
            LateContainerWatch = lateContainerWatch ?? TimeSpan.FromSeconds(4),
            ShutdownMargin = shutdownMargin ?? TimeSpan.FromSeconds(60),
        };
        _supervisor = new RunnerSupervisor(_settings, _engine, _plans, _hostGpus, _link, options, time ?? TimeProvider.System, NullLogger<RunnerSupervisor>.Instance, _selection, instanceLock, paths: paths, protector: new ReversingSecretProtector());
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
                Assert.Fail($"Timed out waiting for {what ?? "condition"}; supervisor is {_supervisor?.View.State}: {_supervisor?.View.Message}\n{string.Join('\n', _supervisor?.LogLines.TakeLast(15) ?? [])}");
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
