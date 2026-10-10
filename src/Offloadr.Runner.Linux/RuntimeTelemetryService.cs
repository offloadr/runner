using Offloadr.Runner.V1;
using Google.Protobuf.WellKnownTypes;
using System.Diagnostics;
using System.Globalization;

namespace Offloadr.Runner.Linux;

public interface IRuntimeTelemetryProbe
{
    string CaptureRuntimeTelemetryQuery();

    string? ReadMemInfo();

    bool TryGetDiskSpace(string path, out RuntimeDiskSpaceSnapshot snapshot);
}

public sealed class RuntimeTelemetryService
{
    private const string DefaultModelDiskPath = "/models";

    private readonly IRuntimeTelemetryProbe _probe;
    private readonly string _modelDiskPath;

    public RuntimeTelemetryService(string? modelDiskPath = null)
        : this(new NvidiaRuntimeTelemetryProbe(), modelDiskPath)
    {
    }

    public RuntimeTelemetryService(IRuntimeTelemetryProbe probe, string? modelDiskPath = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _modelDiskPath = string.IsNullOrWhiteSpace(modelDiskPath)
            ? DefaultModelDiskPath
            : modelDiskPath.Trim();
    }

    public RunnerRuntimeTelemetrySnapshot? TryCaptureSnapshot()
    {
        var sampledUtc = DateTime.UtcNow;
        var snapshot = ParseQueryOutput(_probe.CaptureRuntimeTelemetryQuery(), sampledUtc);
        var hasMetric = snapshot is not null;
        snapshot ??= CreateEmptySnapshot(sampledUtc);

        if (TryParseMemInfo(_probe.ReadMemInfo(), out var ramTotalBytes, out var ramAvailableBytes))
        {
            snapshot.SystemRamTotalBytes = ramTotalBytes;
            snapshot.SystemRamUsedBytes = ramTotalBytes - Math.Min(ramTotalBytes, ramAvailableBytes);
            hasMetric = true;
        }

        if (_probe.TryGetDiskSpace(_modelDiskPath, out var disk))
        {
            snapshot.ModelDiskAvailableBytes = disk.AvailableBytes;
            snapshot.ModelDiskTotalBytes = disk.TotalBytes;
            snapshot.ModelDiskPath = disk.Path;
            hasMetric = true;
        }

        return hasMetric ? snapshot : null;
    }

