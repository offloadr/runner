using System.Collections;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public sealed class SidecarAdmissionLifetimeTests
{
    [Test]
    public async Task GlobalDispose_JoinsPreparationBeforeFirstRpcAndRejectsLatePreparation()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = Path.Combine(Path.GetTempPath(), "sidecar-admission-" + Guid.NewGuid().ToString("N"));
        var input = Directory.CreateDirectory(Path.Combine(root, "input")).FullName;
        var paths = new SessionProcessManager.SessionPaths
        {
            HomeDirectory = root,
            InputDirectory = input,
            UserDirectory = root,
            CustomNodesDirectory = root,
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var invoker = new Mock<CallInvoker>(MockBehavior.Strict);
        var service = new WorkspaceMirrorService(new RunnerWorkspaceService.RunnerWorkspaceServiceClient(invoker.Object),
            null, NullLogger<WorkspaceMirrorService>.Instance);
        var preparation = service.PrepareSessionAsync("synthetic-session", paths, "synthetic-editor", "comfyui",
            new HeldSnapshot(() => { entered.SetResult(); release.Wait(); }), default);
        Task? stop = null;
        Task? disposal = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop = service.StopSessionAsync("synthetic-session");
            Assert.That(service.StopSessionAsync("synthetic-session"), Is.SameAs(stop));
            disposal = service.DisposeAsync().AsTask();
            Assert.That(service.DisposeAsync().AsTask(), Is.SameAs(disposal));
            Assert.CatchAsync<OperationCanceledException>(() => service.PrepareSessionAsync(
                "late-session", paths, "synthetic-editor", "comfyui", [], default));
            await Task.Delay(50);
            Assert.That(stop.IsCompleted, Is.False);
            Assert.That(disposal.IsCompleted, Is.False);
            release.Set();
            Assert.CatchAsync<OperationCanceledException>(() => preparation.WaitAsync(TimeSpan.FromSeconds(5)));
            await Task.WhenAll(stop, disposal).WaitAsync(TimeSpan.FromSeconds(5));
            invoker.VerifyNoOtherCalls();
        }
        finally
        {
            release.Set();
            try { await preparation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            await Task.WhenAll(stop ?? Task.CompletedTask, disposal ?? service.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task StopCleanup_JoinsPeerAndRelayWithoutMaskingOriginalRuntimeFailure()
    {
        var original = new OperationCanceledException("synthetic native failure");
        var peer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var relay = false;
        var artifactCalls = 0;
        var workspaceCalls = 0;
        var cleanup = ServiceClientManager.StopSessionAndCleanupAsync("synthetic-session",
            (_, _, _) => throw original,
            _ => { relay = true; return Task.CompletedTask; },
            _ => { artifactCalls++; throw new IOException("synthetic sidecar failure"); },
            _ => { workspaceCalls++; entered.TrySetResult(); return peer.Task; },
            _ => { }, () => null, _ => { }, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(cleanup.IsCompleted, Is.False);
            Assert.That(relay, Is.False);
            peer.SetResult();
            var failure = Assert.CatchAsync<OperationCanceledException>(() => cleanup.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Multiple(() =>
            {
                Assert.That(failure, Is.SameAs(original));
                Assert.That(relay, Is.True);
                Assert.That(artifactCalls, Is.EqualTo(1));
                Assert.That(workspaceCalls, Is.EqualTo(1));
            });
        }
        finally
        {
            peer.TrySetResult();
            try { await cleanup.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    [Test]
    public void ExitedCleanup_StillStopsRelayAfterSidecarFailure()
    {
        var relay = false;
        Assert.ThrowsAsync<InvalidOperationException>(() => ServiceClientManager.CleanupExitedSessionAsync("synthetic-session",
            _ => { relay = true; return Task.CompletedTask; },
            _ => throw new IOException("synthetic artifact error"),
            _ => Task.CompletedTask, _ => { }, () => null, _ => { }, default));
        Assert.That(relay, Is.True);
    }

    private sealed class HeldSnapshot(Action enter) : IEnumerable<WorkspaceFileMetadata>
    {
        public IEnumerator<WorkspaceFileMetadata> GetEnumerator() { enter(); return Enumerable.Empty<WorkspaceFileMetadata>().GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
