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
    public static ContainerEngineInfo Unavailable(string error, string? executable = null, ContainerEngineKind kind = ContainerEngineKind.Docker)
        => new(false, kind, executable, null, null, null, false, error);
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
    string? ImageId = null);

public sealed record ImageInfo(string Id, string? RepoDigest);

public sealed record GpuAccessCheck(bool Succeeded, string Output);

public sealed class ContainerEngineException(string message, string? detail = null) : Exception(
    string.IsNullOrWhiteSpace(detail) ? message : $"{message} {detail.Trim()}")
{
    public string? Detail { get; } = detail;
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

    /// <summary>Starts the container detached and returns its id.</summary>
    Task<string> RunDetachedAsync(ContainerSpec spec, CancellationToken cancellationToken);

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

    /// <summary>Runs <c>nvidia-smi -L</c> in a throwaway container from <paramref name="image"/> with GPU access.</summary>
    Task<GpuAccessCheck> CheckGpuAccessAsync(string image, CancellationToken cancellationToken);
}
