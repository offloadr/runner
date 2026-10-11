using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Processes;

namespace Offloadr.Runner.Supervisor.Tests;

public class DockerCliArgumentsTests
{
    private static readonly ContainerSpec Spec = new(
        "offloadr-runner",
        "ghcr.io/offloadr/runner-comfyui:latest",
        new Dictionary<string, string> { ["RUNNER_SECRET"] = "secret-value", ["OFFLOADR_API_GRPC"] = "https://offloadr.studio" },
        [new VolumeMount(@"D:\models", "/local", ReadOnly: true)],
        new Dictionary<string, string> { ["app.offloadr.runner.managed"] = "true" },
        Gpus: true);

    [Test]
    public void Run_NamesEnvironmentVariablesWithoutTheirValues()
    {
        var arguments = DockerCliArguments.Run(Spec, ContainerEngineKind.Docker);

        Assert.That(arguments, Is.EqualTo(new[]
        {
            "run", "--detach", "--name", "offloadr-runner", "--pull", "never", "--restart", "no",
            "--gpus", "all",
            "--label", "app.offloadr.runner.managed=true",
            "--env", "OFFLOADR_API_GRPC",
            "--env", "RUNNER_SECRET",
            "--security-opt", "label=disable",
            "--volume", @"D:\models:/local:ro",
            "ghcr.io/offloadr/runner-comfyui:latest",
        }));
    }

    [Test]
    public void Run_WithAModelsFolderOnDocker_DisablesSelinuxLabelling()
    {
        var withModels = new ContainerSpec(
            "offloadr-runner",
            "image",
            new Dictionary<string, string>(),
            [new VolumeMount("/srv/models", "/local", ReadOnly: true)],
            new Dictionary<string, string>(),
            Gpus: true);
        var withoutModels = withModels with { Volumes = [] };

        Assert.Multiple(() =>
        {
            Assert.That(string.Join(" ", DockerCliArguments.Run(withModels, ContainerEngineKind.Docker)), Does.Contain("--security-opt label=disable"));
            Assert.That(string.Join(" ", DockerCliArguments.Run(withoutModels, ContainerEngineKind.Docker)), Does.Not.Contain("label=disable"));
        });
    }

    [Test]
    public void Run_UsesTheCdiDeviceForPodman()
    {
        var arguments = DockerCliArguments.Run(Spec, ContainerEngineKind.Podman);

        Assert.Multiple(() =>
        {
            Assert.That(arguments, Does.Contain("nvidia.com/gpu=all"));
            Assert.That(string.Join(" ", arguments), Does.Contain("--security-opt label=disable"));
            Assert.That(arguments, Does.Not.Contain("--gpus"));
        });
    }

    [Test]
    public void GpuCheck_OverridesTheRunnerEntrypoint()
    {
        Assert.That(
            DockerCliArguments.GpuCheck("image:tag", ContainerEngineKind.Docker, "offloadr-gpu-check-1"),
            Is.EqualTo(new[] { "run", "--rm", "--name", "offloadr-gpu-check-1", "--pull", "never", "--gpus", "all", "--entrypoint", "nvidia-smi", "image:tag", "-L" }));
    }
}

public class DockerCliJsonTests
{
    [Test]
    public void ServerVersion_ReadsDockerAndPodmanShapes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DockerCliJson.ServerVersion("""{"Client":{"Version":"29.0.1"},"Server":{"Version":"29.0.0"}}"""), Is.EqualTo("29.0.0"));
            Assert.That(DockerCliJson.ServerVersion("""{"Client":{"Version":"29.0.1"},"Server":null}"""), Is.Null);
            Assert.That(DockerCliJson.ServerVersion("not json"), Is.Null);
            // Local podman is daemonless and prints only a client; a docker client alone means no daemon.
            Assert.That(DockerCliJson.ServerVersion("""{"Client":{"Version":"4.9.3"}}""", ContainerEngineKind.Podman), Is.EqualTo("4.9.3"));
            Assert.That(DockerCliJson.ServerVersion("""{"Client":{"Version":"5.4.0"},"Server":{"Version":"5.3.1"}}""", ContainerEngineKind.Podman), Is.EqualTo("5.3.1"));
            Assert.That(DockerCliJson.ServerVersion("""{"Client":{"Version":"29.0.1"}}"""), Is.Null);
        });
    }

    [Test]
    public void Info_ReportsOsAndNvidiaRuntime()
    {
        var (os, description, nvidia) = DockerCliJson.Info("""{"OSType":"linux","OperatingSystem":"Docker Desktop","Runtimes":{"runc":{},"nvidia":{}}}""");

        Assert.Multiple(() =>
        {
            Assert.That(os, Is.EqualTo("linux"));
            Assert.That(description, Is.EqualTo("Docker Desktop"));
            Assert.That(nvidia, Is.True);
        });
    }

    [Test]
    public void Container_ReadsStateImageAndLabels()
    {
        var container = DockerCliJson.Container("""
            [{"Id":"abc","Image":"sha256:feed","Config":{"Image":"ghcr.io/offloadr/runner-comfyui:latest","Labels":{"app.offloadr.runner.managed":"true"}},
              "State":{"Running":false,"ExitCode":137}}]
            """);

        Assert.That(container, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(container!.Id, Is.EqualTo("abc"));
            Assert.That(container.Running, Is.False);
            Assert.That(container.ExitCode, Is.EqualTo(137));
            Assert.That(container.ImageId, Is.EqualTo("sha256:feed"));
            Assert.That(container.Labels["app.offloadr.runner.managed"], Is.EqualTo("true"));
        });
    }

    [Test]
    public void Image_ReadsTheFirstRepoDigest()
    {
        var image = DockerCliJson.Image("""[{"Id":"sha256:1","RepoDigests":["ghcr.io/offloadr/runner-comfyui@sha256:2"]}]""");

        Assert.That(image, Is.EqualTo(new ImageInfo("sha256:1", "ghcr.io/offloadr/runner-comfyui@sha256:2")));
    }
}

