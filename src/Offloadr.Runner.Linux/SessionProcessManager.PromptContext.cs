using System.Buffers;
using System.Net.Http;
using Offloadr.EditorRuntime.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Linux;

internal sealed partial class SessionProcessManager
{
    private SessionContext RequirePromptContext(SubmitPromptCommand command, CancellationToken cancellationToken)
    {
        if (!PromptHandoff.HasValidIdentity(command))
            throw new ArgumentException("The prompt handoff must name its exact session and runtime.", nameof(command));
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(command.SessionId, out var context))
            throw new InvalidOperationException("The captured prompt runtime is absent.");
        EnsurePromptContext(command, context, cancellationToken);
        return context;
    }

    /// <summary>
    /// True when the handoff names this runner's current runtime of its session. Checked
    /// before the grant, so a handoff for a stopped or replaced runtime is never granted.
    /// </summary>
    public bool IsPromptRuntimeCurrent(SubmitPromptCommand command)
    {
        try
        {
            _ = RequirePromptContext(command, CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private void EnsurePromptContext(SubmitPromptCommand command, SessionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Cancellation.IsCancellationRequested || context.StopInProgress
            || !_sessions.TryGetValue(command.SessionId, out var current) || !ReferenceEquals(current, context)
            || context.Process.HasExited || !string.Equals(command.EditorRuntimeKind, _runtimeKind, StringComparison.Ordinal)
            || ExactRuntime(command.Runtime) is not { IsValid: true } target || !context.RuntimeIdentity.SameRuntime(target))
            throw new InvalidOperationException("The captured prompt runtime is stopped, replaced or mismatched.");
    }

    /// <summary>
    /// Reads ComfyUI's queue on the tracked runtime. Returns null when the runtime
    /// is not ComfyUI, is no longer current, or the queue cannot be read, so callers
    /// treat the answer as unknown.
    /// </summary>
    public async Task<ComfyPromptQueue?> ReadComfyPromptQueueAsync(
        string sessionId,
        RuntimeIdentity runtime,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(_runtimeKind, "comfyui", StringComparison.Ordinal) || !IsTrackedRuntime(sessionId, runtime))
            return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, new UriBuilder("http", _readyHost, _comfyPort, "api/queue").Uri);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var payload = await TryReadNativeResponseBytesAsync(response.Content, cancellationToken).ConfigureAwait(false);
        // A replacement child answers on the same port; its queue says nothing
        // about this runtime's prompts.
        return payload is null || !IsTrackedRuntime(sessionId, runtime) ? null : ComfyPromptQueue.TryParse(payload);
    }

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
