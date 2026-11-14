namespace Offloadr.Runner.Supervisor.Hosting;

/// <summary>What Windows tells the app's window about sleep and the end of the Windows session.</summary>
public enum SystemEvent
{
    None,

    /// <summary>The computer is about to sleep or hibernate.</summary>
    Suspending,

    /// <summary>The computer woke from sleep or hibernation.</summary>
    Resumed,

    /// <summary>Windows asks whether it may shut down, restart or sign out.</summary>
    SessionEndQuery,

    /// <summary>Windows is shutting down, restarting or signing out now: the app ends within seconds.</summary>
    SessionEnding,

    /// <summary>A shutdown asked about earlier did not happen (another app refused it, say).</summary>
    SessionEndCancelled,
}

/// <summary>Reads the window messages Windows sends for sleep and the end of the Windows session.</summary>
public static class WindowsSystemEvents
{
    public const uint QueryEndSession = 0x0011;
    public const uint EndSession = 0x0016;
    public const uint PowerBroadcast = 0x0218;

    private const long PowerSuspend = 0x0004;

    // Sent on every wake; PBT_APMRESUMESUSPEND follows only when someone is at the computer, so it is
    // not acted on twice.
    private const long PowerResumeAutomatic = 0x0012;

    public static SystemEvent Decode(uint message, nint wParam) => message switch
    {
        PowerBroadcast when wParam == PowerSuspend => SystemEvent.Suspending,
        PowerBroadcast when wParam == PowerResumeAutomatic => SystemEvent.Resumed,
        QueryEndSession => SystemEvent.SessionEndQuery,
        EndSession => wParam != 0 ? SystemEvent.SessionEnding : SystemEvent.SessionEndCancelled,
        _ => SystemEvent.None,
    };
}
