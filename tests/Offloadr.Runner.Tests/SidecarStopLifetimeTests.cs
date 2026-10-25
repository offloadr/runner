using System.Runtime.ExceptionServices;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Offloadr.EditorRuntime.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public sealed class SidecarStopLifetimeTests
{
    [Test]
    public async Task ArtifactStopAndDispose_JoinOriginalReadAfterItsCancellationWaitExpires()
    {
        var root = Path.Combine(Path.GetTempPath(), "sidecar-original-" + Guid.NewGuid().ToString("N"));
        var paths = Paths(root);
        var reader = new HeldReader<RunnerArtifactServiceReadArtifactResponse>(() => new()
        {
            Metadata = new RunnerReadStreamMetadata { SizeBytes = 4 }
        });
        var invoker = new Mock<CallInvoker>(MockBehavior.Strict);
        invoker.Setup(peer => peer.AsyncUnaryCall(
                It.IsAny<Method<RunnerArtifactServiceListArtifactsRequest, RunnerArtifactServiceListArtifactsResponse>>(),
                It.IsAny<string?>(), It.IsAny<CallOptions>(), It.IsAny<RunnerArtifactServiceListArtifactsRequest>()))
            .Returns(new AsyncUnaryCall<RunnerArtifactServiceListArtifactsResponse>(Task.FromResult(new RunnerArtifactServiceListArtifactsResponse()),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { }));
        invoker.Setup(peer => peer.AsyncServerStreamingCall(
                It.IsAny<Method<RunnerArtifactServiceReadArtifactRequest, RunnerArtifactServiceReadArtifactResponse>>(),
                It.IsAny<string?>(), It.IsAny<CallOptions>(), It.IsAny<RunnerArtifactServiceReadArtifactRequest>()))
            .Returns(Stream(reader));
        var service = new ArtifactUploadService(null, new RunnerArtifactService.RunnerArtifactServiceClient(invoker.Object),
            NullLogger<ArtifactUploadService>.Instance);
        var wait = ArtifactUploadService.StopWaitTimeout;
        var record = ArtifactUploadService.StopRecordTimeout;
        ArtifactUploadService.StopWaitTimeout = TimeSpan.FromMilliseconds(20);
        ArtifactUploadService.StopRecordTimeout = TimeSpan.FromMilliseconds(40);
        Task? request = null;
        Task? stop = null;
        Task? dispose = null;
        Exception? bodyFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            await service.StartSessionAsync("synthetic-session", paths, default);
            await service.SeedSessionAsync("synthetic-session", "synthetic-editor", null,
                [new EditorArtifactMetadata { EditorSid = "synthetic-editor", Filename = "remote.png", Type = "output", SizeBytes = 4 }], default);
            request = service.TryEnsureArtifactAvailableAsync(Path.Combine(paths.OutputDirectory, "remote.png"), true, default);
            await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop = service.StopSessionAsync("synthetic-session");
            dispose = service.DisposeAsync().AsTask();
            await Task.Delay(150);
            TestContext.Out.WriteLine($"Artifact original held={!reader.Original.Task.IsCompleted}; stop complete={stop.IsCompleted}; dispose complete={dispose.IsCompleted}");
            Assert.Multiple(() =>
            {
                Assert.That(stop.IsCompleted, Is.False, "A loss-accounting timeout does not finish the original read.");
                Assert.That(dispose.IsCompleted, Is.False, "Global disposal still owns the retired uploader.");
            });
            reader.Release();
            await Task.WhenAll(stop, dispose).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception error) { bodyFailure = error; }
        finally
        {
            reader.Release();
            try
            {
                try { if (request is not null) await request.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
                await Task.WhenAll(stop ?? Task.CompletedTask, dispose ?? service.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error) { cleanupFailure = error; }
            ArtifactUploadService.StopWaitTimeout = wait;
            ArtifactUploadService.StopRecordTimeout = record;
            reader.Dispose();
            Directory.Delete(root, recursive: true);
        }
        Rethrow(bodyFailure, cleanupFailure);
    }

    [Test]
    public async Task WorkspaceStopAndDispose_JoinOriginalInitialization()
    {
        var root = Path.Combine(Path.GetTempPath(), "sidecar-original-" + Guid.NewGuid().ToString("N"));
        var paths = Paths(root);
        var reader = new HeldReader<RunnerWorkspaceServiceReadWorkspaceSeedArchiveResponse>(() => new()
        {
            Metadata = new RunnerReadStreamMetadata { SizeBytes = 0 }
        });
        var invoker = new Mock<CallInvoker>(MockBehavior.Strict);
        invoker.Setup(peer => peer.AsyncServerStreamingCall(
                It.IsAny<Method<RunnerWorkspaceServiceReadWorkspaceSeedArchiveRequest, RunnerWorkspaceServiceReadWorkspaceSeedArchiveResponse>>(),
                It.IsAny<string?>(), It.IsAny<CallOptions>(), It.IsAny<RunnerWorkspaceServiceReadWorkspaceSeedArchiveRequest>()))
            .Returns(Stream(reader));
        var service = new WorkspaceMirrorService(new RunnerWorkspaceService.RunnerWorkspaceServiceClient(invoker.Object), null,
            NullLogger<WorkspaceMirrorService>.Instance);
        Task? initialize = null;
        Task? stop = null;
        Task? dispose = null;
        Exception? bodyFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            initialize = service.PrepareSessionAsync("synthetic-session", paths, "synthetic-editor", "comfyui", [], default);
            await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop = service.StopSessionAsync("synthetic-session");
            dispose = service.DisposeAsync().AsTask();
            await Task.Delay(50);
            TestContext.Out.WriteLine($"Workspace original held={!reader.Original.Task.IsCompleted}; stop complete={stop.IsCompleted}; dispose complete={dispose.IsCompleted}");
            Assert.Multiple(() =>
            {
                Assert.That(stop.IsCompleted, Is.False, "Initialization remains an original mirror operation.");
                Assert.That(dispose.IsCompleted, Is.False, "Global disposal still owns the retired mirror.");
            });
            reader.Release();
            await Task.WhenAll(stop, dispose).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception error) { bodyFailure = error; }
        finally
        {
            reader.Release();
            try
            {
                try { if (initialize is not null) await initialize.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
                await Task.WhenAll(stop ?? Task.CompletedTask, dispose ?? service.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error) { cleanupFailure = error; }
            reader.Dispose();
            Directory.Delete(root, recursive: true);
        }
        Rethrow(bodyFailure, cleanupFailure);
    }

    [Test]
    public async Task MirrorHelper_JoinsOriginalPeerDespiteFailureAndTimeout()
    {
        var original = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = ServiceClientManager.MirrorStopTimeout;
        ServiceClientManager.MirrorStopTimeout = TimeSpan.FromMilliseconds(20);
        Task? stop = null;
        Exception? bodyFailure = null;
        try
        {
            stop = ServiceClientManager.StopArtifactAndWorkspaceMirrorsAsync("synthetic-session",
                _ => throw new InvalidOperationException("synthetic stop failure"),
                _ => { peerStarted.TrySetResult(); return original.Task; }, default);
            await peerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            TestContext.Out.WriteLine($"Helper peer held={!original.Task.IsCompleted}; helper complete={stop.IsCompleted}");
            Assert.That(stop.IsCompleted, Is.False, "Failure and elapsed waiting do not finish the peer's original task.");
            original.SetResult();
            var error = Assert.ThrowsAsync<InvalidOperationException>(() => stop)!;
            Assert.That(error.InnerException, Is.Null);
            Assert.That(error.Message, Is.EqualTo("Session sidecar cleanup failed."));
        }
        catch (Exception error) { bodyFailure = error; }
        finally
        {
            original.TrySetResult();
            try { if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (InvalidOperationException) { }
            ServiceClientManager.MirrorStopTimeout = timeout;
        }
        if (bodyFailure is not null) ExceptionDispatchInfo.Capture(bodyFailure).Throw();
    }

    private static void Rethrow(Exception? bodyFailure, Exception? cleanupFailure)
    {
        if (bodyFailure is not null) ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    private static SessionProcessManager.SessionPaths Paths(string root)
    {
        var paths = new SessionProcessManager.SessionPaths
        {
            HomeDirectory = root,
            UserDirectory = Path.Combine(root, "user"),
            CustomNodesDirectory = Path.Combine(root, "nodes"),
            InputDirectory = Path.Combine(root, "input"),
            OutputDirectory = Path.Combine(root, "output"),
            TempDirectory = Path.Combine(root, "temp"),
            CacheDirectory = Path.Combine(root, "cache"),
            LogsDirectory = Path.Combine(root, "logs")
        };
        foreach (var path in new[] { root, paths.UserDirectory, paths.CustomNodesDirectory, paths.InputDirectory, paths.OutputDirectory, paths.TempDirectory, paths.CacheDirectory, paths.LogsDirectory }) Directory.CreateDirectory(path);
        return paths;
    }

    private static AsyncServerStreamingCall<T> Stream<T>(IAsyncStreamReader<T> reader)
        => new(reader, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

    private sealed class HeldReader<T>(Func<T> metadata) : IAsyncStreamReader<T>, IDisposable
    {
        private bool _metadata;
        private CancellationTokenRegistration _registration;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Original { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public T Current { get; private set; } = default!;
        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (!_metadata) { _metadata = true; Current = metadata(); return true; }
            _registration = cancellationToken.Register(static () => { });
            Started.TrySetResult();
            await Original.Task;
            throw new OperationCanceledException("synthetic response released", cancellationToken);
        }
        public void Release() => Original.TrySetResult();
        public void Dispose() => _registration.Dispose();
    }
}
