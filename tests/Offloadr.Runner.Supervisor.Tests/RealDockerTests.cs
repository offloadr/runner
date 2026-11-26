using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Processes;
using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor.Tests;

/// <summary>
/// Drives a real Docker engine with a locally built runner image. Opt in with
/// RUNNER_REAL_DOCKER_IMAGE=&lt;image&gt;; the runner points at an unreachable endpoint and the launcher is a
/// fake, so nothing reaches a control plane. GPU access is not required.
/// </summary>
[Category("RealDocker")]
public class RealDockerTests
{
    private const string Key = "real-docker-test-key-not-valid-0123456789abcdef";

    [Test]
    public async Task Supervisor_StartsReadsStatusAndStopsARealContainer()
    {
        var image = Environment.GetEnvironmentVariable("RUNNER_REAL_DOCKER_IMAGE");
        if (string.IsNullOrWhiteSpace(image))
        {
            Assert.Ignore("Set RUNNER_REAL_DOCKER_IMAGE to a locally built runner image to run this test.");
        }

        var processes = new SystemProcessRunner();
        var engine = new DockerCliContainerEngine(processes, () => null);
        var plans = new FakeLaunchPlanProvider
        {
            Plan = new LaunchPlan([new LaunchTemplate("comfyui-master", "comfyui", image, "Local test image")], null, TimeSpan.FromMinutes(5), LaunchPlanSource.BuiltIn),
        };
        var settings = new FakeSettingsStore(new RunnerSettings { RunnerKey = Key, OffloadrApiOrigin = "https://127.0.0.1:9" });
        var options = new SupervisorOptions
        {
            ContainerName = "offloadr-runner-test-" + Guid.NewGuid().ToString("n")[..8],
            MonitorInterval = TimeSpan.FromMilliseconds(500),
            StatusInterval = TimeSpan.FromSeconds(1),
            StopTimeout = TimeSpan.FromSeconds(10),
        };
        // A machine without NVIDIA container support cannot honour --gpus; this test checks supervision, not GPUs.
        var noGpuEngine = new WithoutGpus(engine);
        var link = new FakeLauncherLink();
        using var supervisor = new RunnerSupervisor(settings, noGpuEngine, plans, new FakeHostGpuProbe(), link, options, TimeProvider.System, NullLogger<RunnerSupervisor>.Instance);
        await supervisor.StartAsync(CancellationToken.None);
        try
        {
            supervisor.RequestStart();
            await WaitUntilAsync(() => supervisor.View.IsReady && link.Running, supervisor, TimeSpan.FromMinutes(3));
            link.Name("comfyui-master", image);
            await WaitUntilAsync(() => supervisor.View.RunnerStatus is not null, supervisor, TimeSpan.FromMinutes(3));

            var inspect = await processes.RunAsync(
                new ProcessSpec("docker", ["container", "inspect", "--format", "{{json .Args}} {{json .Config.Cmd}} {{json .Config.Labels}}", options.ContainerName]),
                CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(supervisor.View.State, Is.EqualTo(SupervisorState.Running));
                Assert.That(supervisor.View.RunnerStatus!.ActiveSessionId, Is.Empty);
                Assert.That(inspect.StandardOutput, Does.Contain(RunnerSupervisor.ManagedLabel));
                Assert.That(inspect.StandardOutput, Does.Not.Contain(Key));
                Assert.That(supervisor.LogLines, Has.Some.Contains("AGPL-3.0-only"));
                Assert.That(supervisor.LogLines, Has.None.Contains(Key));
            });

            supervisor.RequestStop();
            await WaitUntilAsync(() => supervisor.View.State == SupervisorState.Stopped, supervisor, TimeSpan.FromMinutes(1));
            Assert.That(await engine.InspectContainerAsync(options.ContainerName, CancellationToken.None), Is.Null);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
            await engine.RemoveAsync(options.ContainerName, CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, RunnerSupervisor supervisor, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out; supervisor is {supervisor.View.State}: {supervisor.View.Message}\n{string.Join('\n', supervisor.LogLines.TakeLast(20))}");
            }

            await Task.Delay(250);
        }
    }

    private sealed class WithoutGpus(IContainerEngine inner) : IContainerEngine
    {
        public Task<ContainerEngineInfo> ProbeAsync(CancellationToken cancellationToken) => inner.ProbeAsync(cancellationToken);

        public Task<ContainerEnginePin?> CurrentContextAsync(CancellationToken cancellationToken) => inner.CurrentContextAsync(cancellationToken);

        public Task<string?> ReadEnvironmentVariableAsync(string container, string name, CancellationToken cancellationToken) => inner.ReadEnvironmentVariableAsync(container, name, cancellationToken);

        public Task PullAsync(string image, Action<string> onProgress, CancellationToken cancellationToken) => inner.PullAsync(image, onProgress, cancellationToken);

        public Task<ImageInfo?> InspectImageAsync(string image, CancellationToken cancellationToken) => inner.InspectImageAsync(image, cancellationToken);

        public Task<bool> RemoveImageAsync(string reference, CancellationToken cancellationToken) => inner.RemoveImageAsync(reference, cancellationToken);

        public Task<IReadOnlyList<StoredImage>> ListImagesAsync(CancellationToken cancellationToken) => inner.ListImagesAsync(cancellationToken);

        public Task<string> CreateAsync(ContainerSpec spec, CancellationToken cancellationToken) => inner.CreateAsync(spec with { Gpus = false }, cancellationToken);

        public Task StartAsync(string container, CancellationToken cancellationToken, Func<bool>? stillWanted = null) => inner.StartAsync(container, cancellationToken, stillWanted);

        public Task<ContainerInfo?> InspectContainerAsync(string name, CancellationToken cancellationToken) => inner.InspectContainerAsync(name, cancellationToken);

        public Task<string?> ReadFileAsync(string container, string path, CancellationToken cancellationToken) => inner.ReadFileAsync(container, path, cancellationToken);

        public Task FollowLogsAsync(string container, int tail, Action<string> onLine, CancellationToken cancellationToken) => inner.FollowLogsAsync(container, tail, onLine, cancellationToken);

        public Task StopAsync(string container, TimeSpan timeout, CancellationToken cancellationToken) => inner.StopAsync(container, timeout, cancellationToken);

        public Task RemoveAsync(string container, CancellationToken cancellationToken) => inner.RemoveAsync(container, cancellationToken);

        public Task<bool> RemoveStoppedAsync(string container, CancellationToken cancellationToken) => inner.RemoveStoppedAsync(container, cancellationToken);

        public Task<ContainerInfo?> FindByLabelAsync(string label, string value, CancellationToken cancellationToken) => inner.FindByLabelAsync(label, value, cancellationToken);

        public Task<IReadOnlyList<Offloadr.Runner.Supervisor.Hardware.HostGpu>> ListContainerGpusAsync(string image, CancellationToken cancellationToken) => inner.ListContainerGpusAsync(image, cancellationToken);

        public Task<GpuAccessCheck> CheckGpuAccessAsync(string image, CancellationToken cancellationToken) => Task.FromResult(new GpuAccessCheck(true, "GPU 0: not checked"));
    }
}
