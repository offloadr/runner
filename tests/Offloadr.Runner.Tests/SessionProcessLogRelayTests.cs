using System;
using System.Threading.Tasks;
using Offloadr.Runner.V1;
using Grpc.Core;

namespace Offloadr.Runner.Tests;

public class SessionProcessLogRelayTests
{
    [Test]
    public async Task EnqueuedLogs_AreRetainedUntilClientReattaches()
    {
        await using var relay = new SessionProcessLogRelay("runner-1");

        relay.Enqueue("session-1", SessionProcessLogStream.Stdout, "line-before-attach");

        // Allow sender loop to attempt at least one flush while no client is attached.
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        var client = new RecordingSink();
        relay.AttachSink(client);

        var request = await WaitForAsync(client.FirstRequest.Task, TimeSpan.FromSeconds(3));

        Assert.That(request.Entries, Has.Count.EqualTo(1));
        Assert.That(request.Entries[0].Message, Is.EqualTo("line-before-attach"));
    }

    [Test]
    public async Task FlushRetries_TransientReportFailure()
    {
        var client = new FailingThenSucceedingSink(failuresBeforeSuccess: 1);
        await using var relay = new SessionProcessLogRelay("runner-1");
        relay.AttachSink(client);

        relay.Enqueue("session-1", SessionProcessLogStream.Stderr, "line-retry");

        var request = await WaitForAsync(client.SuccessRequest.Task, TimeSpan.FromSeconds(3));

        Assert.That(client.CallCount, Is.GreaterThanOrEqualTo(2));
        Assert.That(request.Entries, Has.Count.EqualTo(1));
        Assert.That(request.Entries[0].Message, Is.EqualTo("line-retry"));
        Assert.That(client.FirstFailureRequest.Task.Result.Entries[0].Sequence, Is.EqualTo(request.Entries[0].Sequence));
    }

    [Test]
    public async Task Backlog_IsBounded_WhenDetachedDuringHighVolumeLogs()
    {
        const int queueCapacity = 8;
        const int maxBatchSize = 64;

        await using var relay = new SessionProcessLogRelay("runner-1", queueCapacity);
        for (var i = 0; i < 500; i++)
        {
            relay.Enqueue("session-1", SessionProcessLogStream.Stdout, $"line-{i}");
        }

        // Let the sender attempt a flush while detached so pending backlog accumulates in the bounded queue.
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        var client = new RecordingSink();
        relay.AttachSink(client);

        var drained = await WaitForDrainAsync(client, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        Assert.That(drained, Is.LessThanOrEqualTo(queueCapacity + maxBatchSize));
        Assert.That(client.Messages, Contains.Item("line-499"));
    }

    [Test]
    public async Task DisposeAsync_CancelsInFlightUpload()
    {
        var client = new BlockingSink();
        var relay = new SessionProcessLogRelay("runner-1");
        relay.AttachSink(client);

        relay.Enqueue("session-1", SessionProcessLogStream.Stdout, "line-blocked");

        await WaitForAsync(client.InvocationStarted.Task, TimeSpan.FromSeconds(3));

        var disposeTask = relay.DisposeAsync().AsTask();
        await WaitForAsync(client.CancellationObserved.Task, TimeSpan.FromSeconds(3));
        await WaitForAsync(disposeTask, TimeSpan.FromSeconds(3));
    }

    private static async Task<T> WaitForAsync<T>(Task<T> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, task))
        {
            Assert.Fail($"Timed out after {timeout.TotalSeconds:F1}s waiting for async signal.");
        }

        return await task.ConfigureAwait(false);
    }

    private static async Task WaitForAsync(Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, task))
        {
            Assert.Fail($"Timed out after {timeout.TotalSeconds:F1}s waiting for async signal.");
        }

        await task.ConfigureAwait(false);
    }

    private static async Task<int> WaitForDrainAsync(RecordingSink client, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        var stableReads = 0;
        var previousCount = -1;

        while (DateTime.UtcNow - start < timeout)
        {
            await Task.Delay(100).ConfigureAwait(false);
            var current = client.TotalEntries;
            if (current == previousCount)
            {
                stableReads++;
                if (stableReads >= 3)
                {
                    return current;
                }
            }
            else
            {
                stableReads = 0;
                previousCount = current;
            }
        }

        return client.TotalEntries;
    }

    private sealed class RecordingSink : IRunnerSessionSink
    {
        public TaskCompletionSource<ReportSessionLogsRequest> FirstRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Messages { get; } = new();

        public int TotalEntries => Messages.Count;

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
        public Task ReportRuntimeTelemetryAsync(ReportRuntimeTelemetryRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportSessionLogsAsync(ReportSessionLogsRequest request, CancellationToken cancellationToken)
        {
            Messages.AddRange(request.Entries.Select(e => e.Message));
            FirstRequest.TrySetResult(request.Clone());
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingThenSucceedingSink : IRunnerSessionSink
    {
        private int _remainingFailures;

        public FailingThenSucceedingSink(int failuresBeforeSuccess)
        {
            _remainingFailures = failuresBeforeSuccess;
        }

        public int CallCount { get; private set; }

        public TaskCompletionSource<ReportSessionLogsRequest> FirstFailureRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ReportSessionLogsRequest> SuccessRequest { get; } =
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
        public Task ReportRuntimeTelemetryAsync(ReportRuntimeTelemetryRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportSessionLogsAsync(ReportSessionLogsRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                FirstFailureRequest.TrySetResult(request.Clone());
                throw new RpcException(new Status(StatusCode.Unavailable, "transient"));
            }

            SuccessRequest.TrySetResult(request.Clone());
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingSink : IRunnerSessionSink
    {
        public TaskCompletionSource<bool> InvocationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> CancellationObserved { get; } =
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
        public Task ReportRuntimeTelemetryAsync(ReportRuntimeTelemetryRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportSessionLogsAsync(ReportSessionLogsRequest request, CancellationToken cancellationToken)
        {
            InvocationStarted.TrySetResult(true);

            var responseTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() =>
                {
                    CancellationObserved.TrySetResult(true);
                    responseTask.TrySetCanceled(cancellationToken);
                });
            }

            return responseTask.Task;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
