using System.Text.Json;

namespace Offloadr.Runner.Tests;

public class RunnerStatusFileWriterTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "runner-status-tests-" + Guid.NewGuid().ToString("n"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void ResolvePath_IsDisabledWhenUnsetOrBlank()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RunnerStatusFileWriter.ResolvePath(_ => null), Is.Null);
            Assert.That(RunnerStatusFileWriter.ResolvePath(_ => "  "), Is.Null);
            Assert.That(
                RunnerStatusFileWriter.ResolvePath(name => name == "RUNNER_STATUS_FILE" ? " /run/offloadr/status.json " : null),
                Is.EqualTo("/run/offloadr/status.json"));
        });
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Write_DoesNotFollowLinkAtTemporaryPath()
    {
        LinuxTestPrerequisites.RequireLinux();

        var path = Path.Combine(_root, "status.json");
        var victim = Path.Combine(_root, "victim.txt");
        Directory.CreateDirectory(_root);
        File.WriteAllText(victim, "keep");
        File.CreateSymbolicLink(path + ".tmp", victim);

        CreateWriter(path, () => "session-a").Write("session-a", DateTimeOffset.UnixEpoch);

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(victim), Is.EqualTo("keep"));
            Assert.That(File.ReadAllText(path), Does.Contain("session-a"));
            Assert.That(new FileInfo(path).LinkTarget, Is.Null);
            Assert.That(File.GetUnixFileMode(path), Is.EqualTo(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead));
        });
    }

    [Test]
    public void Write_CreatesDirectoryAndPublishesSnapshot()
    {
        var path = Path.Combine(_root, "nested", "status.json");
        var writer = CreateWriter(path, () => "session-a");
        var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

        writer.Write("session-a", now);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var json = document.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(json.GetProperty("runner_id").GetString(), Is.EqualTo("runner-1"));
            Assert.That(json.GetProperty("instance_id").GetString(), Is.EqualTo("instance-1"));
            Assert.That(json.GetProperty("version").GetString(), Is.EqualTo("1.2.3"));
            Assert.That(json.GetProperty("active_session_id").GetString(), Is.EqualTo("session-a"));
            Assert.That(json.GetProperty("supported_editor_templates")[0].GetString(), Is.EqualTo("comfyui-latest"));
            Assert.That(json.GetProperty("updated_utc").GetDateTimeOffset(), Is.EqualTo(now));
            Assert.That(File.Exists(path + ".tmp"), Is.False);
        });
    }

    [Test]
    public async Task RunAsync_RewritesWhenTheActiveSessionChanges()
    {
        var path = Path.Combine(_root, "status.json");
        var sessionId = string.Empty;
        var writer = CreateWriter(path, () => Volatile.Read(ref sessionId), refreshInterval: TimeSpan.FromHours(1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = writer.RunAsync(cancellation.Token);
        await WaitForSessionAsync(path, string.Empty, cancellation.Token);

        Volatile.Write(ref sessionId, "session-b");
        await WaitForSessionAsync(path, "session-b", cancellation.Token);

        await cancellation.CancelAsync();
        await run;
    }

    private static RunnerStatusFileWriter CreateWriter(string path, Func<string> activeSessionId, TimeSpan? refreshInterval = null)
        => new(
            path,
            "runner-1",
            "instance-1",
            "1.2.3",
            ["comfyui-latest"],
            activeSessionId,
            pollInterval: TimeSpan.FromMilliseconds(10),
            refreshInterval: refreshInterval);

    private static async Task WaitForSessionAsync(string path, string expected, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                try
                {
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
                    if (document.RootElement.GetProperty("active_session_id").GetString() == expected)
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    // The writer replaced the file while it was being read; try again.
                }
            }

            await Task.Delay(10, cancellationToken);
        }
    }
}
