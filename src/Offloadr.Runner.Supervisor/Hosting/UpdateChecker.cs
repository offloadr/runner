using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Offloadr.Runner.Supervisor.Hosting;

public sealed record AvailableUpdate(Version Version, string Url);

/// <summary>
/// Looks for a newer release of the app on GitHub shortly after start and then daily. It only
/// reads the public releases API and never downloads or installs anything; the UI offers a link.
/// Failures, rate limits and "no releases yet" all mean no notice.
/// </summary>
public sealed class UpdateChecker : BackgroundService
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/offloadr/runner/releases/latest";

    private readonly HttpClient _http;
    private readonly ILogger<UpdateChecker> _logger;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _interval;
    private AvailableUpdate? _available;

    public UpdateChecker(
        Version currentVersion,
        ILogger<UpdateChecker> logger,
        HttpMessageHandler? handler = null,
        TimeSpan? initialDelay = null,
        TimeSpan? interval = null)
    {
        CurrentVersion = currentVersion;
        _logger = logger;
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(15);
        _interval = interval ?? TimeSpan.FromDays(1);
        _http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OffloadrRunner", currentVersion.ToString(3)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public Version CurrentVersion { get; }

    /// <summary>The newer release, or null when the app is current or the check has not succeeded.</summary>
    public AvailableUpdate? Available => Volatile.Read(ref _available);

    /// <summary>Raised on a background thread when <see cref="Available"/> changes.</summary>
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

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        AvailableUpdate? update = null;
        try
        {
            using var response = await _http.GetAsync(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
                update = Parse(json.RootElement);
            }
            else if (response.StatusCode != HttpStatusCode.NotFound)
            {
                // A rate limit (403, shared by everyone behind the same address) or a server error
                // says nothing about releases: only a release (200) or none at all (404) changes the answer.
                _logger.LogInformation("Release check answered {Status}.", (int)response.StatusCode);
                return Available;
            }
        }
        // IOException covers a connection that drops while the body is read (HttpIOException).
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Release check failed: {Message}", ex.Message);
            return Available;
        }

        if (!Equals(Interlocked.Exchange(ref _available, update), update))
        {
            Changed?.Invoke();
        }

        return update;
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

    private AvailableUpdate? Parse(JsonElement release)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True ||
            release.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        var tag = release.TryGetProperty("tag_name", out var tagName) ? tagName.GetString() : null;
        var url = release.TryGetProperty("html_url", out var htmlUrl) ? htmlUrl.GetString() : null;
        if (!TryParseVersion(tag, out var version) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var link) || link.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return version > CurrentVersion ? new AvailableUpdate(version, link.ToString()) : null;
    }
}
