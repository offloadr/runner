using System;
using System.Threading.Tasks;
using Offloadr.Runner.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Offloadr.Runner.Tests;

public class SessionRuntimeTelemetryRelayTests
{
    [Test]
    public async Task DeadlineExceededBatch_IsDroppedWithoutRetryLoop()
    {
        var sink = new DeadlineExceededSink();
        await using var relay = new SessionRuntimeTelemetryRelay("runner-1");
        relay.AttachSink(sink);

        relay.Enqueue("session-1", CreateSnapshot());

        await WaitForAsync(sink.FirstAttempt.Task, TimeSpan.FromSeconds(3));
        await Task.Delay(TimeSpan.FromMilliseconds(900));

        Assert.That(sink.CallCount, Is.EqualTo(1));
    }

    private static RunnerRuntimeTelemetrySnapshot CreateSnapshot()
        => new()
        {
            UtilizationPercent = 42,
            VramUsedBytes = 1_024,
            VramTotalBytes = 2_048,
            SampledUtc = Timestamp.FromDateTime(DateTime.UtcNow.ToUniversalTime()),
        };

    private static async Task WaitForAsync(Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, task))
        {
            Assert.Fail($"Timed out after {timeout.TotalSeconds:F1}s waiting for async signal.");
        }

        await task.ConfigureAwait(false);
    }

    private sealed class DeadlineExceededSink : IRunnerSessionSink
    {
        public int CallCount { get; private set; }

        public TaskCompletionSource FirstAttempt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RegisterRunnerResponse> RegisterRunnerAsync(RegisterRunnerRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new RegisterRunnerResponse { Accepted = true });

        public Task<SyncLocalModelsResponse> SyncLocalModelsAsync(SyncLocalModelsRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new SyncLocalModelsResponse());

        public Task AckStartSessionAsync(AcknowledgeSessionStartRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AckStopSessionAsync(AcknowledgeSessionStopRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GrantGpuPowerExecutionResponse> GrantGpuPowerExecutionAsync(GrantGpuPowerExecutionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("Power execution is not part of this fixture");

        public Task AckGpuPowerLimitAsync(AcknowledgeGpuPowerLimitRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GrantPromptExecutionResponse> GrantPromptExecutionAsync(GrantPromptExecutionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("This fixture does not execute prompts.");
        public Task ReportPromptEvidenceAsync(ReportPromptEvidenceRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("This fixture does not observe prompts.");
        public Task AckSubmitEditorActionAsync(AcknowledgePromptResultRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportModelDownloadAsync(ReportModelDownloadRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportSessionEventsAsync(ReportSessionEventsRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportRunnerLogsAsync(ReportRunnerLogsRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportSessionLogsAsync(ReportSessionLogsRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportRuntimeTelemetryAsync(ReportRuntimeTelemetryRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            FirstAttempt.TrySetResult();
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, string.Empty));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
