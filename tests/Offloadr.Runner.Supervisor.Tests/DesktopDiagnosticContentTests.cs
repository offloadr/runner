using Microsoft.Extensions.Logging;
using Offloadr.Runner.Supervisor.Logging;

namespace Offloadr.Runner.Supervisor.Tests;

public sealed class DesktopDiagnosticContentTests
{
    private const string Sentinel = "synthetic-prompt-email@example.invalid";
    private string _root = null!;

    [SetUp]
    public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "offloadr-desktop-diagnostics-" + Guid.NewGuid().ToString("n"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void FileDiagnosticsNeverFormatOrPersistArbitraryContent()
    {
        var formatting = 0;
        using var provider = new DesktopDiagnosticLogProvider(_root);
        var logger = provider.CreateLogger("Offloadr." + Sentinel);
        using var scope = logger.BeginScope(Sentinel);
        logger.Log(LogLevel.Error, new EventId(73, Sentinel), Sentinel, new IOException(Sentinel),
            (state, _) => { formatting++; return state; });
        var content = string.Concat(Directory.GetFiles(_root).Select(File.ReadAllText));
        TestContext.Out.WriteLine($"desktop-file: formatter={formatting}; leaked={content.Contains(Sentinel, StringComparison.Ordinal)}");
        Assert.Multiple(() =>
        {
            Assert.That(formatting, Is.Zero);
            Assert.That(content, Does.Not.Contain(Sentinel));
            Assert.That(content, Does.Contain("severity=error").And.Contain("errorClass=io"));
        });
    }

    [Test]
    public void StartupFailurePersistsOnlyFixedOperationalMetadata()
    {
        DesktopStartupDiagnostics.WriteError(_root, new InvalidOperationException(Sentinel, new Exception(Sentinel)));
        var content = string.Concat(Directory.GetFiles(_root).Select(File.ReadAllText));
        TestContext.Out.WriteLine($"desktop-startup: leaked={content.Contains(Sentinel, StringComparison.Ordinal)}");
        Assert.Multiple(() =>
        {
            Assert.That(content, Does.Not.Contain(Sentinel));
            Assert.That(content, Does.Contain("component=desktop-startup").And.Contain("errorClass=state"));
        });
    }

    [Test]
    public void KnownLegacyRawFilesAreRemovedWithoutTouchingUnrelatedData()
    {
        Directory.CreateDirectory(_root);
        var owned = new[] { "runner-2026-10-24.log", "desktop-startup.log" };
        var preserved = new[] { "notes.log", "runner-not-a-date.log", "settings.json", "runner-key", "diagnostic-backup.txt" };
        foreach (var name in owned.Concat(preserved)) File.WriteAllText(Path.Combine(_root, name), Sentinel);
        using var provider = new DesktopDiagnosticLogProvider(_root);
        provider.CreateLogger("Offloadr.Runner.Supervisor.RunnerSupervisor").LogWarning("{Content}", Sentinel);
        TestContext.Out.WriteLine($"desktop-legacy: remaining={owned.Count(name => File.Exists(Path.Combine(_root, name)))}");
        Assert.Multiple(() =>
        {
            Assert.That(owned.Where(name => File.Exists(Path.Combine(_root, name))), Is.Empty);
            foreach (var name in preserved) Assert.That(File.ReadAllText(Path.Combine(_root, name)), Is.EqualTo(Sentinel));
        });
    }
}
