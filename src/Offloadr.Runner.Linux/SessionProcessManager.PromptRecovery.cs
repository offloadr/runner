using System.Buffers;
using System.Net.Http;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Offloadr.Common.V1;
using Offloadr.EditorRuntime.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Linux;

internal sealed partial class SessionProcessManager
{
    private SessionContext RequirePromptContext(SubmitPromptCommand command, CancellationToken cancellationToken)
    {
        _ = PromptRuntimeMarker.TargetFingerprint(command.Target);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(command.SessionId, out var context))
            throw new InvalidOperationException("The captured prompt runtime is absent.");
        EnsurePromptContext(command, context, cancellationToken);
        return context;
    }

    private void EnsurePromptContext(SubmitPromptCommand command, SessionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = command.Target;
        if (context.Cancellation.IsCancellationRequested || context.StopInProgress
            || !_sessions.TryGetValue(command.SessionId, out var current) || !ReferenceEquals(current, context)
            || context.Process.HasExited || !string.Equals(command.EditorRuntimeKind, _runtimeKind, StringComparison.Ordinal)
            || !Guid.TryParse(command.SessionId, out var session) || session != Guid.Parse(target.RunnerSessionId)
            || context.RuntimeIdentity.LifecycleGeneration != target.GpuGeneration
            || context.RuntimeIdentity.RuntimeEpoch != target.RuntimeEpoch
            || Guid.Parse(context.RuntimeIdentity.RuntimeInstanceId) != Guid.Parse(target.RuntimeInstanceId))
            throw new InvalidOperationException("The captured prompt runtime is stopped, replaced or mismatched.");
    }

    public async Task RecoverPromptAsync(SubmitPromptCommand command, string runnerId,
        Func<ReportPromptEvidenceRequest, CancellationToken, Task> reportEvidence, CancellationToken cancellationToken)
    {
        var context = RequirePromptContext(command, cancellationToken);
        using var runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Cancellation.Token);
        cancellationToken = runtimeCancellation.Token;
        if (command.EditorRuntimeKind == "comfyui" && !string.IsNullOrWhiteSpace(command.NativeIdentifiers?.PromptId))
        {
            await ObserveAsync(HttpMethod.Get, "api/history/" + Uri.EscapeDataString(command.NativeIdentifiers.PromptId),
                null, PromptEvidenceKind.ComfyHistory).ConfigureAwait(false);
            await ObserveAsync(HttpMethod.Get, "api/queue", null, PromptEvidenceKind.ComfyQueue).ConfigureAwait(false);
        }
        else if (command.EditorRuntimeKind == "forge-neo" && !string.IsNullOrWhiteSpace(command.NativeIdentifiers?.TaskId))
        {
            await ObserveAsync(HttpMethod.Post, "internal/progress", BuildForgeProgressRequestJson(command.NativeIdentifiers.TaskId),
                PromptEvidenceKind.ForgeProgress).ConfigureAwait(false);
        }
        // Forge queue/data consumes messages. Recovery must never open another
        // reader or resubmit queue/join. Missing retained evidence stays unknown.

        async Task ObserveAsync(HttpMethod method, string path, string? body, PromptEvidenceKind kind)
        {
            using var request = new HttpRequestMessage(method, new UriBuilder("http", _readyHost, _comfyPort, path).Uri);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            EnsurePromptContext(command, context, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;
            var payload = await ReadNativeResponseBytesAsync(response.Content, cancellationToken).ConfigureAwait(false);
            // If the child was replaced during the read, its port alone is not
            // evidence for either runtime. Discard it and retain uncertainty.
            EnsurePromptContext(command, context, cancellationToken);
            await reportEvidence(new()
            {
                RunnerId = runnerId,
                SessionId = command.SessionId,
                SubmissionId = command.SubmissionId,
                CommandId = command.CommandId,
                Target = command.Target.Clone(),
                Evidence = new()
                {
                    Kind = kind,
                    NativeIdentifiers = command.NativeIdentifiers.Clone(),
                    NativePayload = ByteString.CopyFrom(payload),
                    ObservedUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                },
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReadNativeResponseBytesAsync(HttpContent content, CancellationToken cancellationToken)
        => await TryReadNativeResponseBytesAsync(content, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The native response exceeds the retained response limit; its result is uncertain.");

    /// <summary>Returns null when the body exceeds the retained limit.</summary>
    private static async Task<byte[]?> TryReadNativeResponseBytesAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) return body.ToArray();
                if (body.Length + read > ComfyRuntimeTransportLimits.MaxResponseBodyBytes) return null;
                await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
