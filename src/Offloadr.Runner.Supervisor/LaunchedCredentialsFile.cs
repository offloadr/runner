using Offloadr.Runner.Supervisor.Hosting;
using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor;

/// <summary>
/// The <c>launched-credentials</c> note: one line per launch, its ID and then the Offloadr address and
/// runner key its container was started with, encrypted together like the key in the settings.
/// </summary>
internal static class LaunchedCredentialsFile
{
    /// <summary>The note's content for <paramref name="entries"/>; null when there is nothing to keep.</summary>
    public static string? Format(IReadOnlyCollection<KeyValuePair<string, RunnerCleanup.LaunchCredentials>> entries, ISecretProtector protector)
        => entries.Count == 0
            ? null
            : string.Join('\n', entries.Select(entry =>
                $"{entry.Key} {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(protector.Protect(entry.Value.Origin + "\n" + entry.Value.Key)))}"));

    /// <summary>
    /// Reads the note. A line that cannot be read (the protection keys changed, say) is left out, and
    /// <paramref name="unreadable"/> is told why: that launch is then asked about with the settings of now.
    /// </summary>
    public static Dictionary<string, RunnerCleanup.LaunchCredentials> Read(string? content, ISecretProtector protector, Action<Exception>? unreadable = null)
    {
        var read = new Dictionary<string, RunnerCleanup.LaunchCredentials>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (content ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            try
            {
                var both = protector.Unprotect(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]))).Split('\n', 2);
                if (both.Length == 2)
                {
                    read[parts[0]] = new RunnerCleanup.LaunchCredentials(both[0], both[1]);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                unreadable?.Invoke(ex);
            }
        }

        return read;
    }
}
