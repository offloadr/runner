namespace Offloadr.Runner.Tests;

public class SealedStorageProbeTests
{
    private const long GiB = 1L << 30;

    [Test]
    public void Options_DefaultToAutoOnDevShm()
    {
        var options = SealedStorageOptions.FromEnvironment(_ => null);

        Assert.Multiple(() =>
        {
            Assert.That(options.Mode, Is.EqualTo(SealedStorageMode.Auto));
            Assert.That(options.Root, Is.EqualTo("/dev/shm/offloadr"));
            Assert.That(options.MinimumBytes, Is.EqualTo(4 * GiB));
            Assert.That(options.InvalidMode, Is.Null);
        });
    }

    [TestCase("off", SealedStorageMode.Off)]
    [TestCase(" Auto ", SealedStorageMode.Auto)]
    [TestCase("REQUIRED", SealedStorageMode.Required)]
    public void Options_ParseModes(string raw, SealedStorageMode expected)
    {
        var options = SealedStorageOptions.FromEnvironment(name => name == "RUNNER_SEALED_STORAGE" ? raw : null);

        Assert.That(options.Mode, Is.EqualTo(expected));
        Assert.That(options.InvalidMode, Is.Null);
    }

    [Test]
    public void Options_UnknownModeFailsClosed()
    {
        var options = SealedStorageOptions.FromEnvironment(name => name == "RUNNER_SEALED_STORAGE" ? "yes" : null);

        Assert.Multiple(() =>
        {
            Assert.That(options.Mode, Is.EqualTo(SealedStorageMode.Required));
            Assert.That(options.InvalidMode, Is.EqualTo("yes"));
        });
    }

    [Test]
    public void Options_ReadRootAndClampMinimum()
    {
        var env = new Dictionary<string, string?>
        {
            ["RUNNER_SEALED_ROOT"] = " /mnt/sealed ",
            ["RUNNER_SEALED_MIN_GB"] = "0",
        };

        var options = SealedStorageOptions.FromEnvironment(name => env.GetValueOrDefault(name));

        Assert.That(options.Root, Is.EqualTo("/mnt/sealed"));
        Assert.That(options.MinimumBytes, Is.EqualTo(1 * GiB));
    }

    [Test]
    public void SessionOptions_CarrySealing()
    {
        var options = SessionProcessOptions.FromEnvironment(name => name == "RUNNER_SEALED_STORAGE" ? "required" : null);

        Assert.That(options.Sealing.Mode, Is.EqualTo(SealedStorageMode.Required));
    }

    [Test]
    public void Probe_ReportsTmpfsBudgetAndSwap()
    {
        var host = new FakeHost();
        host.Entries["/dev/shm/offloadr"] = new LinuxEntryStatus(true, false, 0, (UnixFileMode)Convert.ToInt32("711", 8));
        host.Files["/sys/fs/cgroup/memory.max"] = "52826210304\n";
        host.Files["/sys/fs/cgroup/memory.current"] = "648421376\n";
        host.Files["/sys/fs/cgroup/memory.swap.max"] = "0\n";
        host.Files["/proc/swaps"] =
            "Filename\t\t\t\tType\t\tSize\t\tUsed\t\tPriority\n/dev/nvme1n1p3                          partition\t62499836\t0\t\t-2\n";

        var status = host.Probe().Probe(Required());

        Assert.Multiple(() =>
        {
            Assert.That(status.Available, Is.True, status.Reason);
            Assert.That(status.Mode, Is.EqualTo("required"));
            Assert.That(status.CapacityBytes, Is.EqualTo(10 * GiB));
            Assert.That(status.FreeBytes, Is.EqualTo(10 * GiB));
            Assert.That(status.MemoryHeadroomBytes, Is.EqualTo(52826210304 - 648421376));
            Assert.That(status.BudgetBytes, Is.EqualTo(10 * GiB));
            Assert.That(status.Swap.DevicesBytes, Is.EqualTo(62499836L * 1024));
            Assert.That(status.Swap.CgroupLimit, Is.EqualTo("0"));
            Assert.That(status.Swap.TmpfsNoswap, Is.False);
        });
    }

    [TestCase("/dev/shm", 1000u, "755", TestName = "Probe_RefusesARootUnderAUserOwnedParent")]
    [TestCase("/dev/shm", 0u, "777", TestName = "Probe_RefusesARootUnderAParentOthersCanRenameIn")]
    [TestCase("/dev", 1000u, "755", TestName = "Probe_RefusesARootUnderAReplaceableAncestor")]
    public void Probe_RefusesARootOthersCouldReplace(string ancestor, uint owner, string mode)
    {
        var host = new FakeHost();
        host.Entries[ancestor] = new LinuxEntryStatus(true, false, owner, (UnixFileMode)Convert.ToInt32(mode, 8));

        // Whether the root exists yet or not.
        var missing = host.Probe().Probe(Required());
        host.Entries["/dev/shm/offloadr"] = new LinuxEntryStatus(true, false, 0, (UnixFileMode)Convert.ToInt32("711", 8));
        var existing = host.Probe().Probe(Required());

        Assert.Multiple(() =>
        {
            Assert.That(missing.Available, Is.False);
            Assert.That(missing.Reason, Does.Contain($"'{ancestor}'"));
            Assert.That(existing.Available, Is.False);
            Assert.That(existing.Reason, Does.Contain($"'{ancestor}'"));
        });
    }

