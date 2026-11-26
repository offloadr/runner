using System.Collections.Concurrent;
using System.Diagnostics;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Settings;
using Offloadr.Runner.Supervisor.Processes;

namespace Offloadr.Runner.Supervisor.Hosting;

/// <summary>
/// Removes a runner container the app left running, for an uninstaller: the app is closed (or was
/// killed) by then, and the container keeps the runner key in its environment. Only the container
/// this data folder's copy of the app started is removed, on the engine it was started on, and only
/// while no other copy of the app manages it: one run from outside the install folder (a build
/// folder, say) shares the data folder, and the installer does not close it.
/// </summary>
public static class RunnerCleanup
{
    /// <summary>A container the app started for an Offloadr launch, which the cleanup stopped: Offloadr counts on it until told.</summary>
    /// <param name="Target">The Offloadr address and runner it was launched for.</param>
    /// <param name="StartedWith">
    /// The address and key its container was started with, read from the container before it was removed.
    /// Used only when Settings name another runner by now (<see cref="ReportAs"/>). Null when unread.
    /// </param>
    public sealed record StoppedLaunch(string LaunchId, string Target, LaunchCredentials? StartedWith = null);

    /// <summary>The Offloadr address and runner key a container was started with. Never printed.</summary>
    public sealed record LaunchCredentials(string Origin, string Key)
    {
        public override string ToString() => "(address and key)";
    }

    /// <summary>
    /// The settings to ask Offloadr with about <paramref name="stopped"/>: those in the data folder when
    /// they name the runner it was launched for. Otherwise (the address or key was changed while it still
    /// ran) those with the address and key its own container was started with, if these name that runner:
    /// only the Offloadr that launched it can be told.
    /// </summary>
    public static RunnerSettings ReportAs(StoppedLaunch stopped, RunnerSettings current)
    {
        if (stopped.StartedWith is not { } startedWith ||
            string.Equals(RunnerSupervisor.LaunchTargetFor(current), stopped.Target, StringComparison.Ordinal))
        {
            return current;
        }

        var original = current with { OffloadrApiOrigin = startedWith.Origin, RunnerKey = startedWith.Key };
        return string.Equals(RunnerSupervisor.LaunchTargetFor(original), stopped.Target, StringComparison.Ordinal) ? original : current;
    }

    /// <summary>A step's share of the time: its own budget, or what is left once <paramref name="reserve"/> is kept for what follows.</summary>
    private static TimeSpan Share(TimeSpan own, TimeSpan left, TimeSpan reserve)
    {
        var available = left - reserve;
        return available >= own ? own : available > MinimumShare ? available : MinimumShare;
    }

    private static readonly TimeSpan MinimumShare = TimeSpan.FromMilliseconds(500);

    /// <summary>Kept for the reports to Offloadr, which come last.</summary>
    private static readonly TimeSpan ReportReserve = TimeSpan.FromSeconds(3);

