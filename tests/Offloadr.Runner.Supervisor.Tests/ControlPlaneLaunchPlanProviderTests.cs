using Grpc.Core;
using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Settings;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Supervisor.Tests;

public class ControlPlaneLaunchPlanProviderTests
{
    private static readonly RunnerSettings Settings = new() { RunnerKey = "runner-key", SelectedEditorTemplate = "forge-neo:neo" };

    [Test]
    public async Task UsesTheControlPlaneAnswer()
    {
        var response = new GetRunnerLaunchPlanResponse { SelectedEditorTemplate = "comfyui-master", RefreshSeconds = 120 };
        response.Templates.Add(new RunnerLaunchTemplate
        {
            EditorTemplate = "comfyui-master",
            EditorRuntimeKind = "comfyui",
            RunnerImage = "ghcr.io/offloadr/runner-comfyui@sha256:abc",
            Label = "ComfyUI - master branch",
        });
        var provider = new ControlPlaneLaunchPlanProvider((_, _) => Task.FromResult(response));

        var plan = await provider.GetAsync(Settings, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Source, Is.EqualTo(LaunchPlanSource.ControlPlane));
            Assert.That(plan.RefreshInterval, Is.EqualTo(TimeSpan.FromMinutes(2)));
            Assert.That(plan.Choose(Settings)?.Image, Is.EqualTo("ghcr.io/offloadr/runner-comfyui@sha256:abc"));
        });
    }

    [TestCase(StatusCode.Unimplemented, LaunchPlanSource.BuiltIn)]
    [TestCase(StatusCode.Unavailable, LaunchPlanSource.Unreachable)]
    [TestCase(StatusCode.DeadlineExceeded, LaunchPlanSource.Unreachable)]
    public async Task FallsBackToTheBuiltInCatalog(StatusCode status, LaunchPlanSource expectedSource)
    {
        var provider = new ControlPlaneLaunchPlanProvider((_, _) => throw new RpcException(new Status(status, "test")));

        var plan = await provider.GetAsync(Settings, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Source, Is.EqualTo(expectedSource));
            Assert.That(plan.Notice, Is.Not.Null);
            Assert.That(plan.Choose(Settings)?.EditorTemplate, Is.EqualTo("forge-neo:neo"));
        });
    }

    [Test]
    public async Task ReportsAnUnreachableServer_WhenTheConnectionFails()
    {
        var provider = new ControlPlaneLaunchPlanProvider((_, _) => throw new HttpRequestException("connection refused"));

        var plan = await provider.GetAsync(Settings, CancellationToken.None);

        Assert.That(plan.Source, Is.EqualTo(LaunchPlanSource.Unreachable));
    }

    [Test]
    public void ReportsACancelledCallAsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = new ControlPlaneLaunchPlanProvider((_, _) => throw new RpcException(new Status(StatusCode.Cancelled, "cancelled")));

        Assert.That(
            async () => await provider.GetAsync(Settings, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public void ReportsARejectedKey()
    {
        var provider = new ControlPlaneLaunchPlanProvider((_, _) => throw new RpcException(new Status(StatusCode.Unauthenticated, "bad key")));

        Assert.ThrowsAsync<LaunchPlanRejectedException>(() => provider.GetAsync(Settings, CancellationToken.None));
    }

    [TestCase("http://offloadr.studio")]
    [TestCase("not a url")]
    public async Task NeverSendsTheKeyToAnAddressThatIsNotHttps(string origin)
    {
        var called = false;
        var provider = new ControlPlaneLaunchPlanProvider((_, _) =>
        {
            called = true;
            return Task.FromResult(new GetRunnerLaunchPlanResponse());
        });

        var plan = await provider.GetAsync(Settings with { OffloadrApiOrigin = origin }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(called, Is.False);
            Assert.That(plan.Source, Is.EqualTo(LaunchPlanSource.BuiltIn));
        });
    }

    [Test]
    public async Task DoesNotCallWithoutAKey()
    {
        var called = false;
        var provider = new ControlPlaneLaunchPlanProvider((_, _) =>
        {
            called = true;
            return Task.FromResult(new GetRunnerLaunchPlanResponse());
        });

        var plan = await provider.GetAsync(new RunnerSettings(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(called, Is.False);
            Assert.That(plan.Source, Is.EqualTo(LaunchPlanSource.BuiltIn));
        });
    }

    [Test]
    public void Map_IgnoresIncompleteTemplates_AndFallsBackWhenNoneRemain()
    {
        var response = new GetRunnerLaunchPlanResponse();
        response.Templates.Add(new RunnerLaunchTemplate { EditorTemplate = "comfyui-latest" });

        var plan = ControlPlaneLaunchPlanProvider.Map(response);

        Assert.That(plan.Source, Is.EqualTo(LaunchPlanSource.BuiltIn));
    }
}
