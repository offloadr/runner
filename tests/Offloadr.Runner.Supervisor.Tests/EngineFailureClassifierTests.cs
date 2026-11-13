using Offloadr.Runner.Supervisor.Containers;

namespace Offloadr.Runner.Supervisor.Tests;

public class EngineFailureClassifierTests
{
    // What Docker Desktop, Docker Engine and Podman print, as they print it.
    [TestCase(EngineOperation.Command, "error during connect: Get \"http://%2F%2F.%2Fpipe%2FdockerDesktopLinuxEngine/v1.47/version\": open //./pipe/dockerDesktopLinuxEngine: The system cannot find the file specified.", FaultKind.EngineNotRunning)]
    [TestCase(EngineOperation.Command, "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?", FaultKind.EngineNotRunning)]
    [TestCase(EngineOperation.Pull, "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?", FaultKind.EngineNotRunning)]
    [TestCase(EngineOperation.Command, "Cannot connect to Podman. Please verify your connection to the Linux system using `podman system connection list`, or try `podman machine init` and `podman machine start` to manage a new Linux VM", FaultKind.EngineNotRunning)]
    [TestCase(EngineOperation.Pull, "error during connect: Post \"https://192.168.1.20:2376/v1.47/images/create\": dial tcp: lookup gpu-box: no such host", FaultKind.EngineNotRunning)]
    [TestCase(EngineOperation.Command, "permission denied while trying to connect to the Docker daemon socket at unix:///var/run/docker.sock: Get \"http://%2Fvar%2Frun%2Fdocker.sock/v1.47/version\": dial unix /var/run/docker.sock: connect: permission denied", FaultKind.EnginePermission)]
    [TestCase(EngineOperation.Command, "error during connect: this error may indicate that the docker daemon is not running: Get \"http://%2F%2F.%2Fpipe%2Fdocker_engine/v1.24/version\": open //./pipe/docker_engine: Access is denied.", FaultKind.EnginePermission)]
    [TestCase(EngineOperation.Run, "docker: Error response from daemon: could not select device driver \"\" with capabilities: [[gpu]].", FaultKind.GpuUnavailable)]
    [TestCase(EngineOperation.Run, "docker: Error response from daemon: failed to create task for container: failed to create shim task: OCI runtime create failed: runc create failed: unable to start container process: error during container init: error running hook #0: error running hook: exit status 1, stdout: , stderr: Auto-detected mode as 'legacy'\nnvidia-container-cli: initialization error: WSL environment detected but no adapters were found: unknown.", FaultKind.GpuUnavailable)]
    [TestCase(EngineOperation.Run, "Error: setting up CDI devices: unresolvable CDI devices nvidia.com/gpu=all", FaultKind.GpuUnavailable)]
    [TestCase(EngineOperation.Command, "nvidia-container-cli: initialization error", FaultKind.Unknown)]
    [TestCase(EngineOperation.Pull, "failed to register layer: write /usr/lib/x86_64-linux-gnu/libcudnn.so.9: no space left on device", FaultKind.DiskFull)]
    [TestCase(EngineOperation.Run, "docker: Error response from daemon: mkdir /var/lib/docker/overlay2/abc-init: no space left on device.", FaultKind.DiskFull)]
    [TestCase(EngineOperation.Pull, "Error response from daemon: manifest unknown", FaultKind.ImageUnavailable)]
    [TestCase(EngineOperation.Pull, "Error response from daemon: Head \"https://ghcr.io/v2/offloadr/runner-comfyui/manifests/latest\": denied: denied", FaultKind.ImageUnavailable)]
    [TestCase(EngineOperation.Pull, "Error response from daemon: pull access denied for offloadr/runner, repository does not exist or may require 'docker login'", FaultKind.ImageUnavailable)]
    [TestCase(EngineOperation.Pull, "Error response from daemon: Get \"https://ghcr.io/v2/\": dial tcp: lookup ghcr.io: no such host", FaultKind.RegistryUnreachable)]
    [TestCase(EngineOperation.Pull, "Error response from daemon: Get \"https://ghcr.io/v2/\": net/http: request canceled while waiting for connection (Client.Timeout exceeded while awaiting headers)", FaultKind.RegistryUnreachable)]
    [TestCase(EngineOperation.Pull, "Error response from daemon: Get \"https://ghcr.io/v2/\": net/http: TLS handshake timeout", FaultKind.RegistryUnreachable)]
    [TestCase(EngineOperation.Pull, "Error response from daemon: toomanyrequests: retry-after: 1.2ms, allowed: 44000/minute", FaultKind.RegistryUnreachable)]
    [TestCase(EngineOperation.Pull, "Error: initializing source docker://ghcr.io/offloadr/runner-comfyui:latest: pinging container registry ghcr.io: Get \"https://ghcr.io/v2/\": dial tcp 140.82.121.34:443: i/o timeout", FaultKind.RegistryUnreachable)]
    [TestCase(EngineOperation.Command, "Error response from daemon: Get \"https://ghcr.io/v2/\": dial tcp: lookup ghcr.io: no such host", FaultKind.Unknown)]
    [TestCase(EngineOperation.Run, "docker: Error response from daemon: Conflict. The container name \"/offloadr-runner\" is already in use.", FaultKind.Unknown)]
    [TestCase(EngineOperation.Pull, "", FaultKind.Unknown)]
    public void Classify_TellsTheCauseFromWhatTheEnginePrinted(EngineOperation operation, string output, FaultKind expected)
        => Assert.That(EngineFailureClassifier.Classify(operation, output), Is.EqualTo(expected));

    [Test]
    public void Exception_KnownKind_WinsOverTheOutput()
    {
        var failure = new ContainerEngineException("Docker did not answer within 30 seconds.", "no space left on device", kind: FaultKind.EngineNotResponding);

        Assert.That(failure.Kind, Is.EqualTo(FaultKind.EngineNotResponding));
    }

    [Test]
    public void Fault_WithAdviceOfItsOwn_KeepsTheEngineWordsAsTheDetail()
    {
        var fault = SupervisorFault.For(FaultKind.DiskFull, "Could not pull x. no space left on device");

        Assert.Multiple(() =>
        {
            Assert.That(fault.Title, Is.EqualTo("Docker is out of disk space"));
            Assert.That(fault.Message, Does.Contain("Free space"));
            Assert.That(fault.Detail, Is.EqualTo("Could not pull x. no space left on device"));
            Assert.That(fault.Action, Is.EqualTo(FaultAction.FreeDiskSpace));
        });
    }

    [Test]
    public void Fault_WithoutAdvice_ShowsTheSupervisorsMessage()
    {
        var fault = SupervisorFault.For(FaultKind.Unknown, "Could not start the runner container. Something odd.");

        Assert.Multiple(() =>
        {
            Assert.That(fault.Message, Is.EqualTo("Could not start the runner container. Something odd."));
            Assert.That(fault.Detail, Is.Null);
            Assert.That(fault.Action, Is.EqualTo(FaultAction.None));
        });
    }

    [Test]
    public void EveryKind_HasATitle()
    {
        foreach (var kind in Enum.GetValues<FaultKind>())
        {
            Assert.That(SupervisorFault.For(kind, "x").Title, Is.Not.Empty, kind.ToString());
        }
    }
}
