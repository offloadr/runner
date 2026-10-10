using System;
using System.Linq;
using System.Text;
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
    public async Task Enqueue_LabelsLinesWithTheWritingRuntimeAfterAReplacement()
    {
        var identities = new RuntimeIdentityRegistry();
        // The registry already names the replacement while the old process still drains output.
        identities.Set("session-1", 7, 4, "replacement");
        await using var relay = new SessionProcessLogRelay("runner-1", identities);

        relay.Enqueue("session-1", SessionProcessLogStream.Stdout, "old-runtime-line", new RuntimeIdentity(7, 3, "original"));
        var client = new RecordingSink();
        relay.AttachSink(client);

        var request = await WaitForAsync(client.FirstRequest.Task, TimeSpan.FromSeconds(3));
        var entry = request.Entries.Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.RuntimeEpoch, Is.EqualTo(3));
            Assert.That(entry.RuntimeInstanceId, Is.EqualTo("original"));
        });
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

    [Test]
    public async Task OversizedLine_IsTruncatedWithMarker()
    {
        var client = new RecordingSink();
        await using var relay = new SessionProcessLogRelay("runner-1");
        relay.AttachSink(client);

        relay.Enqueue("session-1", SessionProcessLogStream.Stdout, new string('x', 100_000));

        var request = await WaitForAsync(client.FirstRequest.Task, TimeSpan.FromSeconds(3));
        var message = request.Entries.Single().Message;
        Assert.That(Encoding.UTF8.GetByteCount(message), Is.LessThanOrEqualTo(LogRelayBounds.MaxMessageBytes));
        Assert.That(message, Does.StartWith("xxxx"));
        Assert.That(message, Does.EndWith("[truncated from 100000 bytes]"));
    }

    [Test]
    public async Task Batches_StayWithinByteCap_AndDeliverEveryLine()
    {
        const int lineCount = 200;
        var client = new RecordingSink();
        await using var relay = new SessionProcessLogRelay("runner-1");

        for (var i = 0; i < lineCount; i++)
        {
            relay.Enqueue("session-1", SessionProcessLogStream.Stdout, $"line-{i:D3}-" + new string('y', 20_000));
        }

        relay.AttachSink(client);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (client.TotalEntries < lineCount && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.That(client.TotalEntries, Is.EqualTo(lineCount));
        Assert.That(client.RequestSizes, Has.Count.GreaterThan(1));
        Assert.That(client.RequestSizes, Has.All.LessThanOrEqualTo(LogRelayBounds.MaxBatchBytes));
    }

    [Test]
    public async Task NonRetryableRejection_DropsBatch_AndLaterLogsStillFlow()
    {
        var client = new RejectingSink(StatusCode.InvalidArgument, "poison");
        await using var relay = new SessionProcessLogRelay("runner-1");
        relay.AttachSink(client);

        relay.Enqueue("session-1", SessionProcessLogStream.Stdout, "poison");
        await WaitForAsync(client.Rejected.Task, TimeSpan.FromSeconds(3));
        relay.Enqueue("session-1", SessionProcessLogStream.Stdout, "after-poison");

        var delivered = await WaitForAsync(client.Delivered.Task, TimeSpan.FromSeconds(3));

        Assert.That(delivered.Entries.Select(entry => entry.Message), Is.EqualTo(new[] { "after-poison" }));
        Assert.That(client.RejectedCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task TransientRejection_IsRetried_NotDropped()
    {
        var client = new RejectingSink(StatusCode.Unavailable, "transient", maxRejections: 3);
        await using var relay = new SessionProcessLogRelay("runner-1");
        relay.AttachSink(client);

        relay.Enqueue("session-1", SessionProcessLogStream.Stdout, "transient");

        var delivered = await WaitForAsync(client.Delivered.Task, TimeSpan.FromSeconds(5));
        Assert.That(delivered.Entries.Select(entry => entry.Message), Is.EqualTo(new[] { "transient" }));
        Assert.That(client.RejectedCalls, Is.EqualTo(3));
    }

    [Test]
    public void TruncateMessage_KeepsShortMessages_AndNeverSplitsCharacters()
    {
        Assert.That(LogRelayBounds.TruncateMessage("short"), Is.EqualTo("short"));

        var exact = new string('a', LogRelayBounds.MaxMessageBytes);
        Assert.That(LogRelayBounds.TruncateMessage(exact), Is.SameAs(exact));

        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 10_000));
        var truncated = LogRelayBounds.TruncateMessage(emoji);
        var kept = truncated[..truncated.IndexOf(" [truncated", StringComparison.Ordinal)];

        Assert.That(Encoding.UTF8.GetByteCount(truncated), Is.LessThanOrEqualTo(LogRelayBounds.MaxMessageBytes));
        Assert.That(kept.Length % 2, Is.EqualTo(0));
        Assert.That(kept, Is.EqualTo(emoji[..kept.Length]));
        Assert.That(char.IsHighSurrogate(kept[^1]), Is.False);
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

        public List<int> RequestSizes { get; } = new();

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
            RequestSizes.Add(request.CalculateSize());
            Messages.AddRange(request.Entries.Select(e => e.Message));
            FirstRequest.TrySetResult(request.Clone());
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RejectingSink : IRunnerSessionSink
    {
        private readonly StatusCode _statusCode;
        private readonly string _rejectedMessage;
        private readonly int _maxRejections;
        private int _rejectedCalls;

        public RejectingSink(StatusCode statusCode, string rejectedMessage, int maxRejections = int.MaxValue)
        {
            _statusCode = statusCode;
            _rejectedMessage = rejectedMessage;
            _maxRejections = maxRejections;
        }

        public int RejectedCalls => Volatile.Read(ref _rejectedCalls);

        public TaskCompletionSource<bool> Rejected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ReportSessionLogsRequest> Delivered { get; } =
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
            if (request.Entries.Any(entry => entry.Message == _rejectedMessage) && RejectedCalls < _maxRejections)
            {
                Interlocked.Increment(ref _rejectedCalls);
                Rejected.TrySetResult(true);
                throw new RpcException(new Status(_statusCode, "rejected"));
            }

            Delivered.TrySetResult(request.Clone());
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
