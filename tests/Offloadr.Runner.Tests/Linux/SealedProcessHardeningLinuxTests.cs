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

    [Test]
    public void ProcessHardening_ReadsTheDumpableFlag()
    {
        LinuxTestPrerequisites.RequireLinux();

        // Only read: making the test host non-dumpable would outlive this test.
        Assert.That(LinuxProcessHardening.ReadDumpable(), Is.InRange(0, 2));
    }
}
