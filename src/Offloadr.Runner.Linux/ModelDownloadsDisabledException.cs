namespace Offloadr.Runner.Linux;

/// <summary>
/// A prompt needs models the runner does not have while <c>RUNNER_MODEL_DOWNLOADS</c> is off. The prompt is
/// refused before the editor sees it; its message names the models, which the control plane sent.
/// </summary>
internal sealed class ModelDownloadsDisabledException(IReadOnlyCollection<string> filenames)
    : InvalidOperationException(Describe(filenames))
{
    public const string DiagnosticCode = "model_downloads_disabled";

    /// <summary>What each refused download is reported with.</summary>
    public const string ReportMessage =
        "Model downloads are turned off on this runner. Turn them on in its settings, or put the model in its local models folder.";

    public IReadOnlyCollection<string> Filenames { get; } = filenames;

    private static string Describe(IReadOnlyCollection<string> filenames)
    {
        var named = filenames.Where(static name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToArray();
        var list = named.Length == 0 ? string.Empty : $": {string.Join(", ", named)}";
        return named.Length == 1
            ? $"This prompt needs a model this runner does not have, and model downloads are turned off on it{list}."
            : $"This prompt needs models this runner does not have, and model downloads are turned off on it{list}.";
    }
}
