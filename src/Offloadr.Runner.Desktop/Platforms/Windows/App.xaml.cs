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
            var logs = MauiProgram.Paths().Logs;
            Directory.CreateDirectory(logs);
            File.AppendAllText(Path.Combine(logs, "desktop-startup.log"), $"{DateTimeOffset.UtcNow:O} {error}\n");
        }
        catch (IOException)
        {
            // Nothing else can report this.
        }
    }
}
