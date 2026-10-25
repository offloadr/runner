namespace Offloadr.Runner.Desktop.WinUI;

/// <summary>WinUI entry point. Startup failures are written to the log folder, since there is no window to show them in yet.</summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        UnhandledException += (_, e) => WriteStartupError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteStartupError(e.ExceptionObject as Exception);
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp()
    {
        try
        {
            return MauiProgram.CreateMauiApp();
        }
        catch (Exception e)
        {
            WriteStartupError(e);
            throw;
        }
    }

    private static void WriteStartupError(Exception? error)
    {
        try
        {
            Offloadr.Runner.Supervisor.Logging.DesktopStartupDiagnostics.WriteError(MauiProgram.Paths().Logs, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            // There is no content-bearing fallback if the configured folder is unavailable.
        }
    }
}
