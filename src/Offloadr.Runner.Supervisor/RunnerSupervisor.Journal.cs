using Microsoft.Extensions.Logging;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Launcher;
using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor;

/// <content>
/// What is owed, kept on disk so a crash forgets none of it: containers Offloadr may still count on,
/// stopped containers an engine kept, a release asked or granted, and takeovers. Read and written on
/// the loop; the jobs that work it off are given copies and bring back what they found.
/// </content>
public sealed partial class RunnerSupervisor
{
    private readonly string? _releaseMarkerPath;
    private readonly string? _takeoverMarkerPath;
    private readonly string? _launchedPath;
    private readonly string? _credentialsPath;
    private readonly ISecretProtector? _protector;
    private readonly string? _leftoversPath;

    /// <summary>The Offloadr addresses and keys (as <see cref="TakeoverTag"/>) another launcher took the runner over for.</summary>
    private readonly HashSet<string> _takenOver = new(StringComparer.Ordinal);

    /// <summary>
    /// The containers this app started that Offloadr may still count on, by launch (saved in
    /// <c>launched-containers</c>): each until Offloadr knows it is gone, by a granted release or an
    /// answered <c>container_exited</c> report.
    /// </summary>
    private readonly Dictionary<string, LaunchedContainer> _launched = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="Gone">It no longer runs and Offloadr has not been told yet.</param>
    /// <param name="Engine">
    /// The engine and context it was started on, as <see cref="ContainerEngineSelection.Describe"/> saves it
    /// (no password): only that engine answering without it proves it gone, and it is looked for there
    /// whichever engine runs the runner now. Null when unknown, and then only its own exit does.
    /// </param>
    /// <param name="Pending">
    /// Its creation was asked for, and its start has not been (saved as <c>starting</c>): whatever became
    /// of it, it ran nothing, and no exit is reported for it. Cleared right before the engine is asked to
    /// start it (saved as <c>running</c>): from then on a container that is not there is gone, which
    /// Offloadr is told, since nothing shows it never ran.
    /// </param>
    /// <param name="Refused">
    /// The engine refused its start, and what became of the container was not seen yet. Seen created, it
    /// never ran; no longer there to say, it is gone, as any other whose start was asked for.
    /// </param>
    /// <param name="Release">
    /// Whether its release was asked, or granted, as it was about to be stopped beside the supervised one
    /// (or by an uninstall): a granted one is never reused, whatever keeps it from being stopped then.
    /// The supervised container's own release is noted in <c>released-launch</c>.
    /// </param>
    /// <param name="SeenCreated">
    /// The engine showed it created, by a look that went on to remove it: gone afterwards, that is what it was
    /// (if its start was refused).
    /// </param>
    internal sealed record LaunchedContainer(string Target, bool Gone, string? Executor, string? Engine = null, bool Pending = false, bool Refused = false, ReleaseNote Release = ReleaseNote.None, bool SeenCreated = false)
    {
        /// <summary>Seen created, it ran nothing: no start of it was asked for, or the engine refused the one that was.</summary>
        public bool RanNothingIfCreated => Pending || Refused;
    }

    /// <summary>
    /// A stopped runner container the engine would not remove, on the engine it ran on: it still holds
    /// the runner key, so its removal is retried there (saved in <c>leftover-containers</c>).
    /// </summary>
    private readonly List<(ContainerEngineSelection.Selected Engine, string Container)> _leftovers = [];

    /// <summary>Offloadr's release of this launch was granted; its container is stopped, never reused.</summary>
    private string? _releasedLaunch;

    /// <summary>A release of this launch was asked and no answer seen (the app may have closed meanwhile).</summary>
    private string? _releaseAsked;

    /// <summary>
    /// Launches whose container was let go of unseen: its creation was cancelled or given up, or the app
    /// closed before the engine answered (it may still create it either way). Each with how long it is
    /// still looked for, by <see cref="Now"/>. Whatever shows by then was never started by this app, so
    /// it is inert: it is removed, and nothing is reported for it. Not found by then, it was never created.
    /// </summary>
    private readonly Dictionary<string, TimeSpan> _unseen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A container of this app found running by its launch, on its engine, while none is supervised: to be supervised.</summary>
    private (ContainerInfo Container, ContainerEngineSelection.Selected Engine)? _foundRunning;

