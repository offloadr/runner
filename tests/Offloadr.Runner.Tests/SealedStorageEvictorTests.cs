namespace Offloadr.Runner.Tests;

public class SealedStorageEvictorTests
{
    private const long GiB = 1L << 30;

    [TestCase(4 * GiB, 0)]
    [TestCase(2 * GiB, 0)]
    [TestCase(2 * GiB - 1, 2 * GiB + 1)]
    [TestCase(0, 4 * GiB)]
    public void ComputeBytesToFree_StartsBelowHalfTheMinimumAndRefillsToIt(long budget, long expected)
    {
        Assert.That(SealedStorageEvictor.ComputeBytesToFree(Status(budget), 4 * GiB), Is.EqualTo(expected));
    }

    [Test]
    public void ComputeBytesToFree_DoesNothingWithoutABudget()
    {
        Assert.That(SealedStorageEvictor.ComputeBytesToFree(Status(null), 4 * GiB), Is.Zero);
    }

    [Test]
    public void RunOnce_EvictsForTheActiveSessionOnlyWhenLow()
    {
        var calls = new List<(string SessionId, long Bytes, TimeSpan Idle)>();
        var free = 1 * GiB;
        var activeSession = "session-1";
        var probe = new SealedStorageProbe(
            path => path switch
            {
                "/sys/fs/cgroup/memory.max" => "68719476736",
                "/sys/fs/cgroup/memory.current" => "0",
                _ => null,
            },
            _ => new LinuxFileSystemStats(0x01021994, 4096, 10 * GiB / 4096, free / 4096),
            path => path == "/dev/shm" ? new LinuxEntryStatus(true, false, 0, UnixFileMode.UserRead) : null,
            path => path);
        var evictor = new SealedStorageEvictor(
            new SealedStorageOptions { Mode = SealedStorageMode.Required },
            probe,
            () => activeSession,
            (sessionId, bytes, idle) =>
            {
                calls.Add((sessionId, bytes, idle));
                return bytes;
            },
            minimumIdle: TimeSpan.FromMinutes(3));

        Assert.That(evictor.RunOnce(), Is.EqualTo(3 * GiB));
        Assert.That(calls, Is.EqualTo(new[] { ("session-1", 3 * GiB, TimeSpan.FromMinutes(3)) }));

        free = 3 * GiB;
        Assert.That(evictor.RunOnce(), Is.Zero);
        activeSession = string.Empty;
        free = 0;
        Assert.That(evictor.RunOnce(), Is.Zero);
        Assert.That(calls, Has.Count.EqualTo(1));
    }

    [Test]
    public void RunOnce_LeavesAnUnsealedSessionAlone()
    {
        var calls = 0;
        var probe = new SealedStorageProbe(
            _ => null,
            _ => new LinuxFileSystemStats(0x01021994, 4096, 16384, 16384),
            path => path == "/dev/shm" ? new LinuxEntryStatus(true, false, 0, UnixFileMode.UserRead) : null,
            path => path);
        var evictor = new SealedStorageEvictor(
            new SealedStorageOptions { Mode = SealedStorageMode.Auto },
            probe,
            () => "session-1",
            (_, _, _) =>
            {
                calls++;
                return 0;
            },
            isSessionSealed: _ => false);

        // Docker's default 64 MiB /dev/shm is far below the budget, but the session is on disk.
        Assert.That(evictor.RunOnce(), Is.Zero);
        Assert.That(calls, Is.Zero);
    }

    private static SealedStorageStatus Status(long? budget)
        => new("required", false, null, "/dev/shm/offloadr", null, null, null, budget, new SealedStorageSwapStatus(0, "0", false));
}
