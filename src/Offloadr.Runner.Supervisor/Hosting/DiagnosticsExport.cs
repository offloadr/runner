using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor.Hosting;

/// <summary>Gathers a <see cref="DiagnosticsReport"/> from the running app.</summary>
public sealed class DiagnosticsExport(RunnerSupervisor supervisor, IRunnerSettingsStore settings, SupervisorPaths paths, TimeProvider clock)
{
    /// <summary>The most of the diagnostic logs kept, newest first: the provider caps them well below this.</summary>
    internal const int MaximumDiagnosticLogBytes = 4 * 1024 * 1024;

    public DiagnosticsInput Gather()
    {
        var activeKey = supervisor.ActiveRunnerKey;
        return new DiagnosticsInput(
            AppVersion(),
            RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture,
            supervisor.View,
            settings.Current,
            supervisor.LogLines,
            ReadDiagnosticLogs(paths.Logs),
            clock.GetUtcNow())
        {
            OtherRunnerKeys = activeKey is null ? [] : [activeKey],
            UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UserName = Environment.UserName,
        };
    }

    /// <summary>The report and the name to suggest for it.</summary>
    public (string FileName, string Text) Create()
    {
        var input = Gather();
        return (DiagnosticsReport.FileName(input.CreatedUtc), DiagnosticsReport.Build(input));
    }

    /// <summary>
    /// The app's diagnostic log files, oldest first. Only the provider's own metadata files are read
    /// (never a link to a file elsewhere), so nothing else in the folder can reach the report.
    /// </summary>
    internal static IReadOnlyList<DiagnosticLogFile> ReadDiagnosticLogs(string directory)
    {
        string[] names;
        try
        {
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                return [];
            }

            names = Directory.EnumerateFiles(directory, "diagnostic-*.log")
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(DesktopDiagnosticLogName)
                .OrderByDescending(name => name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [new DiagnosticLogFile("logs", null, ex.GetType().Name)];
        }

        var files = new List<DiagnosticLogFile>();
        var budget = MaximumDiagnosticLogBytes;
        foreach (var name in names)
        {
            if (budget <= 0)
            {
                files.Add(new DiagnosticLogFile(name, null, "left out: the report already holds the newer logs"));
                continue;
            }

            var path = Path.Combine(directory, name);
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[(int)Math.Min(stream.Length, budget)];
                var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                budget -= read;
                files.Add(new DiagnosticLogFile(name, Encoding.UTF8.GetString(buffer, 0, read)));
            }
            catch (FileNotFoundException)
            {
                // Removed by the hourly sweep since the listing.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                files.Add(new DiagnosticLogFile(name, null, ex.GetType().Name));
            }
        }

        files.Reverse();
        return files;
    }

    private static bool DesktopDiagnosticLogName(string name)
        => name.Length == 25 && name.EndsWith(".log", StringComparison.Ordinal) && !name.AsSpan(11, 10).ContainsAnyExceptInRange('0', '9');

    private static string AppVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(DiagnosticsExport).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}
