using Offloadr.Runner.V1;
using Grpc.Core;

namespace Offloadr.Runner.Core;

internal interface IRunnerSessionSink : IAsyncDisposable
{
    Task<RegisterRunnerResponse> RegisterRunnerAsync(RegisterRunnerRequest request, CancellationToken cancellationToken);
    Task<SyncLocalModelsResponse> SyncLocalModelsAsync(SyncLocalModelsRequest request, CancellationToken cancellationToken);
    Task AckRuntimeStartAsync(AcknowledgeRuntimeStartRequest request, CancellationToken cancellationToken);
    Task AckStopSessionAsync(AcknowledgeSessionStopRequest request, CancellationToken cancellationToken);
    Task<GrantGpuPowerExecutionResponse> GrantGpuPowerExecutionAsync(GrantGpuPowerExecutionRequest request, CancellationToken cancellationToken);
    Task AckGpuPowerLimitAsync(AcknowledgeGpuPowerLimitRequest request, CancellationToken cancellationToken);
    Task<GrantPromptExecutionResponse> GrantPromptExecutionAsync(GrantPromptExecutionRequest request, CancellationToken cancellationToken);
    Task AckSubmitEditorActionAsync(AcknowledgePromptResultRequest request, CancellationToken cancellationToken);
    Task AckEditorRuntimeRequestAsync(AcknowledgeEditorRuntimeRequestRequest request, CancellationToken cancellationToken)
        => Task.CompletedTask;
    Task ReportModelDownloadAsync(ReportModelDownloadRequest request, CancellationToken cancellationToken);
    Task ReportSessionEventsAsync(ReportSessionEventsRequest request, CancellationToken cancellationToken);
    Task ReportSessionLogsAsync(ReportSessionLogsRequest request, CancellationToken cancellationToken);
    Task ReportRunnerLogsAsync(ReportRunnerLogsRequest request, CancellationToken cancellationToken);
    Task ReportRuntimeTelemetryAsync(ReportRuntimeTelemetryRequest request, CancellationToken cancellationToken);
}

internal sealed class GrpcRunnerControlSink : IRunnerSessionSink
{
    private readonly RunnerControlService.RunnerControlServiceClient _statusClient;
    private readonly RunnerControlService.RunnerControlServiceClient _commandClient;
    private readonly RunnerControlService.RunnerControlServiceClient _sessionEventClient;
    private readonly RunnerControlService.RunnerControlServiceClient _opsClient;
    private readonly TimeSpan _rpcTimeout;

    public GrpcRunnerControlSink(
        RunnerControlService.RunnerControlServiceClient statusClient,
        RunnerControlService.RunnerControlServiceClient commandClient,
        RunnerControlService.RunnerControlServiceClient sessionEventClient,
        RunnerControlService.RunnerControlServiceClient opsClient,
        TimeSpan rpcTimeout)
    {
        _statusClient = statusClient ?? throw new ArgumentNullException(nameof(statusClient));
        _commandClient = commandClient ?? throw new ArgumentNullException(nameof(commandClient));
        _sessionEventClient = sessionEventClient ?? throw new ArgumentNullException(nameof(sessionEventClient));
        _opsClient = opsClient ?? throw new ArgumentNullException(nameof(opsClient));
        _rpcTimeout = rpcTimeout;
    }

    public async Task<RegisterRunnerResponse> RegisterRunnerAsync(RegisterRunnerRequest request, CancellationToken cancellationToken)
        => await _statusClient.RegisterRunnerAsync(request, deadline: GetDeadline(), cancellationToken: cancellationToken)
            .ResponseAsync.ConfigureAwait(false);

    public async Task<SyncLocalModelsResponse> SyncLocalModelsAsync(SyncLocalModelsRequest request, CancellationToken cancellationToken)
        => await _opsClient.SyncLocalModelsAsync(request, deadline: GetDeadline(), cancellationToken: cancellationToken)
            .ResponseAsync.ConfigureAwait(false);

