namespace Offloadr.Runner.Tests;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.Runner.V1;
using RunnerRegistrationLoop = Offloadr.Runner.Core.RunnerRegistrationLoop;

public partial class ServiceClientManagerTests
{
    [Test]
    public async Task StartSession_AcknowledgesWhatTheSessionGot()
    {
        var harness = new RuntimeCommandHarness
        {
            SealingStatus = new SessionSealingStatus { MemoryWorkspace = true, EncryptedUploads = true, EncryptedReadsRequired = true },
        };

        await ServiceClientManager.HandleStartRuntimeCommand(SealedStart(InstanceA, 3), harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        Assert.That(ack.Ready, Is.True);
        Assert.That(ack.Sealing, Is.EqualTo(harness.SealingStatus));
        Assert.That(ack.InstanceId, Is.EqualTo(harness.InstanceId.Trim()), "the process that ran it");
        Assert.That(harness.Starts.Single().Sealing, Is.EqualTo(SealedStart(InstanceA, 3).Sealing));
    }

    [Test]
    public async Task Relaunch_WithoutSealingKeepsTheSealingTheSessionStartedWith()
    {
        var harness = new RuntimeCommandHarness();
        var start = SealedStart(InstanceA, 3);
        await ServiceClientManager.HandleStartRuntimeCommand(start, harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);

        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 4), harness.Deps);
        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 2);

        var relaunch = harness.Starts.Last();
        Assert.Multiple(() =>
        {
            Assert.That(ack.Ready, Is.True);
            Assert.That(relaunch.Runtime.RuntimeOrder, Is.EqualTo(4));
            Assert.That(relaunch.Sealing, Is.EqualTo(start.Sealing), "the child's restart stays sealed");
        });
    }

    [Test]
    public async Task RelaunchThatStrengthensTheStartsSealing_KeepsItForTheNextRelaunch()
    {
        var harness = new RuntimeCommandHarness();
        // Started with a weaker, unsealed profile; a relaunch then runs it sealed.
        var start = SealedStart(InstanceA, 3);
        start.Sealing = new SessionSealing { Required = false };
        await ServiceClientManager.HandleStartRuntimeCommand(start, harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        var sealedRelaunch = SealedStart(InstanceB, 4);
        await ServiceClientManager.HandleStartRuntimeCommand(sealedRelaunch, harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 2);

        // The next relaunch omits sealing.
        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceC, 5), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 3);

        Assert.That(harness.Sink.StartAcks.Select(static ack => ack.Ready), Is.All.True);
        Assert.That(harness.Starts.Last().Sealing, Is.EqualTo(sealedRelaunch.Sealing), "it stays as sealed as it last ran");
    }

    [Test]
    public async Task RefusedRelaunch_KeepsTheSealingForTheNextRelaunch()
    {
        var harness = new RuntimeCommandHarness();
        var start = SealedStart(InstanceA, 3);
        await ServiceClientManager.HandleStartRuntimeCommand(start, harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);

        // Refused as weakening, then a relaunch that carries no sealing of its own.
        var weakening = Start(InstanceB, 4);
        weakening.Sealing = new SessionSealing { Required = false };
        await ServiceClientManager.HandleStartRuntimeCommand(weakening, harness.Deps);
        Assert.That((await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 2)).Ready, Is.False);
        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceC, 5), harness.Deps);
        Assert.That((await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 3)).Ready, Is.True);

        Assert.That(harness.Starts.Last().Sealing, Is.EqualTo(start.Sealing), "still sealed as it started");
    }

    [Test]
    public async Task StartOfANewerLifecycle_DoesNotInheritAnOlderLifecyclesSealing()
    {
        var harness = new RuntimeCommandHarness();
        await ServiceClientManager.HandleStartRuntimeCommand(SealedStart(InstanceA, 3), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);

        // Generation 8 began without this runner seeing generation 7 stop.
        var newer = Start(InstanceB, 4);
        newer.LifecycleGeneration = 8;
        await ServiceClientManager.HandleStartRuntimeCommand(newer, harness.Deps);
        Assert.That((await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 2)).Ready, Is.True);

        Assert.That(harness.Starts.Last().Sealing, Is.Null);
    }

    [Test]
    public async Task FailedStart_DoesNotLeaveItsSealingForALaterStart()
    {
        var harness = new RuntimeCommandHarness
        {
            BeforeStart = (start, _) => start.Runtime.RuntimeOrder == 3
                ? throw new InvalidOperationException("Sealed storage is unavailable: low.")
                : Task.CompletedTask,
        };
        await ServiceClientManager.HandleStartRuntimeCommand(SealedStart(InstanceA, 3), harness.Deps);
        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        Assert.That(ack.Ready, Is.False);

        // A later start without sealing of its own.
        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 4), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 2);

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

        await ServiceClientManager.HandleStartRuntimeCommand(SealedStart(InstanceA, 3), harness.Deps);

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
            var start = sealingRequested ? SealedStart(InstanceA, 3) : Start(InstanceA, 3);

            await ServiceClientManager.HandleStartRuntimeCommand(start, harness.Deps);

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
    public async Task StartAskingForOtherSealing_DoesNotJoinTheStartupInProgress()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new RuntimeCommandHarness
        {
            BeforeStart = async (_, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            },
        };

        // An unsealed start of the runtime is under way when a sealed one for it arrives.
        var unsealed = ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ServiceClientManager.HandleStartRuntimeCommand(SealedStart(InstanceA, 3), harness.Deps);
        var refused = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        release.TrySetResult();
        await unsealed.WaitAsync(TimeSpan.FromSeconds(5));
        var started = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 2);

        Assert.Multiple(() =>
        {
            Assert.That(refused.Ready, Is.False);
            Assert.That(refused.Message, Is.EqualTo(ServiceClientManager.OtherSealingMessage));
            Assert.That(started.Ready, Is.True, "the startup in progress goes on");
            Assert.That(harness.Starts, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task RepeatedStartOfAFinishedRuntimeWithOtherSealing_IsRefusedAndKeepsTheAdmittedSealing()
    {
        var harness = new RuntimeCommandHarness();
        var start = SealedStart(InstanceA, 3);
        await ServiceClientManager.HandleStartRuntimeCommand(start, harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);

        // The same runtime's start, delivered again without sealing.
        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceA, 3), harness.Deps);
        var refused = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 2);
        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 4), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 3);

        Assert.Multiple(() =>
        {
            Assert.That(refused.Ready, Is.False);
            Assert.That(refused.Message, Is.EqualTo(ServiceClientManager.OtherSealingMessage));
            Assert.That(harness.Starts, Has.Count.EqualTo(2), "the refused repeat launched nothing");
            Assert.That(harness.Starts.Last().Sealing, Is.EqualTo(start.Sealing));
        });
    }

    [Test]
    public async Task Relaunch_ThatWouldSwapTheOutputRecipientIsRefused()
    {
        var harness = new RuntimeCommandHarness();
        await ServiceClientManager.HandleStartRuntimeCommand(SealedStart(InstanceA, 3), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        var startsBefore = harness.Starts.Count;

        var relaunch = SealedStart(InstanceB, 4);
        relaunch.Sealing.OutputRecipient.KeyId = "output-2";
        await ServiceClientManager.HandleStartRuntimeCommand(relaunch, harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 2);
        Assert.That(ack.Ready, Is.False);
        Assert.That(harness.Starts, Has.Count.EqualTo(startsBefore));
    }

    [Test]
    public async Task Relaunch_ThatWouldDropTheOutputRecipientIsRefused()
    {
        var harness = new RuntimeCommandHarness();
        // Encrypted uploads without the full sealed profile.
        var start = SealedStart(InstanceA, 3);
        start.Sealing.Required = false;
        await ServiceClientManager.HandleStartRuntimeCommand(start, harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        var startsBefore = harness.Starts.Count;

        var relaunch = Start(InstanceB, 4);
        relaunch.Sealing = new SessionSealing();
        await ServiceClientManager.HandleStartRuntimeCommand(relaunch, harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 2);
        Assert.That(ack.Ready, Is.False);
        Assert.That(harness.Starts, Has.Count.EqualTo(startsBefore));
    }

    [Test]
    public async Task Relaunch_ThatWouldWeakenTheSessionsSealingIsRefused()
    {
        var harness = new RuntimeCommandHarness();
        await ServiceClientManager.HandleStartRuntimeCommand(SealedStart(InstanceA, 3), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        var startsBefore = harness.Starts.Count;

        var relaunch = Start(InstanceB, 4);
        relaunch.Sealing = new SessionSealing { Required = false };
        await ServiceClientManager.HandleStartRuntimeCommand(relaunch, harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 2);
        Assert.Multiple(() =>
        {
            Assert.That(ack.Ready, Is.False);
            Assert.That(ack.Message, Does.Contain("cannot run it with less sealing"));
            Assert.That(harness.Starts, Has.Count.EqualTo(startsBefore), "no runtime was started");
            Assert.That(harness.Tracked(CommandSessionId), Is.Null, "the older child was stopped first, as for any newer start");
        });
    }

    [Test]
    public async Task RepeatedStartDelivery_DoesNotReplaceTheAdmittedStartsSealing()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new RuntimeCommandHarness { BeforeStart = (_, token) => release.Task.WaitAsync(token) };
        var start = SealedStart(InstanceA, 3);
        var duplicate = start.Clone();
        duplicate.Sealing = null;

        var admitted = ServiceClientManager.HandleStartRuntimeCommand(start, harness.Deps);
        await WaitUntilStartedAsync(harness);
        var joined = ServiceClientManager.HandleStartRuntimeCommand(duplicate, harness.Deps);
        release.SetResult();
        await Task.WhenAll(admitted, joined);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 2);

        harness.BeforeStart = (_, _) => Task.CompletedTask;
        await ServiceClientManager.HandleStartRuntimeCommand(Start(InstanceB, 4), harness.Deps);
        await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 3);

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
        var harness = RunningHarness(InstanceA, 3);
        harness.UnsavedArtifacts = 1;
        harness.UnsavedArtifactsIncomplete = true;
        var stop = Stop(3);

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
    public async Task Stop_DropsTheLifecycleNoteOnlyAfterItsAcknowledgementGoesThrough()
    {
        var harness = RunningHarness(InstanceA, 3);
        harness.Sink.FailNextAcknowledgements(2);

        await ServiceClientManager.HandleStopSessionCommandAsync(Stop(3), harness.Deps);
        Assert.That(harness.ForgottenLifecycles, Is.Empty, "not before the control plane has the count");

        await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (harness.ForgottenLifecycles.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.That(harness.ForgottenLifecycles, Is.EqualTo(new[] { CommandSessionId }));
    }

    [Test]
    public async Task Stop_ReportsAnIncompleteCountAsUnknown()
    {
        var harness = RunningHarness(InstanceA, 3);
        harness.UnsavedArtifacts = 1;
        harness.UnsavedArtifactsIncomplete = true;

        await ServiceClientManager.HandleStopSessionCommandAsync(Stop(3), harness.Deps);
        var ack = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);

        Assert.That(ack.UnsavedArtifactsUnknown, Is.True);
        Assert.That(ack.UnsavedArtifactCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Stop_ReportsUnsavedArtifactsAndReplaysTheSameAcknowledgement()
    {
        var harness = RunningHarness(InstanceA, 3);
        harness.UnsavedArtifacts = 2;
        var stop = Stop(3);

        await ServiceClientManager.HandleStopSessionCommandAsync(stop, harness.Deps);
        var first = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);
        await ServiceClientManager.HandleStopSessionCommandAsync(stop.Clone(), harness.Deps);
        var replayed = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 2);

        Assert.Multiple(() =>
        {
            Assert.That(first.UnsavedArtifactCount, Is.EqualTo(2));
            Assert.That(replayed.UnsavedArtifactCount, Is.EqualTo(2), "a redelivery replays the completed stop's result");
            Assert.That(harness.UnsavedTakenFor, Is.EqualTo(new[] { CommandSessionId }));
            Assert.That(harness.Calls.Count(call => call.StartsWith("stop:", StringComparison.Ordinal)), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SupersededStop_LeavesTheLostArtifactsAndTheNoteForTheCurrentRuntimesStop()
    {
        var harness = RunningHarness(InstanceB, 4);
        harness.UnsavedArtifacts = 2;

        await ServiceClientManager.HandleStopSessionCommandAsync(Stop(3), harness.Deps);
        var stale = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 1);
        await Task.Delay(100);
        Assert.Multiple(() =>
        {
            Assert.That(harness.UnsavedTakenFor, Is.Empty);
            Assert.That(harness.ForgottenLifecycles, Is.Empty);
        });

        await ServiceClientManager.HandleStopSessionCommandAsync(Stop(4), harness.Deps);
        var current = await harness.Sink.WaitForAsync(harness.Sink.StopAcks, 2);

        Assert.Multiple(() =>
        {
            Assert.That(stale.UnsavedArtifactCount, Is.Zero);
            Assert.That(stale.UnsavedArtifactsUnknown, Is.True);
            Assert.That(stale.CleanupDisposition, Is.EqualTo(RunnerCleanupDisposition.Superseded));
            Assert.That(current.CleanupDisposition, Is.EqualTo(RunnerCleanupDisposition.Performed));
            Assert.That(current.UnsavedArtifactCount, Is.EqualTo(2));
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

        var request = RunnerRegistrationLoop.BuildRequest(
            CommandRunnerId,
            "process-1",
            "1.0.0",
            new RunnerResources(),
            () => new RuntimeStatus(),
            ["comfyui-latest"],
            NullLogger.Instance,
            () => sealedStorage);
        var failing = RunnerRegistrationLoop.BuildRequest(
            CommandRunnerId,
            "process-1",
            "1.0.0",
            new RunnerResources(),
            () => new RuntimeStatus(),
            ["comfyui-latest"],
            NullLogger.Instance,
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

    private static StartRuntimeCommand SealedStart(string runtimeId, ulong runtimeOrder)
    {
        var start = Start(runtimeId, runtimeOrder);
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
