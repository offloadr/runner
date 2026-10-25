using System.Globalization;
using Microsoft.Extensions.Logging;
using Offloadr.Runner.Supervisor.Logging;

namespace Offloadr.Runner.Supervisor.Tests;

public sealed class DesktopDiagnosticLifetimeTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "offloadr-desktop-diagnostic-lifetime-" + Guid.NewGuid().ToString("n"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        else if (File.Exists(_root)) File.Delete(_root);
    }

    [Test]
    public void IdleSweepExpiresTheOriginalHourlyFileWithoutAnotherWrite()
    {
        var clock = new FileClock();
        using var provider = new DesktopDiagnosticLogProvider(_root, clock);
        var logger = provider.CreateLogger("Offloadr.Runner.Supervisor.RunnerSupervisor");
        logger.LogWarning("Synthetic operational event");
        var first = Directory.GetFiles(_root).Single();
        clock.Advance(TimeSpan.FromHours(23));
        logger.LogWarning("Later synthetic operational event");
        Assert.That(File.Exists(first), Is.True, "Positive control before the original hour expires.");
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(first), Is.False);
            Assert.That(Directory.GetFiles(_root), Has.Length.EqualTo(1), "The newer file must survive the same sweep.");
        });
    }

    [Test]
    public void FileNamesAndExpiryRemainStableWithANonGregorianUserLocale()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            var clock = new FileClock();
            using var provider = new DesktopDiagnosticLogProvider(_root, clock);
            provider.CreateLogger("Offloadr.Runner.Supervisor.RunnerSupervisor").LogWarning("Synthetic event");
            Assert.That(Path.GetFileName(Directory.GetFiles(_root).Single()), Is.EqualTo("diagnostic-2026102500.log"));
            clock.Advance(TimeSpan.FromDays(1));
            Assert.That(Directory.GetFiles(_root), Is.Empty);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Test]
    public void AnHourlyFileCannotGrowPastItsByteBound()
    {
        using var provider = new DesktopDiagnosticLogProvider(_root, new FileClock());
        var logger = provider.CreateLogger("Offloadr.Runner.Supervisor.RunnerSupervisor");
        for (var i = 0; i < 1200; i++) logger.LogWarning("Synthetic bounded operational event");
        var file = new FileInfo(Directory.GetFiles(_root).Single());
        Assert.That(file.Length, Is.InRange(DesktopDiagnosticLogProvider.MaximumFileBytes - 256, DesktopDiagnosticLogProvider.MaximumFileBytes));
        Assert.That(File.ReadAllText(file.FullName).EndsWith('\n'), Is.True, "A full file must not contain a partial record.");
    }

    [Test]
    public void ExpiredAndFutureOwnedFilesAreRemovedAtStartup()
    {
        Directory.CreateDirectory(_root);
        var owned = new[] { "diagnostic-2026102400.log", "diagnostic-2026102700.log" };
        foreach (var name in owned) File.WriteAllText(Path.Combine(_root, name), "synthetic-old-metadata");
        var unrelated = Path.Combine(_root, "diagnostic-not-a-time.log");
        File.WriteAllText(unrelated, "preserve");
        using var provider = new DesktopDiagnosticLogProvider(_root, new FileClock());
        Assert.Multiple(() =>
        {
            Assert.That(owned.Where(name => File.Exists(Path.Combine(_root, name))), Is.Empty);
            Assert.That(File.ReadAllText(unrelated), Is.EqualTo("preserve"));
        });
    }

    [Test]
    public void DisposalFencesRetainedLoggersAndQueuedTimerCallbacks()
    {
        var clock = new FileClock();
        var provider = new DesktopDiagnosticLogProvider(_root, clock);
        var logger = provider.CreateLogger("Offloadr.Runner.Supervisor.RunnerSupervisor");
        logger.LogWarning("Before disposal");
        var before = Directory.GetFiles(_root).ToDictionary(file => file, File.ReadAllText);
        provider.Dispose();
        clock.Advance(TimeSpan.FromDays(2), includeDisposed: true);
        logger.LogError("After disposal");
        Assert.That(Directory.GetFiles(_root).ToDictionary(file => file, File.ReadAllText), Is.EquivalentTo(before));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UnavailableFolderDoesNotExposeAFallbackOrFailTheOperation(bool startup)
    {
        File.WriteAllText(_root, "preserve-unrelated-file");
        Assert.DoesNotThrow(() =>
        {
            if (startup) DesktopStartupDiagnostics.WriteError(_root, new IOException("synthetic-private-detail"));
            else
            {
                using var provider = new DesktopDiagnosticLogProvider(_root);
                provider.CreateLogger("Offloadr.Runner.Supervisor.RunnerSupervisor").LogError("synthetic-private-detail");
            }
        });
        Assert.That(File.ReadAllText(_root), Is.EqualTo("preserve-unrelated-file"));
    }

    private sealed class FileClock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        private readonly List<FileTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _utc;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.That(dueTime, Is.EqualTo(DesktopDiagnosticLogProvider.SweepInterval));
            Assert.That(period, Is.EqualTo(dueTime));
            var timer = new FileTimer(() => callback(state));
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan elapsed, bool includeDisposed = false)
        {
            _utc += elapsed;
            foreach (var timer in _timers) if (includeDisposed || !timer.Disposed) timer.Fire();
        }
        private sealed class FileTimer(Action callback) : ITimer
        {
            public bool Disposed { get; private set; }
            public void Fire() => callback();
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
