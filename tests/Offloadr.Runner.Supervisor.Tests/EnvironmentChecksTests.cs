using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hardware;
using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor.Tests;

public class EnvironmentChecksTests
{
    private static readonly SupervisorView Unchecked = SupervisorView.Initial(DateTimeOffset.UtcNow);

    private static readonly ContainerEngineInfo DockerDesktop =
        new(true, ContainerEngineKind.Docker, "docker", "28.1.0", "linux", "Docker Desktop", true, null);

    private static readonly HostGpuReport OneGpu = new([new HostGpu("NVIDIA GeForce RTX 4090", 24564)], null);

    [Test]
    public void Checks_AreNotRunUntilTheSupervisorHasLooked()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EnvironmentChecks.Docker(Unchecked).Ok, Is.Null);
            Assert.That(EnvironmentChecks.HostGpu(Unchecked).Ok, Is.Null);
            Assert.That(EnvironmentChecks.GpuInContainers(Unchecked).Ok, Is.Null);
            Assert.That(EnvironmentChecks.ComputerReady(Unchecked), Is.Null);
        });
    }

    [Test]
    public void Docker_ThatAnswers_SaysWhereAndWhichVersion()
    {
        var check = EnvironmentChecks.Docker(Unchecked with { Engine = DockerDesktop });

        Assert.That(check, Is.EqualTo(new EnvironmentCheck("Docker", true, "Docker Desktop, engine 28.1.0.")));
    }

    [TestCase(FaultKind.EngineMissing, FaultAction.InstallEngine, "Docker was not found. Install Docker Desktop")]
    [TestCase(FaultKind.EngineNotRunning, FaultAction.StartEngine, "Docker is not running. Start Docker Desktop")]
    [TestCase(FaultKind.WindowsContainers, FaultAction.StartEngine, "Docker runs Windows containers.")]
    public void Docker_ThatFailed_SaysWhatToDo_WithItsOwnWordsFoldedAway(FaultKind kind, FaultAction fix, string detail)
    {
        var view = Unchecked with { Engine = ContainerEngineInfo.Unavailable("error during connect: pipe not found", "docker", errorKind: kind) };

        var check = EnvironmentChecks.Docker(view);

        Assert.Multiple(() =>
        {
            Assert.That(check.Ok, Is.False);
            Assert.That(check.Detail, Does.StartWith(detail));
            Assert.That(check.Fix, Is.EqualTo(fix));
            Assert.That(check.Technical, Is.EqualTo("error during connect: pipe not found"));
            Assert.That(EnvironmentChecks.ComputerReady(view with { HostGpus = OneGpu }), Is.False);
        });
    }

    [Test]
    public void Docker_ThatFailedForAnUnknownReason_ShowsTheError()
    {
        var check = EnvironmentChecks.Docker(Unchecked with { Engine = ContainerEngineInfo.Unavailable("something odd") });

        Assert.That(check, Is.EqualTo(new EnvironmentCheck("Docker", false, "something odd")));
    }

    [Test]
    public void Podman_GetsPodmansOwnAdvice()
    {
        var view = Unchecked with { Engine = ContainerEngineInfo.Unavailable("no machine", "podman", ContainerEngineKind.Podman, FaultKind.EngineNotRunning) };

        Assert.That(EnvironmentChecks.Docker(view).Detail, Does.StartWith("Podman is not running."));
    }

    [Test]
    public void HostGpu_Missing_OffersTheNvidiaDriver()
    {
        var check = EnvironmentChecks.HostGpu(Unchecked with { HostGpus = HostGpuReport.None("No NVIDIA driver found (nvidia-smi is not available).") });

        Assert.Multiple(() =>
        {
            Assert.That(check.Ok, Is.False);
            Assert.That(check.Fix, Is.EqualTo(FaultAction.InstallGpuDriver));
            Assert.That(check.Technical, Does.Contain("nvidia-smi"));
        });
    }

    [Test]
    public void HostGpu_Found_NamesItAndItsMemory()
    {
        var check = EnvironmentChecks.HostGpu(Unchecked with { HostGpus = OneGpu });

        Assert.Multiple(() =>
        {
            Assert.That(check.Ok, Is.True);
            Assert.That(check.Detail, Does.StartWith("NVIDIA GeForce RTX 4090 (24"));
            Assert.That(EnvironmentChecks.ComputerReady(Unchecked with { Engine = DockerDesktop, HostGpus = OneGpu }), Is.True);
            Assert.That(EnvironmentChecks.ComputerReady(Unchecked with { HostGpus = OneGpu }), Is.Null, "Docker is still to be checked.");
        });
    }

    [Test]
    public void GpuInContainers_CountsTheGpus_AndOffersTheGuideWhenItFails()
    {
        var two = EnvironmentChecks.GpuInContainers(Unchecked with { GpuAccess = new GpuAccessCheck(true, "GPU 0: A\nGPU 1: B\n") });
        var one = EnvironmentChecks.GpuInContainers(Unchecked with { GpuAccess = new GpuAccessCheck(true, "GPU 0: A") });
        var failed = EnvironmentChecks.GpuInContainers(Unchecked with { GpuAccess = new GpuAccessCheck(false, "could not select device driver") });

        Assert.Multiple(() =>
        {
            Assert.That(two.Detail, Is.EqualTo("2 GPUs visible to the runner."));
            Assert.That(one.Detail, Is.EqualTo("The runner can use the GPU."));
            Assert.That(failed.Ok, Is.False);
            Assert.That(failed.Fix, Is.EqualTo(FaultAction.SetUpGpu));
            Assert.That(failed.Technical, Is.EqualTo("could not select device driver"));
        });
    }

    [Test]
    public void RunnerKey_Missing_SendsThePersonToSettings()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EnvironmentChecks.RunnerKey(new RunnerSettings()).Fix, Is.EqualTo(FaultAction.OpenSettings));
            Assert.That(EnvironmentChecks.RunnerKey(new RunnerSettings { RunnerKey = new string('k', RunnerSettings.MinimumRunnerKeyLength) }).Ok, Is.True);
        });
    }

    [Test]
    public void RunnerKey_ThatCannotWork_SaysWhy()
    {
        var check = EnvironmentChecks.RunnerKey(new RunnerSettings { RunnerKey = "tooshort" });

        Assert.Multiple(() =>
        {
            Assert.That(check.Ok, Is.False);
            Assert.That(check.Detail, Does.StartWith("The runner key is too short."));
            Assert.That(check.Fix, Is.EqualTo(FaultAction.OpenSettings));
        });
    }

    [Test]
    public void SetupPrompt_OpensSetupOnce_AndOnlyWithoutAKey()
    {
        var prompt = new SetupPrompt();
        var withKey = new RunnerSettings { RunnerKey = "key" };

        Assert.Multiple(() =>
        {
            Assert.That(prompt.TryOffer(withKey), Is.False);
            Assert.That(prompt.TryOffer(new RunnerSettings()), Is.True);
            Assert.That(prompt.TryOffer(new RunnerSettings()), Is.False, "Skip for now stays skipped.");
        });
    }

    [Test]
    public void FaultHelp_LinksOutsideTheAppOnlyForInstallsAndGuides()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FaultHelp.For(FaultAction.InstallEngine)?.Url, Is.EqualTo(FaultHelp.InstallDockerDesktop));
            Assert.That(FaultHelp.For(FaultAction.SetUpGpu)?.Url, Is.EqualTo(FaultHelp.DockerGpuSupport));
            Assert.That(FaultHelp.For(FaultAction.InstallGpuDriver)?.Url, Is.EqualTo(FaultHelp.NvidiaDrivers));
            Assert.That(FaultHelp.For(FaultAction.StartEngine), Is.EqualTo(new FaultHelp("Check again", null)));
            Assert.That(FaultHelp.For(FaultAction.OpenSettings)?.Url, Is.Null);
            Assert.That(FaultHelp.For(FaultAction.CheckConnection)?.Url, Is.Null);
            Assert.That(FaultHelp.For(FaultAction.FreeDiskSpace), Is.Null, "The advice says what to free.");
            Assert.That(FaultHelp.For(FaultAction.None), Is.Null);
        });
    }

    [Test]
    public void FaultHelp_LinksAreHttps()
    {
        foreach (var action in Enum.GetValues<FaultAction>())
        {
            if (FaultHelp.For(action)?.Url is { } url)
            {
                Assert.That(new Uri(url).Scheme, Is.EqualTo(Uri.UriSchemeHttps), action.ToString());
            }
        }
    }
}
