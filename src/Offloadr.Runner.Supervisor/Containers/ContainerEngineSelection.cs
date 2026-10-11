using System.Text.Json;

namespace Offloadr.Runner.Supervisor.Containers;

/// <summary>
/// The container engine executable that engine commands use. The supervisor changes it only while
/// it runs no container, so a running container is always inspected, followed and stopped through
/// the engine that started it, even after Settings name another one or discovery would now find
/// another one on PATH.
/// </summary>
/// <param name="locate">Resolves a Settings value to an executable; defaults to <see cref="ContainerEngineLocator"/>.</param>
/// <param name="statePath">
/// Where the engine of a running container is remembered, so that after a crash the app looks for
/// its runner on that engine before Settings may have moved on to another one. Null keeps it in memory.
/// </param>
public sealed class ContainerEngineSelection(Func<string?, LocatedEngine?>? locate = null, string? statePath = null)
{
    private readonly Func<string?, LocatedEngine?> _locate = locate ?? ContainerEngineLocator.Locate;
    private readonly AsyncLocal<Selected?> _scoped = new();
    private Selected _selected = new(null, null);

    /// <summary>
    /// The executable engine commands run, resolved when it was selected; null to discover it. Work
    /// running under <see cref="Use"/> sees that engine instead.
    /// </summary>
    public string? Executable => _scoped.Value is { } scoped ? scoped.Executable : Volatile.Read(ref _selected).Executable;

    /// <summary>The Docker CLI context or Podman connection commands are pinned to; null for the current one.</summary>
    public ContainerEnginePin? Pin => _scoped.Value is { } scoped ? scoped.Context : Volatile.Read(ref _selected).Context;

    /// <summary>The context, connection or endpoint commands are pinned to; null for the current one.</summary>
    public string? Context => Pin?.Label;

    /// <summary>Pins commands to the Docker CLI context or Podman connection the runner is started or found in.</summary>
    public void PinContext(ContainerEnginePin? pin)
    {
        var selected = Volatile.Read(ref _selected);
        var normalized = pin switch
        {
            { Name: { } name } when !string.IsNullOrWhiteSpace(name) => pin with { Name = name.Trim() },
            { Name: null, Endpoint.Count: > 0 } => pin,
            _ => null,
        };
        Volatile.Write(ref _selected, selected with { Context = normalized });
    }

    /// <summary>
    /// Selects the engine Settings name (blank: discover one) and resolves it now, so later commands
    /// keep using the same executable.
    /// </summary>
    public void Select(string? configured) => Volatile.Write(ref _selected, Resolve(configured));

    /// <summary>True when <paramref name="configured"/> is the Settings value the engine was selected from.</summary>
    public bool Matches(string? configured)
        => string.Equals(Volatile.Read(ref _selected).Configured, Normalize(configured), StringComparison.Ordinal);

    /// <summary>
    /// The ID of the runner container on the remembered engine, saved with the hint once known, so
    /// that after a crash the runner is found even when it was renamed; null when unknown.
    /// </summary>
    public string? RememberedContainerId { get; private set; }

    /// <summary>
    /// Remembers the selected engine as the one a runner container now runs on. False, with the
    /// reason, when it could not be saved; the previous hint, if any, is then still in place.
    /// </summary>
    public bool Remember(out string? error) => Remember(null, out error);

    /// <summary>Remembers the selected engine and the ID of the runner container on it (see <see cref="Remember(out string?)"/>).</summary>
    public bool Remember(string? containerId, out string? error)
    {
        error = null;
        if (statePath is null)
        {
            RememberedContainerId = containerId;
            return true;
        }

        // Written next to the hint and renamed over it, so a crash leaves either the old hint or the
        // new one, never a torn file that would read as no hint. A scanner briefly holding either
        // file (common on Windows right after a write) only delays it.
        // A password in an engine address stays in memory only; the hint is plain JSON.
        var selected = Volatile.Read(ref _selected);
        var json = JsonSerializer.Serialize(new Hint(selected with { Context = selected.Context?.WithoutPasswords() }, containerId));
        var temporary = statePath + ".tmp";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
                File.WriteAllText(temporary, json);
                File.Move(temporary, statePath, overwrite: true);
                RememberedContainerId = containerId;
                return true;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(temporary);
                error = $"Could not save which container engine runs the runner to '{statePath}': {ex.Message}";
                return false;
            }
        }
    }

    /// <summary>Remembers the selected engine, best effort (see <see cref="Remember(out string?)"/>).</summary>
    public bool Remember() => Remember(out _);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a leftover temporary file.
        }
    }

    /// <summary>Forgets the remembered engine once no runner container runs on it.</summary>
    public void Forget()
    {
        if (statePath is null)
        {
            return;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(statePath);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                // A scanner may briefly hold the file right after it was written.
                Thread.Sleep(50);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a hint left behind only makes the next start look on that engine first.
                return;
            }
        }
    }

    /// <summary>
    /// Selects the remembered engine, if one is remembered; false otherwise. It is selected even when
    /// its executable is gone: a daemon or remote engine keeps running containers without its CLI,
    /// so only an engine that answers can show the runner is gone.
    /// </summary>
    public bool TryRestore()
    {
        if (statePath is null)
        {
            return false;
        }

        try
        {
            if (!File.Exists(statePath) || JsonSerializer.Deserialize<Hint>(File.ReadAllText(statePath)) is not { Engine: { } remembered } hint)
            {
                return false;
            }

            Volatile.Write(ref _selected, remembered with { Configured = Normalize(remembered.Configured) });
            RememberedContainerId = hint.ContainerId;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static string? Normalize(string? configured) => string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();

    /// <summary>The selected engine (not one a scope uses).</summary>
    internal Selected Current => Volatile.Read(ref _selected);

    /// <summary>Selects exactly this engine (one a switch prepared), context included.</summary>
    internal void SelectExactly(Selected engine) => Volatile.Write(ref _selected, engine);

    /// <summary>Resolves the engine Settings name without selecting it.</summary>
    internal Selected Resolve(string? configured)
    {
        var normalized = Normalize(configured);
        return new Selected(normalized, _locate(normalized)?.Executable ?? normalized);
    }

    /// <summary>
    /// Runs the calling async flow (and work it starts) against <paramref name="engine"/>, for example
    /// to prepare another engine, while every other flow keeps using the selected one.
    /// </summary>
    internal IDisposable Use(Selected engine)
    {
        var previous = _scoped.Value;
        _scoped.Value = engine;
        return new Scope(this, previous);
    }

    private sealed class Scope(ContainerEngineSelection owner, Selected? previous) : IDisposable
    {
        public void Dispose() => owner._scoped.Value = previous;
    }

    /// <param name="Context">
    /// Where the runner was started: its Docker CLI context, Podman connection, or the endpoint the
    /// environment chose (DOCKER_HOST, CONTAINER_HOST). Every command goes there, so `docker context
    /// use` elsewhere, or a restart of the app with another environment, cannot point commands at
    /// another daemon. Null: wherever the CLI goes now.
    /// </param>
    internal sealed record Selected(string? Configured, string? Executable, ContainerEnginePin? Context = null);

    /// <summary>What is saved: the engine, and the ID of the runner container on it once known.</summary>
    private sealed record Hint(Selected Engine, string? ContainerId);
}
