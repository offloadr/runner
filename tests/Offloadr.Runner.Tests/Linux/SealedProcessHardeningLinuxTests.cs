namespace Offloadr.Runner.Tests;

public class SealedProcessHardeningLinuxTests
{
    private const string DumpableProbeSource = """
        #include <stdio.h>
        #include <string.h>
        #include <sys/prctl.h>
        #include <unistd.h>

        int main(int argc, char **argv) {
            printf("%d\n", prctl(PR_GET_DUMPABLE, 0, 0, 0, 0));
            fflush(stdout);
            if (argc > 1 && strcmp(argv[1], "exec") == 0) {
                /* execve resets dumpability; the shim must apply it again in the new program. */
                execl(argv[0], argv[0], (char *)NULL);
                return 2;
            }
            return 0;
        }
        """;

    private const string CoreLockProbeSource = """
        #include <stdio.h>
        #include <stdlib.h>
        #include <string.h>
        #include <sys/prctl.h>
        #include <sys/resource.h>
        #include <unistd.h>

        int main(int argc, char **argv) {
            struct rlimit limit;
            int read = getrlimit(RLIMIT_CORE, &limit) == 0;
            unsigned long soft = (unsigned long)limit.rlim_cur;
            limit.rlim_cur = 0;
            int lowered = setrlimit(RLIMIT_CORE, &limit) == 0;
            int madeDumpable = prctl(PR_SET_DUMPABLE, 1, 0, 0, 0) == 0;
            printf("%d %d %d %d %lu\n", read, lowered, madeDumpable, prctl(PR_GET_DUMPABLE, 0, 0, 0, 0), soft);
            fflush(stdout);
            if (argc > 1 && strcmp(argv[1], "exec") == 0) {
                /* Without the shim from here on: the lock must come with the process. */
                unsetenv("LD_PRELOAD");
                execl(argv[0], argv[0], (char *)NULL);
                return 2;
            }
            return 0;
        }
        """;

    [TestCase("1", "1 0 0 0 1\n1 0 0 1 1\n")]
    [TestCase(null, "1 1 1 1 1\n")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Shim_LocksTheSessionUsersCoreLimitAndDumpability(string? noDump, string expected)
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireRoot();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), $"runner-core-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, (UnixFileMode)Convert.ToInt32("755", 8));
        var original = LinuxProcessHardening.ReadCoreSizeLimit()!.Value;
        try
        {
            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "probe.c");
            var probePath = Path.Combine(root, "probe");
            await VfsShimLinuxTests.CompileShimAsync(shimPath);
            await File.WriteAllTextAsync(probeSourcePath, CoreLockProbeSource);
            await VfsShimLinuxTests.RunProcessAsync("/usr/bin/gcc", [probeSourcePath, "-o", probePath], root);
            // As the agent leaves it for sealed sessions.
            Assert.That(LinuxProcessHardening.SetCoreSizeLimit(1, 1), Is.True);
            await using var user = await LinuxTestUser.CreateAsync();

            string[] environment = noDump is null
                ? [$"LD_PRELOAD={shimPath}", "VFS_ROOTS="]
                : [$"LD_PRELOAD={shimPath}", "VFS_ROOTS=", $"VFS_NO_DUMP={noDump}"];
            var result = await VfsShimLinuxTests.RunProcessAsync(
                "/usr/sbin/runuser",
                ["-u", user.Name, "--", "/usr/bin/env", .. environment, probePath, noDump is null ? "once" : "exec"],
                root);

