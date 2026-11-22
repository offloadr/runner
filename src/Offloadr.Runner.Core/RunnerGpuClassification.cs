using Offloadr.Common.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Core;

/// <summary>
/// Names a GPU the way Offloadr matches it, from what <c>nvidia-smi</c> prints. Shared by the agent in
/// the container and the desktop app, so a launcher and the executor it starts describe the same GPU
/// the same way.
/// </summary>
public static class RunnerGpuClassification
{
    /// <summary>The GPU type for a GPU named <paramref name="name"/> with <paramref name="memoryMiB"/> of memory (0 when unknown).</summary>
    public static GpuType Classify(string? name, ulong memoryMiB)
    {
        int? memoryGb = memoryMiB == 0 ? null : (int)Math.Round(memoryMiB / 1024d, MidpointRounding.AwayFromZero);
        if (ProviderGpuNormalization.TryNormalizeGpuName(name, memoryGb, out var gpuType, ProviderGpuNormalizationSource.Runner))
        {
            return gpuType;
        }

        var lower = (name ?? string.Empty).ToLowerInvariant();
        if (lower.Contains("nvidia", StringComparison.Ordinal)) return GpuType.NvidiaOther;
        if (lower.Contains("amd", StringComparison.Ordinal) || lower.Contains("radeon", StringComparison.Ordinal)) return GpuType.AmdOther;

        return GpuType.Unknown;
    }
}
