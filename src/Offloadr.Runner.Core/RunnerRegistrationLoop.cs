using Microsoft.Extensions.Logging;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Core;

/// <summary>
/// The registration lane: <c>RegisterRunner</c> heartbeats, the runner's authoritative liveness signal.
/// Shared by everything that registers with Offloadr, so all of it refreshes, backs off and gives up
/// on a protocol refusal the same way.
/// </summary>
internal static class RunnerRegistrationLoop
{
    /// <summary>The heartbeat interval when Offloadr names none.</summary>
    internal static readonly TimeSpan DefaultRefresh = TimeSpan.FromMinutes(1);

    /// <summary>
    /// One heartbeat. A failed sealed-storage probe leaves <c>sealed_storage</c> unset instead of
    /// failing the heartbeat: the runner then offers no sealed sessions, but stays registered.
    /// </summary>
    internal static RegisterRunnerRequest BuildRequest(
        string runnerId,
        string instanceId,
        string version,
        RunnerResources resources,
        Func<RuntimeStatus> describeRuntime,
        IReadOnlyList<string> supportedEditorTemplates,
        ILogger logger,
        Func<RunnerSealedStorage>? describeSealedStorage = null)
    {
        ArgumentNullException.ThrowIfNull(describeRuntime);
        ArgumentNullException.ThrowIfNull(supportedEditorTemplates);
        ArgumentNullException.ThrowIfNull(logger);

        var request = new RegisterRunnerRequest
        {
            RunnerId = runnerId,
            InstanceId = instanceId,
            Runtime = describeRuntime(),
            Version = version,
            Resources = resources,
            ProtocolVersion = RunnerProtocolVersion.Current,
        };
        request.SupportedEditorTemplates.AddRange(supportedEditorTemplates);
        try
        {
            request.SealedStorage = describeSealedStorage?.Invoke();
        }
        catch (Exception ex)
        {
            // A failed probe must not stop heartbeats; the runner then offers no sealed sessions.
            logger.LogWarning("{Message}", $"Could not describe sealed storage: {ex.Message}");
        }

        return request;
    }

    /// <summary>
    /// Registers until <paramref name="shutdown"/>, at the interval Offloadr names, retrying failures
    /// and refusals with jittered backoff. <paramref name="initialStatusApplied"/> completes with the
    /// first accepted registration. A protocol refusal ends the loop instead: retrying cannot help
    /// until this build is updated, so <paramref name="onProtocolRejected"/> hears about it once.
    /// </summary>
    internal static async Task RunAsync(
        Func<RegisterRunnerRequest, CancellationToken, Task<RegisterRunnerResponse>> register,
        Func<RegisterRunnerRequest> buildRequest,
        TimeSpan minBackoff,
        TimeSpan maxBackoff,
        TaskCompletionSource initialStatusApplied,
        ILogger logger,
        CancellationToken shutdown,
        Action<RegisterRunnerResponse>? onProtocolRejected = null)
    {
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(buildRequest);
        ArgumentNullException.ThrowIfNull(initialStatusApplied);
        ArgumentNullException.ThrowIfNull(logger);

        var attempt = 0;
        var rng = new Random();
        var nextDelay = TimeSpan.Zero;

        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                if (nextDelay > TimeSpan.Zero)
                {
                    await Task.Delay(nextDelay, shutdown).ConfigureAwait(false);
                }

                var response = await register(buildRequest(), shutdown).ConfigureAwait(false);

                logger.LogInformation("{Message}", $"Status update accepted={response.Accepted} message='{response.Message}' next={response.RefreshSeconds}s");
                if (RunnerProtocolVersion.IsProtocolRejection(response))
                {
                    // Retrying cannot help: this build and Offloadr no longer share a protocol.
                    logger.LogError("{Message}",
                        $"Offloadr refused this runner's protocol {RunnerProtocolVersion.Current} (Offloadr speaks {response.ServerProtocolVersion}): {response.Message}");
                    onProtocolRejected?.Invoke(response);
                    return;
                }

                if (!response.Accepted)
                {
                    throw new RegistrationRejectedException(response.Message);
                }

                initialStatusApplied.TrySetResult();
                attempt = 0;
                nextDelay = response.RefreshSeconds > 0
                    ? TimeSpan.FromSeconds(response.RefreshSeconds)
                    : DefaultRefresh;
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                attempt++;
                nextDelay = RegistrationPolicy.ComputeBackoff(attempt, minBackoff, maxBackoff, rng.NextDouble());
                logger.LogError(ex, "{Message}", $"Runner status update failed attempt {attempt}: {ex.Message}");
                logger.LogWarning("{Message}", $"Retrying runner status update in {nextDelay.TotalSeconds:F1}s");
            }
        }
    }

    private sealed class RegistrationRejectedException(string message)
        : Exception(string.IsNullOrWhiteSpace(message) ? "Runner session status update was rejected." : message);
}
