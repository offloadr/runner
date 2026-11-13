namespace Offloadr.Runner.Core;

/// <summary>
/// Whether the runner fetches models it does not have (<c>RUNNER_MODEL_DOWNLOADS</c>). Off unless asked for:
/// a runner on its owner's machine would spend their disk and bandwidth, so it uses only the models in
/// its local models folder until they turn downloads on. Offloadr launches hosted runners with them on.
/// </summary>
public sealed class ModelDownloadOptions
{
    public bool Enabled { get; init; }

    /// <summary>
    /// The unrecognised <c>RUNNER_MODEL_DOWNLOADS</c> value, if any. Downloads then stay off, so a typo
    /// can never turn them on, and startup validation rejects the value.
    /// </summary>
    public string? InvalidValue { get; init; }

    public static ModelDownloadOptions FromEnvironment(Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);

        var raw = env("RUNNER_MODEL_DOWNLOADS")?.Trim();
        return raw?.ToLowerInvariant() switch
        {
            null or "" or "off" => new ModelDownloadOptions { Enabled = false },
            "on" => new ModelDownloadOptions { Enabled = true },
            _ => new ModelDownloadOptions { Enabled = false, InvalidValue = raw },
        };
    }
}
