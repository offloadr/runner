namespace Offloadr.Runner.Linux;

/// <summary>Binds native GPU operations and child CUDA visibility to one physical device.</summary>
internal sealed class NvidiaGpuSelection
{
    internal const string EnvironmentVariable = NvidiaGpuUuid.EnvironmentVariable;

    private NvidiaGpuSelection(string? uuid) => Uuid = uuid;

    internal string? Uuid { get; }

    internal static NvidiaGpuSelection CaptureEnvironment()
        => FromValues(Environment.GetEnvironmentVariable(EnvironmentVariable),
            Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES"));

    internal static NvidiaGpuSelection FromValues(string? uuid, string? cudaVisibleDevices)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return new(null);
        if (!NvidiaGpuUuid.TryNormalize(uuid, out var selected))
        {
            throw new ArgumentException("OFFLOADR_GPU_UUID must be a complete NVIDIA GPU UUID.");
        }

        // A CUDA ordinal can identify a different physical device under WSL or
        // a container remapping. Never silently override an explicit CUDA mask.
        if (cudaVisibleDevices is not null
            && !string.Equals(cudaVisibleDevices.Trim(), selected, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("CUDA_VISIBLE_DEVICES conflicts with OFFLOADR_GPU_UUID. Remove the CUDA mask or set it to the same GPU UUID.");
        }
        return new(selected);
    }

    internal string[] QueryArguments(string fields)
        => Uuid is null
            ? [$"--query-gpu={fields}", "--format=csv,noheader,nounits"]
            : [$"--id={Uuid}", $"--query-gpu={fields}", "--format=csv,noheader,nounits"];

    internal string[] PowerArguments(string watts) => ["-i", Uuid ?? "0", "-pl", watts];

    internal void BindChildEnvironment(IDictionary<string, string?> environment)
    {
        if (Uuid is not null) environment["CUDA_VISIBLE_DEVICES"] = Uuid;
    }
}
