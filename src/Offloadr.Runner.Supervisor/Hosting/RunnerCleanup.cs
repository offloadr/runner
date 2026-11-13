using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Processes;

namespace Offloadr.Runner.Supervisor.Hosting;

/// <summary>
/// Removes a runner container the app left running, for an uninstaller: the app is closed (or was
/// killed) by then, and the container keeps the runner key in its environment. Only the container
/// this data folder's copy of the app started is removed, on the engine it was started on.
/// </summary>
public static class RunnerCleanup
{
    /// <returns>True when a container was removed.</returns>
    public static async Task<bool> RemoveLeftoverRunnerAsync(
        SupervisorPaths paths,
        IProcessRunner processes,
        SupervisorOptions options,
        CancellationToken cancellationToken)
    {
        // The engine hint exists only while a runner container may still run.
        var selection = new ContainerEngineSelection(statePath: paths.EngineState);
        if (!selection.TryRestore() || SupervisorOwner.TryLoad(paths.Owner) is not { } owner)
        {
            return false;
        }

        IContainerEngine engine = new DockerCliContainerEngine(processes, () => selection.Executable, () => selection.Pin);
        var container = (selection.RememberedContainerId is { } id
                            ? await engine.InspectContainerAsync(id, cancellationToken).ConfigureAwait(false)
                            : null)
                        ?? await engine.InspectContainerAsync(options.ContainerName, cancellationToken).ConfigureAwait(false);
        if (container is null || !RunnerSupervisor.IsOwnedBy(container, owner.Id))
        {
            return false;
        }

        await engine.RemoveAsync(string.IsNullOrWhiteSpace(container.Id) ? options.ContainerName : container.Id, cancellationToken).ConfigureAwait(false);
        selection.Forget();
        return true;
    }
}
