using Microsoft.Extensions.Logging;
using Offloadr.Runner.Agent;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

[NonParallelizable]
public sealed class PlatformDiagnosticContentTests
{
    private const string Sentinel = "synthetic-prompt-email@example.invalid";

    [Test]
    public async Task ConsoleOmitsContentWhileProtectedForwardingStillReceivesIt()
    {
        var original = Console.Out;
        using var output = new StringWriter();
        var delivered = new TaskCompletionSource<ReportRunnerLogsRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new DiagnosticSink
        {
            Runner = (request, _) => { delivered.TrySetResult(request.Clone()); return Task.CompletedTask; }
        };
        await using var relay = new RunnerLogRelay("synthetic-runner");
        relay.AttachSink(sink);
        try
        {
            Console.SetOut(output);
            using (var factory = AgentLogging.CreateFactory(relay))
            {
                factory.CreateLogger("RunnerAgent").LogError(new IOException(Sentinel), "{Content}", Sentinel);
            }
            var request = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            TestContext.Out.WriteLine($"platform-console: leaked={output.ToString().Contains(Sentinel, StringComparison.Ordinal)}; protected={request.Entries.Any(entry => entry.Message.Contains(Sentinel, StringComparison.Ordinal))}");
            Assert.Multiple(() =>
            {
                Assert.That(output.ToString(), Does.Not.Contain(Sentinel));
                Assert.That(output.ToString(), Does.Contain("component=agent").And.Contain("errorClass=io"));
                Assert.That(request.Entries.Any(entry => entry.Message.Contains(Sentinel, StringComparison.Ordinal)), Is.True);
            });
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Test]
    public async Task ProcessConsoleDoesNotInvokeTheArbitraryFormatterOrPrintIdentity()
    {
        var original = Console.Out;
        using var output = new StringWriter();
        await using var relay = new RunnerLogRelay("synthetic-runner");
        var formatting = 0;
        try
        {
            Console.SetOut(output);
            using (var factory = AgentLogging.CreateFactory(relay))
            {
                var logger = factory.CreateLogger("session:" + Sentinel);
                using var scope = logger.BeginScope(Sentinel);
                logger.Log(LogLevel.Information, new EventId(42, Sentinel), Sentinel, new Exception(Sentinel),
                    (state, _) => { formatting++; return state; });
            }
            TestContext.Out.WriteLine($"platform-process: formatter={formatting}; leaked={output.ToString().Contains(Sentinel, StringComparison.Ordinal)}");
            Assert.Multiple(() =>
            {
                Assert.That(formatting, Is.Zero);
                Assert.That(output.ToString(), Does.Not.Contain(Sentinel));
                Assert.That(output.ToString(), Does.Contain("component=editor"));
            });
        }
        finally
        {
            Console.SetOut(original);
        }
    }
    private sealed class DiagnosticSink : IRunnerSessionSink
    {
        public Func<ReportSessionLogsRequest, CancellationToken, Task> Session { get; init; } = (_, _) => throw new NotSupportedException();
        public Func<ReportRunnerLogsRequest, CancellationToken, Task> Runner { get; init; } = (_, _) => throw new NotSupportedException();
        public Task<RegisterRunnerResponse> RegisterRunnerAsync(RegisterRunnerRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
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
        public Task ReportSessionLogsAsync(ReportSessionLogsRequest request, CancellationToken cancellationToken) => Session(request, cancellationToken);
        public Task ReportRunnerLogsAsync(ReportRunnerLogsRequest request, CancellationToken cancellationToken) => Runner(request, cancellationToken);
        public Task ReportRuntimeTelemetryAsync(ReportRuntimeTelemetryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReadEditorRuntimeBridgeCursorResponse> ReadEditorRuntimeBridgeCursorAsync(ReadEditorRuntimeBridgeCursorRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReserveEditorRuntimeBridgeResponse> ReserveEditorRuntimeBridgeAsync(ReserveEditorRuntimeBridgeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReportEditorRuntimeBridgeStateResponse> ReportEditorRuntimeBridgeStateAsync(ReportEditorRuntimeBridgeStateRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}