    [Test]
    public void Probe_UsesTheParentWhenTheRootDoesNotExistYet()
    {
        var host = new FakeHost();

        var status = host.Probe().Probe(Required());

        Assert.That(status.Available, Is.True, status.Reason);
        Assert.That(host.StatFsCalls, Is.EqualTo(new[] { "/dev/shm" }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Probe_LeavesReclaimableFileCacheOutOfTheHeadroom(bool cgroupV1)
    {
        var host = new FakeHost();
        // A 16 GiB limit nearly full, mostly with inactive page cache of models read earlier.
        var limit = (16 * GiB).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n";
        var usage = (16 * GiB - 1024).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n";
        var inactive = (12 * GiB).ToString(System.Globalization.CultureInfo.InvariantCulture);
        host.Files.Remove("/sys/fs/cgroup/memory.max");
        host.Files.Remove("/sys/fs/cgroup/memory.current");
        if (cgroupV1)
        {
            host.Files["/sys/fs/cgroup/memory/memory.limit_in_bytes"] = limit;
            host.Files["/sys/fs/cgroup/memory/memory.usage_in_bytes"] = usage;
            host.Files["/sys/fs/cgroup/memory/memory.stat"] = $"cache 1\ninactive_file 5\ntotal_inactive_file {inactive}\n";
        }
        else
        {
            host.Files["/sys/fs/cgroup/memory.max"] = limit;
            host.Files["/sys/fs/cgroup/memory.current"] = usage;
            host.Files["/sys/fs/cgroup/memory.stat"] = $"anon 1\nfile 1\ninactive_anon 7\ninactive_file {inactive}\nactive_file 3\n";
        }

        var status = host.Probe().Probe(Required());

        Assert.That(status.MemoryHeadroomBytes, Is.EqualTo(12 * GiB + 1024));
        Assert.That(status.Available, Is.True, status.Reason);
    }

    [Test]
    public void Probe_UsesAvailableMemoryWithoutCgroupFiles()
    {
        var host = new FakeHost();
        host.Files.Remove("/sys/fs/cgroup/memory.max");
        host.Files.Remove("/sys/fs/cgroup/memory.current");
        // Most of the machine is in use: its total is not headroom.
        host.Files["/proc/meminfo"] = "MemTotal:       32784444 kB\nMemFree:  1 kB\nMemAvailable:    6291456 kB\n";

        var status = host.Probe().Probe(Required());

        Assert.That(status.MemoryHeadroomBytes, Is.EqualTo(6 * GiB));
    }

    [Test]
    public void Probe_ReportsNoHeadroomWithoutAnyUsageFigure()
    {
        var host = new FakeHost();
        host.Files.Remove("/sys/fs/cgroup/memory.max");
        host.Files.Remove("/sys/fs/cgroup/memory.current");
        host.Files["/proc/meminfo"] = "MemTotal:       32784444 kB\nMemFree:  1 kB\n";

        var status = host.Probe().Probe(Required());

        Assert.That(status.MemoryHeadroomBytes, Is.Null);
        Assert.That(status.Available, Is.False);
    }

    [Test]
    public void Probe_CapsTheCgroupHeadroomAtTheMachinesAvailableMemory()
    {
        var host = new FakeHost();
        host.Files["/sys/fs/cgroup/memory.max"] = "max\n";
        host.Files["/sys/fs/cgroup/memory.current"] = "1073741824\n";
        // Other tenants use most of the machine.
        host.Files["/proc/meminfo"] = "MemTotal: 8388608 kB\nMemAvailable: 2097152 kB\n";

        var status = host.Probe().Probe(Required());

        Assert.That(status.MemoryHeadroomBytes, Is.EqualTo(2 * GiB));
    }

    [Test]
    public void Probe_CapsAnUnlimitedCgroupAtMachineMemory()
    {
        var host = new FakeHost();
        host.Files["/sys/fs/cgroup/memory.max"] = "max\n";
        host.Files["/sys/fs/cgroup/memory.current"] = "1073741824\n";
        host.Files["/proc/meminfo"] = "MemTotal: 8388608 kB\n";

        var status = host.Probe().Probe(Required());

        Assert.That(status.MemoryHeadroomBytes, Is.EqualTo(7 * GiB));
    }

    [Test]
    public void Probe_RefusesASmallBudget()
    {
        var host = new FakeHost();
        host.FileSystem = new LinuxFileSystemStats(0x01021994, 4096, 16384, 16380);

        var status = host.Probe().Probe(Required());

        Assert.That(status.Available, Is.False);
        Assert.That(status.Reason, Does.Contain("below the minimum"));
    }

    [Test]
    public void Probe_RefusesADiskBackedRoot()
    {
        var host = new FakeHost();
        host.FileSystem = new LinuxFileSystemStats(0xEF53, 4096, 1L << 30, 1L << 30);

        var status = host.Probe().Probe(Required());

        Assert.That(status.Available, Is.False);
        Assert.That(status.Reason, Does.Contain("tmpfs"));
    }

    [Test]
    public void Probe_RefusesAReadOnlyMount()
    {
        var host = new FakeHost();
        host.FileSystem = host.FileSystem with { ReadOnly = true };

        var status = host.Probe().Probe(Required());

        Assert.That(status.Available, Is.False);
        Assert.That(status.Reason, Does.Contain("read-only"));
    }

    [TestCase(false, true, 0u, "755")]
    [TestCase(true, false, 0u, "755")]
    [TestCase(false, false, 1000u, "755")]
    [TestCase(false, false, 0u, "775")]
    [TestCase(false, false, 0u, "757")]
    public void Probe_RefusesARootOthersCanChange(bool isLink, bool isFile, uint owner, string mode)
    {
        var host = new FakeHost();
        host.Entries["/dev/shm/offloadr"] = new LinuxEntryStatus(!isFile && !isLink, isLink, owner, (UnixFileMode)Convert.ToInt32(mode, 8));

        var status = host.Probe().Probe(Required());

        Assert.That(status.Available, Is.False);
    }

    [TestCase("relative/root")]
    [TestCase("/dev/shm/../tmp")]
    [TestCase("/dev/shm/offloadr/")]
    [TestCase("/dev//shm")]
    [TestCase("/")]
    public void Probe_RefusesAnUnnormalizedRoot(string root)
    {
        var status = new FakeHost().Probe().Probe(Required(root));

        Assert.That(status.Available, Is.False);
        Assert.That(status.Reason, Does.Contain("normalized"));
    }

    [Test]
    public void Probe_ReadsNoswapFromTheInnermostMount()
    {
        var host = new FakeHost();
        host.Files["/proc/self/mounts"] =
            "overlay / overlay rw,relatime 0 0\n" +
            "shm /dev/shm tmpfs rw,nosuid,nodev,noexec,size=10485760k 0 0\n" +
            "tmpfs /dev/shm/offloadr tmpfs rw,noswap,size=8g 0 0\n";
        host.Entries["/dev/shm/offloadr"] = new LinuxEntryStatus(true, false, 0, (UnixFileMode)Convert.ToInt32("711", 8));

        var status = host.Probe().Probe(Required());

        Assert.That(status.Swap.TmpfsNoswap, Is.True);
    }

    [Test]
    public void Probe_IsUnavailableWhenOff()
    {
        var status = new FakeHost().Probe().Probe(new SealedStorageOptions { Mode = SealedStorageMode.Off });

        Assert.That(status.Available, Is.False);
        Assert.That(status.Mode, Is.EqualTo("off"));
    }

    [Test]
    public void DecodeMountField_UndoesOctalEscapes()
    {
        Assert.That(SealedStorageProbe.DecodeMountField(@"/mnt/with\040space"), Is.EqualTo("/mnt/with space"));
    }

    private static SealedStorageOptions Required(string root = "/dev/shm/offloadr")
        => new() { Mode = SealedStorageMode.Required, Root = root };

    private sealed class FakeHost
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal)
        {
            ["/sys/fs/cgroup/memory.max"] = "68719476736\n",
            ["/sys/fs/cgroup/memory.current"] = "0\n",
            ["/proc/self/mounts"] = "shm /dev/shm tmpfs rw,nosuid,nodev,noexec,relatime,size=10485760k,inode64 0 0\n",
            ["/proc/swaps"] = "Filename\tType\tSize\tUsed\tPriority\n",
        };

        public Dictionary<string, LinuxEntryStatus> Entries { get; } = new(StringComparer.Ordinal)
        {
            ["/"] = new LinuxEntryStatus(true, false, 0, (UnixFileMode)Convert.ToInt32("755", 8)),
            ["/dev"] = new LinuxEntryStatus(true, false, 0, (UnixFileMode)Convert.ToInt32("755", 8)),
            ["/dev/shm"] = new LinuxEntryStatus(true, false, 0, (UnixFileMode)Convert.ToInt32("1777", 8)),
        };

        public LinuxFileSystemStats FileSystem { get; set; } = new(0x01021994, 4096, 2621440, 2621440);

        public List<string> StatFsCalls { get; } = [];

        public SealedStorageProbe Probe()
            => new(
                path => Files.GetValueOrDefault(path),
                path =>
                {
                    StatFsCalls.Add(path);
                    return FileSystem;
                },
                path => Entries.TryGetValue(path, out var entry) ? entry : null,
                path => path);
    }
}
