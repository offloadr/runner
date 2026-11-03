namespace Offloadr.Runner.Core;

/// <summary>What the agent writes to its console.</summary>
public enum ConsoleLogMode
{
    /// <summary>Formatted messages, exceptions and the output of unsealed sessions' editors.</summary>
    Full,

    /// <summary>Only timestamp, severity, component and failure class (<see cref="DiagnosticMetadataLoggerProvider"/>).</summary>
    Metadata,
}

/// <summary>
/// Console logging (<c>RUNNER_CONSOLE_LOGS</c>). A runner on its owner's machine logs in full by
/// default. With <c>RUNNER_SEALED_STORAGE=required</c>, which Offloadr sets on every hosted runner,
/// the console can reach the infrastructure of whoever rents out the host, so it carries metadata only
/// and no setting widens it.
/// </summary>
public sealed class ConsoleLogOptions
{
    public ConsoleLogMode Mode { get; init; } = ConsoleLogMode.Full;

    /// <summary>
    /// The unrecognised <c>RUNNER_CONSOLE_LOGS</c> value, if any. The mode is then
    /// <see cref="ConsoleLogMode.Metadata"/> so a typo can never widen it, and startup validation
    /// rejects the value.
    /// </summary>
    public string? InvalidMode { get; init; }

    /// <summary>
    /// <c>RUNNER_CONSOLE_LOGS=full</c> was asked for while sealed storage is required. The mode stays
    /// <see cref="ConsoleLogMode.Metadata"/> and startup validation rejects the combination.
    /// </summary>
    public bool FullRefused { get; init; }

    public static ConsoleLogOptions FromEnvironment(Func<string, string?> env, SealedStorageOptions sealing)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(sealing);

        var raw = env("RUNNER_CONSOLE_LOGS")?.Trim();
        ConsoleLogMode? requested;
        string? invalidMode = null;
        switch (raw?.ToLowerInvariant())
        {
            case null or "":
                requested = null;
                break;
            case "full":
                requested = ConsoleLogMode.Full;
                break;
            case "metadata":
                requested = ConsoleLogMode.Metadata;
                break;
            default:
                requested = ConsoleLogMode.Metadata;
                invalidMode = raw;
                break;
        }

        // An unrecognised RUNNER_SEALED_STORAGE value also parses as Required, so it cannot widen the console either.
        if (sealing.Mode == SealedStorageMode.Required)
        {
            return new ConsoleLogOptions
            {
                Mode = ConsoleLogMode.Metadata,
                InvalidMode = invalidMode,
                FullRefused = requested == ConsoleLogMode.Full,
            };
        }

        return new ConsoleLogOptions
        {
            Mode = requested ?? ConsoleLogMode.Full,
            InvalidMode = invalidMode,
        };
    }
}
