using Offloadr.Common.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public class RuntimeTelemetryServiceTests
{
    [Test]
    public void ParseQueryOutput_ParsesFirstGpuRow()
    {
        var sampledUtc = new DateTime(2026, 3, 23, 10, 0, 0, DateTimeKind.Utc);

        var result = RuntimeTelemetryService.ParseQueryOutput("17, 5287, 10240, 39, 42, 98.5, 320.0, 100.0, 250.0, 350.0\n4, 123, 10240, 40, 43, 90.0, 320.0, 100.0, 250.0, 350.0\n", sampledUtc);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.UtilizationPercent, Is.EqualTo(17u));
        Assert.That(result.VramUsedBytes, Is.EqualTo(5287UL * 1024UL * 1024UL));
        Assert.That(result.VramTotalBytes, Is.EqualTo(10240UL * 1024UL * 1024UL));
        Assert.That(result.TemperatureCelsius, Is.EqualTo(39u));
        Assert.That(result.FanPercent, Is.EqualTo(42u));
        Assert.That(result.PowerUsageWatts, Is.EqualTo(98.5d));
        Assert.That(result.PowerLimitWatts, Is.EqualTo(320.0d));
        Assert.That(result.PowerMinLimitWatts, Is.EqualTo(100.0d));
        Assert.That(result.PowerDefaultLimitWatts, Is.EqualTo(250.0d));
        Assert.That(result.PowerMaxLimitWatts, Is.EqualTo(350.0d));
        Assert.That(result.SampledUtc?.ToDateTime(), Is.EqualTo(sampledUtc));
    }

    [Test]
    public void ParseQueryOutput_TreatsNaAsMissing()
    {
        var result = RuntimeTelemetryService.ParseQueryOutput("N/A, 5287, 10240, [N/A], N/A, [N/A], N/A\n", DateTime.UtcNow);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.UtilizationPercent, Is.Null);
        Assert.That(result.VramUsedBytes, Is.EqualTo(5287UL * 1024UL * 1024UL));
        Assert.That(result.VramTotalBytes, Is.EqualTo(10240UL * 1024UL * 1024UL));
        Assert.That(result.TemperatureCelsius, Is.Null);
        Assert.That(result.FanPercent, Is.Null);
        Assert.That(result.PowerUsageWatts, Is.Null);
        Assert.That(result.PowerLimitWatts, Is.Null);
    }

    [Test]
    public void ParseQueryOutput_ReturnsNull_WhenRowMalformed()
    {
        var result = RuntimeTelemetryService.ParseQueryOutput("bad-output", DateTime.UtcNow);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void TryCaptureSnapshot_UsesProbeOutput()
    {
        var service = new RuntimeTelemetryService(new FakeProbe { Output = "9, 100, 200, 44, 35, 85.25, 320.0, 100.0, 250.0, 350.0" });

        var result = service.TryCaptureSnapshot();

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.UtilizationPercent, Is.EqualTo(9u));
        Assert.That(result.VramUsedBytes, Is.EqualTo(100UL * 1024UL * 1024UL));
        Assert.That(result.TemperatureCelsius, Is.EqualTo(44u));
        Assert.That(result.FanPercent, Is.EqualTo(35u));
        Assert.That(result.PowerUsageWatts, Is.EqualTo(85.25d));
        Assert.That(result.PowerLimitWatts, Is.EqualTo(320.0d));
        Assert.That(result.PowerMinLimitWatts, Is.EqualTo(100.0d));
        Assert.That(result.PowerDefaultLimitWatts, Is.EqualTo(250.0d));
        Assert.That(result.PowerMaxLimitWatts, Is.EqualTo(350.0d));
    }

    [Test]
    public void TryCaptureSnapshot_AddsRamAndModelDiskMetrics()
    {
        var service = new RuntimeTelemetryService(new FakeProbe
        {
            Output = "9, 100, 200, 44, 35, 85.25, 320.0, 100.0, 250.0, 350.0",
            MemInfo = """
                MemTotal:       16384000 kB
                MemAvailable:    4096000 kB
                """,
            DiskSpace = new RuntimeDiskSpaceSnapshot("/mnt/models", 40_000, 100_000)
        }, "/mnt/models");

        var result = service.TryCaptureSnapshot();

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SystemRamTotalBytes, Is.EqualTo(16_384_000UL * 1024UL));
        Assert.That(result.SystemRamUsedBytes, Is.EqualTo((16_384_000UL - 4_096_000UL) * 1024UL));
        Assert.That(result.ModelDiskAvailableBytes, Is.EqualTo(40_000UL));
        Assert.That(result.ModelDiskTotalBytes, Is.EqualTo(100_000UL));
        Assert.That(result.ModelDiskPath, Is.EqualTo("/mnt/models"));
    }

    [Test]
    public void TryCaptureSnapshot_ReturnsSystemMetrics_WhenGpuUnavailable()
    {
        var service = new RuntimeTelemetryService(new FakeProbe
        {
            Output = string.Empty,
            MemInfo = """
                MemTotal:       8192000 kB
                MemAvailable:   2048000 kB
                """,
            DiskSpace = new RuntimeDiskSpaceSnapshot("/models", 10, 20)
        });

        var result = service.TryCaptureSnapshot();

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.UtilizationPercent, Is.Null);
        Assert.That(result.SystemRamTotalBytes, Is.EqualTo(8_192_000UL * 1024UL));
        Assert.That(result.ModelDiskAvailableBytes, Is.EqualTo(10UL));
    }

    [Test]
    public void TryCaptureSnapshot_ReturnsNull_WhenAllMetricsUnavailable()
    {
        var service = new RuntimeTelemetryService(new FakeProbe());

        var result = service.TryCaptureSnapshot();

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GpuPowerLimitService_UsesExplicitConfirmedWatts()
    {
        var probe = new FakeProbe
        {
            Output = "9, 100, 200, 44, 35, 85.25, 350.0, 100.0, 250.0, 350.0"
        };
        var runner = new FakeGpuPowerLimitCommandRunner();
        var service = new GpuPowerLimitService(new RuntimeTelemetryService(probe), runner);

        var result = await service.ApplyAsync(
            new SetGpuPowerLimitCommand
            {
                CommandId = "cmd-1",
                SessionId = "session-1",
                Target = new GpuControlTarget { Generation = 1 },
                TargetWatts = 350,
            },
            () => "session-1", () => 1,
            CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(GpuPowerOutcome.Applied));
        Assert.That(result.RequestedWatts, Is.EqualTo(350.0d));
        Assert.That(result.AppliedWatts, Is.EqualTo(350.0d));
        Assert.That(runner.TargetWatts, Is.EqualTo(350.0d));
    }

    [Test]
    public async Task GpuPowerLimitService_RejectsTargetOutsideReportedBounds()
    {
        var runner = new FakeGpuPowerLimitCommandRunner();
        var service = new GpuPowerLimitService(
            new RuntimeTelemetryService(new FakeProbe { Output = "9, 100, 200, 44, 35, 85.25, 320.0, 100.0, 250.0, 350.0" }),
            runner);

        var result = await service.ApplyAsync(
            new SetGpuPowerLimitCommand
            {
                CommandId = "cmd-1",
                SessionId = "session-1",
                Target = new GpuControlTarget { Generation = 1 },
                TargetWatts = 90,
            },
            () => "session-1", () => 1,
            CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(GpuPowerOutcome.Rejected));
        Assert.That(result.Message, Does.Contain("below the supported minimum"));
        Assert.That(runner.CallCount, Is.EqualTo(0));
    }

    [Test]
    public async Task GpuPowerLimitService_SurfacesNvidiaSmiFailures()
    {
        var runner = new FakeGpuPowerLimitCommandRunner
        {
            Result = new GpuPowerLimitCommandResult(false, string.Empty, "Insufficient Permissions", 4)
        };
        var service = new GpuPowerLimitService(
            new RuntimeTelemetryService(new FakeProbe { Output = "9, 100, 200, 44, 35, 85.25, 320.0, 100.0, 250.0, 350.0" }),
            runner);

        var result = await service.ApplyAsync(
            new SetGpuPowerLimitCommand
            {
                CommandId = "cmd-1",
                SessionId = "session-1",
                Target = new GpuControlTarget { Generation = 1 },
                TargetWatts = 320,
            },
            () => "session-1", () => 1,
            CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(GpuPowerOutcome.Rejected));
        Assert.That(result.RequestedWatts, Is.EqualTo(320.0d));
        Assert.That(result.Message, Does.Contain("Insufficient Permissions"));
    }

    [Test]
    public async Task GpuPowerLimitService_ReportsTargetWatts_WhenPostApplyTelemetryUnavailable()
    {
        var probe = new FakeProbe
        {
            Outputs = new Queue<string>([
                "9, 100, 200, 44, 35, 85.25, 250.0, 100.0, 250.0, 350.0",
                string.Empty,
            ])
        };
        var runner = new FakeGpuPowerLimitCommandRunner();
        var service = new GpuPowerLimitService(new RuntimeTelemetryService(probe), runner);

        var result = await service.ApplyAsync(
            new SetGpuPowerLimitCommand
            {
                CommandId = "cmd-1",
                SessionId = "session-1",
                Target = new GpuControlTarget { Generation = 1 },
                TargetWatts = 320,
            },
            () => "session-1", () => 1,
            CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(GpuPowerOutcome.Applied));
        Assert.That(result.RequestedWatts, Is.EqualTo(320.0d));
        Assert.That(result.AppliedWatts, Is.EqualTo(320.0d));
        Assert.That(result.Telemetry, Is.Null);
    }

    [Test]
    public async Task ActiveSessionRuntimeTelemetryReporter_OnlySamplesWhenSessionActive()
    {
        string activeSessionId = string.Empty;
        var sampleCalls = 0;
        var enqueued = new List<(string SessionId, RunnerRuntimeTelemetrySnapshot Snapshot)>();
        var reporter = new ActiveSessionRuntimeTelemetryReporter(
            () => activeSessionId,
            () =>
            {
                sampleCalls++;
                return new RunnerRuntimeTelemetrySnapshot();
            },
            (sessionId, snapshot) => enqueued.Add((sessionId, snapshot)),
            TimeSpan.FromMilliseconds(20));

        using var cts = new CancellationTokenSource();
        var runTask = reporter.RunAsync(cts.Token);

        await Task.Delay(35);
        activeSessionId = "session-1";
        await Task.Delay(50);
        cts.Cancel();
        await runTask;

        Assert.That(sampleCalls, Is.GreaterThanOrEqualTo(1));
        Assert.That(enqueued, Is.Not.Empty);
        Assert.That(enqueued.All(entry => entry.SessionId == "session-1"), Is.True);
    }

    private sealed class FakeProbe : IRuntimeTelemetryProbe
    {
        public string Output { get; init; } = string.Empty;

        public Queue<string>? Outputs { get; init; }

        public string? MemInfo { get; init; }

        public RuntimeDiskSpaceSnapshot? DiskSpace { get; init; }

        public string CaptureRuntimeTelemetryQuery()
            => Outputs is { Count: > 0 } ? Outputs.Dequeue() : Output;

        public string? ReadMemInfo() => MemInfo;

        public bool TryGetDiskSpace(string path, out RuntimeDiskSpaceSnapshot snapshot)
        {
            _ = path;
            snapshot = DiskSpace.GetValueOrDefault();
            return DiskSpace.HasValue;
        }
    }

    private sealed class FakeGpuPowerLimitCommandRunner : IGpuPowerLimitCommandRunner
    {
        public int CallCount { get; private set; }

        public double? TargetWatts { get; private set; }

        public GpuPowerLimitCommandResult Result { get; init; } =
            new(true, "Power limit set", string.Empty, 0);

        public Task<GpuPowerLimitCommandResult> SetPowerLimitAsync(double targetWatts, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            CallCount++;
            TargetWatts = targetWatts;
            return Task.FromResult(Result);
        }
    }
}
