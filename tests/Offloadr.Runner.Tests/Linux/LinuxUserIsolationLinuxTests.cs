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
        var sessionId = Guid.NewGuid().ToString();
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
            var identity = await strategy.PrepareAsync("5e551011-0000-4000-8000-000000000001", sessionRoot, CancellationToken.None);
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
    public async Task Cleanup_RemovesSharedTemporaryFilesOfTheSessionUid_WithoutFollowingLinks()
    {
        LinuxTestPrerequisites.RequireRoot();
        LinuxTestPrerequisites.RequireCommand("/bin/chown");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-residue-tests", Guid.NewGuid().ToString("n"));
        var sharedTmp = Path.Combine(root, "tmp");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(sharedTmp);
        Directory.CreateDirectory(outside);
        try
        {
            await using var user = await LinuxTestUser.CreateAsync();
            var victim = Path.Combine(outside, "victim.txt");
            await File.WriteAllTextAsync(victim, "keep");
            var victimDirectory = Path.Combine(outside, "victim-dir");
            Directory.CreateDirectory(victimDirectory);
            await File.WriteAllTextAsync(Path.Combine(victimDirectory, "inner.txt"), "keep");

            var ownedFile = Path.Combine(sharedTmp, "owned.bin");
            await File.WriteAllTextAsync(ownedFile, "residue");
            var ownedDirectory = Path.Combine(sharedTmp, "owned-dir");
            Directory.CreateDirectory(ownedDirectory);
            await File.WriteAllTextAsync(Path.Combine(ownedDirectory, "nested.bin"), "residue");
            File.CreateSymbolicLink(Path.Combine(ownedDirectory, "to-victim-dir"), victimDirectory);
            var ownedLink = Path.Combine(sharedTmp, "owned-link");
            File.CreateSymbolicLink(ownedLink, victim);
            var sharedDirectory = Path.Combine(sharedTmp, "root-dir");
            Directory.CreateDirectory(sharedDirectory);
            var nestedOwned = Path.Combine(sharedDirectory, "nested-owned.bin");
            await File.WriteAllTextAsync(nestedOwned, "residue");
            var rootFile = Path.Combine(sharedTmp, "root-file.bin");
            await File.WriteAllTextAsync(rootFile, "keep");

            var commandRunner = new LinuxCommandRunner();
            foreach (var path in new[] { ownedFile, ownedDirectory, ownedLink, nestedOwned })
            {
                await commandRunner.RunAsync(
                    new LinuxCommand("/bin/chown", ["-h", "-R", user.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), path]),
                    CancellationToken.None);
            }

            var strategy = new LinuxUserIsolationStrategy(
                commandRunner,
                static (userId, token) => LinuxProcessReaper.KillAllOwnedByAsync(userId, token),
                residueRoots: [sharedTmp]);
            var identity = new PreparedSessionIdentity(user.Name, string.Empty, CleanupIdentity: true, user.UserId);

            await strategy.CleanupAsync(identity, CancellationToken.None);
            await strategy.CleanupAsync(identity, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(ownedFile), Is.False);
                Assert.That(Directory.Exists(ownedDirectory), Is.False);
                Assert.That(File.Exists(ownedLink) || Directory.Exists(ownedLink), Is.False);
                Assert.That(File.Exists(nestedOwned), Is.False);
                Assert.That(Directory.Exists(sharedDirectory), Is.True);
                Assert.That(File.ReadAllText(rootFile), Is.EqualTo("keep"));
                Assert.That(File.ReadAllText(victim), Is.EqualTo("keep"));
                Assert.That(File.ReadAllText(Path.Combine(victimDirectory, "inner.txt")), Is.EqualTo("keep"));
            });
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Test]
    public async Task CleanupStale_RemovesLeftoverSessionUsersProcessesAndHomes()
    {
        LinuxTestPrerequisites.RequireRoot();
        LinuxTestPrerequisites.RequireCommand("/usr/sbin/useradd");
        LinuxTestPrerequisites.RequireCommand("/bin/chown");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-stale-tests", Guid.NewGuid().ToString("n"));
        var sessionRoot = Path.Combine(root, "sessions");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(sessionRoot);
        Directory.CreateDirectory(outside);
        var staleUser = $"sess_st{Guid.NewGuid():N}"[..17];
        var commandRunner = new LinuxCommandRunner();
        uint staleUserId = 0;
        try
        {
            var staleHome = Path.Combine(sessionRoot, "stale-session");
            await commandRunner.RunAsync(
                new LinuxCommand("/usr/sbin/useradd", ["--home-dir", staleHome, "-m", "--shell", "/usr/sbin/nologin", staleUser]),
                CancellationToken.None);
            var id = await commandRunner.RunAsync(LinuxCommandFactory.CheckUserExists(staleUser), CancellationToken.None);
            staleUserId = uint.Parse(id.Stdout.Trim(), System.Globalization.CultureInfo.InvariantCulture);

            var launcher = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UserName = staleUser,
                WorkingDirectory = "/",
                UseShellExecute = false
            };
            launcher.ArgumentList.Add("-c");
            launcher.ArgumentList.Add("sleep 300 </dev/null >/dev/null 2>&1 & exit 0");
            using (var process = Process.Start(launcher)!)
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            var uid = staleUserId;
            await WaitUntilAsync(() => LinuxProcessReaper.FindOwnedLiveProcesses(uid).Count >= 1);

            // A home whose user is already gone, a link it planted, and the agent's own entry.
            var orphanHome = Path.Combine(sessionRoot, "orphan-session");
            Directory.CreateDirectory(orphanHome);
            await File.WriteAllTextAsync(Path.Combine(orphanHome, "secret.txt"), "old");
            var victimDirectory = Path.Combine(outside, "victim");
            Directory.CreateDirectory(victimDirectory);
            await File.WriteAllTextAsync(Path.Combine(victimDirectory, "keep.txt"), "keep");
            var orphanLink = Path.Combine(sessionRoot, "orphan-link");
            File.CreateSymbolicLink(orphanLink, victimDirectory);
            await commandRunner.RunAsync(new LinuxCommand("/bin/chown", ["-h", "-R", "54321", orphanHome]), CancellationToken.None);
            await commandRunner.RunAsync(new LinuxCommand("/bin/chown", ["-h", "54321", orphanLink]), CancellationToken.None);
            var agentEntry = Path.Combine(sessionRoot, "agent-owned");
            Directory.CreateDirectory(agentEntry);

            var passwdPath = Path.Combine(root, "passwd");
            await File.WriteAllTextAsync(
                passwdPath,
                $"root:x:0:0:root:/root:/bin/sh\n{staleUser}:x:{staleUserId}:{staleUserId}::{staleHome}:/usr/sbin/nologin\n");
            var strategy = new LinuxUserIsolationStrategy(
                commandRunner,
                static (userId, token) => LinuxProcessReaper.KillAllOwnedByAsync(userId, token),
                residueRoots: [],
                passwdPath: passwdPath);

            await strategy.CleanupStaleAsync(sessionRoot, CancellationToken.None);
            await strategy.CleanupStaleAsync(sessionRoot, CancellationToken.None);

            var lookup = await commandRunner.RunAsync(LinuxCommandFactory.CheckUserExists(staleUser), CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(LinuxProcessReaper.FindOwnedLiveProcesses(uid), Is.Empty);
                Assert.That(lookup.ExitCode, Is.Not.Zero);
                Assert.That(Directory.Exists(staleHome), Is.False);
                Assert.That(Directory.Exists(orphanHome), Is.False);
                Assert.That(File.Exists(orphanLink) || Directory.Exists(orphanLink), Is.False);
                Assert.That(File.ReadAllText(Path.Combine(victimDirectory, "keep.txt")), Is.EqualTo("keep"));
                Assert.That(Directory.Exists(agentEntry), Is.True);
            });
        }
        finally
        {
            if (staleUserId != 0)
            {
                await LinuxProcessReaper.KillAllOwnedByAsync(staleUserId, CancellationToken.None);
            }

            await commandRunner.RunAsync(LinuxCommandFactory.RemoveUser(staleUser), CancellationToken.None);
            TryDeleteDirectory(root);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
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
