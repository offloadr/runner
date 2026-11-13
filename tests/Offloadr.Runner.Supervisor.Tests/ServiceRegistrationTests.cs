using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor.Tests;

public class ServiceRegistrationTests
{
    [Test]
    public void AddRunnerSupervisor_ResolvesTheSupervisorAsAHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecretProtector, ReversingSecretProtector>();
        services.AddRunnerSupervisor(new SupervisorPaths(Path.Combine(Path.GetTempPath(), "runner-di-" + Guid.NewGuid().ToString("n"))));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.Multiple(() =>
        {
            Assert.That(provider.GetServices<IHostedService>().Single(), Is.SameAs(provider.GetRequiredService<RunnerSupervisor>()));
            Assert.That(provider.GetRequiredService<ILaunchPlanProvider>(), Is.InstanceOf<ControlPlaneLaunchPlanProvider>());
            Assert.That(provider.GetRequiredService<ILoggerFactory>(), Is.Not.Null);
        });
    }

    [Test]
    public void AddRunnerUpdates_RegistersTheUpdateServiceAsAHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRunnerUpdates<FakeAppUpdateSource>(new Version(1, 2, 3));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        var updates = provider.GetRequiredService<Offloadr.Runner.Supervisor.Hosting.AppUpdateService>();

        Assert.Multiple(() =>
        {
            Assert.That(provider.GetServices<IHostedService>().Single(), Is.SameAs(updates));
            Assert.That(updates.Status.Current, Is.EqualTo(new Version(1, 2, 3)));
        });
    }
}
