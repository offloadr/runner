using System.Text;

namespace Offloadr.Common.V1;

[Flags]
public enum ProviderGpuNormalizationSource
{
    None = 0,
    RunPod = 1 << 0,
    VastAi = 1 << 1,
    QuickPod = 1 << 2,
    Runner = 1 << 3,
    Shadeform = 1 << 4,
    TensorDock = 1 << 5,
    All = RunPod | VastAi | QuickPod | Runner | Shadeform | TensorDock,
}

public sealed record ProviderGpuNormalizationEntry(
    GpuType GpuType,
    string CanonicalDisplayName,
    string CanonicalSlug,
    int? CanonicalVramGb,
    IReadOnlyList<ProviderGpuAliasRule> AliasRules);

public sealed record ProviderGpuAliasRule(
    string Alias,
    IReadOnlyList<string> NegativeGuards,
    ProviderGpuNormalizationSource Sources = ProviderGpuNormalizationSource.All)
{
    public string NormalizedAlias => ProviderGpuNormalization.NormalizeToken(Alias);
}

public static class ProviderGpuNormalization
{
    private static readonly IReadOnlyList<ProviderGpuNormalizationEntry> Entries =
    [
        // Consumer / GeForce
        Entry(
            GpuType.NvidiaRtx5060Ti16,
            "NVIDIA RTX 5060 Ti",
            "nvidia-rtx-5060-ti-16",
            16,
            Rule("RTX 5060 TI")),
        Entry(
            GpuType.NvidiaRtx5070Ti16,
            "NVIDIA RTX 5070 Ti",
            "nvidia-rtx-5070-ti-16",
            16,
            Rule("RTX 5070 TI")),
        Entry(
            GpuType.NvidiaRtx3090,
            "NVIDIA RTX 3090",
            "nvidia-rtx-3090",
            24,
            Rule("RTX 3090", guards: ["TI"])),
        Entry(
            GpuType.NvidiaRtx3090Ti,
            "NVIDIA RTX 3090 Ti",
            "nvidia-rtx-3090-ti",
            24,
            Rule("RTX 3090 TI")),
        Entry(
            GpuType.NvidiaRtx4080,
            "NVIDIA RTX 4080",
            "nvidia-rtx-4080",
            16,
            Rule("RTX 4080", guards: ["SUPER", "4080S", "4080 S"])),
        Entry(
            GpuType.NvidiaRtx4080Super,
            "NVIDIA RTX 4080 Super",
            "nvidia-rtx-4080-super",
            16,
            Rule("RTX 4080 SUPER"),
            Rule("RTX 4080S")),
        Entry(
            GpuType.NvidiaRtx4090,
            "NVIDIA RTX 4090",
            "nvidia-rtx-4090",
            24,
            Rule("RTX 4090"),
            Rule("RTX4090", sources: ProviderGpuNormalizationSource.Shadeform)),
        Entry(
            GpuType.NvidiaRtx5080,
            "NVIDIA RTX 5080",
            "nvidia-rtx-5080",
            16,
            Rule("RTX 5080")),
        Entry(
            GpuType.NvidiaRtx5090,
            "NVIDIA RTX 5090",
            "nvidia-rtx-5090",
            32,
            Rule("RTX 5090"),
            Rule("RTX5090", sources: ProviderGpuNormalizationSource.Shadeform)),

        // Workstation / pro visualization
        Entry(
            GpuType.NvidiaRtxA4000,
            "NVIDIA RTX A4000",
            "nvidia-rtx-a4000",
            16,
            Rule("RTX A4000", guards: ["A4000 ADA", "ADA"]),
            Rule("A4000", guards: ["RTX 4000", "A4000 ADA", "ADA"])),
        Entry(
            GpuType.NvidiaRtxA5000,
            "NVIDIA RTX A5000",
            "nvidia-rtx-a5000",
            24,
            Rule("RTX A5000", guards: ["A5000 ADA", "ADA"]),
            Rule("A5000", guards: ["A5000 ADA", "ADA"])),
        Entry(
            GpuType.NvidiaRtx6000Ada,
            "NVIDIA RTX 6000 Ada",
            "nvidia-rtx-6000-ada",
            48,
            Rule("RTX6000ADA", sources: ProviderGpuNormalizationSource.Shadeform),
            Rule("RTX 6000ADA"),
            Rule("RTX 6000 ADA"),
            Rule("RTX A6000 ADA")),
        Entry(
            GpuType.NvidiaRtxPro6000Blackwell96,
            "NVIDIA RTX PRO 6000 Blackwell 96GB",
            "nvidia-rtx-pro-6000-blackwell-96",
            96,
            Rule("RTX PRO 6000 BLACKWELL", guards: ["MAX Q", "MAXQ"]),
            Rule("RTXPRO6000", guards: ["ADA", "MAX Q", "MAXQ"], sources: ProviderGpuNormalizationSource.Shadeform),
            Rule("RTX PRO 6000 WS"),
            Rule("RTX PRO 6000 S"),
            Rule("RTX PRO 6000", guards: ["ADA", "MAX Q", "MAXQ"])),
        Entry(
            GpuType.NvidiaRtxA6000,
            "NVIDIA RTX A6000",
            "nvidia-rtx-a6000",
            48,
            Rule("RTX A6000", guards: ["ADA", "RTX 6000", "PRO 6000"]),
            Rule("A6000", guards: ["ADA", "RTX 6000", "PRO 6000"])),

        // Datacenter / accelerator
        Entry(
            GpuType.NvidiaA40,
            "NVIDIA A40",
            "nvidia-a40",
            48,
            Rule("A40", guards: ["A4000"])),
        Entry(
            GpuType.NvidiaL4,
            "NVIDIA L4",
            "nvidia-l4",
            24,
            Rule("L4", guards: ["L40", "L40S"])),
        Entry(
            GpuType.NvidiaL40S,
            "NVIDIA L40S",
            "nvidia-l40s",
            48,
            Rule("L40S")),
        Entry(
            GpuType.NvidiaL40,
            "NVIDIA L40",
            "nvidia-l40",
            48,
            Rule("L40", guards: ["L40S"])),
        Entry(
            GpuType.NvidiaA100Pcie40,
            "NVIDIA A100 PCIe 40GB",
            "nvidia-a100-pcie-40",
            40),
        Entry(
            GpuType.NvidiaA100Pcie80,
            "NVIDIA A100 PCIe 80GB",
            "nvidia-a100-pcie-80",
            80,
            Rule("A100 PCIE", guards: ["SXM"]),
            Rule("A100 PCI E", guards: ["SXM"])),
        Entry(
            GpuType.NvidiaA100Sxm40,
            "NVIDIA A100 SXM 40GB",
            "nvidia-a100-sxm-40",
            40),
        Entry(
            GpuType.NvidiaA100Sxm80,
            "NVIDIA A100 SXM 80GB",
            "nvidia-a100-sxm-80",
            80,
            Rule("A100 SXM"),
            Rule("A100 SXM4")),
        Entry(
            GpuType.NvidiaH100Pcie80,
            "NVIDIA H100 PCIe 80GB",
            "nvidia-h100-pcie-80",
            80,
            Rule(
                "H100",
                guards: ["SXM", "NVL", "PCIE", "PCI E", "HBM3"],
                sources: ProviderGpuNormalizationSource.RunPod | ProviderGpuNormalizationSource.VastAi | ProviderGpuNormalizationSource.QuickPod | ProviderGpuNormalizationSource.Runner | ProviderGpuNormalizationSource.Shadeform),
            Rule("H100 PCIE", guards: ["SXM", "NVL"]),
            Rule("H100 PCI E", guards: ["SXM", "NVL"])),
        Entry(
            GpuType.NvidiaH100Sxm80,
            "NVIDIA H100 SXM 80GB",
            "nvidia-h100-sxm-80",
            80,
            Rule("H100 80GB HBM3", guards: ["NVL"]),
            Rule("H100 HBM3", guards: ["NVL"]),
            Rule("H100 SXM", guards: ["NVL"]),
            Rule("H100 SXM5", guards: ["NVL"])),
        Entry(
            GpuType.NvidiaH100Nvl,
            "NVIDIA H100 NVL",
            "nvidia-h100-nvl",
            94,
            Rule("H100 NVL")),
        Entry(
            GpuType.NvidiaH200,
            "NVIDIA H200",
            "nvidia-h200",
            141,
            Rule("H200")),
        Entry(
            GpuType.NvidiaB200,
            "NVIDIA B200",
            "nvidia-b200",
            192,
            Rule("B200")),
        Entry(GpuType.NvidiaOther, "NVIDIA Other", "nvidia-other", null),
        Entry(GpuType.AmdOther, "AMD Other", "amd-other", null),
        Entry(GpuType.Unknown, "Unknown GPU", "unknown-gpu", null),
    ];

