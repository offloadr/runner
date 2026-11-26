using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hardware;
using Offloadr.Runner.Supervisor.Launcher;
using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Settings;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Supervisor;

/// <content>What depends on no state: container specs, ownership, the offer's arithmetic and fixed texts.</content>
public sealed partial class RunnerSupervisor
{
    /// <summary>
    /// Why the models folder in Settings cannot be mounted on this engine, or null. An engine on another
    /// computer resolves a bind mount on that computer, where the folder does not exist.
    /// </summary>
    private static string? ModelsFolderProblem(RunnerSettings settings, ContainerEnginePin? engine)
        => engine is { Remote: true } && !string.IsNullOrWhiteSpace(settings.LocalModelsDirectory)
            ? "The models folder in Settings is on this computer, but the container engine runs on another one, which cannot see it. Clear the models folder in Settings, or use a container engine on this computer."
            : null;

    internal ContainerSpec BuildSpec(RunnerSettings settings, LaunchTemplate template)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RUNNER_SECRET"] = settings.RunnerKey!.Trim(),
            ["OFFLOADR_API_GRPC"] = settings.OffloadrApiOrigin,
            ["RUNNER_MODEL_DOWNLOADS"] = settings.DownloadModels ? "on" : "off",
        };
        // Each family's, under its own name: the image reads only the one for its editor. Blank ones are left
        // out, so a runner started before they existed keeps its fingerprint.
        foreach (var (runtimeKind, text) in settings.EditorArguments)
        {
            environment[EditorArguments.EnvironmentVariable(runtimeKind)] = text;
        }

        // The container still gets `--gpus all`: under WSL the driver can list every GPU whatever Docker
        // was asked for, so the agent itself binds its queries, power commands and the editor's CUDA
        // visibility to this one. Left out when none is chosen, like the arguments above.
        if (settings.SelectedGpuUuid is { } gpu)
        {
            environment[NvidiaGpuUuid.EnvironmentVariable] = gpu;
        }

        var volumes = new List<VolumeMount>();
        if (!string.IsNullOrWhiteSpace(settings.LocalModelsDirectory))
        {
            volumes.Add(new VolumeMount(settings.LocalModelsDirectory, "/local", ReadOnly: true));
            environment["RUNNER_LOCAL_MODELS_DIR"] = "/local";
            if (settings.DownloadModels)
            {
                // Downloads are saved in the folder, beside the person's own models. Sessions still see
                // it only read-only, at /local; the agent closes this second path to them.
                volumes.Add(new VolumeMount(settings.LocalModelsDirectory, ModelLibraryContainerPath, ReadOnly: false));
                environment["RUNNER_MODEL_LIBRARY_DIR"] = ModelLibraryContainerPath;
            }
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ManagedLabel] = "true",
            [OwnerLabel] = _owner.Id,
            [TemplateLabel] = template.EditorTemplate,
        };
        var spec = new ContainerSpec(_options.ContainerName, template.Image, environment, volumes, labels, Gpus: true);
        labels[SpecLabel] = Fingerprint(spec);
        return spec;
    }

    /// <summary>Identifies everything that requires a new container when it changes. Labels are not part of it.</summary>
    internal static string Fingerprint(ContainerSpec spec)
    {
        var text = new StringBuilder()
            .Append(spec.Image).Append('\n')
            .Append(spec.Gpus).Append('\n');
        foreach (var (name, value) in spec.Environment.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            text.Append(name).Append('=').Append(value).Append('\n');
        }

        foreach (var volume in spec.Volumes)
        {
            text.Append(volume.HostPath).Append(':').Append(volume.ContainerPath).Append(':').Append(volume.ReadOnly).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    internal static RunnerStatusSnapshot? ParseStatus(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RunnerStatusSnapshot>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string Origin, string Key) Credentials(RunnerSettings settings)
        => (settings.OffloadrApiOrigin.Trim(), settings.RunnerKey?.Trim() ?? string.Empty);


    /// <summary>The last line the creation printed (the ID), or null when there is none.</summary>
    private static string? NormalizeContainerId(string? output)
        => output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() is { Length: >= 12 } id ? id : null;


    private static bool IsManaged(ContainerInfo container)
        => container.Labels.TryGetValue(ManagedLabel, out var value) && value == "true";

    /// <summary>
    /// A runner this copy of the app started (now or before a crash). One another copy started, with
    /// another data folder or on another computer sharing the engine, is never adopted or stopped.
    /// </summary>
    private bool IsOwned(ContainerInfo container) => IsOwnedBy(container, _owner.Id);

    /// <summary>True when <paramref name="container"/> is a runner the copy of the app with <paramref name="ownerId"/> started.</summary>
    internal static bool IsOwnedBy(ContainerInfo container, string ownerId)
        => IsManaged(container) &&
           container.Labels.TryGetValue(OwnerLabel, out var owner) &&
           string.Equals(owner, ownerId, StringComparison.Ordinal);


    /// <summary>
    /// Which runner an Offloadr address and key launch for, for the takeover note: the launcher's own
    /// canonical target (so https://host and https://host:443 are one runner), never the key itself.
    /// </summary>
    private static string TakeoverTag((string Origin, string Key) credentials)
        => ControlPlaneLauncherLink.LaunchTargetOf(credentials.Origin, RunnerAgentOptions.ResolveRunnerId(credentials.Key, configuredRunnerId: null));

    /// <summary>The launch target the launcher for <paramref name="settings"/> has: its canonical address and runner.</summary>
    internal static string LaunchTargetFor(RunnerSettings settings) => TakeoverTag(Credentials(settings));


    /// <summary>The same engine and daemon, by value: two pins read at different times are separate objects.</summary>
    private static bool SameEngine(ContainerEngineSelection.Selected? a, ContainerEngineSelection.Selected? b)
        => a is not null && b is not null &&
           a.Configured == b.Configured && a.Executable == b.Executable &&
           PinIdentity(a.Context) == PinIdentity(b.Context);

    private static string PinIdentity(ContainerEnginePin? pin)
        => pin is null
            ? string.Empty
            : $"{pin.Kind}|{pin.Name}|{pin.Remote}|{string.Join(";", (pin.Endpoint ?? new Dictionary<string, string>()).OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{entry.Key}={entry.Value}"))}";

    /// <summary>Downloads <paramref name="image"/>, showing its progress, and reporting it for a launch.</summary>

    /// <summary>The GPU UUIDs `nvidia-smi -L` names, in its order.</summary>
    internal static IEnumerable<string> NamedUuids(string output)
        => System.Text.RegularExpressions.Regex.Matches(output, @"GPU-[0-9a-fA-F-]{8,}").Select(match => match.Value);

    /// <summary>
    /// The GPUs of <paramref name="gpus"/> that may be offered with <paramref name="chosen"/> in Settings:
    /// that one alone, found by its UUID (a list that names none cannot describe it), or all when none is chosen.
    /// </summary>
    internal static IReadOnlyList<HostGpu> OnlyChosen(IReadOnlyList<HostGpu> gpus, string? chosen)
        => chosen is null
            ? gpus
            : [.. gpus.Where(gpu => string.Equals(gpu.Uuid, chosen, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// The GPUs the launcher offers now, and what identifies that offer: the engine and context checked,
    /// the GPU chosen, and each GPU offered (a GPU gone after sleep changes it). An engine on another
    /// computer gives its containers that computer's GPUs, not these: then, or when this computer cannot
    /// list its own, the GPUs the checked containers reported are offered.
    /// </summary>
    /// <summary>
    /// What the launcher offers now and its signature: <see cref="CurrentOffer"/>, or nothing at all while
    /// it offers nothing (paused, stopping, quitting, or no GPU it can describe). Nothing is an offer of its
    /// own, with its own revision: a launch chosen before is then one chosen for an older offer.

    /// <summary>
    /// The host GPUs the last GPU check saw inside a container (`nvidia-smi -L` names each by UUID):
    /// one this computer lists but containers do not get is not offered. All of them when the check
    /// named no UUID to compare with.
    /// </summary>
    internal static HostGpuReport VisibleToContainers(HostGpuReport host, string? checkedOutput)
    {
        if (checkedOutput is null || host.Gpus.All(gpu => gpu.Uuid is null))
        {
            return host;
        }

        // In the order the containers list them (the executor classifies by its first GPU), not this computer's.
        var seen = host.Gpus.Where(gpu => gpu.Uuid is { } uuid && checkedOutput.Contains(uuid, StringComparison.OrdinalIgnoreCase))
            .OrderBy(gpu => checkedOutput.IndexOf(gpu.Uuid!, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Containers named GPUs of their own, none of them these: none of these is offered.
        var containersNamedUuids = checkedOutput.Contains("UUID: GPU-", StringComparison.OrdinalIgnoreCase);
        return seen.Count == 0 && !containersNamedUuids ? host : host with { Gpus = seen };
    }

    /// <summary>What the view says while available, from how the launcher stands with Offloadr.</summary>
    /// <param name="paused">Nothing is offered for now: it is checked again what can be.</param>
    private static string AvailableMessage(LauncherLinkState link, bool paused) => link.Connection switch
    {
        LauncherConnection.Online when paused => "Checking Docker and your GPU again. No session can start here until that is done.",
        LauncherConnection.Online when link.StreamLive => "Ready for sessions. A session's editor starts here when Offloadr needs it.",
        LauncherConnection.Refused or LauncherConnection.Unreachable => $"{link.Message ?? "Offloadr cannot be reached."} The app keeps trying.",
        _ => "Connecting to Offloadr…",
    };


    private const string EarlierAppContainer = "A runner container an earlier version of the app started is running on this computer. Stop the runner in the app to replace it.";

    private const string EarlierSettingsContainer = "A runner container for your earlier Offloadr address or runner key is running on this computer. Stop the runner in the app to replace it.";

    /// <summary>What a launch failure on invalid settings tells Offloadr: not the problem itself, which may name a folder on this computer.</summary>
    private const string SettingsReported = "The runner's settings in the desktop app need attention.";


    /// <summary>True for a failure that leaves this computer unable to launch anything, not just this image.</summary>
    private static bool LeavesNothingLaunchable(FaultKind kind) => kind is FaultKind.EngineMissing or FaultKind.EngineNotRunning
        or FaultKind.EngineNotResponding or FaultKind.EnginePermission or FaultKind.WindowsContainers or FaultKind.EngineUnreachable
        or FaultKind.GpuUnavailable or FaultKind.Settings or FaultKind.NameTaken;

    internal static string ShortImageId(string imageId)
    {
        var hex = imageId.StartsWith("sha256:", StringComparison.Ordinal) ? imageId["sha256:".Length..] : imageId;
        return hex.Length > 12 ? hex[..12] : hex;
    }

    private static FaultKind KindOf(Exception failure) => failure switch
    {
        ContainerEngineException engine => engine.Kind,
        LaunchPlanRejectedException => FaultKind.KeyRejected,
        _ => FaultKind.Unknown,
    };


    /// <summary>
    /// A URL's query: where a signed URL keeps its signature. An apostrophe may be part of a link
    /// (alex's-model), so it does not end one; a link in single quotes then loses its closing quote with
    /// its query, which only removes more. As the diagnostics export matches them.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex UrlQuery =
        new(@"(?<url>\bhttps?://[^\s?#""<>]+)\?[^\s""<>]*", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>A line with each URL's query removed: an engine's or agent's output may carry a signed URL.</summary>
    internal static string RedactUrlQueries(string line) => UrlQuery.Replace(line, "${url}?[redacted]");

    private static string RedactKey(string line, string? key)
        => key is { Length: >= 8 } ? line.Replace(key, "[runner key]", StringComparison.Ordinal) : line;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

}
