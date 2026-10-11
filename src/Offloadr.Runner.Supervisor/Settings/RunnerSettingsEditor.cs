using Offloadr.Runner.Supervisor.LaunchPlans;

namespace Offloadr.Runner.Supervisor.Settings;

/// <summary>The settings as a form shows and edits them, before they are checked and saved.</summary>
public sealed record SettingsForm(
    string? RunnerKey,
    string? SelectedEditorTemplate,
    string? LocalModelsDirectory,
    string? OffloadrApiOrigin,
    string? ContainerEngineExecutable,
    bool AutoStart);

/// <param name="Saved">False when the settings could not be written; <paramref name="Error"/> says why.</param>
/// <param name="Problems">What is still wrong with saved settings; the runner does not start until it is fixed.</param>
public sealed record SettingsSaveResult(bool Saved, IReadOnlyList<string> Problems, string? Error = null);

/// <summary>
/// Turns what a settings form holds into saved settings, so every host edits them the same way: the
/// defaults a blank field stands for, the save itself and the problems left to fix.
/// </summary>
public sealed class RunnerSettingsEditor(IRunnerSettingsStore store)
{
    /// <summary>The saved settings as the form starts with them.</summary>
    public SettingsForm Load()
    {
        var settings = store.Current;
        return new SettingsForm(
            settings.RunnerKey,
            settings.SelectedEditorTemplate ?? BuiltInLaunchPlanCatalog.Templates[0].EditorTemplate,
            settings.LocalModelsDirectory,
            settings.OffloadrApiOrigin,
            settings.ContainerEngineExecutable,
            settings.AutoStart);
    }

    /// <summary>Saves the form. Settings with problems are saved too, so nothing typed is lost; the problems are returned.</summary>
    public async Task<SettingsSaveResult> SaveAsync(SettingsForm form, CancellationToken cancellationToken = default)
    {
        var settings = new RunnerSettings
        {
            RunnerKey = form.RunnerKey,
            SelectedEditorTemplate = form.SelectedEditorTemplate,
            LocalModelsDirectory = form.LocalModelsDirectory,
            OffloadrApiOrigin = string.IsNullOrWhiteSpace(form.OffloadrApiOrigin) ? RunnerSettings.DefaultOffloadrApiOrigin : form.OffloadrApiOrigin,
            ContainerEngineExecutable = form.ContainerEngineExecutable,
            AutoStart = form.AutoStart,
        };

        try
        {
            await store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsSaveResult(false, settings.Validate(), $"Could not save the settings: {ex.Message}");
        }

        return new SettingsSaveResult(true, store.Current.Validate());
    }
}
