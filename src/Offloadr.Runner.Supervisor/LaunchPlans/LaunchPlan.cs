using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor.LaunchPlans;

/// <summary>One editor template and the runner image that serves it.</summary>
public sealed record LaunchTemplate(string EditorTemplate, string RuntimeKind, string Image, string Label);

public enum LaunchPlanSource
{
    /// <summary>The control plane answered with the owner's templates and selection.</summary>
    ControlPlane,

    /// <summary>The control plane does not offer launch plans (yet); the built-in catalog is used.</summary>
    BuiltIn,

    /// <summary>
    /// The control plane could not be reached; the built-in catalog stands in. A running runner keeps
    /// the plan it was started with rather than switching to this one.
    /// </summary>
    Unreachable,
}

/// <summary>Which images exist and which one this runner should run.</summary>
public sealed record LaunchPlan(
    IReadOnlyList<LaunchTemplate> Templates,
    string? SelectedEditorTemplate,
    TimeSpan RefreshInterval,
    LaunchPlanSource Source,
    string? Notice = null)
{
    /// <summary>
    /// The template to run: the control plane's selection, then the person's choice in settings,
    /// then the first template.
    /// </summary>
    public LaunchTemplate? Choose(RunnerSettings settings)
        => Find(SelectedEditorTemplate) ?? Find(settings.SelectedEditorTemplate) ?? Templates.FirstOrDefault();

    public LaunchTemplate? Find(string? editorTemplate)
        => string.IsNullOrWhiteSpace(editorTemplate)
            ? null
            : Templates.FirstOrDefault(template => string.Equals(template.EditorTemplate, editorTemplate.Trim(), StringComparison.OrdinalIgnoreCase));
}

public interface ILaunchPlanProvider
{
    Task<LaunchPlan> GetAsync(RunnerSettings settings, CancellationToken cancellationToken);
}

/// <summary>The published runner images. Template ids match what the images advertise when they register.</summary>
public sealed class BuiltInLaunchPlanCatalog : ILaunchPlanProvider
{
    public static readonly IReadOnlyList<LaunchTemplate> Templates =
    [
        new("comfyui-latest", "comfyui", "ghcr.io/offloadr/runner-comfyui:latest", "ComfyUI - current release"),
        new("comfyui-master", "comfyui", "ghcr.io/offloadr/runner-comfyui:master", "ComfyUI - master branch"),
        new("forge-neo:latest", "forge-neo", "ghcr.io/offloadr/runner-forge-neo:latest", "Forge Neo - current release"),
        new("forge-neo:neo", "forge-neo", "ghcr.io/offloadr/runner-forge-neo:neo", "Forge Neo - neo branch"),
    ];

    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    public static LaunchPlan Plan(string? notice = null, LaunchPlanSource source = LaunchPlanSource.BuiltIn)
        => new(Templates, null, RefreshInterval, source, notice);

    public Task<LaunchPlan> GetAsync(RunnerSettings settings, CancellationToken cancellationToken)
        => Task.FromResult(Plan());
}
