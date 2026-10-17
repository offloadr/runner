namespace Offloadr.Runner.Tests;

using Microsoft.Extensions.Logging;
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
    public async Task LaunchSeenWithoutItsStart_KeepsItsSealingForTheNextLaunch()
    {
        var harness = new RuntimeCommandHarness();
        // This runner never saw the lifecycle's Start (a restart of the agent, say).
        var first = Launch(7, 4, InstanceA, revision: 1);
        first.Sealing = SealedStart(7, 4, InstanceA).Sealing;
        await ServiceClientManager.HandleLaunchRuntimeCommand(first, harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1);

        await ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 5, InstanceB, revision: 2), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 2);

        Assert.That(harness.Starts.Last().Sealing, Is.EqualTo(first.Sealing), "the child's restart stays sealed");
    }

    [Test]
    public async Task RefusedLaunch_KeepsTheSealingForTheNextLaunch()
    {
        var harness = new RuntimeCommandHarness();
        var start = SealedStart(7, 3, InstanceA);
        await ServiceClientManager.HandleStartSessionCommand(start, harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);

        // Refused as weakening, then a launch that carries no sealing of its own.
        var weakening = Launch(7, 4, InstanceB, revision: 1);
        weakening.Sealing = new SessionSealing { Required = false };
        await ServiceClientManager.HandleLaunchRuntimeCommand(weakening, harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1);
        await ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 5, InstanceA, revision: 2), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 2);

        Assert.That(harness.Starts.Last().Sealing, Is.EqualTo(start.Sealing), "still sealed as it started");
    }

    [Test]
    public async Task LaunchOfANewerLifecycle_DoesNotInheritAnOlderLifecyclesSealing()
    {
        var harness = new RuntimeCommandHarness();
        await ServiceClientManager.HandleStartSessionCommand(SealedStart(7, 3, InstanceA), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);

        // Generation 8 began without this runner seeing generation 7 stop.
        await ServiceClientManager.HandleLaunchRuntimeCommand(Launch(8, 1, InstanceB, revision: 1), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1);

        Assert.That(harness.Starts.Last().Sealing, Is.Null);
    }

    [Test]
    public async Task FailedStart_DoesNotLeaveItsSealingForALaterLaunch()
    {
        var harness = new RuntimeCommandHarness
        {
            BeforeStart = (start, _) => start.RuntimeEpoch == 3
                ? throw new InvalidOperationException("Sealed storage is unavailable: low.")
                : Task.CompletedTask,
        };
        await ServiceClientManager.HandleStartSessionCommand(SealedStart(7, 3, InstanceA), harness.Deps);
        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        Assert.That(ack.Ready, Is.False);

        // A launch whose Start this runner did not see, without sealing of its own.
        await ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 4, InstanceB, revision: 1), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1);

        Assert.That(harness.Starts.Last().Sealing, Is.Null, "the failed start's recipient is not inherited");
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

    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public async Task FailedSealedStart_LogsNoSessionFileNames(bool sealingRequested, bool sealedByRunner)
    {
        var logs = new List<string>();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLikeLoggerProvider(logs)));
        RunnerLog.Configure(loggerFactory);
        try
        {
            var harness = new RuntimeCommandHarness
            {
                BeforeStart = (_, _) => throw new InvalidOperationException("Workspace seed entry 'my-secret-portrait.png' is invalid."),
                SealsStart = _ => sealedByRunner,
            };
            var start = sealingRequested ? SealedStart(7, 3, InstanceA) : Start(7, 3, InstanceA);

            await ServiceClientManager.HandleStartSessionCommand(start, harness.Deps);

            var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
            Assert.That(ack.Message, Does.Contain("my-secret-portrait.png"), "the control plane still learns why");
            List<string> lines;
            lock (logs)
            {
                lines = [.. logs];
            }

            if (sealingRequested || sealedByRunner)
            {
                Assert.That(lines, Has.None.Contains("my-secret-portrait"));
                Assert.That(lines, Has.Some.Contains("InvalidOperationException"));
            }
            else
            {
                Assert.That(lines, Has.Some.Contains("my-secret-portrait.png"));
            }
        }
        finally
        {
            RunnerLog.Configure(null);
        }
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
    public async Task DuplicateStartOfARunningRuntime_KeepsTheAdmittedSealing()
    {
        var harness = new RuntimeCommandHarness();
        var start = SealedStart(7, 3, InstanceA);
        await ServiceClientManager.HandleStartSessionCommand(start, harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        harness.Track(start.SessionId, new RuntimeIdentity(7, 3, InstanceA));

        // The same runtime's Start, delivered again without sealing.
        var duplicate = Start(7, 3, InstanceA);
        await ServiceClientManager.HandleStartSessionCommand(duplicate, harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 2);
        await ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 4, InstanceB, revision: 1), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1);

        Assert.That(harness.Starts.Last().Sealing, Is.EqualTo(start.Sealing));
    }

    [Test]
    public async Task LaunchRuntime_ThatWouldSwapTheOutputRecipientIsRefused()
    {
        var harness = new RuntimeCommandHarness();
        await ServiceClientManager.HandleStartSessionCommand(SealedStart(7, 3, InstanceA), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        var startsBefore = harness.Starts.Count;

        var launch = Launch(7, 4, InstanceB, revision: 1);
        launch.Sealing = SealedStart(7, 4, InstanceB).Sealing;
        launch.Sealing.OutputRecipient.KeyId = "output-2";
        await ServiceClientManager.HandleLaunchRuntimeCommand(launch, harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1);
        Assert.That(ack.Ready, Is.False);
        Assert.That(harness.Starts, Has.Count.EqualTo(startsBefore));
    }

    [Test]
    public async Task LaunchRuntime_ThatWouldDropTheOutputRecipientIsRefused()
    {
        var harness = new RuntimeCommandHarness();
        // Encrypted uploads without the full sealed profile.
        var start = SealedStart(7, 3, InstanceA);
        start.Sealing.Required = false;
        await ServiceClientManager.HandleStartSessionCommand(start, harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        var startsBefore = harness.Starts.Count;

        var launch = Launch(7, 4, InstanceB, revision: 1);
        launch.Sealing = new SessionSealing();
        await ServiceClientManager.HandleLaunchRuntimeCommand(launch, harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1);
        Assert.That(ack.Ready, Is.False);
        Assert.That(harness.Starts, Has.Count.EqualTo(startsBefore));
    }

    [Test]
    public async Task LaunchRuntime_ThatWouldWeakenTheSessionsSealingIsRefused()
    {
        var harness = new RuntimeCommandHarness();
        await ServiceClientManager.HandleStartSessionCommand(SealedStart(7, 3, InstanceA), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        var startsBefore = harness.Starts.Count;

        var launch = Launch(7, 4, InstanceB, revision: 1);
        launch.Sealing = new SessionSealing { Required = false };
        await ServiceClientManager.HandleLaunchRuntimeCommand(launch, harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1);
        Assert.That(ack.Ready, Is.False);
        Assert.That(ack.Message, Does.Contain("cannot run it with less sealing"));
        Assert.That(harness.Starts, Has.Count.EqualTo(startsBefore), "no runtime was started");
    }

    [Test]
    public async Task DuplicateStartDelivery_DoesNotReplaceTheAdmittedStartsSealing()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new RuntimeCommandHarness { BeforeStart = (_, token) => release.Task.WaitAsync(token) };
        var start = SealedStart(7, 3, InstanceA);
        var duplicate = start.Clone();
        duplicate.Sealing = null;

        var admitted = ServiceClientManager.HandleStartSessionCommand(start, harness.Deps);
        await WaitUntilStartedAsync(harness);
        var joined = ServiceClientManager.HandleStartSessionCommand(duplicate, harness.Deps);
        release.SetResult();
        await Task.WhenAll(admitted, joined);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);

        await ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 4, InstanceB, revision: 1), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1);

        Assert.That(harness.Starts.Last().Sealing, Is.EqualTo(start.Sealing));
    }

    private static async Task WaitUntilStartedAsync(RuntimeCommandHarness harness)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (harness.Starts.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    [Test]
    public async Task Stop_WithAnUnknownCountReplaysTheSameAcknowledgement()
    {
        var harness = RunningHarness(7, 3, InstanceA);
        harness.UnsavedArtifacts = 1;
        harness.UnsavedArtifactsIncomplete = true;
        var stop = Stop(7, 3, InstanceA);

        await ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);
        var first = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);
        // The first acknowledgement loop has ended; the control plane delivers the Stop again.
        await Task.Delay(200);
        await ServiceClientManager.HandleStopSessionCommandAsync(stop.Clone(), harness.Deps);
        var replayed = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 2);

        Assert.That(replayed, Is.EqualTo(first));
        Assert.That(replayed.UnsavedArtifactsUnknown, Is.True);
    }

    [Test]
    public async Task Stop_ReportsAnIncompleteCountAsUnknown()
    {
        var harness = RunningHarness(7, 3, InstanceA);
        harness.UnsavedArtifacts = 1;
        harness.UnsavedArtifactsIncomplete = true;

        await ServiceClientManager.HandleStopSessionCommandAsync(Stop(7, 3, InstanceA), harness.Deps);
        var ack = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);

        Assert.That(ack.UnsavedArtifactsUnknown, Is.True);
        Assert.That(ack.UnsavedArtifactCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Stop_ReportsUnsavedArtifactsAndReplaysTheSameAcknowledgement()
    {
        var harness = RunningHarness(7, 3, InstanceA);
        harness.UnsavedArtifacts = 2;
        var stop = Stop(7, 3, InstanceA);

        await ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);
        var first = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);
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
    public async Task SupersededStop_LeavesTheLostArtifactsForTheCurrentRuntimesStop()
    {
        var harness = RunningHarness(7, 4, InstanceB);
        harness.UnsavedArtifacts = 2;

        await ServiceClientManager.HandleStopSessionCommandAsync(Stop(7, 3, InstanceA), harness.Deps);
        var stale = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);
        await ServiceClientManager.HandleStopSessionCommandAsync(Stop(7, 4, InstanceB), harness.Deps);
        var current = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 2);

        Assert.Multiple(() =>
        {
            Assert.That(stale.UnsavedArtifactCount, Is.Zero);
            Assert.That(current.UnsavedArtifactCount, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task LaunchRuntime_UsesItsOwnSealingAndAcknowledgesWhatItGot()
    {
        var harness = new RuntimeCommandHarness
        {
            SealingStatus = new SessionSealingStatus { MemoryWorkspace = true, EncryptedUploads = true, EncryptedReadsRequired = true },
        };
        var launch = Launch(7, 3, InstanceA, revision: 1);
        launch.Sealing = SealedStart(7, 3, InstanceA).Sealing;

        // A runner that never saw the start, as after an agent restart.
        await ServiceClientManager.HandleLaunchRuntimeCommand(launch, harness.Deps);
        var ack = await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1);

        Assert.That(harness.Starts.Single().Sealing, Is.EqualTo(launch.Sealing));
        Assert.That(ack.Sealing, Is.EqualTo(harness.SealingStatus));
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

    /// <summary>Records what a console sink would print: the message plus any attached exception.</summary>
    private sealed class ConsoleLikeLoggerProvider(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(lines);

        public void Dispose()
        {
        }

        private sealed class Logger(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var line = formatter(state, exception) + (exception is null ? string.Empty : " " + exception);
                lock (lines)
                {
                    lines.Add(line);
                }
            }
        }
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
