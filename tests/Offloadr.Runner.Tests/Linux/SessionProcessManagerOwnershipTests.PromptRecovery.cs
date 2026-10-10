using System.Net;
using Offloadr.EditorRuntime.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public partial class SessionProcessManagerOwnershipTests
{
    [Test]
    public async Task ReplacementCannotReuseItsHomeWhileOldCleanupIsStillRunning()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await PromptRuntimeFixture.CreateAsync(cleanup: async () =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        var stopping = fixture.Manager.StopSessionAsync(fixture.Start.SessionId, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Start.RuntimeEpoch++;
            fixture.Start.RuntimeInstanceId = Guid.NewGuid().ToString("n");
            Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.StartSessionAsync(fixture.Start, CancellationToken.None));
        }
        finally { release.TrySetResult(); await stopping; }
        await fixture.Manager.StartSessionAsync(fixture.Start, CancellationToken.None);
        Assert.That(fixture.Manager.TryGetSessionPaths(fixture.Start.SessionId, out var paths), Is.True);
        Assert.That(Directory.Exists(paths.HomeDirectory), Is.True);
        Assert.That((await fixture.Manager.SubmitEditorActionAsync(fixture.Command(), CancellationToken.None)).StatusCode, Is.EqualTo(200));
    }

    [Test]
    public async Task OverlappingStopAwaitsTheInFlightStop()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await PromptRuntimeFixture.CreateAsync(cleanup: async () =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        var first = fixture.Manager.StopSessionAsync(fixture.Start.SessionId, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = fixture.Manager.StopSessionAsync(fixture.Start.SessionId, CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            Assert.That(second.IsCompleted, Is.False, "A second Stop must not report completion while the first is still stopping.");

            release.TrySetResult();
            await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(fixture.Manager.GetActiveSessionId(), Is.Empty);
        }
        finally
        {
            release.TrySetResult();
            await first;
        }
    }

    [Test]
    public async Task IdentityCheckedStopLeavesADifferentRuntimeRunning()
    {
        await using var fixture = await PromptRuntimeFixture.CreateAsync();
        var start = fixture.Start;
        var tracked = new RuntimeIdentity(start.LifecycleGeneration, start.RuntimeEpoch, start.RuntimeInstanceId);
        var cleanupCalls = 0;
        Task CountCleanup(string _, CancellationToken __)
        {
            cleanupCalls++;
            return Task.CompletedTask;
        }

        var stale = await fixture.Manager.StopSessionIfRuntimeMatchesAsync(
            start.SessionId, tracked with { RuntimeEpoch = start.RuntimeEpoch - 1 }, CountCleanup, CancellationToken.None);
        Assert.That(stale, Is.False);
        Assert.That(cleanupCalls, Is.Zero);
        Assert.That(fixture.Manager.TryGetRuntimeIdentity(start.SessionId, out var current), Is.True);
        Assert.That(current.SameRuntime(tracked), Is.True);

        // The same runtime spelled with dashes still matches.
        var exact = tracked with { RuntimeInstanceId = Guid.Parse(start.RuntimeInstanceId).ToString("D") };
        Assert.That(await fixture.Manager.StopSessionIfRuntimeMatchesAsync(start.SessionId, exact, CountCleanup, CancellationToken.None), Is.True);
        Assert.That(cleanupCalls, Is.EqualTo(1));
        Assert.That(fixture.Manager.TryGetRuntimeIdentity(start.SessionId, out _), Is.False);
    }

    [Test]
    public async Task IsTrackedRuntimeMatchesOnlyTheExactLiveChild()
    {
        await using var fixture = await PromptRuntimeFixture.CreateAsync();
        var start = fixture.Start;
        var tracked = new RuntimeIdentity(start.LifecycleGeneration, start.RuntimeEpoch, Guid.Parse(start.RuntimeInstanceId).ToString("D"));

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Manager.IsTrackedRuntime(start.SessionId, tracked), Is.True);
            Assert.That(fixture.Manager.IsTrackedRuntime(start.SessionId, tracked with { RuntimeEpoch = start.RuntimeEpoch + 1 }), Is.False);
            Assert.That(fixture.Manager.IsTrackedRuntime(Guid.NewGuid().ToString("n"), tracked), Is.False);
        });

        await fixture.Manager.StopSessionAsync(start.SessionId, CancellationToken.None);
        Assert.That(fixture.Manager.IsTrackedRuntime(start.SessionId, tracked), Is.False);
    }

    [TestCase("generation")]
    [TestCase("epoch")]
    [TestCase("instance")]
    public async Task NativeInvocationRejectsDifferentChildIdentityBeforeHttp(string mismatch)
    {
        await using var fixture = await PromptRuntimeFixture.CreateAsync();
        var command = fixture.Command();
        if (mismatch == "generation") command.Target.GpuGeneration++;
        else if (mismatch == "epoch") command.Target.RuntimeEpoch++;
        else command.Target.RuntimeInstanceId = Guid.NewGuid().ToString("n");
        Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.SubmitEditorActionAsync(command, CancellationToken.None));
        Assert.That(fixture.Http.Requests, Is.Empty);
    }

    [TestCase("target-session")]
    [TestCase("target-instance")]
    [TestCase("command-session")]
    public async Task MalformedIdentityIsRejectedAsValidationFailureBeforeHttp(string malformed)
    {
        await using var fixture = await PromptRuntimeFixture.CreateAsync();
        var command = fixture.Command();
        if (malformed == "target-session") command.Target.RunnerSessionId = "not-a-uuid";
        else if (malformed == "target-instance") command.Target.RuntimeInstanceId = "not-a-uuid";
        else command.SessionId = "not-a-uuid";

        var failure = Assert.CatchAsync(() => fixture.Manager.SubmitEditorActionAsync(command, CancellationToken.None));
        var recoveryFailure = Assert.CatchAsync(() => fixture.Manager.RecoverPromptAsync(
            command, "runner-1", (_, _) => Task.CompletedTask, CancellationToken.None));

        Assert.That(failure, Is.InstanceOf<InvalidOperationException>().Or.InstanceOf<ArgumentException>());
        Assert.That(recoveryFailure, Is.InstanceOf<InvalidOperationException>().Or.InstanceOf<ArgumentException>());
        Assert.That(fixture.Http.Requests, Is.Empty);
    }

    [Test]
    public async Task ForgeLocalPreparationFailureDoesNotMarkNativeRequestAttempt()
    {
        await using var fixture = await PromptRuntimeFixture.CreateAsync("forge-neo");
        var command = fixture.Command();
        command.HttpPath = "queue/join";
        command.PromptJson = "{";
        command.ArtifactReferences.Add(new Offloadr.Common.V1.PromptArtifactReference
        {
            Placeholder = "offloadr://forge-artifact/input",
            Filename = "input.png",
            Type = "temp",
            Subfolder = "forge-queue/input"
        });
        var nativeAttempts = 0;
        var failure = Assert.CatchAsync(() => fixture.Manager.SubmitEditorActionAsync(
            command, responseEventSink: null, CancellationToken.None, () => nativeAttempts++));
        Assert.That(failure, Is.InstanceOf<System.Text.Json.JsonException>());
        Assert.That(nativeAttempts, Is.Zero);
        Assert.That(fixture.Http.Requests, Is.Empty);
    }

    [Test]
    public async Task GenerationRequestMarksNativeRequestAttempt()
    {
        await using var fixture = await PromptRuntimeFixture.CreateAsync();
        var nativeAttempts = 0;
        var result = await fixture.Manager.SubmitEditorActionAsync(
            fixture.Command(), responseEventSink: null, CancellationToken.None, () => nativeAttempts++);
        Assert.That(result.StatusCode, Is.EqualTo(200));
        Assert.That(nativeAttempts, Is.EqualTo(1));
        Assert.That(fixture.Http.Requests.Select(request => request.Path), Is.EqualTo(new[] { "/api/prompt" }));
    }

    [Test]
    public async Task OversizedSynchronousResponseKeepsStatusAndOmitsBody()
    {
        await using var fixture = await PromptRuntimeFixture.CreateAsync();
        fixture.Http.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[ComfyRuntimeTransportLimits.MaxResponseBodyBytes + 1]),
        });
        var result = await fixture.Manager.SubmitEditorActionAsync(fixture.Command(), responseEventSink: null, CancellationToken.None);
        Assert.That(result.StatusCode, Is.EqualTo(200));
        Assert.That(result.Success, Is.True);
        Assert.That(result.BodyOmitted, Is.True);
        Assert.That(result.Body, Is.Empty);
    }

    [Test]
    public async Task ForgePreflightFailureMarksNativeRequestAttempt()
    {
        await using var fixture = await PromptRuntimeFixture.CreateAsync("forge-neo");
        var command = fixture.Command();
        command.HttpPath = "queue/join";
        command.PreflightRequests.Add(new Offloadr.Common.V1.PromptHttpRequest { Method = "POST", Path = "api/preflight", Body = "{}" });
        fixture.Http.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var nativeAttempts = 0;
        Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.SubmitEditorActionAsync(
            command, responseEventSink: null, CancellationToken.None, () => nativeAttempts++));
        Assert.That(nativeAttempts, Is.EqualTo(1));
        Assert.That(fixture.Http.Requests.Select(request => request.Path), Is.EqualTo(new[] { "/api/preflight" }));
    }

    [Test]
    public async Task StopCancelsNativeIoAndOldCommandCannotReachReplacementOnSamePort()
    {
        await using var fixture = await PromptRuntimeFixture.CreateAsync();
        var original = fixture.Command();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.Respond = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new AssertionException("Stop did not cancel native I/O");
        };
        var pending = fixture.Manager.SubmitEditorActionAsync(original, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Manager.StopSessionAsync(fixture.Start.SessionId, CancellationToken.None);
        Assert.ThrowsAsync<TaskCanceledException>(async () => await pending);

        fixture.Start.RuntimeEpoch++;
        fixture.Start.RuntimeInstanceId = Guid.NewGuid().ToString("n");
        await fixture.Manager.StartSessionAsync(fixture.Start, CancellationToken.None);
        fixture.Http.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xff, 0x01]) });
        Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.SubmitEditorActionAsync(original, CancellationToken.None));
        var result = await fixture.Manager.SubmitEditorActionAsync(fixture.Command(), CancellationToken.None);
        Assert.That(fixture.Http.Requests, Has.Count.EqualTo(2));
        Assert.That(result.Body, Is.EqualTo(new byte[] { 0xff, 0x01 }));
    }

    [Test]
    public async Task OldInterruptCannotReachAReplacementChild()
    {
        await using var fixture = await PromptRuntimeFixture.CreateAsync();
        var command = new RelayEditorRuntimeRequestCommand
        {
            SessionId = fixture.Start.SessionId,
            LifecycleGeneration = fixture.Start.LifecycleGeneration,
            RuntimeEpoch = fixture.Start.RuntimeEpoch + 1,
            RuntimeInstanceId = fixture.Start.RuntimeInstanceId,
            Kind = EditorRuntimeRequestKind.Control,
            Method = "POST",
            Path = "api/interrupt",
        };
        Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.RelayEditorRuntimeRequestAsync(command, CancellationToken.None));
        Assert.That(fixture.Http.Requests, Is.Empty);
    }

    [Test]
    public async Task StartForAnotherEditorRuntimeIsRejectedBeforeProvisioning()
    {
        await using var fixture = await PromptRuntimeFixture.CreateAsync();
        var misrouted = fixture.Start.Clone();
        misrouted.SessionId = Guid.NewGuid().ToString("n");
        misrouted.EditorRuntimeKind = "forge-neo";

        var error = Assert.ThrowsAsync<ArgumentException>(() => fixture.Manager.StartSessionAsync(misrouted, CancellationToken.None));
        Assert.Multiple(() =>
        {
            Assert.That(error!.Message, Does.Contain("forge-neo"));
            Assert.That(fixture.Manager.GetActiveSessionId(), Is.EqualTo(fixture.Start.SessionId));
            Assert.That(fixture.Manager.TryGetRuntimeIdentity(misrouted.SessionId, out _), Is.False);
        });
    }

    [Test]
    public async Task ReplacementStartWaitsForTheExitedRuntimesCleanup()
    {
        await using var fixture = await PromptRuntimeFixture.CreateAsync();
        var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Manager.UnexpectedSessionExitCleanup = async (_, _, _, _) =>
        {
            hookEntered.TrySetResult();
            await releaseHook.Task;
        };

        var pid = fixture.Manager.TryGetTrackedProcessId(fixture.Start.SessionId);
        Assert.That(pid, Is.Not.Null);
        System.Diagnostics.Process.GetProcessById(pid!.Value).Kill();
        await hookEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The old runtime's exit cleanup is still running when the replacement arrives.
        var replacement = fixture.Start.Clone();
        replacement.RuntimeEpoch++;
        replacement.RuntimeInstanceId = Guid.NewGuid().ToString("n");
        var start = fixture.Manager.StartSessionAsync(replacement, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.That(start.IsCompleted, Is.False, "The replacement waits for the exited runtime's cleanup.");

        releaseHook.TrySetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(fixture.Manager.TryGetRuntimeIdentity(replacement.SessionId, out var tracked), Is.True);
        Assert.That(tracked, Is.EqualTo(new RuntimeIdentity(replacement.LifecycleGeneration, replacement.RuntimeEpoch, replacement.RuntimeInstanceId)));
    }

    [Test]
    public async Task StartForTheRuntimeThatJustExitedIsRefused()
    {
        await using var fixture = await PromptRuntimeFixture.CreateAsync();
        var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Manager.UnexpectedSessionExitCleanup = async (_, _, _, _) =>
        {
            hookEntered.TrySetResult();
            await releaseHook.Task;
        };
        System.Diagnostics.Process.GetProcessById(fixture.Manager.TryGetTrackedProcessId(fixture.Start.SessionId)!.Value).Kill();
        await hookEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            // A redelivered start for the runtime that just died must not bring it back.
            Assert.That(
                async () => await fixture.Manager.StartSessionAsync(fixture.Start.Clone(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)),
                Throws.InvalidOperationException);
        }
        finally
        {
            releaseHook.TrySetResult();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StartDuringAStopIsRefusedRatherThanWaitingForIt(bool sameRuntime)
    {
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await PromptRuntimeFixture.CreateAsync(cleanup: async () =>
        {
            cleanupEntered.TrySetResult();
            await releaseCleanup.Task;
        });
        var stopping = fixture.Manager.StopSessionAsync(fixture.Start.SessionId, CancellationToken.None);
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var start = fixture.Start.Clone();
        if (!sameRuntime)
        {
            start.RuntimeEpoch++;
            start.RuntimeInstanceId = Guid.NewGuid().ToString("n");
        }

        try
        {
            Assert.That(
                async () => await fixture.Manager.StartSessionAsync(start, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)),
                Throws.InvalidOperationException);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task DuplicateStartMustMatchTheExactTrackedChild()
    {
        await using var fixture = await PromptRuntimeFixture.CreateAsync();
        await fixture.Manager.StartSessionAsync(fixture.Start, CancellationToken.None);
        var replacement = fixture.Start.Clone();
        replacement.RuntimeEpoch++;
        replacement.RuntimeInstanceId = Guid.NewGuid().ToString("n");
        Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.StartSessionAsync(replacement, CancellationToken.None));
        var result = await fixture.Manager.SubmitEditorActionAsync(fixture.Command(), CancellationToken.None);
        Assert.That(result.StatusCode, Is.EqualTo(200));
    }

    [Test]
    public async Task ComfyRecoveryOnlyReadsHistoryAndQueueAndRetainsExactTarget()
    {
        await using var fixture = await PromptRuntimeFixture.CreateAsync();
        var command = fixture.Command();
        command.RecoveryOnly = true;
        command.NativeIdentifiers = new() { PromptId = Guid.NewGuid().ToString("D") };
        var evidence = new List<ReportPromptEvidenceRequest>();
        await fixture.Manager.RecoverPromptAsync(command, command.Target.RunnerId,
            (request, _) => { evidence.Add(request); return Task.CompletedTask; }, CancellationToken.None);
        Assert.That(fixture.Http.Requests.Select(request => request.Method), Is.EqualTo(new[] { "GET", "GET" }));
        Assert.That(fixture.Http.Requests.Select(request => request.Path), Is.EqualTo(new[] { "/api/history/" + command.NativeIdentifiers.PromptId, "/api/queue" }));
        Assert.That(evidence, Has.Count.EqualTo(2));
        Assert.That(evidence.All(item => item.Target.Equals(command.Target) && item.CommandId == command.CommandId), Is.True);
    }

    [Test]
    public async Task NativeForgeObserverDoesNotPollAGenerationsProgressThroughItsSharedStream()
    {
        await using var fixture = await PromptRuntimeFixture.CreateAsync("forge-neo");
        var command = fixture.Command();
        command.HttpMethod = "GET";
        command.HttpPath = "queue/data";
        command.HttpQuery = "session_hash=captured-session";
        command.PromptJson = string.Empty;
        command.NativeIdentifiers = new() { SessionHash = "captured-session" };
        await fixture.Manager.SubmitEditorActionAsync(command, (_, _, _) => Task.CompletedTask, CancellationToken.None);
        Assert.That(fixture.Http.Requests.Select(request => request.Path), Is.EqualTo(new[] { "/queue/data" }));
    }

    [Test]
    public async Task ForgeObserverRecoveryCannotOpenAnotherConsumingStream()
    {
        await using var fixture = await PromptRuntimeFixture.CreateAsync("forge-neo");
        var command = fixture.Command();
        command.HttpMethod = "GET";
        command.HttpPath = "queue/data";
        command.NativeIdentifiers = new() { SessionHash = "captured-session" };
        command.RecoveryOnly = true;
        await fixture.Manager.RecoverPromptAsync(command, command.Target.RunnerId,
            (_, _) => throw new AssertionException("No native observation could have been produced"), CancellationToken.None);
        Assert.That(fixture.Http.Requests, Is.Empty);
    }

    private sealed class PromptRuntimeFixture(string root, SessionProcessManager manager, PromptHttpHandler http, StartSessionCommand start) : IAsyncDisposable
    {
        public SessionProcessManager Manager { get; } = manager;
        public PromptHttpHandler Http { get; } = http;
        public StartSessionCommand Start { get; } = start;

        public SubmitPromptCommand Command() => new()
        {
            CommandId = Guid.NewGuid().ToString("n"),
            SubmissionId = Guid.NewGuid().ToString("n"),
            SessionId = Start.SessionId,
            EditorSid = "editor",
            Owner = "alice",
            EditorRuntimeKind = Start.EditorRuntimeKind,
            HttpMethod = "POST",
            HttpPath = "api/prompt",
            PromptJson = "{}",
            ContentType = "application/json",
            Target = new()
            {
                EditorLifecycleId = "cpu",
                EditorGeneration = 1,
                CpuRuntimeGeneration = 1,
                CpuRuntimeInstanceId = Guid.NewGuid().ToString("n"),
                GpuLifecycleId = "gpu",
                GpuGeneration = Start.LifecycleGeneration,
                AllocationId = "allocation",
                RuntimeEpoch = Start.RuntimeEpoch,
                RuntimeInstanceId = Start.RuntimeInstanceId,
                RunnerId = "11111111111141118111111111111111",
                RunnerSessionId = Start.SessionId,
            },
        };

        public static async Task<PromptRuntimeFixture> CreateAsync(string kind = "comfyui", Func<Task>? cleanup = null)
        {
            LinuxTestPrerequisites.RequireLinux();
            var root = Path.Combine(Path.GetTempPath(), "runner-prompt-runtime-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(root);
            var entry = Path.Combine(root, "runtime.sh");
            File.WriteAllText(entry, "#!/bin/sh\nexec /bin/sleep 600\n");
            MakeExecutable(entry);
            var options = new SessionProcessOptions
            {
                SessionRoot = Path.Combine(root, "sessions"),
                EntryPointPath = entry,
                WorkingDirectory = root,
                BundledCustomNodesSeedPath = Path.Combine(root, "seed-custom"),
                BundledInputSeedPath = Path.Combine(root, "seed-input"),
                SeedVirtualEnvPath = CreateEmptySeedVirtualEnv(root),
                UvBinaryPath = FakeUvBinaryPath(root),
                RuntimeKind = kind,
                ComfyPort = 8188,
                ReadyHost = "127.0.0.1",
                ShutdownGracePeriod = TimeSpan.FromSeconds(2),
                ReadyTimeout = TimeSpan.FromSeconds(5),
            };
            var start = new StartSessionCommand
            {
                SessionId = Guid.NewGuid().ToString("n"),
                User = "alice",
                EditorRuntimeKind = kind,
                LifecycleGeneration = 7,
                RuntimeEpoch = 11,
                RuntimeInstanceId = Guid.NewGuid().ToString("n")
            };
            var http = new PromptHttpHandler();
            // This fixture tests process/HTTP fencing, not OS user isolation.
            // Empty UserName inherits the test account; setting even the current
            // name asks .NET to set Unix credentials and requires privileges.
            var manager = new SessionProcessManager(options,
                new RecordingIsolationStrategy(string.Empty, Path.Combine(options.SessionRoot, start.SessionId)) { Cleanup = cleanup },
                new RunnerVfsEnvironmentBuilder(), new PromptSignalRunner(), httpMessageHandler: http);
            var fixture = new PromptRuntimeFixture(root, manager, http, start);
            try { await manager.StartSessionAsync(start, CancellationToken.None); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.StopAllAsync(CancellationToken.None);
            Manager.Dispose();
            TryDelete(root);
        }
    }

    private sealed class PromptHttpHandler : HttpMessageHandler
    {
        public List<(string Method, string Path)> Requests { get; } = [];
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; }
            = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("{}"u8.ToArray()) });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath));
            return Respond(request, cancellationToken);
        }
    }

    private sealed class PromptSignalRunner : ILinuxCommandRunner
    {
        public Task<LinuxCommandResult> RunAsync(LinuxCommand command, CancellationToken cancellationToken)
            => command.FileName == "/bin/kill" ? new LinuxCommandRunner().RunAsync(command, cancellationToken)
                : Task.FromResult(new LinuxCommandResult(0, "", ""));
    }
}
