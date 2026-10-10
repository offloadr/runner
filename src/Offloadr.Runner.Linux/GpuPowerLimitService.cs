using Offloadr.Common.V1;
using Offloadr.Runner.V1;
using System.Diagnostics;
using System.Globalization;

namespace Offloadr.Runner.Linux;

public sealed class GpuPowerLimitService
{
    private readonly RuntimeTelemetryService _telemetryService;
    private readonly IGpuPowerLimitCommandRunner _commandRunner;

    public GpuPowerLimitService(RuntimeTelemetryService telemetryService, IGpuPowerLimitCommandRunner? commandRunner = null)
    {
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        _commandRunner = commandRunner ?? new NvidiaSmiGpuPowerLimitCommandRunner();
    }

    public async Task<GpuPowerLimitApplyResult> ApplyAsync(
        SetGpuPowerLimitCommand command,
        Func<string> getActiveSessionId,
        Func<ulong> getActiveGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var commandSessionId = command.SessionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(command.CommandId))
        {
            return GpuPowerLimitApplyResult.Failure("GPU power command is missing command_id");
        }

        if (string.IsNullOrWhiteSpace(commandSessionId))
        {
            return GpuPowerLimitApplyResult.Failure("GPU power command is missing session_id");
        }

        if (!string.Equals(getActiveSessionId()?.Trim(), commandSessionId, StringComparison.Ordinal))
        {
            return GpuPowerLimitApplyResult.Failure("GPU session is not active on this runner");
        }

        if (command.Target is null || command.Target.Generation == 0 || command.Target.Generation != getActiveGeneration())
            return GpuPowerLimitApplyResult.Failure("GPU lifecycle generation changed");
        var before = _telemetryService.TryCaptureSnapshot();
        var targetWatts = ResolveTargetWatts(command, before, out var validationMessage);
        if (targetWatts is null)
        {
            return GpuPowerLimitApplyResult.Failure(validationMessage);
        }

        if (getActiveSessionId()?.Trim() != commandSessionId || command.Target.Generation != getActiveGeneration())
            return GpuPowerLimitApplyResult.Failure("GPU session changed while refreshing physical limits");
        cancellationToken.ThrowIfCancellationRequested();
        var commandResult = await _commandRunner
            .SetPowerLimitAsync(targetWatts.Value, cancellationToken)
            .ConfigureAwait(false);
        if (!commandResult.Success)
        {
            return GpuPowerLimitApplyResult.Failure(
                BuildFailureMessage(commandResult),
                requestedWatts: targetWatts.Value,
                telemetry: before,
                outcome: commandResult.MayHaveApplied ? GpuPowerOutcome.Unknown : GpuPowerOutcome.Rejected);
        }

        var after = _telemetryService.TryCaptureSnapshot();
        return new GpuPowerLimitApplyResult(
            Outcome: GpuPowerOutcome.Applied,
            Message: "GPU power limit applied",
            RequestedWatts: targetWatts.Value,
            AppliedWatts: after?.PowerLimitWatts ?? targetWatts.Value,
            Telemetry: after);
    }

    private static double? ResolveTargetWatts(
        SetGpuPowerLimitCommand command,
        RunnerRuntimeTelemetrySnapshot? telemetry,
        out string validationMessage)
    {
        validationMessage = string.Empty;

        if (telemetry?.PowerMinLimitWatts is not { } minimumBound || !double.IsFinite(minimumBound) || minimumBound <= 0
            || telemetry.PowerMaxLimitWatts is not { } maximumBound || !double.IsFinite(maximumBound) || maximumBound < minimumBound)
        {
            validationMessage = "Current supported GPU power bounds are unavailable";
            return null;
        }
        var targetWatts = command.TargetWatts;

        if (!double.IsFinite(targetWatts) || targetWatts <= 0)
        {
            validationMessage = "target_watts must be greater than zero";
            return null;
        }

        const double tolerance = 0.001d;
        if (telemetry?.PowerMinLimitWatts is { } minimum &&
            double.IsFinite(minimum) &&
            minimum > 0 &&
            targetWatts + tolerance < minimum)
        {
            validationMessage = $"target_watts is below the supported minimum ({minimum.ToString("0.###", CultureInfo.InvariantCulture)} W)";
            return null;
        }

        if (telemetry?.PowerMaxLimitWatts is { } maximumLimit &&
            double.IsFinite(maximumLimit) &&
            maximumLimit > 0 &&
            targetWatts - tolerance > maximumLimit)
        {
            validationMessage = $"target_watts is above the supported maximum ({maximumLimit.ToString("0.###", CultureInfo.InvariantCulture)} W)";
            return null;
        }

        return targetWatts;
    }

    private static string BuildFailureMessage(GpuPowerLimitCommandResult result)
    {
        var detail = result.StandardError;
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = result.StandardOutput;
        }

        detail = detail?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(detail)
            ? $"nvidia-smi failed with exit code {result.ExitCode}"
            : $"nvidia-smi failed with exit code {result.ExitCode}: {detail}";
    }
}

