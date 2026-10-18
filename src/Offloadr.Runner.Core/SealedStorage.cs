using System.Globalization;
using System.Text.Json.Serialization;

namespace Offloadr.Runner.Core;

/// <summary>When sessions keep their data in memory-backed storage.</summary>
public enum SealedStorageMode
{
    /// <summary>Never; a start that requires it is refused.</summary>
    Off,

    /// <summary>Only for sessions whose start asks for it.</summary>
    Auto,

    /// <summary>For every session; startup fails when the sealed root is unusable.</summary>
    Required,
}

/// <summary>
/// Settings for the memory-backed session workspace (<c>RUNNER_SEALED_*</c>).
/// </summary>
public sealed class SealedStorageOptions
{
    public const string DefaultRoot = "/dev/shm/offloadr";
    public const int DefaultMinimumGigabytes = 4;
    private const long BytesPerGigabyte = 1L << 30;

    public SealedStorageMode Mode { get; init; } = SealedStorageMode.Auto;

    /// <summary>Directory on a tmpfs or ramfs that holds one subdirectory per sealed session.</summary>
    public string Root { get; init; } = DefaultRoot;

    /// <summary>Smallest usable budget (free space and memory headroom) for the root to count as available.</summary>
    public long MinimumBytes { get; init; } = DefaultMinimumGigabytes * BytesPerGigabyte;

    /// <summary>
    /// The unrecognised <c>RUNNER_SEALED_STORAGE</c> value, if any. The mode is then
    /// <see cref="SealedStorageMode.Required"/> so a typo can never weaken it, and startup
    /// validation rejects the value.
    /// </summary>
    public string? InvalidMode { get; init; }

    public static SealedStorageOptions FromEnvironment(Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);

        var rawMode = env("RUNNER_SEALED_STORAGE")?.Trim();
        SealedStorageMode mode;
        string? invalidMode = null;
        switch (rawMode?.ToLowerInvariant())
        {
            case null or "":
            case "auto":
                mode = SealedStorageMode.Auto;
                break;
            case "off":
                mode = SealedStorageMode.Off;
                break;
            case "required":
                mode = SealedStorageMode.Required;
                break;
            default:
                mode = SealedStorageMode.Required;
                invalidMode = rawMode;
                break;
        }

        var root = env("RUNNER_SEALED_ROOT")?.Trim();
        var minimumGigabytes = DefaultMinimumGigabytes;
        if (int.TryParse(env("RUNNER_SEALED_MIN_GB"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            minimumGigabytes = Math.Clamp(parsed, 1, 1024);
        }

        return new SealedStorageOptions
        {
            Mode = mode,
            Root = string.IsNullOrEmpty(root) ? DefaultRoot : root,
            MinimumBytes = minimumGigabytes * BytesPerGigabyte,
            InvalidMode = invalidMode,
        };
    }

    public static string FormatMode(SealedStorageMode mode)
        => mode switch
        {
            SealedStorageMode.Off => "off",
            SealedStorageMode.Auto => "auto",
            _ => "required",
        };
}

/// <summary>
/// What the runner observed about its sealed root. Swap exposure is reported, never enforced:
/// the cgroup limit and the tmpfs <c>noswap</c> option are the only controls visible in a container.
/// </summary>
public sealed record SealedStorageStatus(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("capacity_bytes")] long? CapacityBytes,
    [property: JsonPropertyName("free_bytes")] long? FreeBytes,
    [property: JsonPropertyName("memory_headroom_bytes")] long? MemoryHeadroomBytes,
    [property: JsonPropertyName("budget_bytes")] long? BudgetBytes,
    [property: JsonPropertyName("swap")] SealedStorageSwapStatus Swap);

public sealed record SealedStorageSwapStatus(
    [property: JsonPropertyName("devices_bytes")] long DevicesBytes,
    [property: JsonPropertyName("cgroup_limit")] string? CgroupLimit,
    [property: JsonPropertyName("tmpfs_noswap")] bool TmpfsNoswap);
