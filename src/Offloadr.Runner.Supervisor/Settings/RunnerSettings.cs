namespace Offloadr.Runner.Supervisor.Settings;

/// <summary>What the person running the desktop app configures. Immutable; replace it to change it.</summary>
public sealed record RunnerSettings
{
    public const string DefaultOffloadrApiOrigin = "https://offloadr.studio";

    /// <summary>The shortest key the agent accepts: 32 random bytes, base64-encoded.</summary>
    public const int MinimumRunnerKeyLength = 43;

    /// <summary>Origin of the Offloadr app the runner connects to.</summary>
    public string OffloadrApiOrigin { get; init; } = DefaultOffloadrApiOrigin;

    /// <summary>Personal runner key. Held in memory in plain text and protected at rest.</summary>
    public string? RunnerKey { get; init; }

    /// <summary>Host folder with model files, mounted read-only into the runner.</summary>
    public string? LocalModelsDirectory { get; init; }

    /// <summary>Path to the docker or podman executable. Empty means discover it.</summary>
    public string? ContainerEngineExecutable { get; init; }

    /// <summary>Editor template to run when the control plane does not say which one is selected.</summary>
    public string? SelectedEditorTemplate { get; init; }

    /// <summary>Start the runner when the app opens.</summary>
    public bool AutoStart { get; init; }

    public bool HasRunnerKey => !string.IsNullOrWhiteSpace(RunnerKey);

    /// <summary>True when the Offloadr address is an https origin, the only kind the runner key may be sent to.</summary>
    public bool HasHttpsOrigin
        => Uri.TryCreate(OffloadrApiOrigin, UriKind.Absolute, out var origin) &&
           origin.Scheme == Uri.UriSchemeHttps &&
           origin.PathAndQuery is "" or "/";

    /// <summary>Problems that prevent the runner from starting with these settings, in display order.</summary>
    public IReadOnlyList<string> Validate(Func<string, bool>? directoryExists = null)
    {
        directoryExists ??= Directory.Exists;
        var errors = new List<string>();
        if (!HasHttpsOrigin)
        {
            errors.Add("The Offloadr address must be an https:// origin, such as https://offloadr.studio.");
        }

        if (!HasRunnerKey)
        {
            errors.Add("Add your personal runner key. Create one under Settings in the Offloadr web app.");
        }
        else if (RunnerKey!.Trim().Any(char.IsWhiteSpace))
        {
            errors.Add("The runner key cannot contain spaces.");
        }
        else if (RunnerKey.Trim().Length < MinimumRunnerKeyLength)
        {
            // The agent refuses shorter keys at startup, so the container would only exit and restart.
            errors.Add("The runner key is too short. Copy the whole key from the Offloadr web app.");
        }

        if (!string.IsNullOrWhiteSpace(LocalModelsDirectory) && LocalModelsDirectory.IndexOf(':', HasDrive(LocalModelsDirectory) ? 2 : 0) >= 0)
        {
            // `--volume` separates its fields with colons, so a colon in the folder's own path would
            // split it (a Windows drive letter is the one colon it understands).
            errors.Add($"The models folder '{LocalModelsDirectory}' cannot contain ':'. Rename the folder or choose another one.");
        }
        else if (!string.IsNullOrWhiteSpace(LocalModelsDirectory) && !Path.IsPathFullyQualified(LocalModelsDirectory))
        {
            // Docker reads a bind-mount source that is not a full path as the name of an (empty)
            // volume, so the runner would see no models at all.
            errors.Add($"The models folder '{LocalModelsDirectory}' must be a full path, such as D:\\models.");
        }
        else if (!string.IsNullOrWhiteSpace(LocalModelsDirectory) && !directoryExists(LocalModelsDirectory))
        {
            errors.Add($"The models folder '{LocalModelsDirectory}' does not exist.");
        }

        return errors;
    }

    private static bool HasDrive(string path) => path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
}
