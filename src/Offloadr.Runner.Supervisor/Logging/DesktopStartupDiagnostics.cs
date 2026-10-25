using Microsoft.Extensions.Logging;

namespace Offloadr.Runner.Supervisor.Logging;

public static class DesktopStartupDiagnostics
{
    /// <summary>Records a fixed failure class even before the desktop host exists.</summary>
    public static void WriteError(string logs, Exception? error)
    {
        using var provider = new DesktopDiagnosticLogProvider(logs);
        provider.CreateLogger("Offloadr.Runner.Desktop.Startup").LogError(error, "Desktop startup failed.");
    }
}
