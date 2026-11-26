using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Offloadr.Runner.Supervisor;
using Offloadr.Runner.Supervisor.Hosting;
using Offloadr.Runner.Supervisor.Launcher;
using Offloadr.Runner.Supervisor.Processes;
using Offloadr.Runner.Supervisor.Settings;

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
            var paths = MauiProgram.Paths();
            var services = new ServiceCollection();
            MauiProgram.AddRunnerKeyProtection(services, paths);
            using var provider = services.BuildServiceProvider();
            RunnerCleanup.RemoveLeftoverRunnerAsync(paths, new SystemProcessRunner(), new SupervisorOptions(), timeout.Token,
                    (stopped, token) => AskOffloadrAsync(paths, stopped, exited: true, token),
                    (stopped, token) => AskOffloadrAsync(paths, stopped, exited: false, token),
                    provider.GetRequiredService<ISecretProtector>(),
                    within: CleanupBudget)
                .GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// Asks Offloadr about a container the app started for it, with the address and key in the data
    /// folder, registering no launcher: to release it before it is stopped (true when granted), or, with
    /// <paramref name="exited"/>, that it is gone (true once Offloadr took the report). The app is being
    /// removed, so nothing else may ever tell it.
    /// </summary>
    private static async Task<bool> AskOffloadrAsync(SupervisorPaths paths, RunnerCleanup.StoppedLaunch stopped, bool exited, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(paths);
        MauiProgram.AddRunnerKeyProtection(services, paths);
        services.AddSingleton<IRunnerSettingsStore, FileRunnerSettingsStore>();
        await using var provider = services.BuildServiceProvider();
        // (As the runner its container was started for, if Settings name another by now.)
        var settings = RunnerCleanup.ReportAs(stopped, provider.GetRequiredService<IRunnerSettingsStore>().Current);
        using var link = new ControlPlaneLauncherLink(provider.GetRequiredService<ILogger<ControlPlaneLauncherLink>>());
        var result = await link.ReleaseAsync(stopped.LaunchId, executorInstanceId: null, stopped.Target, cancellationToken, containerExited: exited, reportAs: settings)
            .ConfigureAwait(false);
        // (A report Offloadr refused was not taken: it stays noted, for a later start of the app.)
        return result.Released;
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
