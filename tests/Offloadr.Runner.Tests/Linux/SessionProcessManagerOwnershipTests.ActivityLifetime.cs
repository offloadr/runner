using System.Collections.Concurrent;
using System.Diagnostics;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public partial class SessionProcessManagerOwnershipTests
{
    [Test]
    public async Task RuntimeActivity_FirstByteRetainsOriginalMetadataBeforeLineCompletion()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireCommand("/bin/sleep");
        var root = Path.Combine(Path.GetTempPath(), $"runner-activity-byte-{Guid.NewGuid():N}");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new ActivitySink((_, _) => { entered.TrySetResult(); return Task.FromResult(RuntimeActivityOutcome.Applied); });
        try
        {
            var (options, identity) = PrepareActivityFixture(root, TimeSpan.FromSeconds(5));
            var isolation = new RecordingIsolationStrategy(identity.UserName, identity.HomeDirectory);
            await using var manager = new SessionProcessManager(sink, options, isolation, new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));
            var command = ActivityStartCommand();
            await manager.StartSessionAsync(command, CancellationToken.None);
            command.SessionId = "changed-after-start";
            command.RuntimeEpoch = 99;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(sink.Observations.Single(), Is.EqualTo(new RuntimeActivityObservation(
                ActivitySessionId, ActivityRuntime(1), false, false, false)));
            await manager.StopSessionAsync(ActivitySessionId, CancellationToken.None);
            Assert.That(isolation.CleanupCallCount, Is.EqualTo(1));
        }
        finally { TryDelete(root); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RuntimeActivity_ExpiryStopsExactChildWhileOriginalRpcAndCleanupAreHeld(bool disposeRoot)
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireCommand("/bin/sleep");
        var root = Path.Combine(Path.GetTempPath(), $"runner-activity-expiry-{Guid.NewGuid():N}");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RuntimeActivityOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new ActivitySink(async (observation, token) =>
        {
            if (observation.Runtime.RuntimeEpoch == 2)
            {
                replacementAccepted.TrySetResult();
                return RuntimeActivityOutcome.Applied;
            }
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            return await release.Task;
        });
        SessionProcessManager? manager = null;
        try
        {
            var (options, identity) = PrepareActivityFixture(root, TimeSpan.FromMilliseconds(500));
            var isolation = new RecordingIsolationStrategy(identity.UserName, identity.HomeDirectory);
            manager = new SessionProcessManager(sink, options, isolation, new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));
            await manager.StartSessionAsync(ActivityStartCommand(), CancellationToken.None);
            using var original = Process.GetProcessById(manager.TryGetTrackedProcessId(ActivitySessionId)!.Value);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await original.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var stop = manager.StopSessionAsync(ActivitySessionId, CancellationToken.None);
            var other = disposeRoot ? manager.DisposeAsync().AsTask() : manager.StopSessionAsync(ActivitySessionId, CancellationToken.None);
            var duplicateDispose = disposeRoot ? manager.DisposeAsync().AsTask() : Task.CompletedTask;
            try
            {
                Assert.Multiple(() =>
                {
                    Assert.That(stop.IsCompleted, Is.False);
                    Assert.That(other.IsCompleted, Is.False);
                    if (disposeRoot) Assert.That(duplicateDispose.IsCompleted, Is.False);
                    Assert.That(isolation.CleanupCallCount, Is.Zero);
                    Assert.That(Directory.Exists(identity.HomeDirectory), Is.True);
                });
                Assert.That(() => manager.StartSessionAsync(ActivityStartCommand(2), CancellationToken.None),
                    Throws.InstanceOf<InvalidOperationException>());
            }
            finally
            {
                release.TrySetResult(RuntimeActivityOutcome.Applied);
                await Task.WhenAll(stop, other, duplicateDispose);
            }
            if (!disposeRoot)
            {
                await manager.StartSessionAsync(ActivityStartCommand(2), CancellationToken.None);
                await replacementAccepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                manager.SignalRuntimeActivity(ActivitySessionId, ActivityRuntime(1));
                using var replacement = Process.GetProcessById(manager.TryGetTrackedProcessId(ActivitySessionId)!.Value);
                Assert.That(replacement.HasExited, Is.False);
                Assert.That(sink.Observations.Count, Is.EqualTo(2));
            }
        }
        finally
        {
            release.TrySetResult(RuntimeActivityOutcome.Applied);
            if (manager is not null) await manager.DisposeAsync();
            TryDelete(root);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RuntimeActivity_StopOrDisposalJoinsOriginalPendingStart(bool disposeRoot)
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = Path.Combine(Path.GetTempPath(), $"runner-activity-start-{Guid.NewGuid():N}");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new ActivitySink((_, _) => throw new AssertionException("cancelled startup must never produce activity"));
        SessionProcessManager? manager = null;
        try
        {
            var (options, identity) = PrepareActivityFixture(root, TimeSpan.FromSeconds(5));
            var isolation = new RecordingIsolationStrategy(identity.UserName, identity.HomeDirectory);
            manager = new SessionProcessManager(sink, options, isolation, new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));
            var start = manager.StartSessionAsync(ActivityStartCommand(), async (_, token) =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.TrySetResult();
                await release.Task;
            }, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = disposeRoot ? manager.DisposeAsync().AsTask() : manager.StopSessionAsync(ActivitySessionId, CancellationToken.None);
            try
            {
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(start.IsCompleted, Is.False);
                Assert.That(stop.IsCompleted, Is.False);
                Assert.That(isolation.CleanupCallCount, Is.Zero);
            }
            finally
            {
                release.TrySetResult();
                try { await start; }
                catch (OperationCanceledException) { }
                await stop;
            }
            Assert.That(start.IsCanceled, Is.True);
            Assert.That(sink.Observations, Is.Empty);
            Assert.That(isolation.CleanupCallCount, Is.EqualTo(1));
        }
        finally
        {
            release.TrySetResult();
            if (manager is not null) await manager.DisposeAsync();
            TryDelete(root);
        }
    }

    [Test]
    public async Task DisposeAsync_DoesNotReplayAFailedStopAfterItsOwnedCleanupCompleted()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireCommand("/bin/sleep");
        var root = Path.Combine(Path.GetTempPath(), $"runner-retired-stop-{Guid.NewGuid():N}");
        SessionProcessManager? manager = null;
        Task? disposal = null;
        try
        {
            var (options, identity) = PrepareActivityFixture(root, TimeSpan.FromSeconds(5));
            var isolation = new RecordingIsolationStrategy(identity.UserName, identity.HomeDirectory);
            manager = new SessionProcessManager(TestRuntimeActivitySink.Accepted, options, isolation, new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));
            await manager.StartSessionAsync(ActivityStartCommand(), CancellationToken.None);
            using var original = Process.GetProcessById(manager.TryGetTrackedProcessId(ActivitySessionId)!.Value);
            var failure = new InvalidOperationException("controlled cleanup callback failure");
            var callbacks = 0;
            var observed = Assert.ThrowsAsync<InvalidOperationException>(() => manager.StopSessionAsync(
                ActivitySessionId,
                (_, _) => ++callbacks == 1 ? Task.FromException(failure) : Task.CompletedTask,
                CancellationToken.None));
            await original.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.SameAs(failure), "the original Stop still reports its failure");
                Assert.That(manager.TryGetTrackedProcessId(ActivitySessionId), Is.Null);
                Assert.That(Directory.Exists(identity.HomeDirectory), Is.False);
                Assert.That(isolation.CleanupCallCount, Is.EqualTo(1));
            });

            TestContext.Out.WriteLine("Original Stop failure and completed process/home/account cleanup assertions passed before disposal.");
            disposal = manager.DisposeAsync().AsTask();
            await disposal;
        }
        finally
        {
            try { if (manager is not null && disposal is null) await manager.DisposeAsync(); }
            finally { TryDelete(root); }
        }
    }

    private const string ActivitySessionId = "ac719111-0000-4000-8000-000000000001";
    private static RuntimeIdentity ActivityRuntime(ulong epoch) => new(1, epoch, "33333333333343338333333333333333");
    private static StartSessionCommand ActivityStartCommand(ulong epoch = 1)
        => new()
        {
            SessionId = ActivitySessionId,
            LifecycleGeneration = 1,
            RuntimeEpoch = epoch,
            RuntimeInstanceId = ActivityRuntime(epoch).RuntimeInstanceId,
            EditorRuntimeKind = "forge-neo"
        };

    private static (SessionProcessOptions Options, PreparedSessionIdentity Identity) PrepareActivityFixture(string root, TimeSpan budget)
    {
        Directory.CreateDirectory(root);
        var entry = Path.Combine(root, "editor.sh");
        File.WriteAllText(entry, "#!/bin/sh\nprintf x\nexec /bin/sleep 300\n");
        MakeExecutable(entry);
        var options = new SessionProcessOptions
        {
            SessionRoot = Path.Combine(root, "sessions"),
            EntryPointPath = entry,
            WorkingDirectory = root,
            BundledCustomNodesSeedPath = Path.Combine(root, "seed-custom_nodes"),
            BundledInputSeedPath = Path.Combine(root, "seed-input"),
            SeedVirtualEnvPath = CreateEmptySeedVirtualEnv(root),
            UvBinaryPath = FakeUvBinaryPath(root),
            ComfyPort = 8188,
            ShutdownGracePeriod = TimeSpan.FromMilliseconds(50),
            ReadyTimeout = budget,
            ReadyHost = "127.0.0.1",
            RuntimeKind = "forge-neo"
        };
        return (options, new PreparedSessionIdentity(string.Empty, Path.Combine(options.SessionRoot, ActivitySessionId), true));
    }

    private sealed class ActivitySink(Func<RuntimeActivityObservation, CancellationToken, Task<RuntimeActivityOutcome>> report) : IRuntimeActivitySink
    {
        public ConcurrentQueue<RuntimeActivityObservation> Observations { get; } = new();
        public Task<RuntimeActivityOutcome> ReportAsync(RuntimeActivityObservation observation, CancellationToken token)
        {
            Observations.Enqueue(observation);
            return report(observation, token);
        }
    }
}
