using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Offloadr.Runner.Core;

internal sealed class RunnerSecretClientInterceptor(string runnerSecret) : Interceptor
{
    private const string RunnerSecretHeader = "x-runner-secret";
    private readonly string _runnerSecret = runnerSecret?.Trim() ?? string.Empty;

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        return continuation(request, BuildContext(context));
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        return continuation(request, BuildContext(context));
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        return continuation(BuildContext(context));
    }

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        return continuation(BuildContext(context));
    }

    private ClientInterceptorContext<TRequest, TResponse> BuildContext<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        return new ClientInterceptorContext<TRequest, TResponse>(
            context.Method,
            context.Host,
            WithRunnerSecret(context.Options));
    }

    private CallOptions WithRunnerSecret(CallOptions options)
    {
        var headers = new Metadata();
        if (options.Headers is not null)
        {
            foreach (var entry in options.Headers)
            {
                headers.Add(entry);
            }
        }

        var hasSecret = headers.Any(h => string.Equals(h.Key, RunnerSecretHeader, StringComparison.OrdinalIgnoreCase));
        if (!hasSecret)
        {
            headers.Add(RunnerSecretHeader, _runnerSecret);
        }

        return options.WithHeaders(headers);
    }
}
