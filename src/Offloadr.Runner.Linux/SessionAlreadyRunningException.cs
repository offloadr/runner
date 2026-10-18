namespace Offloadr.Runner.Linux;

/// <summary>
/// A start for a runtime that is already running, but not as that start asks: it is refused, and
/// the running runtime is left alone rather than cleaned up as a failed start.
/// </summary>
internal sealed class SessionAlreadyRunningException(string message) : InvalidOperationException(message);
