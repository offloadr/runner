using Grpc.Net.Client;
using System.Net;

namespace Offloadr.Runner.Core;

internal sealed class TraceHttpHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RunnerLog.Info<TraceHttpHandler>($"[grpc-http] -> {request.Method} {request.RequestUri} v{request.Version}");
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        RunnerLog.Info<TraceHttpHandler>($"[grpc-http] <- {(int)response.StatusCode} v{response.Version}");
        return response;
    }
}

public static class GrpcChannelManager
{
    private const int MaxGrpcMessageSizeBytes = Offloadr.Common.V1.PromptTransportLimits.MaxRunnerMessageBytes;

    /// <summary>
    /// The control-plane address as it may be logged: scheme, host and port only. User
    /// info, path and query are left out, since deployments may put credentials there.
    /// </summary>
    public static string RedactForLog(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Authority}"
            : "(unparsable url)";

    public static GrpcChannel CreateChannel(
        string offloadrApiUrl,
        bool traceHttp)
    {
        var baseHandler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
            EnableMultipleHttp2Connections = true
        };

        if (offloadrApiUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        }

        HttpMessageHandler handler = baseHandler;
        if (traceHttp)
        {
            handler = new TraceHttpHandler(handler);
        }

        RunnerLog.Info(nameof(GrpcChannelManager), $"[grpc] Creating native HTTP/2 channel url={RedactForLog(offloadrApiUrl)}");
        return GrpcChannel.ForAddress(offloadrApiUrl, new GrpcChannelOptions
        {
            HttpHandler = handler,
            MaxReceiveMessageSize = MaxGrpcMessageSizeBytes,
            MaxSendMessageSize = MaxGrpcMessageSizeBytes
        });
    }
}
