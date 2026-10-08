using System.Collections.Concurrent;

namespace Offloadr.Runner.Tests;

public class SessionIsolationTests
{
    private const string SessionId = "6f1c2d3e-4b5a-4c6d-8e7f-a0b1c2d3e4f5";

    [TestCase("6f1c2d3e-4b5a-4c6d-8e7f-a0b1c2d3e4f5")]
    [TestCase("6F1C2D3E-4B5A-4C6D-8E7F-A0B1C2D3E4F5")]
    [TestCase("6f1c2d3e4b5a4c6d8e7fa0b1c2d3e4f5")]
    public void IsValidSessionId_AcceptsUuids(string sessionId)
    {
        Assert.That(SessionHomePaths.IsValidSessionId(sessionId), Is.True);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("session-1")]
    [TestCase("..")]
    [TestCase("../etc")]
    [TestCase("/etc")]
    [TestCase("6f1c2d3e-4b5a-4c6d-8e7f-a0b1c2d3e4f5/../../etc")]
    [TestCase("{6f1c2d3e-4b5a-4c6d-8e7f-a0b1c2d3e4f5}")]
    [TestCase(" 6f1c2d3e-4b5a-4c6d-8e7f-a0b1c2d3e4f5")]
    [TestCase("00000000-0000-0000-0000-000000000000")]
    public void IsValidSessionId_RejectsNonUuids(string? sessionId)
    {
        Assert.That(SessionHomePaths.IsValidSessionId(sessionId), Is.False);
    }

    [Test]
    public void ResolveHomeDirectory_ReturnsDirectChildOfSessionRoot()
    {
        var sessionRoot = Path.Combine(Path.GetTempPath(), "sessions-root");

        var home = SessionHomePaths.ResolveHomeDirectory(sessionRoot, SessionId);

        Assert.That(home, Is.EqualTo(Path.Combine(Path.GetFullPath(sessionRoot), SessionId)));
        Assert.That(SessionHomePaths.IsDirectChildOf(sessionRoot, home), Is.True);
    }

    [Test]
    public void IsDirectChildOf_RejectsNestedEscapingAndRootPaths()
    {
        var sessionRoot = Path.Combine(Path.GetTempPath(), "sessions-root");

        Assert.Multiple(() =>
        {
            Assert.That(SessionHomePaths.IsDirectChildOf(sessionRoot, sessionRoot), Is.False);
            Assert.That(SessionHomePaths.IsDirectChildOf(sessionRoot, Path.Combine(sessionRoot, "a", "b")), Is.False);
            Assert.That(SessionHomePaths.IsDirectChildOf(sessionRoot, Path.Combine(sessionRoot, "..", "other")), Is.False);
            Assert.That(SessionHomePaths.IsDirectChildOf(sessionRoot, Path.GetTempPath()), Is.False);
        });
    }

    [Test]
    public async Task PrepareAsync_WithUuidSessionId_PreparesHomeBeneathSessionRoot()
    {
        var root = CreateTempDirectory();
        try
        {
            var sessionRoot = Path.Combine(root, "sessions");
            var runner = new RecordingCommandRunner(userExists: true);
            var strategy = new LinuxUserIsolationStrategy(runner);

            var identity = await strategy.PrepareAsync(SessionId, sessionRoot, CancellationToken.None);

            var expectedHome = Path.Combine(Path.GetFullPath(sessionRoot), SessionId);
            Assert.Multiple(() =>
            {
                Assert.That(identity.HomeDirectory, Is.EqualTo(expectedHome));
                Assert.That(Directory.Exists(expectedHome), Is.True);
                Assert.That(runner.Commands.Select(static command => command.FileName), Does.Contain("/bin/chown"));
                Assert.That(
                    runner.Commands.Single(static command => command.FileName == "/bin/chown").Arguments[^1],
                    Is.EqualTo(expectedHome));
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestCase("../escape")]
    [TestCase("/etc")]
    [TestCase("..")]
    [TestCase("session-1")]
    [TestCase("00000000-0000-0000-0000-000000000000")]
    public void PrepareAsync_WithInvalidSessionId_RefusesBeforeAnyFilesystemOrUserOperation(string sessionId)
    {
        var root = CreateTempDirectory();
        try
        {
            var sessionRoot = Path.Combine(root, "sessions");
            var runner = new RecordingCommandRunner(userExists: false);
            var strategy = new LinuxUserIsolationStrategy(runner);

            Assert.That(
                async () => await strategy.PrepareAsync(sessionId, sessionRoot, CancellationToken.None),
                Throws.ArgumentException);
            Assert.Multiple(() =>
            {
                Assert.That(runner.Commands, Is.Empty);
                Assert.That(Directory.Exists(sessionRoot), Is.False);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"session-isolation-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort test cleanup.
        }
    }

    private sealed class RecordingCommandRunner(bool userExists) : ILinuxCommandRunner
    {
        private readonly ConcurrentQueue<LinuxCommand> _commands = new();

        public IReadOnlyList<LinuxCommand> Commands => [.. _commands];

        public Task<LinuxCommandResult> RunAsync(LinuxCommand command, CancellationToken cancellationToken)
        {
            _commands.Enqueue(command);
            var exitCode = command.FileName == "/usr/bin/id" && !userExists ? 1 : 0;
            return Task.FromResult(new LinuxCommandResult(exitCode, string.Empty, string.Empty));
        }
    }
}
