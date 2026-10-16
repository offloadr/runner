using System.Runtime.Versioning;

namespace Offloadr.Runner.Tests;

[SupportedOSPlatform("linux")]
public class NvidiaGpuSelectionProcessTests
{
    [SetUp]
    public void RequireLinux() => LinuxTestPrerequisites.RequireLinux();

    [Test]
    [SetCulture("sv-SE")]
    public async Task AllNativeLanesAddressTheSelectedUuid()
    {
        var directory = Path.Combine(Path.GetTempPath(), "offloadr-selected-gpu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var transcript = Path.Combine(directory, "arguments");
            var executable = Path.Combine(directory, "nvidia-smi");
            const string uuid = NvidiaGpuSelectionTests.SelectedUuid;
            await File.WriteAllTextAsync(executable, $$"""
                #!/bin/sh
                printf '%s\n' "$*" >> '{{transcript}}'
                case "$*" in
                  '--id={{uuid}} --query-gpu=name,memory.total --format=csv,noheader,nounits')
                    printf 'NVIDIA RTX 4000 Ada Generation,20480\n' ;;
                  '--id={{uuid}} --query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu,fan.speed,power.draw,power.limit,power.min_limit,power.default_limit,power.max_limit --format=csv,noheader,nounits')
                    printf '10,100,20480,40,20,50,130,60,130,140\n' ;;
                  '-i {{uuid}} -pl 100.5') exit 0 ;;
                  *) printf 'unexpected device selection\n' >&2; exit 9 ;;
                esac
                """.Replace("\r\n", "\n") + "\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var selection = NvidiaGpuSelection.FromValues(uuid, null);

            var resources = new ResourceDiscoveryService(new LinuxResourceProbe(executable, selection)).BuildInitialResources();
            var telemetry = new RuntimeTelemetryService(new NvidiaRuntimeTelemetryProbe(executable, selection)).TryCaptureSnapshot();
            var power = await new NvidiaSmiGpuPowerLimitCommandRunner(executable, selection).SetPowerLimitAsync(100.5, default);

            Assert.Multiple(() =>
            {
                Assert.That(resources.GpuCount, Is.EqualTo(1));
                Assert.That(resources.GpuModelName, Is.EqualTo("NVIDIA RTX 4000 Ada Generation"));
                Assert.That(resources.VramBytes, Is.EqualTo(20UL * 1024 * 1024 * 1024));
                Assert.That(telemetry!.VramTotalBytes, Is.EqualTo(resources.VramBytes));
                Assert.That(power.Success, Is.True);
                Assert.That(File.ReadAllLines(transcript), Has.Length.EqualTo(3));
                Assert.That(File.ReadAllLines(transcript).All(line => line.Contains(uuid, StringComparison.Ordinal)), Is.True);
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public async Task FailedQueriesAndPowerNeverFallBackToAnotherDevice()
    {
        var directory = Path.Combine(Path.GetTempPath(), "offloadr-missing-gpu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var transcript = Path.Combine(directory, "arguments");
            var executable = Path.Combine(directory, "nvidia-smi");
            await File.WriteAllTextAsync(executable, $$"""
                #!/bin/sh
                printf '%s\n' "$*" >> '{{transcript}}'
                case "$*" in
                  *--query-gpu=name*) printf 'NVIDIA GeForce RTX 3080,10240\n' ;;
                  *) printf '10,100,10240,40,20,50,130,60,130,140\n' ;;
                esac
                exit 4
                """.Replace("\r\n", "\n") + "\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var selection = NvidiaGpuSelection.FromValues(NvidiaGpuSelectionTests.SelectedUuid, null);

            var resources = new ResourceDiscoveryService(new LinuxResourceProbe(executable, selection)).BuildInitialResources();
            var output = new NvidiaRuntimeTelemetryProbe(executable, selection).CaptureRuntimeTelemetryQuery();
            var power = await new NvidiaSmiGpuPowerLimitCommandRunner(executable, selection).SetPowerLimitAsync(100, default);

            Assert.Multiple(() =>
            {
                Assert.That(resources.GpuCount, Is.Zero);
                Assert.That(output, Is.Empty);
                Assert.That(power.Success, Is.False);
                Assert.That(power.MayHaveApplied, Is.False);
                Assert.That(File.ReadAllLines(transcript), Has.Length.EqualTo(3));
                Assert.That(File.ReadAllLines(transcript).All(line => line.Contains(NvidiaGpuSelectionTests.SelectedUuid, StringComparison.Ordinal)), Is.True);
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
