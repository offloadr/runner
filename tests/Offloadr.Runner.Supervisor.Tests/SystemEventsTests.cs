using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Hosting;

namespace Offloadr.Runner.Supervisor.Tests;

public class WindowsSystemEventsTests
{
    [TestCase(WindowsSystemEvents.PowerBroadcast, 0x0004, SystemEvent.Suspending)]
    [TestCase(WindowsSystemEvents.PowerBroadcast, 0x0012, SystemEvent.Resumed)]
    // Follows the automatic resume when someone is at the computer: not acted on twice.
    [TestCase(WindowsSystemEvents.PowerBroadcast, 0x0007, SystemEvent.None)]
    // A power setting or battery change.
    [TestCase(WindowsSystemEvents.PowerBroadcast, 0x8013, SystemEvent.None)]
    [TestCase(WindowsSystemEvents.QueryEndSession, 0, SystemEvent.SessionEndQuery)]
    [TestCase(WindowsSystemEvents.EndSession, 1, SystemEvent.SessionEnding)]
    [TestCase(WindowsSystemEvents.EndSession, 0, SystemEvent.SessionEndCancelled)]
    [TestCase(0x0010u, 0, SystemEvent.None)]
    public void Decode_ReadsSleepAndTheEndOfTheWindowsSession(uint message, long wParam, SystemEvent expected)
        => Assert.That(WindowsSystemEvents.Decode(message, (nint)wParam), Is.EqualTo(expected));
}

public class SessionKeepAwakeTests
{
    private static SupervisorView View(string activeSessionId) => SupervisorView.Initial(DateTimeOffset.UtcNow) with
    {
        RunnerStatus = new RunnerStatusSnapshot("runner", "instance", "1.0", [], activeSessionId, DateTimeOffset.UtcNow),
    };

    [Test]
    public void KeepsAwakeOnlyWhileASessionRuns_AndTheSettingAllowsIt()
    {
        var keepAwake = new RecordingKeepAwake();
        var view = View("");
        var on = new Settings.RunnerSettings();
        var off = on with { KeepAwakeDuringSessions = false };
        var settings = on;
        using var session = new SessionKeepAwake(keepAwake, () => view, () => settings);

        session.Refresh();
        view = View("session-1");
        session.Refresh();
        session.Refresh();
        settings = off;
        session.Refresh();
        settings = on;
        session.Refresh();
        view = View("");
        session.Refresh();

        Assert.That(keepAwake.Calls, Is.EqualTo(new[] { true, false, true, false }), "Only changes reach the power request.");
    }

    [Test]
    public async Task ARefreshHeldUpBehindANewerOne_CannotApplyAnOlderSetting()
    {
        // A refresh that read Keep awake off is held up while the setting is turned on and a second refresh
        // runs. The second must still decide last, so the computer stays awake for the running session.
        var keepAwake = new RecordingKeepAwake();
        var view = View("session-1");
        var settings = new Settings.RunnerSettings { KeepAwakeDuringSessions = false };
        using var read = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holdNextRead = true;
        Settings.RunnerSettings ReadSettings()
        {
            var current = Volatile.Read(ref settings);
            if (Interlocked.Exchange(ref holdNextRead, false))
            {
                read.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }

            return current;
        }

        using var session = new SessionKeepAwake(keepAwake, () => view, ReadSettings);
        // Becomes awake first, so the held refresh's stale "off" would clear the request.
        Volatile.Write(ref holdNextRead, false);
        Volatile.Write(ref settings, settings with { KeepAwakeDuringSessions = true });
        session.Refresh();
        Volatile.Write(ref settings, settings with { KeepAwakeDuringSessions = false });
        Volatile.Write(ref holdNextRead, true);

        var older = Task.Run(session.Refresh);
        Assert.That(read.Wait(TimeSpan.FromSeconds(10)), Is.True);
        Volatile.Write(ref settings, settings with { KeepAwakeDuringSessions = true });
        var newer = Task.Run(session.Refresh);
        // Lets the newer refresh finish first if it can: one that does not wait for the held one would.
        newer.Wait(TimeSpan.FromMilliseconds(300));
        release.Set();
        await Task.WhenAll(older, newer).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(keepAwake.Calls[^1], Is.True, "The newer setting decides last.");
    }

    [Test]
    public void Dispose_LetsTheComputerSleepAgain_AndIgnoresWhatComesAfter()
    {
        var keepAwake = new RecordingKeepAwake();
        var view = View("session-1");
        var session = new SessionKeepAwake(keepAwake, () => view, () => new Settings.RunnerSettings());
        session.Refresh();

        session.Dispose();
        view = View("session-2");
        session.Refresh();
        session.Dispose();

        Assert.That(keepAwake.Calls, Is.EqualTo(new[] { true, false }));
    }

    private sealed class RecordingKeepAwake : IKeepAwake
    {
        private readonly object _gate = new();
        private readonly List<bool> _calls = [];

        public IReadOnlyList<bool> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public void Set(bool awake)
        {
            lock (_gate)
            {
                _calls.Add(awake);
            }
        }
    }
}
