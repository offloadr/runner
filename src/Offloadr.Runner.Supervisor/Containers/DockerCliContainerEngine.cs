using System.Globalization;
using Offloadr.Runner.Supervisor.Processes;

namespace Offloadr.Runner.Supervisor.Containers;

/// <summary>
/// Drives the docker (or podman) command line. The CLI is what people already run by hand, and it
/// hides the differences between Docker Desktop's named pipe, rootless sockets and podman.
/// </summary>
public sealed class DockerCliContainerEngine(IProcessRunner processes, Func<string?> configuredExecutable, Func<ContainerEnginePin?>? pinnedContext = null) : IContainerEngine
{
    private static readonly TimeSpan DefaultGpuCheckTimeout = TimeSpan.FromMinutes(2);

    internal DockerCliContainerEngine(IProcessRunner processes, Func<string?> configuredExecutable, TimeSpan gpuCheckTimeout)
        : this(processes, configuredExecutable)
    {
        _gpuCheckTimeout = gpuCheckTimeout;
    }

    private readonly TimeSpan _gpuCheckTimeout = DefaultGpuCheckTimeout;

    private static readonly TimeSpan QuickCommandTimeout = TimeSpan.FromSeconds(30);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ContainerEngineKind> _detectedKinds = new(StringComparer.Ordinal);

    public async Task<ContainerEngineInfo> ProbeAsync(CancellationToken cancellationToken)
    {
        var engine = Locate();
        if (engine is null)
        {
            return ContainerEngineInfo.Unavailable(string.IsNullOrWhiteSpace(configuredExecutable())
                ? "Docker was not found. Install Docker Desktop (Windows, macOS) or Docker Engine with the NVIDIA Container Toolkit (Linux)."
                : $"The container engine '{configuredExecutable()}' was not found.");
        }

        ProcessResult version;
        try
        {
            version = await RunQuickAsync(engine, ["version", "--format", "json"], cancellationToken).ConfigureAwait(false);
        }
        catch (ContainerEngineException ex)
        {
            // Not answering, or not even starting: either way the engine is unavailable right now.
            return ContainerEngineInfo.Unavailable(ex.Message, engine.Executable, engine.Kind);
        }

        if (engine.Kind == ContainerEngineKind.Docker && DockerCliJson.IsPodman(version.StandardOutput))
        {
            // Podman installed as `docker` (podman-docker): it gets Podman's arguments from now on.
            engine = engine with { Kind = ContainerEngineKind.Podman };
            _detectedKinds[engine.Executable] = ContainerEngineKind.Podman;
        }

        var serverVersion = DockerCliJson.ServerVersion(version.StandardOutput, engine.Kind);
        if (!version.Succeeded || serverVersion is null)
        {
            return ContainerEngineInfo.Unavailable(
                $"{Name(engine)} is installed but its engine is not running. Start {(engine.Kind == ContainerEngineKind.Docker ? "Docker Desktop" : "podman")} and try again.",
                engine.Executable,
                engine.Kind);
        }

        ProcessResult info;
        try
        {
            info = await RunQuickAsync(engine, ["info", "--format", "json"], cancellationToken).ConfigureAwait(false);
        }
        catch (ContainerEngineException ex)
        {
            return ContainerEngineInfo.Unavailable(ex.Message, engine.Executable, engine.Kind);
        }

        if (!info.Succeeded)
        {
            // The engine answered `version` but not `info` (restarting in between, say): not usable yet.
            return ContainerEngineInfo.Unavailable(
                $"{Name(engine)} did not report its engine details. {info.StandardError.Trim()}".Trim(),
                engine.Executable,
                engine.Kind);
        }

        var (serverOs, operatingSystem, nvidia) = DockerCliJson.Info(info.StandardOutput);
        if (serverOs is not null && !serverOs.Equals("linux", StringComparison.OrdinalIgnoreCase))
        {
            return new ContainerEngineInfo(false, engine.Kind, engine.Executable, serverVersion, serverOs, operatingSystem, nvidia,
                "The engine runs Windows containers. Switch Docker Desktop to Linux containers.");
        }

        return new ContainerEngineInfo(true, engine.Kind, engine.Executable, serverVersion, serverOs, operatingSystem, nvidia, null);
    }

    public async Task PullAsync(string image, Action<string> onProgress, CancellationToken cancellationToken)
    {
        var engine = Require();
        var lastLines = new Queue<string>();
        var exitCode = await StreamAsync(
            Spec(engine, ["pull", image]),
            line =>
            {
                lock (lastLines)
                {
                    lastLines.Enqueue(line);
                    if (lastLines.Count > 3)
                    {
                        lastLines.Dequeue();
                    }
                }

                onProgress(line);
            },
            cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            string detail;
            lock (lastLines)
            {
                detail = string.Join(" ", lastLines);
            }

            throw new ContainerEngineException($"Could not pull {image}.", detail);
        }
    }