    public async Task AckRuntimeStartAsync(AcknowledgeRuntimeStartRequest request, CancellationToken cancellationToken)
        => await _commandClient.AcknowledgeRuntimeStartAsync(request, deadline: GetDeadline(), cancellationToken: cancellationToken)
            .ResponseAsync.ConfigureAwait(false);

    public async Task AckStopSessionAsync(AcknowledgeSessionStopRequest request, CancellationToken cancellationToken)
        => await _commandClient.AcknowledgeSessionStopAsync(request, deadline: GetDeadline(), cancellationToken: cancellationToken)
            .ResponseAsync.ConfigureAwait(false);

    public async Task<GrantGpuPowerExecutionResponse> GrantGpuPowerExecutionAsync(GrantGpuPowerExecutionRequest request, CancellationToken cancellationToken)
        => await _commandClient.GrantGpuPowerExecutionAsync(request, deadline: GetDeadline(), cancellationToken: cancellationToken)
            .ResponseAsync.ConfigureAwait(false);

    public async Task AckGpuPowerLimitAsync(AcknowledgeGpuPowerLimitRequest request, CancellationToken cancellationToken)
        => await _commandClient.AcknowledgeGpuPowerLimitAsync(request, deadline: GetDeadline(), cancellationToken: cancellationToken)
            .ResponseAsync.ConfigureAwait(false);

    public async Task<GrantPromptExecutionResponse> GrantPromptExecutionAsync(GrantPromptExecutionRequest request, CancellationToken cancellationToken)
        => await _commandClient.GrantPromptExecutionAsync(request, deadline: GetDeadline(), cancellationToken: cancellationToken)
            .ResponseAsync.ConfigureAwait(false);

    public async Task AckSubmitEditorActionAsync(AcknowledgePromptResultRequest request, CancellationToken cancellationToken)
        => await _commandClient.AcknowledgePromptResultAsync(request, deadline: GetDeadline(), cancellationToken: cancellationToken)
            .ResponseAsync.ConfigureAwait(false);

    public async Task AckEditorRuntimeRequestAsync(AcknowledgeEditorRuntimeRequestRequest request, CancellationToken cancellationToken)
        => await _commandClient.AcknowledgeEditorRuntimeRequestAsync(request, deadline: GetDeadline(), cancellationToken: cancellationToken)
            .ResponseAsync.ConfigureAwait(false);

    public async Task ReportModelDownloadAsync(ReportModelDownloadRequest request, CancellationToken cancellationToken)
        => await _opsClient.ReportModelDownloadAsync(request, deadline: GetDeadline(), cancellationToken: cancellationToken)
            .ResponseAsync.ConfigureAwait(false);

    public async Task ReportSessionEventsAsync(ReportSessionEventsRequest request, CancellationToken cancellationToken)
        => await _sessionEventClient.ReportSessionEventsAsync(request, deadline: GetDeadline(), cancellationToken: cancellationToken)
            .ResponseAsync.ConfigureAwait(false);

    public async Task ReportSessionLogsAsync(ReportSessionLogsRequest request, CancellationToken cancellationToken)
        => await _opsClient.ReportSessionLogsAsync(request, deadline: GetDeadline(), cancellationToken: cancellationToken)
            .ResponseAsync.ConfigureAwait(false);

    public async Task ReportRunnerLogsAsync(ReportRunnerLogsRequest request, CancellationToken cancellationToken)
        => await _opsClient.ReportRunnerLogsAsync(request, deadline: GetDeadline(), cancellationToken: cancellationToken)
            .ResponseAsync.ConfigureAwait(false);

    public async Task ReportRuntimeTelemetryAsync(ReportRuntimeTelemetryRequest request, CancellationToken cancellationToken)
        => await _opsClient.ReportRuntimeTelemetryAsync(request, deadline: GetDeadline(), cancellationToken: cancellationToken)
            .ResponseAsync.ConfigureAwait(false);

    private DateTime? GetDeadline()
        => _rpcTimeout > TimeSpan.Zero ? DateTime.UtcNow.Add(_rpcTimeout) : null;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
