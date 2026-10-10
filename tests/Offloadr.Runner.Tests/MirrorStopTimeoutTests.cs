namespace Offloadr.Runner.Tests;

public class MirrorStopTimeoutTests
{
    [Test]
    public async Task StopArtifactAndWorkspaceMirrorsAsync_CompletesWhenMirrorsNeverStop()
    {
        var originalTimeout = ServiceClientManager.MirrorStopTimeout;
        ServiceClientManager.MirrorStopTimeout = TimeSpan.FromMilliseconds(100);
        try
        {
            var artifactStopCalls = 0;
            var workspaceStopCalls = 0;

            await ServiceClientManager
                .StopArtifactAndWorkspaceMirrorsAsync(
                    "6f1c2d3e-4b5a-4c6d-8e7f-a0b1c2d3e4f5",
                    _ =>
                    {
                        artifactStopCalls++;
                        return new TaskCompletionSource().Task;
                    },
                    _ =>
                    {
                        workspaceStopCalls++;
                        return new TaskCompletionSource().Task;
                    },
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Multiple(() =>
            {
                Assert.That(artifactStopCalls, Is.EqualTo(1));
                Assert.That(workspaceStopCalls, Is.EqualTo(1));
            });
        }
        finally
        {
            ServiceClientManager.MirrorStopTimeout = originalTimeout;
        }
    }
}
