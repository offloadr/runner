using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Offloadr.Runner.Core;

/// <summary>
/// Local status a supervisor reads to decide when restarting the runner is safe.
/// It is published inside the container only and never sent to the control plane.
/// </summary>
internal sealed record RunnerStatusSnapshot(
    [property: JsonPropertyName("runner_id")] string RunnerId,
    [property: JsonPropertyName("instance_id")] string InstanceId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("supported_editor_templates")] IReadOnlyList<string> SupportedEditorTemplates,
    [property: JsonPropertyName("active_session_id")] string ActiveSessionId,
    [property: JsonPropertyName("updated_utc")] DateTimeOffset UpdatedUtc);

/// <summary>
/// Periodically writes <see cref="RunnerStatusSnapshot"/> to a file with an atomic replace.
/// The file is rewritten when the active session changes and at least every <c>refreshInterval</c>,
/// so a reader can treat an old <c>updated_utc</c> as a stalled agent.
/// </summary>
internal sealed class RunnerStatusFileWriter(
    string path,
    string runnerId,
    string instanceId,
    string version,
    IReadOnlyList<string> supportedEditorTemplates,
    Func<string> activeSessionId,
    TimeSpan? pollInterval = null,
    TimeSpan? refreshInterval = null,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        // Local file read by people and tools; no HTML context to escape for.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
    private readonly TimeSpan _refreshInterval = refreshInterval ?? TimeSpan.FromSeconds(15);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string Path { get; } = path;

    /// <summary>Path from <c>RUNNER_STATUS_FILE</c>; null disables the status file.</summary>
    public static string? ResolvePath(Func<string, string?> getEnvironmentVariable)
    {
        var configured = getEnvironmentVariable("RUNNER_STATUS_FILE")?.Trim();
        return string.IsNullOrEmpty(configured) ? null : configured;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        string? lastSessionId = null;
        var lastWrite = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(_pollInterval, _time);
        do
        {
            var sessionId = activeSessionId();
            var now = _time.GetUtcNow();
            if (!string.Equals(sessionId, lastSessionId, StringComparison.Ordinal) || now - lastWrite >= _refreshInterval)
            {
                try
                {
                    Write(sessionId, now);
                    lastSessionId = sessionId;
                    lastWrite = now;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort: the status file is advisory and must never affect the runner itself.
                    RunnerLog.Warning($"Could not write runner status file '{Path}': {ex.Message}");
                }
            }
        }
        while (await WaitForNextTickAsync(timer, cancellationToken).ConfigureAwait(false));
    }

    internal void Write(string sessionId, DateTimeOffset now)
    {
        var snapshot = new RunnerStatusSnapshot(runnerId, instanceId, version, supportedEditorTemplates, sessionId, now);
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, SerializerOptions));
        File.Move(temporary, Path, overwrite: true);
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
