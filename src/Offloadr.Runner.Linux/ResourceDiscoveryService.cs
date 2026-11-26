using Offloadr.Common.V1;
using Offloadr.Runner.Core;
using Offloadr.Runner.V1;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Offloadr.Runner.Linux;

public interface IResourceProbe
{
    string CaptureGpuQuery();
    bool TryReadLinuxMemTotalKb(out ulong memTotalKb);
}

public sealed class ResourceDiscoveryService
{
    private readonly IResourceProbe _probe;

    public ResourceDiscoveryService()
        : this(new LinuxResourceProbe())
    {
    }

    public ResourceDiscoveryService(IResourceProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public RunnerResources BuildInitialResources()
    {
        var (gpuType, gpuModelName, gpuCount, vramBytes) = ParseGpuQueryOutput(_probe.CaptureGpuQuery());

        return new RunnerResources
        {
            GpuType = gpuType,
            GpuModelName = gpuModelName,
            GpuCount = gpuCount,
            VramBytes = vramBytes,
            SystemRamBytes = GetTotalRamBytes()
        };
    }

    public ulong GetTotalRamBytes()
    {
        if (_probe.TryReadLinuxMemTotalKb(out var memTotalKb))
        {
            return memTotalKb * 1024UL;
        }

        return 0;
    }

    public static (GpuType GpuType, string GpuModelName, ulong GpuCount, ulong VramBytes) ParseGpuQueryOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return (GpuType.Unknown, string.Empty, 0, 0);
        }

        var lines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        if (lines.Length == 0)
        {
            return (GpuType.Unknown, string.Empty, 0, 0);
        }

        ulong totalVramBytes = 0;
        ulong gpuCount = 0;
        var detectedType = GpuType.Unknown;
        var detectedName = string.Empty;

        // Only rows in the queried shape count as GPUs: diagnostics or other text in the
        // output must never make the runner advertise hardware it does not have.
        foreach (var line in lines)
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || !ulong.TryParse(parts[1], out var memMiB))
            {
                continue;
            }

            if (gpuCount == 0)
            {
                detectedName = parts[0];
                detectedType = RunnerGpuClassification.Classify(parts[0], memMiB);
            }

            checked
            {
                totalVramBytes += memMiB * 1024UL * 1024UL;
                gpuCount++;
            }
        }

        return (detectedType, detectedName, gpuCount, totalVramBytes);
    }

    public static GpuType MapGpuNameToType(string nameRaw, int? detectedVramGb = null)
        => RunnerGpuClassification.Classify(nameRaw, detectedVramGb is > 0 ? (ulong)detectedVramGb.Value * 1024UL : 0);
}

public sealed class LinuxResourceProbe : IResourceProbe
{
    private readonly string _executablePath;
    private readonly NvidiaGpuSelection _gpuSelection;

    public LinuxResourceProbe() : this("nvidia-smi", NvidiaGpuSelection.CaptureEnvironment()) { }

    internal LinuxResourceProbe(string executablePath, NvidiaGpuSelection gpuSelection)
    {
        _executablePath = executablePath;
        _gpuSelection = gpuSelection;
    }

    public string CaptureGpuQuery()
    {
        return RunProcessCapture(_executablePath, _gpuSelection.QueryArguments("name,memory.total"), timeoutMs: 4000);
    }

    public bool TryReadLinuxMemTotalKb(out ulong memTotalKb)
    {
        memTotalKb = 0;

        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return false;
            }

            var line = File.ReadLines("/proc/meminfo").FirstOrDefault(item => item.StartsWith("MemTotal:", StringComparison.Ordinal));
            if (line is null)
            {
                return false;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return false;
            }

            return ulong.TryParse(parts[1], out memTotalKb);
        }
        catch
        {
            return false;
        }
    }

    private static string RunProcessCapture(string fileName, IEnumerable<string> arguments, int timeoutMs)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);

            if (!process.Start())
            {
                return string.Empty;
            }

            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort process cleanup.
                }

                return string.Empty;
            }

            // A failed query (for example an NVML driver mismatch) reports no hardware; its
            // diagnostics are never parsed as query output.
            return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