    private static readonly IReadOnlyDictionary<GpuType, ProviderGpuNormalizationEntry> EntryByGpuType =
        Entries.ToDictionary(entry => entry.GpuType);

    public static IReadOnlyList<ProviderGpuNormalizationEntry> ListEntries()
        => Entries;

    public static bool TryGetEntry(GpuType gpuType, out ProviderGpuNormalizationEntry entry)
        => EntryByGpuType.TryGetValue(gpuType, out entry!);

    public static ProviderGpuNormalizationEntry GetRequiredEntry(GpuType gpuType)
        => EntryByGpuType.TryGetValue(gpuType, out var entry)
            ? entry
            : throw new InvalidOperationException($"No provider GPU normalization entry exists for {gpuType}.");

    public static string GetCanonicalDisplayName(GpuType gpuType)
        => TryGetEntry(gpuType, out var entry) ? entry.CanonicalDisplayName : gpuType.ToString();

    public static string GetCanonicalSlug(GpuType gpuType)
        => GetRequiredEntry(gpuType).CanonicalSlug;

    public static int? GetCanonicalVramGb(GpuType gpuType)
        => TryGetEntry(gpuType, out var entry) ? entry.CanonicalVramGb : null;

    public static long? GetCanonicalVramBytes(GpuType gpuType)
        => GetCanonicalVramGb(gpuType) is { } vramGb
            ? vramGb * 1024L * 1024L * 1024L
            : null;

