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

    [Test]
    public void AWantedRunner_IsResumedOnceByTheNextVersion()
    {
        UpdateHandoff.Save(_paths, runnerWanted: true);

        Assert.Multiple(() =>
        {
            Assert.That(UpdateHandoff.TakeResume(_paths), Is.True);
            Assert.That(UpdateHandoff.TakeResume(_paths), Is.False, "Only the first start after the update resumes it.");
        });
    }

    [Test]
    public void ARunnerThatWasNotWanted_IsNotStarted()
    {
        UpdateHandoff.Save(_paths, runnerWanted: true);
        UpdateHandoff.Save(_paths, runnerWanted: false);

        Assert.That(UpdateHandoff.TakeResume(_paths), Is.False);
    }
}
