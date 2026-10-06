using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Offloadr.Runner.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Offloadr.Runner.Tests;

public class RunnerLogRelayTests
{
    [Test]
    public async Task EnqueuedLogs_AreDroppedUntilSinkAttaches()
    {
        await using var relay = new RunnerLogRelay("runner-1");

        relay.Enqueue("RunnerAgent", "line-before-attach");
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        var client = new RecordingSink();
        relay.AttachSink(client);
        relay.Enqueue("RunnerAgent", "line-after-attach");

        var request = await WaitForAsync(client.FirstRequest.Task, TimeSpan.FromSeconds(3));

        Assert.That(request.Entries, Has.Count.EqualTo(1));
        Assert.That(request.Entries[0].Message, Is.EqualTo("line-after-attach"));
        Assert.That(request.Entries[0].Category, Is.EqualTo("RunnerAgent"));
    }

    [Test]
    public async Task DetachSink_DropsBufferedLogs_BeforeReattach()
    {
        await using var relay = new RunnerLogRelay("runner-1");
        var firstClient = new BlockingSink();
        relay.AttachSink(firstClient);
        relay.Enqueue("RunnerAgent", "line-before-detach");

        await WaitForAsync(firstClient.InvocationStarted.Task, TimeSpan.FromSeconds(3));
        relay.DetachSink(firstClient);

        var secondClient = new RecordingSink();
        relay.AttachSink(secondClient);
        relay.Enqueue("RunnerAgent", "line-after-reattach");

        var request = await WaitForAsync(secondClient.FirstRequest.Task, TimeSpan.FromSeconds(3));

        Assert.That(request.Entries.Select(entry => entry.Message), Is.EqualTo(new[] { "line-after-reattach" }));
    }

    [Test]
    public async Task ForwardingProvider_SkipsSessionCategories_AndForwardsGeneralLogs()
    {
        await using var relay = new RunnerLogRelay("runner-1");
        var client = new RecordingSink();
        relay.AttachSink(client);

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(new RunnerLogForwardingProvider(relay));
            builder.SetMinimumLevel(LogLevel.Information);
        });

        loggerFactory.CreateLogger("ServiceClientManager").LogInformation("connected");
        loggerFactory.CreateLogger("session:abc123").LogInformation("raw session line");

        var request = await WaitForAsync(client.FirstRequest.Task, TimeSpan.FromSeconds(3));

        Assert.That(request.Entries.Select(entry => entry.Message), Is.EquivalentTo(new[] { "connected" }));
        Assert.That(request.Entries.Select(entry => entry.Category), Is.EquivalentTo(new[] { "ServiceClientManager" }));
    }

    [Test]
    public async Task StructuredEvent_ForwardsStableEventTagAndFormattedProperties()
    {
        await using var relay = new RunnerLogRelay("runner-1");
        var client = new RecordingSink();
        relay.AttachSink(client);

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(new RunnerLogForwardingProvider(relay));
            builder.SetMinimumLevel(LogLevel.Information);
        });

        try
        {
            RunnerLog.Configure(loggerFactory);
            RunnerLog.Event<RunnerLogRelayTests>(
                "model_hydration_promoted",
                "Model hydration promoted model_id={ModelId} reason={Reason}",
                "model-1",
                "range_waiter");

            var request = await WaitForAsync(client.FirstRequest.Task, TimeSpan.FromSeconds(3));

            Assert.That(
                request.Entries.Single().Message,
                Is.EqualTo(
                    "model_hydration_promoted Model hydration promoted model_id=model-1 reason=range_waiter"));
        }
        finally
        {
            RunnerLog.Configure(null);
        }
    }

    [Test]
    public async Task DisposeAsync_FlushesBufferedLogs_BeforeShutdown()
    {
        var relay = new RunnerLogRelay("runner-1");
        var client = new RecordingSink();
        relay.AttachSink(client);
        relay.Enqueue("RunnerAgent", "line-before-dispose");

        await relay.DisposeAsync();

        var request = await WaitForAsync(client.FirstRequest.Task, TimeSpan.FromSeconds(3));
        Assert.That(request.Entries.Select(entry => entry.Message), Is.EqualTo(new[] { "line-before-dispose" }));
    }

    [Test]
    public async Task DisposeAsync_CancelsStuckFlush_WithoutHanging()
    {
        var relay = new RunnerLogRelay("runner-1");
        var client = new BlockingSink();
        relay.AttachSink(client);
        relay.Enqueue("RunnerAgent", "line-before-dispose");

        await WaitForAsync(client.InvocationStarted.Task, TimeSpan.FromSeconds(3));
        var disposeTask = relay.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        if (!ReferenceEquals(completed, disposeTask))
        {
            Assert.Fail("Timed out waiting for relay disposal to complete.");
            return;
        }

        await disposeTask.ConfigureAwait(false);
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

    private sealed class RecordingSink : IRunnerSessionSink
    {
        public TaskCompletionSource<ReportRunnerLogsRequest> FirstRequest { get; } =
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
        public Task ReportSessionLogsAsync(ReportSessionLogsRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportRuntimeTelemetryAsync(ReportRuntimeTelemetryRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportRunnerLogsAsync(ReportRunnerLogsRequest request, CancellationToken cancellationToken)
        {
            FirstRequest.TrySetResult(request.Clone());
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingSink : IRunnerSessionSink
    {
        public TaskCompletionSource<bool> InvocationStarted { get; } =
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
        public Task ReportSessionLogsAsync(ReportSessionLogsRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportRuntimeTelemetryAsync(ReportRuntimeTelemetryRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportRunnerLogsAsync(ReportRunnerLogsRequest request, CancellationToken cancellationToken)
        {
            InvocationStarted.TrySetResult(true);
            var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => wait.TrySetCanceled(cancellationToken));
            }

            return wait.Task;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
