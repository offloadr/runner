using Offloadr.Runner.V1;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Offloadr.Runner.Tests;

public class RunnerSecretClientInterceptorTests
{
    [Test]
    public async Task AsyncUnaryCall_AddsRunnerSecretHeader()
    {
        var interceptor = new RunnerSecretClientInterceptor("super-secret-token");
        Metadata? observedHeaders = null;

        var method = new Method<RegisterRunnerRequest, RegisterRunnerResponse>(
            MethodType.Unary,
            "offloadr.runner.v1.RunnerControlService",
            "RegisterRunner",
            Marshallers.Create(_ => Array.Empty<byte>(), _ => new RegisterRunnerRequest()),
            Marshallers.Create(_ => Array.Empty<byte>(), _ => new RegisterRunnerResponse()));

        var context = new ClientInterceptorContext<RegisterRunnerRequest, RegisterRunnerResponse>(
            method,
            "https://api.offloadr.app",
            new CallOptions());

        var call = interceptor.AsyncUnaryCall(
            new RegisterRunnerRequest(),
            context,
            (_, capturedContext) =>
            {
                observedHeaders = capturedContext.Options.Headers;
                return new AsyncUnaryCall<RegisterRunnerResponse>(
                    Task.FromResult(new RegisterRunnerResponse()),
                    Task.FromResult(new Metadata()),
                    () => Status.DefaultSuccess,
                    () => new Metadata(),
                    () => { });
            });

        await call.ResponseAsync;

        Assert.That(observedHeaders, Is.Not.Null);
        Assert.That(observedHeaders!.Any(h => h.Key == "x-runner-secret" && h.Value == "super-secret-token"), Is.True);
    }
}
