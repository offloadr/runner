using System.Diagnostics;

namespace Offloadr.Runner.Tests;

public class LinuxUserIsolationLinuxTests
{
    [Test]
    public async Task Cleanup_KillsDetachedSessionProcessesBeforeRemovingUser_AndIsIdempotent()
    {
        LinuxTestPrerequisites.RequireRoot();
        LinuxTestPrerequisites.RequireCommand("/usr/sbin/useradd");
        LinuxTestPrerequisites.RequireCommand("/bin/sh");

        var sessionRoot = Path.Combine(Path.GetTempPath(), "runneragent-isolation-tests", Guid.NewGuid().ToString("n"));
        var sessionId = $"reap{Guid.NewGuid():N}"[..12];
        var commandRunner = new LinuxCommandRunner();
        var strategy = new LinuxUserIsolationStrategy(commandRunner);
        PreparedSessionIdentity identity = default;
        try
        {
            identity = await strategy.PrepareAsync(sessionId, sessionRoot, CancellationToken.None);
            Assert.That(identity.UserId, Is.Not.Null.And.Not.Zero);
            var userId = identity.UserId!.Value;

            // The launcher exits at once; its background children are reparented away from
            // any process tree the agent tracks, as a double-forking session would be.
            var launcher = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UserName = identity.UserName,
                WorkingDirectory = "/",
                UseShellExecute = false
            };
            launcher.ArgumentList.Add("-c");
            launcher.ArgumentList.Add("sleep 300 </dev/null >/dev/null 2>&1 & sleep 300 </dev/null >/dev/null 2>&1 & exit 0");
            using (var process = Process.Start(launcher)!)
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            await WaitUntilAsync(() => LinuxProcessReaper.FindOwnedLiveProcesses(userId).Count >= 2);

            await strategy.CleanupAsync(identity, CancellationToken.None);

            var userLookup = await commandRunner.RunAsync(LinuxCommandFactory.CheckUserExists(identity.UserName), CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(LinuxProcessReaper.FindOwnedLiveProcesses(userId), Is.Empty);
                Assert.That(userLookup.ExitCode, Is.Not.Zero);
            });

            Assert.That(async () => await strategy.CleanupAsync(identity, CancellationToken.None), Throws.Nothing);
            Assert.That(await LinuxProcessReaper.KillAllOwnedByAsync(userId, CancellationToken.None), Is.True);
        }
        finally
        {
            if (identity.UserId is { } uid)
            {
                await LinuxProcessReaper.KillAllOwnedByAsync(uid, CancellationToken.None);
            }

            if (!string.IsNullOrEmpty(identity.UserName))
            {
                await commandRunner.RunAsync(LinuxCommandFactory.RemoveUser(identity.UserName), CancellationToken.None);
            }

            try
            {
                Directory.Delete(sessionRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Test]
    public async Task Prepare_KillsProcessesAlreadyRunningAsTheSessionUid()
    {
        var killed = new List<uint>();
        var strategy = new LinuxUserIsolationStrategy(
            new ScriptedCommandRunner(userId: "4242"),
            (userId, _) =>
            {
                killed.Add(userId);
                return Task.FromResult(true);
            });
        var sessionRoot = Path.Combine(Path.GetTempPath(), "runneragent-isolation-tests", Guid.NewGuid().ToString("n"));
        try
        {
            var identity = await strategy.PrepareAsync("session-reused", sessionRoot, CancellationToken.None);
            await strategy.CleanupAsync(identity, CancellationToken.None);
            await strategy.CleanupAsync(identity with { UserId = null }, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(identity.UserId, Is.EqualTo(4242));
                Assert.That(killed, Is.EqualTo(new uint[] { 4242, 4242, 4242 }));
            });
        }
        finally
        {
            try
            {
                Directory.Delete(sessionRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Test]
    public void KillAllOwnedBy_RefusesRoot()
    {
        Assert.That(
            async () => await LinuxProcessReaper.KillAllOwnedByAsync(0, CancellationToken.None),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("Condition was not met in time.");
            }

            await Task.Delay(25);
        }
    }

    /// <summary>Answers `id -u` with a fixed uid and succeeds for every other command.</summary>
    private sealed class ScriptedCommandRunner(string userId) : ILinuxCommandRunner
    {
        public Task<LinuxCommandResult> RunAsync(LinuxCommand command, CancellationToken cancellationToken)
            => Task.FromResult(command.FileName == "/usr/bin/id"
                ? new LinuxCommandResult(0, userId + "\n", string.Empty)
                : new LinuxCommandResult(0, string.Empty, string.Empty));
    }
}
