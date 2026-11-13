using Velopack;

namespace Offloadr.Runner.Desktop.WinUI;

/// <summary>
/// The app's entry point, in place of the one WinUI generates (DISABLE_XAML_GENERATED_MAIN): the
/// installer runs the app with its own arguments when it installs, updates or removes it, and those
/// runs must end before anything else starts, the single-instance lock included.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main()
    {
        // A downloaded update installs when the app restarts to update or quits, never at start: a
        // second launch only brings up the open copy, and must not replace that copy's files.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => InstallerHooks.BeforeUninstall())
            .Run();

        // What WinUI's generated entry point does.
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
