using Grpc.Core;
using Offloadr.Common.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public sealed class GpuPowerCommandExecutorTests
{
    [Test]
    public async Task DuplicateDeliveryAndLostAcknowledgement_RetryOriginalResultWithoutRepeatingExecution()
    {
        var runner = new PhysicalRunner();
        var executor = Executor(runner);
        var command = Command();
        var grants = 0;
        var acknowledgements = new List<AcknowledgeGpuPowerLimitRequest>();
        Task<GrantGpuPowerExecutionResponse> Grant(GrantGpuPowerExecutionRequest request, CancellationToken token)
        { grants++; return Task.FromResult(new GrantGpuPowerExecutionResponse { MayExecute = true }); }
        Task Ack(AcknowledgeGpuPowerLimitRequest result, CancellationToken token)
        {
            acknowledgements.Add(result.Clone());
            return acknowledgements.Count == 1 ? Task.FromException(new RpcException(new(StatusCode.Unavailable, "controlled lost ack"))) : Task.CompletedTask;
        }
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => executor.HandleAsync(command.Clone(), "runner", () => "session", () => 9, Grant, Ack, default)));
        Assert.That(grants, Is.EqualTo(1));
        Assert.That(runner.Calls, Is.EqualTo(1));
        Assert.That(acknowledgements, Has.Count.EqualTo(2));
        Assert.That(acknowledgements[0], Is.EqualTo(acknowledgements[1]));
        Assert.That(acknowledgements[0].Target.Generation, Is.EqualTo(9));
        Assert.That(acknowledgements[0].Outcome, Is.EqualTo(GpuPowerOutcome.Applied));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DeniedOrCommittedLostExecutionGrant_NeverCallsPhysicalCommand(bool lost)
    {
        var runner = new PhysicalRunner(); var executor = Executor(runner); var acks = 0; var grants = 0;
        await executor.HandleAsync(Command(), "runner", () => "session", () => 9,
            (_, _) => ++grants == 1 && lost ? Task.FromException<GrantGpuPowerExecutionResponse>(new IOException("committed grant response lost"))
                : Task.FromResult(new GrantGpuPowerExecutionResponse { MayExecute = false }),
            (_, _) => { acks++; return Task.CompletedTask; }, default);
        Assert.That(runner.Calls, Is.Zero);
        Assert.That(acks, Is.Zero);
        Assert.That(grants, Is.EqualTo(lost ? 2 : 1));
    }

    [Test]
    public async Task UncommittedGrantFailureAndRedelivery_RetryOriginalGrantThenExecuteOnce()
    {
        var runner = new PhysicalRunner(); var executor = Executor(runner); var acks = 0;
        var grants = new List<GrantGpuPowerExecutionRequest>();
        Task<GrantGpuPowerExecutionResponse> Grant(GrantGpuPowerExecutionRequest request, CancellationToken token)
        {
            grants.Add(request.Clone());
            return grants.Count == 1
                ? Task.FromException<GrantGpuPowerExecutionResponse>(new RpcException(new(StatusCode.Unavailable, "not committed")))
                : Task.FromResult(new GrantGpuPowerExecutionResponse { MayExecute = true });
        }
        Task Handle() => executor.HandleAsync(Command(), "runner", () => "session", () => 9, Grant,
            (_, _) => { acks++; return Task.CompletedTask; }, default);
        await Task.WhenAll(Handle(), Handle(), Handle());
        await Handle();
        Assert.That(grants, Has.Count.EqualTo(2));
        Assert.That(grants[0], Is.EqualTo(grants[1]));
        Assert.That(runner.Calls, Is.EqualTo(1));
        Assert.That(acks, Is.EqualTo(1));
    }

    [Test]
    public async Task GrantRetry_StopsOnPermanentRejection()
    {
        var runner = new PhysicalRunner(); var executor = Executor(runner); var grants = 0;
        Task Handle() => executor.HandleAsync(Command(), "runner", () => "session", () => 9,
            (_, _) => { grants++; throw new RpcException(new(StatusCode.FailedPrecondition, "target stopped")); },
            (_, _) => throw new AssertionException("No acknowledgement without execution permission"), default);
        await Handle(); await Handle();
        Assert.That(grants, Is.EqualTo(1));
        Assert.That(runner.Calls, Is.Zero);
    }

    [Test]
    public async Task ReplacementActivation_WaitsForPhysicalExecutionButNotAcknowledgementRetry()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new PhysicalRunner { Execute = async () => { started.SetResult(); await finish.Task; return new(true, "", "", 0); } };
        var executor = Executor(runner);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var work = executor.HandleAsync(Command(), "runner", () => "session", () => 9,
            (_, _) => Task.FromResult(new GrantGpuPowerExecutionResponse { MayExecute = true }),
            (_, _) => Task.FromException(new RpcException(new(StatusCode.Unavailable, "lost ack"))), shutdown.Token);
        await started.Task.WaitAsync(shutdown.Token);
        var activated = false;
        var activation = executor.ActivateSessionAsync(() => activated = true, shutdown.Token);
        Assert.That(activated, Is.False);
        finish.SetResult();
        await activation.WaitAsync(shutdown.Token);
        Assert.That(activated, Is.True);
        Assert.That(work.IsCompleted, Is.False);
        shutdown.Cancel(); await work;
        Assert.That(runner.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task StopCanCancelReplacementWhileOlderPhysicalExecutionStillHoldsTheFence()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new PhysicalRunner { Execute = async () => { started.SetResult(); await finish.Task; return new(true, "", "", 0); } };
        var executor = Executor(runner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var work = executor.HandleAsync(Command(), "runner", () => "session", () => 9,
            (_, _) => Task.FromResult(new GrantGpuPowerExecutionResponse { MayExecute = true }),
            (_, _) => Task.CompletedTask, timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var activated = false;
        var activation = executor.ActivateSessionAsync(() => activated = true, startup.Token);
        startup.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await activation.WaitAsync(timeout.Token));
        Assert.That(work.IsCompleted, Is.False);
        finish.SetResult();
        await work.WaitAsync(timeout.Token);
        Assert.That(activated, Is.False);
    }

    [TestCase("bounds")]
    [TestCase("missing")]
    [TestCase("generation")]
    [TestCase("session")]
    public async Task FreshPhysicalProbeAndIdentityFence_RejectBeforeSideEffect(string schedule)
    {
        var runner = new PhysicalRunner(); var generation = 9UL; var session = "session";
        var probe = new Probe
        {
            Output = schedule == "bounds" ? "9, 100, 200, 44, 35, 85, 180, 100, 150, 180"
                : schedule == "missing" ? "9, 100, 200, 44, 35, 85, 250, N/A, N/A, N/A" : Probe.Valid,
            OnCapture = () => { if (schedule == "generation") generation = 10; if (schedule == "session") session = "replacement"; },
        };
        var result = await new GpuPowerLimitService(new RuntimeTelemetryService(probe), runner)
            .ApplyAsync(Command(), () => session, () => generation, default);
        Assert.That(result.Outcome, Is.EqualTo(GpuPowerOutcome.Rejected));
        Assert.That(runner.Calls, Is.Zero);
    }

    [Test]
    public async Task AmbiguousPhysicalFailure_RetainsUnknownAndNeverRepeatsCommand()
    {
        var runner = new PhysicalRunner { Execute = () => Task.FromResult(new GpuPowerLimitCommandResult(false, "", "timeout", -1, MayHaveApplied: true)) };
        var executor = Executor(runner); AcknowledgeGpuPowerLimitRequest? ack = null;
        Task Handle() => executor.HandleAsync(Command(), "runner", () => "session", () => 9,
            (_, _) => Task.FromResult(new GrantGpuPowerExecutionResponse { MayExecute = true }),
            (result, _) => { ack = result; return Task.CompletedTask; }, default);
        await Handle(); await Handle();
        Assert.That(ack!.Outcome, Is.EqualTo(GpuPowerOutcome.Unknown));
        Assert.That(runner.Calls, Is.EqualTo(1));
    }

    [TestCase(3, "Changing power management limit is not supported in current scope")]
    [TestCase(4, "Insufficient Permissions")]
    public async Task CompletedNativeRejection_DoesNotLeaveAnUnknownPowerOperation(int exitCode, string detail)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("The runner's native process fixture requires Linux.");
            return;
        }
        var directory = Path.Combine(Path.GetTempPath(), $"offloadr-power-rejection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "nvidia-smi");
            await File.WriteAllTextAsync(executable, $"#!/bin/sh\nprintf '%s\\n' '{detail}' >&2\nexit {exitCode}\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var executor = new GpuPowerCommandExecutor(new(new(new Probe()), new NvidiaSmiGpuPowerLimitCommandRunner(executable)));
            var acknowledgements = new List<AcknowledgeGpuPowerLimitRequest>();
            await executor.HandleAsync(Command(), "runner", () => "session", () => 9,
                (_, _) => Task.FromResult(new GrantGpuPowerExecutionResponse { MayExecute = true }),
                (result, _) => { acknowledgements.Add(result); return Task.CompletedTask; }, default);
            Assert.That(acknowledgements, Has.Count.EqualTo(1));
            Assert.That(acknowledgements[0].Outcome, Is.EqualTo(GpuPowerOutcome.Rejected));
            Assert.That(acknowledgements[0].Message, Does.Contain(detail));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public async Task ShutdownDrainsPhysicalCleanupAndRejectsNewDeliveries()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new PhysicalRunner
        {
            ExecuteCancellable = async token =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException)
                {
                    cancelling.SetResult();
                    await cleanup.Task;
                    throw;
                }
                throw new AssertionException("Physical work requires cancellation");
            },
        };
        var executor = Executor(runner); var grants = 0; var acks = 0;
        Task Handle(SetGpuPowerLimitCommand command) => executor.HandleAsync(command, "runner", () => "session", () => 9,
            (_, _) => { grants++; return Task.FromResult(new GrantGpuPowerExecutionResponse { MayExecute = true }); },
            (_, _) => { acks++; return Task.CompletedTask; }, default);
        var work = Handle(Command());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var draining = executor.CancelAndDrainAsync();
        try
        {
            await cancelling.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(draining.IsCompleted, Is.False);
            Assert.That(work.IsCompleted, Is.False);
            var replacement = Command(); replacement.CommandId = "new-delivery";
            await Handle(replacement);
            Assert.That(grants, Is.EqualTo(1));
            Assert.CatchAsync<OperationCanceledException>(() => executor.ActivateSessionAsync(
                () => Assert.Fail("A closing runner cannot activate a replacement"), default));
        }
        finally { cleanup.TrySetResult(); await draining.WaitAsync(TimeSpan.FromSeconds(5)); }
        await work;
        await executor.CancelAndDrainAsync();
        Assert.That(runner.Calls, Is.EqualTo(1));
        Assert.That(acks, Is.Zero);
    }

    [TestCase("grant")]
    [TestCase("acknowledgement")]
    public async Task ShutdownCancelsNetworkWaitsWithoutRepeatingPhysicalWork(string stage)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new PhysicalRunner(); var executor = Executor(runner);
        async Task<GrantGpuPowerExecutionResponse> Grant(GrantGpuPowerExecutionRequest request, CancellationToken token)
        {
            if (stage == "grant") { started.SetResult(); await Task.Delay(Timeout.Infinite, token); }
            return new() { MayExecute = true };
        }
        async Task Ack(AcknowledgeGpuPowerLimitRequest result, CancellationToken token)
        { started.SetResult(); await Task.Delay(Timeout.Infinite, token); }
        var work = executor.HandleAsync(Command(), "runner", () => "session", () => 9, Grant, Ack, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await executor.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await work;
        Assert.That(runner.Calls, Is.EqualTo(stage == "grant" ? 0 : 1));
    }

    private static SetGpuPowerLimitCommand Command() => new()
    {
        CommandId = "operation",
        SessionId = "session",
        TargetWatts = 200,
        Target = new() { EditorLifecycleId = "editor", EditorGeneration = 2, LifecycleId = "gpu", AllocationId = "allocation", Generation = 9 },
    };
    private static GpuPowerCommandExecutor Executor(PhysicalRunner runner) => new(new(new(new Probe()), runner));
    private sealed class PhysicalRunner : IGpuPowerLimitCommandRunner
    {
        public int Calls { get; private set; }
        public Func<Task<GpuPowerLimitCommandResult>> Execute { get; init; } = () => Task.FromResult(new GpuPowerLimitCommandResult(true, "", "", 0));
        public Func<CancellationToken, Task<GpuPowerLimitCommandResult>>? ExecuteCancellable { get; init; }
        public Task<GpuPowerLimitCommandResult> SetPowerLimitAsync(double targetWatts, CancellationToken cancellationToken)
        { Calls++; return ExecuteCancellable?.Invoke(cancellationToken) ?? Execute(); }
    }
    private sealed class Probe : IRuntimeTelemetryProbe
    {
        public const string Valid = "9, 100, 200, 44, 35, 85, 200, 100, 250, 350";
        public string Output { get; init; } = Valid;
        public Action? OnCapture { get; init; }
        public string CaptureRuntimeTelemetryQuery() { OnCapture?.Invoke(); return Output; }
        public string? ReadMemInfo() => null;
        public bool TryGetDiskSpace(string path, out RuntimeDiskSpaceSnapshot snapshot) { snapshot = default; return false; }
    }
}
