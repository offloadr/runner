namespace Offloadr.Runner.Supervisor;

/// <summary>What went wrong, as far as the person needs to know to fix it.</summary>
public enum FaultKind
{
    /// <summary>Nothing more specific is known; the message says what happened.</summary>
    Unknown,

    /// <summary>The Docker (or Podman) command line is not installed, or does not start.</summary>
    EngineMissing,

    /// <summary>The command line is installed but its engine is not running.</summary>
    EngineNotRunning,

    /// <summary>The engine did not answer in time: still starting, or stuck.</summary>
    EngineNotResponding,

    /// <summary>This account may not use the engine.</summary>
    EnginePermission,

    /// <summary>Docker Desktop runs Windows containers; the runner is a Linux container.</summary>
    WindowsContainers,

    /// <summary>The engine the runner was last started on does not answer; the runner may still run there.</summary>
    EngineUnreachable,

    /// <summary>The engine could not give the container the NVIDIA GPU.</summary>
    GpuUnavailable,

    /// <summary>The engine ran out of disk space.</summary>
    DiskFull,

    /// <summary>The image registry could not be reached, or turned the download away for now.</summary>
    RegistryUnreachable,

    /// <summary>The registry has no such image, or refuses access to it.</summary>
    ImageUnavailable,

    /// <summary>Offloadr names no runner image for the chosen editor.</summary>
    NoImageForEditor,

    /// <summary>A setting is missing or invalid.</summary>
    Settings,

    /// <summary>Offloadr did not accept the runner key.</summary>
    KeyRejected,

    /// <summary>A container that is not this app's runner has the runner's name.</summary>
    NameTaken,

    /// <summary>Another copy of the app manages the runner.</summary>
    AnotherCopy,
}

/// <summary>The one thing that most likely fixes a fault, for the UI to offer as a button or link.</summary>
public enum FaultAction
{
    None,
    InstallDocker,
    StartDocker,
    OpenSettings,
    SetUpGpu,
    FreeDiskSpace,
    CheckConnection,
}

/// <summary>
/// Why the runner needs attention, or why an automatic restart is waiting: a short title, what to do
/// about it, and the engine's own words for those who want them. Shown next to the view's message,
/// which keeps saying what happened.
/// </summary>
public sealed record SupervisorFault(FaultKind Kind, string Title, string Message, string? Detail)
{
    public FaultAction Action => Kind switch
    {
        FaultKind.EngineMissing => FaultAction.InstallDocker,
        FaultKind.EngineNotRunning or FaultKind.EngineNotResponding or FaultKind.WindowsContainers or FaultKind.EngineUnreachable => FaultAction.StartDocker,
        FaultKind.Settings or FaultKind.KeyRejected => FaultAction.OpenSettings,
        FaultKind.GpuUnavailable => FaultAction.SetUpGpu,
        FaultKind.DiskFull => FaultAction.FreeDiskSpace,
        FaultKind.RegistryUnreachable => FaultAction.CheckConnection,
        _ => FaultAction.None,
    };

    /// <summary>
    /// A fault of <paramref name="kind"/>. Kinds with advice of their own explain themselves, and the
    /// supervisor's <paramref name="message"/> becomes the detail; the others show that message, which
    /// already says what to do.
    /// </summary>
    public static SupervisorFault For(FaultKind kind, string message)
    {
        var (title, advice) = Describe(kind);
        return advice is null
            ? new SupervisorFault(kind, title, message, null)
            : new SupervisorFault(kind, title, advice, message);
    }

    private static (string Title, string? Advice) Describe(FaultKind kind) => kind switch
    {
        FaultKind.EngineMissing => ("Docker was not found",
            "Install Docker Desktop and use its WSL 2 engine (Windows), or Docker Engine with the NVIDIA Container Toolkit (Linux). Then press Start."),
        FaultKind.EngineNotRunning => ("Docker is not running",
            "Start Docker Desktop and wait until its engine is running, then press Start."),
        FaultKind.EngineNotResponding => ("Docker is not answering",
            "Docker may still be starting. If this keeps happening, restart Docker Desktop, then press Start."),
        FaultKind.EnginePermission => ("No permission to use Docker",
            "This account may not use Docker. On Windows, add it to the docker-users group and sign in again; on Linux, add it to the docker group. Then press Start."),
        FaultKind.WindowsContainers => ("Docker runs Windows containers",
            "The runner is a Linux container. Choose Switch to Linux containers in the Docker Desktop menu, then press Start."),
        FaultKind.GpuUnavailable => ("Docker cannot use the GPU",
            "Update the NVIDIA driver and make sure Docker Desktop uses the WSL 2 engine (on Linux, install the NVIDIA Container Toolkit). Restart Docker, then press Start."),
        FaultKind.DiskFull => ("Docker is out of disk space",
            "Free space on the drive Docker keeps its data on, or raise the disk limit in Docker Desktop's settings, then press Start."),
        FaultKind.RegistryUnreachable => ("Could not download the runner",
            "The image registry could not be reached. Check the internet connection, proxy or firewall, then press Start."),
        FaultKind.ImageUnavailable => ("The runner image is not available",
            "The registry does not offer this image right now. Try again later; if it keeps failing, contact Offloadr support."),
        FaultKind.EngineUnreachable => ("The runner's Docker does not answer", null),
        FaultKind.NoImageForEditor => ("No runner for this editor", null),
        FaultKind.Settings => ("Check your settings", null),
        FaultKind.KeyRejected => ("Offloadr did not accept the runner key", null),
        FaultKind.NameTaken => ("The runner's container name is taken", null),
        FaultKind.AnotherCopy => ("Another copy of the app is open", null),
        _ => ("The runner ran into a problem", null),
    };
}
