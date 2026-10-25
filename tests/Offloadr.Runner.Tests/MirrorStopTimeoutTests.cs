namespace Offloadr.Runner.Tests;

public class MirrorStopTimeoutTests
{
    [Test]
    public async Task StopArtifactAndWorkspaceMirrorsAsync_JoinsBothOriginalsAfterSlowWarnings()
    {
        var originalTimeout = ServiceClientManager.MirrorStopTimeout;
        ServiceClientManager.MirrorStopTimeout = TimeSpan.FromMilliseconds(20);
        var artifacts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspace = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? stop = null;
        try
        {
            var artifactStopCalls = 0;
            var workspaceStopCalls = 0;
            stop = ServiceClientManager.StopArtifactAndWorkspaceMirrorsAsync("synthetic-session",
                _ => { artifactStopCalls++; return artifacts.Task; },
                _ => { workspaceStopCalls++; return workspace.Task; }, CancellationToken.None);
            await Task.Delay(100);
            Assert.Multiple(() =>
            {
                Assert.That(artifactStopCalls, Is.EqualTo(1));
                Assert.That(workspaceStopCalls, Is.EqualTo(1));
                Assert.That(stop.IsCompleted, Is.False);
            });
            artifacts.SetResult();
            await Task.Delay(20);
            Assert.That(stop.IsCompleted, Is.False, "The original peer remains held.");
            workspace.SetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            artifacts.TrySetResult(); workspace.TrySetResult();
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(5));
            ServiceClientManager.MirrorStopTimeout = originalTimeout;
        }
    }
}
