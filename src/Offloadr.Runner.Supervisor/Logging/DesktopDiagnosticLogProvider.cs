using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Offloadr.Runner.Core;

namespace Offloadr.Runner.Supervisor.Logging;

/// <summary>
/// Stores fixed diagnostic metadata in bounded hourly files. Old content-bearing
/// log files belong to the retired sink and are removed, without reading their bodies.
/// </summary>
public sealed class DesktopDiagnosticLogProvider : ILoggerProvider
{
    internal static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    internal const int MaximumFileBytes = 64 * 1024;
    private readonly string _directory;
    private readonly TimeProvider _clock;
    private readonly DiagnosticMetadataLoggerProvider _metadata;
    private readonly object _gate = new();
    private readonly ITimer _sweep;
    private bool _disposed;

    public DesktopDiagnosticLogProvider(string directory) : this(directory, TimeProvider.System) { }

    internal DesktopDiagnosticLogProvider(string directory, TimeProvider clock)
    {
        _directory = directory;
        _clock = clock;
        _metadata = new DiagnosticMetadataLoggerProvider(Write, clock);
        Sweep();
        _sweep = clock.CreateTimer(_ => Sweep(), null, SweepInterval, SweepInterval);
    }

    public ILogger CreateLogger(string categoryName) => _metadata.CreateLogger(categoryName);

    public void Dispose()
    {
        // Taking the gate joins any file operation already executing. Later timer
        // callbacks see _disposed before touching files, including queued callbacks.
        lock (_gate) _disposed = true;
        _sweep.Dispose();
        _metadata.Dispose();
    }

    private void Sweep()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                if (!PrepareDirectory()) return;
                Prune(_clock.GetUtcNow());
            }
            catch (Exception ex) when (IsFileFailure(ex))
            {
                // No content-bearing fallback when the optional log folder is unavailable.
            }
        }
    }

    private void Write(string metadata)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                if (!PrepareDirectory()) return;
                var now = _clock.GetUtcNow();
                Prune(now);
                var hour = now.UtcDateTime.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
                var file = Path.Combine(_directory, $"diagnostic-{hour}.log");
                if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return;
                var bytes = Encoding.UTF8.GetBytes(metadata + "\n");
                // The exclusive writer handle makes the byte check and append one
                // operation even when a startup failure overlaps the main provider.
                using var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                if (stream.Length + bytes.Length > MaximumFileBytes) return;
                stream.Seek(0, SeekOrigin.End);
                stream.Write(bytes);
            }
            catch (Exception ex) when (IsFileFailure(ex))
            {
                // Logging must never take the app down or expose content elsewhere.
            }
        }
    }

    private bool PrepareDirectory()
    {
        Directory.CreateDirectory(_directory);
        return (File.GetAttributes(_directory) & FileAttributes.ReparsePoint) == 0;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var file in Directory.EnumerateFiles(_directory))
        {
            var name = Path.GetFileName(file);
            var legacy = name == "desktop-startup.log" ||
                (name.Length == 21 && name.StartsWith("runner-", StringComparison.Ordinal) && name.EndsWith(".log", StringComparison.Ordinal) &&
                 DateTime.TryParseExact(name.AsSpan(7, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
            var captured = default(DateTimeOffset);
            var metadata = name.Length == 25 && name.StartsWith("diagnostic-", StringComparison.Ordinal) && name.EndsWith(".log", StringComparison.Ordinal) &&
                DateTimeOffset.TryParseExact(name.AsSpan(11, 10), "yyyyMMddHH", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out captured);
            if (legacy || (metadata && (captured + Retention <= now || captured > now))) File.Delete(file);
        }
    }

    private static bool IsFileFailure(Exception error)
        => error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException;
}
