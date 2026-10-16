namespace Offloadr.Runner.Tests;

using Offloadr.Runner.V1;

public partial class ServiceClientManagerTests
{
    [Test]
    public async Task StartSession_AcknowledgesWhatTheSessionGot()
    {
        var harness = new RuntimeCommandHarness
        {
            SealingStatus = new SessionSealingStatus { MemoryWorkspace = true, EncryptedUploads = true, EncryptedReadsRequired = true },
        };

        await ServiceClientManager.HandleStartSessionCommand(SealedStart(7, 3, InstanceA), harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        Assert.That(ack.Ready, Is.True);
        Assert.That(ack.Sealing, Is.EqualTo(harness.SealingStatus));
    }

    [Test]
    public async Task StartSession_ThatFailsAcknowledgesNoSealing()
    {
        var harness = new RuntimeCommandHarness
        {
            SealingStatus = new SessionSealingStatus { MemoryWorkspace = true },
            BeforeStart = (_, _) => throw new InvalidOperationException("Sealed storage is unavailable: low."),
        };

        await ServiceClientManager.HandleStartSessionCommand(SealedStart(7, 3, InstanceA), harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        Assert.That(ack.Ready, Is.False);
        Assert.That(ack.Message, Does.Contain("Sealed storage is unavailable"));
        Assert.That(ack.Sealing, Is.Null);
    }

    [Test]
    public async Task LaunchRuntime_KeepsTheSealingTheSessionStartedWith()
    {
        var harness = new RuntimeCommandHarness();
        var start = SealedStart(7, 3, InstanceA);
        await ServiceClientManager.HandleStartSessionCommand(start, harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);

        await ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 4, InstanceB, revision: 1), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1);

        var relaunch = harness.Starts.Last();
        Assert.That(relaunch.RuntimeEpoch, Is.EqualTo(4));
        Assert.That(relaunch.Sealing, Is.EqualTo(start.Sealing));
    }

    [Test]
    public async Task Stop_ReportsUnsavedArtifactsAndReplaysTheSameAcknowledgement()
    {
        var harness = RunningHarness(7, 3, InstanceA);
        harness.UnsavedArtifacts = 2;
        var stop = Stop(7, 3, InstanceA);

        await ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);
        var first = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);
        harness.UnsavedArtifacts = 0;
        await ServiceClientManager.HandleStopSessionCommandAsync(stop.Clone(), harness.Deps);
        var replayed = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 2);

        Assert.Multiple(() =>
        {
            Assert.That(first.UnsavedArtifactCount, Is.EqualTo(2));
            Assert.That(replayed.UnsavedArtifactCount, Is.EqualTo(2), "a redelivery replays the completed stop's result");
            Assert.That(harness.Calls.Count(call => call.StartsWith("stop:", StringComparison.Ordinal)), Is.EqualTo(1));
        });
    }

    [Test]
    public void StatusUpdate_ReportsSealedStorage()
    {
        var sealedStorage = new RunnerSealedStorage
        {
            Mode = SealedStorageMode.Required,
            MemoryWorkspaceAvailable = true,
            BudgetBytes = 8UL << 30,
            Algorithms = { Offloadr.Runner.Core.Sealing.SealedEnvelopeFormat.AlgorithmId },
        };

        var request = ServiceClientManager.BuildStatusUpdateRequest(
            CommandRunnerId,
            "process-1",
            "1.0.0",
            new RunnerResources(),
            () => string.Empty,
            ["comfyui-latest"],
            () => sealedStorage);
        var failing = ServiceClientManager.BuildStatusUpdateRequest(
            CommandRunnerId,
            "process-1",
            "1.0.0",
            new RunnerResources(),
            () => string.Empty,
            ["comfyui-latest"],
            () => throw new IOException("statfs failed"));

        Assert.That(request.SealedStorage, Is.EqualTo(sealedStorage));
        Assert.That(failing.SealedStorage, Is.Null, "a failed probe offers no sealed sessions but keeps heartbeating");
    }

    private static StartSessionCommand SealedStart(ulong generation, ulong epoch, string instance)
    {
        var start = Start(generation, epoch, instance);
        start.Sealing = new SessionSealing
        {
            Required = true,
            OutputRecipient = new SealingPublicKey
            {
                KeyId = "output-1",
                Algorithm = Offloadr.Runner.Core.Sealing.SealedEnvelopeFormat.AlgorithmId,
                PublicKey = Google.Protobuf.ByteString.CopyFrom(new byte[65]),
            },
        };
        return start;
    }
}
