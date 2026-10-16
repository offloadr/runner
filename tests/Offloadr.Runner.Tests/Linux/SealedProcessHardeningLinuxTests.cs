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
    public void EnvironmentBuilder_AsksTheShimToDisableCoreDumpsForSealedSessions()
    {
        var builder = new RunnerVfsEnvironmentBuilder();
        var paths = new RunnerVfsSessionPaths("/models", "/out", "/in", "/temp");

        var sealedEnvironment = builder.Build(paths, getEnv: _ => null, fileExists: _ => true, disableCoreDumps: true);
        var unsealedEnvironment = builder.Build(paths, getEnv: _ => null, fileExists: _ => true);

        Assert.That(sealedEnvironment["VFS_NO_DUMP"], Is.EqualTo("1"));
        Assert.That(unsealedEnvironment.ContainsKey("VFS_NO_DUMP"), Is.False);
    }

    [Test]
    public void ProcessHardening_ReadsTheDumpableFlag()
    {
        LinuxTestPrerequisites.RequireLinux();

        // Only read: making the test host non-dumpable would outlive this test.
        Assert.That(LinuxProcessHardening.ReadDumpable(), Is.InRange(0, 2));
    }
}
