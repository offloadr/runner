using System.Text.Json.Serialization;

namespace Offloadr.Runner.Supervisor.Containers;

public enum ContainerEngineKind
{
    Docker,
    Podman,
}

/// <summary>
/// Where a runner's commands go, kept across a crash: the endpoint behind a Docker CLI context or
/// Podman connection (<see cref="Name"/>, for display), or one the environment chose
/// (<see cref="Endpoint"/>). A pin by name alone is still honored as a context or connection. The kind says
/// which engine it belongs to, so it is passed the right way even before the engine is probed again.
/// </summary>
public sealed record ContainerEnginePin(string? Name, ContainerEngineKind Kind)
{
    /// <summary>
    /// The variables that reach the endpoint: DOCKER_HOST with its TLS settings, or CONTAINER_HOST with
    /// its SSH key. Every command gets them again, so after a restart without them (or with other
    /// values), or once the context or connection is changed, commands still reach the runner's engine.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Endpoint { get; init; }

    /// <summary>
    /// The engine runs on another computer: a folder on this one cannot be mounted into its containers.
    /// </summary>
    public bool Remote { get; init; }

    /// <summary>
    /// The pin as it is saved to disk: a password in an endpoint address is left out. After a restart
    /// the engine is then reached with the SSH keys or agent the CLI has, or reported unreachable.
    /// </summary>
    public ContainerEnginePin WithoutPasswords()
        => Endpoint is null
            ? this
            : this with { Endpoint = Endpoint.ToDictionary(entry => entry.Key, entry => WithoutPassword(entry.Value), StringComparer.Ordinal) };

    private static string WithoutPassword(string address)
    {
        var schemeEnd = address.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return address;
        }

        var authorityStart = schemeEnd + 3;
        var authorityEnd = address.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = address.Length;
        }

        var at = authorityEnd == authorityStart ? -1 : address.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
        var colon = at < 0 ? -1 : address.IndexOf(':', authorityStart, at - authorityStart);
        return colon < 0 ? address : address[..colon] + address[at..];
    }

    /// <summary>The context or connection name, or else the endpoint, for display.</summary>
    [JsonIgnore]
    public string Label => Name ?? EndpointText;

    private string EndpointText
        => string.Join(' ', (Endpoint ?? new Dictionary<string, string>()).OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{entry.Key}={entry.Value}"));

    public bool Equals(ContainerEnginePin? other)
        => other is not null &&
           Name == other.Name &&
           Kind == other.Kind &&
           string.Equals(EndpointText, other.EndpointText, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Name, Kind, EndpointText);
}

/// <summary>What the engine said about itself. <see cref="Available"/> is false when no engine answered.</summary>
public sealed record ContainerEngineInfo(
    bool Available,
    ContainerEngineKind Kind,
    string? Executable,
    string? ServerVersion,
    string? ServerOs,
    string? OperatingSystem,
    bool NvidiaRuntimeListed,
    string? Error)
{
    /// <summary>Why no engine is available, when <see cref="Error"/> is set.</summary>
    public FaultKind ErrorKind { get; init; }

    /// <summary>
    /// The memory the engine's containers can use (Docker Desktop: its WSL virtual machine's), as the
    /// engine reports it; null when it did not.
    /// </summary>
    public long? MemoryBytes { get; init; }

    public static ContainerEngineInfo Unavailable(
        string error,
        string? executable = null,
        ContainerEngineKind kind = ContainerEngineKind.Docker,
        FaultKind errorKind = FaultKind.Unknown)
        => new(false, kind, executable, null, null, null, false, error) { ErrorKind = errorKind };
}

public sealed record VolumeMount(string HostPath, string ContainerPath, bool ReadOnly);

/// <summary>A detached container. Environment values reach the engine through its process environment, never its arguments.</summary>
public sealed record ContainerSpec(
    string Name,
    string Image,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<VolumeMount> Volumes,
    IReadOnlyDictionary<string, string> Labels,
    bool Gpus);

public sealed record ContainerInfo(
    string Id,
    string Image,
    bool Running,
    int? ExitCode,
    IReadOnlyDictionary<string, string> Labels,
    string? ImageId = null)
{
    /// <summary>
    /// Never started: nothing of it ever ran. Docker's and Podman's "created" state, or one the engine
    /// is removing (or left dead as it removed it) that has no start time.
    /// </summary>
    public bool NeverStarted { get; init; }

    /// <summary>
    /// The engine is removing it, or left it dead as it removed it: it runs nothing, and cannot be
    /// started any more, whatever was asked of it before.
    /// </summary>
    public bool BeingRemoved { get; init; }

    /// <summary>
    /// Never started, and in a state a start can still take it from: an engine that was asked to start
    /// it, and never said it refused, may start it yet.
    /// </summary>
    public bool MayStartYet => NeverStarted && !Running && !BeingRemoved;
}

/// <param name="RepoTags">The names that point at the image; none once its tag moved to a newer one.</param>
public sealed record ImageInfo(string Id, string? RepoDigest, IReadOnlyList<string>? RepoTags = null);

public sealed record GpuAccessCheck(bool Succeeded, string Output)
{
    /// <summary>It was not answered (it never finished, or the engine's command line did not run): it says nothing of the GPU.</summary>
    public bool Inconclusive { get; init; }
}