public class ContainerEngineLocatorTests
{
    [Test]
    public void PrefersDockerOnPath()
    {
        var files = new HashSet<string> { Path.Combine("/opt/bin", "docker"), Path.Combine("/usr/bin", "podman") };

        var found = ContainerEngineLocator.Locate(null, $"/usr/bin{Path.PathSeparator}/opt/bin", isWindows: false, files.Contains, null);

        Assert.That(found, Is.EqualTo(new LocatedEngine(Path.Combine("/opt/bin", "docker"), ContainerEngineKind.Docker)));
    }

    [Test]
    public void FallsBackToPodman()
    {
        var files = new HashSet<string> { Path.Combine("/usr/bin", "podman") };

        var found = ContainerEngineLocator.Locate(null, "/usr/bin", isWindows: false, files.Contains, null);

        Assert.That(found?.Kind, Is.EqualTo(ContainerEngineKind.Podman));
    }

    [Test]
    public void FindsDockerDesktopWhenItIsNotOnPath()
    {
        var expected = Path.Combine(@"C:\Program Files", "Docker", "Docker", "resources", "bin", "docker.exe");

        var found = ContainerEngineLocator.Locate(null, string.Empty, isWindows: true, path => path == expected, @"C:\Program Files");

        Assert.That(found?.Executable, Is.EqualTo(expected));
    }

    [Test]
    public void ResolvesAConfiguredNameOnPath_AndRejectsAMissingPath()
    {
        var files = new HashSet<string> { Path.Combine("/usr/bin", "podman") };

        Assert.Multiple(() =>
        {
            Assert.That(ContainerEngineLocator.Locate("podman", "/usr/bin", false, files.Contains, null)?.Kind, Is.EqualTo(ContainerEngineKind.Podman));
            Assert.That(ContainerEngineLocator.Locate("/missing/docker", "/usr/bin", false, files.Contains, null), Is.Null);
        });
    }

    [Test]
    public void FindsDockerDesktopOnMacOSWhenItIsNotOnPath()
    {
        var perUser = Path.Combine("/Users/someone", ".docker", "bin", "docker");
        const string inTheApp = "/Applications/Docker.app/Contents/Resources/bin/docker";

        Assert.Multiple(() =>
        {
            Assert.That(ContainerEngineLocator.Locate(null, string.Empty, false, path => path == perUser, null, "/Users/someone")?.Executable, Is.EqualTo(perUser));
            Assert.That(ContainerEngineLocator.Locate(null, string.Empty, false, path => path == inTheApp, null, "/Users/someone")?.Executable, Is.EqualTo(inTheApp));
        });
    }

    [Test]
    public void RelativePaths_AreMadeAbsolute()
    {
        var configured = Path.Combine(".", "tools", "docker");
        var onRelativePath = Path.Combine("bin", "podman");

        Assert.Multiple(() =>
        {
            Assert.That(ContainerEngineLocator.Locate(configured, string.Empty, false, path => path == configured, null)?.Executable, Is.EqualTo(Path.GetFullPath(configured)));
            Assert.That(ContainerEngineLocator.Locate(null, "bin", false, path => path == onRelativePath, null)?.Executable, Is.EqualTo(Path.GetFullPath(onRelativePath)));
        });
    }
}

public class DockerCliContainerEngineTests
{
    private static readonly string Docker = Environment.ProcessPath!;

