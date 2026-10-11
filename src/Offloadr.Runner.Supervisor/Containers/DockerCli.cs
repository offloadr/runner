using System.Text.Json;

namespace Offloadr.Runner.Supervisor.Containers;

/// <summary>Builds docker/podman command lines. Pure, so the exact arguments are unit-tested.</summary>
internal static class DockerCliArguments
{
    public static IReadOnlyList<string> Run(ContainerSpec spec, ContainerEngineKind kind)
    {
        var arguments = new List<string> { "run", "--detach", "--name", spec.Name, "--pull", "never", "--restart", "no" };
        if (spec.Gpus)
        {
            arguments.AddRange(GpuArguments(kind));
        }

        foreach (var (name, value) in spec.Labels.OrderBy(label => label.Key, StringComparer.Ordinal))
        {
            arguments.Add("--label");
            arguments.Add($"{name}={value}");
        }

        // Name only: the engine copies the value from its own environment, so secrets stay out of argv.
        foreach (var name in spec.Environment.Keys.Order(StringComparer.Ordinal))
        {
            arguments.Add("--env");
            arguments.Add(name);
        }

        // Docker leaves a bind mount's SELinux labels as they are, so on an enforcing host the runner
        // could not read the models folder; relabelling it (:z) would rewrite the labels of the
        // person's own files instead. Podman gets the same option with its GPU arguments.
        if (kind == ContainerEngineKind.Docker && spec.Volumes.Count > 0)
        {
            arguments.AddRange(["--security-opt", "label=disable"]);
        }

        foreach (var volume in spec.Volumes)
        {
            arguments.Add("--volume");
            arguments.Add($"{volume.HostPath}:{volume.ContainerPath}{(volume.ReadOnly ? ":ro" : string.Empty)}");
        }

        arguments.Add(spec.Image);
        return arguments;
    }

    /// <param name="name">Names the check's container, so one left hanging can be removed.</param>
    public static IReadOnlyList<string> GpuCheck(string image, ContainerEngineKind kind, string name)
    {
        var arguments = new List<string> { "run", "--rm", "--name", name, "--pull", "never" };
        arguments.AddRange(GpuArguments(kind));
        arguments.AddRange(["--entrypoint", "nvidia-smi", image, "-L"]);
        return arguments;
    }

    // Podman: as in NVIDIA's CDI instructions, SELinux labelling is turned off, so an enforcing host
    // neither denies the GPU device files nor the read-only models folder, which Podman would not
    // relabel (and relabelling it with :z would rewrite the labels of the person's own files).
    private static IEnumerable<string> GpuArguments(ContainerEngineKind kind) => kind == ContainerEngineKind.Podman
        ? ["--device", "nvidia.com/gpu=all", "--security-opt", "label=disable"]
        : ["--gpus", "all"];
}

/// <summary>Reads the JSON that <c>docker version/info/inspect --format json</c> print. Tolerates podman's shapes.</summary>
internal static class DockerCliJson
{
    /// <summary>
    /// True when `version --format json` came from Podman, whatever the executable is called (the
    /// podman-docker package installs Podman as `docker`). Only Podman's client reports `OsArch`;
    /// Docker's reports `Os` and `Arch`.
    /// </summary>
    public static bool IsPodman(string json)
    {
        using var document = Parse(json);
        return document is not null &&
               document.RootElement.ValueKind == JsonValueKind.Object &&
               String(document.RootElement, "Client", "OsArch") is not null;
    }

    /// <summary>
    /// The engine version, or null when no engine answered. Local Podman on Linux is daemonless and
    /// prints only a client, which is then the engine; a Docker client without a server is not.
    /// </summary>
    public static string? ServerVersion(string json, ContainerEngineKind kind = ContainerEngineKind.Docker)
    {
        using var document = Parse(json);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        return String(root, "Server", "Version") ??
               String(root, "Version", "Version") ??
               (kind == ContainerEngineKind.Podman ? String(root, "Client", "Version") : null);
    }

    /// <summary>Server OS type, OS description and whether an nvidia runtime is registered.</summary>
    public static (string? ServerOs, string? OperatingSystem, bool NvidiaRuntime) Info(string json)
    {
        using var document = Parse(json);
        if (document is null)
        {
            return (null, null, false);
        }

        var root = document.RootElement;
        var nvidia = root.TryGetProperty("Runtimes", out var runtimes) &&
            runtimes.ValueKind == JsonValueKind.Object &&
            runtimes.EnumerateObject().Any(runtime => runtime.Name.Contains("nvidia", StringComparison.OrdinalIgnoreCase));
        return (
            String(root, "OSType") ?? String(root, "host", "os"),
            String(root, "OperatingSystem") ?? String(root, "host", "distribution", "distribution"),
            nvidia);
    }

