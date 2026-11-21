namespace Offloadr.Runner.Linux;

/// <summary>
/// A prompt needs models the runner does not have while <c>RUNNER_MODEL_DOWNLOADS</c> is off. The prompt is
/// refused before the editor sees it; its message names the models, which the control plane sent.
/// </summary>
internal sealed class ModelDownloadsDisabledException(IReadOnlyCollection<string> filenames)
    : InvalidOperationException(Describe(filenames))
{
    public const string DiagnosticCode = "model_downloads_disabled";

    public IReadOnlyCollection<string> Filenames { get; } = filenames;

    /// <summary>The models named for the runner's log, at most ten.</summary>
    internal static string NameForLog(IEnumerable<string?> filenames)
    {
        var named = filenames.Where(static name => !string.IsNullOrWhiteSpace(name)).Select(static name => name!.Trim())
            .Distinct(StringComparer.Ordinal).ToArray();
        var shown = string.Join(", ", named.Take(10));
        return named.Length > 10 ? $"{shown} and {named.Length - 10} more" : shown;
    }

    private static string Describe(IReadOnlyCollection<string> filenames)
    {
        var named = filenames.Where(static name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToArray();
        var list = named.Length == 0 ? string.Empty : $": {string.Join(", ", named)}";
        return named.Length == 1
            ? $"This prompt needs a model this runner does not have, and model downloads are turned off on it{list}."
            : $"This prompt needs models this runner does not have, and model downloads are turned off on it{list}.";
    }
}
