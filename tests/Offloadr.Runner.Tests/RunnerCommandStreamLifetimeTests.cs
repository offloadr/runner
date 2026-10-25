using System.Reflection;
using System.Runtime.ExceptionServices;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public sealed class RunnerCommandStreamLifetimeTests
{
    [Test]
    public async Task Shutdown_JoinsOriginalLaunchAcknowledgementAfterCommandStreamEnds()
        => await RunHeldCommandAsync("launch");

    [Test]
    public async Task Shutdown_JoinsOriginalStopAcknowledgementAfterCommandStreamEnds()
        => await RunHeldCommandAsync("stop");

    [Test]
    public async Task Shutdown_JoinsOriginalTransientAcknowledgementAfterCommandStreamEnds()
        => await RunHeldCommandAsync("transient");

    [Test]
    public async Task Shutdown_JoinsOriginalPowerGrantAfterCommandStreamEnds()
        => await RunHeldCommandAsync("power");

    private static async Task RunHeldCommandAsync(string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "runner-command-lifetime-" + Guid.NewGuid().ToString("n"));
        var response = new HeldResponse();
        var streamDisposed = Gate();
        var command = new CommandStreamResponse
        {
            LaunchEditorRuntime = new LaunchEditorRuntimeCommand
            {
                RunnerId = "other-runner",
                SessionId = "synthetic-session",
                CommandId = "synthetic-launch",
                LifecycleGeneration = 1,
                RuntimeEpoch = 1,
                RuntimeInstanceId = "33333333333343338333333333333333"
            }
        };
        if (kind == "stop") command = new()
        {
            StopSession = new()
            {
                SessionId = "synthetic-session",
                CommandId = "44444444444444448444444444444444"
            }
        };
        if (kind == "transient") command = new()
        {
            RelayEditorRuntimeRequest = new()
            {
                RunnerId = "other-runner",
                RequestId = "synthetic-request"
            }
        };
        if (kind == "power") command = new()
        {
            SetGpuPowerLimit = new()
            {
                CommandId = "synthetic-power",
                SessionId = "synthetic-session"
            }
        };
        var stream = new SingleCommandReader(command);
        var invoker = new Mock<CallInvoker>(MockBehavior.Strict);
        invoker.Setup(peer => peer.AsyncServerStreamingCall(
                It.IsAny<Method<CommandStreamRequest, CommandStreamResponse>>(), It.IsAny<string?>(),
                It.IsAny<CallOptions>(), It.IsAny<CommandStreamRequest>()))
            .Returns(new AsyncServerStreamingCall<CommandStreamResponse>(stream, Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => streamDisposed.TrySetResult()));
        invoker.Setup(peer => peer.AsyncUnaryCall(
                It.IsAny<Method<AcknowledgeEditorRuntimeLaunchRequest, AcknowledgeEditorRuntimeLaunchResponse>>(),
                It.IsAny<string?>(), It.IsAny<CallOptions>(), It.IsAny<AcknowledgeEditorRuntimeLaunchRequest>()))
            .Returns((Method<AcknowledgeEditorRuntimeLaunchRequest, AcknowledgeEditorRuntimeLaunchResponse> _, string? _,
                CallOptions options, AcknowledgeEditorRuntimeLaunchRequest _) => response.Call<AcknowledgeEditorRuntimeLaunchResponse>(options));
        invoker.Setup(peer => peer.AsyncUnaryCall(
                It.IsAny<Method<AcknowledgeSessionStopRequest, AcknowledgeSessionStopResponse>>(),
                It.IsAny<string?>(), It.IsAny<CallOptions>(), It.IsAny<AcknowledgeSessionStopRequest>()))
            .Returns((Method<AcknowledgeSessionStopRequest, AcknowledgeSessionStopResponse> _, string? _,
                CallOptions options, AcknowledgeSessionStopRequest _) => response.Call<AcknowledgeSessionStopResponse>(options));
        invoker.Setup(peer => peer.AsyncUnaryCall(
                It.IsAny<Method<AcknowledgeEditorRuntimeRequestRequest, AcknowledgeEditorRuntimeRequestResponse>>(),
                It.IsAny<string?>(), It.IsAny<CallOptions>(), It.IsAny<AcknowledgeEditorRuntimeRequestRequest>()))
            .Returns((Method<AcknowledgeEditorRuntimeRequestRequest, AcknowledgeEditorRuntimeRequestResponse> _, string? _,
                CallOptions options, AcknowledgeEditorRuntimeRequestRequest _) => response.Call<AcknowledgeEditorRuntimeRequestResponse>(options));
        invoker.Setup(peer => peer.AsyncUnaryCall(
                It.IsAny<Method<GrantGpuPowerExecutionRequest, GrantGpuPowerExecutionResponse>>(),
                It.IsAny<string?>(), It.IsAny<CallOptions>(), It.IsAny<GrantGpuPowerExecutionRequest>()))
            .Returns((Method<GrantGpuPowerExecutionRequest, GrantGpuPowerExecutionResponse> _, string? _,
                CallOptions options, GrantGpuPowerExecutionRequest _) => response.Call<GrantGpuPowerExecutionResponse>(options));
        using var host = new CancellationTokenSource();
        Task? listener = null;
        Exception? bodyFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            var isolation = new Mock<ISessionIsolationStrategy>(MockBehavior.Strict);
            var commands = new Mock<ILinuxCommandRunner>(MockBehavior.Strict);
            var options = SessionProcessOptions.FromEnvironment(name => name == "SESSION_ROOT" ? Path.Combine(root, "sessions") : null);
            await using var manager = new SessionProcessManager(TestRuntimeActivitySink.Accepted, options, isolation.Object,
                new RunnerVfsEnvironmentBuilder(), commands.Object);
            var settings = Aria2Settings.FromEnvironment(name => name switch
            {
                "ARIA2_STATE_DIR" => Path.Combine(root, "aria"),
                "ARIA2_DOWNLOAD_DIR" => Path.Combine(root, "models"),
                _ => null
            }, () => "fixture");
            await using var downloads = new ModelDownloadService(new IdleBackend(), settings);
            var client = new RunnerControlService.RunnerControlServiceClient(invoker.Object);
            await using var sink = new GrpcRunnerControlSink(client, client, client, client, TimeSpan.FromSeconds(5));
            await using var relay = new ComfySessionEventRelay((_, _) => throw new NotSupportedException(), "runner", "127.0.0.1", 8188,
                "fixture-process", _ => null, (_, _) => false);
            await using var artifacts = new ArtifactUploadService(null, new RunnerArtifactService.RunnerArtifactServiceClient(invoker.Object),
                NullLogger<ArtifactUploadService>.Instance);
            await using var workspace = new WorkspaceMirrorService(new RunnerWorkspaceService.RunnerWorkspaceServiceClient(invoker.Object),
                null, NullLogger<WorkspaceMirrorService>.Instance);
            var gpu = new GpuPowerCommandExecutor(new GpuPowerLimitService(new RuntimeTelemetryService(new Mock<IRuntimeTelemetryProbe>(MockBehavior.Strict).Object)));
            try
            {
                var method = typeof(ServiceClientManager).GetMethod("RunCommandStreamLoopAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
                listener = (Task)method.Invoke(null,
                [
                    client, sink, host.Token, "runner", "fixture-process", "fixture", TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromMilliseconds(20), Task.CompletedTask, manager, downloads, relay, artifacts, workspace, gpu,
                    null, new ServiceClientManager.RunnerCommandWorkState(host.Token), new ServiceClientManager.LogicalSessionState(string.Empty),
                    new RuntimeIdentityRegistry(), (Func<Task>)(() => host.CancelAsync())
                ])!;
                await response.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                isolation.VerifyNoOtherCalls(); commands.VerifyNoOtherCalls();
                await host.CancelAsync();
                await response.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await streamDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(50);
                TestContext.Out.WriteLine($"Original response held={!response.Task.IsCompleted}; command stream disposed={streamDisposed.Task.IsCompleted}; listener complete={listener.IsCompleted}");
                Assert.Multiple(() =>
                {
                    Assert.That(response.Task.IsCompleted, Is.False);
                    Assert.That(listener.IsCompleted, Is.False, "The listener still owns its original acknowledgement RPC task.");
                });
                response.SetResult(new());
                await listener.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error) { bodyFailure = error; }
            finally
            {
                response.TrySetResult(new());
                try
                {
                    await host.CancelAsync();
                    if (listener is not null) await listener.WaitAsync(TimeSpan.FromSeconds(5));
                    await gpu.CancelAndDrainAsync();
                }
                catch (Exception error) { cleanupFailure = error; }
                response.Dispose();
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        if (bodyFailure is not null) ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class IdleBackend : TestModelTransferBackend;

    private sealed class HeldResponse : IDisposable
    {
        private Task? _original;
        private Func<bool>? _complete;
        private CancellationTokenRegistration _registration;
        public TaskCompletionSource Started { get; } = Gate();
        public TaskCompletionSource Cancelled { get; } = Gate();
        public Task Task => _original ?? throw new InvalidOperationException("RPC has not started.");
        public AsyncUnaryCall<T> Call<T>(CallOptions options) where T : new()
        {
            var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _original = source.Task;
            _complete = () => source.TrySetResult(new());
            _registration = options.CancellationToken.Register(() => Cancelled.TrySetResult());
            Started.TrySetResult();
            return new(source.Task, System.Threading.Tasks.Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
        public void SetResult(object _) => _complete!();
        public bool TrySetResult(object _) => _complete?.Invoke() ?? false;
        public void Dispose() => _registration.Dispose();
    }

    private sealed class SingleCommandReader(CommandStreamResponse command) : IAsyncStreamReader<CommandStreamResponse>
    {
        private bool _delivered;
        public TaskCompletionSource Waiting { get; } = Gate();
        public CommandStreamResponse Current => command;
        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (!_delivered) { _delivered = true; return true; }
            Waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return false;
        }
    }
}
