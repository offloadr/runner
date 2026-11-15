using Grpc.Core;
using Grpc.Core.Interceptors;
using Offloadr.Runner.Core;
using Offloadr.Runner.Linux;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public class RunnerProtocolHandshakeTests
{
    [Test]
    public void StatusAndCommandRequestsCarryThisBuildsProtocol()
    {
        var request = ServiceClientManager.BuildStatusUpdateRequest(
            Guid.NewGuid().ToString("n"), "process-1", "1.0.0", new RunnerResources(), () => string.Empty, ["comfyui-latest"]);

        Assert.That(request.ProtocolVersion, Is.EqualTo((uint)RunnerProtocol.Current));
        Assert.That(RunnerProtocolVersion.Current, Is.GreaterThan(0u));
    }

    [Test]
    public async Task EveryCallCarriesTheProtocolHeader()
    {
        var interceptor = new RunnerSecretClientInterceptor("super-secret-token");
        Metadata? observed = null;
        var method = new Method<ReportSessionEventsRequest, ReportSessionEventsResponse>(
            MethodType.Unary, "offloadr.runner.v1.RunnerControlService", "ReportSessionEvents",
            Marshallers.Create(_ => Array.Empty<byte>(), _ => new ReportSessionEventsRequest()),
            Marshallers.Create(_ => Array.Empty<byte>(), _ => new ReportSessionEventsResponse()));
        var call = interceptor.AsyncUnaryCall(
            new ReportSessionEventsRequest(),
            new ClientInterceptorContext<ReportSessionEventsRequest, ReportSessionEventsResponse>(method, "https://control-plane.example", new CallOptions()),
            (_, context) =>
            {
                observed = context.Options.Headers;
                return new AsyncUnaryCall<ReportSessionEventsResponse>(Task.FromResult(new ReportSessionEventsResponse()),
                    Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
            });
        await call.ResponseAsync;

        Assert.That(observed!.Single(entry => entry.Key == RunnerProtocolVersion.HeaderName).Value,
            Is.EqualTo(RunnerProtocolVersion.HeaderValue));
    }

    [TestCase(RegistrationRejection.RunnerOutdated)]
    [TestCase(RegistrationRejection.ServerOutdated)]
    public async Task AProtocolRejectionStopsTheStatusLoopInsteadOfRetrying(RegistrationRejection rejection)
    {
        var sink = new RegistrationSink(_ => new RegisterRunnerResponse
        {
            Accepted = false,
            Message = "Update the runner.",
            Rejection = rejection,
            ServerProtocolVersion = RunnerProtocolVersion.Current + 1,
        });
        RegisterRunnerResponse? reported = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await ServiceClientManager.RunRunnerStatusLoopAsync(
            sink, timeout.Token, Guid.NewGuid().ToString("n"), "process-1", "1.0.0", () => string.Empty,
            new RunnerResources(), ["comfyui-latest"], TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5),
            new TaskCompletionSource(), onProtocolRejected: response => reported = response);

        Assert.That(timeout.IsCancellationRequested, Is.False, "the loop returns by itself");
        Assert.That(reported?.Rejection, Is.EqualTo(rejection));
        Assert.That(sink.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task OtherRefusalsAreStillRetried()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sink = new RegistrationSink(calls =>
        {
            if (calls == 3) stop.Cancel();
            return new RegisterRunnerResponse { Accepted = false, Message = "Account access is not available." };
        });
        var rejected = false;

        await ServiceClientManager.RunRunnerStatusLoopAsync(
            sink, stop.Token, Guid.NewGuid().ToString("n"), "process-1", "1.0.0", () => string.Empty,
            new RunnerResources(), ["comfyui-latest"], TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5),
            new TaskCompletionSource(), onProtocolRejected: _ => rejected = true);

        Assert.That(sink.Calls, Is.GreaterThanOrEqualTo(3));
        Assert.That(rejected, Is.False);
    }

    /// <summary>Answers registration; every other call is not part of these tests.</summary>
    private sealed class RegistrationSink(Func<int, RegisterRunnerResponse> answer) : IRunnerSessionSink
    {
        public int Calls { get; private set; }

        public Task<RegisterRunnerResponse> RegisterRunnerAsync(RegisterRunnerRequest request, CancellationToken cancellationToken)
            => Task.FromResult(answer(++Calls));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<SyncLocalModelsResponse> SyncLocalModelsAsync(SyncLocalModelsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AckStartSessionAsync(AcknowledgeSessionStartRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AckStopSessionAsync(AcknowledgeSessionStopRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GrantGpuPowerExecutionResponse> GrantGpuPowerExecutionAsync(GrantGpuPowerExecutionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AckGpuPowerLimitAsync(AcknowledgeGpuPowerLimitRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GrantPromptExecutionResponse> GrantPromptExecutionAsync(GrantPromptExecutionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReportPromptEvidenceAsync(ReportPromptEvidenceRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AckSubmitEditorActionAsync(AcknowledgePromptResultRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReportModelDownloadAsync(ReportModelDownloadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReportSessionEventsAsync(ReportSessionEventsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReportSessionLogsAsync(ReportSessionLogsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReportRunnerLogsAsync(ReportRunnerLogsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReportRuntimeTelemetryAsync(ReportRuntimeTelemetryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReadEditorRuntimeBridgeCursorResponse> ReadEditorRuntimeBridgeCursorAsync(ReadEditorRuntimeBridgeCursorRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReserveEditorRuntimeBridgeResponse> ReserveEditorRuntimeBridgeAsync(ReserveEditorRuntimeBridgeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReportEditorRuntimeBridgeStateResponse> ReportEditorRuntimeBridgeStateAsync(ReportEditorRuntimeBridgeStateRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
