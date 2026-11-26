using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.Common.V1;
using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hardware;
using Offloadr.Runner.Supervisor.Launcher;
using Offloadr.Runner.Supervisor.Settings;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Supervisor.Tests;

public class LauncherLinkTests
{
    private const string Key = "c2VjcmV0LWtleS1mb3ItdGhlLWxhdW5jaGVyLWxpbmstdGVzdHM=";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private static readonly RunnerSettings Settings = new() { RunnerKey = Key, OffloadrApiOrigin = "https://offloadr.example" };

    private static readonly LauncherLinkOptions FastOptions = new()
    {
        RegistrationMinBackoff = TimeSpan.FromMilliseconds(10),
        RegistrationMaxBackoff = TimeSpan.FromMilliseconds(20),
        StreamMinBackoff = TimeSpan.FromMilliseconds(10),
        StreamMaxBackoff = TimeSpan.FromMilliseconds(20),
        ReportMinBackoff = TimeSpan.FromMilliseconds(10),
        ReportMaxBackoff = TimeSpan.FromMilliseconds(20),
    };

    private static long _sequence = 100;
    private FakeLauncherControlPlane _server = null!;
    private CancellationTokenSource _stop = null!;

    [SetUp]
    public void SetUp()
    {
        _server = new FakeLauncherControlPlane();
        _stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    }

    [TearDown]
    public void TearDown()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    [Test]
    public async Task RegistersAsALauncherForTheKeysRunner_AndOpensTheStreamOnlyOnceAccepted()
    {
        var accept = false;
        _server.Register = _ => accept
            ? new RegisterRunnerResponse { Accepted = true, RefreshSeconds = 1 }
            : new RegisterRunnerResponse { Accepted = false, Message = "Not yet." };
        var link = NewLink();
        var run = RunAsync(link);

        await WaitUntilAsync(() => link.State.Connection == LauncherConnection.Refused);
        Assert.That(link.State.Message, Is.EqualTo("Not yet."));
        Assert.That(_server.StreamRequests, Is.Empty, "the stream is fenced on a registered launcher");

        accept = true;
        await WaitUntilAsync(() => _server.StreamRequests.Count == 1);
        var registration = _server.Registrations.Last();
        var stream = _server.StreamRequests.Single();
        Assert.Multiple(() =>
        {
            Assert.That(link.State.Connection, Is.EqualTo(LauncherConnection.Online));
            Assert.That(registration.Role, Is.EqualTo(RunnerRole.Launcher));
            Assert.That(registration.RunnerId, Is.EqualTo(RunnerAgentOptions.ResolveRunnerId(Key, null)), "the same runner the agent registers with this key");
            Assert.That(registration.InstanceId, Is.EqualTo(link.InstanceId));
            Assert.That(registration.ProtocolVersion, Is.EqualTo(RunnerProtocolVersion.Current));
            Assert.That(registration.SupportedEditorTemplates, Is.EqualTo(new[] { "comfyui-latest", "forge-neo" }));
            Assert.That(registration.Resources.GpuModelName, Is.EqualTo("NVIDIA GeForce RTX 4090"));
            Assert.That(registration.Runtime, Is.Null);
            Assert.That(registration.SealedStorage, Is.Null);
            Assert.That(registration.LaunchId, Is.Empty);
            Assert.That(registration.OfferRevision, Is.EqualTo(7UL), "each launch is checked against the offer it names");
            Assert.That(link.AcceptedOfferRevision, Is.EqualTo(7UL), "Offloadr accepted that offer");
            Assert.That(stream.RunnerId, Is.EqualTo(registration.RunnerId));
            Assert.That(stream.InstanceId, Is.EqualTo(link.InstanceId));
            Assert.That(stream.ProtocolVersion, Is.EqualTo(RunnerProtocolVersion.Current));
            Assert.That(_server.Keys, Is.All.EqualTo(Key));
        });

        _stop.Cancel();
        await run;
        Assert.That(link.State, Is.EqualTo(LauncherLinkState.Offline));
    }

