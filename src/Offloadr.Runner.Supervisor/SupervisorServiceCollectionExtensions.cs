using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hardware;
using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Processes;
using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor;

public static class SupervisorServiceCollectionExtensions
{
    /// <summary>
    /// Registers the runner supervisor as a hosted service. The host must also register an
    /// <see cref="ISecretProtector"/> for the runner key.
    /// </summary>
    public static IServiceCollection AddRunnerSupervisor(this IServiceCollection services, SupervisorPaths paths, SupervisorOptions? options = null)
    {
        services.AddSingleton(paths);
        services.AddSingleton(options ?? new SupervisorOptions());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IProcessRunner, SystemProcessRunner>();
        services.AddSingleton<IRunnerSettingsStore, FileRunnerSettingsStore>();
        services.AddSingleton(new ContainerEngineSelection(statePath: paths.EngineState));
        services.AddSingleton(new SupervisorInstanceLock(paths.SupervisorLock));
        services.AddSingleton(_ => SupervisorOwner.LoadOrCreate(paths.Owner));
        services.AddSingleton<IContainerEngine>(provider => new DockerCliContainerEngine(
            provider.GetRequiredService<IProcessRunner>(),
            () => provider.GetRequiredService<ContainerEngineSelection>().Executable,
            () => provider.GetRequiredService<ContainerEngineSelection>().Pin));
        services.AddSingleton<IHostGpuProbe, NvidiaSmiHostGpuProbe>();
        services.TryAddSingleton<ILaunchPlanProvider, ControlPlaneLaunchPlanProvider>();
        services.AddSingleton<RunnerSupervisor>();
        services.AddHostedService(provider => provider.GetRequiredService<RunnerSupervisor>());
        return services;
    }
}
