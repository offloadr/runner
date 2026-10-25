using System.Diagnostics;

namespace Offloadr.Runner.Tests;

public partial class SessionProcessManagerOwnershipTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task FinalSidecarSnapshot_FollowsDescendantQuiescence(bool unexpectedExit)
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireCommand("/bin/sleep");
        var root = Path.Combine(Path.GetTempPath(), $"runner-snapshot-quiescence-{Guid.NewGuid():N}");
        var (options, identity) = PrepareActivityFixture(root, TimeSpan.FromSeconds(5));
        File.WriteAllText(options.EntryPointPath, SurvivorEntryPoint);
        Process? child = null;
        var snapshots = new List<bool>();
        var snapshotTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var isolation = new RecordingIsolationStrategy(identity.UserName, identity.HomeDirectory)
        {
            Quiesce = async () =>
            {
                if (child is null) return;
                await QuiesceSnapshotDescendantAsync(child);
            }
        };
        await using var manager = new SessionProcessManager(TestRuntimeActivitySink.Accepted, options, isolation,
            new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));
        Task SnapshotAsync(string _, CancellationToken __)
        {
            snapshots.Add(SnapshotDescendantCanWrite(child!));
            TestContext.Out.WriteLine($"descendantAliveAtSnapshot={snapshots[^1]}; accountRetired={isolation.CleanupCallCount > 0}; dataPresent={Directory.Exists(identity.HomeDirectory)}");
            Assert.That(isolation.CleanupCallCount, Is.Zero);
            Assert.That(Directory.Exists(identity.HomeDirectory), Is.True);
            snapshotTaken.TrySetResult();
            return Task.CompletedTask;
        }
        try
        {
            await manager.StartSessionAsync(ActivityStartCommand(), CancellationToken.None);
            var childFile = Path.Combine(identity.HomeDirectory, "survivor.pid");
            await WaitForFileAsync(childFile);
            child = Process.GetProcessById(int.Parse(File.ReadAllText(childFile)));
            Assert.That(SnapshotDescendantCanWrite(child), Is.True, "The real child must be alive before the parent exits.");
            if (unexpectedExit)
            {
                manager.UnexpectedSessionExitCleanup = (session, _, _, token) => SnapshotAsync(session, token);
                using var original = Process.GetProcessById(manager.TryGetTrackedProcessId(ActivitySessionId)!.Value);
                original.Kill();
                await original.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await snapshotTaken.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await manager.StopSessionAsync(ActivitySessionId, CancellationToken.None);
            }
            else await manager.StopSessionAsync(ActivitySessionId, SnapshotAsync, CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(snapshots, Is.EqualTo(new[] { false }), "No descendant may keep writing during the final sidecar snapshot.");
                Assert.That(SnapshotDescendantCanWrite(child), Is.False);
                Assert.That(isolation.CleanupCallCount, Is.EqualTo(1));
            });
        }
        finally
        {
            if (child is not null)
            {
                await QuiesceSnapshotDescendantAsync(child);
            }
            await manager.DisposeAsync();
            child?.Dispose();
            TryDelete(root);
        }
    }

    [Test]
    public async Task FailedDescendantQuiescence_RetainsAccountAndDataWithoutTakingSnapshot()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireCommand("/bin/sleep");
        var root = Path.Combine(Path.GetTempPath(), $"runner-snapshot-failure-{Guid.NewGuid():N}");
        var (options, identity) = PrepareActivityFixture(root, TimeSpan.FromSeconds(5));
        File.WriteAllText(options.EntryPointPath, SurvivorEntryPoint);
        Process? child = null;
        var fail = true;
        var snapshots = 0;
        var isolation = new RecordingIsolationStrategy(identity.UserName, identity.HomeDirectory)
        {
            Quiesce = async () =>
            {
                if (child is null) return;
                if (fail) throw new InvalidOperationException("controlled surviving descendant");
                await QuiesceSnapshotDescendantAsync(child);
            }
        };
        await using var manager = new SessionProcessManager(TestRuntimeActivitySink.Accepted, options, isolation,
            new RunnerVfsEnvironmentBuilder(), new RecordingCommandRunner(string.Empty));
        Task SnapshotAsync(string _, CancellationToken __) { snapshots++; return Task.CompletedTask; }
        try
        {
            await manager.StartSessionAsync(ActivityStartCommand(), CancellationToken.None);
            var childFile = Path.Combine(identity.HomeDirectory, "survivor.pid");
            await WaitForFileAsync(childFile);
            child = Process.GetProcessById(int.Parse(File.ReadAllText(childFile)));
            Assert.ThrowsAsync<InvalidOperationException>(() => manager.StopSessionAsync(ActivitySessionId, SnapshotAsync, CancellationToken.None));
            TestContext.Out.WriteLine($"failedQuiescenceSnapshotCount={snapshots}; descendantAlive={SnapshotDescendantCanWrite(child)}");
            Assert.Multiple(() =>
            {
                Assert.That(snapshots, Is.Zero);
                Assert.That(SnapshotDescendantCanWrite(child), Is.True);
                Assert.That(isolation.CleanupCallCount, Is.Zero);
                Assert.That(Directory.Exists(identity.HomeDirectory), Is.True);
            });
            fail = false;
            await manager.StopSessionAsync(ActivitySessionId, SnapshotAsync, CancellationToken.None);
            Assert.That(snapshots, Is.EqualTo(1));
            Assert.That(SnapshotDescendantCanWrite(child), Is.False);
        }
        finally
        {
            fail = false;
            if (child is not null)
            {
                await QuiesceSnapshotDescendantAsync(child);
            }
            await manager.DisposeAsync();
            child?.Dispose();
            TryDelete(root);
        }
    }

    private static async Task QuiesceSnapshotDescendantAsync(Process child)
    {
        if (SnapshotDescendantCanWrite(child)) child.Kill();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (SnapshotDescendantCanWrite(child)) await Task.Delay(10, timeout.Token);
        TestContext.Out.WriteLine($"descendantQuiescedPid={child.Id}; kernelState={SnapshotDescendantState(child)}");
    }

    private static bool SnapshotDescendantCanWrite(Process child)
        => SnapshotDescendantState(child) is not ("gone" or "Z" or "X");

    private static string SnapshotDescendantState(Process child)
    {
        if (child.HasExited) return "gone";
        try
        {
            // Container PID 1 may leave an orphan as a zombie after SIGKILL.
            // Match the production reaper: a zombie cannot write the session files.
            var state = File.ReadLines($"/proc/{child.Id}/status")
                .Single(line => line.StartsWith("State:", StringComparison.Ordinal));
            return state["State:".Length..].TrimStart()[..1];
        }
        catch (FileNotFoundException) { return "gone"; }
        catch (DirectoryNotFoundException) { return "gone"; }
    }

    private const string SurvivorEntryPoint = "#!/bin/sh\n/bin/sh -c 'trap \"\" TERM; exec /bin/sleep 300' >/dev/null 2>&1 &\nprintf '%s\\n' \"$!\" > \"$HOME/survivor.pid\"\nprintf x\nexec /bin/sleep 300\n";
}
