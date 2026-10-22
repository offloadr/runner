namespace Offloadr.Runner.Tests;

public sealed class ArtifactUploadBudgetTests
{
    [TestCase("metadata")]
    [TestCase("body")]
    [TestCase("receipt")]
    public void NoProgressCancelsTheAttemptInEveryPhase(string phase)
    {
        var clock = new UploadClock();
        var policy = TestPolicy(clock);
        var lifetime = new ArtifactUploadBudget(policy);
        using var attempt = lifetime.BeginAttempt(CancellationToken.None);
        if (phase == "body") attempt.BeginBody();
        if (phase == "receipt") attempt.BeginReceipt();
        clock.Advance(TimeSpan.FromMilliseconds(1999));
        attempt.BodyWritten(0);
        Assert.That(attempt.Token.IsCancellationRequested, Is.False);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Multiple(() =>
        {
            Assert.That(attempt.Token.IsCancellationRequested, Is.True);
            Assert.That(attempt.IsTimedOut, Is.True);
            Assert.That(lifetime.IsExpired, Is.False);
        });
    }

    [Test]
    public void PositiveProgressCannotRenewTheLogicalElapsedCeiling()
    {
        var clock = new UploadClock();
        var lifetime = new ArtifactUploadBudget(TestPolicy(clock));
        using var attempt = lifetime.BeginAttempt(CancellationToken.None);
        attempt.BindSnapshot(1000);
        attempt.BeginBody();
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            attempt.BodyWritten(1);
            Assert.That(attempt.Token.IsCancellationRequested, Is.False);
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Multiple(() =>
        {
            Assert.That(attempt.Token.IsCancellationRequested, Is.True);
            Assert.That(attempt.ExpiredAllowance, Is.EqualTo("total elapsed"));
            Assert.That(lifetime.IsExpired, Is.True);
        });
    }

    [Test]
    public void RetryAndTimeBetweenAttemptsConsumeTheOriginalAllowance()
    {
        var clock = new UploadClock();
        var lifetime = new ArtifactUploadBudget(TestPolicy(clock));
        // A queued entry has no active delivery clock until its first dequeue.
        clock.Advance(TimeSpan.FromHours(1));
        using (var first = lifetime.BeginAttempt(CancellationToken.None))
        {
            first.BindSnapshot(1000);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        using var retry = lifetime.BeginAttempt(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        retry.BeginBody();
        clock.Advance(TimeSpan.FromSeconds(1));
        retry.BodyWritten(1);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.That(retry.ExpiredAllowance, Is.EqualTo("total elapsed"));
        Assert.That(retry.Token.IsCancellationRequested, Is.True);
        Assert.Throws<TimeoutException>(() => lifetime.BeginAttempt(CancellationToken.None));
    }

    [Test]
    public void ShutdownCancellationIsNotAnUploadTimeout()
    {
        var clock = new UploadClock();
        using var shutdown = new CancellationTokenSource();
        using var attempt = new ArtifactUploadBudget(TestPolicy(clock)).BeginAttempt(shutdown.Token);
        shutdown.Cancel();
        Assert.Multiple(() =>
        {
            Assert.That(attempt.Token.IsCancellationRequested, Is.True);
            Assert.That(attempt.IsTimedOut, Is.False);
        });
    }

    [Test]
    public void SnapshotSizingBindsOnceWithoutRestartingTheDequeueClock()
    {
        var clock = new UploadClock();
        var lifetime = new ArtifactUploadBudget(TestPolicy(clock));
        using var attempt = lifetime.BeginAttempt(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        attempt.BindSnapshot(1000);
        attempt.BeginBody();
        clock.Advance(TimeSpan.FromSeconds(1));
        attempt.BindSnapshot(1_000_000);
        for (var i = 0; i < 2; i++)
        {
            attempt.BodyWritten(1);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        attempt.BodyWritten(1);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.That(attempt.Token.IsCancellationRequested, Is.True);
        Assert.That(attempt.ExpiredAllowance, Is.EqualTo("total elapsed"));
    }

    [Test]
    public void BindingCannotReviveAnExpiredMetadataAttempt()
    {
        var clock = new UploadClock();
        using var attempt = new ArtifactUploadBudget(TestPolicy(clock)).BeginAttempt(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Throws<OperationCanceledException>(() => attempt.BindSnapshot(1_000_000));
        Assert.That(attempt.Token.IsCancellationRequested, Is.True);
    }

    private static ArtifactUploadPolicy TestPolicy(TimeProvider clock) => new()
    {
        Clock = clock,
        MetadataTimeout = TimeSpan.FromSeconds(2),
        NoProgressTimeout = TimeSpan.FromSeconds(2),
        ReceiptTimeout = TimeSpan.FromSeconds(2),
        MinimumBytesPerSecond = 1000
    };

    private sealed class UploadClock : TimeProvider
    {
        private long _ticks;
        private readonly List<UploadTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new UploadTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            _ticks += duration.Ticks;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue();
        }

        private sealed class UploadTimer(UploadClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long? _due;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot timers are used.");
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._ticks + dueTime.Ticks;
                return true;
            }
            public void FireIfDue()
            {
                if (_disposed || _due is not { } due || due > clock._ticks) return;
                _due = null;
                callback(state);
            }
            public void Dispose()
            {
                _disposed = true;
                clock._timers.Remove(this);
            }
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
