namespace Offloadr.Runner.Tests;

public partial class SessionIsolationTests
{
    private static readonly PreparedSessionIdentity CleanupIdentity = new("sess_6f1c2d3e4b5a", "/sessions/x", CleanupIdentity: true, 4242);

    [TestCase(false)]
    [TestCase(true)]
    public async Task Cleanup_MissingAccountNeverActsOnItsRememberedUid(bool processesOnly)
    {
        var runner = new CleanupCommandRunner { LookupExitCode = 1 };
        var strategy = new LinuxUserIsolationStrategy(
            runner,
            static (_, _) => throw new AssertionException("the remembered uid is no longer authorized"),
            residueRoots: [],
            removeOwnedEntries: static (uint _, IEnumerable<string> _, out int _) => throw new AssertionException("must not remove another owner's files"),
            countIpcObjects: static _ => throw new AssertionException("must not inspect another owner's IPC"));

        await RunCleanupAsync(strategy, CleanupIdentity, processesOnly);

        Assert.That(runner.Commands.Select(command => command.FileName), Is.EqualTo(new[] { "/usr/bin/id" }));
    }

    [TestCase(false, "9001")]
    [TestCase(true, "9001")]
    [TestCase(false, "0")]
    [TestCase(true, "0")]
    [TestCase(false, "invalid")]
    [TestCase(true, "invalid")]
    [TestCase(false, "-1")]
    [TestCase(true, "-1")]
    public void Cleanup_ChangedOrInvalidUidRefusesAllEffects(bool processesOnly, string currentUid)
    {
        var runner = new CleanupCommandRunner { CurrentUid = currentUid };
        var strategy = new LinuxUserIsolationStrategy(
            runner,
            static (_, _) => throw new AssertionException("must not kill a replacement account's processes"),
            residueRoots: [],
            removeOwnedEntries: static (uint _, IEnumerable<string> _, out int _) => throw new AssertionException("must not remove replacement files"),
            countIpcObjects: static _ => throw new AssertionException("must not inspect replacement IPC"));

        Assert.That(async () => await RunCleanupAsync(strategy, CleanupIdentity, processesOnly), Throws.InvalidOperationException);
        Assert.That(runner.Commands.Select(command => command.FileName), Is.EqualTo(new[] { "/usr/bin/id" }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Cleanup_UnexpectedLookupFailureRefusesAllEffects(bool processesOnly)
    {
        var runner = new CleanupCommandRunner { LookupExitCode = 2 };
        var strategy = new LinuxUserIsolationStrategy(runner, static (_, _) => throw new AssertionException("lookup failed"), residueRoots: []);

        Assert.That(async () => await RunCleanupAsync(strategy, CleanupIdentity, processesOnly), Throws.InvalidOperationException);
        Assert.That(runner.Commands.Select(command => command.FileName), Is.EqualTo(new[] { "/usr/bin/id" }));
    }

    [Test]
    public async Task StopProcessesAsync_JoinsTheOriginalReaperWithoutRetiringAccountOrData()
    {
        var runner = new CleanupCommandRunner();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = new CancellationTokenSource();
        var strategy = new LinuxUserIsolationStrategy(runner, async (uid, token) =>
        {
            Assert.That(uid, Is.EqualTo(4242));
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            returned.TrySetResult();
            return true;
        }, residueRoots: [],
            removeOwnedEntries: static (uint _, IEnumerable<string> _, out int _) => throw new AssertionException("process quiescence must retain data"),
            countIpcObjects: static _ => throw new AssertionException("process quiescence must retain IPC"));
        var quiescence = strategy.StopProcessesAsync(CleanupIdentity, stopping.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await stopping.CancelAsync();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(quiescence.IsCompleted, Is.False, "the actual reaper is still held");
        }
        finally
        {
            release.TrySetResult();
            await quiescence.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Multiple(() =>
        {
            Assert.That(returned.Task.IsCompletedSuccessfully, Is.True);
            Assert.That(runner.Commands.Select(command => command.FileName), Is.EqualTo(new[] { "/usr/bin/id" }));
        });
    }

    [Test]
    public void StopProcessesAsync_PreservesAccountAndDataWhileAProcessSurvives()
    {
        var runner = new CleanupCommandRunner();
        var strategy = new LinuxUserIsolationStrategy(runner, static (_, _) => Task.FromResult(false), residueRoots: []);

        Assert.That(async () => await strategy.StopProcessesAsync(CleanupIdentity, CancellationToken.None),
            Throws.InvalidOperationException.With.Message.Contains("still running"));
        Assert.That(runner.Commands.Select(command => command.FileName), Is.EqualTo(new[] { "/usr/bin/id" }));
    }

    [Test]
    public async Task CleanupAsync_RevalidatesUidAfterTheProcessOnlyPhase()
    {
        var runner = new CleanupCommandRunner();
        var reaped = new List<uint>();
        var strategy = new LinuxUserIsolationStrategy(runner, (uid, _) =>
        {
            reaped.Add(uid);
            return Task.FromResult(true);
        }, residueRoots: [], countIpcObjects: static _ => 0);

        await strategy.StopProcessesAsync(CleanupIdentity, CancellationToken.None);
        runner.CurrentUid = "9001";
        Assert.That(async () => await strategy.CleanupAsync(CleanupIdentity, CancellationToken.None), Throws.InvalidOperationException);
        Assert.Multiple(() =>
        {
            Assert.That(reaped, Is.EqualTo(new uint[] { 4242 }));
            Assert.That(runner.Commands.Select(command => command.FileName), Is.EqualTo(new[] { "/usr/bin/id", "/usr/bin/id" }));
        });
    }

    private static Task RunCleanupAsync(LinuxUserIsolationStrategy strategy, PreparedSessionIdentity identity, bool processesOnly)
        => processesOnly
            ? strategy.StopProcessesAsync(identity, CancellationToken.None)
            : strategy.CleanupAsync(identity, CancellationToken.None);

    private sealed class CleanupCommandRunner : ILinuxCommandRunner
    {
        public string CurrentUid { get; set; } = "4242";
        public int LookupExitCode { get; init; }
        public List<LinuxCommand> Commands { get; } = [];

        public Task<LinuxCommandResult> RunAsync(LinuxCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(command.FileName == "/usr/bin/id"
                ? new LinuxCommandResult(LookupExitCode, CurrentUid, string.Empty)
                : new LinuxCommandResult(0, string.Empty, string.Empty));
        }
    }
}
