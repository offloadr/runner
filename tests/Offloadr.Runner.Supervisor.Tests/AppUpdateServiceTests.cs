using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.Runner.Supervisor.Hosting;

namespace Offloadr.Runner.Supervisor.Tests;

public class AppUpdateServiceTests
{
    private static readonly Version Current = new(1, 2, 0);

    [Test]
    public async Task ANewerRelease_IsDownloadedAndThenReady()
    {
        var source = new FakeAppUpdateSource { Latest = new Version(1, 3, 0) };
        using var updates = Create(source);
        var states = new List<AppUpdateState>();
        updates.Changed += () => states.Add(updates.Status.State);

        await updates.CheckAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(updates.Status, Is.EqualTo(new AppUpdateStatus(AppUpdateState.Ready, Current, new Version(1, 3, 0), 100)));
            Assert.That(source.Downloads, Is.EqualTo(1));
            Assert.That(states, Is.EqualTo(new[] { AppUpdateState.Checking, AppUpdateState.Downloading, AppUpdateState.Downloading, AppUpdateState.Downloading, AppUpdateState.Ready }));
        });
    }

    [Test]
    public async Task NoNewerRelease_IsCurrentAndDownloadsNothing()
    {
        var source = new FakeAppUpdateSource { Latest = Current };
        using var updates = Create(source);

        await updates.CheckAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(updates.Status.State, Is.EqualTo(AppUpdateState.Current));
            Assert.That(source.Downloads, Is.Zero);
        });
    }

    [Test]
    public async Task ACopyThatWasNotInstalled_NeverChecks()
    {
        var source = new FakeAppUpdateSource { IsInstalled = false, Latest = new Version(9, 0, 0) };
        using var updates = Create(source);

        await updates.CheckAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(updates.Status.State, Is.EqualTo(AppUpdateState.NotInstalled));
            Assert.That(source.Checks, Is.Zero);
        });
    }

    [Test]
    public void AReleaseDownloadedBeforeARestart_IsReadyAtOnce()
    {
        using var updates = Create(new FakeAppUpdateSource { Pending = new Version(1, 3, 0) });

        Assert.That(updates.Status, Is.EqualTo(new AppUpdateStatus(AppUpdateState.Ready, Current, new Version(1, 3, 0), 100)));
    }

    [Test]
    public async Task AFailedCheck_IsReportedAndTheNextOneSucceeds()
    {
        var source = new FakeAppUpdateSource { Latest = new Version(1, 3, 0), CheckFailure = new HttpRequestException("offline") };
        using var updates = Create(source);

        await updates.CheckAsync(CancellationToken.None);
        Assert.That(updates.Status.State, Is.EqualTo(AppUpdateState.Failed));
        Assert.That(updates.Status.Problem, Does.Not.Contain("offline"), "The person sees a plain message; the detail goes to the log.");

        source.CheckFailure = null;
        await updates.CheckAsync(CancellationToken.None);
        Assert.That(updates.Status.State, Is.EqualTo(AppUpdateState.Ready));
    }

    [Test]
    public async Task OnceReady_LaterChecksLeaveTheDownloadAlone()
    {
        var source = new FakeAppUpdateSource { Latest = new Version(1, 3, 0) };
        using var updates = Create(source);
        await updates.CheckAsync(CancellationToken.None);

        source.Latest = new Version(1, 4, 0);
        await updates.CheckAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(updates.Status.Available, Is.EqualTo(new Version(1, 3, 0)));
            Assert.That(source.Checks, Is.EqualTo(1));
        });
    }

    [TestCase(SupervisorState.Stopped, true)]
    [TestCase(SupervisorState.Running, true)]
    [TestCase(SupervisorState.RestartPending, true)]
    [TestCase(SupervisorState.Faulted, true)]
    [TestCase(SupervisorState.Pulling, false)]
    [TestCase(SupervisorState.Starting, false)]
    [TestCase(SupervisorState.Stopping, false)]
    public async Task ARestart_WaitsWhileTheRunnerIsBeingStartedOrStopped(SupervisorState state, bool allowed)
    {
        using var updates = Create(new FakeAppUpdateSource { Latest = new Version(1, 3, 0) });
        await updates.CheckAsync(CancellationToken.None);

        var view = SupervisorView.Initial(DateTimeOffset.UtcNow) with { State = state };

        Assert.That(updates.CanRestartNow(view), Is.EqualTo(allowed));
    }

    [Test]
    public async Task ApplyAfterExit_HandsTheArgumentsToTheInstaller_OnlyOnceReady()
    {
        var source = new FakeAppUpdateSource { Latest = new Version(1, 3, 0) };
        using var updates = Create(source);
        Assert.That(() => updates.ApplyAfterExit(restart: true, []), Throws.InvalidOperationException);

        await updates.CheckAsync(CancellationToken.None);
        updates.ApplyAfterExit(restart: true, ["--minimized"]);

        Assert.Multiple(() =>
        {
            Assert.That(source.AppliedWith, Is.EqualTo(new[] { "--minimized" }));
            Assert.That(source.Restarts, Is.True);
        });
    }

    [Test]
    public async Task BackgroundService_ChecksAfterTheInitialDelay()
    {
        var source = new FakeAppUpdateSource { Latest = new Version(1, 5, 0) };
        using var updates = new AppUpdateService(source, Current, NullLogger<AppUpdateService>.Instance, TimeSpan.FromMilliseconds(10), TimeSpan.FromHours(1));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        updates.Changed += () =>
        {
            if (updates.Status.State == AppUpdateState.Ready)
            {
                ready.TrySetResult();
            }
        };

        await updates.StartAsync(CancellationToken.None);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await updates.StopAsync(CancellationToken.None);

        Assert.That(updates.Status.Available, Is.EqualTo(new Version(1, 5, 0)));
    }

    [TestCase("0.1.0+9a681a2", 0, 1, 0)]
    [TestCase("v2.3", 2, 3, 0)]
    [TestCase("1.2.3.4", 1, 2, 3)]
    public void TryParseVersion_NormalizesToThreeParts(string text, int major, int minor, int build)
    {
        Assert.Multiple(() =>
        {
            Assert.That(AppUpdateService.TryParseVersion(text, out var version), Is.True);
            Assert.That(version, Is.EqualTo(new Version(major, minor, build)));
        });
    }

    [TestCase("1.2.3-beta")]
    [TestCase("not a version")]
    [TestCase(null)]
    public void TryParseVersion_RejectsWhatIsNoRelease(string? text)
        => Assert.That(AppUpdateService.TryParseVersion(text, out _), Is.False);

    private static AppUpdateService Create(FakeAppUpdateSource source) => new(source, Current, NullLogger<AppUpdateService>.Instance);
}