    /// <summary>
    /// Containers of this app found running by their launch while another is supervised (one left on an
    /// engine the person moved on from, which answers again): by launch, as last seen. One runner is
    /// supervised at a time, so these only stay known: a Stop, or the app closing, stops them too, and
    /// one is taken under supervision once nothing else is.
    /// </summary>
    private readonly Dictionary<string, (ContainerInfo Container, ContainerEngineSelection.Selected Engine)> _strays = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The launches whose container started and then exited by itself (not stopped by the person): told to
    /// Offloadr as an exit. Such a launch never fails for it, whenever Offloadr names it again.
    /// </summary>
    private readonly HashSet<string> _exitedLaunches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The launches a container was started for, so none starts twice.</summary>
    private readonly HashSet<string> _dispatched = new(StringComparer.OrdinalIgnoreCase);

    internal enum ReleaseNote
    {
        None,

        /// <summary>Asked, and not known to be granted: it may be.</summary>
        Asked,

        Granted,
    }

    private readonly Slot _goneSlot = new("gone reports");
    private readonly Slot _maintenanceSlot = new("maintenance");

    /// <summary>
    /// The launches whose containers the maintenance job under way looks for, by launch alone. It removes
    /// what it finds stopped: so it is ended before one of these launches creates a container again,
    /// which it would take for the earlier attempt's.
    /// </summary>
    private HashSet<string> _maintenanceLooksFor = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When gone containers were last reported, and when the engines were last asked about the rest, by <see cref="Now"/>.</summary>
    private TimeSpan? _goneReportedAt;
    private TimeSpan? _maintainedAt;

    /// <summary>Something to report was noted, or the launcher came online, since the last report went out.</summary>
    private bool _goneReportDue;

    private bool HasLaunchRecords => _launched.Count > 0;

    private bool HasGoneToReport => _launched.Values.Any(container => container.Gone);

    private bool HasLeftovers => _leftovers.Count > 0;

    /// <summary>The leftovers said to run again: said once for each.</summary>
    private readonly HashSet<string> _leftoversRunning = new(StringComparer.Ordinal);

