namespace Offloadr.Runner.Tests;

using System.Collections.Concurrent;
using Grpc.Core;
using Offloadr.Runner.V1;

public partial class ServiceClientManagerTests
{
    private const string CommandRunnerId = "11111111111141118111111111111111";
    private const string CommandSessionId = "22222222222242228222222222222222";
    private const string InstanceA = "aaaaaaaaaaaa4aaa8aaaaaaaaaaaaaaa";
    private const string InstanceB = "bbbbbbbbbbbb4bbb8bbbbbbbbbbbbbbb";

    [Test]
    public async Task LaunchRuntime_AcknowledgesReadyAndRetainsIdentity()
    {
        var harness = new RuntimeCommandHarness();

        await ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 3, InstanceA, revision: 1), harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.Ready, Is.True);
            Assert.That(ack.RuntimeEpoch, Is.EqualTo(3));
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(new RuntimeIdentity(7, 3, InstanceA)));
            Assert.That(harness.Logical.GetActiveSessionId(), Is.EqualTo(CommandSessionId));
            Assert.That(harness.Identities.TryGet(CommandSessionId, out var identity), Is.True);
            Assert.That(identity, Is.EqualTo(new RuntimeIdentity(7, 3, InstanceA)));
        });
    }

    [Test]
    public async Task Quiesce_WhenStopFails_AcknowledgesNotQuiescedAndKeepsState()
    {
        var harness = RunningHarness(7, 3, InstanceA);
        harness.BeforeStop = (_, _) => throw new IOException("signal failed");

        await ServiceClientManager.HandleQuiesceRuntimeCommandAsync(Quiesce(7, 3, InstanceA, revision: 1), harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.QuiesceAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.Quiesced, Is.False);
            Assert.That(harness.Logical.GetActiveSessionId(), Is.EqualTo(CommandSessionId));
            Assert.That(harness.Identities.TryGet(CommandSessionId, out _), Is.True);
        });
    }

    [Test]
    public async Task Stop_WhenStopFails_KeepsStateAndDoesNotAcknowledge()
    {
        var harness = RunningHarness(7, 3, InstanceA);
        harness.BeforeStop = (_, _) => throw new IOException("signal failed");

        await ServiceClientManager.HandleStopSessionCommandAsync(
            new StopSessionCommand { SessionId = CommandSessionId, User = "alice" },
            harness.Deps);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.Multiple(() =>
        {
            Assert.That(harness.Sink.StopAcks, Is.Empty);
            Assert.That(harness.Logical.GetActiveSessionId(), Is.EqualTo(CommandSessionId));
            Assert.That(harness.Identities.TryGet(CommandSessionId, out _), Is.True);
            Assert.That(harness.Tracked(CommandSessionId), Is.Not.Null);
        });
    }

    [Test]
    public async Task FailedLaunch_WhenCleanupStopFails_KeepsStateUntilTheProcessExits()
    {
        var harness = new RuntimeCommandHarness
        {
            BeforeStart = (command, _) => throw new TimeoutException("not ready"),
            BeforeStop = (_, _) => throw new IOException("signal failed"),
        };

        await ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 3, InstanceA, revision: 1), harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.Ready, Is.False);
            Assert.That(harness.Logical.GetActiveSessionId(), Is.EqualTo(CommandSessionId));
            Assert.That(harness.Identities.TryGet(CommandSessionId, out _), Is.True);
        });
    }

    [Test]
    public async Task ConcurrentLaunches_CancelAndAwaitThePreviousStartupBeforeStarting()
    {
        var harness = new RuntimeCommandHarness();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeStart = async (command, token) =>
        {
            if (command.RuntimeEpoch == 3)
            {
                firstEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };

        var first = ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 3, InstanceA, revision: 1), harness.Deps);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 4, InstanceB, revision: 2), harness.Deps);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        var calls = harness.Calls.ToList();
        Assert.Multiple(() =>
        {
            Assert.That(harness.Sink.LaunchAcks.Single(ack => ack.RuntimeEpoch == 3).Ready, Is.False);
            Assert.That(harness.Sink.LaunchAcks.Single(ack => ack.RuntimeEpoch == 4).Ready, Is.True);
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(new RuntimeIdentity(7, 4, InstanceB)));
            Assert.That(harness.Logical.TryGetRuntime(CommandSessionId, out var logical, out var revision), Is.True);
            Assert.That(logical, Is.EqualTo(new RuntimeIdentity(7, 4, InstanceB)));
            Assert.That(revision, Is.EqualTo(2));
            Assert.That(harness.Identities.TryGet(CommandSessionId, out var identity), Is.True);
            Assert.That(identity, Is.EqualTo(new RuntimeIdentity(7, 4, InstanceB)));
            // The superseded startup finished its cleanup before the newer one started.
            Assert.That(calls.IndexOf("start:4"), Is.GreaterThan(calls.IndexOf("stop:" + CommandSessionId)));
            Assert.That(calls.IndexOf("stop:" + CommandSessionId), Is.GreaterThan(calls.IndexOf("start:3")));
        });
    }

    [Test]
    public async Task StaleLaunch_DoesNotReplaceTheNewerRuntimeOrItsIdentity()
    {
        var harness = RunningHarness(7, 4, InstanceB);

        await ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 3, InstanceA, revision: 1), harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.Ready, Is.False);
            Assert.That(ack.RuntimeEpoch, Is.EqualTo(3));
            Assert.That(harness.Calls, Is.Empty);
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(new RuntimeIdentity(7, 4, InstanceB)));
            Assert.That(harness.Logical.TryGetRuntime(CommandSessionId, out var logical, out _), Is.True);
            Assert.That(logical, Is.EqualTo(new RuntimeIdentity(7, 4, InstanceB)));
            Assert.That(harness.Identities.TryGet(CommandSessionId, out var identity), Is.True);
            Assert.That(identity, Is.EqualTo(new RuntimeIdentity(7, 4, InstanceB)));
        });
    }

    [Test]
    public async Task StaleLaunch_DoesNotCancelTheNewerStartup()
    {
        var harness = new RuntimeCommandHarness();
        var newerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNewer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeStart = async (_, token) =>
        {
            newerEntered.TrySetResult();
            await releaseNewer.Task.WaitAsync(token);
        };

        var newer = ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 4, InstanceB, revision: 2), harness.Deps);
        await newerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 3, InstanceA, revision: 1), harness.Deps);
        releaseNewer.TrySetResult();
        await newer.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(harness.Sink.LaunchAcks.Single(ack => ack.RuntimeEpoch == 3).Ready, Is.False);
            Assert.That(harness.Sink.LaunchAcks.Single(ack => ack.RuntimeEpoch == 4).Ready, Is.True);
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(new RuntimeIdentity(7, 4, InstanceB)));
        });
    }

    [Test]
    public async Task Launch_ForAnotherRunner_IsRejectedWithoutTouchingState()
    {
        var harness = new RuntimeCommandHarness();
        var launch = Launch(7, 3, InstanceA, revision: 1);
        launch.RunnerId = "99999999999949998999999999999999";

        await ServiceClientManager.HandleLaunchRuntimeCommand(launch, harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.LaunchAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.Ready, Is.False);
            Assert.That(harness.Calls, Is.Empty);
            Assert.That(harness.Logical.GetActiveSessionId(), Is.Empty);
            Assert.That(harness.Identities.TryGet(CommandSessionId, out _), Is.False);
        });
    }

    [Test]
    public async Task StaleStart_DoesNotReplaceTheNewerRuntime()
    {
        var harness = RunningHarness(8, 1, InstanceB);

        await ServiceClientManager.HandleStartSessionCommand(Start(7, 1, InstanceA), harness.Deps);

        var ack = await harness.Sink.WaitForAsync(harness.Sink.StartAcks, 1);
        Assert.Multiple(() =>
        {
            Assert.That(ack.Ready, Is.False);
            Assert.That(harness.Calls, Is.Empty);
            Assert.That(harness.Tracked(CommandSessionId), Is.EqualTo(new RuntimeIdentity(8, 1, InstanceB)));
        });
    }

    [Test]
    public async Task StopDuringStartup_CancelsTheStartupAndClearsItsState()
    {
        var harness = new RuntimeCommandHarness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeStart = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        var launch = ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 3, InstanceA, revision: 1), harness.Deps);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ServiceClientManager.HandleStopSessionCommandAsync(
            new StopSessionCommand { SessionId = CommandSessionId, User = "alice" },
            harness.Deps).WaitAsync(TimeSpan.FromSeconds(5));
        await launch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(harness.Sink.LaunchAcks.Single().Ready, Is.False);
            Assert.That(harness.Sink.StopAcks, Has.Count.EqualTo(1));
            Assert.That(harness.Tracked(CommandSessionId), Is.Null);
            Assert.That(harness.Logical.GetActiveSessionId(), Is.Empty);
            Assert.That(harness.Identities.TryGet(CommandSessionId, out _), Is.False);
            Assert.That(harness.WorkState.GetActiveStartup(), Is.Null);
        });
    }

    [Test]
    public async Task Stop_IsNotBlockedByStartupAcknowledgementRetries()
    {
        var harness = new RuntimeCommandHarness();
        harness.Sink.FailWhen = request => request is AcknowledgeEditorRuntimeLaunchRequest;

        var launch = ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 3, InstanceA, revision: 1), harness.Deps);
        await WaitUntilAsync(() => harness.Sink.Attempts.OfType<AcknowledgeEditorRuntimeLaunchRequest>().Any());

        await ServiceClientManager.HandleStopSessionCommandAsync(
            new StopSessionCommand { SessionId = CommandSessionId, User = "alice" },
            harness.Deps).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Multiple(() =>
        {
            Assert.That(launch.IsCompleted, Is.False, "The launch acknowledgement keeps retrying independently.");
            Assert.That(harness.Tracked(CommandSessionId), Is.Null);
        });
        await harness.Shutdown.CancelAsync();
        await launch.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task LaunchAcknowledgementRetry_ResendsTheIdenticalResultWithoutRelaunching()
    {
        var harness = new RuntimeCommandHarness();
        harness.Sink.FailNextAcknowledgements(2);

        await ServiceClientManager.HandleLaunchRuntimeCommand(Launch(7, 3, InstanceA, revision: 1), harness.Deps)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var attempts = harness.Sink.Attempts.OfType<AcknowledgeEditorRuntimeLaunchRequest>().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(attempts, Has.Count.EqualTo(3));
            Assert.That(attempts.Distinct(), Has.Exactly(1).Items);
            Assert.That(attempts[0].Ready, Is.True);
            Assert.That(harness.Sink.LaunchAcks, Has.Count.EqualTo(1));
            Assert.That(harness.Calls.Count(call => call.StartsWith("start:", StringComparison.Ordinal)), Is.EqualTo(1));
        });
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for condition.");
            }

            await Task.Delay(10);
        }
    }

    private static RuntimeCommandHarness RunningHarness(ulong generation, ulong epoch, string instance)
    {
        var harness = new RuntimeCommandHarness();
        harness.Logical.SetActiveRuntime(CommandSessionId, generation, epoch, instance);
        harness.Identities.Set(CommandSessionId, generation, epoch, instance);
        harness.Track(CommandSessionId, new RuntimeIdentity(generation, epoch, instance));
        return harness;
    }

    private static LaunchEditorRuntimeCommand Launch(ulong generation, ulong epoch, string instance, uint revision, string? commandId = null)
        => new()
        {
            CommandId = commandId ?? Guid.NewGuid().ToString("n"),
            RestartId = "restart-" + revision,
            RunnerId = CommandRunnerId,
            SessionId = CommandSessionId,
            User = "alice",
            EditorSid = "editor",
            EditorRuntimeKind = "comfyui",
            LifecycleGeneration = generation,
            RuntimeEpoch = epoch,
            RuntimeInstanceId = instance,
            RestartRevision = revision,
            Attempt = 1,
        };

    private static QuiesceEditorRuntimeCommand Quiesce(ulong generation, ulong epoch, string instance, uint revision, string? commandId = null)
        => new()
        {
            CommandId = commandId ?? Guid.NewGuid().ToString("n"),
            RestartId = "restart-" + revision,
            RunnerId = CommandRunnerId,
            SessionId = CommandSessionId,
            EditorSid = "editor",
            LifecycleGeneration = generation,
            RuntimeEpoch = epoch,
            RuntimeInstanceId = instance,
            RestartRevision = revision,
        };

    private static StartSessionCommand Start(ulong generation, ulong epoch, string instance, string sessionId = CommandSessionId)
        => new()
        {
            SessionId = sessionId,
            User = "alice",
            EditorSid = "editor",
            EditorRuntimeKind = "comfyui",
            LifecycleGeneration = generation,
            RuntimeEpoch = epoch,
            RuntimeInstanceId = instance,
        };

    /// <summary>
    /// Binds the runtime command handlers to an in-memory runtime table so that
    /// identity fencing can be observed without a child process.
    /// </summary>
    private sealed class RuntimeCommandHarness
    {
        private readonly ConcurrentDictionary<string, RuntimeIdentity> _tracked = new(StringComparer.Ordinal);

        public RuntimeCommandHarness()
        {
            WorkState = new ServiceClientManager.RunnerCommandWorkState(Shutdown.Token);
        }

        public CancellationTokenSource Shutdown { get; } = new();
        public RuntimeCommandSink Sink { get; } = new();
        public ServiceClientManager.RunnerCommandWorkState WorkState { get; }
        public ServiceClientManager.LogicalSessionState Logical { get; } = new(string.Empty);
        public RuntimeIdentityRegistry Identities { get; } = new();
        public ConcurrentDictionary<string, CancellationTokenSource> TransientCancellation { get; } = new(StringComparer.Ordinal);
        public ConcurrentQueue<string> Calls { get; } = new();

        /// <summary>Runs before a start registers its runtime; may block or throw.</summary>
        public Func<StartSessionCommand, CancellationToken, Task> BeforeStart { get; set; } = (_, _) => Task.CompletedTask;

        /// <summary>Runs before a stop removes its runtime; may block or throw.</summary>
        public Func<string, CancellationToken, Task> BeforeStop { get; set; } = (_, _) => Task.CompletedTask;

        public RuntimeIdentity? Tracked(string sessionId)
            => _tracked.TryGetValue(sessionId, out var identity) ? identity : null;

        public void Track(string sessionId, RuntimeIdentity identity) => _tracked[sessionId] = identity;

        public ServiceClientManager.RuntimeCommandDependencies Deps => new()
        {
            RunnerId = CommandRunnerId,
            Sink = Sink,
            WorkState = WorkState,
            LogicalSessionState = Logical,
            RuntimeIdentities = Identities,
            TransientSessionCancellation = TransientCancellation,
            GetActiveRuntimeSessionId = () => _tracked.Keys.FirstOrDefault() ?? string.Empty,
            StartRuntimeAndWaitForReady = StartAsync,
            StopRuntime = StopAsync,
            StopSessionRelay = sessionId => Record("relay:" + sessionId),
            StopArtifactUploads = sessionId => Record("uploads:" + sessionId),
            StopWorkspaceMirrors = sessionId => Record("mirror:" + sessionId),
            CancelSessionDownloads = sessionId => Calls.Enqueue("downloads:" + sessionId),
            GetDownloadActiveSessionId = () => null,
            SetDownloadActiveSession = _ => { },
            ActivateSession = (activate, token) =>
            {
                token.ThrowIfCancellationRequested();
                activate();
                return Task.CompletedTask;
            },
            Shutdown = Shutdown.Token,
        };

        private Task Record(string call)
        {
            Calls.Enqueue(call);
            return Task.CompletedTask;
        }

        private async Task StartAsync(StartSessionCommand command, CancellationToken token)
        {
            Calls.Enqueue($"start:{command.RuntimeEpoch}");
            var identity = new RuntimeIdentity(command.LifecycleGeneration, command.RuntimeEpoch, command.RuntimeInstanceId);
            if (_tracked.TryGetValue(command.SessionId, out var existing) && existing != identity)
            {
                throw new InvalidOperationException("The tracked session is a different runtime.");
            }

            await BeforeStart(command, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _tracked[command.SessionId] = identity;
        }

        private async Task StopAsync(string sessionId, Func<string, CancellationToken, Task>? beforeCleanup, CancellationToken token)
        {
            Calls.Enqueue("stop:" + sessionId);
            await BeforeStop(sessionId, token).ConfigureAwait(false);
            _tracked.TryRemove(sessionId, out _);
            if (beforeCleanup is not null)
            {
                await beforeCleanup(sessionId, token).ConfigureAwait(false);
            }
        }
    }

    private sealed class RuntimeCommandSink : IRunnerSessionSink
    {
        private int _failuresRemaining;

        public ConcurrentQueue<AcknowledgeSessionStartRequest> StartAcks { get; } = new();
        public ConcurrentQueue<AcknowledgeSessionStopRequest> StopAcks { get; } = new();
        public ConcurrentQueue<AcknowledgeEditorRuntimeQuiesceRequest> QuiesceAcks { get; } = new();
        public ConcurrentQueue<AcknowledgeEditorRuntimeLaunchRequest> LaunchAcks { get; } = new();
        public ConcurrentQueue<AcknowledgeEditorRuntimeRequestRequest> RequestAcks { get; } = new();
        public ConcurrentQueue<object> Attempts { get; } = new();

        /// <summary>Fails the next acknowledgement attempts with a retryable status.</summary>
        public void FailNextAcknowledgements(int count) => Volatile.Write(ref _failuresRemaining, count);

        /// <summary>Fails every matching acknowledgement attempt with a retryable status.</summary>
        public Func<object, bool> FailWhen { get; set; } = _ => false;

        public async Task<T> WaitForAsync<T>(ConcurrentQueue<T> queue, int count, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            while (queue.Count < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail($"Timed out waiting for {count} acknowledgement(s) of {typeof(T).Name}; saw {queue.Count}.");
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            return queue.ElementAt(count - 1);
        }

        private Task Record<T>(ConcurrentQueue<T> queue, T request) where T : Google.Protobuf.IMessage<T>
        {
            Attempts.Enqueue(request.Clone());
            if (FailWhen(request) || Interlocked.Decrement(ref _failuresRemaining) >= 0)
            {
                return Task.FromException(new RpcException(new Status(StatusCode.Unavailable, "controlled lost acknowledgement")));
            }

            queue.Enqueue(request.Clone());
            return Task.CompletedTask;
        }

        public Task AckStartSessionAsync(AcknowledgeSessionStartRequest request, CancellationToken cancellationToken) => Record(StartAcks, request);
        public Task AckStopSessionAsync(AcknowledgeSessionStopRequest request, CancellationToken cancellationToken) => Record(StopAcks, request);
        public Task AckEditorRuntimeQuiesceAsync(AcknowledgeEditorRuntimeQuiesceRequest request, CancellationToken cancellationToken) => Record(QuiesceAcks, request);
        public Task AckEditorRuntimeLaunchAsync(AcknowledgeEditorRuntimeLaunchRequest request, CancellationToken cancellationToken) => Record(LaunchAcks, request);
        public Task AckEditorRuntimeRequestAsync(AcknowledgeEditorRuntimeRequestRequest request, CancellationToken cancellationToken) => Record(RequestAcks, request);

        public Task<RegisterRunnerResponse> RegisterRunnerAsync(RegisterRunnerRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new RegisterRunnerResponse { Accepted = true });
        public Task<SyncLocalModelsResponse> SyncLocalModelsAsync(SyncLocalModelsRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new SyncLocalModelsResponse());
        public Task<GrantGpuPowerExecutionResponse> GrantGpuPowerExecutionAsync(GrantGpuPowerExecutionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task AckGpuPowerLimitAsync(AcknowledgeGpuPowerLimitRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GrantPromptExecutionResponse> GrantPromptExecutionAsync(GrantPromptExecutionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task ReportPromptEvidenceAsync(ReportPromptEvidenceRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AckSubmitEditorActionAsync(AcknowledgePromptResultRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportModelDownloadAsync(ReportModelDownloadRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportSessionEventsAsync(ReportSessionEventsRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportSessionLogsAsync(ReportSessionLogsRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportRunnerLogsAsync(ReportRunnerLogsRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReportRuntimeTelemetryAsync(ReportRuntimeTelemetryRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
