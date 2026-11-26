using System.Text.RegularExpressions;

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
public static partial class EngineFailureClassifier
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

    /// <summary>
    /// True when <paramref name="output"/> is positively the engine's own answer that it did not do what
    /// was asked: an error (Docker's daemon response, or Podman's "Error: …") that names why the
    /// container was turned down: the GPU it could not attach, the runtime that would not create it, a
    /// name or port that is taken, a disk that is full. Anything else proves nothing: the engine could not be reached, the
    /// connection to it broke, or a proxy in front of it answered in its place ("Internal Server Error"),
    /// and it may still do it. The prefix alone never counts: Podman words a lost answer the same way.
    /// </summary>
    public static bool IsRefusal(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        // Read from the error's own words: a folder or an image called "gateway" is no gateway answering.
        var words = QuotedValues().Replace(output, " ");
        if (Has(words, NotRunning) || Has(words, Network) || Has(words, Unanswered))
        {
            return false;
        }

        return (words.Contains("Error response from daemon", StringComparison.OrdinalIgnoreCase) ||
                words.TrimStart().StartsWith("Error:", StringComparison.OrdinalIgnoreCase)) &&
               (Has(words, Gpu) || Has(words, TurnedDown) || Has(words, DiskFull));
    }

    /// <summary>
    /// True when <paramref name="output"/>, of a GPU check that failed, says that containers do not get the
    /// GPU: the engine could not attach it, or nvidia-smi ran and found none. Any other failure (no answer,
    /// a connection that broke) says nothing of the GPU.
    /// </summary>
    public static bool SaysNoGpu(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var words = QuotedValues().Replace(output, " ");
        return !Has(words, NotRunning) && !Has(words, Network) && !Has(words, Unanswered) &&
               (Has(words, Gpu) || Has(words, NoGpu) ||
                (words.Contains("nvidia-smi", StringComparison.OrdinalIgnoreCase) && words.Contains("executable file not found", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// True when a GPU check failed for what its image asks for, or lacks: another image may pass on the
    /// same engine and driver.
    /// </summary>
    public static bool ConcernsOneImage(string? output)
        => !string.IsNullOrWhiteSpace(output) && Has(QuotedValues().Replace(output, " "), OneImage);

    private static readonly string[] OneImage =
    [
        "requirement error",
        "unsatisfied condition",
        "executable file not found",
        "exec format error",
        "no such image",
    ];

    /// <summary>What nvidia-smi says in a container that does not get a working GPU.</summary>
    private static readonly string[] NoGpu =
    [
        "no devices were found",
        "no devices found",
        "nvidia-smi has failed",
        "failed to initialize nvml",
        "unable to determine the device handle",
        "gpu is lost",
    ];

    /// <summary>
    /// The values an engine's error quotes: a path, address or image name, in quotes or on its own, and the
    /// path of a mount it would not make (to the end of what names it, as it may hold spaces). Text in
    /// quotes that is none of these stays: an error wrapped in another one is quoted too.
    /// </summary>
    [GeneratedRegex(
        @"(?<=bind source path does not exist:)[^\r\n]*" +
        @"|(?<=\bthe path\b).*?(?=is not shared)" +
        @"|""(?:/|\\|[a-z]:[\\/]|[a-z][a-z0-9+.-]*://)[^""\r\n]*""" +
        @"|(?<!\w)'(?:/|\\|[a-z]:[\\/]|[a-z][a-z0-9+.-]*://)[^'\r\n]*'" +
        @"|(?<![^\s=(\[,])(?:/|\\|[a-z]:[\\/]|[a-z][a-z0-9+.-]*://)[^\s""']*(?:(?<!:) [^\s:""']*[\\/][^\s""']*)*" +
        @"|(?<![^\s""'(=])[\w-]+(?:\.[\w-]+)+(?::\d+)?/[^\s""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuotedValues();

    /// <summary>What Docker's daemon, or Podman and its runtime, say when they decided not to create or start a container.</summary>
    private static readonly string[] TurnedDown =
    [
        "crun:",
        "runc:",
        "oci runtime error",
        "oci permission denied",
        "oci runtime create failed",
        "failed to create task for container",
        "failed to create shim task",
        "is already in use by container",
        "port is already allocated",
        "address already in use",
        "driver failed programming external connectivity",
        "no such container",
        "no such image",
        "invalid mount config",
        "mounts denied",
        "bind source path does not exist",
        "error while creating mount source path",
        "unknown or invalid runtime name",
        "executable file not found",
    ];

    /// <summary>
    /// What an error says when no answer came from the engine itself: a proxy in front of it gave up, or
    /// the wait ran out. The engine may have taken the request all the same.
    /// </summary>
    private static readonly string[] Unanswered =
    [
        "gateway",
        "timeout",
        "timed out",
        "deadline exceeded",
        "context canceled",
        "context cancelled",
        "service unavailable",
        "proxy",
        "eof",
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

        // Windows says so of the named pipe when the account is not in docker-users; a Unix socket
        // (Docker's or Podman's) says "dial unix …: connect: permission denied". A TCP connection
        // refused the same way is a network policy, not the engine.
        if (Has(output, Permission) ||
            (output.Contains("//./pipe/", StringComparison.Ordinal) && output.Contains("access is denied", StringComparison.OrdinalIgnoreCase)) ||
            (output.Contains("dial unix ", StringComparison.Ordinal) && output.Contains("connect: permission denied", StringComparison.OrdinalIgnoreCase)))
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
