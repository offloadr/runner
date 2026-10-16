using Microsoft.Extensions.Logging;

namespace Offloadr.Runner.Tests;

public class SealedSessionLoggerTests
{
    [Test]
    public void Log_ReplacesFileNamesAndKeepsTheRest()
    {
        var inner = new CapturingLogger();
        ILogger logger = new SealedSessionLogger(inner);

        logger.LogInformation(
            "Uploading artifact session={SessionId} type={Type} path={Relative} bytes={Size}",
            "session-1",
            "output",
            "portraits/alice_00001_.png",
            1234);
        logger.LogWarning("Artifact upload queue is full limit={Limit} latest={Path}", 4096, "/dev/shm/offloadr/s/output/secret.png");

        Assert.That(inner.Messages, Is.EqualTo(new[]
        {
            "Uploading artifact session=session-1 type=output path=[sealed] bytes=1234",
            "Artifact upload queue is full limit=4096 latest=[sealed]",
        }));
    }

    [Test]
    public void Log_KeepsOnlyTheExceptionsType()
    {
        var inner = new CapturingLogger();
        ILogger logger = new SealedSessionLogger(inner);

        logger.LogWarning(
            new IOException("Linux filesystem operation failed while inspecting 'portraits/alice.png'"),
            "Failed to upload artifact {Path} after retries",
            "/dev/shm/offloadr/s/output/portraits/alice.png");

        Assert.That(inner.Messages.Single(), Is.EqualTo("Failed to upload artifact [sealed] after retries error=IOException"));
        Assert.That(inner.Exceptions.Single(), Is.Null);
    }

    [Test]
    public void Log_RespectsTheInnerLevel()
    {
        var inner = new CapturingLogger { MinimumLevel = LogLevel.Information };
        ILogger logger = new SealedSessionLogger(inner);

        logger.LogDebug("Retrying {Path}", "a.png");

        Assert.That(inner.Messages, Is.Empty);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public LogLevel MinimumLevel { get; init; } = LogLevel.Trace;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }
}
