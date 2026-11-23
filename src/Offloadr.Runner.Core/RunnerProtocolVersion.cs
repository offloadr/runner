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

    /// <summary>
    /// The agent's exit code when Offloadr fenced this container for good
    /// (<see cref="RegistrationRejection.ExecutorFenced"/>): only a new container can register.
    /// </summary>
    public const int ExecutorFencedExitCode = 4;

    public static uint Current => (uint)RunnerProtocol.Current;

    public static string HeaderValue => Current.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A refusal that retrying cannot fix: the runner or the control plane must be updated.</summary>
    public static bool IsProtocolRejection(RegisterRunnerResponse response)
        => !response.Accepted && response.Rejection is RegistrationRejection.RunnerOutdated or RegistrationRejection.ServerOutdated;

    /// <summary>
    /// A refusal no retry with this identity can turn into an acceptance: an outdated protocol, a
    /// superseded launcher or a fenced executor.
    /// </summary>
    public static bool IsFinalRejection(RegisterRunnerResponse response)
        => IsProtocolRejection(response)
            || (!response.Accepted && response.Rejection is RegistrationRejection.LauncherSuperseded or RegistrationRejection.ExecutorFenced);
}
