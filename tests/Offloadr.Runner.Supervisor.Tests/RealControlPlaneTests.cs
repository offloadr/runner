using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor.Tests;

/// <summary>
/// Calls GetRunnerLaunchPlan on a real control plane. Opt in with RUNNER_REAL_API_ORIGIN and
/// RUNNER_REAL_KEY (a runner key for that environment). Passes whether or not the server
/// implements the call yet; it fails when the answer cannot be classified.
/// </summary>
[Category("RealControlPlane")]
public class RealControlPlaneTests
{
    [Test]
    public async Task GetRunnerLaunchPlan_ReturnsAUsablePlan()
    {
        var origin = Environment.GetEnvironmentVariable("RUNNER_REAL_API_ORIGIN");
        var key = Environment.GetEnvironmentVariable("RUNNER_REAL_KEY");
        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(key))
        {
            Assert.Ignore("Set RUNNER_REAL_API_ORIGIN and RUNNER_REAL_KEY to run this test.");
        }

        using var provider = new ControlPlaneLaunchPlanProvider();
        var settings = new RunnerSettings { OffloadrApiOrigin = origin!, RunnerKey = key };

        var plan = await provider.GetAsync(settings, CancellationToken.None);

        TestContext.Out.WriteLine($"source={plan.Source} selected={plan.SelectedEditorTemplate ?? "-"} notice={plan.Notice ?? "-"}");
        foreach (var template in plan.Templates)
        {
            TestContext.Out.WriteLine($"  {template.EditorTemplate} -> {template.Image}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(plan.Templates, Is.Not.Empty);
            Assert.That(plan.Choose(settings), Is.Not.Null);
            if (plan.Source == LaunchPlanSource.BuiltIn)
            {
                Assert.That(plan.Notice, Does.Contain("does not suggest a runner image yet"), "Expected UNIMPLEMENTED, not a connection failure.");
            }
        });
    }
}
