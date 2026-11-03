using Microsoft.Extensions.Logging;

namespace Offloadr.Runner.Tests;

public sealed class DiagnosticTextLoggerProviderTests
{
    [TestCase("RunnerAgent", "agent")]
    [TestCase("session:synthetic-session", "editor")]
    [TestCase("Offloadr.Runner.Linux.WorkspaceMirrorService", "workspace")]
    public void WritesTheFormattedMessageAfterTheComponentLabel(string category, string component)
    {
        var lines = new List<string>();
        using var provider = new DiagnosticTextLoggerProvider(lines.Add);
        provider.CreateLogger(category).LogWarning("Mirror {Name} is behind", "inputs");

        Assert.That(lines, Has.Count.EqualTo(1));
        Assert.That(lines[0], Does.EndWith($" warning {component}: Mirror inputs is behind"));
    }

    [Test]
    public void PrefixesTheEventNameAndAppendsTheException()
    {
        var lines = new List<string>();
        using var provider = new DiagnosticTextLoggerProvider(lines.Add);
        provider.CreateLogger("RunnerAgent").LogError(new EventId(5, "session-start"), new InvalidOperationException("synthetic-state"), "Start failed");

        Assert.That(lines, Has.Count.EqualTo(1));
        Assert.That(lines[0], Does.Contain(" error agent: session-start Start failed" + Environment.NewLine));
        Assert.That(lines[0], Does.Contain("System.InvalidOperationException: synthetic-state"));
    }

    [TestCase(LogLevel.Trace)]
    [TestCase(LogLevel.Debug)]
    [TestCase(LogLevel.None)]
    public void DisabledLevelsDoNotRenderOrWrite(LogLevel level)
    {
        var writes = 0;
        using var provider = new DiagnosticTextLoggerProvider(_ => writes++);
        provider.CreateLogger("RunnerAgent").Log(level, new EventId(0), "state", null, (_, _) => throw new InvalidOperationException());
        Assert.That(writes, Is.Zero);
    }

    [Test]
    public void AFailingConsoleDoesNotInterruptTheCaller()
    {
        using var provider = new DiagnosticTextLoggerProvider(_ => throw new IOException("synthetic-closed-console"));
        Assert.DoesNotThrow(() => provider.CreateLogger("RunnerAgent").LogInformation("Runner starting"));
    }
}