    public static int? ResolveVramGb(GpuType gpuType, int? providerVramGb)
        => providerVramGb is > 0 ? providerVramGb : GetCanonicalVramGb(gpuType);

    public static bool TryNormalizeGpuName(
        string? providerGpuNameRaw,
        int? providerVramGb,
        out GpuType gpuType,
        ProviderGpuNormalizationSource source = ProviderGpuNormalizationSource.All)
    {
        var normalized = NormalizeToken(providerGpuNameRaw);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            gpuType = GpuType.Unspecified;
            return false;
        }

        if (TryNormalizeA100(normalized, providerVramGb, source, out gpuType))
        {
            return true;
        }

        foreach (var entry in Entries)
        {
            if (entry.AliasRules.Count == 0)
            {
                continue;
            }

            if (entry.AliasRules.Any(rule => IsMatch(rule, normalized, source)))
            {
                gpuType = entry.GpuType;
                return true;
            }
        }

        gpuType = GpuType.Unspecified;
        return false;
    }

    public static bool TryNormalizeGpuName(
        string? providerGpuNameRaw,
        out GpuType gpuType,
        ProviderGpuNormalizationSource source = ProviderGpuNormalizationSource.All)
        => TryNormalizeGpuName(providerGpuNameRaw, null, out gpuType, source);

    public static string NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value.Trim().ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(ch);
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    private static bool IsMatch(
        ProviderGpuAliasRule rule,
        string normalizedValue,
        ProviderGpuNormalizationSource source)
    {
        if ((rule.Sources & source) == 0)
        {
            return false;
        }

        if (!normalizedValue.Contains(rule.NormalizedAlias, StringComparison.Ordinal))
        {
            return false;
        }

        return rule.NegativeGuards.All(guard =>
            !normalizedValue.Contains(NormalizeToken(guard), StringComparison.Ordinal));
    }

    private static bool TryNormalizeA100(
        string normalizedValue,
        int? providerVramGb,
        ProviderGpuNormalizationSource source,
        out GpuType gpuType)
    {
        if (!normalizedValue.Contains("A100", StringComparison.Ordinal))
        {
            gpuType = GpuType.Unspecified;
            return false;
        }

        var isSxm = normalizedValue.Contains("A100", StringComparison.Ordinal) &&
            (normalizedValue.Contains("SXM", StringComparison.Ordinal) ||
             normalizedValue.Contains("SXM4", StringComparison.Ordinal));
        var isPcie = normalizedValue.Contains("A100", StringComparison.Ordinal) &&
            (normalizedValue.Contains("PCIE", StringComparison.Ordinal) ||
             normalizedValue.Contains("PCI E", StringComparison.Ordinal));
        var isGenericA100 = !isSxm &&
            !isPcie &&
            (source & (ProviderGpuNormalizationSource.RunPod | ProviderGpuNormalizationSource.QuickPod | ProviderGpuNormalizationSource.VastAi | ProviderGpuNormalizationSource.Runner | ProviderGpuNormalizationSource.Shadeform)) != 0;

        if (!isSxm && !isPcie && !isGenericA100)
        {
            gpuType = GpuType.Unspecified;
            return false;
        }

        var normalizedVramGb = ResolveA100VramGb(normalizedValue, providerVramGb);
        if (normalizedVramGb is null)
        {
            gpuType = GpuType.Unspecified;
            return false;
        }

        gpuType = (isSxm, normalizedVramGb.Value) switch
        {
            (true, 40) => GpuType.NvidiaA100Sxm40,
            (true, 80) => GpuType.NvidiaA100Sxm80,
            (false, 40) => GpuType.NvidiaA100Pcie40,
            (false, 80) => GpuType.NvidiaA100Pcie80,
            _ => GpuType.Unspecified,
        };

        return gpuType != GpuType.Unspecified;
    }

    private static int? ResolveA100VramGb(string normalizedValue, int? providerVramGb)
    {
        if (ContainsExplicitVram(normalizedValue, 40))
        {
            return 40;
        }

        if (ContainsExplicitVram(normalizedValue, 80))
        {
            return 80;
        }

        if (providerVramGb is >= 36 and <= 44)
        {
            return 40;
        }

        if (providerVramGb is >= 72 and <= 88)
        {
            return 80;
        }

        return null;
    }

    private static bool ContainsExplicitVram(string normalizedValue, int expectedVramGb)
        => normalizedValue.Contains($"{expectedVramGb}GB", StringComparison.Ordinal) ||
           normalizedValue.Contains($"{expectedVramGb} GB", StringComparison.Ordinal);

    private static ProviderGpuNormalizationEntry Entry(
        GpuType gpuType,
        string canonicalDisplayName,
        string canonicalSlug,
        int? canonicalVramGb,
        params ProviderGpuAliasRule[] aliasRules)
        => new(gpuType, canonicalDisplayName, canonicalSlug, canonicalVramGb, aliasRules);

    private static ProviderGpuAliasRule Rule(
        string alias,
        ProviderGpuNormalizationSource sources = ProviderGpuNormalizationSource.All,
        params string[] guards)
        => new(alias, guards, sources);
}
