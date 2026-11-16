using Offloadr.Runner.Supervisor.Hardware;
using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor;

/// <summary>
/// One line of the checklist on the Setup and Status pages: what was checked, whether it passed (null
/// until it has run), what that means, and the one thing that most likely fixes a failure.
/// </summary>
/// <param name="Technical">The tool's own words, for those who want them; shown folded away.</param>
public sealed record EnvironmentCheck(string Title, bool? Ok, string Detail, FaultAction Fix = FaultAction.None, string? Technical = null);

/// <summary>The checks of this computer that Setup walks through and Status shows, read from the supervisor's view.</summary>
public static class EnvironmentChecks
{
    public static EnvironmentCheck Docker(SupervisorView view)
    {
        if (view.Engine is not { } engine)
        {
            return new EnvironmentCheck("Docker", null, "Checking…");
        }

        if (engine.Available)
        {
            var where = engine.OperatingSystem ?? engine.Kind.ToString();
            return new EnvironmentCheck("Docker", true, engine.ServerVersion is { } version ? $"{where}, engine {version}." : $"{where}.");
        }

        var error = engine.Error ?? "No container engine answered.";
        var fault = SupervisorFault.For(engine.ErrorKind, error, engine.Kind);
        return fault.Detail is null
            ? new EnvironmentCheck("Docker", false, error, fault.Action)
            : new EnvironmentCheck("Docker", false, $"{fault.Title}. {fault.Message}", fault.Action, fault.Detail);
    }

    /// <param name="chosenGpu">The UUID of the GPU chosen in Settings (<see cref="RunnerSettings.SelectedGpuUuid"/>); null for every GPU.</param>
    public static EnvironmentCheck HostGpu(SupervisorView view, string? chosenGpu = null) => view.HostGpus switch
    {
        null => new EnvironmentCheck("NVIDIA GPU", null, "Checking…"),
        { Gpus.Count: 0 } report => new EnvironmentCheck(
            "NVIDIA GPU",
            false,
            "No NVIDIA GPU was found. The runner needs one, with a current NVIDIA driver.",
            FaultAction.InstallGpuDriver,
            report.Error),
        var report when chosenGpu is not null && report.Gpus.FirstOrDefault(gpu => Same(gpu.Uuid, chosenGpu)) is { } chosen => new EnvironmentCheck(
            "NVIDIA GPU",
            true,
            $"{Describe(chosen)}, the GPU chosen in Settings."),
        // Only a driver that names its GPUs can show the chosen one is gone; the runner never falls back to another.
        var report when chosenGpu is not null && report.Gpus.Any(gpu => gpu.Uuid is not null) => new EnvironmentCheck(
            "NVIDIA GPU",
            false,
            "The GPU chosen in Settings is not on this computer now, so the runner has no GPU to use. Choose another one under Advanced in Settings.",
            FaultAction.OpenSettings,
            chosenGpu),
        var report => new EnvironmentCheck(
            "NVIDIA GPU",
            true,
            string.Join(", ", report.Gpus.Select(Describe))),
    };

    /// <summary>A GPU as the app names it: its name and memory, such as <c>NVIDIA GeForce RTX 4090 (24 GB)</c>.</summary>
    public static string Describe(HostGpu gpu) => $"{gpu.Name} ({gpu.MemoryMiB / 1024.0:0.#} GB)";

    private static bool Same(string? uuid, string chosen) => string.Equals(uuid, chosen, StringComparison.OrdinalIgnoreCase);

    public static EnvironmentCheck GpuInContainers(SupervisorView view) => view.GpuAccess switch
    {
        null => new EnvironmentCheck("GPU inside containers", null, "Checked with the runner image once it is downloaded."),
        { Succeeded: true } check => new EnvironmentCheck(
            "GPU inside containers",
            true,
            GpuCount(check.Output) is var count and > 1 ? $"{count} GPUs visible to the runner." : "The runner can use the GPU.",
            Technical: check.Output),
        var check => new EnvironmentCheck(
            "GPU inside containers",
            false,
            "Docker could not give a container the GPU. Update the NVIDIA driver and make sure Docker Desktop uses the WSL 2 engine.",
            FaultAction.SetUpGpu,
            check.Output),
    };

    /// <summary>Whether Docker and the GPU both passed: false once either failed, null while either has not been checked.</summary>
    public static bool? ComputerReady(SupervisorView view)
    {
        var docker = Docker(view).Ok;
        var gpu = HostGpu(view).Ok;
        return docker == false || gpu == false ? false : docker == true && gpu == true ? true : null;
    }

    /// <summary>
    /// Whether the app opens on Setup rather than Status: until a runner key is saved, since nothing works
    /// without one. A copy that already has a key never opens there, even after an update.
    /// </summary>
    public static bool NeedsSetup(RunnerSettings settings) => !settings.HasRunnerKey;

    public static EnvironmentCheck RunnerKey(RunnerSettings settings) => settings.RunnerKeyProblem switch
    {
        null => new EnvironmentCheck("Runner key", true, "Saved on this computer, encrypted."),
        _ when !settings.HasRunnerKey => new EnvironmentCheck(
            "Runner key", false, "Not added yet. Create a personal runner key under Settings in the Offloadr web app.", FaultAction.OpenSettings),
        var problem => new EnvironmentCheck("Runner key", false, problem, FaultAction.OpenSettings),
    };

    // nvidia-smi -L prints one line per GPU.
    private static int GpuCount(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
}

/// <summary>Opens Setup once per app run when it is needed, so skipping it is not undone by every visit to Status.</summary>
public sealed class SetupPrompt
{
    private int _offered;

    /// <summary>True the first time it is asked while <paramref name="settings"/> need setup; false ever after.</summary>
    public bool TryOffer(RunnerSettings settings)
        => EnvironmentChecks.NeedsSetup(settings) && Interlocked.Exchange(ref _offered, 1) == 0;
}

/// <summary>What the UI offers for a <see cref="FaultAction"/>: a short label and, for help outside the app, where it leads.</summary>
public sealed record FaultHelp(string Label, string? Url)
{
    public const string InstallDockerDesktop = "https://docs.docker.com/desktop/setup/install/windows-install/";
    public const string DockerGpuSupport = "https://docs.docker.com/desktop/features/gpu/";
    public const string NvidiaDrivers = "https://www.nvidia.com/en-us/drivers/";

    /// <summary>The help for <paramref name="action"/>; null when there is nothing to offer beyond the advice itself.</summary>
    public static FaultHelp? For(FaultAction action) => action switch
    {
        FaultAction.InstallEngine => new FaultHelp("Install Docker Desktop", InstallDockerDesktop),
        FaultAction.SetUpGpu => new FaultHelp("GPU setup guide", DockerGpuSupport),
        FaultAction.InstallGpuDriver => new FaultHelp("Get the NVIDIA driver", NvidiaDrivers),
        FaultAction.StartEngine => new FaultHelp("Check again", null),
        FaultAction.OpenSettings => new FaultHelp("Open Settings", null),
        FaultAction.CheckConnection => new FaultHelp("Try again", null),
        _ => null,
    };
}
