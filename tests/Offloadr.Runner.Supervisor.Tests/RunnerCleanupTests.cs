using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hosting;

namespace Offloadr.Runner.Supervisor.Tests;

public class RunnerCleanupTests
{
    private const string OwnerId = "0123456789abcdef0123456789abcdef";

    private string _directory = string.Empty;
    private SupervisorPaths _paths = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "offloadr-cleanup-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);
        _paths = new SupervisorPaths(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public async Task WithoutARememberedEngine_NothingRanSoNothingIsCalled()
    {
        File.WriteAllText(_paths.Owner, OwnerId);
        var processes = new FakeProcessRunner();

        var removed = await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.False);
            Assert.That(processes.Calls, Is.Empty);
        });
    }

    [Test]
    public async Task TheRememberedRunnerOfThisDataFolder_IsRemovedAndForgotten()
    {
        File.WriteAllText(_paths.Owner, OwnerId);
        Remember("abc");
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId));

        var removed = await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None);

        var commands = processes.Calls.Select(call => string.Join(' ', call.Arguments)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.True);
            Assert.That(commands, Does.Contain("stop --time 10 abc"), "The agent gets to shut down cleanly first.");
            Assert.That(commands.IndexOf("stop --time 10 abc"), Is.LessThan(commands.IndexOf("rm --force abc")));
            Assert.That(File.Exists(_paths.EngineState), Is.False);
        });
    }

    [Test]
    public async Task ARunnerThatWillNotStop_IsStillRemoved()
    {
        File.WriteAllText(_paths.Owner, OwnerId);
        Remember("abc");
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId))
            .On("stop", 1, stderr: "Error response from daemon: cannot stop container: abc");

        var removed = await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.True);
            Assert.That(processes.Calls.Select(call => string.Join(' ', call.Arguments)), Does.Contain("rm --force abc"));
        });
    }

    [Test]
    public async Task ARunnerAnotherCopyStarted_IsLeftAlone()
    {
        File.WriteAllText(_paths.Owner, OwnerId);
        Remember("abc");
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect("fedcba9876543210fedcba9876543210"));

        var removed = await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.False);
            Assert.That(processes.Calls.Select(call => call.Arguments[0]), Does.Not.Contain("rm"));
        });
    }

    [Test]
    public async Task ARunnerAnotherRunningCopyManages_IsLeftAlone()
    {
        File.WriteAllText(_paths.Owner, OwnerId);
        Remember("abc");
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId));
        using var running = new SupervisorInstanceLock(_paths.SupervisorLock);
        Assert.That(running.TryAcquire(), Is.True);

        var removed = await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.False);
            Assert.That(processes.Calls, Is.Empty);
            Assert.That(File.Exists(_paths.EngineState), Is.True, "The running copy still needs its engine hint.");
        });
    }

    [Test]
    public async Task WithoutAnOwnerFile_NothingIsRemoved()
    {
        Remember("abc");
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId));

        var removed = await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.False);
            Assert.That(File.Exists(_paths.Owner), Is.False, "Cleanup must not create an owner ID.");
        });
    }

    private void Remember(string containerId)
    {
        // An executable that exists everywhere, so cleanup finds the remembered engine on any machine;
        // the fake process runner answers its commands.
        var docker = Environment.ProcessPath!;
        var selection = new ContainerEngineSelection(_ => new LocatedEngine(docker, ContainerEngineKind.Docker), _paths.EngineState);
        selection.Select(null);
        Assert.That(selection.Remember(containerId, out var error), Is.True, error);
    }

    private static string Inspect(string owner)
        => $$$"""[{"Id":"abc","Image":"sha256:feed","Config":{"Image":"ghcr.io/offloadr/runner-comfyui:latest","Labels":{"app.offloadr.runner.managed":"true","app.offloadr.runner.owner":"{{{owner}}}"}},"State":{"Running":true}}]""";
}
