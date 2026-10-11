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

    /// <summary>Held while a supervisor manages the runner container.</summary>
    public string SupervisorLock => Path.Combine(Data, "supervisor.lock");

    /// <summary>The ID that marks the runner containers this data folder's supervisor starts.</summary>
    public string Owner => Path.Combine(Data, "owner");

    /// <summary>The engine the running container was started on, kept across a crash.</summary>
    public string EngineState => Path.Combine(Data, "engine.json");

    /// <summary>%LOCALAPPDATA%\OffloadrRunner on Windows, ~/.local/share/OffloadrRunner on Linux.</summary>
    public static SupervisorPaths UserDefault()
        => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OffloadrRunner"));
}