public interface IGpuPowerLimitCommandRunner
{
    Task<GpuPowerLimitCommandResult> SetPowerLimitAsync(double targetWatts, CancellationToken cancellationToken);
}

public sealed class NvidiaSmiGpuPowerLimitCommandRunner : IGpuPowerLimitCommandRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _executablePath;

    public NvidiaSmiGpuPowerLimitCommandRunner() : this("nvidia-smi") { }
    internal NvidiaSmiGpuPowerLimitCommandRunner(string executablePath) => _executablePath = executablePath;

    public async Task<GpuPowerLimitCommandResult> SetPowerLimitAsync(double targetWatts, CancellationToken cancellationToken)
    {
        var formattedWatts = targetWatts.ToString("0.###", CultureInfo.InvariantCulture);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                Arguments = $"-i 0 -pl {formattedWatts}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        var started = false;
        try
        {
            if (!process.Start())
            {
                return new GpuPowerLimitCommandResult(false, string.Empty, "Failed to start nvidia-smi", -1);
            }

            started = true;
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Timeout);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new GpuPowerLimitCommandResult(
                process.ExitCode == 0,
                stdout,
                stderr,
                // A completed single-device command has an authoritative exit
                // result. Only interrupted execution below remains ambiguous.
                process.ExitCode, MayHaveApplied: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (started) await TerminateAndWaitAsync(process).ConfigureAwait(false);
            return new GpuPowerLimitCommandResult(false, string.Empty, "nvidia-smi timed out", -1, MayHaveApplied: started);
        }
        catch (Exception ex)
        {
            if (started) await TerminateAndWaitAsync(process).ConfigureAwait(false);
            return new GpuPowerLimitCommandResult(false, string.Empty, ex.Message, -1, MayHaveApplied: started);
        }
    }

    private static async Task TerminateAndWaitAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex)
        {
            RunnerLog.Error(nameof(NvidiaSmiGpuPowerLimitCommandRunner), ex, "Waiting for the uncertain power process to exit");
        }
        // Retain the physical-execution fence until this process exits. Releasing
        // it on timeout alone could let an old command affect a replacement session.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

}

public readonly record struct GpuPowerLimitApplyResult(
    GpuPowerOutcome Outcome,
    string Message,
    double RequestedWatts,
    double AppliedWatts,
    RunnerRuntimeTelemetrySnapshot? Telemetry)
{
    public static GpuPowerLimitApplyResult Failure(
        string message,
        double requestedWatts = 0,
        RunnerRuntimeTelemetrySnapshot? telemetry = null,
        GpuPowerOutcome outcome = GpuPowerOutcome.Rejected)
        => new(outcome, message, requestedWatts, 0, telemetry);
}

public readonly record struct GpuPowerLimitCommandResult(
    bool Success,
    string StandardOutput,
    string StandardError,
    int ExitCode,
    bool MayHaveApplied = false);