    public static RunnerRuntimeTelemetrySnapshot? ParseQueryOutput(string? output, DateTime sampledUtc)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var lines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 7)
            {
                continue;
            }

            var utilizationPercent = ParseOptionalUInt32(parts[0]);
            var vramUsedMiB = ParseOptionalUInt64(parts[1]);
            var vramTotalMiB = ParseOptionalUInt64(parts[2]);
            var temperatureCelsius = ParseOptionalUInt32(parts[3]);
            var fanPercent = ParseOptionalUInt32(parts[4]);
            var powerUsageWatts = ParseOptionalDouble(parts[5]);
            var powerLimitWatts = ParseOptionalDouble(parts[6]);
            var powerMinLimitWatts = parts.Length > 7 ? ParseOptionalDouble(parts[7]) : null;
            var powerDefaultLimitWatts = parts.Length > 8 ? ParseOptionalDouble(parts[8]) : null;
            var powerMaxLimitWatts = parts.Length > 9 ? ParseOptionalDouble(parts[9]) : null;

            if (utilizationPercent is null &&
                vramUsedMiB is null &&
                vramTotalMiB is null &&
                temperatureCelsius is null &&
                fanPercent is null &&
                powerUsageWatts is null &&
                powerLimitWatts is null &&
                powerMinLimitWatts is null &&
                powerDefaultLimitWatts is null &&
                powerMaxLimitWatts is null)
            {
                continue;
            }

            return new RunnerRuntimeTelemetrySnapshot
            {
                UtilizationPercent = utilizationPercent,
                VramUsedBytes = vramUsedMiB is null ? null : MiBToBytes(vramUsedMiB.Value),
                VramTotalBytes = vramTotalMiB is null ? null : MiBToBytes(vramTotalMiB.Value),
                TemperatureCelsius = temperatureCelsius,
                FanPercent = fanPercent,
                PowerUsageWatts = powerUsageWatts,
                PowerLimitWatts = powerLimitWatts,
                PowerMinLimitWatts = powerMinLimitWatts,
                PowerDefaultLimitWatts = powerDefaultLimitWatts,
                PowerMaxLimitWatts = powerMaxLimitWatts,
                SampledUtc = Timestamp.FromDateTime(sampledUtc.ToUniversalTime())
            };
        }

        return null;
    }

    public static bool TryParseMemInfo(string? memInfo, out ulong totalBytes, out ulong availableBytes)
    {
        totalBytes = 0;
        availableBytes = 0;
        if (string.IsNullOrWhiteSpace(memInfo))
        {
            return false;
        }

        var sawMemTotal = false;
        var sawMemAvailable = false;
        foreach (var line in memInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                totalBytes = ParseMemInfoKilobytes(line);
                sawMemTotal = true;
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                availableBytes = ParseMemInfoKilobytes(line);
                sawMemAvailable = true;
            }
        }

        return sawMemTotal && sawMemAvailable && totalBytes > 0;
    }

    private static RunnerRuntimeTelemetrySnapshot CreateEmptySnapshot(DateTime sampledUtc)
        => new()
        {
            SampledUtc = Timestamp.FromDateTime(sampledUtc.ToUniversalTime())
        };

    private static ulong ParseMemInfoKilobytes(string line)
    {
        var value = line
            .Split(':', 2, StringSplitOptions.TrimEntries)
            .ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var token = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (!ulong.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kib))
        {
            return 0;
        }

        checked
        {
            return kib * 1024UL;
        }
    }

    private static uint? ParseOptionalUInt32(string raw)
    {
        if (!TryNormalizeNumericValue(raw, out var normalized))
        {
            return null;
        }

        return uint.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static ulong? ParseOptionalUInt64(string raw)
    {
        if (!TryNormalizeNumericValue(raw, out var normalized))
        {
            return null;
        }

        return ulong.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? ParseOptionalDouble(string raw)
    {
        if (!TryNormalizeNumericValue(raw, out var normalized))
        {
            return null;
        }

        return double.TryParse(normalized, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool TryNormalizeNumericValue(string raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        normalized = raw.Trim();
        if (normalized.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("[N/A]", StringComparison.OrdinalIgnoreCase))
        {
            normalized = string.Empty;
            return false;
        }

        return true;
    }

    private static ulong MiBToBytes(ulong value)
    {
        checked
        {
            return value * 1024UL * 1024UL;
        }
    }
}

public sealed class NvidiaRuntimeTelemetryProbe : IRuntimeTelemetryProbe
{
    public string CaptureRuntimeTelemetryQuery()
    {
        return RunProcessCapture(
            "nvidia-smi",
            "--query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu,fan.speed,power.draw,power.limit,power.min_limit,power.default_limit,power.max_limit --format=csv,noheader,nounits",
            timeoutMs: 4000);
    }

    public string? ReadMemInfo()
    {
        try
        {
            return File.ReadAllText("/proc/meminfo");
        }
        catch
        {
            return null;
        }
    }

    public bool TryGetDiskSpace(string path, out RuntimeDiskSpaceSnapshot snapshot)
    {
        snapshot = default;
        try
        {
            var normalizedPath = NormalizePath(path);
            var drive = ResolveDriveForPath(normalizedPath);
            if (drive is null || !drive.IsReady || drive.TotalSize < 0 || drive.AvailableFreeSpace < 0)
            {
                return false;
            }

            snapshot = new RuntimeDiskSpaceSnapshot(
                normalizedPath,
                (ulong)drive.AvailableFreeSpace,
                (ulong)drive.TotalSize);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DriveInfo? ResolveDriveForPath(string normalizedPath)
    {
        DriveInfo? best = null;
        var bestRootLength = -1;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
            {
                continue;
            }

            var root = NormalizeRoot(drive.RootDirectory.FullName);
            if (!PathIsOnRoot(normalizedPath, root) || root.Length <= bestRootLength)
            {
                continue;
            }

            best = drive;
            bestRootLength = root.Length;
        }

        if (best is not null)
        {
            return best;
        }

        var fallbackRoot = Path.GetPathRoot(normalizedPath);
        return string.IsNullOrWhiteSpace(fallbackRoot)
            ? null
            : new DriveInfo(fallbackRoot);
    }

    private static string NormalizePath(string path)
    {
        var candidate = string.IsNullOrWhiteSpace(path) ? "/models" : path.Trim();
        var normalized = Path.GetFullPath(candidate);
        return normalized == Path.DirectorySeparatorChar.ToString()
            ? normalized
            : normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeRoot(string root)
    {
        var normalized = Path.GetFullPath(root);
        if (normalized == Path.DirectorySeparatorChar.ToString())
        {
            return normalized;
        }

        return normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool PathIsOnRoot(string path, string root)
    {
        if (root == Path.DirectorySeparatorChar.ToString())
        {
            return path.StartsWith(root, StringComparison.Ordinal);
        }

        return string.Equals(path, root, StringComparison.Ordinal) ||
            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
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
                    // Best effort cleanup.
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

public readonly record struct RuntimeDiskSpaceSnapshot(
    string Path,
    ulong AvailableBytes,
    ulong TotalBytes);
