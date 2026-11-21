using System.Text;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Linux;

internal static partial class ServiceClientManager
{
    /// <summary>Longest failure message a heartbeat carries, in UTF-8 bytes.</summary>
    internal const int MaxRuntimeStatusMessageBytes = 512;

    /// <summary>How the last startup this runner owned ended, for the runtime it started.</summary>
    internal sealed record RecordedStartupOutcome(string SessionId, RuntimeIdentity Runtime, StartupOutcome Outcome, Guid? Process);

    /// <summary>
    /// What the heartbeat reports about the runner's one logical session: the session, the
    /// runtime it runs and that runtime's phase. It reads the state the command handlers
    /// already own, so the report never runs ahead of them. READY needs a recorded ready
    /// outcome of the process now running, recorded by the startup that owns it before it
    /// releases its slot.
    /// </summary>
    internal static RuntimeStatus DescribeRuntimeStatus(
        LogicalSessionState logicalSessionState,
        RunnerCommandWorkState workState,
        Func<string, bool> isStopping,
        Func<string, RuntimeIdentity, Guid?> trackedProcess)
    {
        // The process check runs outside the work state's lock. A startup that begins
        // meanwhile could start a fresh process for the same runtime, so a snapshot that a
        // startup overtook is read again; READY is never built from a stale one.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var status = DescribeRuntimeStatusOnce(logicalSessionState, workState, isStopping, trackedProcess, out var version);
            if (version == workState.StartupVersion) return status;
        }
        return new RuntimeStatus { SessionId = logicalSessionState.GetActiveSessionId(), Phase = RuntimePhase.None };
    }

    private static RuntimeStatus DescribeRuntimeStatusOnce(
        LogicalSessionState logicalSessionState,
        RunnerCommandWorkState workState,
        Func<string, bool> isStopping,
        Func<string, RuntimeIdentity, Guid?> trackedProcess,
        out long version)
    {
        version = workState.StartupVersion;
        var sessionId = logicalSessionState.GetActiveSessionId();
        var status = new RuntimeStatus { SessionId = sessionId, Phase = RuntimePhase.None };
        if (sessionId.Length == 0) return status;

        var hasRuntime = logicalSessionState.TryGetRuntime(sessionId, out var runtime, out _);
        if (isStopping(sessionId))
        {
            status.Phase = RuntimePhase.Stopping;
            if (hasRuntime) SetRuntime(status, runtime);
            return status;
        }

        var (startup, recorded, read) = workState.ReadStartupState(sessionId, hasRuntime ? runtime : null);
        version = read;
        if (startup is not null && string.Equals(startup.SessionId, sessionId, StringComparison.Ordinal) &&
            startup.RuntimeIdentity.IsValid)
        {
            status.Phase = RuntimePhase.Starting;
            SetRuntime(status, startup.RuntimeIdentity);
            return status;
        }

        if (!hasRuntime || recorded is null)
        {
            return status;
        }

        if (recorded.Outcome.Ready)
        {
            // READY belongs to the process that started ready: one gone or stopping is no
            // longer ready, and a relaunch of the same runtime is a new, unready process.
            if (recorded.Process is not { } process || trackedProcess(sessionId, runtime) != process) return status;
            status.Phase = RuntimePhase.Ready;
            status.Sealing = recorded.Outcome.Sealing?.Clone();
        }
        else
        {
            status.Phase = RuntimePhase.Failed;
            status.Message = Truncate(recorded.Outcome.Message, MaxRuntimeStatusMessageBytes);
        }

        SetRuntime(status, runtime);
        return status;
    }

    private static void SetRuntime(RuntimeStatus status, RuntimeIdentity runtime)
    {
        var normalized = runtime.Normalized();
        status.LifecycleGeneration = normalized.LifecycleGeneration;
        status.RuntimeEpoch = normalized.RuntimeEpoch;
        status.RuntimeInstanceId = normalized.RuntimeInstanceId;
    }

    internal static string Truncate(string? message, int maxBytes)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        if (Encoding.UTF8.GetByteCount(message) <= maxBytes) return message;
        var length = 0;
        var bytes = 0;
        foreach (var rune in message.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes) break;
            bytes += rune.Utf8SequenceLength;
            length += rune.Utf16SequenceLength;
        }
        return message[..length];
    }
}
