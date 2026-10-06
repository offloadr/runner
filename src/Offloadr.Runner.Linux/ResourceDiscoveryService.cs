using Offloadr.Common.V1;
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
        var detectedType = GpuType.Unknown;
        var detectedName = string.Empty;

        for (var index = 0; index < lines.Length; index++)
        {
            var parts = lines[index].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var detectedGpuVramGb = TryNormalizeDetectedGpuVramGb(parts[1]);
            if (index == 0)
            {
                detectedName = parts[0].Trim();
                detectedType = MapGpuNameToType(parts[0], detectedGpuVramGb);
            }

            if (ulong.TryParse(parts[1], out var memMiB))
            {
                checked
                {
                    totalVramBytes += memMiB * 1024UL * 1024UL;
                }
            }
        }

        return (detectedType, detectedName, (ulong)lines.Length, totalVramBytes);
    }

    public static GpuType MapGpuNameToType(string nameRaw, int? detectedVramGb = null)
    {
        if (ProviderGpuNormalization.TryNormalizeGpuName(nameRaw, detectedVramGb, out var gpuType, ProviderGpuNormalizationSource.Runner))
        {
            return gpuType;
        }

        var name = (nameRaw ?? string.Empty).ToLowerInvariant();
        if (name.Contains("nvidia", StringComparison.Ordinal)) return GpuType.NvidiaOther;
        if (name.Contains("amd", StringComparison.Ordinal) || name.Contains("radeon", StringComparison.Ordinal)) return GpuType.AmdOther;

        return GpuType.Unknown;
    }

    private static int? TryNormalizeDetectedGpuVramGb(string? memoryTotalMiBRaw)
    {
        if (!ulong.TryParse(memoryTotalMiBRaw, out var memoryTotalMiB) || memoryTotalMiB == 0)
        {
            return null;
        }

        return (int)Math.Round(memoryTotalMiB / 1024d, MidpointRounding.AwayFromZero);
    }
}

public sealed class LinuxResourceProbe : IResourceProbe
{
    public string CaptureGpuQuery()
    {
        return RunProcessCapture("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader,nounits", timeoutMs: 4000);
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

    private static string RunProcessCapture(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

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

            var stdout = process.StandardOutput.ReadToEnd();
            if (string.IsNullOrWhiteSpace(stdout))
            {
                stdout = process.StandardError.ReadToEnd();
            }

            return stdout;
        }
        catch
        {
            return string.Empty;
        }
    }
}
