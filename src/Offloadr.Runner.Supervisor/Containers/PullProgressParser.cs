using System.Text.RegularExpressions;

namespace Offloadr.Runner.Supervisor.Containers;

/// <summary>
/// How far an image download is, in layers. Without a terminal the engines print no byte counts, only
/// a line per layer and step, so layers are what can be counted.
/// </summary>
/// <param name="Measured">
/// False while the engine has only announced layers: Podman without a terminal may never say when
/// one is done, and a bar that stays at zero would be worse than none.
/// </param>
public sealed record PullProgress(int Layers, int Downloaded, int Extracted, bool Measured = true)
{
    /// <summary>Downloading and unpacking each count for half; null until it can be measured.</summary>
    public double? Fraction => Layers == 0 || !Measured ? null : (Downloaded + Extracted) / (2.0 * Layers);

    public string Summary => Layers == 0
        ? "Looking up the image…"
        : !Measured
            ? $"Downloading {Layers} layers…"
            : Downloaded < Layers
                ? $"Downloaded {Downloaded} of {Layers} layers"
                : $"Unpacking: {Extracted} of {Layers} layers done";
}

/// <summary>
/// Follows the lines <c>docker pull</c> or <c>podman pull</c> prints without a terminal:
/// <c>f18232174bc9: Pulling fs layer</c> … <c>Download complete</c> … <c>Pull complete</c> from Docker,
/// <c>Copying blob sha256:…</c> from Podman, which unpacks as it copies.
/// </summary>
public sealed partial class PullProgressParser
{
    private readonly Dictionary<string, LayerStep> _layers = new(StringComparer.Ordinal);
    private bool _measured;

    public PullProgress Current { get; private set; } = new(0, 0, 0);

    /// <summary>Reads one line; true when the progress moved.</summary>
    public bool Accept(string line)
    {
        if (DockerLayer().Match(line) is { Success: true } docker)
        {
            var status = docker.Groups["status"].Value;
            var step = status switch
            {
                _ when status.StartsWith("Pull complete", StringComparison.Ordinal) ||
                       status.StartsWith("Already exists", StringComparison.Ordinal) => LayerStep.Extracted,
                _ when status.StartsWith("Download complete", StringComparison.Ordinal) ||
                       status.StartsWith("Extracting", StringComparison.Ordinal) => LayerStep.Downloaded,
                _ when status.StartsWith("Pulling fs layer", StringComparison.Ordinal) ||
                       status.StartsWith("Waiting", StringComparison.Ordinal) ||
                       status.StartsWith("Downloading", StringComparison.Ordinal) ||
                       status.StartsWith("Verifying Checksum", StringComparison.Ordinal) ||
                       status.StartsWith("Retrying", StringComparison.Ordinal) => LayerStep.Known,
                _ => (LayerStep?)null,
            };
            // Docker reports every step of every layer, so its counts are a measure from the start.
            _measured = true;
            return step is { } known && Advance(docker.Groups["id"].Value, known);
        }

        if (PodmanBlob().Match(line) is { Success: true } podman)
        {
            var id = podman.Groups["id"].Value[..12];
            var rest = podman.Groups["rest"].Value;
            var done = rest.Contains("done", StringComparison.Ordinal) || rest.Contains("skipped", StringComparison.Ordinal);
            _measured |= done;
            return Advance(id, done ? LayerStep.Extracted : LayerStep.Known);
        }

        if (line.StartsWith("Writing manifest to image destination", StringComparison.Ordinal) && _layers.Count > 0)
        {
            // Podman has copied every blob, whether or not it said so per blob.
            _measured = true;
            foreach (var id in _layers.Keys.ToArray())
            {
                _layers[id] = LayerStep.Extracted;
            }

            return Publish();
        }

        return false;
    }

    private bool Advance(string id, LayerStep step)
    {
        // Steps only move forward: lines for one layer can arrive out of order across layers.
        if (_layers.TryGetValue(id, out var previous) && previous >= step)
        {
            return false;
        }

        _layers[id] = step;
        return Publish();
    }

    private bool Publish()
    {
        var next = new PullProgress(
            _layers.Count,
            _layers.Values.Count(value => value >= LayerStep.Downloaded),
            _layers.Values.Count(value => value == LayerStep.Extracted),
            _measured);
        if (next == Current)
        {
            return false;
        }

        Current = next;
        return true;
    }

    private enum LayerStep
    {
        Known,
        Downloaded,
        Extracted,
    }

    [GeneratedRegex("^(?<id>[0-9a-f]{12}): (?<status>.+)$")]
    private static partial Regex DockerLayer();

    [GeneratedRegex("^Copying blob (?:sha256:)?(?<id>[0-9a-f]{12,64})(?<rest>.*)$")]
    private static partial Regex PodmanBlob();
}
