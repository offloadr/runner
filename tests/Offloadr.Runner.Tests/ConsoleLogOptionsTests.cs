namespace Offloadr.Runner.Tests;

public sealed class ConsoleLogOptionsTests
{
    [TestCase(null, null, ConsoleLogMode.Full)]
    [TestCase("", "auto", ConsoleLogMode.Full)]
    [TestCase("full", "off", ConsoleLogMode.Full)]
    [TestCase(" Full ", "auto", ConsoleLogMode.Full)]
    [TestCase("metadata", null, ConsoleLogMode.Metadata)]
    [TestCase("METADATA", "auto", ConsoleLogMode.Metadata)]
    [TestCase(null, "required", ConsoleLogMode.Metadata)]
    [TestCase("metadata", "required", ConsoleLogMode.Metadata)]
    public void ModeFollowsTheSettingOutsideRequiredSealedStorage(string? consoleLogs, string? sealedStorage, ConsoleLogMode expected)
    {
        var options = Parse(consoleLogs, sealedStorage);

        Assert.Multiple(() =>
        {
            Assert.That(options.Mode, Is.EqualTo(expected));
            Assert.That(options.InvalidMode, Is.Null);
            Assert.That(options.FullRefused, Is.False);
        });
    }

    [TestCase("required")]
    [TestCase("synthetic-typo")]
    public void FullIsRefusedWhereSealedStorageIsRequired(string sealedStorage)
    {
        // An unrecognised sealed storage value is treated as required, so it cannot widen the console.
        var options = Parse("full", sealedStorage);

        Assert.Multiple(() =>
        {
            Assert.That(options.Mode, Is.EqualTo(ConsoleLogMode.Metadata));
            Assert.That(options.FullRefused, Is.True);
        });
    }

    [TestCase(null)]
    [TestCase("required")]
    public void AnUnrecognisedValueFallsBackToMetadataAndIsReported(string? sealedStorage)
    {
        var options = Parse("verbose", sealedStorage);

        Assert.Multiple(() =>
        {
            Assert.That(options.Mode, Is.EqualTo(ConsoleLogMode.Metadata));
            Assert.That(options.InvalidMode, Is.EqualTo("verbose"));
            Assert.That(options.FullRefused, Is.False);
        });
    }

    private static ConsoleLogOptions Parse(string? consoleLogs, string? sealedStorage)
    {
        string? Env(string name) => name switch
        {
            "RUNNER_CONSOLE_LOGS" => consoleLogs,
            "RUNNER_SEALED_STORAGE" => sealedStorage,
            _ => null
        };
        return ConsoleLogOptions.FromEnvironment(Env, SealedStorageOptions.FromEnvironment(Env));
    }
}
