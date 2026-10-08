namespace Offloadr.Runner.Linux;

/// <summary>
/// Decides which of the agent's own environment variables a session process may inherit.
/// Session processes run untrusted code, and the agent's environment carries the runner key,
/// the control-plane address and whatever the host or hosting provider injected, so only
/// variables the editors and the CUDA stack need are passed on. Everything the session
/// needs beyond that is set explicitly by the session and VFS environment builders.
/// </summary>
internal static class SessionEnvironmentFilter
{
    private static readonly HashSet<string> InheritableNames = new(StringComparer.Ordinal)
    {
        "PATH",
        "LANG",
        "LANGUAGE",
        "TZ",
        "TERM",
        "LD_LIBRARY_PATH",
    };

    private static readonly string[] InheritablePrefixes =
    [
        "LC_",
        "NVIDIA_",
        "NV_",
        "CUDA_",
        "NCCL_",
        "NVARCH",
        "UV_",
        "PYTHON",
        "PYTORCH_",
        "TORCH_",
        "XFORMERS_",
        "GRADIO_",
        "COMFY",
        "OFFLOADR_TORCH",
        "OFFLOADR_VENV",
    ];

    public static bool IsInheritable(string name)
        => InheritableNames.Contains(name)
            || InheritablePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Removes every variable that a session process must not inherit.</summary>
    public static void RemoveNonInheritable(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys.Where(name => !IsInheritable(name)).ToList())
        {
            environment.Remove(name);
        }
    }
}
