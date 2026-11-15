using Offloadr.Runner.V1;

namespace Offloadr.Runner.Core;

/// <summary>
/// The wire protocol this build speaks (<see cref="RunnerProtocol.Current"/>). The control plane
/// accepts exactly one version and tells any other runner to update.
/// </summary>
public static class RunnerProtocolVersion
{
    /// <summary>Sent on every call; the control plane refuses calls without the current version.</summary>
    public const string HeaderName = "x-offloadr-runner-protocol";

    /// <summary>
    /// The agent's exit code when Offloadr refused this runner's protocol, so a supervisor can tell
    /// "fetch the image Offloadr names" from a crash.
    /// </summary>
    public const int ProtocolRejectedExitCode = 3;

    public static uint Current => (uint)RunnerProtocol.Current;

    public static string HeaderValue => Current.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A refusal that retrying cannot fix: the runner or the control plane must be updated.</summary>
    public static bool IsProtocolRejection(RegisterRunnerResponse response)
        => !response.Accepted && response.Rejection is RegistrationRejection.RunnerOutdated or RegistrationRejection.ServerOutdated;
}
