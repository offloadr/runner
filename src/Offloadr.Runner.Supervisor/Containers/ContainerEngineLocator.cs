namespace Offloadr.Runner.Supervisor.Containers;

public sealed record LocatedEngine(string Executable, ContainerEngineKind Kind);

/// <summary>
/// Finds the docker or podman executable: the configured path first, then PATH, then the
/// places Docker Desktop, Podman and Linux packages install it (an app opened from the desktop
/// often does not get the PATH a terminal has). The result is always an absolute path, since it is
/// remembered across a restart that may run in another working directory.
/// </summary>
public static class ContainerEngineLocator
{
    public static LocatedEngine? Locate(string? configured)
        => Locate(
            configured,
            Environment.GetEnvironmentVariable("PATH"),
            OperatingSystem.IsWindows(),
            File.Exists,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static LocatedEngine? Locate(
        string? configured,
        string? pathVariable,
        bool isWindows,
        Func<string, bool> fileExists,
        string? programFiles,
        string? home = null)
    {
        var searchPath = (pathVariable ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var extension = isWindows ? ".exe" : string.Empty;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var candidate = configured.Trim();
            if (fileExists(candidate))
            {
                return Found(candidate);
            }

            // A bare name such as "podman" is looked up on PATH.
            if (candidate.IndexOfAny(['/', '\\']) < 0)
            {
                var name = isWindows && !candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? candidate + ".exe" : candidate;
                var onPath = searchPath.Select(directory => Path.Combine(directory, name)).FirstOrDefault(fileExists);
                if (onPath is not null)
                {
                    return Found(onPath);
                }
            }

            return null;
        }

        foreach (var name in new[] { "docker" + extension, "podman" + extension })
        {
            var onPath = searchPath.Select(directory => Path.Combine(directory, name)).FirstOrDefault(fileExists);
            if (onPath is not null)
            {
                return Found(onPath);
            }
        }

        string[] wellKnown = isWindows
            ? [Path.Combine(programFiles ?? @"C:\Program Files", "Docker", "Docker", "resources", "bin", "docker.exe")]
            :
            [
                "/usr/bin/docker",
                "/usr/local/bin/docker",
                // Docker Desktop for macOS: its per-user CLI folder, and the copy inside the app.
                .. home is null ? Array.Empty<string>() : [Path.Combine(home, ".docker", "bin", "docker")],
                "/Applications/Docker.app/Contents/Resources/bin/docker",
                "/opt/homebrew/bin/docker",
                "/usr/bin/podman",
                "/usr/local/bin/podman",
                // Podman's macOS installer, and Homebrew on Apple silicon.
                "/opt/podman/bin/podman",
                "/opt/homebrew/bin/podman",
            ];
        var found = wellKnown.FirstOrDefault(fileExists);
        return found is null ? null : new LocatedEngine(found, KindOf(found));
    }

    /// <summary>A relative path (configured, or a relative PATH entry) is made absolute against the current folder.</summary>
    private static LocatedEngine Found(string executable)
    {
        var absolute = Path.IsPathRooted(executable) ? executable : Path.GetFullPath(executable);
        return new LocatedEngine(absolute, KindOf(absolute));
    }

    private static ContainerEngineKind KindOf(string executable)
        => Path.GetFileNameWithoutExtension(executable).Contains("podman", StringComparison.OrdinalIgnoreCase)
            ? ContainerEngineKind.Podman
            : ContainerEngineKind.Docker;
}
