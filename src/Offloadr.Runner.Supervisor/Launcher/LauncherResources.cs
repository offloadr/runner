using Offloadr.Common.V1;
using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Hardware;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Supervisor.Launcher;

/// <summary>The resources a launcher reports: what an executor it starts would see.</summary>
public static class LauncherResources
{
    /// <summary>
    /// The GPUs in <paramref name="gpus"/>, only the one chosen in Settings when
    /// <paramref name="selectedGpuUuid"/> is set (a container gets only that one), and the engine's
    /// memory. <paramref name="gpus"/> must be what the engine's containers see: with an engine on
    /// another machine, this computer's own GPUs are not.
    /// </summary>
    public static RunnerResources From(HostGpuReport gpus, string? selectedGpuUuid, long? engineMemoryBytes)
    {
        ArgumentNullException.ThrowIfNull(gpus);
        var visible = selectedGpuUuid is null
            ? gpus.Gpus
            : gpus.Gpus.Where(gpu => string.Equals(gpu.Uuid, selectedGpuUuid, StringComparison.OrdinalIgnoreCase)).ToList();

        var resources = new RunnerResources
        {
            GpuType = GpuType.Unknown,
            SystemRamBytes = engineMemoryBytes is > 0 ? (ulong)engineMemoryBytes.Value : 0,
        };
        if (visible.Count == 0)
        {
            return resources;
        }

        var first = visible[0];
        resources.GpuType = RunnerGpuClassification.Classify(first.Name, first.MemoryMiB > 0 ? (ulong)first.MemoryMiB : 0);
        resources.GpuModelName = first.Name;
        resources.GpuCount = (ulong)visible.Count;
        resources.VramBytes = visible.Aggregate(0UL, (total, gpu) => total + (gpu.MemoryMiB > 0 ? (ulong)gpu.MemoryMiB * 1024UL * 1024UL : 0));
        return resources;
    }
}
