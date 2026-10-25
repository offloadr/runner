using Microsoft.Extensions.Logging;

namespace Offloadr.Runner.Tests;

public sealed class DiagnosticMetadataProjectionTests
{
    [TestCase("RunnerAgent", "agent")]
    [TestCase("session:synthetic-private-identity", "editor")]
    [TestCase("Offloadr.Runner.Linux.SessionProcessManager", "session-process")]
    [TestCase("Offloadr.Runner.Supervisor.Settings.FileRunnerSettingsStore", "settings")]
    [TestCase("synthetic-private-identity", "other")]
    public void ProjectionDoesNotInspectStateScopesEventNamesOrExceptionDetails(string category, string component)
    {
        var lines = new List<string>();
        using var provider = new DiagnosticMetadataLoggerProvider(lines.Add);
        var logger = provider.CreateLogger(category);
        using var scope = logger.BeginScope(new DangerousState());
        logger.Log(LogLevel.Error, new EventId(73, "synthetic-private-event"), new DangerousState(), new DangerousException(),
            (_, _) => throw new InvalidOperationException("The platform sink must not invoke the formatter."));
        Assert.That(lines, Has.Count.EqualTo(1));
        Assert.That(lines[0], Does.Contain($"severity=error component={component} errorClass=unexpected"));
        Assert.That(lines[0], Does.Not.Contain("synthetic-private"));
    }

    [TestCase(LogLevel.Trace)]
    [TestCase(LogLevel.Debug)]
    [TestCase(LogLevel.None)]
    public void DisabledLevelsDoNotRenderOrWrite(LogLevel level)
    {
        var writes = 0;
        using var provider = new DiagnosticMetadataLoggerProvider(_ => writes++);
        var logger = provider.CreateLogger("RunnerAgent");
        logger.Log(level, new EventId(0), new DangerousState(), null, (_, _) => throw new InvalidOperationException());
        Assert.That(writes, Is.Zero);
    }

    private sealed class DangerousState
    {
        public override string ToString() => throw new InvalidOperationException("State must remain uninspected.");
    }

    private sealed class DangerousException : Exception
    {
        public override string Message => throw new InvalidOperationException("Exception detail must remain uninspected.");
        public override string ToString() => throw new InvalidOperationException("Exception text must remain uninspected.");
    }
}
