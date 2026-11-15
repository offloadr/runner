using Offloadr.Runner.Supervisor.Hosting;
using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor.Tests;

public class AppUpdateStatusTests
{
    private static readonly Version Current = new(1, 4, 0);
    private static readonly Version Newer = new(1, 5, 0);

    [TestCase(AppUpdateState.NotInstalled, "This copy was not installed with the installer, so it does not update itself.", false)]
    [TestCase(AppUpdateState.Current, "This is the latest version.", true)]
    [TestCase(AppUpdateState.Checking, "Checking for updates…", false)]
    [TestCase(AppUpdateState.Downloading, "Downloading version 1.5.0… 40%", false)]
    [TestCase(AppUpdateState.Ready, "Version 1.5.0 is ready. It installs when you restart the app from the banner, or when you quit it.", false)]
    [TestCase(AppUpdateState.Failed, "Could not check for updates. The app tries again later.", true)]
    public void Summary_SaysWhereUpdatesStand_AndWhetherACheckMakesSense(AppUpdateState state, string summary, bool canCheck)
    {
        var status = new AppUpdateStatus(state, Current, state is AppUpdateState.Downloading or AppUpdateState.Ready ? Newer : null, 40);

        Assert.Multiple(() =>
        {
            Assert.That(status.Summary, Is.EqualTo(summary));
            Assert.That(status.CanCheck, Is.EqualTo(canCheck));
        });
    }

    [Test]
    public void Summary_OfAFailedCheck_GivesItsProblem()
        => Assert.That(new AppUpdateStatus(AppUpdateState.Failed, Current, Problem: "Offline.").Summary, Is.EqualTo("Offline."));
}

public class SettingsFormTests
{
    private static readonly SettingsForm Saved = new(
        "key", "comfyui-latest", @"D:\models", RunnerSettings.DefaultOffloadrApiOrigin, null, AutoStart: true, DownloadModels: false);

    [Test]
    public void SameAs_IgnoresBlankAndSurroundingSpaces()
    {
        var edited = Saved with
        {
            RunnerKey = " key ",
            LocalModelsDirectory = @"D:\models ",
            OffloadrApiOrigin = "",
            ContainerEngineExecutable = "  ",
        };

        Assert.That(edited.SameAs(Saved), Is.True);
    }

    [Test]
    public void SameAs_SeesEveryRealChange()
    {
        var changes = new[]
        {
            Saved with { RunnerKey = "other" },
            Saved with { SelectedEditorTemplate = "forge-neo:neo" },
            Saved with { LocalModelsDirectory = null },
            Saved with { OffloadrApiOrigin = "https://staging.example" },
            Saved with { ContainerEngineExecutable = "podman" },
            Saved with { AutoStart = false },
            Saved with { DownloadModels = true },
            Saved with { KeepAwakeDuringSessions = false },
        };

        Assert.That(changes.Select(change => change.SameAs(Saved)), Has.All.False);
    }
}
