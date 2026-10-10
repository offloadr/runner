namespace Offloadr.Runner.Linux;

/// <summary>
/// Decides which of the agent's own environment variables a session process may inherit.
/// Session processes run untrusted code, and the agent's environment carries the runner key,
/// the control-plane address and whatever the host or hosting provider injected, so only
/// named variables the editors and the CUDA stack need are passed on. Whole prefixes are not
/// admitted, because settings such as package index URLs can embed credentials. Everything
/// the session needs beyond this is set explicitly by the session and VFS environment builders.
/// </summary>
internal static class SessionEnvironmentFilter
{
    private static readonly HashSet<string> InheritableNames = new(StringComparer.Ordinal)
    {
        // Process basics and locale.
        "PATH",
        "LANG",
        "LANGUAGE",
        "TZ",
        "TERM",
        "LD_LIBRARY_PATH",

        // CUDA stack, as set by the CUDA base images and by GPU hosts.
        "NVARCH",
        "NVIDIA_VISIBLE_DEVICES",
        "NVIDIA_DRIVER_CAPABILITIES",
        "NVIDIA_REQUIRE_CUDA",
        "NVIDIA_PRODUCT_NAME",
        "CUDA_VERSION",
        "CUDA_VISIBLE_DEVICES",
        "CUDA_DEVICE_ORDER",
        "CUDA_MODULE_LOADING",
        "NCCL_VERSION",
        "NV_CUDA_CUDART_VERSION",
        "NV_CUDA_LIB_VERSION",
        "NV_NVTX_VERSION",
        "NV_LIBNPP_VERSION",
        "NV_LIBNPP_PACKAGE",
        "NV_LIBCUSPARSE_VERSION",
        "NV_LIBCUBLAS_PACKAGE_NAME",
        "NV_LIBCUBLAS_VERSION",
        "NV_LIBCUBLAS_PACKAGE",
        "NV_LIBNCCL_PACKAGE_NAME",
        "NV_LIBNCCL_PACKAGE_VERSION",
        "NV_LIBNCCL_PACKAGE",

        // Python, PyTorch and xformers runtime settings.
        "PYTHONUNBUFFERED",
        "PYTHONDONTWRITEBYTECODE",
        "PYTHONIOENCODING",
        "PYTORCH_CUDA_ALLOC_CONF",
        "PYTORCH_ALLOC_CONF",
        "TORCH_CUDA_ARCH_LIST",
        "XFORMERS_IGNORE_FLASH_VERSION_CHECK",

        // uv and the image's torch stack pins. Index URLs are left out: they can carry credentials.
        "UV_LINK_MODE",
        "UV_PYTHON_INSTALL_DIR",
        "UV_PYTHON_BIN_DIR",
        "UV_CONSTRAINT",
        "OFFLOADR_VENV",
        "OFFLOADR_TORCH_VERSION",
        "OFFLOADR_TORCHVISION_VERSION",
        "OFFLOADR_TORCH_FLAVOR",

        // Editor settings; OFFLOADR_FORCE_CPU is read by the editor entrypoints.
        "OFFLOADR_FORCE_CPU",
        "COMFYUI_PATH",
        "COMFY_PORT",
        "COMFY_WORKDIR",
        "GRADIO_ANALYTICS_ENABLED",
    };

    public static bool IsInheritable(string name)
        => InheritableNames.Contains(name) || name.StartsWith("LC_", StringComparison.Ordinal);

    /// <summary>Removes every variable that a session process must not inherit.</summary>
    public static void RemoveNonInheritable(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys.Where(name => !IsInheritable(name)).ToList())
        {
            environment.Remove(name);
        }
    }
}