    [Test]
    public async Task Probe_ReportsAStoppedDaemon()
    {
        var processes = new FakeProcessRunner().On("version", 1, """{"Client":{"Version":"29.0.1"},"Server":null}""");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        var info = await engine.ProbeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(info.Available, Is.False);
            Assert.That(info.Error, Does.Contain("not running"));
        });
    }

    [Test]
    public async Task Probe_AcceptsDaemonlessPodman()
    {
        var directory = Directory.CreateTempSubdirectory("offloadr-podman-");
        try
        {
            var podman = Path.Combine(directory.FullName, "podman");
            await File.WriteAllTextAsync(podman, string.Empty);
            var processes = new FakeProcessRunner()
                .On("version", 0, """{"Client":{"APIVersion":"4.9.3","Version":"4.9.3","OsArch":"linux/amd64"}}""")
                .On("info", 0, """{"host":{"os":"linux","distribution":{"distribution":"fedora"}}}""");
            var engine = new DockerCliContainerEngine(processes, () => podman);

            var info = await engine.ProbeAsync(CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(info.Available, Is.True, info.Error);
                Assert.That(info.Kind, Is.EqualTo(ContainerEngineKind.Podman));
                Assert.That(info.ServerVersion, Is.EqualTo("4.9.3"));
            });
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Probe_RejectsWindowsContainers()
    {
        var processes = new FakeProcessRunner()
            .On("version", 0, """{"Server":{"Version":"29.0.0"}}""")
            .On("info", 0, """{"OSType":"windows"}""");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        var info = await engine.ProbeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(info.Available, Is.False);
            Assert.That(info.Error, Does.Contain("Linux containers"));
        });
    }

    [Test]
    public async Task Probe_ReportsAMissingEngine()
    {
        var engine = new DockerCliContainerEngine(new FakeProcessRunner(), () => Path.Combine(Path.GetTempPath(), "no-such-docker"));

        var info = await engine.ProbeAsync(CancellationToken.None);

        Assert.That(info.Available, Is.False);
    }

    [Test]
    public void FollowLogs_ReportsAStreamThatFails()
    {
        var processes = new FakeProcessRunner().On("logs", 1, stderr: "error during connect");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        Assert.That(
            async () => await engine.FollowLogsAsync("offloadr-runner", 0, _ => { }, CancellationToken.None),
            Throws.InstanceOf<ContainerEngineException>());
        Assert.That(processes.Calls.Single().Arguments, Is.EqualTo(new[] { "logs", "--follow", "--tail", "0", "offloadr-runner" }));
    }

    [Test]
    public async Task GpuCheck_ThatHangs_ReportsAFailedCheck()
    {
        var processes = new FakeProcessRunner { OnRun = (spec, token) => spec.Arguments[0] == "run" ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask };
        var engine = new DockerCliContainerEngine(processes, () => Docker, TimeSpan.FromMilliseconds(50));

        var check = await engine.CheckGpuAccessAsync("image:tag", CancellationToken.None);

        // The hanging container keeps the GPU until it is removed; killing the CLI does not stop it.
        var name = processes.Calls[0].Arguments[processes.Calls[0].Arguments.ToList().IndexOf("--name") + 1];
        Assert.Multiple(() =>
        {
            Assert.That(check.Succeeded, Is.False);
            Assert.That(check.Output, Does.Contain("did not finish"));
            Assert.That(name, Does.StartWith("offloadr-gpu-check-"));
            Assert.That(processes.Calls.Skip(1).Select(call => call.Arguments), Is.EqualTo(new[] { new[] { "rm", "--force", name } }));
        });
    }

    [Test]
    public async Task GpuCheck_CancelledWhileRunning_RemovesItsContainer()
    {
        using var cancellation = new CancellationTokenSource();
        var processes = new FakeProcessRunner
        {
            OnRun = async (spec, token) =>
            {
                if (spec.Arguments[0] == "run")
                {
                    await cancellation.CancelAsync();
                    await Task.Delay(Timeout.Infinite, token);
                }
            },
        };
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        Assert.That(async () => await engine.CheckGpuAccessAsync("image:tag", cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(processes.Calls.Select(call => call.Arguments[0]), Is.EqualTo(new[] { "run", "rm" }));
    }

    [Test]
    public async Task GpuCheck_WhenTheEngineCannotStart_ReportsAFailedCheck()
    {
        var processes = new FakeProcessRunner
        {
            OnRun = (spec, _) => throw new ProcessStartException(spec.FileName, new FileNotFoundException("missing")),
        };
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        var check = await engine.CheckGpuAccessAsync("image:tag", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(check.Succeeded, Is.False);
            Assert.That(check.Output, Does.Contain("Could not start"));
        });
    }

    [Test]
    public async Task Probe_RecognizesPodmanInstalledAsDocker()
    {
        // podman-docker: the executable is called docker, but Podman answers.
        var processes = new FakeProcessRunner()
            .On("version", 0, """{"Client":{"APIVersion":"4.9.3","Version":"4.9.3","OsArch":"linux/amd64","Os":"linux"}}""")
            .On("info", 0, """{"host":{"os":"linux","distribution":{"distribution":"fedora"}}}""")
            .On("run", 0, "container-id\n");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        var info = await engine.ProbeAsync(CancellationToken.None);
        await engine.RunDetachedAsync(
            new ContainerSpec("offloadr-runner", "image", new Dictionary<string, string>(), [], new Dictionary<string, string>(), Gpus: true),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(info.Available, Is.True, info.Error);
            Assert.That(info.Kind, Is.EqualTo(ContainerEngineKind.Podman));
            Assert.That(info.ServerVersion, Is.EqualTo("4.9.3"));
            Assert.That(processes.Calls.Last().Arguments, Does.Contain("nvidia.com/gpu=all"));
        });
    }

    [Test]
    public void ContainerCommands_WhenTheExecutableNoLongerStarts_ReportAnEngineFailure()
    {
        var processes = new FakeProcessRunner
        {
            OnRun = (spec, _) => throw new ProcessStartException(spec.FileName, new UnauthorizedAccessException("Access is denied.")),
        };
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        Assert.Multiple(() =>
        {
            Assert.That(
                async () => await engine.InspectContainerAsync("offloadr-runner", CancellationToken.None),
                Throws.InstanceOf<ContainerEngineException>().With.Message.Contains("Access is denied"));
            Assert.That(
                async () => await engine.StopAsync("offloadr-runner", TimeSpan.FromSeconds(1), CancellationToken.None),
                Throws.InstanceOf<ContainerEngineException>());
            Assert.That(
                async () => await engine.FollowLogsAsync("offloadr-runner", 0, _ => { }, CancellationToken.None),
                Throws.InstanceOf<ContainerEngineException>());
        });
    }

    [Test]
    public async Task Probe_WhenTheExecutableNoLongerStarts_ReportsTheEngineUnavailable()
    {
        var processes = new FakeProcessRunner
        {
            OnRun = (spec, _) => throw new ProcessStartException(spec.FileName, new UnauthorizedAccessException("Access is denied.")),
        };
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        var info = await engine.ProbeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(info.Available, Is.False);
            Assert.That(info.Error, Does.Contain("Access is denied"));
        });
    }

    [Test]
    public async Task Inspect_UsesTheDefaultJsonOutput_WhichPodmanPrintsToo()
    {
        var processes = new FakeProcessRunner()
            .On("container", 0, """[{"Id":"abc","Config":{"Image":"image","Labels":{}},"State":{"Running":true}}]""")
            .On("image", 0, """[{"Id":"sha256:1","RepoDigests":[]}]""");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        var container = await engine.InspectContainerAsync("offloadr-runner", CancellationToken.None);
        var image = await engine.InspectImageAsync("image:tag", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(container?.Running, Is.True);
            Assert.That(image?.Id, Is.EqualTo("sha256:1"));
            Assert.That(processes.Calls.Select(call => string.Join(' ', call.Arguments)), Is.EqualTo(new[]
            {
                "container inspect offloadr-runner",
                "image inspect image:tag",
            }));
        });
    }

    [TestCase("Error response from daemon: No such container: offloadr-runner")]
    [TestCase("Error: no such container offloadr-runner")]
    [TestCase("Error: no container with name or ID \"offloadr-runner\" found: no such container")]
    public async Task Inspect_ReportsAMissingContainerAsNone(string stderr)
    {
        var processes = new FakeProcessRunner().On("container", 1, stderr: stderr);
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        Assert.That(await engine.InspectContainerAsync("offloadr-runner", CancellationToken.None), Is.Null);
    }

    [Test]
    public void Inspect_DoesNotMistakeAConnectionErrorForAMissingContainer()
    {
        var processes = new FakeProcessRunner()
            .On("container", 1, stderr: "error during connect: Get \"http://remote:2375/v1.47/containers/offloadr-runner/json\": dial tcp: lookup remote: no such host");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        Assert.That(
            async () => await engine.InspectContainerAsync("offloadr-runner", CancellationToken.None),
            Throws.InstanceOf<ContainerEngineException>());
    }

    [Test]
    public async Task Probe_WhenInfoFails_ReportsTheEngineUnavailable()
    {
        var processes = new FakeProcessRunner()
            .On("version", 0, """{"Client":{"Version":"29.0.1"},"Server":{"Version":"29.0.0"}}""")
            .On("info", 1, stderr: "Cannot connect to the Docker daemon. Is the docker daemon running?");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        var info = await engine.ProbeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(info.Available, Is.False);
            Assert.That(info.Error, Does.Contain("Cannot connect"));
        });
    }

    [Test]
    public async Task Commands_ArePinnedToTheRunnersDockerContext()
    {
        var processes = new FakeProcessRunner().On("--context", 1, stderr: "Error response from daemon: No such container: offloadr-runner");
        var engine = new DockerCliContainerEngine(processes, () => Docker, () => new ContainerEnginePin("desktop-linux", ContainerEngineKind.Docker));

        await engine.InspectContainerAsync("offloadr-runner", CancellationToken.None);

        Assert.That(processes.Calls.Single().Arguments, Is.EqualTo(new[] { "--context", "desktop-linux", "container", "inspect", "offloadr-runner" }));
    }

    [Test]
    public async Task SavedPodmanConnection_IsUsedBeforeTheProbeRecognizesPodmanInstalledAsDocker()
    {
        var processes = new FakeProcessRunner().On("--connection", 1, stderr: "Error: unable to connect to Podman socket");
        var engine = new DockerCliContainerEngine(processes, () => Docker, () => new ContainerEnginePin("podman-machine-default", ContainerEngineKind.Podman));

        await engine.ProbeAsync(CancellationToken.None);

        Assert.That(processes.Calls[0].Arguments.Take(3), Is.EqualTo(new[] { "--connection", "podman-machine-default", "version" }));
    }

    [Test]
    public async Task DefaultContextPin_IgnoresADockerHostSetLater()
    {
        // Started on the `default` context; after a restart the app's environment names another daemon.
        using var environment = new EnvironmentScope(new() { ["DOCKER_HOST"] = "tcp://other:2376", ["DOCKER_TLS_VERIFY"] = "1" });
        var processes = new FakeProcessRunner().On("--context", 1, stderr: "Error response from daemon: No such container: offloadr-runner");
        var engine = new DockerCliContainerEngine(processes, () => Docker, () => new ContainerEnginePin("default", ContainerEngineKind.Docker));

        await engine.InspectContainerAsync("offloadr-runner", CancellationToken.None);

        var call = processes.Calls.Single();
        Assert.Multiple(() =>
        {
            Assert.That(call.Arguments.Take(2), Is.EqualTo(new[] { "--context", "default" }));
            Assert.That(call.RemovedEnvironment, Is.SupersetOf(new[] { "DOCKER_HOST", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH" }));
        });
    }

    [TestCase("Error: No such image: ghcr.io/offloadr/runner-comfyui:latest")]
    [TestCase("Error: ghcr.io/offloadr/runner-comfyui:latest: image not known")]
    public async Task InspectImage_ForAMissingImage_IsNull(string error)
    {
        var processes = new FakeProcessRunner().On("image", 1, stderr: error);
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        Assert.That(await engine.InspectImageAsync("ghcr.io/offloadr/runner-comfyui:latest", CancellationToken.None), Is.Null);
    }

    [Test]
    public void InspectImage_WhenTheEngineFails_ReportsAnEngineFailure()
    {
        var processes = new FakeProcessRunner().On("image", 1, stderr: "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        Assert.That(
            async () => await engine.InspectImageAsync("ghcr.io/offloadr/runner-comfyui:latest", CancellationToken.None),
            Throws.InstanceOf<ContainerEngineException>());
    }

    [Test]
    public void CurrentContext_ThatCannotBeRead_IsAnEngineFailure()
    {
        var failing = new FakeProcessRunner().On("context", 1, stderr: "failed to read the current context");
        var notStarting = new FakeProcessRunner { OnRun = (spec, _) => throw new ProcessStartException(spec.FileName, new IOException("busy")) };

        Assert.Multiple(() =>
        {
            Assert.That(
                async () => await new DockerCliContainerEngine(failing, () => Docker).CurrentContextAsync(CancellationToken.None),
                Throws.InstanceOf<ContainerEngineException>());
            Assert.That(
                async () => await new DockerCliContainerEngine(notStarting, () => Docker).CurrentContextAsync(CancellationToken.None),
                Throws.InstanceOf<ContainerEngineException>());
        });
    }

    [Test]
    public async Task CurrentContext_OfACliWithoutContexts_PinsNothing()
    {
        var processes = new FakeProcessRunner().On("context", 1, stderr: "docker: 'context' is not a docker command.");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        Assert.That(await engine.CurrentContextAsync(CancellationToken.None), Is.Null);
    }

    [TestCase("unix:///var/run/docker.sock", false)]
    [TestCase("npipe:////./pipe/dockerDesktopLinuxEngine", false)]
    [TestCase("tcp://localhost:2375", false)]
    [TestCase("ssh://core@127.0.0.1:52000/run/user/1000/podman/podman.sock", false)]
    [TestCase("tcp://10.0.0.5:2376", true)]
    [TestCase("ssh://builder@build-server.example", true)]
    [TestCase(null, false)]
    public void RemoteAddresses_AreThoseOfAnotherComputer(string? address, bool remote)
        => Assert.That(DockerCliContainerEngine.IsRemoteAddress(address), Is.EqualTo(remote));

    [Test]
    public async Task CurrentContext_OnAnotherComputer_IsMarkedRemote()
    {
        var processes = new FakeProcessRunner()
            .On("context show", 0, "build-server")
            .On("context inspect", 0, """[{"Name":"build-server","Endpoints":{"docker":{"Host":"ssh://builder@build-server.example"}}}]""");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        var pin = await engine.CurrentContextAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(pin?.Name, Is.EqualTo("build-server"));
            Assert.That(pin?.Remote, Is.True);
        });
    }

    [Test]
    public async Task CurrentContext_IsPinnedByTheEndpointBehindIt()
    {
        var tlsPath = Path.Combine(Path.GetTempPath(), "contexts", "tls", "abc");
        var processes = new FakeProcessRunner()
            .On("context show", 0, "build-server")
            .On("context inspect", 0, $$$"""
                [{"Name":"build-server",
                  "Endpoints":{"docker":{"Host":"tcp://build-server.example:2376","SkipTLSVerify":false}},
                  "TLSMaterial":{"docker":["ca.pem","cert.pem","key.pem"]},
                  "Storage":{"TLSPath":{{{System.Text.Json.JsonSerializer.Serialize(tlsPath)}}}}}]
                """);
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        var pin = await engine.CurrentContextAsync(CancellationToken.None);

        // `docker context update` can point the name at another daemon; the address and TLS files stay.
        Assert.That(pin?.Endpoint, Is.EquivalentTo(new Dictionary<string, string>
        {
            ["DOCKER_HOST"] = "tcp://build-server.example:2376",
            ["DOCKER_CERT_PATH"] = Path.Combine(tlsPath, "docker"),
            ["DOCKER_TLS_VERIFY"] = "1",
        }));
    }

    [Test]
    public async Task CurrentContext_SkippingVerificationWithoutCertificates_KeepsTls()
    {
        var processes = new FakeProcessRunner()
            .On("context show", 0, "build-server")
            .On("context inspect", 0, """[{"Name":"build-server","Endpoints":{"docker":{"Host":"tcp://build-server.example:2376","SkipTLSVerify":true}}}]""");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        var pin = await engine.CurrentContextAsync(CancellationToken.None);

        Assert.That(pin?.Endpoint, Is.EquivalentTo(new Dictionary<string, string>
        {
            ["DOCKER_HOST"] = "tcp://build-server.example:2376",
            ["DOCKER_TLS"] = "1",
        }));
    }

    [Test]
    public async Task ContextResolvedToItsEndpoint_IsReachedByTheEndpointNotTheName()
    {
        var pin = new ContainerEnginePin("build-server", ContainerEngineKind.Docker)
        {
            Endpoint = new Dictionary<string, string> { ["DOCKER_HOST"] = "tcp://build-server.example:2376" },
        };
        var processes = new FakeProcessRunner().On("container", 1, stderr: "Error response from daemon: No such container: offloadr-runner");
        var engine = new DockerCliContainerEngine(processes, () => Docker, () => pin);

        await engine.InspectContainerAsync("offloadr-runner", CancellationToken.None);

        var call = processes.Calls.Single();
        Assert.Multiple(() =>
        {
            Assert.That(call.Arguments, Does.Not.Contain("--context"));
            Assert.That(call.Environment?["DOCKER_HOST"], Is.EqualTo("tcp://build-server.example:2376"));
            Assert.That(call.RemovedEnvironment, Does.Contain("DOCKER_CONTEXT"));
        });
    }

    [Test]
    public async Task CurrentContext_IsReadFromTheCli()
    {
        var processes = new FakeProcessRunner()
            .On("context show", 0, "desktop-linux\n")
            .On("context inspect", 0, """[{"Name":"desktop-linux","Endpoints":{"docker":{"Host":"npipe:////./pipe/dockerDesktopLinuxEngine"}}}]""");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        var pin = await engine.CurrentContextAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(pin?.Name, Is.EqualTo("desktop-linux"));
            Assert.That(pin?.Endpoint, Is.EquivalentTo(new Dictionary<string, string> { ["DOCKER_HOST"] = "npipe:////./pipe/dockerDesktopLinuxEngine" }));
            Assert.That(pin?.Remote, Is.False);
        });
    }

    [TestCase(1, "")]
    [TestCase(0, "not json")]
    public void CurrentContext_WhoseEndpointCannotBeRead_IsAnEngineFailure(int exitCode, string output)
    {
        var processes = new FakeProcessRunner()
            .On("context show", 0, "build-server")
            .On("context inspect", exitCode, output, exitCode == 0 ? string.Empty : "context not found");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        Assert.That(async () => await engine.CurrentContextAsync(CancellationToken.None), Throws.InstanceOf<ContainerEngineException>());
    }

    [Test]
    public async Task ReadEnvironmentVariable_FindsTheVariableInTheContainerConfig()
    {
        var processes = new FakeProcessRunner()
            .On("container", 0, """[{"Id":"abc","Config":{"Image":"image","Env":["PATH=/usr/bin","RUNNER_SECRET=the-key=with-equals"]},"State":{"Running":true}}]""");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        Assert.Multiple(async () =>
        {
            Assert.That(await engine.ReadEnvironmentVariableAsync("offloadr-runner", "RUNNER_SECRET", CancellationToken.None), Is.EqualTo("the-key=with-equals"));
            Assert.That(await engine.ReadEnvironmentVariableAsync("offloadr-runner", "MISSING", CancellationToken.None), Is.Null);
        });
    }

    [Test]
    public void ReadEnvironmentVariable_WhenTheEngineFails_ReportsAnEngineFailure()
    {
        var processes = new FakeProcessRunner().On("container", 1, stderr: "Cannot connect to the Docker daemon.");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        Assert.That(
            async () => await engine.ReadEnvironmentVariableAsync("offloadr-runner", "RUNNER_SECRET", CancellationToken.None),
            Throws.InstanceOf<ContainerEngineException>());
    }

    [Test]
    public async Task ReadEnvironmentVariable_ForAMissingContainer_IsNull()
    {
        var processes = new FakeProcessRunner().On("container", 1, stderr: "Error response from daemon: No such container: offloadr-runner");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        Assert.That(await engine.ReadEnvironmentVariableAsync("offloadr-runner", "RUNNER_SECRET", CancellationToken.None), Is.Null);
    }

    [Test]
    public void DefaultConnection_IsTheConnectionMarkedDefault()
    {
        const string json = """
            [{"Name":"other","URI":"ssh://core@127.0.0.1:1/run","Default":false},
             {"Name":"podman-machine-default","URI":"ssh://core@127.0.0.1:2/run","Default":true}]
            """;

        Assert.Multiple(() =>
        {
            Assert.That(DockerCliJson.DefaultConnection(json), Is.EqualTo("podman-machine-default"));
            Assert.That(DockerCliJson.DefaultConnection("[]"), Is.Null);
        });
    }

    [Test]
    public async Task PodmanCommands_ArePinnedToTheRunnersConnection()
    {
        var directory = Directory.CreateTempSubdirectory("offloadr-podman-");
        try
        {
            var podman = Path.Combine(directory.FullName, "podman");
            await File.WriteAllTextAsync(podman, string.Empty);
            var processes = new FakeProcessRunner().On("--connection", 1, stderr: "Error: no such container offloadr-runner");
            var engine = new DockerCliContainerEngine(processes, () => podman, () => new ContainerEnginePin("podman-machine-default", ContainerEngineKind.Podman));

            await engine.InspectContainerAsync("offloadr-runner", CancellationToken.None);

            Assert.That(processes.Calls.Single().Arguments, Is.EqualTo(new[] { "--connection", "podman-machine-default", "container", "inspect", "offloadr-runner" }));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task CurrentContext_WithDockerHostSet_PinsThatEndpoint()
    {
        using var environment = new EnvironmentScope(new()
        {
            ["DOCKER_HOST"] = "tcp://build-host:2376",
            ["DOCKER_TLS_VERIFY"] = "1",
            ["DOCKER_CERT_PATH"] = "/certs",
            ["DOCKER_TLS"] = null,
            ["DOCKER_CONTEXT"] = null,
        });
        var processes = new FakeProcessRunner().On("context", 0, "default\n");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        var pin = await engine.CurrentContextAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(pin?.Name, Is.Null);
            Assert.That(pin?.Endpoint, Is.EquivalentTo(new Dictionary<string, string>
            {
                ["DOCKER_HOST"] = "tcp://build-host:2376",
                ["DOCKER_TLS_VERIFY"] = "1",
                ["DOCKER_CERT_PATH"] = "/certs",
            }));
            Assert.That(processes.Calls, Is.Empty);
        });
    }

    [TestCase("build-server", "tcp://build-server.example:2376")]
    [TestCase("default", "tcp://from-docker-host:2376")]
    public async Task CurrentContext_WithDockerHostAndDockerContextSet_PinsWhatTheCliUses(string effective, string expectedHost)
    {
        using var environment = new EnvironmentScope(new() { ["DOCKER_HOST"] = "tcp://from-docker-host:2376", ["DOCKER_CONTEXT"] = "build-server" });
        var processes = new FakeProcessRunner()
            .On("context show", 0, effective)
            .On("context inspect", 0, """[{"Name":"build-server","Endpoints":{"docker":{"Host":"tcp://build-server.example:2376"}}}]""");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        var pin = await engine.CurrentContextAsync(CancellationToken.None);

        Assert.That(pin?.Endpoint?["DOCKER_HOST"], Is.EqualTo(expectedHost));
    }

    [Test]
    public async Task Commands_ReachThePinnedEndpoint_WhateverTheEnvironmentSaysNow()
    {
        // Started through DOCKER_HOST; after a restart the app's environment names another daemon.
        using var environment = new EnvironmentScope(new() { ["DOCKER_HOST"] = "tcp://elsewhere:2376", ["DOCKER_CERT_PATH"] = "/other-certs" });
        var pin = new ContainerEnginePin(null, ContainerEngineKind.Docker) { Endpoint = new Dictionary<string, string> { ["DOCKER_HOST"] = "tcp://build-host:2376" } };
        var processes = new FakeProcessRunner().On("container", 1, stderr: "Error response from daemon: No such container: offloadr-runner");
        var engine = new DockerCliContainerEngine(processes, () => Docker, () => pin);

        await engine.InspectContainerAsync("offloadr-runner", CancellationToken.None);

        var call = processes.Calls.Single();
        Assert.Multiple(() =>
        {
            Assert.That(call.Arguments, Is.EqualTo(new[] { "container", "inspect", "offloadr-runner" }));
            Assert.That(call.Environment, Is.EquivalentTo(new Dictionary<string, string> { ["DOCKER_HOST"] = "tcp://build-host:2376" }));
            Assert.That(call.RemovedEnvironment, Is.SupersetOf(new[] { "DOCKER_CERT_PATH", "DOCKER_TLS_VERIFY", "DOCKER_CONTEXT" }));
            Assert.That(call.RemovedEnvironment, Does.Not.Contain("DOCKER_HOST"));
        });
    }

    [TestCase(true, "podman-machine-default")]
    [TestCase(false, null)]
    public async Task CurrentConnection_IsPinnedOnlyForARemotePodman(bool remote, string? expected)
    {
        var directory = Directory.CreateTempSubdirectory("offloadr-podman-");
        try
        {
            var podman = Path.Combine(directory.FullName, "podman");
            await File.WriteAllTextAsync(podman, string.Empty);
            var processes = new FakeProcessRunner()
                .On("version", 0, remote
                    ? """{"Client":{"Version":"5.4.0","OsArch":"linux/amd64"},"Server":{"Version":"5.4.0"}}"""
                    : """{"Client":{"Version":"5.4.0","OsArch":"linux/amd64"}}""")
                .On("system", 0, """[{"Name":"podman-machine-default","URI":"ssh://core@127.0.0.1:2/run","Default":true}]""");
            var engine = new DockerCliContainerEngine(processes, () => podman);

            Assert.That((await engine.CurrentContextAsync(CancellationToken.None))?.Name, Is.EqualTo(expected));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task CurrentConnection_WithContainerConnectionSet_PinsThatConnection()
    {
        using var environment = new EnvironmentScope(new() { ["CONTAINER_CONNECTION"] = "build-machine" });
        var directory = Directory.CreateTempSubdirectory("offloadr-podman-");
        try
        {
            var podman = Path.Combine(directory.FullName, "podman");
            await File.WriteAllTextAsync(podman, string.Empty);
            var processes = new FakeProcessRunner()
                .On("version", 0, """{"Client":{"Version":"5.4.0","OsArch":"linux/amd64"},"Server":{"Version":"5.4.0"}}""")
                .On("system", 0, """
                    [{"Name":"podman-machine-default","URI":"ssh://core@127.0.0.1:52000/run/user/1000/podman/podman.sock","Default":true},
                     {"Name":"build-machine","URI":"ssh://core@build-server.example:22/run/podman/podman.sock","Default":false}]
                    """);
            var engine = new DockerCliContainerEngine(processes, () => podman);

            var pin = await engine.CurrentContextAsync(CancellationToken.None);

            // Pinned by the address behind the name, which can be added again for another machine.
            Assert.Multiple(() =>
            {
                Assert.That(pin?.Name, Is.EqualTo("build-machine"));
                Assert.That(pin?.Endpoint, Is.EquivalentTo(new Dictionary<string, string> { ["CONTAINER_HOST"] = "ssh://core@build-server.example:22/run/podman/podman.sock" }));
                Assert.That(pin?.Remote, Is.True);
                Assert.That(processes.Calls.Select(call => call.Arguments[0]), Has.None.EqualTo("version"));
            });
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task CurrentConnection_WhoseAddressCannotBeRead_IsAnEngineFailure()
    {
        using var environment = new EnvironmentScope(new() { ["CONTAINER_CONNECTION"] = "build-machine" });
        var directory = Directory.CreateTempSubdirectory("offloadr-podman-");
        try
        {
            var podman = Path.Combine(directory.FullName, "podman");
            await File.WriteAllTextAsync(podman, string.Empty);
            var processes = new FakeProcessRunner().On("system", 125, stderr: "Error: cannot read the connection configuration");
            var engine = new DockerCliContainerEngine(processes, () => podman);

            Assert.That(async () => await engine.CurrentContextAsync(CancellationToken.None), Throws.InstanceOf<ContainerEngineException>());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Sets environment variables (null: unsets) for one test and restores them afterwards.</summary>
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

        public EnvironmentScope(Dictionary<string, string?> variables)
        {
            foreach (var (name, value) in variables)
            {
                _previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [Test]
    public async Task RunDetached_PassesTheSecretThroughTheProcessEnvironment()
    {
        var processes = new FakeProcessRunner().On("run", 0, "container-id\n");
        var engine = new DockerCliContainerEngine(processes, () => Docker);
        var spec = new ContainerSpec(
            "offloadr-runner",
            "image",
            new Dictionary<string, string> { ["RUNNER_SECRET"] = "secret-value" },
            [],
            new Dictionary<string, string>(),
            Gpus: true);

        var id = await engine.RunDetachedAsync(spec, CancellationToken.None);

        var call = processes.Calls.Single();
        Assert.Multiple(() =>
        {
            Assert.That(id, Is.EqualTo("container-id"));
            Assert.That(call.Arguments, Has.None.Contains("secret-value"));
            Assert.That(call.Environment!["RUNNER_SECRET"], Is.EqualTo("secret-value"));
        });
    }

    [Test]
    public async Task InspectContainer_ReturnsNullWhenItDoesNotExist()
    {
        var processes = new FakeProcessRunner().On("container", 1, stderr: "Error: No such container: offloadr-runner");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        Assert.That(await engine.InspectContainerAsync("offloadr-runner", CancellationToken.None), Is.Null);
    }

    [Test]
    public void Pull_FailureCarriesTheLastLines()
    {
        var processes = new FakeProcessRunner().On("pull", 1, stderr: "Error response from daemon: denied");
        var engine = new DockerCliContainerEngine(processes, () => Docker);

        var error = Assert.ThrowsAsync<ContainerEngineException>(() => engine.PullAsync("image", _ => { }, CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("denied"));
    }
}

public class SystemProcessRunnerTests
{
    [Test]
    public void Run_ReportsAMissingExecutable()
    {
        var runner = new SystemProcessRunner();

        Assert.ThrowsAsync<ProcessStartException>(() => runner.RunAsync(
            new ProcessSpec(Path.Combine(Path.GetTempPath(), "definitely-missing-executable"), []),
            CancellationToken.None));
    }

    [Test]
    public async Task Run_CapturesOutputAndPassesEnvironment()
    {
        var runner = new SystemProcessRunner();
        var spec = OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd.exe", ["/c", "echo %RUNNER_TEST_VALUE%"], new Dictionary<string, string> { ["RUNNER_TEST_VALUE"] = "hello" })
            : new ProcessSpec("/bin/sh", ["-c", "echo $RUNNER_TEST_VALUE"], new Dictionary<string, string> { ["RUNNER_TEST_VALUE"] = "hello" });

        var result = await runner.RunAsync(spec, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.Zero);
            Assert.That(result.StandardOutput.Trim(), Is.EqualTo("hello"));
        });
    }

    [Test]
    public async Task Run_LeavesOutRemovedVariables()
    {
        var previous = Environment.GetEnvironmentVariable("RUNNER_TEST_REMOVED");
        Environment.SetEnvironmentVariable("RUNNER_TEST_REMOVED", "inherited");
        try
        {
            var runner = new SystemProcessRunner();
            var spec = OperatingSystem.IsWindows()
                ? new ProcessSpec("cmd.exe", ["/c", "echo %RUNNER_TEST_REMOVED%"], RemovedEnvironment: ["RUNNER_TEST_REMOVED"])
                : new ProcessSpec("/bin/sh", ["-c", "echo $RUNNER_TEST_REMOVED"], RemovedEnvironment: ["RUNNER_TEST_REMOVED"]);

            var result = await runner.RunAsync(spec, CancellationToken.None);

            Assert.That(result.StandardOutput, Does.Not.Contain("inherited"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("RUNNER_TEST_REMOVED", previous);
        }
    }
}