/// <summary>An image the engine holds. <paramref name="Size"/> counts layers it shares with other images too.</summary>
public sealed record StoredImage(string Id, IReadOnlyList<string> RepoTags, IReadOnlyList<string> RepoDigests, long Size, DateTimeOffset? Created = null);

/// <summary>
/// The engine failed. <see cref="Kind"/> is what the engine's output (<paramref name="detail"/>) says
/// about the cause, unless the thrower already knows it.
/// </summary>
public sealed class ContainerEngineException(
    string message,
    string? detail = null,
    EngineOperation operation = EngineOperation.Command,
    FaultKind? kind = null) : Exception(string.IsNullOrWhiteSpace(detail) ? message : $"{message} {detail.Trim()}")
{
    public string? Detail { get; } = detail;

    /// <summary>What failed, in this app's words, without the engine's own output (<see cref="Detail"/>), which may hold a signed URL.</summary>
    public string Summary { get; } = message;

    public FaultKind Kind { get; } = kind ?? EngineFailureClassifier.Classify(operation, detail);

    /// <summary>
    /// The engine itself answered that it did not do what was asked, so nothing of it is still under way
    /// there. False when its answer may only be lost (the command line could not reach it, or was given
    /// up): the engine may still do it.
    /// </summary>
    public bool Refused { get; } = EngineFailureClassifier.IsRefusal(detail);
}

public interface IContainerEngine
{
    Task<ContainerEngineInfo> ProbeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Where the engine's commands go now (its Docker CLI context, Podman connection, or the endpoint
    /// the environment chooses), to pin a runner's commands to; null when nothing needs pinning.
    /// </summary>
    Task<ContainerEnginePin?> CurrentContextAsync(CancellationToken cancellationToken);

    Task PullAsync(string image, Action<string> onProgress, CancellationToken cancellationToken);

    Task<ImageInfo?> InspectImageAsync(string image, CancellationToken cancellationToken);

    /// <summary>Every image the engine holds, with the names and digests that point at it and its size.</summary>
    Task<IReadOnlyList<StoredImage>> ListImagesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes <paramref name="reference"/> (a name, a digest reference or an ID), never by force: the engine
    /// keeps an image a container still uses. Removing a name or digest reference removes the image only
    /// when no other name points at it. False when the engine kept it, or it is gone.
    /// </summary>
    Task<bool> RemoveImageAsync(string reference, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the container without starting it, and returns its ID. A created container runs nothing
    /// until <see cref="StartAsync"/>: one the engine creates after this call was cancelled or given up
    /// is inert, and can only be found and removed.
    /// </summary>
    Task<string> CreateAsync(ContainerSpec spec, CancellationToken cancellationToken);

    /// <summary>
    /// Starts a created container, by its ID. When this fails or is given up, the container is still
    /// known by that ID, and what became of it can be asked.
    /// </summary>
    /// <param name="stillWanted">Asked at the last moment before the engine's command is run: false, and it is not (a <c>ProcessNotSpawnedException</c>).</param>
    Task StartAsync(string container, CancellationToken cancellationToken, Func<bool>? stillWanted = null);

    /// <summary>Null when no container has that name.</summary>
    Task<ContainerInfo?> InspectContainerAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// One environment variable of a container (the runner key an adopted runner was started with,
    /// say); null when the container or the variable is missing. The value is never logged.
    /// </summary>
    Task<string?> ReadEnvironmentVariableAsync(string container, string name, CancellationToken cancellationToken);

    /// <summary>Reads a file inside a running container; null when it does not exist yet.</summary>
    Task<string?> ReadFileAsync(string container, string path, CancellationToken cancellationToken);

    /// <summary>
    /// Follows the container log, starting with its last <paramref name="tail"/> lines, until it stops
    /// or the token is cancelled. Throws when following fails, for example while the engine restarts.
    /// </summary>
    Task FollowLogsAsync(string container, int tail, Action<string> onLine, CancellationToken cancellationToken);

    Task StopAsync(string container, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Removes the container if it exists.</summary>
    Task RemoveAsync(string container, CancellationToken cancellationToken);

    /// <summary>
    /// Removes <paramref name="container"/> only if it does not run (never by force): false when the
    /// engine kept it because it runs. Gone already counts as removed; any other failure throws.
    /// </summary>
    Task<bool> RemoveStoppedAsync(string container, CancellationToken cancellationToken);

    /// <summary>
    /// The container, running or not, whose <paramref name="label"/> is <paramref name="value"/>, whatever
    /// it is called; null when there is none.
    /// </summary>
    Task<ContainerInfo?> FindByLabelAsync(string label, string value, CancellationToken cancellationToken);

    /// <summary>Runs <c>nvidia-smi -L</c> in a throwaway container from <paramref name="image"/> with GPU access.</summary>
    Task<GpuAccessCheck> CheckGpuAccessAsync(string image, CancellationToken cancellationToken);

    /// <summary>
    /// The GPUs a throwaway container from <paramref name="image"/> gets, with their names and memory
    /// (<c>nvidia-smi --query-gpu</c>): what the engine's own computer has, where this one cannot tell
    /// (a remote engine). Empty when that cannot be read.
    /// </summary>
    Task<IReadOnlyList<Hardware.HostGpu>> ListContainerGpusAsync(string image, CancellationToken cancellationToken);
}
