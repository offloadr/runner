using System.Diagnostics.CodeAnalysis;

namespace Offloadr.Runner.Core;

/// <summary>The UUID that names one physical NVIDIA GPU, as <c>nvidia-smi</c> reports it, and the setting that binds a runner to it.</summary>
public static class NvidiaGpuUuid
{
    /// <summary>Binds the runner, and the editor it starts, to the one GPU it names.</summary>
    public const string EnvironmentVariable = "OFFLOADR_GPU_UUID";

    /// <summary>
    /// The UUID in <c>nvidia-smi</c>'s own spelling (<c>GPU-</c> and lower-case digits), or false when
    /// <paramref name="text"/> is not a complete one.
    /// </summary>
    public static bool TryNormalize(string? text, [NotNullWhen(true)] out string? uuid)
    {
        uuid = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (!text.StartsWith("GPU-", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(text[4..], "D", out var identifier))
        {
            return false;
        }

        uuid = "GPU-" + identifier.ToString("D");
        return true;
    }
}