    /// <summary>Kept for the removal of the runner container, which holds the runner key.</summary>
    private static readonly TimeSpan RemovalReserve = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Drops, from what the app saved of the launched containers' addresses and keys, every launch that
    /// is settled (no longer noted): no report needs it any more, and the data folder outlives the app.
    /// </summary>
    private static void PruneSavedCredentials(SupervisorPaths paths)
    {
        try
        {
            if (!File.Exists(paths.LaunchedCredentials))
            {
                return;
            }

            var noted = File.Exists(paths.LaunchedContainers)
                ? File.ReadAllLines(paths.LaunchedContainers)
                    .Select(static line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Where(static parts => parts.Length >= 2)
                    .Select(static parts => parts[1])
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            var lines = File.ReadAllLines(paths.LaunchedCredentials);
            var kept = lines.Where(line => line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries) is { Length: 2 } parts && noted.Contains(parts[0])).ToList();
            if (kept.Count == 0)
            {
                File.Delete(paths.LaunchedCredentials);
            }
            else if (kept.Count != lines.Length)
            {
                var temporary = paths.LaunchedCredentials + ".tmp";
                File.WriteAllLines(temporary, kept);
                File.Move(temporary, paths.LaunchedCredentials, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the app drops them at its next start.
        }
    }

    private static ConcurrentDictionary<string, LaunchCredentials> ReadSavedCredentials(SupervisorPaths paths, ISecretProtector? protector)
    {
        try
        {
            if (protector is not null && File.Exists(paths.LaunchedCredentials))
            {
                return new ConcurrentDictionary<string, LaunchCredentials>(LaunchedCredentialsFile.Read(File.ReadAllText(paths.LaunchedCredentials), protector), StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unread: asked with the settings in the data folder.
        }

        return new ConcurrentDictionary<string, LaunchCredentials>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reads, from a launched container that is about to go, the address and key it was started with (best effort).</summary>
    private static async Task CaptureAsync(IContainerEngine engine, ContainerInfo container, ConcurrentDictionary<string, LaunchCredentials> startedWith, CancellationToken cancellationToken)
    {
        if (!container.Labels.TryGetValue(RunnerSupervisor.LaunchIdLabel, out var launchId))
        {
            return;
        }

        // Briefly, within a budget of its own: the container is stopped and removed whatever this reads.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(CaptureBudget);
        try
        {
            var target = string.IsNullOrWhiteSpace(container.Id) ? launchId : container.Id;
            var origin = await engine.ReadEnvironmentVariableAsync(target, "OFFLOADR_API_GRPC", budget.Token).ConfigureAwait(false);
            var key = await engine.ReadEnvironmentVariableAsync(target, "RUNNER_SECRET", budget.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(origin) && !string.IsNullOrWhiteSpace(key))
            {
                startedWith[launchId] = new LaunchCredentials(origin.Trim(), key.Trim());
            }
        }
        catch (Exception ex) when (ex is ContainerEngineException or ProcessStartException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Unread: asked with the settings in the data folder, as before.
        }
    }

    private static readonly TimeSpan CaptureBudget = TimeSpan.FromSeconds(2);

    /// <summary>How long the engine the runner was started on gets to say whether it is still there.</summary>
    private static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(4);

    /// <param name="reportGone">
    /// Tells Offloadr that a launched container is gone (<c>container_exited</c>), without registering a
    /// launcher; true once Offloadr took the report. The app is being removed, so there may be no next
    /// start to tell it. Null: the note in the data folder is all that is kept.
    /// </param>
    /// <param name="askRelease">
    /// Asks Offloadr, before a running launched container is stopped, to release it (so nothing is
    /// assigned to it while it stops); true when it granted that. The container is stopped either way.
    /// </param>
    /// <param name="protector">Reads what the app saved of each launched container's address and key (<c>launched-credentials</c>).</param>
    /// <param name="within">
    /// The time the caller has in all (an installer's hook, say): the steps share it, so that waiting on
    /// one engine or on Offloadr leaves time to remove the runner container and to send the reports.
    /// </param>
    /// <returns>True when a container was removed.</returns>
    public static async Task<bool> RemoveLeftoverRunnerAsync(
        SupervisorPaths paths,
        IProcessRunner processes,
        SupervisorOptions options,
        CancellationToken cancellationToken,
        Func<StoppedLaunch, CancellationToken, Task<bool>>? reportGone = null,
        Func<StoppedLaunch, CancellationToken, Task<bool>>? askRelease = null,
        ISecretProtector? protector = null,
        TimeSpan? within = null)
    {
        // What is left of the caller's time: each step gets its own share, and none may use up what the
        // removal of the runner (it holds the key) and the reports to Offloadr still need.
        var began = Stopwatch.GetTimestamp();
        TimeSpan Left() => within is { } all ? all - Stopwatch.GetElapsedTime(began) : TimeSpan.FromHours(1);

        // Held for the whole cleanup, so a copy that starts meanwhile waits instead of adopting a
        // runner this removes.
        using var supervisorLock = new SupervisorInstanceLock(paths.SupervisorLock);
        if (!supervisorLock.TryAcquire())
        {
            return false;
        }

        if (SupervisorOwner.TryLoad(paths.Owner) is not { } owner)
        {
            return false;
        }

        // The runner that may still run first; then stopped ones the engine would not remove at the time
        // (leftover-containers), which hold the key too, within a budget of their own: an engine of theirs
        // that does not answer must not cost the active runner its cleanup.
        var removed = false;
        // What each launched container was started with, as the app saved it: a container that is gone
        // can still be reported to the Offloadr that launched it. (What a container still there says wins.)
        var startedWith = ReadSavedCredentials(paths, protector);

        Exception? rememberedFailure = null;
        try
        {
            removed = await RemoveRememberedRunnerAsync(paths, processes, options, owner.Id, askRelease, startedWith, Left, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ContainerEngineException or ProcessStartException)
        {
            // That engine does not answer: the stopped containers on other engines are removed all the same.
            rememberedFailure = ex;
        }

        using var leftoverBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        leftoverBudget.CancelAfter(Share(LeftoverBudget, Left(), ReportReserve));
        try
        {
            removed |= await RemoveStoppedLeftoversAsync(paths, processes, owner.Id, leftoverBudget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The next start of the app tries those again.
        }

        // Containers the app started and still counts as running (one removed while it was closed, or one
        // under another name): looked up by their launch on the engine each was started on, and removed.
        using (var reconcileBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            reconcileBudget.CancelAfter(Share(ReconcileBudget, Left(), ReportReserve));
            try
            {
                removed |= await ReconcileNotedAsync(paths, processes, owner.Id, askRelease, startedWith, options.CancelledLaunchSettle, reconcileBudget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // What stays noted, a later start of the app looks for.
            }
        }

        // Containers noted gone that Offloadr was not told of yet (stopped while it could not be reached,
        // say): told now, since no later start of the app may.
        await ReportNotedGoneAsync(paths, reportGone, startedWith, cancellationToken).ConfigureAwait(false);
        PruneSavedCredentials(paths);
        if (rememberedFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(rememberedFailure);
        }

        return removed;
    }

    /// <summary>
    /// Settles each container noted as started and not known gone: found by its launch on its own engine,
    /// it is stopped and removed (it holds the runner key); not there any more, it is gone. One that ran
    /// is noted gone, for Offloadr to be told; one never seen started is dropped. An engine that does not
    /// answer leaves its record as it is.
    /// </summary>
    private static async Task<bool> ReconcileNotedAsync(
        SupervisorPaths paths,
        IProcessRunner processes,
        string ownerId,
        Func<StoppedLaunch, CancellationToken, Task<bool>>? askRelease,
        ConcurrentDictionary<string, LaunchCredentials> startedWith,
        TimeSpan settle,
        CancellationToken cancellationToken)
    {
        List<string> lines;
        try
        {
            if (!File.Exists(paths.LaunchedContainers))
            {
                return false;
            }

            lines = [.. await File.ReadAllLinesAsync(paths.LaunchedContainers, cancellationToken).ConfigureAwait(false)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        // Each on its own, at the same time: an engine that hangs must not cost the containers on the others
        // their turn (each holds the runner key).
        using var journal = new NotedRecords(paths, lines);
        var removed = await Task.WhenAll(lines.Select((line, index) => ReconcileAsync(index, line))).ConfigureAwait(false);
        return removed.Contains(true);

        async Task<bool> ReconcileAsync(int index, string line)
        {
            // state, launch, target, engine ("-" when unknown), then the executor instance if known.
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || parts[0] == "gone" || parts[3] == "-" || ContainerEngineSelection.Read(parts[3]) is not { } noted)
            {
                return false;
            }

            var selection = new ContainerEngineSelection(statePath: null);
            selection.SelectExactly(noted);
            IContainerEngine engine = new DockerCliContainerEngine(processes, () => selection.Executable, () => selection.Pin);
            try
            {
                return await ReconcileOnAsync(index, parts, engine, noted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ContainerEngineException or ProcessStartException or OperationCanceledException)
            {
                // That engine does not answer now (or not in its share of the time), or would not remove it:
                // its record stays as it is, for a later start of the app.
                return false;
            }
        }

        async Task<bool> ReconcileOnAsync(int index, string[] parts, IContainerEngine engine, ContainerEngineSelection.Selected noted)
        {
            ContainerInfo? found;
            using (var lookup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                lookup.CancelAfter(LookupBudget);
                found = await engine.FindByLabelAsync(RunnerSupervisor.LaunchIdLabel, parts[1], lookup.Token).ConfigureAwait(false);
            }

            // Its start was never asked for (only its creation): absent, it ran nothing.
            var startAsked = parts[0] != "starting";

            // The engine refused its start: found created it never ran, nor will it. Absent, nothing says so.
            var refused = parts[0] == "refused";
            if (found is null && !startAsked)
            {
                // Its creation may still be under way in the engine (the app closed, or crashed, right
                // after asking for it): looked for a moment longer. Not there by then, its record
                // stays, so a later start of the app looks for what may yet be made (it would hold
                // the runner key).
                var started = Stopwatch.GetTimestamp();
                while (found is null && Stopwatch.GetElapsedTime(started) < settle)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250, Math.Max(1, settle.TotalMilliseconds / 4))), cancellationToken).ConfigureAwait(false);
                    using var again = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    again.CancelAfter(LookupBudget);
                    found = await engine.FindByLabelAsync(RunnerSupervisor.LaunchIdLabel, parts[1], again.Token).ConfigureAwait(false);
                }

                if (found is null)
                {
                    return false;
                }
            }

            var ran = startAsked;
            var released = parts[0] == "released";
            if (found is null || !RunnerSupervisor.IsOwnedBy(found, ownerId))
            {
                // It ran, and is gone: for Offloadr to be told. One it released, it knows of.
                await journal.SetAsync(index, Settled(parts, ran, released)).ConfigureAwait(false);
                return false;
            }

            var target = string.IsNullOrWhiteSpace(found.Id) ? parts[1] : found.Id;

            // Seen created counts only for one whose start was never asked for: after that, it
            // may have started and exited before it is removed here.
            ran = (startAsked && !refused) || !found.NeverStarted;
            await CaptureAsync(engine, found, startedWith, cancellationToken).ConfigureAwait(false);
            var stopped = !found.Running;

            // That it ran is saved before anything is done to it: removed with the answer lost, nothing
            // would show it any more.
            Task NoteRanAsync()
            {
                if (parts[0] is not ("starting" or "refused"))
                {
                    return Task.CompletedTask;
                }

                parts[0] = "running";
                return journal.SetAsync(index, string.Join(' ', parts));
            }

            if (!found.NeverStarted)
            {
                await NoteRanAsync().ConfigureAwait(false);
            }

            // Seen created, or exited: removed only if it does not run. It may have been started since,
            // and it may serve: it is released and stopped then, as any that runs. (Seen created, with
            // its start asked for before the app closed, the engine may still start it.)
            var mayStillStart = found.NeverStarted && startAsked && !refused;
            var early = await RemoveIfStoppedAsync(engine, found, target, ReconcileRemovalBudget, () => LookupBudget, mayStillStart, cancellationToken).ConfigureAwait(false);
            ran |= early.HasRun;
            switch (early.Outcome)
            {
                case EarlyOutcome.Unknown:
                    // It may run, or be started yet, or the engine does not say: its record stays as it is.
                    return false;
                case EarlyOutcome.Kept:
                    await journal.NoteLeftoverAsync(noted, target).ConfigureAwait(false);
                    await journal.SetAsync(index, Settled(parts, ran, released)).ConfigureAwait(false);
                    return false;
                case EarlyOutcome.Removed:
                    await journal.ForgetLeftoverAsync(noted, target).ConfigureAwait(false);
                    await journal.SetAsync(index, Settled(parts, ran, released)).ConfigureAwait(false);
                    return true;
                case EarlyOutcome.Runs:
                    found = found with { Running = true, NeverStarted = false, ExitCode = null };
                    stopped = false;
                    await NoteRanAsync().ConfigureAwait(false);
                    break;
                default:
                    break;
            }

            // (Noted as it is asked, and as it is granted, before the stop.)
            released |= await TryReleaseAsync(
                found,
                askRelease,
                startedWith,
                (_, state) =>
                {
                    if (parts[0] != "released")
                    {
                        parts[0] = state;
                    }

                    return journal.SetAsync(index, string.Join(' ', parts));
                },
                cancellationToken).ConfigureAwait(false);
            using (var stopBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                stopBudget.CancelAfter(ReconcileStopGrace + TimeSpan.FromSeconds(2));
                try
                {
                    await engine.StopAsync(target, ReconcileStopGrace, stopBudget.Token).ConfigureAwait(false);
                    stopped = true;
                }
                catch (Exception ex) when (ex is ContainerEngineException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    // Removing it below still ends it.
                }
            }

            if (stopped)
            {
                // Stopped here: saved as what Offloadr is told before its removal is tried, which may
                // use up the time that is left. Its removal is noted first: with its record settled,
                // nothing else would look for it (it still holds the runner key), should this be cut
                // short right here.
                await journal.NoteLeftoverAsync(noted, target).ConfigureAwait(false);
                await journal.SetAsync(index, Settled(parts, ran, released)).ConfigureAwait(false);
            }

            try
            {
                using var removal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                removal.CancelAfter(ReconcileRemovalBudget);
                await engine.RemoveAsync(target, removal.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (stopped && ex is ContainerEngineException or OperationCanceledException)
            {
                // Stopped, and the engine kept it (or did not remove it in time): it serves nothing any more,
                // which is what Offloadr is told. It stays noted, so a later start of the app (the data folder
                // stays) removes it.
                return false;
            }

            if (stopped)
            {
                await journal.ForgetLeftoverAsync(noted, target).ConfigureAwait(false);
            }

            await journal.SetAsync(index, Settled(parts, ran, released)).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>
    /// The records of launched-containers while each is settled on its own, at the same time: the notes are
    /// written one change after another, each time whole.
    /// </summary>
    private sealed class NotedRecords(SupervisorPaths paths, IEnumerable<string> lines) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly string?[] _lines = [.. lines];

        /// <summary>Saves what a record is now; null drops it.</summary>
        public Task SetAsync(int index, string? line)
            => InTurnAsync(() =>
            {
                if (string.Equals(_lines[index], line, StringComparison.Ordinal))
                {
                    return Task.CompletedTask;
                }

                _lines[index] = line;
                return SaveNotedAsync(paths, [.. _lines.OfType<string>()]);
            });

        public Task NoteLeftoverAsync(ContainerEngineSelection.Selected engine, string container)
            => InTurnAsync(() => RunnerCleanup.NoteLeftoverAsync(paths, engine, container, CancellationToken.None));

        public Task ForgetLeftoverAsync(ContainerEngineSelection.Selected engine, string container)
            => InTurnAsync(() => RunnerCleanup.ForgetLeftoverAsync(paths, engine, container));

        public void Dispose() => _gate.Dispose();

        private async Task InTurnAsync(Func<Task> change)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await change().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>What stays noted of a container that is settled: gone, for Offloadr to be told, unless it never ran or was released.</summary>
    private static string? Settled(string[] parts, bool ran, bool released)
        => ran && !released ? string.Join(' ', ["gone", .. parts.Skip(1)]) : null;

    private static async Task SaveNotedAsync(SupervisorPaths paths, IReadOnlyCollection<string> lines)
    {
        try
        {
            await ReplaceAsync(paths.LaunchedContainers, lines, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a later start of the app looks again.
        }
    }

    /// <summary>Notes a stopped runner container an engine would not remove, on its engine, for removal later.</summary>
    private static async Task NoteLeftoverAsync(SupervisorPaths paths, ContainerEngineSelection.Selected engine, string container, CancellationToken cancellationToken)
    {
        try
        {
            var line = $"{ContainerEngineSelection.Describe(engine)} {container}";
            var lines = File.Exists(paths.LeftoverContainers)
                ? [.. await File.ReadAllLinesAsync(paths.LeftoverContainers, cancellationToken).ConfigureAwait(false)]
                : new List<string>();
            if (!lines.Contains(line, StringComparer.Ordinal))
            {
                lines.Add(line);
                await ReplaceAsync(paths.LeftoverContainers, lines, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    /// <summary>Takes a container out of the note of removals still to do: the engine removed it.</summary>
    private static async Task ForgetLeftoverAsync(SupervisorPaths paths, ContainerEngineSelection.Selected engine, string container)
    {
        try
        {
            if (!File.Exists(paths.LeftoverContainers))
            {
                return;
            }

            var line = $"{ContainerEngineSelection.Describe(engine)} {container}";
            var lines = await File.ReadAllLinesAsync(paths.LeftoverContainers, CancellationToken.None).ConfigureAwait(false);
            var kept = lines.Where(noted => !string.Equals(noted.Trim(), line, StringComparison.Ordinal)).ToList();
            if (kept.Count != lines.Length)
            {
                await ReplaceAsync(paths.LeftoverContainers, kept, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a later start of the app finds it gone, and drops the note.
        }
    }

    /// <summary>
    /// Writes a note whole, or removes it when nothing is left: through a temporary file, so being cut
    /// short leaves the old note or the new one, never half of one (and none of the entries it kept).
    /// </summary>
    private static async Task ReplaceAsync(string path, IReadOnlyCollection<string> lines, CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
        {
            File.Delete(path);
            return;
        }

        var temporary = path + ".tmp";
        await File.WriteAllLinesAsync(temporary, lines, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private static async Task ReportNotedGoneAsync(
        SupervisorPaths paths,
        Func<StoppedLaunch, CancellationToken, Task<bool>>? reportGone,
        ConcurrentDictionary<string, LaunchCredentials> startedWith,
        CancellationToken cancellationToken)
    {
        if (reportGone is null)
        {
            return;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ReportBudget);
        try
        {
            if (!File.Exists(paths.LaunchedContainers))
            {
                return;
            }

            var lines = (await File.ReadAllLinesAsync(paths.LaunchedContainers, budget.Token).ConfigureAwait(false)).ToList();

            // Each on its own, at the same time: an Offloadr that does not answer (an earlier address, say)
            // must not use up the time for the others, and what the others took is saved whatever it does.
            async Task<bool> TellAsync(string line)
            {
                // state, launch, target, engine, then the executor instance if known.
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4 || parts[0] != "gone")
                {
                    return false;
                }

                try
                {
                    return await reportGone(new StoppedLaunch(parts[1], parts[2], startedWith.GetValueOrDefault(parts[1])), budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            var told = await Task.WhenAll(lines.Select(TellAsync)).ConfigureAwait(false);
            var kept = lines.Where((_, index) => !told[index]).ToList();

            if (kept.Count == lines.Count)
            {
                return;
            }

            await ReplaceAsync(paths.LaunchedContainers, kept, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Best effort: what stays noted, a later start of the app reports.
        }
    }

    /// <summary>
    /// How long settling the containers still noted as started may take, and how long one found running
    /// gets to stop: short enough that its removal, which takes the runner key off the engine, and the
    /// report of it still fit.
    /// </summary>
    private static readonly TimeSpan ReconcileBudget = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ReconcileStopGrace = TimeSpan.FromSeconds(3);

    /// <summary>How long one such container's removal may take, so the next one, on another engine, still gets its turn.</summary>
    private static readonly TimeSpan ReconcileRemovalBudget = TimeSpan.FromSeconds(3);

    /// <summary>How long the stopped leftovers may take, after the active runner.</summary>
    private static readonly TimeSpan LeftoverBudget = TimeSpan.FromSeconds(5);

    private static async Task<bool> RemoveRememberedRunnerAsync(
        SupervisorPaths paths,
        IProcessRunner processes,
        SupervisorOptions options,
        string ownerId,
        Func<StoppedLaunch, CancellationToken, Task<bool>>? askRelease,
        ConcurrentDictionary<string, LaunchCredentials> startedWith,
        Func<TimeSpan> left,
        CancellationToken cancellationToken)
    {
        // The engine hint exists only while a runner container may still run.
        var selection = new ContainerEngineSelection(statePath: paths.EngineState);
        if (!selection.TryRestore())
        {
            return false;
        }

        IContainerEngine engine = new DockerCliContainerEngine(processes, () => selection.Executable, () => selection.Pin);
        ContainerInfo? container;
        using (var lookup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            // Briefly: an engine that hangs here must leave time for the other engines and the reports.
            lookup.CancelAfter(Share(LookupBudget, left(), RemovalReserve + ReportReserve));
            try
            {
                container = (selection.RememberedContainerId is { } id
                                ? await engine.InspectContainerAsync(id, lookup.Token).ConfigureAwait(false)
                                : null)
                            ?? await engine.InspectContainerAsync(options.ContainerName, lookup.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ContainerEngineException($"The container engine did not answer within {LookupBudget.TotalSeconds:0} seconds.");
            }
        }

        if (container is null || !RunnerSupervisor.IsOwnedBy(container, ownerId))
        {
            return false;
        }

        var target = string.IsNullOrWhiteSpace(container.Id) ? options.ContainerName : container.Id;
        var stopped = !container.Running;
        container.Labels.TryGetValue(RunnerSupervisor.LaunchIdLabel, out var notedLaunch);
        var released = notedLaunch is not null && NotedState(paths, notedLaunch) == "released";
        await CaptureAsync(engine, container, startedWith, cancellationToken).ConfigureAwait(false);

        // That it ran is saved before anything is done to it (as in the journal's pass).
        async Task NoteRanAsync()
        {
            if (notedLaunch is not null && NotedState(paths, notedLaunch) is "starting" or "refused")
            {
                await NoteStateAsync(paths, new StoppedLaunch(notedLaunch!, string.Empty, null), "running", selection.Current).ConfigureAwait(false);
            }
        }

        if (!container.NeverStarted)
        {
            await NoteRanAsync().ConfigureAwait(false);
        }

        // Seen created, or exited: removed only if it does not run (as there). Looked at again only in the
        // time that is left before the reports to Offloadr, which come last.
        var mayStillStart = container.NeverStarted && notedLaunch is not null && StartWasAsked(paths, notedLaunch);
        var early = await RemoveIfStoppedAsync(
            engine,
            container,
            target,
            Share(RemovalBudget, left(), ReportReserve),
            () => left() - ReportReserve,
            mayStillStart,
            cancellationToken).ConfigureAwait(false);
        if (early.HasRun)
        {
            container = container with { NeverStarted = false };
        }

        if (early.Outcome == EarlyOutcome.Runs)
        {
            container = container with { Running = true, NeverStarted = false, ExitCode = null };
            stopped = false;
            await NoteRanAsync().ConfigureAwait(false);
        }

        if (container.Running)
        {
            // Noted before it is asked, and once it is granted, before the stop: cut short in between, the
            // next start of the app never takes up a container Offloadr released.
            released |= await TryReleaseAsync(
                container,
                askRelease,
                startedWith,
                (launch, state) => NoteStateAsync(paths, launch, state, selection.Current),
                cancellationToken).ConfigureAwait(false);

            // Stopped first, as on a host shutdown, so the agent sends what it still holds (session
            // events, workspace writes) instead of being killed. The grace period is short, to leave
            // the removal room in the installer's budget.
            // (And no longer than leaves its removal, and the reports, their time.)
            var stopShare = Share(StopGracePeriod + TimeSpan.FromSeconds(3), left(), RemovalReserve + ReportReserve);
            var grace = stopShare - TimeSpan.FromSeconds(3) is var room && room > TimeSpan.FromSeconds(1) ? room : TimeSpan.FromSeconds(1);
            using var stopBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stopBudget.CancelAfter(stopShare);
            try
            {
                await engine.StopAsync(target, grace < StopGracePeriod ? grace : StopGracePeriod, stopBudget.Token).ConfigureAwait(false);
                stopped = true;
            }
            catch (Exception ex) when (ex is ContainerEngineException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Removing it below still ends it.
            }
        }

        // It serves nothing any more once it is removed, or stopped: Offloadr is told either way. One
        // created and never started ran nothing, and is no exit to report.
        var settled = false;
        async Task SettleAsync()
        {
            if (settled ||
                !container.Labels.TryGetValue(RunnerSupervisor.LaunchIdLabel, out var launchId) ||
                !container.Labels.TryGetValue(RunnerSupervisor.LaunchTargetLabel, out var launchTarget))
            {
                return;
            }

            if (container.NeverStarted && !StartWasAsked(paths, launchId))
            {
                // Only created (its start was never asked for, or refused): it ran nothing, and is no exit to
                // report. Forgotten, as what was seen here is the only proof of that: once it is removed, a
                // later look finds nothing to say so. One whose start was asked for may have run since it
                // was looked at: that one is told.
                settled = true;
                await ForgetNotedAsync(paths, launchId, cancellationToken).ConfigureAwait(false);
                return;
            }

            settled = true;
            if (released)
            {
                // Offloadr released it: it knows, and there is nothing to report.
                await ForgetNotedAsync(paths, launchId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Noted gone here (a file, at once); told to Offloadr with everything else noted gone, once
                // the engines are done with: the report must not hold up the removal, nor the removal the report.
                await NoteGoneAsync(paths, new StoppedLaunch(launchId, launchTarget, startedWith.GetValueOrDefault(launchId)), reportGone: null, cancellationToken).ConfigureAwait(false);
            }
        }

        if (stopped && early.Outcome is not (EarlyOutcome.Unknown or EarlyOutcome.Removed))
        {
            // Before its removal is tried: an engine that hangs on that must not lose the note. Its
            // removal is noted first: once its record is settled, only that note says it is still there
            // (it holds the runner key), should the engine not remove it now.
            await NoteLeftoverAsync(paths, selection.Current, target, CancellationToken.None).ConfigureAwait(false);
            await SettleAsync().ConfigureAwait(false);
        }

        ContainerEngineException? removalFailure = null;
        using (var removalBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            removalBudget.CancelAfter(Share(RemovalBudget, left(), ReportReserve));
            try
            {
                if (early.Outcome is EarlyOutcome.Kept or EarlyOutcome.Unknown)
                {
                    throw early.Failure ?? new ContainerEngineException("The container engine did not remove the runner container in time.");
                }

                if (early.Outcome != EarlyOutcome.Removed)
                {
                    await engine.RemoveAsync(target, removalBudget.Token).ConfigureAwait(false);
                }

                if (stopped)
                {
                    await ForgetLeftoverAsync(paths, selection.Current, target).ConfigureAwait(false);
                }

                // The hint goes only with the container: one the engine kept is still looked for there.
                selection.Forget();
            }
            catch (ContainerEngineException ex)
            {
                removalFailure = ex;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                removalFailure = new ContainerEngineException($"The container engine did not remove the runner container within {RemovalBudget.TotalSeconds:0} seconds.");
            }
        }

        if (removalFailure is null)
        {
            await SettleAsync().ConfigureAwait(false);
        }
        else
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(removalFailure);
        }

        return true;
    }

    private enum EarlyOutcome
    {
        /// <summary>It was seen running: nothing was tried.</summary>
        None,

        Removed,

        /// <summary>It runs: it was started since.</summary>
        Runs,

        /// <summary>The engine kept it, and shows that it does not run, and will not.</summary>
        Kept,

        /// <summary>It may run, or may come to: nothing is told of it.</summary>
        Unknown,
    }

    /// <param name="HasRun">The engine showed it as one that ran, since it was looked at.</param>
    private sealed record EarlyRemoval(EarlyOutcome Outcome, bool HasRun = false, ContainerEngineException? Failure = null);

    /// <summary>
    /// Removes a container that was not seen running, never by force: it may have been started since. One the
    /// engine did not remove is looked at again, for no more than <paramref name="lookWithin"/> (what is left of
    /// the time once the reports' share is kept; with none left it is not looked at).
    /// </summary>
    /// <param name="mayStart">Its start was asked for and not refused: still only created, it may be started yet.</param>
    private static async Task<EarlyRemoval> RemoveIfStoppedAsync(
        IContainerEngine engine,
        ContainerInfo container,
        string target,
        TimeSpan within,
        Func<TimeSpan> lookWithin,
        bool mayStart,
        CancellationToken cancellationToken)
    {
        if (container.Running)
        {
            return new EarlyRemoval(EarlyOutcome.None);
        }

        ContainerEngineException? failure;
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            budget.CancelAfter(within);
            try
            {
                return await engine.RemoveStoppedAsync(target, budget.Token).ConfigureAwait(false)
                    ? new EarlyRemoval(EarlyOutcome.Removed)
                    : new EarlyRemoval(EarlyOutcome.Runs, HasRun: true);
            }
            catch (Exception ex) when (ex is ContainerEngineException or OperationCanceledException)
            {
                failure = ex as ContainerEngineException;
            }
        }

        var lookFor = lookWithin();
        if (lookFor <= TimeSpan.Zero || cancellationToken.IsCancellationRequested)
        {
            return new EarlyRemoval(EarlyOutcome.Unknown, Failure: failure);
        }

        using var look = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        look.CancelAfter(lookFor < LookupBudget ? lookFor : LookupBudget);
        try
        {
            var now = await engine.InspectContainerAsync(target, look.Token).ConfigureAwait(false);
            return new EarlyRemoval(
                now switch
                {
                    // (Gone: its removal was done, and only the answer lost; or someone else removed it.)
                    null => EarlyOutcome.Removed,
                    { Running: true } => EarlyOutcome.Runs,
                    { MayStartYet: true } when mayStart => EarlyOutcome.Unknown,
                    _ => EarlyOutcome.Kept,
                },
                HasRun: now is { Running: true } or { NeverStarted: false },
                failure);
        }
        catch (Exception ex) when (ex is ContainerEngineException or ProcessStartException or OperationCanceledException)
        {
            return new EarlyRemoval(EarlyOutcome.Unknown, Failure: failure);
        }
    }

    /// <summary>True when the app noted that the start of <paramref name="launchId"/>'s container was asked for, and not refused (its record is neither <c>starting</c> nor <c>refused</c>).</summary>
    private static bool StartWasAsked(SupervisorPaths paths, string launchId)
    {
        try
        {
            return File.Exists(paths.LaunchedContainers) &&
                   File.ReadAllLines(paths.LaunchedContainers)
                       .Select(static line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                       .Any(parts => parts.Length >= 2 && string.Equals(parts[1], launchId, StringComparison.OrdinalIgnoreCase) && parts[0] is not ("starting" or "refused"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>How long the remembered runner's removal may take, so the stopped containers on other engines, and the reports, still get their turn.</summary>
    private static readonly TimeSpan RemovalBudget = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Before a running launched container is stopped, asks Offloadr once, briefly, to release it:
    /// granted, nothing more is assigned to it while it stops. Its answer does not change the stop.
    /// </summary>
    private static async Task<bool> TryReleaseAsync(
        ContainerInfo container,
        Func<StoppedLaunch, CancellationToken, Task<bool>>? askRelease,
        ConcurrentDictionary<string, LaunchCredentials> startedWith,
        Func<StoppedLaunch, string, Task> note,
        CancellationToken cancellationToken)
    {
        if (askRelease is null ||
            !container.Labels.TryGetValue(RunnerSupervisor.LaunchIdLabel, out var launchId) ||
            !container.Labels.TryGetValue(RunnerSupervisor.LaunchTargetLabel, out var launchTarget))
        {
            return false;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ReleaseBudget);
        try
        {
            var asked = new StoppedLaunch(launchId, launchTarget, startedWith.GetValueOrDefault(launchId));
            await note(asked, "asking").ConfigureAwait(false);
            if (!await askRelease(asked, budget.Token).ConfigureAwait(false))
            {
                return false;
            }

            await note(asked, "released").ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static readonly TimeSpan ReleaseBudget = TimeSpan.FromSeconds(2);

    /// <summary>The state noted for a launched container, or null with no note.</summary>
    private static string? NotedState(SupervisorPaths paths, string launchId)
    {
        try
        {
            return !File.Exists(paths.LaunchedContainers)
                ? null
                : File.ReadAllLines(paths.LaunchedContainers)
                    .Select(static line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .FirstOrDefault(parts => parts.Length >= 2 && string.Equals(parts[1], launchId, StringComparison.OrdinalIgnoreCase))?[0];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Notes that the release of a launched container is being asked, or was granted. A granted one stays
    /// so, and is noted even if the app kept no record of that container.
    /// </summary>
    private static async Task NoteStateAsync(SupervisorPaths paths, StoppedLaunch launch, string state, ContainerEngineSelection.Selected engine)
    {
        try
        {
            var lines = File.Exists(paths.LaunchedContainers)
                ? (await File.ReadAllLinesAsync(paths.LaunchedContainers, CancellationToken.None).ConfigureAwait(false)).ToList()
                : [];
            var index = lines.FindIndex(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 4 } parts &&
                                                string.Equals(parts[1], launch.LaunchId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                var parts = lines[index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts[0] is "released" or "gone")
                {
                    return;
                }

                parts[0] = state;
                lines[index] = string.Join(' ', parts);
            }
            else if (state == "released")
            {
                lines.Add($"{state} {launch.LaunchId} {launch.Target} {ContainerEngineSelection.Describe(engine)}");
            }
            else
            {
                return;
            }

            await ReplaceAsync(paths.LaunchedContainers, lines, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: its agent was fenced by the grant, and exits by itself.
        }
    }

    /// <summary>Drops the note of a launched container Offloadr released.</summary>
    private static async Task ForgetNotedAsync(SupervisorPaths paths, string launchId, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(paths.LaunchedContainers))
            {
                return;
            }

            var lines = await File.ReadAllLinesAsync(paths.LaunchedContainers, cancellationToken).ConfigureAwait(false);
            var kept = lines.Where(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is not { Length: >= 2 } parts ||
                                           !string.Equals(parts[1], launchId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (kept.Count != lines.Length)
            {
                await ReplaceAsync(paths.LaunchedContainers, kept, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a report for a released container is answered as released again.
        }
    }

    /// <summary>How long telling Offloadr may take: the installer allows the whole cleanup half a minute.</summary>
    private static readonly TimeSpan ReportBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A launched container was stopped without Offloadr releasing it: noted as gone in
    /// <c>launched-containers</c> (so a later start of the app reports it), and told to Offloadr now,
    /// briefly, when that is possible. Only one Offloadr still counts on: one it released, or was told
    /// about, is no longer noted there.
    /// </summary>
    private static async Task NoteGoneAsync(
        SupervisorPaths paths,
        StoppedLaunch stopped,
        Func<StoppedLaunch, CancellationToken, Task<bool>>? reportGone,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(paths.LaunchedContainers))
            {
                return;
            }

            // state, launch, target, engine, then the executor instance if known.
            var lines = (await File.ReadAllLinesAsync(paths.LaunchedContainers, cancellationToken).ConfigureAwait(false)).ToList();
            var index = lines.FindIndex(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 4 } parts &&
                                                string.Equals(parts[1], stopped.LaunchId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return;
            }

            var noted = lines[index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            noted[0] = "gone";
            lines[index] = string.Join(' ', noted);
            await ReplaceAsync(paths.LaunchedContainers, lines, cancellationToken).ConfigureAwait(false);
            if (reportGone is null)
            {
                return;
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(ReportBudget);
            if (await reportGone(stopped, budget.Token).ConfigureAwait(false))
            {
                lines.RemoveAt(index);
                await ReplaceAsync(paths.LaunchedContainers, lines, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Best effort: the note, if it was written, has the next start of the app report it.
        }
    }

    /// <summary>
    /// Removes each stopped runner container noted in leftover-containers, on its own engine, if this copy
    /// of the app started it; never by force. The note keeps those it could not remove. All at the same time: an
    /// engine that hangs must not cost the containers on the others their turn.
    /// </summary>
    private static async Task<bool> RemoveStoppedLeftoversAsync(SupervisorPaths paths, IProcessRunner processes, string ownerId, CancellationToken cancellationToken)
    {
        string[] lines;
        try
        {
            lines = File.Exists(paths.LeftoverContainers) ? await File.ReadAllLinesAsync(paths.LeftoverContainers, cancellationToken).ConfigureAwait(false) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        async Task<(bool Removed, bool Keep)> RemoveAsync(string line)
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || ContainerEngineSelection.Read(parts[0]) is not { } leftoverEngine)
            {
                return (false, false);
            }

            var selection = new ContainerEngineSelection(statePath: null);
            selection.SelectExactly(leftoverEngine);
            IContainerEngine engine = new DockerCliContainerEngine(processes, () => selection.Executable, () => selection.Pin);
            try
            {
                var container = await engine.InspectContainerAsync(parts[1], cancellationToken).ConfigureAwait(false);
                if (container is null || !RunnerSupervisor.IsOwnedBy(container, ownerId))
                {
                    // Gone, or not this app's any more: nothing of ours to remove there.
                    return (false, false);
                }

                var removed = !container.Running && await engine.RemoveStoppedAsync(parts[1], cancellationToken).ConfigureAwait(false);
                return (removed, !removed);
            }
            catch (Exception ex) when (ex is ContainerEngineException or ProcessStartException or OperationCanceledException)
            {
                // That engine does not answer now, or not in time: kept in the note.
                return (false, true);
            }
        }

        var noted = lines.Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        var done = await Task.WhenAll(noted.Select(RemoveAsync)).ConfigureAwait(false);
        var kept = noted.Where((_, index) => done[index].Keep).ToList();
        var removed = done.Any(result => result.Removed);

        try
        {
            await ReplaceAsync(paths.LeftoverContainers, kept, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next start of the app tries the rest again.
        }

        return removed;
    }

    /// <summary>How long the runner may take to shut down cleanly before it is killed.</summary>
    public static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(10);
}
