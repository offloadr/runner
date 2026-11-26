namespace Offloadr.Runner.Supervisor;

/// <summary>Where the desktop runner keeps its files.</summary>
public sealed record SupervisorPaths
{
    public SupervisorPaths(string data)
    {
        Data = Path.GetFullPath(data);
    }

    public string Data { get; }

    public string SettingsFile => Path.Combine(Data, "settings.json");

    public string Logs => Path.Combine(Data, "logs");

    public string Keys => Path.Combine(Data, "keys");

    /// <summary>The desktop window's browser profile (WebView2).</summary>
    public string WebView => Path.Combine(Data, "webview");

    /// <summary>Held while a supervisor manages the runner container.</summary>
    public string SupervisorLock => Path.Combine(Data, "supervisor.lock");

    /// <summary>The ID that marks the runner containers this data folder's supervisor starts.</summary>
    public string Owner => Path.Combine(Data, "owner");

    /// <summary>The engine the running container was started on, kept across a crash.</summary>
    public string EngineState => Path.Combine(Data, "engine.json");

    /// <summary>The launch whose container Offloadr released and the app is stopping; such a container is never reused.</summary>
    public string ReleasedLaunch => Path.Combine(Data, "released-launch");

    /// <summary>Stopped runner containers the engine would not remove yet, with their engines; removed later.</summary>
    public string LeftoverContainers => Path.Combine(Data, "leftover-containers");

    /// <summary>The containers the app started that Offloadr may still count on, until it learns each is gone.</summary>
    public string LaunchedContainers => Path.Combine(Data, "launched-containers");

    /// <summary>
    /// The Offloadr address and runner key each of those containers was started with, encrypted like the
    /// key in the settings: only that Offloadr can be told it is gone, also after Settings name another.
    /// </summary>
    public string LaunchedCredentials => Path.Combine(Data, "launched-credentials");

    /// <summary>Which Offloadr address and runner key another app took the runner over for; only Start takes it back.</summary>
    public string TakenOver => Path.Combine(Data, "taken-over");

    /// <summary>Present while the app restarts to update with the runner wanted (see <see cref="Hosting.UpdateHandoff"/>).</summary>
    public string ResumeAfterUpdate => Path.Combine(Data, "resume-after-update");

    /// <summary>%LOCALAPPDATA%\OffloadrRunner on Windows, ~/.local/share/OffloadrRunner on Linux.</summary>
    public static SupervisorPaths UserDefault()
        => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OffloadrRunner"));
}