    [Test]
    public async Task OnlyTheLatestStateOnALiveStreamLetsALaunchStart()
    {
        var link = NewLink();
        var run = RunAsync(link);
        var first = await _server.NextStreamAsync();
        var launch = NewLaunch(1);

        Assert.That(MayStart(link, launch.LaunchId), Is.False, "nothing before the stream's first state");
        await first.WriteAsync(State(launch));
        await WaitUntilAsync(() => MayStart(link, launch.LaunchId));
        Assert.That(MayStart(link, Guid.NewGuid().ToString()), Is.False);

        first.Complete();
        await WaitUntilAsync(() => !link.State.StreamLive);
        Assert.Multiple(() =>
        {
            Assert.That(MayStart(link, launch.LaunchId), Is.False, "a broken stream starts nothing");
            Assert.That(link.State.Launch?.LaunchId, Is.EqualTo(launch.LaunchId), "kept, so a download can go on");
        });

        var second = await _server.NextStreamAsync();
        await Task.Delay(50);
        Assert.That(MayStart(link, launch.LaunchId), Is.False, "after a reconnect, only once the new stream's first state arrives");

        await second.WriteAsync(State(null));
        await WaitUntilAsync(() => link.State.StreamLive);
        Assert.Multiple(() =>
        {
            Assert.That(link.State.Launch, Is.Null, "withdrawn while the stream was down");
            Assert.That(MayStart(link, launch.LaunchId), Is.False);
        });

        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task TryStart_DispatchesOnlyWhatMayStart_AndNoStateLandsMeanwhile()
    {
        var link = NewLink();
        var run = RunAsync(link);
        var stream = await _server.NextStreamAsync();
        var launch = NewLaunch(1);
        await stream.WriteAsync(State(launch));
        await WaitUntilAsync(() => MayStart(link, launch.LaunchId));
        var named = link.State.Launch!;
        var dispatched = 0;

        Assert.That(link.TryStart(named with { LaunchId = Guid.NewGuid().ToString() }, () => dispatched++), Is.False, "not the launch named");

        LauncherLinkState? seenDuring = null;
        var withdrawal = Task.CompletedTask;
        var started = link.TryStart(named, () =>
        {
            dispatched++;
            // A withdrawal arriving now waits until the start is handed over.
            withdrawal = Task.Run(async () => await stream.WriteAsync(State(null)));
            Thread.Sleep(200);
            seenDuring = link.State;
        });
        await withdrawal;
        await WaitUntilAsync(() => link.State.Launch is null);

        Assert.Multiple(() =>
        {
            Assert.That(started, Is.True);
            Assert.That(dispatched, Is.EqualTo(1));
            Assert.That(seenDuring?.Launch, Is.EqualTo(named), "the state stays as checked while the start is handed over");
            Assert.That(link.TryStart(named, () => dispatched++), Is.False, "withdrawn afterwards");
        });

        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task AStreamSilentForTheKeepaliveTimeoutIsReconnected_KeepalivesKeepItOpen()
    {
        // Keepalives 100 ms apart against a 1.5 s timeout: a busy machine cannot make them look late.
        var link = NewLink(FastOptions with { KeepAliveTimeout = TimeSpan.FromMilliseconds(1500) });
        var run = RunAsync(link);
        var stream = await _server.NextStreamAsync();
        var launch = NewLaunch(1);
        await stream.WriteAsync(State(launch));

        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(100);
            await stream.WriteAsync(new LauncherStreamResponse { Keepalive = new LauncherStreamKeepAlive() });
        }

        Assert.Multiple(() =>
        {
            Assert.That(_server.StreamRequests, Has.Count.EqualTo(1));
            Assert.That(MayStart(link, launch.LaunchId), Is.True);
        });

        await _server.NextStreamAsync();
        Assert.Multiple(() =>
        {
            Assert.That(_server.StreamRequests, Has.Count.EqualTo(2), "silence counts as a broken stream");
            Assert.That(MayStart(link, launch.LaunchId), Is.False);
        });

        _stop.Cancel();
        await run;
    }

    [TestCase(RegistrationRejection.RunnerOutdated, LauncherConnection.Outdated)]
    [TestCase(RegistrationRejection.ServerOutdated, LauncherConnection.Outdated)]
    [TestCase(RegistrationRejection.LauncherSuperseded, LauncherConnection.Superseded)]
    public async Task AFinalRefusalEndsTheRun(RegistrationRejection rejection, LauncherConnection expected)
    {
        _server.Register = _ => new RegisterRunnerResponse { Accepted = false, Rejection = rejection, Message = "No." };
        var link = NewLink();

        await link.RunAsync(Settings, Profile, _stop.Token).WaitAsync(WaitLimit);

        Assert.Multiple(() =>
        {
            Assert.That(link.State.Connection, Is.EqualTo(expected));
            Assert.That(link.State.IsFinal, Is.True);
            Assert.That(link.State.Message, Is.Not.Empty);
            Assert.That(_server.Registrations, Has.Count.EqualTo(1), "never retried");
            Assert.That(_server.StreamRequests, Is.Empty);
        });
    }

    [Test]
    public async Task ALauncherStartedAgainAfterATakeoverIsANewInstance_AndAPlainRestartIsNot()
    {
        var link = NewLink();
        var first = link.InstanceId;
        using (var stop = new CancellationTokenSource())
        {
            var run = Task.Run(() => link.RunAsync(Settings, Profile, stop.Token));
            await _server.NextStreamAsync();
            await stop.CancelAsync();
            await run.WaitAsync(WaitLimit);
        }

        Assert.That(link.InstanceId, Is.EqualTo(first), "the same launcher coming back");

        _server.Register = _ => new RegisterRunnerResponse { Accepted = false, Rejection = RegistrationRejection.LauncherSuperseded };
        await link.RunAsync(Settings, Profile, _stop.Token).WaitAsync(WaitLimit);
        Assert.That(link.State.Connection, Is.EqualTo(LauncherConnection.Superseded));

        // A start that fails on its settings does not use up the takeover.
        await link.RunAsync(Settings with { RunnerKey = null }, Profile, _stop.Token).WaitAsync(WaitLimit);
        _server.Register = _ => new RegisterRunnerResponse { Accepted = true, RefreshSeconds = 1 };
        var again = RunAsync(link);
        await _server.NextStreamAsync();

        Assert.Multiple(() =>
        {
            Assert.That(link.InstanceId, Is.Not.EqualTo(first));
            Assert.That(_server.Registrations.Last().InstanceId, Is.EqualTo(link.InstanceId));
        });
        _stop.Cancel();
        await again;
    }

    [Test]
    public async Task ATakeoverOnOneAddressLeavesAnotherAddresssInstanceAsItWas()
    {
        var link = NewLink();
        var other = Settings with { OffloadrApiOrigin = "https://other.example" };
        using (var stop = new CancellationTokenSource())
        {
            var run = Task.Run(() => link.RunAsync(Settings, Profile, stop.Token));
            await _server.NextStreamAsync();
            await stop.CancelAsync();
            await run.WaitAsync(WaitLimit);
        }

        var onA = link.InstanceId;
        _server.Register = _ => new RegisterRunnerResponse { Accepted = false, Rejection = RegistrationRejection.LauncherSuperseded };
        await link.RunAsync(other, Profile, _stop.Token).WaitAsync(WaitLimit);
        Assert.That(link.InstanceId, Is.Not.EqualTo(onA), "another address is another instance");

        _server.Register = _ => new RegisterRunnerResponse { Accepted = true, RefreshSeconds = 1 };
        var back = RunAsync(link);
        await _server.NextStreamAsync();

        Assert.That(link.InstanceId, Is.EqualTo(onA), "the takeover was on the other address");
        _stop.Cancel();
        await back;
    }

    [TestCase(StatusCode.Unauthenticated, LauncherConnection.Refused, "runner key")]
    [TestCase(StatusCode.PermissionDenied, LauncherConnection.Refused, "runner key")]
    [TestCase(StatusCode.Unavailable, LauncherConnection.Unreachable, "Could not reach")]
    public async Task AFailedRegistrationSaysWhy_AndIsRetried(StatusCode status, LauncherConnection expected, string message)
    {
        _server.Register = _ => throw new RpcException(new Status(status, "detail"));
        var link = NewLink();
        var run = RunAsync(link);

        await WaitUntilAsync(() => _server.Registrations.Count >= 3);
        Assert.Multiple(() =>
        {
            Assert.That(link.State.Connection, Is.EqualTo(expected));
            Assert.That(link.State.Message, Does.Contain(message));
        });

        _stop.Cancel();
        await run;
    }

    [TestCase("http://offloadr.example", Key)]
    [TestCase("https://offloadr.example", "short")]
    [TestCase("https://offloadr.example", null)]
    public async Task UnusableSettingsSendNothing(string origin, string? key)
    {
        var link = NewLink();

        await link.RunAsync(new RunnerSettings { OffloadrApiOrigin = origin, RunnerKey = key }, Profile, _stop.Token).WaitAsync(WaitLimit);

        Assert.Multiple(() =>
        {
            Assert.That(link.State.Connection, Is.EqualTo(LauncherConnection.Refused));
            Assert.That(_server.Registrations, Is.Empty);
        });
    }

    [Test]
    public async Task ATerminalReportIsResentUnchangedUntilAccepted()
    {
        var link = NewLink();
        var run = RunAsync(link);
        var launchId = await NameAsync(link, await _server.NextStreamAsync());
        var failures = 2;
        _server.Report = _ => failures-- > 0 ? new RpcException(new Status(StatusCode.Unavailable, "down")) : null;

        await link.ReportAsync(new LaunchReport(launchId, RunnerLaunchPhase.Started), _stop.Token).WaitAsync(WaitLimit);

        Assert.That(_server.Reports, Has.Count.EqualTo(3));
        Assert.That(_server.Reports.Distinct().Count(), Is.EqualTo(1), "the same content every time");
        Assert.That(_server.Reports[0].InstanceId, Is.EqualTo(link.InstanceId));
        Assert.That(_server.Reports[0].Phase, Is.EqualTo(RunnerLaunchPhase.Started));

        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task AReportOffloadrRefusesForGoodIsSentOnce_AndNotRepeated()
    {
        var link = NewLink(FastOptions with { ProgressRepeat = TimeSpan.FromMilliseconds(50) });
        var run = RunAsync(link);
        var stream = await _server.NextStreamAsync();
        var failed = await NameAsync(link, stream);
        var preparing = await NameAsync(link, stream);
        _server.Report = _ => new RpcException(new Status(StatusCode.InvalidArgument, "malformed"));

        await link.ReportAsync(new LaunchReport(failed, RunnerLaunchPhase.Failed, "No image."), _stop.Token).WaitAsync(WaitLimit);
        await link.ReportAsync(new LaunchReport(preparing, RunnerLaunchPhase.Preparing), _stop.Token).WaitAsync(WaitLimit);
        await Task.Delay(200);

        Assert.That(_server.Reports.Select(report => report.Phase), Is.EqualTo(new[] { RunnerLaunchPhase.Failed, RunnerLaunchPhase.Preparing }));

        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task AReportForALaunchThisRunWasNotSentIsDropped()
    {
        var link = NewLink();
        var run = RunAsync(link);
        await NameAsync(link, await _server.NextStreamAsync());

        await link.ReportAsync(new LaunchReport(new LaunchRequest(Guid.NewGuid().ToString(), 1, "comfyui-latest", "image"), RunnerLaunchPhase.Started), _stop.Token).WaitAsync(WaitLimit);

        Assert.That(_server.Reports, Is.Empty, "a launch belongs to the run whose stream named it");
        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task ProgressOfALaunchThatWasReplacedIsDropped_ItsOutcomeIsNot()
    {
        var link = NewLink();
        var run = RunAsync(link);
        var stream = await _server.NextStreamAsync();
        var replaced = await NameAsync(link, stream);
        var current = await NameAsync(link, stream);

        await link.ReportAsync(new LaunchReport(current, RunnerLaunchPhase.Downloading, DownloadedBytes: 1, TotalBytes: 2), _stop.Token);
        await link.ReportAsync(new LaunchReport(replaced, RunnerLaunchPhase.Downloading, DownloadedBytes: 1, TotalBytes: 2), _stop.Token);
        await link.ReportAsync(new LaunchReport(replaced, RunnerLaunchPhase.Superseded), _stop.Token);

        Assert.That(_server.Reports.Select(report => (report.LaunchId, report.Phase)), Is.EqualTo(new[]
        {
            (current.LaunchId, RunnerLaunchPhase.Downloading),
            (replaced.LaunchId, RunnerLaunchPhase.Superseded),
        }));
        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task AWithdrawnLaunchsProgressIsNotRepeated()
    {
        var link = NewLink(FastOptions with { ProgressRepeat = TimeSpan.FromMilliseconds(30) });
        var run = RunAsync(link);
        var stream = await _server.NextStreamAsync();
        var launchId = await NameAsync(link, stream);
        await link.ReportAsync(new LaunchReport(launchId, RunnerLaunchPhase.Downloading, DownloadedBytes: 1, TotalBytes: 2), _stop.Token);
        await WaitUntilAsync(() => _server.Reports.Count >= 2);

        await stream.WriteAsync(State(null));
        await WaitUntilAsync(() => link.State.Launch is null);
        var sent = _server.Reports.Count;
        await Task.Delay(200);

        Assert.That(_server.Reports, Has.Count.EqualTo(sent));
        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task ALaunchHasOneOutcome_ALaterDifferentOneIsDropped()
    {
        var link = NewLink();
        var run = RunAsync(link);
        var launchId = await NameAsync(link, await _server.NextStreamAsync());

        await link.ReportAsync(new LaunchReport(launchId, RunnerLaunchPhase.Started), _stop.Token);
        await link.ReportAsync(new LaunchReport(launchId, RunnerLaunchPhase.Failed, "Lost track of it."), _stop.Token);
        await link.ReportAsync(new LaunchReport(launchId, RunnerLaunchPhase.Started), _stop.Token);

        Assert.That(_server.Reports.Select(report => report.Phase), Is.EqualTo(new[] { RunnerLaunchPhase.Started }),
            "the first outcome is the acknowledgement; once answered, a repeat sends nothing and another outcome is dropped");
        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task DisposingTheLinkEndsItsRun()
    {
        var link = NewLink();
        var run = RunAsync(link);
        await _server.NextStreamAsync();

        link.Dispose();
        await run.WaitAsync(WaitLimit);
        var registrations = _server.Registrations.Count;
        await Task.Delay(100);

        Assert.Multiple(() =>
        {
            Assert.That(link.State, Is.EqualTo(LauncherLinkState.Offline));
            Assert.That(_server.Registrations, Has.Count.EqualTo(registrations));
        });
        Assert.ThrowsAsync<ObjectDisposedException>(() => link.RunAsync(Settings, Profile, _stop.Token));
    }

    [Test]
    public async Task OldAnsweredOutcomesAreForgotten_TheLatestOnesKept()
    {
        var link = NewLink();
        var run = RunAsync(link);
        var stream = await _server.NextStreamAsync();
        var launches = new List<LaunchRequest>();
        for (var i = 0; i < 40; i++)
        {
            var launch = await NameAsync(link, stream);
            launches.Add(launch);
            await link.ReportAsync(new LaunchReport(launch, RunnerLaunchPhase.Started), _stop.Token);
        }

        Assert.Multiple(() =>
        {
            Assert.That(link.OutcomeOf(launches[0].LaunchId), Is.Null, "answered long ago");
            Assert.That(link.OutcomeOf(launches[^1].LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started));
            Assert.That(link.OutcomeOf(launches[^30].LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Started));
        });
        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task AnOldLaunchsRetriesDoNotHoldUpTheCurrentLaunchsProgress()
    {
        var link = NewLink();
        var run = RunAsync(link);
        var stream = await _server.NextStreamAsync();
        var old = await NameAsync(link, stream);
        var current = await NameAsync(link, stream);
        var release = new TaskCompletionSource();
        _server.ReportAsyncHook = request => request.LaunchId == old.LaunchId ? release.Task : Task.CompletedTask;

        var stuck = link.ReportAsync(new LaunchReport(old, RunnerLaunchPhase.Superseded), _stop.Token);
        await WaitUntilAsync(() => _server.Reports.Count == 1);
        await link.ReportAsync(new LaunchReport(current, RunnerLaunchPhase.Downloading, DownloadedBytes: 1, TotalBytes: 2), _stop.Token).WaitAsync(WaitLimit);

        Assert.That(_server.Reports.Last().LaunchId, Is.EqualTo(current.LaunchId));
        release.SetResult();
        await stuck.WaitAsync(WaitLimit);
        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task ARefusedHeartbeatWithdrawsPermissionToStart_UntilOneIsAcceptedAndAFreshStateArrives()
    {
        var link = NewLink();
        var run = RunAsync(link);
        var launch = await NameAsync(link, await _server.NextStreamAsync());
        Assert.That(link.MayStart(launch), Is.True);

        _server.Register = _ => throw new RpcException(new Status(StatusCode.Unauthenticated, "key revoked"));
        await WaitUntilAsync(() => link.State.Connection == LauncherConnection.Refused);

        Assert.That(link.MayStart(launch), Is.False);
        _server.Register = _ => new RegisterRunnerResponse { Accepted = true, RefreshSeconds = 1 };
        // No stream opens while the heartbeat is refused; the next one opens after it is accepted.
        var fresh = await _server.NextStreamAsync();
        Assert.That(link.State.Connection, Is.EqualTo(LauncherConnection.Online));
        await fresh.WriteAsync(State(new LaunchRunnerCommand { LaunchId = launch.LaunchId, LaunchSequence = launch.Sequence, EditorTemplate = launch.EditorTemplate, RunnerImage = launch.RunnerImage }));
        await WaitUntilAsync(() => MayStart(link, launch.LaunchId));

        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task CancellingTheRunWithdrawsPermissionToStartAtOnce()
    {
        var link = NewLink();
        using var stop = new CancellationTokenSource();
        var run = Task.Run(() => link.RunAsync(Settings, Profile, stop.Token));
        var launchId = await NameAsync(link, await _server.NextStreamAsync());
        Assert.That(MayStart(link, launchId.LaunchId), Is.True);

        stop.Cancel();

        Assert.That(MayStart(link, launchId.LaunchId), Is.False, "availability was switched off");
        await run.WaitAsync(WaitLimit);
    }

    [Test]
    public async Task ATerminalReportIsResentAfterItsCallerStopsWaiting_UntilAccepted()
    {
        var link = NewLink();
        var run = RunAsync(link);
        var launchId = await NameAsync(link, await _server.NextStreamAsync());
        var accept = false;
        var accepted = 0;
        _server.Report = _ =>
        {
            if (!Volatile.Read(ref accept))
            {
                return new RpcException(new Status(StatusCode.Unavailable, "down"));
            }

            Interlocked.Increment(ref accepted);
            return null;
        };
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await link.ReportAsync(new LaunchReport(launchId, RunnerLaunchPhase.Started), caller.Token).WaitAsync(WaitLimit);
        var sent = _server.Reports.Count;
        await WaitUntilAsync(() => _server.Reports.Count > sent);

        Volatile.Write(ref accept, true);
        await WaitUntilAsync(() => Volatile.Read(ref accepted) == 1);
        var settled = _server.Reports.Count;
        await Task.Delay(100);

        Assert.That(_server.Reports, Has.Count.EqualTo(settled), "accepted, so no longer sent");
        Assert.That(_server.Reports.Distinct().Count(), Is.EqualTo(1));
        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task ATerminalReportIsNotResentOnceItsRunHasEnded()
    {
        var link = NewLink();
        using var stop = new CancellationTokenSource();
        var run = Task.Run(() => link.RunAsync(Settings, Profile, stop.Token));
        var launchId = await NameAsync(link, await _server.NextStreamAsync());
        _server.Report = _ => new RpcException(new Status(StatusCode.Unavailable, "down"));

        var report = link.ReportAsync(new LaunchReport(launchId, RunnerLaunchPhase.Started), CancellationToken.None);
        await WaitUntilAsync(() => _server.Reports.Count >= 2);
        await stop.CancelAsync();
        await run.WaitAsync(WaitLimit);
        await report.WaitAsync(WaitLimit);
        var sent = _server.Reports.Count;
        await Task.Delay(100);

        Assert.That(_server.Reports, Has.Count.EqualTo(sent), "host shutdown sends no further STARTED");
    }

    [Test]
    public async Task AFailedProgressReportIsTriedAgainSoon_NotAfterAWholeRepeatInterval()
    {
        var link = NewLink(FastOptions with { ProgressRepeat = TimeSpan.FromSeconds(20), ReportMinBackoff = TimeSpan.FromMilliseconds(30) });
        var run = RunAsync(link);
        var launchId = await NameAsync(link, await _server.NextStreamAsync());
        var failures = 1;
        _server.Report = _ => failures-- > 0 ? new RpcException(new Status(StatusCode.Unavailable, "down")) : null;

        await link.ReportAsync(new LaunchReport(launchId, RunnerLaunchPhase.Downloading, DownloadedBytes: 1, TotalBytes: 2), _stop.Token);

        await WaitUntilAsync(() => _server.Reports.Count == 2);
        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task TheLatestProgressIsRepeatedWhileTheLaunchIsInFlight_AndNeverAfterItsOutcome()
    {
        var link = NewLink(FastOptions with { ProgressRepeat = TimeSpan.FromMilliseconds(30) });
        var run = RunAsync(link);
        var launchId = await NameAsync(link, await _server.NextStreamAsync());

        await link.ReportAsync(new LaunchReport(launchId, RunnerLaunchPhase.Downloading, DownloadedBytes: 10, TotalBytes: 100), _stop.Token);
        await WaitUntilAsync(() => _server.Reports.Count >= 3);
        Assert.That(_server.Reports, Is.All.Matches<ReportRunnerLaunchRequest>(report =>
            report.Phase == RunnerLaunchPhase.Downloading && report.DownloadCompletedBytes == 10 && report.DownloadTotalBytes == 100));

        await link.ReportAsync(new LaunchReport(launchId, RunnerLaunchPhase.Started), _stop.Token);
        var sent = _server.Reports.Count;
        await link.ReportAsync(new LaunchReport(launchId, RunnerLaunchPhase.Downloading, DownloadedBytes: 20, TotalBytes: 100), _stop.Token);
        await Task.Delay(200);

        Assert.That(_server.Reports, Has.Count.EqualTo(sent));
        Assert.That(_server.Reports.Last().Phase, Is.EqualTo(RunnerLaunchPhase.Started), "no repeat follows the outcome");
        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task AFailingListenerDoesNotStopTheStreamFromReconnecting()
    {
        var link = NewLink();
        link.StateChanged += (_, _) => throw new InvalidOperationException("listener bug");
        var run = RunAsync(link);
        var first = await _server.NextStreamAsync();
        await first.WriteAsync(State(NewLaunch(1)));
        first.Complete();

        var second = await _server.NextStreamAsync();
        var launch = NewLaunch(2);
        await second.WriteAsync(State(launch));
        await WaitUntilAsync(() => MayStart(link, launch.LaunchId));

        _stop.Cancel();
        await run;
    }

    [Test]
    public void AReportMessageIsCutToTheProtocolsLimit_AtACharacterBoundary()
    {
        var text = new string('é', 300);

        var cut = ControlPlaneLauncherLink.Truncate(text, ControlPlaneLauncherLink.MaxReportMessageBytes);

        Assert.That(Encoding.UTF8.GetByteCount(cut), Is.EqualTo(512));
        Assert.That(cut, Is.EqualTo(new string('é', 256)));
        Assert.That(ControlPlaneLauncherLink.Truncate("short", 512), Is.EqualTo("short"));
    }

    [Test]
    public async Task AReleaseNamesTheLaunchAndTheExecutor_AndSaysWhetherOffloadrWasReached()
    {
        var link = NewLink();
        Assert.That((await link.ReleaseAsync(Guid.NewGuid().ToString(), null, "any", _stop.Token)).Reached, Is.False, "not registered yet");

        var run = RunAsync(link);
        await _server.NextStreamAsync();
        var launchId = Guid.NewGuid().ToString();
        var target = link.LaunchTarget!;

        _server.Release = _ => new ReleaseRunnerResponse { Released = true };
        var granted = await link.ReleaseAsync(launchId, " executor-1 ", target, _stop.Token);
        _server.Release = _ => new ReleaseRunnerResponse { Released = false, Message = "A session is open." };
        var refused = await link.ReleaseAsync(launchId, null, target, _stop.Token);
        _server.Release = _ => throw new RpcException(new Status(StatusCode.Unavailable, "down"));
        var unreachable = await link.ReleaseAsync(launchId, null, target, _stop.Token);
        _server.Release = _ => throw new RpcException(new Status(StatusCode.FailedPrecondition, "not current"));
        var rejected = await link.ReleaseAsync(launchId, null, target, _stop.Token);
        _server.Release = _ => throw new RpcException(new Status(StatusCode.Unimplemented, "404 from a proxy"));
        var noRoute = await link.ReleaseAsync(launchId, null, target, _stop.Token);
        Assert.That(noRoute.Reached, Is.False, "a missing route did not ask Offloadr");
        _server.Release = _ => new ReleaseRunnerResponse { Released = true };
        var exited = await link.ReleaseAsync(launchId, null, target, _stop.Token, containerExited: true);

        var request = _server.Releases[0];
        Assert.Multiple(() =>
        {
            Assert.That(granted, Is.EqualTo(new ReleaseResult(true, true, string.Empty)));
            Assert.That(refused, Is.EqualTo(new ReleaseResult(false, true, "A session is open.")));
            Assert.That(unreachable.Reached, Is.False);
            Assert.That(rejected is { Reached: true, Released: false }, Is.True);
            Assert.That(request.LaunchId, Is.EqualTo(launchId));
            Assert.That(request.ContainerExited, Is.False);
            Assert.That(exited.Released, Is.True);
            Assert.That(_server.Releases.Last().ContainerExited, Is.True, "a container that is gone is reported as such");
            Assert.That(request.ExecutorInstanceId, Is.EqualTo("executor-1"));
            Assert.That(request.InstanceId, Is.EqualTo(link.InstanceId));
            Assert.That(request.RunnerId, Is.EqualTo(RunnerAgentOptions.ResolveRunnerId(Key, null)));
        });
        Assert.ThrowsAsync<ArgumentException>(() => link.ReleaseAsync("", null, target, _stop.Token));

        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task AStreamThatDroppedAndCameBack_StartsNothingThatWasCheckedBefore()
    {
        var link = NewLink();
        var run = RunAsync(link);
        var first = await _server.NextStreamAsync();
        var launch = NewLaunch(1);
        await first.WriteAsync(State(launch));
        await WaitUntilAsync(() => MayStart(link, launch.LaunchId));
        var drops = link.StreamDrops;
        Assert.That(link.MayStart(link.State.Launch!, drops), Is.True);

        first.Complete();
        await WaitUntilAsync(() => !link.State.StreamLive);
        var second = await _server.NextStreamAsync();
        await second.WriteAsync(State(launch));
        await WaitUntilAsync(() => MayStart(link, launch.LaunchId));

        Assert.Multiple(() =>
        {
            Assert.That(link.MayStart(link.State.Launch!, drops), Is.False, "The same launch, named on another stream than the checks were made on.");
            Assert.That(link.MayStart(link.State.Launch!, link.StreamDrops), Is.True);
        });

        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task AReleaseWhoseCallIsSlowToBeSetUp_HoldsUpNothingElseOfTheLink()
    {
        var link = NewLink();
        var run = RunAsync(link);
        var stream = await _server.NextStreamAsync();
        await stream.WriteAsync(State(null));
        await WaitUntilAsync(() => link.State.StreamLive);
        using var setUp = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        _server.Release = _ =>
        {
            setUp.Set();
            proceed.Wait(TimeSpan.FromSeconds(8));
            return new ReleaseRunnerResponse { Released = true };
        };
        var target = link.LaunchTarget!;
        var release = Task.Run(() => link.ReleaseAsync(Guid.NewGuid().ToString(), null, target, _stop.Token));
        Assert.That(setUp.Wait(TimeSpan.FromSeconds(8)), Is.True);
        try
        {
            // Meanwhile the stream names a launch, and the link records it.
            var launch = NewLaunch(1);
            await stream.WriteAsync(State(launch));
            var recorded = Task.Run(() => WaitUntilAsync(() => MayStart(link, launch.LaunchId)));
            await recorded.WaitAsync(TimeSpan.FromSeconds(4));
        }
        finally
        {
            proceed.Set();
        }

        Assert.That((await release).Released, Is.True);
        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task AReleaseThatMakesRoomForALaunch_IsSentOnlyWhileThatLaunchIsNamed()
    {
        var link = NewLink();
        var run = RunAsync(link);
        var stream = await _server.NextStreamAsync();
        var launch = await NameAsync(link, stream);
        var target = link.LaunchTarget!;
        _server.Release = _ => new ReleaseRunnerResponse { Released = true };

        var whileNamed = await link.ReleaseAsync(Guid.NewGuid().ToString(), null, target, _stop.Token, onlyFor: launch);
        await stream.WriteAsync(State(null));
        await WaitUntilAsync(() => link.State.Launch is null);
        var afterWithdrawal = await link.ReleaseAsync(Guid.NewGuid().ToString(), null, target, _stop.Token, onlyFor: launch);

        Assert.Multiple(() =>
        {
            Assert.That(launch.OfferRevision, Is.EqualTo(7UL), "the offer Offloadr checked it against");
            Assert.That(whileNamed is { Released: true, Sent: true }, Is.True);
            Assert.That(afterWithdrawal is { Released: false, Reached: false, Sent: false }, Is.True);
            Assert.That(_server.Releases, Has.Count.EqualTo(1), "Nothing is sent once the launch is withdrawn.");
        });
        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task AReleaseWithNoLauncherToSendIt_IsToldAsNotSent()
    {
        var link = NewLink();
        var run = RunAsync(link);
        await _server.NextStreamAsync();
        var target = link.LaunchTarget!;
        _server.Release = _ => new ReleaseRunnerResponse { Released = true };
        var another = await link.ReleaseAsync(Guid.NewGuid().ToString(), null, "https://another.example#runner", _stop.Token);
        _stop.Cancel();
        await run;

        var ended = await link.ReleaseAsync(Guid.NewGuid().ToString(), null, target, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(another is { Released: false, Reached: false, Sent: false }, Is.True, "Only the Offloadr that launched it can say.");
            Assert.That(ended is { Released: false, Reached: false, Sent: false }, Is.True, "Nothing can have been granted.");
            Assert.That(_server.Releases, Is.Empty);
        });
    }

    [Test]
    public async Task AContainerGone_IsStillReported_AfterTheRunEnded_AsTheInstanceThatRan()
    {
        var link = NewLink();
        var run = RunAsync(link);
        await _server.NextStreamAsync();
        var target = link.LaunchTarget!;
        var instance = link.InstanceId;
        _stop.Cancel();
        await run;
        _server.Release = _ => new ReleaseRunnerResponse { Released = true };

        // Taken over, say: no run now. A gone container is reported all the same; a normal release is not.
        var exited = await link.ReleaseAsync(Guid.NewGuid().ToString(), null, target, CancellationToken.None, containerExited: true);
        var normal = await link.ReleaseAsync(Guid.NewGuid().ToString(), null, target, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(exited.Released, Is.True);
            Assert.That(_server.Releases.Single().InstanceId, Is.EqualTo(instance), "as the launcher instance that ran");
            Assert.That(_server.Releases.Single().ContainerExited, Is.True);
            Assert.That(normal.Reached, Is.False, "a normal release needs a registered launcher");
        });
    }

    [Test]
    public async Task AContainerGone_IsReported_WithNoRunAtAll_AsTheSettingsThatNameItsTarget()
    {
        var link = NewLink();
        var target = RunnerSupervisor.LaunchTargetFor(Settings);
        _server.Release = _ => new ReleaseRunnerResponse { Released = true };

        // A new process, say: nothing ran here. Reported all the same, without registering.
        var exited = await link.ReleaseAsync(Guid.NewGuid().ToString(), null, target, CancellationToken.None, containerExited: true, reportAs: Settings);
        var elsewhere = await link.ReleaseAsync(Guid.NewGuid().ToString(), null, "https://other.example#runner", CancellationToken.None, containerExited: true, reportAs: Settings);

        Assert.Multiple(() =>
        {
            Assert.That(exited.Released, Is.True);
            Assert.That(_server.Registrations, Is.Empty, "not registered just to report");
            Assert.That(_server.Releases.Single().InstanceId, Is.EqualTo(link.InstanceId));
            Assert.That(_server.Releases.Single().RunnerId, Is.EqualTo(RunnerAgentOptions.ResolveRunnerId(Key, null)));
            Assert.That(elsewhere.Reached, Is.False, "settings for another target do not speak for it");
        });
    }

    [Test]
    public async Task AReleaseBeforeAStop_IsAsked_WithNoRunAtAll_AsTheSettingsThatNameItsTarget()
    {
        var link = NewLink();
        var target = RunnerSupervisor.LaunchTargetFor(Settings);
        _server.Release = _ => new ReleaseRunnerResponse { Released = true };

        // The person stops an adopted container while this computer is not offered: asked without registering.
        var asked = await link.ReleaseAsync(Guid.NewGuid().ToString(), null, target, CancellationToken.None, reportAs: Settings);

        Assert.Multiple(() =>
        {
            Assert.That(asked.Released, Is.True);
            Assert.That(_server.Registrations, Is.Empty, "not registered just to ask");
            Assert.That(_server.Releases.Single().ContainerExited, Is.False);
        });
    }

    [Test]
    public async Task AReleaseBeforeAStop_ForARunnerSettingsNoLongerName_IsAskedAsTheLauncherThatRanForIt()
    {
        var link = NewLink();
        var run = RunAsync(link);
        await _server.NextStreamAsync();
        var target = link.LaunchTarget!;
        var instance = link.InstanceId;
        await _stop.CancelAsync();
        await run;
        _server.Release = _ => new ReleaseRunnerResponse { Released = true };

        // The person changed the key and stops the container the old one launched: only the Offloadr
        // that launched it can say, and it is asked as the launcher that ran for it.
        var other = Settings with { RunnerKey = "another-runner-key-0123456789abcdefghijklmnop" };
        var asked = await link.ReleaseAsync(Guid.NewGuid().ToString(), null, target, CancellationToken.None, reportAs: other);

        Assert.Multiple(() =>
        {
            Assert.That(asked.Released, Is.True);
            Assert.That(_server.Releases.Single().InstanceId, Is.EqualTo(instance));
            Assert.That(_server.Releases.Single().ContainerExited, Is.False);
        });
    }

    [Test]
    public async Task AContainerStartedForAnotherAddressOrKeyIsNotReleasedHere()
    {
        var link = NewLink();
        var run = RunAsync(link);
        await _server.NextStreamAsync();
        var elsewhere = $"https://other.example#{RunnerAgentOptions.ResolveRunnerId(Key, null)}";
        Assert.That(link.LaunchTarget, Is.EqualTo($"https://offloadr.example#{RunnerAgentOptions.ResolveRunnerId(Key, null)}"));

        var result = await link.ReleaseAsync(Guid.NewGuid().ToString(), null, elsewhere, _stop.Token);

        Assert.Multiple(() =>
        {
            Assert.That(result is { Released: false, Reached: false }, Is.True);
            Assert.That(_server.Releases, Is.Empty, "this Offloadr does not know what runs in it");
            Assert.That(link.LaunchTarget, Does.StartWith("https://offloadr.example#"));
        });
        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task AnEquivalentAddressIsTheSameLaunchTarget_AndAMessageGoesOnlyWithFailed()
    {
        var link = NewLink();
        var run = Task.Run(() => link.RunAsync(Settings with { OffloadrApiOrigin = "https://Offloadr.example:443/" }, Profile, _stop.Token));
        var stream = await _server.NextStreamAsync();
        var started = await NameAsync(link, stream);
        var failed = await NameAsync(link, stream);

        await link.ReportAsync(new LaunchReport(started, RunnerLaunchPhase.Started, "Started fine."), _stop.Token);
        await link.ReportAsync(new LaunchReport(failed, RunnerLaunchPhase.Failed, "No space left."), _stop.Token);

        Assert.Multiple(() =>
        {
            Assert.That(link.LaunchTarget, Is.EqualTo($"https://offloadr.example#{RunnerAgentOptions.ResolveRunnerId(Key, null)}"));
            Assert.That(_server.Reports.Select(report => report.Message), Is.EqualTo(new[] { string.Empty, "No space left." }));
        });
        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task OnlyAHigherSequenceReplacesALaunch()
    {
        var link = NewLink();
        var run = RunAsync(link);
        var stream = await _server.NextStreamAsync();
        var current = NewLaunch(10);
        await stream.WriteAsync(State(current));
        await WaitUntilAsync(() => MayStart(link, current.LaunchId));

        // Each state that breaks the order is not obeyed, withdraws permission and reconnects for a fresh one.
        var older = NewLaunch(9);
        await stream.WriteAsync(State(older));
        stream = await _server.NextStreamAsync();
        Assert.That(MayStart(link, current.LaunchId), Is.False);

        var conflicting = NewLaunch(10);
        await stream.WriteAsync(State(conflicting));
        stream = await _server.NextStreamAsync();

        var newer = NewLaunch(11);
        await stream.WriteAsync(State(newer));
        await WaitUntilAsync(() => MayStart(link, newer.LaunchId));
        Assert.Multiple(() =>
        {
            Assert.That(MayStart(link, older.LaunchId), Is.False);
            Assert.That(MayStart(link, conflicting.LaunchId), Is.False);
            Assert.That(MayStart(link, current.LaunchId), Is.False);
        });

        // Withdrawn, then an old snapshot naming it again: it stays withdrawn.
        await stream.WriteAsync(State(null));
        await WaitUntilAsync(() => link.State.Launch is null);
        await stream.WriteAsync(State(newer));
        await _server.NextStreamAsync();
        Assert.That(MayStart(link, newer.LaunchId), Is.False);

        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task AnOutcomeOutlivesARestartOfTheSameLauncher_AndItsLaunchDoesNotStartAgain()
    {
        var link = NewLink();
        LaunchRequest launchId;
        using (var stop = new CancellationTokenSource())
        {
            var run = Task.Run(() => link.RunAsync(Settings, Profile, stop.Token));
            launchId = await NameAsync(link, await _server.NextStreamAsync());
            await link.ReportAsync(new LaunchReport(launchId, RunnerLaunchPhase.Failed, "No space left."), _stop.Token);
            await stop.CancelAsync();
            await run.WaitAsync(WaitLimit);
        }

        var again = RunAsync(link);
        var stream = await _server.NextStreamAsync();
        var launch = new LaunchRunnerCommand { LaunchId = launchId.LaunchId, LaunchSequence = launchId.Sequence, EditorTemplate = "comfyui-latest", RunnerImage = "image" };
        await stream.WriteAsync(State(launch));
        await WaitUntilAsync(() => link.State.Launch?.LaunchId == launchId.LaunchId);

        Assert.Multiple(() =>
        {
            Assert.That(link.OutcomeOf(launchId.LaunchId)?.Phase, Is.EqualTo(RunnerLaunchPhase.Failed), "replayed, not tried again");
            Assert.That(MayStart(link, launchId.LaunchId), Is.False);
        });
        _stop.Cancel();
        await again;
    }

    [Test]
    public async Task ALateReportFromAnEarlierRunIsDropped_AndDoesNotBlockTheLaunchLater()
    {
        var link = NewLink();
        LaunchRequest launchId;
        using (var stop = new CancellationTokenSource())
        {
            var run = Task.Run(() => link.RunAsync(Settings, Profile, stop.Token));
            launchId = await NameAsync(link, await _server.NextStreamAsync());
            await stop.CancelAsync();
            await run.WaitAsync(WaitLimit);
        }

        var again = RunAsync(link);
        var stream = await _server.NextStreamAsync();
        await link.ReportAsync(new LaunchReport(launchId, RunnerLaunchPhase.Failed, "From the earlier run."), _stop.Token);
        var launch = new LaunchRunnerCommand { LaunchId = launchId.LaunchId, LaunchSequence = launchId.Sequence, EditorTemplate = "comfyui-latest", RunnerImage = "image" };
        await stream.WriteAsync(State(launch));
        await WaitUntilAsync(() => MayStart(link, launchId.LaunchId));

        // Even once this run's stream names the same launch, the earlier run's report is not its own.
        await link.ReportAsync(new LaunchReport(launchId, RunnerLaunchPhase.Failed, "Still from the earlier run."), _stop.Token);

        Assert.Multiple(() =>
        {
            Assert.That(_server.Reports, Is.Empty);
            Assert.That(link.OutcomeOf(launchId.LaunchId), Is.Null);
            Assert.That(MayStart(link, launchId.LaunchId), Is.True);
            Assert.That(link.MayStart(launchId), Is.False, "the earlier run's request does not start it");
        });
        await link.ReportAsync(new LaunchReport(link.State.Launch!, RunnerLaunchPhase.Started), _stop.Token);
        Assert.That(_server.Reports.Single().Phase, Is.EqualTo(RunnerLaunchPhase.Started), "this run's own report goes out");
        _stop.Cancel();
        await again;
    }

    [Test]
    public async Task AnOutcomeLeftUnansweredIsSentAgainByTheNextRun()
    {
        var link = NewLink();
        LaunchRequest launchId;
        _server.Report = _ => new RpcException(new Status(StatusCode.Unavailable, "down"));
        using (var stop = new CancellationTokenSource())
        {
            var run = Task.Run(() => link.RunAsync(Settings, Profile, stop.Token));
            launchId = await NameAsync(link, await _server.NextStreamAsync());
            var report = link.ReportAsync(new LaunchReport(launchId, RunnerLaunchPhase.Cancelled), CancellationToken.None);
            await WaitUntilAsync(() => _server.Reports.Count >= 1);
            await stop.CancelAsync();
            await run.WaitAsync(WaitLimit);
            await report.WaitAsync(WaitLimit);
        }

        _server.Report = _ => null;
        var sent = _server.Reports.Count;
        var again = RunAsync(link);

        await WaitUntilAsync(() => _server.Reports.Count > sent);
        Assert.That(_server.Reports.Last(), Is.EqualTo(_server.Reports[0]), "the same acknowledgement, resumed");
        await Task.Delay(100);
        Assert.That(_server.Reports, Has.Count.EqualTo(sent + 1), "and only until it is answered");
        _stop.Cancel();
        await again;
    }

    [Test]
    public async Task ACancelledReportRecordsNothing()
    {
        var link = NewLink();
        var run = RunAsync(link);
        var launchId = await NameAsync(link, await _server.NextStreamAsync());

        await link.ReportAsync(new LaunchReport(launchId, RunnerLaunchPhase.Started), new CancellationToken(canceled: true));

        Assert.Multiple(() =>
        {
            Assert.That(_server.Reports, Is.Empty);
            Assert.That(link.OutcomeOf(launchId.LaunchId), Is.Null);
            Assert.That(MayStart(link, launchId.LaunchId), Is.True);
        });
        _stop.Cancel();
        await run;
    }

    [Test]
    public async Task TheLaunchTargetNeverHoldsCredentialsFromTheAddress()
    {
        var link = NewLink();
        var run = Task.Run(() => link.RunAsync(Settings with { OffloadrApiOrigin = "https://user:secret@offloadr.example" }, Profile, _stop.Token));
        await WaitUntilAsync(() => link.LaunchTarget is not null);

        Assert.That(link.LaunchTarget, Is.EqualTo($"https://offloadr.example#{RunnerAgentOptions.ResolveRunnerId(Key, null)}"));
        _stop.Cancel();
        await run;
    }

    [TestCase("https://[2001:db8::1]:8443", "https://[2001:db8::1]:8443#")]
    [TestCase("https://[2001:db8::1:8443]", "https://[2001:db8::1:8443]#")]
    [TestCase("https://[fe80::1%253]:8443", "https://[fe80::1%253]:8443#")]
    [TestCase("https://[fe80::1%257]:8443", "https://[fe80::1%257]:8443#")]
    public async Task AnIpv6AddressKeepsItsBrackets(string origin, string expected)
    {
        var link = NewLink();
        var run = Task.Run(() => link.RunAsync(Settings with { OffloadrApiOrigin = origin }, Profile, _stop.Token));
        await WaitUntilAsync(() => link.LaunchTarget is not null);

        Assert.That(link.LaunchTarget, Does.StartWith(expected));
        _stop.Cancel();
        await run;
    }

    [Test]
    public void TheResourcesAreWhatTheContainerWouldSee()
    {
        var report = new HostGpuReport(
        [
            new HostGpu("NVIDIA GeForce RTX 4090", 24564, "GPU-11111111-1111-1111-1111-111111111111"),
            new HostGpu("NVIDIA GeForce RTX 3090", 24576, "GPU-22222222-2222-2222-2222-222222222222"),
        ], null);

        var all = LauncherResources.From(report, null, 32L * 1024 * 1024 * 1024);
        var chosen = LauncherResources.From(report, "GPU-22222222-2222-2222-2222-222222222222", null);
        var none = LauncherResources.From(HostGpuReport.None("no driver"), null, null);

        Assert.Multiple(() =>
        {
            Assert.That(all.GpuCount, Is.EqualTo(2UL));
            Assert.That(all.GpuType, Is.EqualTo(GpuType.NvidiaRtx4090));
            Assert.That(all.VramBytes, Is.EqualTo((24564UL + 24576UL) * 1024 * 1024));
            Assert.That(all.SystemRamBytes, Is.EqualTo(32UL * 1024 * 1024 * 1024));
            Assert.That(chosen.GpuCount, Is.EqualTo(1UL));
            Assert.That(chosen.GpuModelName, Is.EqualTo("NVIDIA GeForce RTX 3090"));
            Assert.That(chosen.GpuType, Is.EqualTo(GpuType.NvidiaRtx3090));
            Assert.That(chosen.VramBytes, Is.EqualTo(24576UL * 1024 * 1024));
            Assert.That(none.GpuCount, Is.EqualTo(0UL));
            Assert.That(none.GpuType, Is.EqualTo(GpuType.Unknown));
        });
    }

    [Test]
    public void TheEngineMemoryIsReadFromDockerAndPodmanInfo()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DockerCliJson.MemoryBytes("""{"MemTotal":33554432000,"OSType":"linux"}"""), Is.EqualTo(33554432000L));
            Assert.That(DockerCliJson.MemoryBytes("""{"host":{"memTotal":16777216000}}"""), Is.EqualTo(16777216000L));
            Assert.That(DockerCliJson.MemoryBytes("""{"OSType":"linux"}"""), Is.Null);
            Assert.That(DockerCliJson.MemoryBytes("""{"MemTotal":null,"host":{"memTotal":"lots"}}"""), Is.Null);
            Assert.That(DockerCliJson.MemoryBytes("not json"), Is.Null);
        });
    }

    private static LauncherProfile Profile()
        => new(LauncherResources.From(new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564)], null), null, null),
            ["comfyui-latest", "forge-neo"], OfferRevision: 7);

    private static LaunchRunnerCommand NewLaunch(ulong sequence) => new()
    {
        LaunchId = Guid.NewGuid().ToString(),
        LaunchSequence = sequence,
        EditorTemplate = "comfyui-latest",
        RunnerImage = "ghcr.io/offloadr/runner:comfyui-latest-p6",
        OfferRevision = 7,
    };

    /// <summary>Whether the launch with <paramref name="launchId"/> may start, asked as a caller would: with the request the state holds.</summary>
    private static bool MayStart(ControlPlaneLauncherLink link, string launchId)
        => link.State.Launch is { } launch && launch.LaunchId == launchId && link.MayStart(launch);

    /// <summary>Has <paramref name="stream"/> name a new launch and waits until the link has it.</summary>
    private static async Task<LaunchRequest> NameAsync(ControlPlaneLauncherLink link, ChannelWriter<LauncherStreamResponse> stream)
    {
        var launch = NewLaunch((ulong)Interlocked.Increment(ref _sequence));
        await stream.WriteAsync(State(launch));
        await WaitUntilAsync(() => link.State.Launch?.LaunchId == launch.LaunchId);
        return link.State.Launch!;
    }

    private static LauncherStreamResponse State(LaunchRunnerCommand? launch)
        => new() { State = launch is null ? new LauncherState() : new LauncherState { CurrentLaunch = launch } };

    private ControlPlaneLauncherLink NewLink(LauncherLinkOptions? options = null)
        => new(_server, options ?? FastOptions, TimeProvider.System, NullLogger.Instance);

    private Task RunAsync(ControlPlaneLauncherLink link) => Task.Run(() => link.RunAsync(Settings, Profile, _stop.Token));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition was not met in time.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>A control plane that records every call; each stream is fed by the test.</summary>
    private sealed class FakeLauncherControlPlane : ILauncherControlPlane
    {
        private readonly Channel<ChannelWriter<LauncherStreamResponse>> _opened = Channel.CreateUnbounded<ChannelWriter<LauncherStreamResponse>>();
        private readonly object _gate = new();
        private readonly List<RegisterRunnerRequest> _registrations = [];
        private readonly List<LauncherStreamRequest> _streams = [];
        private readonly List<ReportRunnerLaunchRequest> _reports = [];
        private readonly List<ReleaseRunnerRequest> _releases = [];
        private readonly List<string> _keys = [];

        public Func<RegisterRunnerRequest, RegisterRunnerResponse> Register { get; set; } = _ => new RegisterRunnerResponse { Accepted = true, RefreshSeconds = 1 };
        public Func<ReportRunnerLaunchRequest, Exception?> Report { get; set; } = _ => null;
        public Func<ReleaseRunnerRequest, ReleaseRunnerResponse> Release { get; set; } = _ => new ReleaseRunnerResponse { Released = true };

        public IReadOnlyList<RegisterRunnerRequest> Registrations { get { lock (_gate) return [.. _registrations]; } }
        public IReadOnlyList<LauncherStreamRequest> StreamRequests { get { lock (_gate) return [.. _streams]; } }
        public IReadOnlyList<ReportRunnerLaunchRequest> Reports { get { lock (_gate) return [.. _reports]; } }
        public IReadOnlyList<ReleaseRunnerRequest> Releases { get { lock (_gate) return [.. _releases]; } }
        public IReadOnlyList<string> Keys { get { lock (_gate) return [.. _keys]; } }

        /// <summary>The writer of the stream the link opened last.</summary>
        public ChannelWriter<LauncherStreamResponse>? LatestStream { get; private set; }

        /// <summary>The writer of the next stream the link opens.</summary>
        public async Task<ChannelWriter<LauncherStreamResponse>> NextStreamAsync()
            => await _opened.Reader.ReadAsync().AsTask().WaitAsync(WaitLimit);

        public Task<RegisterRunnerResponse> RegisterAsync(RunnerSettings settings, RegisterRunnerRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _registrations.Add(request.Clone());
                _keys.Add(settings.RunnerKey!);
            }

            return Task.FromResult(Register(request));
        }

        public async IAsyncEnumerable<LauncherStreamResponse> StreamAsync(
            RunnerSettings settings,
            LauncherStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var channel = Channel.CreateUnbounded<LauncherStreamResponse>();
            lock (_gate)
            {
                _streams.Add(request.Clone());
                _keys.Add(settings.RunnerKey!);
            }

            LatestStream = channel.Writer;
            await _opened.Writer.WriteAsync(channel.Writer, cancellationToken);
            await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return message;
            }
        }

        public async Task ReportAsync(RunnerSettings settings, ReportRunnerLaunchRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _reports.Add(request.Clone());
            }

            await ReportAsyncHook(request).WaitAsync(cancellationToken);
            if (Report(request) is { } failure)
            {
                throw failure;
            }
        }

        /// <summary>Lets a test hold a report's answer back.</summary>
        public Func<ReportRunnerLaunchRequest, Task> ReportAsyncHook { get; set; } = _ => Task.CompletedTask;

        public Task<ReleaseRunnerResponse> ReleaseAsync(RunnerSettings settings, ReleaseRunnerRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _releases.Add(request.Clone());
            }

            return Task.FromResult(Release(request));
        }
    }
}
