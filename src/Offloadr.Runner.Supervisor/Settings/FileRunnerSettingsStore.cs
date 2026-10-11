using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Offloadr.Runner.Supervisor.Settings;

/// <summary>Encrypts secrets at rest. Hosts supply an implementation (for example ASP.NET Core Data Protection).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}

public interface IRunnerSettingsStore
{
    RunnerSettings Current { get; }

    event Action<RunnerSettings>? Changed;

    Task SaveAsync(RunnerSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>
/// Settings in one JSON file, written with an atomic replace. The runner key is stored only in
/// protected form; if it can no longer be decrypted (for example after the key ring was lost) the
/// store loads without a key and the person enters it again.
/// </summary>
public sealed class FileRunnerSettingsStore : IRunnerSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private RunnerSettings _current;

    public FileRunnerSettingsStore(SupervisorPaths paths, ISecretProtector protector, ILogger<FileRunnerSettingsStore> logger)
    {
        _path = paths.SettingsFile;
        _protector = protector;
        _current = Load(logger);
    }

    public RunnerSettings Current => Volatile.Read(ref _current);

    public event Action<RunnerSettings>? Changed;

    public async Task SaveAsync(RunnerSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = Normalize(settings);
        var document = new SettingsDocument
        {
            OffloadrApiOrigin = normalized.OffloadrApiOrigin,
            ProtectedRunnerKey = normalized.HasRunnerKey ? _protector.Protect(normalized.RunnerKey!) : null,
            LocalModelsDirectory = normalized.LocalModelsDirectory,
            ContainerEngineExecutable = normalized.ContainerEngineExecutable,
            SelectedEditorTemplate = normalized.SelectedEditorTemplate,
            AutoStart = normalized.AutoStart,
        };

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, SerializerOptions), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
            Volatile.Write(ref _current, normalized);
        }
        finally
        {
            _writeLock.Release();
        }

        Changed?.Invoke(normalized);
    }

    private RunnerSettings Load(ILogger logger)
    {
        if (!File.Exists(_path))
        {
            return new RunnerSettings();
        }

        SettingsDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(_path), SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read runner settings from {Path}; using defaults.", _path);
            return new RunnerSettings();
        }

        if (document is null)
        {
            return new RunnerSettings();
        }

        string? runnerKey = null;
        if (!string.IsNullOrEmpty(document.ProtectedRunnerKey))
        {
            try
            {
                runnerKey = _protector.Unprotect(document.ProtectedRunnerKey);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The saved runner key could not be decrypted; it must be entered again.");
            }
        }

        return Normalize(new RunnerSettings
        {
            OffloadrApiOrigin = document.OffloadrApiOrigin ?? RunnerSettings.DefaultOffloadrApiOrigin,
            RunnerKey = runnerKey,
            LocalModelsDirectory = document.LocalModelsDirectory,
            ContainerEngineExecutable = document.ContainerEngineExecutable,
            SelectedEditorTemplate = document.SelectedEditorTemplate,
            AutoStart = document.AutoStart,
        });
    }

    private static RunnerSettings Normalize(RunnerSettings settings) => settings with
    {
        OffloadrApiOrigin = string.IsNullOrWhiteSpace(settings.OffloadrApiOrigin)
            ? RunnerSettings.DefaultOffloadrApiOrigin
            : settings.OffloadrApiOrigin.Trim().TrimEnd('/'),
        RunnerKey = Blank(settings.RunnerKey),
        LocalModelsDirectory = Blank(settings.LocalModelsDirectory),
        ContainerEngineExecutable = Blank(settings.ContainerEngineExecutable),
        SelectedEditorTemplate = Blank(settings.SelectedEditorTemplate),
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class SettingsDocument
    {
        public string? OffloadrApiOrigin { get; set; }

        public string? ProtectedRunnerKey { get; set; }

        public string? LocalModelsDirectory { get; set; }

        public string? ContainerEngineExecutable { get; set; }

        public string? SelectedEditorTemplate { get; set; }

        public bool AutoStart { get; set; }
    }
}
