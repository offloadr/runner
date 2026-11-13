using Offloadr.Runner.Supervisor.Hosting;
using Offloadr.Runner.Supervisor.Processes;

namespace Offloadr.Runner.Desktop.WinUI;

/// <summary>
/// Runs while the installer removes the app, before its files go. The installer allows 30 seconds
/// and shows nothing, so each step is best effort and reports only to the log folder. Settings, the
/// runner key and logs stay in the data folder, which is not the install folder.
/// </summary>
internal static class InstallerHooks
{
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(20);

    public static void BeforeUninstall()
    {
        if (MauiProgram.UsesOtherDataDirectory)
        {
            return;
        }

        Try("remove the sign-in entry", () => new WindowsStartupRegistration(MauiProgram.LaunchPath).RemoveIfRegistered());

        // The app is closed by now (or was killed), which can leave its runner running, and the
        // container keeps the runner key in its environment.
        Try("remove the runner container", () =>
        {
            using var timeout = new CancellationTokenSource(CleanupBudget);
            RunnerCleanup.RemoveLeftoverRunnerAsync(MauiProgram.Paths(), new SystemProcessRunner(), new SupervisorOptions(), timeout.Token)
                .GetAwaiter().GetResult();
        });
    }

    private static void Try(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            App.WriteStartupError(new InvalidOperationException($"Uninstall could not {step}.", ex));
        }
    }
}
