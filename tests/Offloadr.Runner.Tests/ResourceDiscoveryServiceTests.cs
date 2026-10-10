using Offloadr.Common.V1;

namespace Offloadr.Runner.Tests;

public class ResourceDiscoveryServiceTests
{
    [TestCase("NVIDIA GeForce RTX 4090", GpuType.NvidiaRtx4090)]
    [TestCase("NVIDIA GeForce RTX 5060 Ti", GpuType.NvidiaRtx5060Ti16)]
    [TestCase("NVIDIA GeForce RTX 5070 Ti", GpuType.NvidiaRtx5070Ti16)]
    [TestCase("NVIDIA GeForce RTX 3090 Ti", GpuType.NvidiaRtx3090Ti)]
    [TestCase("NVIDIA GeForce RTX 4080", GpuType.NvidiaRtx4080)]
    [TestCase("NVIDIA GeForce RTX 4080 SUPER", GpuType.NvidiaRtx4080Super)]
    [TestCase("NVIDIA GeForce RTX 5080", GpuType.NvidiaRtx5080)]
    [TestCase("NVIDIA RTX 6000 Ada", GpuType.NvidiaRtx6000Ada)]
    [TestCase("AMD Radeon Pro", GpuType.AmdOther)]
    [TestCase("Something Unknown", GpuType.Unknown)]
    public void MapGpuNameToType_MapsKnownNamesWithoutVramHint(string name, GpuType expected)
    {
        var result = ResourceDiscoveryService.MapGpuNameToType(name);

        Assert.That(result, Is.EqualTo(expected));
    }

    [TestCase("NVIDIA H100", 80, GpuType.NvidiaH100Pcie80)]
    [TestCase("NVIDIA H100 80GB HBM3", 80, GpuType.NvidiaH100Sxm80)]
    [TestCase("NVIDIA H100 PCIe", 80, GpuType.NvidiaH100Pcie80)]
    [TestCase("NVIDIA A100", 40, GpuType.NvidiaA100Pcie40)]
    [TestCase("NVIDIA A100", 80, GpuType.NvidiaA100Pcie80)]
    [TestCase("NVIDIA A100-PCIE-40GB", null, GpuType.NvidiaA100Pcie40)]
    [TestCase("NVIDIA A100-SXM4-40GB", null, GpuType.NvidiaA100Sxm40)]
    [TestCase("NVIDIA A100-SXM4-80GB", null, GpuType.NvidiaA100Sxm80)]
    public void MapGpuNameToType_MapsKnownNamesWithVramHint(string name, int? detectedVramGb, GpuType expected)
    {
        var result = ResourceDiscoveryService.MapGpuNameToType(name, detectedVramGb);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void ParseGpuQueryOutput_AggregatesGpuCountAndVram()
    {
        const string output = "NVIDIA GeForce RTX 4090,24564\nNVIDIA GeForce RTX 4090,24564\n";

        var result = ResourceDiscoveryService.ParseGpuQueryOutput(output);

        Assert.That(result.GpuType, Is.EqualTo(GpuType.NvidiaRtx4090));
        Assert.That(result.GpuModelName, Is.EqualTo("NVIDIA GeForce RTX 4090"));
        Assert.That(result.GpuCount, Is.EqualTo(2UL));
        Assert.That(result.VramBytes, Is.EqualTo(24564UL * 1024UL * 1024UL * 2UL));
    }

    [Test]
    public void ParseGpuQueryOutput_ReturnsUnknown_WhenOutputMalformed()
    {
        var result = ResourceDiscoveryService.ParseGpuQueryOutput("invalid");

        Assert.That(result.GpuType, Is.EqualTo(GpuType.Unknown));
        Assert.That(result.GpuModelName, Is.EqualTo(string.Empty));
        Assert.That(result.GpuCount, Is.EqualTo(0UL));
        Assert.That(result.VramBytes, Is.EqualTo(0UL));
    }

    [Test]
    public void ParseGpuQueryOutput_DoesNotCountDiagnosticLinesAsGpus()
    {
        const string output =
            "Failed to initialize NVML: Driver/library version mismatch\n" +
            "NVML library version: 550.54\n" +
            "NVIDIA GeForce RTX 4090,24564\n";

        var result = ResourceDiscoveryService.ParseGpuQueryOutput(output);

        Assert.Multiple(() =>
        {
            Assert.That(result.GpuCount, Is.EqualTo(1UL));
            Assert.That(result.GpuModelName, Is.EqualTo("NVIDIA GeForce RTX 4090"));
            Assert.That(result.GpuType, Is.EqualTo(GpuType.NvidiaRtx4090));
            Assert.That(result.VramBytes, Is.EqualTo(24564UL * 1024UL * 1024UL));
        });
    }

    [Test]
    public void ParseGpuQueryOutput_ReportsNoGpus_ForAnNvmlFailure()
    {
        var result = ResourceDiscoveryService.ParseGpuQueryOutput(
            "Failed to initialize NVML: Driver/library version mismatch\nNVML library version: 550.54\n");

        Assert.Multiple(() =>
        {
            Assert.That(result.GpuCount, Is.EqualTo(0UL));
            Assert.That(result.GpuType, Is.EqualTo(GpuType.Unknown));
        });
    }

    [Test]
    public void BuildInitialResources_UsesProbeForGpuAndRam()
    {
        var probe = new FakeProbe
        {
            GpuOutput = "NVIDIA GeForce RTX 5090,32000",
            MemTotalKb = 1024UL
        };

        var sut = new ResourceDiscoveryService(probe);

        var result = sut.BuildInitialResources();

        Assert.That(result.GpuType, Is.EqualTo(GpuType.NvidiaRtx5090));
        Assert.That(result.GpuModelName, Is.EqualTo("NVIDIA GeForce RTX 5090"));
        Assert.That(result.GpuCount, Is.EqualTo(1UL));
        Assert.That(result.VramBytes, Is.EqualTo(32000UL * 1024UL * 1024UL));
        Assert.That(result.SystemRamBytes, Is.EqualTo(1024UL * 1024UL));
    }

    [Test]
    public void GetTotalRamBytes_ReturnsZero_WhenProbeCannotReadMemInfo()
    {
        var sut = new ResourceDiscoveryService(new FakeProbe { CanReadMemInfo = false });

        var ramBytes = sut.GetTotalRamBytes();

        Assert.That(ramBytes, Is.EqualTo(0UL));
    }

    private sealed class FakeProbe : IResourceProbe
    {
        public string GpuOutput { get; init; } = string.Empty;
        public ulong MemTotalKb { get; init; }
        public bool CanReadMemInfo { get; init; } = true;

        public string CaptureGpuQuery() => GpuOutput;

        public bool TryReadLinuxMemTotalKb(out ulong memTotalKb)
        {
            memTotalKb = MemTotalKb;
            return CanReadMemInfo;
        }
    }
}
