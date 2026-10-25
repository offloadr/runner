using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

[NonParallelizable]
public sealed class DiagnosticRelayLifetimeTests
{
    [Test]
    public async Task DetachedSessionBacklogCannotOutliveItsOriginalCapture()
    {
        var clock = new DiagnosticClock();
        await using var relay = new SessionProcessLogRelay("runner", 8, clock: clock);
        relay.Enqueue("session", SessionProcessLogStream.Stdout, "expired synthetic line");
        clock.Advance(LogRelayBounds.MaximumQueuedAge + TimeSpan.FromSeconds(1));
        var received = new TaskCompletionSource<ReportSessionLogsRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new DiagnosticSink
        {
            Session = (request, _) =>
            {
                received.TrySetResult(request.Clone());
                return Task.CompletedTask;
            }
        };
        relay.AttachSink(sink);
        relay.Enqueue("session", SessionProcessLogStream.Stdout, "fresh synthetic line");
        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        TestContext.Out.WriteLine($"native-age backlog: expired={request.Entries.Count(entry => entry.Message == "expired synthetic line")}");
        Assert.That(request.Entries.Select(entry => entry.Message), Is.EqualTo(new[] { "fresh synthetic line" }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task OriginalAgeCancelsTheOriginalDiagnosticUpload(bool runner)
    {
        var clock = new DiagnosticClock();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new DiagnosticSink
        {
            Session = (_, token) => { entered.TrySetResult(token); return release.Task; },
            Runner = (_, token) => { entered.TrySetResult(token); return release.Task; }
        };
        RunnerLogRelay? runnerRelay = null;
        SessionProcessLogRelay? sessionRelay = null;
        try
        {
            if (runner)
            {
                runnerRelay = new("runner", 8, clock);
                runnerRelay.AttachSink(sink);
                runnerRelay.Enqueue("RunnerAgent", "synthetic blocked line");
            }
            else
            {
                sessionRelay = new("runner", 8, clock: clock);
                sessionRelay.AttachSink(sink);
                sessionRelay.Enqueue("session", SessionProcessLogStream.Stdout, "synthetic blocked line");
            }
            var originalToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            clock.Advance(LogRelayBounds.MaximumQueuedAge + TimeSpan.FromSeconds(1));
            TestContext.Out.WriteLine($"native-age original: runner={runner}; cancelled={originalToken.IsCancellationRequested}");
            Assert.That(originalToken.IsCancellationRequested, Is.True,
                "The original call must be cancelled; an observation timeout must not leave it live.");
        }
        finally
        {
            release.TrySetResult();
            if (runnerRelay is not null) await runnerRelay.DisposeAsync();
            if (sessionRelay is not null) await sessionRelay.DisposeAsync();
        }
    }

    [Test]
    public async Task RetryDiagnosticsCannotPersistArbitraryExceptionText()
    {
        const string sentinel = "synthetic-native-prompt-secret";
        var capture = new DiagnosticLogger();
        using var factory = LoggerFactory.Create(builder => { builder.ClearProviders(); builder.AddProvider(capture); });
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var sink = new DiagnosticSink
        {
            Session = (_, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1) return Task.FromException(new InvalidOperationException(sentinel));
                delivered.TrySetResult();
                return Task.CompletedTask;
            }
        };
        try
        {
            RunnerLog.Configure(factory);
            await using var relay = new SessionProcessLogRelay("runner");
            relay.AttachSink(sink);
            relay.Enqueue("session", SessionProcessLogStream.Stdout, "synthetic line");
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            TestContext.Out.WriteLine($"native-age diagnostics: exceptionObjects={capture.Records.Count(row => row.Exception is not null)}; detailCopies={capture.Records.Count(row => row.Text.Contains(sentinel, StringComparison.Ordinal))}");
            Assert.That(capture.Records, Is.Not.Empty, "The failure must still have a safe diagnostic.");
            Assert.That(capture.Records.Any(row => row.Exception is not null || row.Text.Contains(sentinel, StringComparison.Ordinal)), Is.False);
            Assert.That(capture.Records.Any(row => row.Text.Contains(nameof(InvalidOperationException), StringComparison.Ordinal)), Is.True);
        }
        finally { RunnerLog.Configure(null); }
    }

    private sealed class DiagnosticSink : IRunnerSessionSink
    {
        public Func<ReportSessionLogsRequest, CancellationToken, Task> Session { get; init; } = (_, _) => throw new NotSupportedException();
        public Func<ReportRunnerLogsRequest, CancellationToken, Task> Runner { get; init; } = (_, _) => throw new NotSupportedException();
        public Task<RegisterRunnerResponse> RegisterRunnerAsync(RegisterRunnerRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SyncLocalModelsResponse> SyncLocalModelsAsync(SyncLocalModelsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AckStartSessionAsync(AcknowledgeSessionStartRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AckStopSessionAsync(AcknowledgeSessionStopRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GrantGpuPowerExecutionResponse> GrantGpuPowerExecutionAsync(GrantGpuPowerExecutionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AckGpuPowerLimitAsync(AcknowledgeGpuPowerLimitRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GrantPromptExecutionResponse> GrantPromptExecutionAsync(GrantPromptExecutionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReportPromptEvidenceAsync(ReportPromptEvidenceRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AckSubmitEditorActionAsync(AcknowledgePromptResultRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReportModelDownloadAsync(ReportModelDownloadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReportSessionEventsAsync(ReportSessionEventsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReportSessionLogsAsync(ReportSessionLogsRequest request, CancellationToken cancellationToken) => Session(request, cancellationToken);
        public Task ReportRunnerLogsAsync(ReportRunnerLogsRequest request, CancellationToken cancellationToken) => Runner(request, cancellationToken);
        public Task ReportRuntimeTelemetryAsync(ReportRuntimeTelemetryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReadEditorRuntimeBridgeCursorResponse> ReadEditorRuntimeBridgeCursorAsync(ReadEditorRuntimeBridgeCursorRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReserveEditorRuntimeBridgeResponse> ReserveEditorRuntimeBridgeAsync(ReserveEditorRuntimeBridgeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReportEditorRuntimeBridgeStateResponse> ReportEditorRuntimeBridgeStateAsync(ReportEditorRuntimeBridgeStateRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DiagnosticLogger : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<(string Text, Exception? Exception)> Records { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Records.Enqueue((formatter(state, exception), exception));
        public void Dispose() { }
    }

    internal sealed class DiagnosticClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly HashSet<DiagnosticTimer> _timers = [];
        private DateTimeOffset _utc = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utc; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DiagnosticTimer(this, callback, state);
            lock (_gate) { _timers.Add(timer); timer.Change(dueTime, period); }
            return timer;
        }
        public void Advance(TimeSpan elapsed) => Shift(elapsed, elapsed);
        public void Shift(TimeSpan utc, TimeSpan monotonic)
        {
            Action[] callbacks;
            lock (_gate)
            {
                _utc += utc;
                _ticks += monotonic.Ticks;
                callbacks = _timers.Select(timer => timer.TakeIfDue()).OfType<Action>().ToArray();
            }
            foreach (var callback in callbacks) callback();
        }
        private sealed class DiagnosticTimer(DiagnosticClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long? _due;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    if (_disposed) return false;
                    if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot timers are used.");
                    _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._ticks + dueTime.Ticks;
                    return true;
                }
            }
            public Action? TakeIfDue()
            {
                if (_disposed || _due is not { } due || due > clock._ticks) return null;
                _due = null;
                return () => callback(state);
            }
            public void Dispose() { lock (clock._gate) { _disposed = true; clock._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