    private void LoadJournals()
    {
        ReadReleaseMarker();
        foreach (var line in (ReadMarker(_launchedPath) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // state, launch, target, engine ("-" when unknown), then the executor instance if known.
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4 && Guid.TryParse(parts[1], out _))
            {
                _launched[parts[1]] = new LaunchedContainer(parts[2], parts[0] == "gone", parts.Length > 4 ? parts[4] : null, parts[3] == "-" ? null : parts[3], parts[0] == "starting", parts[0] == "refused",
                    parts[0] switch { "asking" => ReleaseNote.Asked, "released" => ReleaseNote.Granted, _ => ReleaseNote.None });
            }
        }

        foreach (var line in (ReadMarker(_leftoversPath) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && ContainerEngineSelection.Read(parts[0]) is { } engine)
            {
                _leftovers.Add((engine, parts[1]));
            }
        }

        foreach (var line in (ReadMarker(_takeoverMarkerPath) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _takenOver.Add(line);
        }

        LoadStartedWith();
        _goneReportDue = HasGoneToReport;
    }

    // Containers this app started

    /// <summary>
    /// Notes, before its creation is asked for, that a container is made for <paramref name="launchId"/> on
    /// <paramref name="engine"/>. Required: after a crash it is how Offloadr learns the container is gone.
    /// </summary>
    private bool NoteLaunched(string launchId, string target, ContainerEngineSelection.Selected engine)
    {
        _launched[launchId] = new LaunchedContainer(target, Gone: false, Executor: null, ContainerEngineSelection.Describe(engine), Pending: true);
        if (SaveLaunched())
        {
            return true;
        }

        _launched.Remove(launchId);
        return false;
    }

    /// <summary>
    /// Notes, right before the engine is asked to start it, that the container of <paramref name="launchId"/>
    /// is started. Required: after a crash it is what tells a container that may have run (and is told
    /// to Offloadr when it is gone) from one that was only created. False when it could not be saved.
    /// </summary>
    private bool NoteStartAsked(string launchId)
    {
        if (!_launched.TryGetValue(launchId, out var known) || !known.Pending)
        {
            return true;
        }

        _launched[launchId] = known with { Pending = false };
        if (SaveLaunched())
        {
            return true;
        }

        _launched[launchId] = known;
        return false;
    }

    /// <summary>The start noted with <see cref="NoteStartAsked"/> was not asked for after all: the container is only created.</summary>
    private void NoteStartNotAsked(string launchId)
    {
        if (_launched.TryGetValue(launchId, out var known) && known is { Pending: false, Gone: false })
        {
            _launched[launchId] = known with { Pending = true };
            SaveLaunched();
        }
    }

    /// <summary>
    /// The engine refused the start noted with <see cref="NoteStartAsked"/>. Saved, so that after a crash
    /// right here the container found created is one that never ran, not one whose start may be under way.
    /// </summary>
    private void NoteStartRefused(string launchId)
    {
        if (_launched.TryGetValue(launchId, out var known) && known is { Pending: false, Gone: false, Refused: false })
        {
            _launched[launchId] = known with { Refused = true };
            SaveLaunched();
        }
    }

    /// <summary>Offloadr granted the release of <paramref name="launchId"/>'s container, as noted in the marker or with its record: it is stopped, and knows so.</summary>
    private bool IsReleased(string? launchId)
        => launchId is not null &&
           (string.Equals(launchId, _releasedLaunch, StringComparison.OrdinalIgnoreCase) || ReleaseNoted(launchId) == ReleaseNote.Granted);

    /// <summary>The procedure in progress looks for, or removes, containers this app started before.</summary>
    private bool ProcedureLooks
        => _main.Job is { } job && (job.Key is "launched" or "remembered" or "startup name" or "name" || job.Key.StartsWith("name ", StringComparison.Ordinal) || job.Key.StartsWith("stray stop ", StringComparison.Ordinal));

    /// <summary>Notes what was asked, or granted, of a release right before the container of <paramref name="launchId"/> is stopped.</summary>
    private void NoteRelease(string launchId, ReleaseNote release)
    {
        // (A grant is for good: asking again takes nothing of it back.)
        if (_launched.TryGetValue(launchId, out var known) && !known.Gone && known.Release != release && !(known.Release == ReleaseNote.Granted && release == ReleaseNote.Asked))
        {
            _launched[launchId] = known with { Release = release };
            SaveLaunched();
        }
    }

    /// <summary>What is noted with its record of the release of <paramref name="launchId"/>'s container.</summary>
    private ReleaseNote ReleaseNoted(string? launchId)
        => launchId is not null && _launched.TryGetValue(launchId, out var known) ? known.Release : ReleaseNote.None;

    /// <summary>The container started for <paramref name="launchId"/> was seen started: from now on its absence is an exit.</summary>
    private void ConfirmLaunched(string? launchId)
    {
        if (launchId is not null && _launched.TryGetValue(launchId, out var known) && known.RanNothingIfCreated)
        {
            _launched[launchId] = known with { Pending = false, Refused = false };
            SaveLaunched();
        }
    }

    /// <summary>The engine showed the container of <paramref name="launchId"/>, before it is removed or stopped.</summary>
    private void NoteSeen(string launchId, bool ran)
    {
        if (ran)
        {
            NoteRan(launchId);
            return;
        }

        if (_ownsContainer && string.Equals(launchId, _containerLaunchId, StringComparison.OrdinalIgnoreCase))
        {
            _seenCreated = true;
        }

        if (_launched.TryGetValue(launchId, out var known) && !known.SeenCreated)
        {
            // (Not saved: after a crash, a container that is not there is one that nothing is known of.)
            _launched[launchId] = known with { SeenCreated = true };
        }
    }

    /// <summary>The engine showed the container of <paramref name="launchId"/> as one that ran: it never counts as one that did not.</summary>
    private void NoteRan(string launchId)
    {
        ConfirmLaunched(launchId);
        if (_ownsContainer && string.Equals(launchId, _containerLaunchId, StringComparison.OrdinalIgnoreCase))
        {
            _seenRunning = true;
        }
    }

    /// <summary>Forgets a container Offloadr knows is gone (it released it), or that never existed.</summary>
    private void ForgetLaunched(string launchId)
    {
        if (_startedWith.Remove(launchId))
        {
            SaveStartedWith();
        }

        if (_launched.Remove(launchId))
        {
            SaveLaunched();
        }
    }

    /// <summary>
    /// The Offloadr address and key each launched container was started with, by launch, until Offloadr
    /// knows it is gone: only that Offloadr can be asked to release it or told that it is gone, also
    /// after Settings name another address or key, and after the container itself is no longer there to
    /// read them from. Saved encrypted (<c>launched-credentials</c>), like the key in the settings, when
    /// the host supplies the protection; kept in memory otherwise. Never logged.
    /// </summary>
    private readonly Dictionary<string, Hosting.RunnerCleanup.LaunchCredentials> _startedWith = new(StringComparer.OrdinalIgnoreCase);

    /// <returns>False when it could not be saved (it is kept in memory all the same).</returns>
    private bool RememberStartedWith(string? launchId, string? origin, string? key)
    {
        if (launchId is null || string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(key))
        {
            return true;
        }

        var credentials = new Hosting.RunnerCleanup.LaunchCredentials(origin.Trim(), key.Trim());
        if (_startedWith.TryGetValue(launchId, out var known) && known == credentials)
        {
            return true;
        }

        _startedWith[launchId] = credentials;
        return SaveStartedWith();
    }

    /// <returns>False when the host protects them and they could not be written.</returns>
    private bool SaveStartedWith()
    {
        if (_protector is null || _credentialsPath is null)
        {
            return true;
        }

        try
        {
            return WriteMarker(_credentialsPath, LaunchedCredentialsFile.Format(_startedWith, _protector));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Kept in memory all the same; without it on disk, a restart can report only for the settings of then.
            _logger.LogWarning("Could not save what the runner containers were started with: {Error}", ex.GetType().Name);
            return false;
        }
    }

    private void LoadStartedWith()
    {
        if (_protector is null)
        {
            return;
        }

        // Unreadable (the protection keys changed, say): reported with the settings of now, if they name it.
        var read = LaunchedCredentialsFile.Read(ReadMarker(_credentialsPath), _protector,
            ex => _logger.LogWarning("Could not read what a runner container was started with: {Error}", ex.GetType().Name));
        foreach (var entry in read)
        {
            // (One whose launch is settled is not kept: dropped at the next save.)
            if (_launched.ContainsKey(entry.Key))
            {
                _startedWith[entry.Key] = entry.Value;
            }
        }
    }

    /// <summary>
    /// The settings to ask Offloadr with about the container of <paramref name="launchId"/>: those in
    /// Settings when they name the runner it was launched for, otherwise those with the address and key
    /// it was started with, if these do.
    /// </summary>
    private RunnerSettings ReportAsFor(string? launchId, string? target, RunnerSettings current)
        => launchId is null || target is null
            ? current
            : Hosting.RunnerCleanup.ReportAs(new Hosting.RunnerCleanup.StoppedLaunch(launchId, target, _startedWith.GetValueOrDefault(launchId)), current);

    /// <summary>
    /// Notes that the container started for <paramref name="launchId"/> no longer runs, without a granted
    /// release. Offloadr is told as soon as it can be (<c>container_exited</c>): it then ends the session
    /// that container's executor held, rather than waiting on it.
    /// </summary>
    private void NoteGone(string launchId, string target, string? executor)
    {
        var known = _launched.TryGetValue(launchId, out var before) ? before : null;
        // An instance id that would not fit the saved record is left out: the launch alone names it.
        executor = executor is { Length: > 0 } named && !named.Any(char.IsWhiteSpace) ? named : null;
        _launched[launchId] = new LaunchedContainer(known?.Target ?? target, Gone: true, executor ?? known?.Executor, known?.Engine);
        SaveLaunched();
        _goneReportDue = true;
    }

    /// <summary>Called by a look, on its own thread, as soon as it removed a container of a launch.</summary>
    private void NoteRemoved(SupervisorEffects.RemovedLaunch removed) => Post(() => RecordRemoved(removed));

    /// <summary>
    /// Still only created, and its engine would not remove it, though its start was asked for (the app closed
    /// before the engine answered): it may start it yet.
    /// </summary>
    private bool MayStartYet(SupervisorEffects.LaunchedLook look)
        => look.StillCreated && _launched.TryGetValue(look.LaunchId, out var asked) && !asked.RanNothingIfCreated && !asked.Gone;

    /// <summary>
    /// A look removed a stopped container of a launch: its record is settled by what was seen of it. One
    /// created and never started ran nothing, so its record goes with no exit to report; one that ran is
    /// gone, and Offloadr is told.
    /// </summary>
    private void RecordRemoved(SupervisorEffects.RemovedLaunch removed)
    {
        if (IsLaunchingOrSupervised(removed.LaunchId) ||
            !_launched.TryGetValue(removed.LaunchId, out var known) || known.Gone)
        {
            // Not this app's to account for, or Offloadr knows already (it released it, or was told).
            return;
        }

        _unseen.Remove(removed.LaunchId);
        if (SettleReleased(removed.LaunchId))
        {
            return;
        }

        if (removed.NeverStarted && known.RanNothingIfCreated)
        {
            // Seen created, and its start was never asked for: it ran nothing. (One whose start was asked
            // for before the app closed may have started and exited before it was removed: that is gone.)
            ForgetLaunched(removed.LaunchId);
            return;
        }

        // It ran, and stopped by itself while the app was not looking: an exit, for Offloadr to learn,
        // whatever its record said (a start not yet confirmed when the app closed, say).
        _exitedLaunches.Add(removed.LaunchId);
        NoteGone(removed.LaunchId, removed.Target ?? known.Target, executor: null);
    }

    /// <summary>
    /// A container that no longer runs was released by Offloadr before the app closed (the grant is
    /// noted): Offloadr knows it is gone. Its record and the note go, and nothing is reported. True
    /// when that was so.
    /// </summary>
    private bool SettleReleased(string launchId)
    {
        var marked = string.Equals(launchId, _releasedLaunch, StringComparison.OrdinalIgnoreCase);
        if (!IsReleased(launchId))
        {
            return false;
        }

        // (It ran: should its launch be named again, that it started is the true answer.)
        _exitedLaunches.Add(launchId);
        ForgetLaunched(launchId);
        if (marked)
        {
            ClearReleaseMarker();
        }

        return true;
    }

    private bool SaveLaunched() => WriteMarker(_launchedPath, _launched.Count == 0 ? null
        : string.Join('\n', _launched.Select(entry =>
            $"{(entry.Value.Gone ? "gone" : entry.Value.Release == ReleaseNote.Granted ? "released" : entry.Value.Release == ReleaseNote.Asked ? "asking" : entry.Value.Pending ? "starting" : entry.Value.Refused ? "refused" : "running")} {entry.Key} {entry.Value.Target} {entry.Value.Engine ?? "-"}{(entry.Value.Executor is { } executor ? " " + executor : string.Empty)}")));

    /// <summary>
    /// True when <paramref name="launchId"/> is the launch in flight, the container supervised, or the one
    /// beside it that is being stopped: what becomes of it is for that procedure to say. (A look that found
    /// it running, or gone, meanwhile would have it stopped, or told, a second time.)
    /// </summary>
    private bool IsLaunchingOrSupervised(string launchId)
        => string.Equals(launchId, _flight?.Launch.LaunchId, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(launchId, (_stop?.Strays ?? _closing?.Strays)?.Current?.LaunchId, StringComparison.OrdinalIgnoreCase) ||
           (_ownsContainer && string.Equals(launchId, _containerLaunchId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The containers noted as started and not known gone, that are neither in flight nor supervised:
    /// each with the engine it was started on. <paramref name="on"/> keeps those of one engine.
    /// </summary>
    private List<(string LaunchId, ContainerEngineSelection.Selected Engine)> UnresolvedLaunches(ContainerEngineSelection.Selected? on = null)
        => [.. _launched.Where(entry => !entry.Value.Gone && !IsLaunchingOrSupervised(entry.Key))
            .Select(entry => (entry.Key, Engine: entry.Value.Engine is { } described ? ContainerEngineSelection.Read(described) : null))
            .Where(entry => entry.Engine is not null && (on is null || SameEngine(entry.Engine, on)))
            .Select(entry => (entry.Key, entry.Engine!))];

    /// <summary>
    /// Records what a look for started containers found: one that does not run is gone, unless it is
    /// in flight or supervised by now (a lookup takes time), and a stopped one an engine kept is noted
    /// for removal. What could not be looked for stays as it was.
    /// </summary>
    /// <param name="maintenance">Made by maintenance, not by the procedure in progress: the two look on their own.</param>
    private void RecordLaunchedLook(SupervisorEffects.LaunchedLook look, bool maintenance)
    {
        if (look is { Found: false } && (maintenance ? ProcedureLooks : _maintenanceSlot.Busy && _maintenanceLooksFor.Contains(look.LaunchId)))
        {
            // Not there, while the other one looks as well: it may have removed the container, and have seen
            // what this one no longer can (that it never ran). What it saw counts; with nothing from it, the
            // container is looked for again.
            return;
        }

        // Nothing of one that may start yet is settled, and it is no leftover that only waits to be
        // removed: it is looked for again.
        var mayStartYet = MayStartYet(look);
        if (look.Leftover is { } leftover && !mayStartYet)
        {
            NoteLeftover(look.Engine, leftover);
        }

        // (Its engine answered: what was seen of it before counts no more.)
        _strays.Remove(look.LaunchId);
        if (look.Running is null && _foundRunning is { } waiting &&
            waiting.Container.Labels.TryGetValue(LaunchIdLabel, out var queuedFor) &&
            string.Equals(queuedFor, look.LaunchId, StringComparison.OrdinalIgnoreCase))
        {
            // Found running by an earlier look, and waiting to be taken up: it does not run any more.
            _foundRunning = null;
        }

        if (mayStartYet || IsLaunchingOrSupervised(look.LaunchId) || !_launched.TryGetValue(look.LaunchId, out var known) || known.Gone)
        {
            return;
        }

        if (look.Running is { } running)
        {
            // It runs, under another name or on an engine that did not answer before: this app started
            // it, so it is supervised again (released before anything replaces it, and reached by Stop
            // or Quit), as soon as nothing else is. Beside a runner that is supervised, it stays known.
            // That it ran is noted now: it may be gone again before it is taken up.
            ConfirmLaunched(look.LaunchId);
            _unseen.Remove(look.LaunchId);
            if (!_ownsContainer && _foundRunning is null)
            {
                _foundRunning = (running, look.Engine);
            }
            else if (_foundRunning is not { } queued ||
                     !string.Equals(NormalizeContainerId(queued.Container.Id), NormalizeContainerId(running.Id), StringComparison.OrdinalIgnoreCase))
            {
                // Beside the supervised one, or beside the one about to be supervised: known all the
                // same, so a Stop or the app closing stops it too.
                _strays[look.LaunchId] = (running, look.Engine);
            }

            return;
        }

        if (!look.Found && known.Pending)
        {
            // Never seen since its creation was asked for. Not there now is no proof it never will be
            // (the engine may still create it): looked for a while longer. Whatever shows was never
            // started, and is only removed; not there by then, it was never created.
            if (!_unseen.TryGetValue(look.LaunchId, out var until))
            {
                _unseen[look.LaunchId] = _now + _options.LateContainerWatch;
            }
            else if (_now >= until)
            {
                _unseen.Remove(look.LaunchId);
                ForgetLaunched(look.LaunchId);
            }

            return;
        }

        _unseen.Remove(look.LaunchId);
        if (SettleReleased(look.LaunchId))
        {
            return;
        }

        if ((look.NeverStarted || (!look.Found && known.SeenCreated)) && known.RanNothingIfCreated)
        {
            // Created, and its start never asked for (or refused): it ran nothing, so there is no exit to report.
            // (Not there any more, it was seen created before, and nothing showed it as one that ran since.)
            // (One whose start was asked for may have started and exited before it was removed.)
            ForgetLaunched(look.LaunchId);
            return;
        }

        // It ran and exited by itself, or its start was asked for and it is not there to say: its
        // launch never fails for that, whenever it is named again.
        _exitedLaunches.Add(look.LaunchId);

        _launched[look.LaunchId] = known with { Gone = true, Pending = false, Refused = false };
        SaveLaunched();
        _goneReportDue = true;
    }

    /// <summary>
    /// Tells Offloadr of each container noted gone, for the runner it was started for: as the launcher
    /// registered now, or as the one that last ran for that runner (taken over since, say), which Offloadr
    /// accepts for a report. Each is resent until Offloadr answers. The launcher is never registered just to
    /// report, which could take the runner from another computer: one that never ran here waits.
    /// </summary>
    private void WorkOffGoneReports()
    {
        if (!HasGoneToReport || _goneSlot.Busy)
        {
            return;
        }

        var retryAt = (_goneReportedAt ?? TimeSpan.MinValue) + _options.ReleaseRetryInterval;
        if (!_goneReportDue && _goneReportedAt is not null && _now < retryAt)
        {
            WakeAt(retryAt);
            return;
        }

        _goneReportDue = false;
        _goneReportedAt = _now;
        var settings = _settings.Current;
        var pending = _launched.Where(entry => entry.Value.Gone)
            .Select(entry => (entry.Key, entry.Value, ReportAsFor(entry.Key, entry.Value.Target, settings)))
            .ToList();
        Start(_goneSlot, "gone", token => _fx.ReportGoneAsync(pending, token), result =>
        {
            foreach (var answered in result.Value ?? [])
            {
                AppendLog(answered.Taken
                    ? "supervisor: told Offloadr that a runner container it started is gone."
                    // (Not what it answered: that may quote the key the container was started with, which is
                    // not one the log knows to hide any more.)
                    : "supervisor: Offloadr did not take the report of a runner container that is gone.");

                // Only what was reported: one noted gone again since (never, for one launch) would stay.
                if (!_launched.TryGetValue(answered.LaunchId, out var known) || !known.Gone)
                {
                    continue;
                }

                if (!answered.Taken && known.Executor is not null)
                {
                    // Refused as it was named: the executor instance read before may no longer be the one
                    // registered for this launch (the agent restarted inside the container). Still owed,
                    // and sent again at once naming the launch alone, which is what names the container.
                    _launched[answered.LaunchId] = known with { Executor = null };
                    SaveLaunched();
                    _goneReportDue = true;
                    continue;
                }

                // Taken; or refused though it named the launch alone, which nothing sent again would change.
                ForgetLaunched(answered.LaunchId);

                // A launch Offloadr still names for it can be answered now.
                _launchLook = true;
            }

            if (result.Error is { } error)
            {
                _logger.LogWarning("Could not report a runner container that is gone: {Error}", error.GetType().Name);
            }
        });
    }

    /// <summary>
    /// Removes stopped leftovers and looks for containers started before whose fate is not known, each on
    /// its own engine, in a lane of its own: an engine that does not answer (one Settings moved away
    /// from) holds up nothing else. As often as the engine is checked, whatever the runner's state.
    /// </summary>
    private void WorkOffMaintenance()
    {
        if (_maintenanceSlot.Busy)
        {
            return;
        }

        var unresolved = UnresolvedLaunches();
        if (!HasLeftovers && unresolved.Count == 0)
        {
            return;
        }

        var dueAt = (_maintainedAt ?? TimeSpan.MinValue) + _options.EngineCheckInterval;
        if (_maintainedAt is not null && _now < dueAt)
        {
            WakeAt(dueAt);
            return;
        }

        _maintainedAt = _now;
        var leftovers = _leftovers.ToList();
        _maintenanceLooksFor = new HashSet<string>(unresolved.Select(static launch => launch.LaunchId), StringComparer.OrdinalIgnoreCase);
        Start(_maintenanceSlot, "maintenance", token => _fx.MaintainAsync(leftovers, unresolved, look => Post(() => RecordLaunchedLook(look, maintenance: true)), token), result =>
        {
            _maintenanceLooksFor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (result.Value is not { } done)
            {
                return;
            }

            foreach (var leftover in done.Running)
            {
                // It stays noted (with its launch settled, nothing else would look for it): it still holds
                // the runner key, and goes once it has stopped again.
                if (_leftoversRunning.Add(leftover.Container))
                {
                    AppendLog("warning: a stopped runner container was started again outside the app; it is left running.");
                }
            }

            foreach (var leftover in done.Resolved)
            {
                _leftoversRunning.Remove(leftover.Container);
                _leftovers.RemoveAll(known => known.Container == leftover.Container && SameEngine(known.Engine, leftover.Engine));
            }

            if (done.Resolved.Count > 0)
            {
                SaveLeftovers();
            }
        });
    }

    // Stopped containers an engine kept

    /// <summary>Notes a stopped runner container the engine kept, on its engine, saved so a crash forgets none.</summary>
    private void NoteLeftover(ContainerEngineSelection.Selected engine, string container)
    {
        if (!_leftovers.Any(leftover => leftover.Container == container && SameEngine(leftover.Engine, engine)))
        {
            _leftovers.Add((engine, container));
        }

        SaveLeftovers();
    }

    private void SaveLeftovers() => WriteMarker(_leftoversPath, _leftovers.Count == 0 ? null
        : string.Join('\n', _leftovers.Select(leftover => $"{ContainerEngineSelection.Describe(leftover.Engine)} {leftover.Container}")));

    // Takeovers

    private bool IsTakenOver(RunnerSettings settings) => _takenOver.Contains(TakeoverTag(Credentials(settings)));

    /// <summary>Notes that another launcher took the runner of <paramref name="credentials"/> over; kept across app starts.</summary>
    private void RememberTakeover((string Origin, string Key) credentials)
    {
        if (_takenOver.Add(TakeoverTag(credentials)))
        {
            SaveTakeovers();
        }
    }

    /// <summary>The person pressed Start with <paramref name="settings"/>: that runner is taken back, and only that one.</summary>
    private void ForgetTakeover(RunnerSettings settings)
    {
        if (_takenOver.Remove(TakeoverTag(Credentials(settings))))
        {
            SaveTakeovers();
        }
    }

    private void SaveTakeovers() => WriteMarker(_takeoverMarkerPath, _takenOver.Count == 0 ? null : string.Join('\n', _takenOver));

    // The release marker

    private const string ReleaseAskedPrefix = "asking:";

    /// <summary>
    /// Reads the release marker: the launch whose release was granted (its container is stopped, never
    /// reused), or the one whose release was asked without an answer seen (asked again before reuse).
    /// </summary>
    private void ReadReleaseMarker()
    {
        var content = ReadMarker(_releaseMarkerPath);
        _releasedLaunch = content is not null && !content.StartsWith(ReleaseAskedPrefix, StringComparison.Ordinal) ? content : null;
        _releaseAsked = content is not null && content.StartsWith(ReleaseAskedPrefix, StringComparison.Ordinal) ? content[ReleaseAskedPrefix.Length..] : null;
    }

    /// <summary>Notes, before asking, that a release of <paramref name="launchId"/> is being asked: a grant whose answer is lost still counts.</summary>
    private bool WriteReleaseAsked(string launchId)
    {
        KeepReleaseOfAnother(launchId);
        _releasedLaunch = null;
        _releaseAsked = launchId;
        return WriteMarker(_releaseMarkerPath, ReleaseAskedPrefix + launchId);
    }

    /// <summary>Notes that the release of <paramref name="launchId"/> was granted, until its container is stopped.</summary>
    private void WriteReleaseMarker(string? launchId)
    {
        KeepReleaseOfAnother(launchId);
        _releasedLaunch = launchId;
        _releaseAsked = null;
        if (launchId is not null)
        {
            WriteMarker(_releaseMarkerPath, launchId);
        }
    }

    private void ClearReleaseMarker()
    {
        if (_releasedLaunch is null && _releaseAsked is null)
        {
            return;
        }

        KeepReleaseOfAnother(_containerLaunchId);
        _releasedLaunch = null;
        _releaseAsked = null;
        WriteMarker(_releaseMarkerPath, null);
    }

    /// <summary>
    /// The marker names one launch, the supervised one's. Before it names <paramref name="launchId"/>, or
    /// nothing, what it says of another whose container is still looked for (its engine stopped answering
    /// after the grant, and the person moved on) goes to that launch's own record: it is stopped, and never
    /// taken up again, whenever that engine answers.
    /// </summary>
    private void KeepReleaseOfAnother(string? launchId)
    {
        if (_releasedLaunch is { } released && !string.Equals(released, launchId, StringComparison.OrdinalIgnoreCase))
        {
            NoteRelease(released, ReleaseNote.Granted);
        }

        if (_releaseAsked is { } asked && !string.Equals(asked, launchId, StringComparison.OrdinalIgnoreCase) && ReleaseNoted(asked) == ReleaseNote.None)
        {
            NoteRelease(asked, ReleaseNote.Asked);
        }
    }

    // Files

    /// <summary>Writes, or with null removes, a small state file in the app's data folder; false when that failed.</summary>
    private bool WriteMarker(string? path, string? content)
    {
        if (path is null)
        {
            return true;
        }

        try
        {
            if (content is null)
            {
                File.Delete(path);
            }
            else
            {
                // Replaced whole, so a crash leaves the old note or the new one, never half of one.
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, content);
                File.Move(temporary, path, overwrite: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not update {Marker}: {Error}", Path.GetFileName(path), ex.GetType().Name);
            return false;
        }
    }

    private string? ReadMarker(string? path)
    {
        try
        {
            return path is not null && File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } content ? content : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not read {Marker}: {Error}", Path.GetFileName(path), ex.GetType().Name);
            return null;
        }
    }
}
