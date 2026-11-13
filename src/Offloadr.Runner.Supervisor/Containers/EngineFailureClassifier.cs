namespace Offloadr.Runner.Supervisor.Containers;

/// <summary>What the engine was doing when it failed: the same words mean different things in a download.</summary>
public enum EngineOperation
{
    Command,
    Pull,
    Run,
}

/// <summary>
/// Tells from what Docker or Podman printed what kind of failure it was. Only phrases the engines
/// print for that cause count; anything else is <see cref="FaultKind.Unknown"/>, and the person sees
/// the engine's own words.
/// </summary>
public static class EngineFailureClassifier
{
    private static readonly string[] DiskFull = ["no space left on device", "not enough space on the disk", "disk quota exceeded"];

    // The engine did not answer at all: these come before everything else, since its socket or pipe
    // being unavailable says nothing about images or GPUs.
    private static readonly string[] Permission =
    [
        "permission denied while trying to connect to the docker daemon",
        "permission denied while trying to connect to the docker api",
    ];

    // "error during connect" covers Docker Desktop's pipe missing (not started) and a DOCKER_HOST that
    // does not answer; either way no engine is there to use.
    private static readonly string[] NotRunning =
    [
        "cannot connect to the docker daemon",
        "is the docker daemon running",
        "error during connect",
        "docker desktop is unable to start",
        "docker desktop is not running",
        "cannot connect to podman",
        "unable to connect to podman socket",
        "failed to connect to the docker api",
    ];

    private static readonly string[] Gpu =
    [
        "could not select device driver",
        "nvidia-container-cli",
        "unknown or invalid runtime name: nvidia",
        "cdi device injection failed",
        "unresolvable cdi devices",
        "failed to inject cdi devices",
        "nvidia driver",
        "libnvidia-ml",
    ];

    private static readonly string[] ImageMissing =
    [
        "manifest unknown",
        "pull access denied",
        "repository does not exist",
        "name unknown",
        "requested access to the resource is denied",
        "denied: denied",
        "unauthorized",
    ];

    private static readonly string[] Network =
    [
        "dial tcp",
        "i/o timeout",
        "tls handshake timeout",
        "no such host",
        "connection reset by peer",
        "connection refused",
        "network is unreachable",
        "client.timeout exceeded",
        "net/http: request canceled",
        "toomanyrequests",
        "unexpected eof",
        "proxyconnect",
        "temporary failure in name resolution",
        "503 service unavailable",
        "502 bad gateway",
    ];

    public static FaultKind Classify(EngineOperation operation, string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return FaultKind.Unknown;
        }

        if (Has(output, DiskFull))
        {
            return FaultKind.DiskFull;
        }

        // Windows says so of the named pipe when the account is not in docker-users.
        if (Has(output, Permission) ||
            (output.Contains("//./pipe/", StringComparison.Ordinal) && output.Contains("access is denied", StringComparison.OrdinalIgnoreCase)))
        {
            return FaultKind.EnginePermission;
        }

        if (Has(output, NotRunning))
        {
            return FaultKind.EngineNotRunning;
        }

        if (operation == EngineOperation.Run && Has(output, Gpu))
        {
            return FaultKind.GpuUnavailable;
        }

        if (operation == EngineOperation.Pull)
        {
            // Network words come last: a refused image is answered over a working connection.
            if (Has(output, ImageMissing))
            {
                return FaultKind.ImageUnavailable;
            }

            if (Has(output, Network))
            {
                return FaultKind.RegistryUnreachable;
            }
        }

        return FaultKind.Unknown;
    }

    private static bool Has(string output, string[] phrases)
        => phrases.Any(phrase => output.Contains(phrase, StringComparison.OrdinalIgnoreCase));
}
