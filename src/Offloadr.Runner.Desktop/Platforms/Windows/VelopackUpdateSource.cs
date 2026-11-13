using Offloadr.Runner.Supervisor.Hosting;
using Velopack;
using Velopack.Sources;

namespace Offloadr.Runner.Desktop.WinUI;

/// <summary>
/// Updates from this repository's GitHub releases, through the installer's update manager. For
/// trying an update without publishing one, <see cref="FeedVariable"/> names a folder of packed
/// releases (artifacts/releases/windows-win-x64) to update from instead.
/// </summary>
internal sealed class VelopackUpdateSource : IAppUpdateSource
{
    public const string ReleasesRepository = "https://github.com/offloadr/runner";
    public const string FeedVariable = "OFFLOADR_RUNNER_UPDATE_FEED";

    private readonly UpdateManager _manager;
    private readonly bool _localFeed;
    private UpdateInfo? _found;

    public VelopackUpdateSource()
    {
        var feed = Environment.GetEnvironmentVariable(FeedVariable);
        _localFeed = !string.IsNullOrWhiteSpace(feed);
        _manager = new UpdateManager(_localFeed
            ? new SimpleFileSource(new DirectoryInfo(feed!))
            : new GithubSource(ReleasesRepository, accessToken: null, prerelease: false));
    }

    // A copy run with another data folder (a test copy next to an installed one) leaves updating to
    // the installed copy, unless it is pointed at a local feed to try an update.
    public bool IsInstalled => _manager.IsInstalled && (_localFeed || !MauiProgram.UsesOtherDataDirectory);

    public Version? Pending => _manager.UpdatePendingRestart is { } asset ? ToVersion(asset) : null;

    public async Task<Version?> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _found = await _manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return _found is { IsDowngrade: false } found ? ToVersion(found.TargetFullRelease) : null;
    }

    public Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken)
        => _found is { } found
            ? _manager.DownloadUpdatesAsync(found, progress, cancellationToken)
            : throw new InvalidOperationException("Check for an update before downloading it.");

    public void ApplyAfterExit(bool restart, IReadOnlyList<string> arguments)
    {
        var asset = _found?.TargetFullRelease ?? _manager.UpdatePendingRestart
            ?? throw new InvalidOperationException("No update has been downloaded.");
        _manager.WaitExitThenApplyUpdates(asset, silent: true, restart, [.. arguments]);
    }

    private static Version? ToVersion(VelopackAsset asset)
        => AppUpdateService.TryParseVersion(asset.Version.ToString(), out var version) ? version : null;
}
