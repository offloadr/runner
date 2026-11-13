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
        VelopackApp.Build()
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
