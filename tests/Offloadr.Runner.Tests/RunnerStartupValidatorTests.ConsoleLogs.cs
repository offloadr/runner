namespace Offloadr.Runner.Tests;

public partial class RunnerStartupValidatorTests
{
    [Test]
    public void ValidateOrThrow_RejectsAnUnrecognisedConsoleLogMode()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Runner startup validation is Linux-specific.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-validator-{Guid.NewGuid():N}");
        try
        {
            var options = BuildOptions(tempRoot, "/bin/sh",
                consoleLogs: new ConsoleLogOptions { Mode = ConsoleLogMode.Metadata, InvalidMode = "verbose" });
            var validator = new RunnerStartupValidator(new NoopIsolationStrategy());

            var ex = Assert.Throws<InvalidOperationException>(() => validator.ValidateOrThrow(options));
            Assert.That(ex!.Message, Does.Contain("RUNNER_CONSOLE_LOGS must be full or metadata, not 'verbose'."));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Test]
    public void ValidateOrThrow_RefusesFullConsoleLogsWithRequiredSealedStorage()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Runner startup validation is Linux-specific.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"runner-validator-{Guid.NewGuid():N}");
        try
        {
            var options = BuildOptions(tempRoot, "/bin/sh",
                consoleLogs: new ConsoleLogOptions { Mode = ConsoleLogMode.Metadata, FullRefused = true });
            var validator = new RunnerStartupValidator(new NoopIsolationStrategy());

            var ex = Assert.Throws<InvalidOperationException>(() => validator.ValidateOrThrow(options));
            Assert.That(ex!.Message, Does.Contain("RUNNER_CONSOLE_LOGS=full is refused with RUNNER_SEALED_STORAGE=required"));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }
}
