using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Offloadr.Runner.Supervisor.Hosting;

/// <summary>Where the app gets its updates from: the installer's update feed, for an installed copy.</summary>
public interface IAppUpdateSource
{
    /// <summary>False for a copy that was not installed (a build folder, say), which cannot update itself.</summary>
    bool IsInstalled { get; }

    /// <summary>A version downloaded earlier and waiting for a restart, or null.</summary>
    Version? Pending { get; }

    /// <summary>Looks for a newer release; returns its version, or null when this one is current.</summary>
    Task<Version?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Downloads the release <see cref="CheckAsync"/> found, reporting percent done.</summary>
    Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Has the installer apply the downloaded release once this process exits, and then, when
    /// <paramref name="restart"/>, start the app again with <paramref name="arguments"/>. The app must
    /// exit within a minute.
    /// </summary>
    void ApplyAfterExit(bool restart, IReadOnlyList<string> arguments);
}

public enum AppUpdateState
{
    /// <summary>This copy cannot update itself; nothing is checked.</summary>
    NotInstalled,
    Current,
    Checking,
    Downloading,

    /// <summary>A newer release is downloaded: it installs when the app restarts to update, or when it quits.</summary>
    Ready,

    /// <summary>The last check or download failed; the next one is tried as usual.</summary>
    Failed,
}

public sealed record AppUpdateStatus(AppUpdateState State, Version Current, Version? Available = null, int Percent = 0, string? Problem = null);

/// <summary>
/// Keeps the installed app up to date: shortly after start and then daily it looks for a newer
/// release, downloads it in the background, and offers a restart to install it. Nothing installs
/// without that restart, or until the app quits. Never at start: a second launch of the app (which
/// only brings up the open copy) would otherwise replace the files of the copy that is running.
/// </summary>
public sealed class AppUpdateService : BackgroundService
{
    private readonly IAppUpdateSource _source;
    private readonly ILogger<AppUpdateService> _logger;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppUpdateStatus _status;

    public AppUpdateService(IAppUpdateSource source, Version currentVersion, ILogger<AppUpdateService> logger, TimeSpan? initialDelay = null, TimeSpan? interval = null)
    {
        _source = source;
        _logger = logger;
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(15);
        _interval = interval ?? TimeSpan.FromDays(1);
        _status = !source.IsInstalled
            ? new AppUpdateStatus(AppUpdateState.NotInstalled, currentVersion)
            : source.Pending is { } pending && pending > currentVersion
                ? new AppUpdateStatus(AppUpdateState.Ready, currentVersion, pending, 100)
                : new AppUpdateStatus(AppUpdateState.Current, currentVersion);
    }

    public AppUpdateStatus Status => Volatile.Read(ref _status);

    /// <summary>Raised on a background thread when <see cref="Status"/> changes.</summary>
    public event Action? Changed;

    /// <summary>The app's version from its informational version, without build metadata.</summary>
    public static Version VersionOf(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return TryParseVersion(informational, out var version) ? version : new Version(0, 0, 0);
    }

    /// <summary>Accepts "1.2.3", "v1.2.3" and "1.2.3+commit"; pre-release suffixes are not releases.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        var value = text?.Trim() ?? string.Empty;
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var metadata = value.IndexOf('+');
        if (metadata >= 0)
        {
            value = value[..metadata];
        }

        if (value.Contains('-') || !Version.TryParse(value, out var parsed) || parsed.Major < 0)
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    /// <summary>
    /// True when a restart may install the downloaded release now. Not while the runner is being
    /// prepared, started or stopped: the app would exit in the middle of that. Nor while it waits
    /// to restart after exiting: no container runs then for the new version to reconnect to, and
    /// the pending restart would be lost. A running runner, with or without a session, is fine: the
    /// restart leaves it running and the new version reconnects to it.
    /// </summary>
    public bool CanRestartNow(SupervisorView view)
        => Status.State == AppUpdateState.Ready &&
           view.State is not (SupervisorState.Checking or SupervisorState.ResolvingPlan or SupervisorState.Pulling
               or SupervisorState.Starting or SupervisorState.Stopping or SupervisorState.WaitingToRestart);

    /// <summary>
    /// Hands the downloaded release to the installer, which applies it once the app exits and, when
    /// <paramref name="restart"/>, starts it again with <paramref name="arguments"/>.
    /// </summary>
    public void ApplyAfterExit(bool restart, IReadOnlyList<string> arguments)
    {
        if (Status.State != AppUpdateState.Ready)
        {
            throw new InvalidOperationException("No update is ready to install.");
        }

        _source.ApplyAfterExit(restart, arguments);
    }

    /// <summary>Checks now, and downloads a newer release. Concurrent calls share one check.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (!_source.IsInstalled || !await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var current = Status.Current;
            if (Status.State == AppUpdateState.Ready)
            {
                // Already downloaded: a newer release found later is picked up after the restart.
                return;
            }

            Set(new AppUpdateStatus(AppUpdateState.Checking, current));
            var available = await _source.CheckAsync(cancellationToken).ConfigureAwait(false);
            if (available is null || available <= current)
            {
                Set(new AppUpdateStatus(AppUpdateState.Current, current));
                return;
            }

            _logger.LogInformation("Downloading Offloadr Runner {Version}.", available.ToString(3));
            Set(new AppUpdateStatus(AppUpdateState.Downloading, current, available));
            var reported = -1;
            await _source.DownloadAsync(
                percent =>
                {
                    // Report whole steps of five percent: every percent would redraw the window a hundred times.
                    var step = Math.Clamp(percent, 0, 100) / 5 * 5;
                    if (Interlocked.Exchange(ref reported, step) != step)
                    {
                        Set(new AppUpdateStatus(AppUpdateState.Downloading, current, available, step));
                    }
                },
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Offloadr Runner {Version} is ready to install.", available.ToString(3));
            Set(new AppUpdateStatus(AppUpdateState.Ready, current, available, 100));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline, rate limited, a release still being uploaded: the next check tries again.
            _logger.LogInformation(ex, "Update check failed.");
            Set(new AppUpdateStatus(AppUpdateState.Failed, Status.Current, Status.Available, Problem: "Could not check for updates. The app tries again later."));
        }
        finally
        {
            _gate.Release();
        }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_source.IsInstalled)
        {
            return;
        }

        try
        {
            await Task.Delay(_initialDelay, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(_interval);
            do
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The app is closing.
        }
    }

    private void Set(AppUpdateStatus status)
    {
        if (!Equals(Interlocked.Exchange(ref _status, status), status))
        {
            Changed?.Invoke();
        }
    }
}