            // Sealed: the soft limit stays at 1 byte and the process cannot make itself dumpable,
            // after an exec without the shim too (which makes it dumpable, the limit covering it).
            Assert.That(result.Stdout, Is.EqualTo(expected));
        }
        finally
        {
            LinuxProcessHardening.SetCoreSizeLimit(original.Soft, original.Hard);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestCase("1", "0\n0\n")]
    [TestCase(null, "1\n1\n")]
    public async Task Shim_MakesProcessesNonDumpableAcrossExecWhenAsked(string? noDump, string expected)
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runner-dumpable-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "probe.c");
            var probePath = Path.Combine(root, "probe");
            await VfsShimLinuxTests.CompileShimAsync(shimPath);
            await File.WriteAllTextAsync(probeSourcePath, DumpableProbeSource);
            await VfsShimLinuxTests.RunProcessAsync("/usr/bin/gcc", [probeSourcePath, "-o", probePath], root);

            var result = await VfsShimLinuxTests.RunProcessAsync(
                probePath,
                ["exec"],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_NO_DUMP"] = noDump,
                    ["VFS_ROOTS"] = string.Empty,
                });

            Assert.That(result.Stdout, Is.EqualTo(expected));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task CoreDumpHardeningProbe_PassesOnlyForAShimThatLoads()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runner-dumpable-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            await VfsShimLinuxTests.CompileShimAsync(shimPath);
            // The loader only warns about these and starts the program anyway.
            var corrupt = Path.Combine(root, "corrupt.so");
            await File.WriteAllTextAsync(corrupt, "not an ELF object");
            var missing = Path.Combine(root, "missing.so");

            Assert.That(RunnerVfsEnvironmentBuilder.ProbeCoreDumpHardening(shimPath), Is.True);
            Assert.That(RunnerVfsEnvironmentBuilder.ProbeCoreDumpHardening(corrupt), Is.False);
            Assert.That(RunnerVfsEnvironmentBuilder.ProbeCoreDumpHardening(missing), Is.False);
            Assert.That(new RunnerVfsEnvironmentBuilder { DefaultVfsLibraryPath = corrupt }.CanDisableCoreDumps(_ => null), Is.False);
            Assert.That(new RunnerVfsEnvironmentBuilder { DefaultVfsLibraryPath = shimPath }.CanDisableCoreDumps(_ => null), Is.True);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void EnvironmentBuilder_HardensAndSilencesTheShimForSealedSessions()
    {
        var builder = new RunnerVfsEnvironmentBuilder();
        var paths = new RunnerVfsSessionPaths("/models", "/out", "/in", "/temp");
        // Tracing is on for the runner, as RUNNER_VFS_LOG=1 in auto mode allows.
        string? Env(string name) => name == "RUNNER_VFS_LOG" ? "1" : null;

        var sealedEnvironment = builder.Build(paths, getEnv: Env, fileExists: _ => true, sealedSession: true);
        var unsealedEnvironment = builder.Build(paths, getEnv: Env, fileExists: _ => true);

        Assert.That(sealedEnvironment["VFS_NO_DUMP"], Is.EqualTo("1"));
        Assert.That(sealedEnvironment["VFS_LOG"], Is.EqualTo("0"));
        Assert.That(unsealedEnvironment.ContainsKey("VFS_NO_DUMP"), Is.False);
        Assert.That(unsealedEnvironment["VFS_LOG"], Is.EqualTo("1"));
    }

    [Test]
    public async Task CoreSizeLimit_IsInheritedAndCannotBeRaisedByASessionUser()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireRoot();

        // DisableCoreDumps itself would also make the test host non-dumpable, for good: only the
        // limit it sets is applied here, and restored (as root) afterwards.
        var original = LinuxProcessHardening.ReadCoreSizeLimit()!.Value;
        try
        {
            Assert.That(LinuxProcessHardening.SetCoreSizeLimit(1, 1), Is.True);
            await using var user = await LinuxTestUser.CreateAsync();

            // A child as the session user sees the hard limit and cannot raise it.
            var result = await VfsShimLinuxTests.RunProcessAsync(
                "/usr/sbin/runuser",
                ["-u", user.Name, "--", "/bin/sh", "-c", "ulimit -H -c; ulimit -H -c unlimited 2>/dev/null && echo raised || echo refused"],
                Path.GetTempPath());

            Assert.That(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries), Is.EqualTo(new[] { "0", "refused" }));
        }
        finally
        {
            LinuxProcessHardening.SetCoreSizeLimit(original.Soft, original.Hard);
        }
    }

    [TestCase(1UL, 1UL, "|/usr/lib/systemd/systemd-coredump %P", true)]
    [TestCase(0UL, 0UL, "|/usr/share/apport/apport %p", false)]
    [TestCase(0UL, 0UL, "core", true)]
    [TestCase(1UL, 1UL, "core", true)]
    [TestCase(1UL, 1UL, "@/run/coredump.socket", false)]
    [TestCase(0UL, 1UL, "|/bin/handler", false)]
    [TestCase(1UL, ulong.MaxValue, "core", false)]
    public void CoreSizeLimit_StopsDumpsOnlyWhereTheHostsHandlerHonoursIt(ulong soft, ulong hard, string pattern, bool stops)
    {
        // A piped handler ignores the limit unless it is exactly 1; a socket handler always does.
        Assert.That(LinuxProcessHardening.StopsCoreDumps((soft, hard), pattern + Environment.NewLine), Is.EqualTo(stops));
        Assert.That(LinuxProcessHardening.StopsCoreDumps((1, 1), null), Is.False, "an unreadable pattern");
    }

    [Test]
    public void ProcessHardening_ReadsTheDumpableFlag()
    {
        LinuxTestPrerequisites.RequireLinux();

        // Only read: making the test host non-dumpable would outlive this test.
        Assert.That(LinuxProcessHardening.ReadDumpable(), Is.InRange(0, 2));
    }
}
