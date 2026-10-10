using Grpc.Core;
using Offloadr.Common.V1;
using Offloadr.EditorRuntime.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Linux;

internal sealed partial class ComfySessionEventRelay
{
    internal ReportPromptEvidenceRequest? CapturePromptEvidence(SubmitPromptCommand command, SessionStreamEvent frame)
    {
        // Binary previews, global progress and a latest-submission guess cannot
        // establish execution facts. Only exact native terminal identities qualify.
        if (frame.EventType is not ("execution_success" or "execution_error" or "execution_interrupted")
            || frame.FrameType != SessionStreamFrameType.Text || frame.Payload.IsEmpty
            || frame.Payload.Length > ComfyRuntimeTransportLimits.MaxResponseBodyBytes
            || command.NativeIdentifiers is null || frame.PromptId != command.NativeIdentifiers.PromptId
            || string.IsNullOrEmpty(frame.PromptId) || frame.SubmissionId != command.SubmissionId
            || frame.RunnerId != _runnerId || frame.SessionId != command.SessionId || frame.EditorSid != command.EditorSid
            || frame.LifecycleGeneration != command.Target.GpuGeneration || frame.RuntimeEpoch != command.Target.RuntimeEpoch
            || !Guid.TryParse(frame.RuntimeInstanceId, out var instance) || instance != Guid.Parse(command.Target.RuntimeInstanceId)) return null;
        return new ReportPromptEvidenceRequest
        {
            RunnerId = _runnerId,
            SessionId = command.SessionId,
            SubmissionId = command.SubmissionId,
            CommandId = command.CommandId,
            Target = command.Target.Clone(),
            Evidence = new()
            {
                Kind = PromptEvidenceKind.ComfyEvent,
                NativeIdentifiers = command.NativeIdentifiers.Clone(),
                NativePayload = frame.Payload,
                ObservedUtc = frame.CreatedUtc?.Clone(),
            },
        };
    }

    internal Task ReportRetainedPromptEvidenceAsync(ReportPromptEvidenceRequest evidence)
    {
        // Capture the lifetime before scheduling: bridge Stop must not cancel this
        // lane, and a later relay disposal must not require reading its disposed CTS.
        var shutdown = _shutdown.Token;
        async Task Deliver(ReportPromptEvidenceRequest retained, CancellationToken token)
        {
            IRunnerSessionSink? sink;
            lock (_sinkLock) { sink = _sink; }
            if (sink is null) throw new RpcException(new(StatusCode.Unavailable, "The runner connection is unavailable."));
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            attempt.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await sink.ReportPromptEvidenceAsync(retained, attempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("Prompt evidence reporting timed out.");
            }
        }
        // Only the small captured evidence survives here, never the prompt body
        // or download plan. Process loss falls back to matching history/unknown.
        return Task.Run(() => ServiceClientManager.AcknowledgeRuntimeCommandWithRetryAsync(
            Deliver, evidence, evidence.CommandId, shutdown, maxAttempts: 6));
    }
}