    public async Task<ImageInfo?> InspectImageAsync(string image, CancellationToken cancellationToken)
    {
        var result = await RunQuickAsync(Require(), ["image", "inspect", image], cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return DockerCliJson.Image(result.StandardOutput);
        }

        // Only a missing image is null; an engine that does not answer is not proof the image is gone.
        return IsImageNotFound(result) ? null : throw new ContainerEngineException($"Could not look up {image}.", result.StandardError);
    }

    public async Task<string> RunDetachedAsync(ContainerSpec spec, CancellationToken cancellationToken)
    {
        var engine = Require();
        var result = await processes.RunAsync(
            Spec(engine, DockerCliArguments.Run(spec, engine.Kind), spec.Environment),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new ContainerEngineException($"Could not start the runner container from {spec.Image}.", result.StandardError);
        }

        return result.StandardOutput.Trim();
    }

    public async Task<ContainerInfo?> InspectContainerAsync(string name, CancellationToken cancellationToken)
    {
        var result = await RunQuickAsync(Require(), ["container", "inspect", name], cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return DockerCliJson.Container(result.StandardOutput);
        }

        if (IsNotFound(result))
        {
            return null;
        }

        throw new ContainerEngineException($"Could not inspect container {name}.", result.StandardError);
    }

    public async Task<string?> ReadEnvironmentVariableAsync(string container, string name, CancellationToken cancellationToken)
    {
        var result = await RunQuickAsync(Require(), ["container", "inspect", container], cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return DockerCliJson.EnvironmentVariable(result.StandardOutput, name);
        }

        // A missing container has no variables; any other failure is the engine's, not an answer.
        return IsNotFound(result)
            ? null
            : throw new ContainerEngineException($"Could not inspect container {container}.", result.StandardError);
    }

