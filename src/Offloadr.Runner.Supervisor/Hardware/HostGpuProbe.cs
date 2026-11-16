using System.Globalization;
using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Processes;

namespace Offloadr.Runner.Supervisor.Hardware;

/// <param name="Uuid">The UUID that names this GPU, in <c>nvidia-smi</c>'s spelling; null when the driver did not give one.</param>
public sealed record HostGpu(string Name, long MemoryMiB, string? Uuid = null);

public sealed record HostGpuReport(IReadOnlyList<HostGpu> Gpus, string? Error)
{
    public static HostGpuReport None(string error) => new([], error);
}

public interface IHostGpuProbe
{
    Task<HostGpuReport> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>Lists NVIDIA GPUs on the host with <c>nvidia-smi</c>, which ships with the driver on Windows and Linux.</summary>
public sealed class NvidiaSmiHostGpuProbe(IProcessRunner processes) : IHostGpuProbe
{
    public async Task<HostGpuReport> ProbeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var result = await processes.RunAsync(
                new ProcessSpec("nvidia-smi", ["--query-gpu=uuid,name,memory.total", "--format=csv,noheader,nounits"]),
                timeout.Token).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return HostGpuReport.None(FirstLine(result.StandardError) ?? "nvidia-smi failed.");
            }

            var gpus = Parse(result.StandardOutput);
            return gpus.Count == 0 ? HostGpuReport.None("nvidia-smi found no NVIDIA GPU.") : new HostGpuReport(gpus, null);
        }
        catch (ProcessStartException)
        {
            return HostGpuReport.None("No NVIDIA driver found (nvidia-smi is not available).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HostGpuReport.None("nvidia-smi did not answer.");
        }
    }

    internal static IReadOnlyList<HostGpu> Parse(string csv)
    {
        var gpus = new List<HostGpu>();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // uuid, name, memory: the name is what lies between the first comma and the last.
            var first = line.IndexOf(',');
            var last = line.LastIndexOf(',');
            if (first <= 0 || last <= first)
            {
                continue;
            }

            var name = line[(first + 1)..last].Trim();
            if (name.Length > 0 &&
                long.TryParse(line[(last + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var memory))
            {
                // A driver that cannot name the GPU prints "[N/A]": the GPU is listed, but cannot be chosen.
                gpus.Add(new HostGpu(name, memory, NvidiaGpuUuid.TryNormalize(line[..first], out var uuid) ? uuid : null));
            }
        }

        return gpus;
    }

    private static string? FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
