using Offloadr.Runner.Supervisor.Hosting;

namespace Offloadr.Runner.Supervisor.Tests;

public class UpdateHandoffTests
{
    private string _directory = string.Empty;
    private SupervisorPaths _paths = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "offloadr-handoff-" + Guid.NewGuid().ToString("n"));
        _paths = new SupervisorPaths(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestCase(SupervisorState.Running)]
    [TestCase(SupervisorState.RestartPending)]
    [TestCase(SupervisorState.WaitingToRestart)]
    [TestCase(SupervisorState.Starting)]
    public void AWantedRunner_IsResumedOnceByTheNextVersion(SupervisorState state)
    {
        UpdateHandoff.Save(_paths, View(state));

        Assert.Multiple(() =>
        {
            Assert.That(UpdateHandoff.TakeResume(_paths), Is.True);
            Assert.That(UpdateHandoff.TakeResume(_paths), Is.False, "Only the first start after the update resumes it.");
        });
    }

    [TestCase(SupervisorState.Stopped)]
    [TestCase(SupervisorState.Faulted)]
    public void ARunnerThatWasNotWanted_IsNotStarted(SupervisorState state)
    {
        UpdateHandoff.Save(_paths, View(SupervisorState.Running));
        UpdateHandoff.Save(_paths, View(state));

        Assert.That(UpdateHandoff.TakeResume(_paths), Is.False);
    }

    private static SupervisorView View(SupervisorState state) => SupervisorView.Initial(DateTimeOffset.UtcNow) with { State = state };
}