    public async Task<string?> ReadFileAsync(string container, string path, CancellationToken cancellationToken)
    {
        var result = await RunQuickAsync(Require(), ["exec", container, "cat", path], cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput : null;
    }

    public async Task FollowLogsAsync(string container, int tail, Action<string> onLine, CancellationToken cancellationToken)
    {
        int exitCode;
        try
        {
            exitCode = await StreamAsync(
                Spec(Require(), ["logs", "--follow", "--tail", tail.ToString(CultureInfo.InvariantCulture), container]),
                onLine,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Following stops when the caller is done with this container.
            return;
        }

        if (exitCode != 0 && !cancellationToken.IsCancellationRequested)
        {
            throw new ContainerEngineException($"Following the log of {container} stopped with exit code {exitCode}.");
        }
    }

    public async Task StopAsync(string container, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var engine = Require();
        var seconds = ((int)Math.Ceiling(timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        // --time is the grace period before the engine kills the container, not a deadline for the
        // CLI: a hung engine must not hold the supervisor's command loop for good.
        var deadline = timeout + QuickCommandTimeout;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(deadline);
        ProcessResult result;
        try
        {
            result = await processes.RunAsync(
                Spec(engine, ["stop", "--time", seconds, container]),
                bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ContainerEngineException($"{Name(engine)} did not stop container {container} within {deadline.TotalSeconds:0} seconds.");
        }
        catch (ProcessStartException ex)
        {
            throw new ContainerEngineException(ex.Message);
        }

        if (!result.Succeeded && !IsNotFound(result))
        {
            throw new ContainerEngineException($"Could not stop container {container}.", result.StandardError);
        }
    }

    public async Task RemoveAsync(string container, CancellationToken cancellationToken)
    {
        var result = await RunQuickAsync(Require(), ["rm", "--force", container], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded && !IsNotFound(result))
        {
            throw new ContainerEngineException($"Could not remove container {container}.", result.StandardError);
        }
    }

    public async Task<GpuAccessCheck> CheckGpuAccessAsync(string image, CancellationToken cancellationToken)
    {
        var engine = Require();
        var limit = _gpuCheckTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        var name = $"offloadr-gpu-check-{Guid.NewGuid():N}";
        ProcessResult result;
        try
        {
            result = await processes.RunAsync(
                Spec(engine, DockerCliArguments.GpuCheck(image, engine.Kind, name)),
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Killing the CLI leaves the container running in the engine, holding the GPU, and `--rm`
            // only removes it once a hanging nvidia-smi exits; so it is removed here.
            await RemoveGpuCheckAsync(engine, name).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            // A broken driver or toolkit can leave nvidia-smi hanging: that is the answer to show.
            return new GpuAccessCheck(false, $"The GPU check did not finish within {limit.TotalSeconds:0} seconds. Check the NVIDIA driver and container toolkit.");
        }
        catch (ProcessStartException ex)
        {
            return new GpuAccessCheck(false, ex.Message);
        }

        var output = (result.Succeeded ? result.StandardOutput : result.StandardError + result.StandardOutput).Trim();
        return new GpuAccessCheck(result.Succeeded && output.Contains("GPU", StringComparison.Ordinal), output);
    }

    /// <summary>Removes a GPU check that did not finish, best effort, with its own time limit.</summary>
    private async Task RemoveGpuCheckAsync(LocatedEngine engine, string name)
    {
        try
        {
            await RunQuickAsync(engine, ["rm", "--force", name], CancellationToken.None).ConfigureAwait(false);
        }
        catch (ContainerEngineException)
        {
            // The engine does not answer either; nothing more can be done from here.
        }
    }

    /// <summary>
    /// Where the CLI's commands go now, to pin a runner's commands to: the endpoint the environment
    /// chooses, the current Docker context, or a remote Podman's default connection; null when there
    /// is none (a local Podman). Throws when the CLI cannot tell, rather than leave commands unpinned.
    /// </summary>
    public async Task<ContainerEnginePin?> CurrentContextAsync(CancellationToken cancellationToken)
    {
        var engine = Locate();
        if (engine is null)
        {
            return null;
        }

        // An endpoint the environment chooses is what the CLI uses. It is pinned as it is, so a restart
        // of the app without those variables (or with others) still reaches the same engine.
        if (engine.Kind == ContainerEngineKind.Podman && Variable("CONTAINER_CONNECTION") is { } connectionName)
        {
            return ConnectionPin(connectionName, await ConnectionListAsync(engine, cancellationToken).ConfigureAwait(false));
        }

        var hostVariable = engine.Kind == ContainerEngineKind.Podman ? "CONTAINER_HOST" : "DOCKER_HOST";
        ContainerEnginePin EnvironmentEndpoint()
        {
            var endpoint = EndpointVariables(engine.Kind)
                .Select(name => (Name: name, Value: Variable(name)))
                .Where(variable => variable.Value is not null)
                .ToDictionary(variable => variable.Name, variable => variable.Value!, StringComparer.Ordinal);
            return new ContainerEnginePin(null, engine.Kind) { Endpoint = endpoint, Remote = IsRemoteAddress(endpoint[hostVariable]) };
        }

        // With DOCKER_CONTEXT set as well, the CLI says below which of the two it uses.
        var dockerHostSet = engine.Kind == ContainerEngineKind.Docker && Variable("DOCKER_HOST") is not null;
        if (Variable(hostVariable) is not null && (engine.Kind == ContainerEngineKind.Podman || Variable("DOCKER_CONTEXT") is null))
        {
            return EnvironmentEndpoint();
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(QuickCommandTimeout);
            if (engine.Kind == ContainerEngineKind.Podman)
            {
                // Only a remote client (Windows and macOS always, podman-remote anywhere) talks to a
                // connection; it reports a server next to its client, a local Podman does not.
                var version = await processes.RunAsync(new ProcessSpec(engine.Executable, ["version", "--format", "json"]), timeout.Token).ConfigureAwait(false);
                if (!version.Succeeded)
                {
                    throw new ContainerEngineException("Could not tell which Podman connection to use.", version.StandardError);
                }

                if (DockerCliJson.ServerVersion(version.StandardOutput) is null)
                {
                    // A local Podman: there is no connection to pin.
                    return null;
                }

                var connections = await processes.RunAsync(
                    new ProcessSpec(engine.Executable, ["system", "connection", "list", "--format", "json"]),
                    timeout.Token).ConfigureAwait(false);
                if (!connections.Succeeded)
                {
                    throw new ContainerEngineException("Could not tell which Podman connection to use.", connections.StandardError);
                }

                return DockerCliJson.DefaultConnection(connections.StandardOutput) is { } connection
                    ? ConnectionPin(connection, connections.StandardOutput)
                    : null;
            }

            var result = await processes.RunAsync(new ProcessSpec(engine.Executable, ["context", "show"]), timeout.Token).ConfigureAwait(false);
            var context = result.StandardOutput.Trim();
            if (result.Succeeded && context == "default" && dockerHostSet)
            {
                // DOCKER_HOST is what the CLI uses, not the context DOCKER_CONTEXT names.
                return EnvironmentEndpoint();
            }

            if (result.Succeeded && context.Length > 0 && !context.Contains('\n', StringComparison.Ordinal))
            {
                // The endpoint behind the name is pinned, not the name: `docker context update` can point
                // the same name at another daemon while the runner runs. One that cannot be read is an
                // engine error to retry, never a pin by the name alone.
                var inspected = await processes.RunAsync(new ProcessSpec(engine.Executable, ["context", "inspect", context]), timeout.Token).ConfigureAwait(false);
                if (!inspected.Succeeded || DockerCliJson.ContextEndpoint(inspected.StandardOutput) is not { } resolved)
                {
                    throw new ContainerEngineException($"Could not read where the Docker context '{context}' points.", inspected.StandardError);
                }

                var variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["DOCKER_HOST"] = resolved.Host };
                // TLS without verification needs no files of its own; with them, verification is on
                // unless the context skips it.
                if (resolved.SkipTlsVerify)
                {
                    variables["DOCKER_TLS"] = "1";
                }

                if (resolved.TlsFolder is { } tlsFolder)
                {
                    variables["DOCKER_CERT_PATH"] = tlsFolder;
                    if (!resolved.SkipTlsVerify)
                    {
                        variables["DOCKER_TLS_VERIFY"] = "1";
                    }
                }

                return new ContainerEnginePin(context, ContainerEngineKind.Docker) { Endpoint = variables, Remote = IsRemoteAddress(resolved.Host) };
            }

            // A CLI from before contexts has only the endpoint its environment names: nothing to pin.
            if (!result.Succeeded && result.StandardError.Contains("is not a docker command", StringComparison.OrdinalIgnoreCase))
            {
                return dockerHostSet ? EnvironmentEndpoint() : null;
            }

            throw new ContainerEngineException("Could not read the current Docker context.", result.StandardError);
        }
        catch (ProcessStartException ex)
        {
            // Failing is the safe answer: unpinned commands could reach whichever daemon the default
            // points at by the time they run.
            throw new ContainerEngineException(ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ContainerEngineException($"{Name(engine)} did not tell which engine its commands reach within {QuickCommandTimeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>
    /// A command for the engine, pinned to the runner's Docker context or Podman connection when one
    /// is set.
    /// </summary>
    private ProcessSpec Spec(LocatedEngine engine, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var pin = pinnedContext?.Invoke();
        if (pin is null)
        {
            return environment is null ? new ProcessSpec(engine.Executable, arguments) : new ProcessSpec(engine.Executable, arguments, environment);
        }

        if (pin.Endpoint is not { Count: > 0 } && pin.Name is { } name)
        {
            // The context or connection alone decides, not an endpoint the environment names now:
            // Docker's `default` context takes its endpoint from DOCKER_HOST, so a restart of the app
            // with DOCKER_HOST set would otherwise reach another daemon.
            return new ProcessSpec(
                engine.Executable,
                [engine.Kind == ContainerEngineKind.Podman ? "--connection" : "--context", name, .. arguments],
                environment,
                EndpointVariables(engine.Kind));
        }

        // The endpoint the runner started on (chosen by the environment, or behind a context or
        // connection), exactly: its variables are set again, and the others that would change where
        // commands go are not inherited.
        var merged = new Dictionary<string, string>(pin.Endpoint ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var (variable, value) in environment ?? new Dictionary<string, string>())
        {
            merged[variable] = value;
        }

        string[] removed = engine.Kind == ContainerEngineKind.Podman
            ? [.. EndpointVariables(ContainerEngineKind.Podman), "CONTAINER_CONNECTION"]
            : [.. EndpointVariables(ContainerEngineKind.Docker), "DOCKER_CONTEXT"];
        return new ProcessSpec(engine.Executable, arguments, merged, [.. removed.Where(variable => !merged.ContainsKey(variable))]);
    }

    /// <summary>
    /// A Podman connection pinned by the address and SSH key behind its name, since the name can be
    /// added again for another machine while the runner runs. One whose address is unknown is an
    /// engine error to retry, never a pin by the name alone.
    /// </summary>
    private static ContainerEnginePin ConnectionPin(string name, string connectionList)
    {
        if (DockerCliJson.ConnectionUri(connectionList, name) is not { Length: > 0 } uri)
        {
            throw new ContainerEngineException($"Could not read where the Podman connection '{name}' points.");
        }

        var variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["CONTAINER_HOST"] = uri };
        if (DockerCliJson.ConnectionIdentity(connectionList, name) is { } identity)
        {
            variables["CONTAINER_SSHKEY"] = identity;
        }

        return new ContainerEnginePin(name, ContainerEngineKind.Podman) { Endpoint = variables, Remote = IsRemoteAddress(uri) };
    }

    /// <summary>Podman's connection list; an engine error when it cannot be read.</summary>
    private async Task<string> ConnectionListAsync(LocatedEngine engine, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(QuickCommandTimeout);
        ProcessResult connections;
        try
        {
            connections = await processes.RunAsync(
                new ProcessSpec(engine.Executable, ["system", "connection", "list", "--format", "json"]),
                timeout.Token).ConfigureAwait(false);
        }
        catch (ProcessStartException ex)
        {
            throw new ContainerEngineException(ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ContainerEngineException($"Podman did not list its connections within {QuickCommandTimeout.TotalSeconds:0} seconds.");
        }

        return connections.Succeeded
            ? connections.StandardOutput
            : throw new ContainerEngineException("Could not tell which Podman connection to use.", connections.StandardError);
    }

    /// <summary>
    /// True when an engine address points at another computer, whose engine cannot see this computer's
    /// folders. A socket, a named pipe and loopback (Docker Desktop, a Podman machine) are this one.
    /// </summary>
    internal static bool IsRemoteAddress(string? address)
        => Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
           uri.Scheme is not ("unix" or "npipe" or "fd") &&
           !uri.IsLoopback &&
           !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>The variables that choose an engine endpoint without a context or connection, with their credentials.</summary>
    private static string[] EndpointVariables(ContainerEngineKind kind) => kind == ContainerEngineKind.Podman
        ? ["CONTAINER_HOST", "CONTAINER_SSHKEY"]
        : ["DOCKER_HOST", "DOCKER_TLS", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH"];

    private static string? Variable(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value) ? value : null;

    private LocatedEngine Require()
        => Locate() ?? throw new ContainerEngineException("Docker was not found.");

    /// <summary>
    /// The engine to run, as the probe identified it when that differs from its file name. A pinned
    /// Podman connection identifies it too: after a restart, Podman installed as `docker` is otherwise
    /// only recognized by the probe, whose first command must already reach that connection.
    /// </summary>
    private LocatedEngine? Locate()
    {
        var engine = ContainerEngineLocator.Locate(configuredExecutable());
        if (engine is null)
        {
            return null;
        }

        if (_detectedKinds.TryGetValue(engine.Executable, out var kind))
        {
            return engine with { Kind = kind };
        }

        return pinnedContext?.Invoke() is { Kind: ContainerEngineKind.Podman } ? engine with { Kind = ContainerEngineKind.Podman } : engine;
    }

    private async Task<ProcessResult> RunQuickAsync(LocatedEngine engine, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(QuickCommandTimeout);
        try
        {
            return await processes.RunAsync(Spec(engine, arguments), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ContainerEngineException($"{Name(engine)} did not answer within {QuickCommandTimeout.TotalSeconds:0} seconds.");
        }
        catch (ProcessStartException ex)
        {
            // An executable that no longer starts (permissions, a broken replacement) is an engine
            // outage like any other: the runner stays supervised and the command is retried.
            throw new ContainerEngineException(ex.Message);
        }
    }

    /// <summary>Streams a command's output; an executable that does not start is an engine failure.</summary>
    private async Task<int> StreamAsync(ProcessSpec spec, Action<string> onLine, CancellationToken cancellationToken)
    {
        try
        {
            return await processes.StreamAsync(spec, onLine, cancellationToken).ConfigureAwait(false);
        }
        catch (ProcessStartException ex)
        {
            throw new ContainerEngineException(ex.Message);
        }
    }

    /// <summary>
    /// The engine answered that the container does not exist. Only container-specific answers count:
    /// a connection error such as "dial tcp: lookup …: no such host" means the engine did not answer,
    /// and must never pass for a container that is gone.
    /// </summary>
    private static bool IsNotFound(ProcessResult result)
        => result.StandardError.Contains("No such container", StringComparison.OrdinalIgnoreCase) ||
           result.StandardError.Contains("No such object", StringComparison.OrdinalIgnoreCase) ||
           result.StandardError.Contains("no container with name or ID", StringComparison.OrdinalIgnoreCase);

    private static bool IsImageNotFound(ProcessResult result)
        => result.StandardError.Contains("No such image", StringComparison.OrdinalIgnoreCase) ||
           result.StandardError.Contains("No such object", StringComparison.OrdinalIgnoreCase) ||
           result.StandardError.Contains("image not known", StringComparison.OrdinalIgnoreCase);

    private static string Name(LocatedEngine engine) => engine.Kind == ContainerEngineKind.Docker ? "Docker" : "Podman";
}