    /// <summary>The name of Podman's default connection in `system connection list --format json`, or null.</summary>
    public static string? DefaultConnection(string json)
    {
        using var document = Parse(json);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var connection in document.RootElement.EnumerateArray())
        {
            if (connection.ValueKind == JsonValueKind.Object &&
                connection.TryGetProperty("Default", out var isDefault) &&
                isDefault.ValueKind == JsonValueKind.True &&
                String(connection, "Name") is { Length: > 0 } name)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>The address of a connection in <c>podman system connection list --format json</c>, or null.</summary>
    public static string? ConnectionUri(string json, string name)
    {
        using var document = Parse(json);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var connection in document.RootElement.EnumerateArray())
        {
            if (connection.ValueKind == JsonValueKind.Object && String(connection, "Name") == name)
            {
                return String(connection, "URI");
            }
        }

        return null;
    }

    /// <summary>
    /// The Docker endpoint of a context in <c>docker context inspect</c>: its address, the folder of its
    /// TLS files (null without any), and whether it skips verifying the server. Null when not readable.
    /// </summary>
    public static (string Host, string? TlsFolder, bool SkipTlsVerify)? ContextEndpoint(string json)
    {
        using var document = Parse(json);
        if (document is null ||
            document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() == 0 ||
            document.RootElement[0].ValueKind != JsonValueKind.Object ||
            !document.RootElement[0].TryGetProperty("Endpoints", out var endpoints) ||
            endpoints.ValueKind != JsonValueKind.Object ||
            !endpoints.TryGetProperty("docker", out var docker) ||
            docker.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (String(docker, "Host") is not { Length: > 0 } host)
        {
            return null;
        }

        var context = document.RootElement[0];
        var hasTls = context.TryGetProperty("TLSMaterial", out var material) &&
                     material.ValueKind == JsonValueKind.Object &&
                     material.TryGetProperty("docker", out var files) &&
                     files.ValueKind == JsonValueKind.Array &&
                     files.GetArrayLength() > 0;
        var tlsFolder = hasTls && String(context, "Storage", "TLSPath") is { Length: > 0 } tlsPath ? Path.Combine(tlsPath, "docker") : null;
        var skipVerify = docker.TryGetProperty("SkipTLSVerify", out var skip) && skip.ValueKind == JsonValueKind.True;
        return (host, tlsFolder, skipVerify);
    }

    /// <summary>The SSH key of a connection in <c>podman system connection list --format json</c>, or null.</summary>
    public static string? ConnectionIdentity(string json, string name)
    {
        using var document = Parse(json);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var connection in document.RootElement.EnumerateArray())
        {
            if (connection.ValueKind == JsonValueKind.Object && String(connection, "Name") == name)
            {
                return String(connection, "Identity") is { Length: > 0 } identity ? identity : null;
            }
        }

        return null;
    }

    /// <summary>The value of one variable in a container's <c>Config.Env</c>, or null.</summary>
    public static string? EnvironmentVariable(string json, string name)
    {
        using var document = Parse(json);
        if (First(document) is not { } container ||
            !container.TryGetProperty("Config", out var config) ||
            !config.TryGetProperty("Env", out var environment) ||
            environment.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var prefix = name + "=";
        foreach (var entry in environment.EnumerateArray())
        {
            if (entry.GetString() is { } text && text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return text[prefix.Length..];
            }
        }

        return null;
    }

    public static ContainerInfo? Container(string json)
    {
        using var document = Parse(json);
        var item = First(document);
        if (item is not { } container)
        {
            return null;
        }

        int? exitCode = container.TryGetProperty("State", out var state) &&
            state.TryGetProperty("ExitCode", out var code) &&
            code.TryGetInt32(out var value)
                ? value
                : null;
        var running = state.ValueKind == JsonValueKind.Object &&
            state.TryGetProperty("Running", out var runningElement) &&
            runningElement.ValueKind == JsonValueKind.True;
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (container.TryGetProperty("Config", out var config) &&
            config.TryGetProperty("Labels", out var labelsElement) &&
            labelsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var label in labelsElement.EnumerateObject())
            {
                labels[label.Name] = label.Value.GetString() ?? string.Empty;
            }
        }

        return new ContainerInfo(
            String(container, "Id") ?? string.Empty,
            String(container, "Config", "Image") ?? string.Empty,
            running,
            exitCode,
            labels,
            // The image ID it runs (docker and podman both put it here), which a moved tag no longer names.
            String(container, "Image"));
    }

    public static ImageInfo? Image(string json)
    {
        using var document = Parse(json);
        var item = First(document);
        if (item is not { } image)
        {
            return null;
        }

        string? digest = null;
        if (image.TryGetProperty("RepoDigests", out var digests) && digests.ValueKind == JsonValueKind.Array)
        {
            digest = digests.EnumerateArray().Select(element => element.GetString()).FirstOrDefault(value => !string.IsNullOrEmpty(value));
        }

        return new ImageInfo(String(image, "Id") ?? string.Empty, digest);
    }

    private static JsonDocument? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? First(JsonDocument? document)
    {
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.GetArrayLength() > 0 ? root[0].Clone() : null;
        }

        return root.ValueKind == JsonValueKind.Object ? root.Clone() : null;
    }

    private static string? String(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
