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
        using var session = new SessionKeepAwake(keepAwake);
        var on = new Settings.RunnerSettings();
        var off = on with { KeepAwakeDuringSessions = false };

        session.Apply(View(""), on);
        session.Apply(View("session-1"), on);
        session.Apply(View("session-1"), on);
        session.Apply(View("session-1"), off);
        session.Apply(View("session-1"), on);
        session.Apply(View(""), on);

        Assert.That(keepAwake.Calls, Is.EqualTo(new[] { true, false, true, false }), "Only changes reach the power request.");
    }

    [Test]
    public void Dispose_LetsTheComputerSleepAgain_AndIgnoresWhatComesAfter()
    {
        var keepAwake = new RecordingKeepAwake();
        var session = new SessionKeepAwake(keepAwake);
        session.Apply(View("session-1"), new Settings.RunnerSettings());

        session.Dispose();
        session.Apply(View("session-2"), new Settings.RunnerSettings());
        session.Dispose();

        Assert.That(keepAwake.Calls, Is.EqualTo(new[] { true, false }));
    }

    private sealed class RecordingKeepAwake : IKeepAwake
    {
        public List<bool> Calls { get; } = [];

        public void Set(bool awake) => Calls.Add(awake);
    }
}
