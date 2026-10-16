namespace Offloadr.Runner.Tests;

public class NvidiaGpuSelectionTests
{
    internal const string SelectedUuid = "GPU-11111111-2222-3333-4444-555555555555";

    [Test]
    public void ExplicitSelectionUsesTheSameDeviceForQueriesAndPower()
    {
        var selection = NvidiaGpuSelection.FromValues(SelectedUuid.ToLowerInvariant(), null);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Uuid, Is.EqualTo(SelectedUuid));
            Assert.That(selection.QueryArguments("name,memory.total"), Is.EqualTo(new[]
                { "--id=" + SelectedUuid, "--query-gpu=name,memory.total", "--format=csv,noheader,nounits" }));
            Assert.That(selection.PowerArguments("200.5"), Is.EqualTo(new[] { "-i", SelectedUuid, "-pl", "200.5" }));
        });
    }

    [Test]
    public void UnselectedHostRetainsItsInventoryAndDefaultPowerTarget()
    {
        var selection = NvidiaGpuSelection.FromValues(null, "0");
        Assert.That(selection.QueryArguments("name,memory.total"), Is.EqualTo(new[]
            { "--query-gpu=name,memory.total", "--format=csv,noheader,nounits" }));
        Assert.That(selection.PowerArguments("200"), Is.EqualTo(new[] { "-i", "0", "-pl", "200" }));
    }

    [TestCase("0")]
    [TestCase("GPU-11111111")]
    [TestCase("GPU-11111111-2222-3333-4444-555555555555 -pl 300")]
    [TestCase("MIG-11111111-2222-3333-4444-555555555555")]
    public void MalformedPhysicalSelectionIsRejectedBeforeAnyNativeCommand(string value)
        => Assert.Throws<ArgumentException>(() => NvidiaGpuSelection.FromValues(value, null));

    [TestCase("")]
    [TestCase("0")]
    [TestCase("-1")]
    [TestCase("GPU-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [TestCase("accidentally-pasted-runner-secret")]
    public void ConflictingCudaMaskIsRejectedWithoutExposingItsValue(string value)
    {
        var error = Assert.Throws<ArgumentException>(() => NvidiaGpuSelection.FromValues(SelectedUuid, value));
        Assert.That(error!.Message, Does.Contain("CUDA_VISIBLE_DEVICES conflicts"));
        if (value.Length > 0) Assert.That(error.Message, Does.Not.Contain(value));
    }

    [TestCase(null)]
    [TestCase(SelectedUuid)]
    public void ChildEnvironmentPinsCudaAndDropsAgentAuthority(string? cudaMask)
    {
        var environment = new Dictionary<string, string?>
        {
            [NvidiaGpuSelection.EnvironmentVariable] = SelectedUuid,
            ["RUNNER_SECRET"] = "synthetic-secret",
            ["OFFLOADR_API_GRPC"] = "https://example.invalid",
            ["PATH"] = "/usr/bin",
        };
        if (cudaMask is not null) environment["CUDA_VISIBLE_DEVICES"] = cudaMask;

        SessionEnvironmentFilter.PrepareForChildProcess(environment);

        Assert.That(environment, Is.EquivalentTo(new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["CUDA_VISIBLE_DEVICES"] = SelectedUuid,
        }));
    }
}
