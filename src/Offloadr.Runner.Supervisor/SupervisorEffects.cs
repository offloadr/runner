using Microsoft.Extensions.Logging;
using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hardware;
using Offloadr.Runner.Supervisor.Launcher;
using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Processes;
using Offloadr.Runner.Supervisor.Settings;
using Selected = Offloadr.Runner.Supervisor.Containers.ContainerEngineSelection.Selected;

namespace Offloadr.Runner.Supervisor;

/// <summary>
/// Everything the supervisor asks of Docker and Offloadr that takes time. Each method is one job: it
/// is given what it needs as values, runs on the engine it is told, and brings back what it found.
/// It holds none of the supervisor's state and cannot reach it, so nothing here decides on a value
/// that may have changed since: deciding is the supervisor loop's, with the result in hand.
/// </summary>
internal sealed class SupervisorEffects(
    IContainerEngine engine,
    ContainerEngineSelection selection,
    ILaunchPlanProvider plans,
    IHostGpuProbe gpus,
    ILauncherLink link,
    SupervisorOptions options,
    TimeProvider time,
    ILogger logger,
    string ownerId,
    Action<string> log,
    Action<string, bool> seen)
{
    private bool IsOwned(ContainerInfo container) => RunnerSupervisor.IsOwnedBy(container, ownerId);

    /// <summary>The exact container that was checked, by ID: the name may pass to another container meanwhile.</summary>
    private string Target(ContainerInfo container)
        => string.IsNullOrWhiteSpace(container.Id) ? options.ContainerName : container.Id;

    /// <summary>
    /// True unless the container has another ID than <paramref name="id"/> (a container that took the
    /// name meanwhile), or, while that ID is unknown, another run label than <paramref name="attempt"/>.
    /// Unknown IDs never count against it.
    /// </summary>
    internal static bool IsContainer(ContainerInfo container, string? id, string? attempt)
    {
        if (id is null)
        {
            return attempt is null ||
                   (container.Labels.TryGetValue(RunnerSupervisor.LaunchLabel, out var label) && string.Equals(label, attempt, StringComparison.Ordinal));
        }

        return NormalizeContainerId(container.Id) is not { } found ||
               found.StartsWith(id, StringComparison.OrdinalIgnoreCase) ||
               id.StartsWith(found, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The last line the creation printed (the ID), or null when there is none.</summary>
    internal static string? NormalizeContainerId(string? output)
        => output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() is { Length: >= 12 } id ? id : null;

    /// <param name="Running">It runs: it started since it was looked at, and is kept, to be supervised.</param>
    /// <param name="Started">
    /// The engine saw it run since it was looked at. That stands whatever is learned next: it is no
    /// container that never started, whatever it was before.
    /// </param>
    /// <param name="Unseen">The engine saw it run and then would not describe it: taken as running, and looked at again.</param>
    /// <param name="Kept">It does not run, and the engine would not remove it: why.</param>
    /// <param name="Created">
    /// Kept, and still only created: an engine that was asked to start it, and never said it refused, may
    /// start it yet. That it serves nothing is known only once it is removed, or seen exited.
    /// </param>
    private readonly record struct Removal(ContainerInfo? Running, bool Started = false, bool Unseen = false, ContainerEngineException? Kept = null, bool Created = false);

    /// <summary>How long a removal that was begun may still take once it is cancelled.</summary>
    private static readonly TimeSpan CancelledRemovalGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Removes a stopped container, seen through for a moment when it is cancelled meanwhile (a Stop, the app
    /// closing): cut short, the engine may remove it all the same, and what was seen of it, which nothing
    /// shows afterwards, would be lost with the answer.
    /// </summary>
    private async Task<bool> RemoveStoppedSeenThroughAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var limit = new CancellationTokenSource();
        await using var cancelled = cancellationToken.Register(() => limit.CancelAfter(CancelledRemovalGrace));
        try
        {
            return await engine.RemoveStoppedAsync(target, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>Hands over, at once and by its launch, that the engine shows a container as one that ran, or as one that was only created.</summary>
    private void Seen(ContainerInfo container, bool ran)
    {
        if (container.Labels.TryGetValue(RunnerSupervisor.LaunchIdLabel, out var launchId))
        {
            seen(launchId, ran);
        }
    }

    /// <summary>
    /// Removes a stopped runner container (it still holds the runner key in its environment), never by
    /// force: one started again since it was looked at may serve a session without a release, and is
    /// returned instead, to be supervised. The engine refuses only a container it sees running, so a
    /// refusal is kept as evidence that it started, also when the engine then says nothing more. One it
    /// kept for another reason is looked at again: what it is then says whether it started meanwhile.
    /// Throws only when the engine does not say what became of it.
    /// </summary>
    private async Task<Removal> RemoveUnlessStartedAgainAsync(ContainerInfo container, CancellationToken cancellationToken)
    {
        // What the engine shows it as (one that ran, or was only created) is handed over at once: that stands
        // whatever becomes of the rest.
        var started = false;
        void Ran()
        {
            if (!started)
            {
                started = true;
                Seen(container, ran: true);
            }
        }

        Seen(container, ran: !container.NeverStarted);

        var assumedRunning = container with { Running = true, ExitCode = null, NeverStarted = false };
        for (var attempt = 0; ; attempt++)
        {
            ContainerEngineException? kept = null;
            try
            {
                if (await RemoveStoppedSeenThroughAsync(Target(container), cancellationToken).ConfigureAwait(false))
                {
                    return new Removal(null, started);
                }

                // The engine refuses only a container it sees running: that it started stands from here on.
                Ran();
            }
            catch (ContainerEngineException ex)
            {
                // Not removed, for another reason. It may have started meanwhile, and stopped again before
                // the engine could say that it ran: as it is now, it tells.
                kept = ex;
            }

            ContainerInfo? now;
            try
            {
                now = await engine.InspectContainerAsync(Target(container), cancellationToken).ConfigureAwait(false);
            }
            catch (ContainerEngineException ex) when (kept is null)
            {
                // Seen running, and the engine says nothing more: taken as running, and looked at again.
                logger.LogWarning("Could not look at a runner container that started again: {Error}", ex.Summary);
                return new Removal(assumedRunning, Started: true, Unseen: true);
            }

            // (After a removal that failed, an engine that does not answer this either says nothing: thrown.)
            if (now is null)
            {
                // Gone since (and if it was seen running: it ran).
                return new Removal(null, started);
            }

            if (now.Running)
            {
                Ran();
                return new Removal(now, Started: true);
            }

            if (container.NeverStarted && !now.NeverStarted)
            {
                Ran();
            }

            if (kept is not null)
            {
                // Seen stopped after the engine would not remove it: kept there, to be removed later.
                // (Still only created, it is for the caller to say whether a start of it may be under way.)
                return new Removal(null, started, Kept: kept, Created: now.MayStartYet && !started);
            }

            if (attempt > 0)
            {
                // Running at each removal and stopped at each look: taken as running, and looked at again.
                return new Removal(assumedRunning, Started: true, Unseen: true);
            }

            // Stopped again after it ran: it still holds the runner key, so its removal is tried once more.
        }
    }

    /// <summary>
    /// A stopped container of a launch that a look removed, as it was seen before: what it was started
    /// for, and whether it ever started. The evidence goes with it, so the record of that launch is
    /// settled by what was seen, not by its absence afterwards.
    /// </summary>
    /// <param name="NeverStarted">It was created and never started: it ran nothing, so no exit is reported for it.</param>
    public sealed record RemovedLaunch(string LaunchId, string? Target, bool NeverStarted);

    private static RemovedLaunch? RemovedOf(ContainerInfo container, bool startedSince = false)
        => !container.Running && container.Labels.TryGetValue(RunnerSupervisor.LaunchIdLabel, out var launchId)
            ? new RemovedLaunch(launchId, container.Labels.TryGetValue(RunnerSupervisor.LaunchTargetLabel, out var target) ? target : null, container.NeverStarted && !startedSince)
            : null;

    /// <summary>
    /// Hands over what was removed as soon as it is: that stands whatever becomes of the rest of the look,
    /// and of the procedure that asked for it (a Stop, the app closing).
    /// </summary>
    private static void NoteRemoved(RemovedLaunch? removed, Action<RemovedLaunch> onRemoved)
    {
        if (removed is not null)
        {
            onRemoved(removed);
        }
    }

    /// <summary>What an error's log line says: an engine's own output, which its message holds, may name what must not be stored (a signed URL).</summary>
    internal static string Told(Exception error) => error is ContainerEngineException engineError ? engineError.Summary : error.GetType().Name;

    // The view's own GPU check

    /// <summary>The Status page's check: Docker missing or not answering is a failed check to show, not an error.</summary>
    public async Task<GpuAccessCheck> CheckGpuAccessForViewAsync(string? image, CancellationToken cancellationToken)
    {
        try
        {
            return image is null || await engine.InspectImageAsync(image, cancellationToken).ConfigureAwait(false) is null
                ? new GpuAccessCheck(false, "Start the runner once so its image is downloaded, then check again.")
                : await engine.CheckGpuAccessAsync(image, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ContainerEngineException or ProcessStartException)
        {
            return new GpuAccessCheck(false, ex.Message);
        }
    }

    public async Task<GpuAccessCheck?> CheckGpuAccessQuietlyAsync(string? image, Selected on, CancellationToken cancellationToken)
    {
        try
        {
            using (selection.Use(on))
            {
                return await CheckGpuAccessForViewAsync(image, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The automatic GPU check failed: {Error}", Told(ex));
            return null;
        }
    }

    // The launch plan

    /// <param name="Plan">Null when it could not be had.</param>
    /// <param name="Rejected">Offloadr refused the runner key it was asked with.</param>
    public sealed record PlanAnswer(LaunchPlan? Plan, bool Rejected, (string Origin, string Key) Credentials);

    public async Task<PlanAnswer> GetPlanAsync(RunnerSettings settings, CancellationToken cancellationToken)
    {
        var credentials = (settings.OffloadrApiOrigin.Trim(), settings.RunnerKey?.Trim() ?? string.Empty);
        try
        {
            return new PlanAnswer(await plans.GetAsync(settings, cancellationToken).ConfigureAwait(false), false, credentials);
        }
        catch (LaunchPlanRejectedException)
        {
            // Noted, so a first start says so before it downloads anything.
            logger.LogWarning("The control plane rejected the runner key in Settings for the launch plan.");
            return new PlanAnswer(null, true, credentials);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Could not refresh the launch plan: {Error}", Told(ex));
            return new PlanAnswer(null, false, credentials);
        }
    }

    // Probing

    /// <param name="Pin">The context the engine's commands reach now, read when <paramref name="Engine"/> answered and none was pinned.</param>
    public sealed record Probed(ContainerEngineInfo Engine, HostGpuReport Gpus, ContainerEnginePin? Pin);

    /// <summary>Asks whether <paramref name="on"/> answers, lists this computer's GPUs, and reads the context when asked to.</summary>
    public async Task<Probed> ProbeAsync(Selected on, bool readContext, CancellationToken cancellationToken)
    {
        using (selection.Use(on))
        {
            var info = await engine.ProbeAsync(cancellationToken).ConfigureAwait(false);
            // The GPUs listed again too: one may be gone (after sleep, say), and heartbeats name them.
            var listed = await gpus.ProbeAsync(cancellationToken).ConfigureAwait(false);
            var pin = info.Available && readContext && on.Context is null
                ? await engine.CurrentContextAsync(cancellationToken).ConfigureAwait(false)
                : null;
            return new Probed(info, listed, pin);
        }
    }

    /// <summary>Asks only whether <paramref name="on"/> answers.</summary>
    public async Task<ContainerEngineInfo> ProbeEngineAsync(Selected on, CancellationToken cancellationToken)
    {
        using (selection.Use(on))
        {
            return await engine.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Looking for a runner started before

    public enum Remembered
    {
        /// <summary>The remembered engine answered and holds no runner of this app by that name or ID.</summary>
        None,

        /// <summary>A runner of this app runs there: <see cref="RememberedLook.Container"/>.</summary>
        Found,

        /// <summary>The remembered engine does not answer, so the runner may still run there.</summary>
        Unreachable,
    }

    public sealed record RememberedLook(
        Remembered Outcome,
        ContainerEngineInfo? Engine = null,
        HostGpuReport? Gpus = null,
        ContainerInfo? Container = null);

    /// <summary>
    /// After a crash, looks for the runner on the engine it was started on. A stopped one there still
    /// holds the runner key in its environment and goes; one started again meanwhile is found instead.
    /// </summary>
    /// <param name="onRemoved">Told at once of a container of a launch that was removed, as it was seen before.</param>
    public async Task<RememberedLook> LookForRememberedAsync(Selected on, string? rememberedId, Action<RemovedLaunch> onRemoved, CancellationToken cancellationToken)
    {
        try
        {
            using (selection.Use(on))
            {
                var info = await engine.ProbeAsync(cancellationToken).ConfigureAwait(false);
                if (!info.Available)
                {
                    // Not answering is no proof the runner is gone (a remote engine, or one restarting).
                    return new RememberedLook(Remembered.Unreachable);
                }

                // By the saved ID first: the runner may have been renamed before the crash.
                var container = (rememberedId is not null
                                    ? await engine.InspectContainerAsync(rememberedId, cancellationToken).ConfigureAwait(false)
                                    : null)
                                ?? await engine.InspectContainerAsync(options.ContainerName, cancellationToken).ConfigureAwait(false);
                if (container is not null && IsOwned(container) && !container.Running)
                {
                    var stopped = container;
                    var after = await RemoveUnlessStartedAgainAsync(stopped, cancellationToken).ConfigureAwait(false);
                    if (after.Kept is { } kept)
                    {
                        throw kept;
                    }

                    container = after.Running;
                    if (container is null)
                    {
                        NoteRemoved(RemovedOf(stopped, after.Started), onRemoved);
                    }
                }

                if (container is { Running: true } && IsOwned(container))
                {
                    var listed = await gpus.ProbeAsync(cancellationToken).ConfigureAwait(false);
                    return new RememberedLook(Remembered.Found, info, listed, container);
                }

                return new RememberedLook(Remembered.None, info);
            }
        }
        catch (ContainerEngineException ex)
        {
            logger.LogWarning("Could not look for the runner on the engine it was started on: {Error}", Told(ex));
            return new RememberedLook(Remembered.Unreachable);
        }
    }

    /// <param name="Holder">What has the runner's name now, after a stopped runner of this app there was removed.</param>
    /// <param name="Foreign">It is not this app's to adopt or remove: another's container, or one monitoring rejected.</param>
    public sealed record NameLook(ContainerInfo? Holder, bool Foreign);

    /// <summary>
    /// Looks at what has the runner's name. A stopped runner of this app goes (it still holds the runner
    /// key), never by force; one that runs, also one started again as it was removed, is brought back.
    /// </summary>
    /// <param name="onRemoved">Told at once of a container of a launch that was removed, as it was seen before.</param>
    public async Task<NameLook> LookAtNameAsync(Selected on, string? rejectedId, Action<RemovedLaunch> onRemoved, CancellationToken cancellationToken)
    {
        using (selection.Use(on))
        {
            var left = await engine.InspectContainerAsync(options.ContainerName, cancellationToken).ConfigureAwait(false);
            if (left is null)
            {
                return new NameLook(null, false);
            }

            if (!IsOwned(left) || IsRejected(left, rejectedId))
            {
                return new NameLook(left, true);
            }

            if (left.Running)
            {
                return new NameLook(left, false);
            }

            var after = await RemoveUnlessStartedAgainAsync(left, cancellationToken).ConfigureAwait(false);
            if (after.Kept is { } kept)
            {
                throw kept;
            }

            if (after.Running is null)
            {
                NoteRemoved(RemovedOf(left, after.Started), onRemoved);
            }

            return new NameLook(after.Running, false);
        }
    }

    internal static bool IsRejected(ContainerInfo container, string? rejectedId)
        => rejectedId is not null &&
           NormalizeContainerId(container.Id) is { } id &&
           (id.StartsWith(rejectedId, StringComparison.OrdinalIgnoreCase) || rejectedId.StartsWith(id, StringComparison.OrdinalIgnoreCase));

    /// <param name="Missing">It does not run on its engine: it is gone.</param>
    /// <param name="Running">It runs there (under another name, or started again): to be supervised.</param>
    /// <param name="Leftover">It is stopped and the engine would not remove it: removed there later.</param>
    /// <param name="NeverStarted">It was created and never started: it ran nothing, and is gone without an exit to report.</param>
    /// <param name="Found">A container of that launch was seen there (and, unless it runs, is gone now): not one that never showed.</param>
    /// <param name="StillCreated">
    /// It is the leftover, and still only created: if its start was asked for, the engine may start it
    /// yet, and nothing of it is settled until it is removed, or seen exited.
    /// </param>
    public sealed record LaunchedLook(
        string LaunchId,
        Selected Engine,
        bool Missing,
        ContainerInfo? Running = null,
        string? Leftover = null,
        bool NeverStarted = false,
        bool Found = false,
        bool StillCreated = false);

    /// <summary>
    /// Looks up each container this app started by its launch, on the engine it was started on, whatever
    /// it is called now: one renamed may still serve a session, only one that does not run is gone, and a
    /// stopped one goes (never by force). An engine that does not answer proves nothing: with
    /// <paramref name="tolerant"/> that candidate is left out, otherwise the failure is the caller's to weigh.
    /// Each look is handed to <paramref name="onLooked"/> as soon as it is made: what it removed leaves
    /// nothing for another look to see, so it stands whatever becomes of the rest (an engine that stops
    /// answering, a Stop that ends this).
    /// </summary>
    public async Task<IReadOnlyList<LaunchedLook>> LookForLaunchedAsync(
        IReadOnlyList<(string LaunchId, Selected Engine)> candidates,
        bool tolerant,
        Action<LaunchedLook> onLooked,
        CancellationToken cancellationToken)
    {
        var looked = new List<LaunchedLook>();
        void Looked(LaunchedLook look)
        {
            looked.Add(look);
            onLooked(look);
        }

        foreach (var (launchId, on) in candidates)
        {
            try
            {
                using (selection.Use(on))
                {
                    var found = await engine.FindByLabelAsync(RunnerSupervisor.LaunchIdLabel, launchId, cancellationToken).ConfigureAwait(false);
                    if (found is { Running: true } && IsOwned(found))
                    {
                        Looked(new LaunchedLook(launchId, on, Missing: false, Running: found, Found: true));
                        continue;
                    }

                    if (found is null || !IsOwned(found))
                    {
                        Looked(new LaunchedLook(launchId, on, Missing: true));
                        continue;
                    }

                    // Stopped, found by its launch alone (renamed): it still holds the runner key, so it goes
                    // too, never by force, or is kept for removal later. (An engine that then says nothing
                    // of it proves nothing: with the rest of this look, it is the caller's to weigh.)
                    var after = await RemoveUnlessStartedAgainAsync(found, cancellationToken).ConfigureAwait(false);
                    Looked(after.Running is { } startedAgain
                        ? new LaunchedLook(launchId, on, Missing: false, Running: startedAgain, Found: true)
                        : new LaunchedLook(launchId, on, Missing: true, Leftover: after.Kept is null ? null : Target(found), NeverStarted: found.NeverStarted && !after.Started, Found: true, StillCreated: after.Created));
                }
            }
            catch (ContainerEngineException ex) when (tolerant)
            {
                // Not known now: looked for again at the next check.
                logger.LogWarning("Could not look for a runner container this app started: {Error}", ex.Summary);
            }
        }

        return looked;
    }

    /// <param name="Resolved">Removed, or not there any more.</param>
    /// <param name="Running">Started again outside the app: never removed by force, and looked at again.</param>
    public sealed record Maintenance(
        IReadOnlyList<(Selected Engine, string Container)> Resolved,
        IReadOnlyList<(Selected Engine, string Container)> Running);

    /// <summary>Tries again to remove each stopped container an engine kept, then looks for the containers whose fate is unknown.</summary>
    public async Task<Maintenance> MaintainAsync(
        IReadOnlyList<(Selected Engine, string Container)> leftovers,
        IReadOnlyList<(string LaunchId, Selected Engine)> unresolved,
        Action<LaunchedLook> onLooked,
        CancellationToken cancellationToken)
    {
        var resolved = new List<(Selected Engine, string Container)>();
        var running = new List<(Selected Engine, string Container)>();
        foreach (var leftover in leftovers)
        {
            try
            {
                ContainerInfo? startedAgain;
                using (selection.Use(leftover.Engine))
                {
                    var container = await engine.InspectContainerAsync(leftover.Container, cancellationToken).ConfigureAwait(false);
                    if (container is null || container.Running)
                    {
                        startedAgain = container;
                    }
                    else
                    {
                        var after = await RemoveUnlessStartedAgainAsync(container, cancellationToken).ConfigureAwait(false);
                        if (after.Kept is { } kept)
                        {
                            throw kept;
                        }

                        startedAgain = after.Running;
                    }
                }

                (startedAgain is null ? resolved : running).Add(leftover);
            }
            catch (ContainerEngineException ex)
            {
                // Its engine may not answer now: tried again at the next check, and at the next start.
                logger.LogWarning("Could not remove the stopped runner container yet: {Error}", ex.Summary);
            }
        }

        await LookForLaunchedAsync(unresolved, tolerant: true, onLooked, cancellationToken).ConfigureAwait(false);
        return new Maintenance(resolved, running);
    }

    // Reports to Offloadr

    public sealed record GoneAnswer(string LaunchId, bool Taken, string Message);

    /// <summary>
    /// Reports each container as gone, for the runner it was started for. Brings back those Offloadr's
    /// release handler answered; the others are kept and sent again.
    /// </summary>
    public async Task<IReadOnlyList<GoneAnswer>> ReportGoneAsync(
        IReadOnlyList<(string LaunchId, RunnerSupervisor.LaunchedContainer Container, RunnerSettings ReportAs)> pending,
        CancellationToken cancellationToken)
    {
        // Each on its own, at the same time: an Offloadr that does not answer (an earlier address, say)
        // must not use up the time of the others, whose sessions end sooner the sooner it is told.
        async Task<GoneAnswer?> TellAsync(string launchId, RunnerSupervisor.LaunchedContainer container, RunnerSettings reportAs)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.StopReleaseTimeout);
            ReleaseResult result;
            try
            {
                result = await link.ReleaseAsync(launchId, container.Executor, container.Target, timeout.Token, containerExited: true, reportAs: reportAs)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            // Taken up by Offloadr's release handler. Unreachable, or the call refused (as for an
            // authentication problem), it is kept and sent again.
            return result.Reached && result.Answered ? new GoneAnswer(launchId, result.Released, result.Message) : null;
        }

        var told = await Task.WhenAll(pending.Select(entry => TellAsync(entry.LaunchId, entry.Container, entry.ReportAs))).ConfigureAwait(false);
        return [.. told.OfType<GoneAnswer>()];
    }

    /// <param name="Leftover">It stopped, and the engine would not remove it: its target, removed there later.</param>
    public sealed record StrayStopped(string? Leftover = null);

    /// <summary>
    /// Stops and removes a runner container of this app that runs beside the supervised one (on an engine
    /// the person moved on from). Throws when its engine does not answer: it stays known, and is looked
    /// for again.
    /// </summary>
    /// <param name="onStopped">Told at once when the engine confirmed the stop: a cancellation then ends only the removal.</param>
    public async Task<StrayStopped> StopStrayAsync(Selected on, ContainerInfo container, TimeSpan grace, Action onStopped, CancellationToken cancellationToken)
    {
        using var onEngine = selection.Use(on);
        await engine.StopAsync(Target(container), grace, cancellationToken).ConfigureAwait(false);
        onStopped();
        try
        {
            await engine.RemoveAsync(Target(container), cancellationToken).ConfigureAwait(false);
            return new StrayStopped();
        }
        catch (OperationCanceledException)
        {
            // Out of time for the removal: it is stopped, and removed later.
            return new StrayStopped(Target(container));
        }
        catch (ContainerEngineException ex)
        {
            logger.LogWarning("Could not remove a stopped runner container: {Error}", ex.Summary);
            return new StrayStopped(Target(container));
        }
    }

    /// <summary>Asks Offloadr once whether a container may be stopped; null when it did not answer within <paramref name="within"/>.</summary>
    /// <param name="askAs">Set when no launcher runs: asked with these settings' address and key, registering nothing.</param>
    public async Task<ReleaseResult?> ReleaseAsync(
        string launchId,
        string? executor,
        string target,
        TimeSpan? within,
        LaunchRequest? onlyFor,
        CancellationToken sendUnless,
        CancellationToken cancellationToken,
        RunnerSettings? askAs = null,
        long? streamDrops = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (within is { } limit)
        {
            timeout.CancelAfter(limit);
        }

        try
        {
            return await link.ReleaseAsync(launchId, executor, target, timeout.Token, onlyFor: onlyFor, sendUnless: sendUnless, reportAs: askAs, streamDrops: streamDrops)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Submits an outcome, recorded by the link before this returns; it resends it by itself and never throws for a failed send.</summary>
    public void Report(LaunchReport report, CancellationToken stopToken) => _ = ReportQuietlyAsync(report, stopToken);

    /// <summary>Sends a launch's progress.</summary>
    public Task ReportProgressAsync(LaunchReport report, CancellationToken stopToken) => ReportQuietlyAsync(report, stopToken);

    private async Task ReportQuietlyAsync(LaunchReport report, CancellationToken stopToken)
    {
        try
        {
            await link.ReportAsync(report, stopToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not report launch {LaunchId} as {Phase}: {Error}", report.LaunchId, report.Phase, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Waits until an outcome already submitted has reached Offloadr (its link resends it until then). One
    /// the link did not record (a Stop or shutdown kept it back) is not submitted here: there is nothing to wait for.
    /// </summary>
    public async Task<bool> DeliverAsync(LaunchReport outcome, TimeSpan within, CancellationToken cancellationToken)
    {
        if (link.OutcomeOf(outcome.LaunchId) is null)
        {
            return true;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(within);
        try
        {
            await link.ReportAsync(outcome, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (ex is not OperationCanceledException)
            {
                logger.LogWarning("Launch {LaunchId}: waiting for its outcome to be delivered failed: {Error}", outcome.LaunchId, ex.GetType().Name);
            }
        }

        return true;
    }

    // The launcher link

    /// <param name="Superseded">The run ended because another launcher took the runner over.</param>
    /// <param name="Accepted">Offloadr accepted a registration of this run.</param>
    /// <param name="Ended">It was ended from here (a Stop, new settings), not by Offloadr refusing it.</param>
    public sealed record LinkEnded(bool Superseded, bool Accepted, bool Ended);

    /// <summary>
    /// Runs the launcher link until it is cancelled or Offloadr refuses this launcher for good. With
    /// <paramref name="takeOver"/> (the person pressed Start), a run that learns from its first heartbeat that
    /// another app had taken the runner over runs once more, as a new instance, which takes it back. One
    /// that is taken over while it runs stays off, so two computers do not keep taking it from each other.
    /// </summary>
    /// <param name="unlessAccepted">
    /// Raised by a Stop, or by Settings naming another address or key: a run Offloadr has not accepted
    /// yet ends at once then (registering after it could take that runner from another computer). One
    /// accepted goes on, to ask for a release before its container stops.
    /// </param>
    public async Task<LinkEnded> RunLinkAsync(
        RunnerSettings settings,
        Func<LauncherProfile> profile,
        bool takeOver,
        CancellationToken unlessAccepted,
        CancellationToken stop)
    {
        var accepted = false;
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var cancellationToken = run.Token;
        var early = false;
        using var fenced = unlessAccepted.Register(() =>
        {
            if (!Volatile.Read(ref accepted))
            {
                Volatile.Write(ref early, true);
                try
                {
                    run.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The run ended meanwhile.
                }
            }
        });
        void Watch(object? sender, EventArgs e)
        {
            if (link.State.Connection == LauncherConnection.Online)
            {
                Volatile.Write(ref accepted, true);
            }
        }

        link.StateChanged += Watch;
        try
        {
            await link.RunAsync(settings, profile, cancellationToken).ConfigureAwait(false);
            if (takeOver && !cancellationToken.IsCancellationRequested && !Volatile.Read(ref accepted) &&
                link.State.Connection == LauncherConnection.Superseded)
            {
                log("supervisor: another app had taken over this runner; taking it back, as asked.");
                await link.RunAsync(settings, profile, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError("The launcher link failed: {Error}", ex.GetType().Name);
        }
        catch (OperationCanceledException)
        {
            // Ended as asked.
        }
        finally
        {
            link.StateChanged -= Watch;
        }

        return new LinkEnded(link.State.Connection == LauncherConnection.Superseded, Volatile.Read(ref accepted), Volatile.Read(ref early) || stop.IsCancellationRequested);
    }

    /// <summary>The agent's status, read briefly: null when it has none, or could not be read within <paramref name="budget"/>.</summary>
    public async Task<RunnerStatusSnapshot?> ReadStatusWithinAsync(Selected on, string container, TimeSpan budget, CancellationToken cancellationToken)
    {
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limited.CancelAfter(budget);
        try
        {
            return await ReadStatusAsync(on, container, limited.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ContainerEngineException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Not read in time: asked with the launch alone.
            return null;
        }
    }

    // Adoption

    /// <param name="StoppedReleased">It was released before the app could stop it: stopped and removed now, not adopted.</param>
    /// <param name="KeyPending">The key it was started with could not be read yet.</param>
    /// <param name="Origin">The Offloadr address it was started with, read with its key.</param>
    public sealed record Adoption(ContainerEnginePin? Pin, bool StoppedReleased, PlanAnswer? Plan, string? Key, bool KeyPending, string? Origin = null);

    /// <summary>
    /// What adopting a running container takes of the engine and Offloadr: the context to pin, the plan
    /// its template is named in, and, when it was started with other settings, the key in its environment
    /// (so its log can be shown redacted). One Offloadr already released is stopped instead.
    /// </summary>
    public async Task<Adoption> PrepareAdoptionAsync(
        Selected on,
        ContainerInfo container,
        bool released,
        bool readKey,
        RunnerSettings settings,
        TimeSpan stopGrace,
        CancellationToken cancellationToken)
    {
        ContainerEnginePin? pin = null;
        if (on.Context is null)
        {
            using (selection.Use(on))
            {
                pin = await engine.CurrentContextAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Pinned from here on, as the supervisor pins it once this is recorded.
        using (selection.Use(ContainerEngineSelection.WithContext(on, pin ?? on.Context)))
        {
            if (released)
            {
                await engine.StopAsync(Target(container), stopGrace, cancellationToken).ConfigureAwait(false);
                await engine.RemoveAsync(Target(container), cancellationToken).ConfigureAwait(false);
                return new Adoption(pin, true, null, null, false);
            }

            var plan = await GetPlanAsync(settings, cancellationToken).ConfigureAwait(false);
            if (!readKey)
            {
                return new Adoption(pin, false, plan, null, false);
            }

            try
            {
                var key = await engine.ReadEnvironmentVariableAsync(Target(container), "RUNNER_SECRET", cancellationToken).ConfigureAwait(false);
                var origin = await engine.ReadEnvironmentVariableAsync(Target(container), "OFFLOADR_API_GRPC", cancellationToken).ConfigureAwait(false);
                return new Adoption(pin, false, plan, string.IsNullOrWhiteSpace(key) ? null : key.Trim(), false, string.IsNullOrWhiteSpace(origin) ? null : origin.Trim());
            }
            catch (ContainerEngineException ex)
            {
                // The engine did not answer; monitoring reads it again until it does.
                logger.LogWarning("Could not read what the runner that was already running was started with: {Error}", Told(ex));
                return new Adoption(pin, false, plan, null, true);
            }
        }
    }

    // The GPU check

    /// <param name="Candidates">Images to check in if one is here, in order of preference.</param>
    /// <param name="Preferred">The image to download when none is here; null when the plan names none.</param>
    /// <param name="HostGpus">This computer's own list, to tell whether it can describe what containers get.</param>
    public sealed record GpuCheckRequest(
        Selected Engine,
        IReadOnlyList<string> Candidates,
        LaunchTemplate? Preferred,
        bool MayDownload,
        string? ChosenGpu,
        HostGpuReport HostGpus);

    /// <param name="Problem">Why containers cannot be offered on this engine; null when they can.</param>
    /// <param name="Inventory">The GPUs the containers reported, when this computer's own list cannot describe them.</param>
    /// <param name="GpuLost">
    /// The check was answered: containers do not get the GPU. False when it only failed (an engine that did
    /// not answer, a check that never finished), which says nothing of the GPU.
    /// </param>
    public sealed record GpuChecked(
        string? Problem,
        FaultKind Kind,
        GpuAccessCheck? Check = null,
        IReadOnlyList<HostGpu>? Inventory = null,
        string? Image = null,
        bool GpuLost = false,
        string? ImageId = null);

    /// <summary>
    /// Proves containers of an engine reach the GPU, with a runner image this computer already has (or,
    /// when it may, downloads the one for the editor in Settings).
    /// </summary>
    /// <param name="onDownload">Called when a download begins, with the editor's label.</param>
    /// <param name="onProgress">Called as the download goes on.</param>
    /// <param name="onChecking">Called when the download is done and the check itself begins.</param>
    public async Task<GpuChecked> CheckGpuAsync(
        GpuCheckRequest request,
        RunnerImageGate imageGate,
        Action<string> onDownload,
        Action<PullProgress?> onProgress,
        Action onChecking,
        CancellationToken cancellationToken)
    {
        using var onEngine = selection.Use(request.Engine);

        // With each image that is here, until containers of one reach the GPU: an image can need more of
        // the driver than another does. (A check that was not answered is the answer: the others would hang as well.)
        // Each as the exact image it is now: a tag moved meanwhile is not credited with what another image showed.
        string? image = null;
        string? imageId = null;
        GpuChecked? proved = null;
        GpuChecked? refused = null;
        var preferredTried = false;
        foreach (var candidate in request.Candidates)
        {
            if (await engine.InspectImageAsync(candidate, cancellationToken).ConfigureAwait(false) is not { } here)
            {
                continue;
            }

            preferredTried |= string.Equals(candidate, request.Preferred?.Image, StringComparison.Ordinal);
            var exact = RunnerSupervisor.ExactImage(candidate, here);
            proved = await CheckGpuWithAsync(exact, request.ChosenGpu, cancellationToken).ConfigureAwait(false);
            if (proved.Problem is null)
            {
                (image, imageId) = (exact, here.Id);
                break;
            }

            refused ??= proved;
            if (proved.Kind != FaultKind.GpuUnavailable || proved.Check is { Inconclusive: true })
            {
                return proved;
            }
        }

        if (image is null && refused is not null &&
            (preferredTried || !request.MayDownload || request.Preferred is null || !EngineFailureClassifier.ConcernsOneImage(refused.Check?.Output)))
        {
            // No image that is here reaches it. The one for the editor in Settings is downloaded to find out
            // only when the failure is of an image, not of the engine or the driver.
            return refused;
        }

        if (image is null)
        {
            if (request.Preferred is not { } preferred)
            {
                return new GpuChecked("No runner image is available for your editor.", FaultKind.NoImageForEditor);
            }

            if (!request.MayDownload)
            {
                return new GpuChecked("Checking the GPU on this container engine needs a runner image it does not have yet; it is downloaded once the runner container here has stopped.", FaultKind.Unknown);
            }

            // A first start: an image is needed to check the GPU, and the first session needs it anyway.
            image = preferred.Image;
            using var pulling = await imageGate.EnterUseAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                onDownload(preferred.Label);
                await PullCoreAsync(image, onProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (ContainerEngineException ex)
            {
                return new GpuChecked(ex.Message, ex.Kind);
            }

            if (await engine.InspectImageAsync(image, cancellationToken).ConfigureAwait(false) is { } downloaded)
            {
                (image, imageId) = (RunnerSupervisor.ExactImage(image, downloaded), downloaded.Id);
            }

            onChecking();
        }

        if (proved is not { Problem: null })
        {
            proved = await CheckGpuWithAsync(image, request.ChosenGpu, cancellationToken).ConfigureAwait(false);
        }

        if (proved is not { Problem: null, Check: { } check })
        {
            return proved;
        }

        // What this computer lists cannot describe what the engine's containers get (a remote engine,
        // nvidia-smi failing here, or containers seeing other GPUs than it lists): the containers are
        // asked, so the launcher offers what launches get.
        // The GPU chosen in Settings, if any, must be among them: only it is offered.
        IReadOnlyList<HostGpu>? inventory = null;
        var described = RunnerSupervisor.OnlyChosen(RunnerSupervisor.VisibleToContainers(request.HostGpus, check.Output).Gpus, request.ChosenGpu);
        var containersSee = RunnerSupervisor.NamedUuids(check.Output)
            .Where(uuid => request.ChosenGpu is null || string.Equals(uuid, request.ChosenGpu, StringComparison.OrdinalIgnoreCase));
        if (request.Engine.Context is { Remote: true } || described.Count == 0 ||
            containersSee.Any(uuid => !described.Any(gpu => string.Equals(gpu.Uuid, uuid, StringComparison.OrdinalIgnoreCase))))
        {
            // (None listed although they reach the GPU: the listing failed, and is made again.)
            for (var attempt = 1; ; attempt++)
            {
                inventory = await engine.ListContainerGpusAsync(image, cancellationToken).ConfigureAwait(false);
                if (inventory.Count > 0 || attempt == 3)
                {
                    break;
                }

                await Task.Delay(options.MonitorInterval, time, cancellationToken).ConfigureAwait(false);
            }

            if (RunnerSupervisor.OnlyChosen(inventory, request.ChosenGpu).Count == 0)
            {
                // Nothing could be offered: Offloadr allocates by the GPU's name and memory.
                return new GpuChecked("Containers can use the GPU, but its name and memory could not be read on the computer the container engine runs on. Check the NVIDIA driver there.", FaultKind.GpuUnavailable, check);
            }
        }

        // (By its ID: what a launch of that image, however it is named, is compared by.)
        return new GpuChecked(null, FaultKind.Unknown, check, inventory, image, ImageId: imageId);
    }

    /// <summary>Proves containers of <paramref name="image"/>, which is here, reach the GPU on that engine.</summary>
    public async Task<GpuChecked> CheckImageGpuAsync(Selected on, string image, string? chosenGpu, CancellationToken cancellationToken)
    {
        using (selection.Use(on))
        {
            return await CheckGpuWithAsync(image, chosenGpu, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<GpuChecked> CheckGpuWithAsync(string image, string? chosenGpu, CancellationToken cancellationToken)
    {
        var check = await engine.CheckGpuAccessAsync(image, cancellationToken).ConfigureAwait(false);
        if (!check.Succeeded)
        {
            var kind = EngineFailureClassifier.Classify(EngineOperation.Run, check.Output) is var classified and (FaultKind.EngineNotRunning or FaultKind.EnginePermission)
                ? classified
                : FaultKind.GpuUnavailable;
            return new GpuChecked(check.Output, kind, check, GpuLost: !check.Inconclusive && EngineFailureClassifier.SaysNoGpu(check.Output) && !EngineFailureClassifier.ConcernsOneImage(check.Output));
        }

        // `nvidia-smi -L` names each GPU with its UUID: the one chosen must be among them, or the
        // agent would bind the editor to a GPU its container does not have.
        return chosenGpu is { } chosen && !check.Output.Contains(chosen, StringComparison.OrdinalIgnoreCase)
            ? new GpuChecked($"Containers cannot use the GPU chosen in Settings ({chosen}). Choose another GPU under Advanced, or check the NVIDIA driver.", FaultKind.GpuUnavailable, check, GpuLost: true)
            : new GpuChecked(null, FaultKind.Unknown, check, Image: image);
    }

    private async Task PullCoreAsync(string image, Action<PullProgress?> onProgress, CancellationToken cancellationToken)
    {
        var progress = new PullProgressParser();
        onProgress(progress.Current);
        await engine.PullAsync(
            image,
            line =>
            {
                log("pull: " + line);
                if (progress.Accept(line))
                {
                    onProgress(progress.Current);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    // A launch

    /// <param name="Failure">Why the download failed; the copy already here, if any, is in <paramref name="Image"/>.</param>
    public sealed record Pulled(ContainerEngineException? Failure, ImageInfo? Image);

    /// <summary>Downloads a launch's image and looks at what is here afterwards.</summary>
    public async Task<Pulled> PullForLaunchAsync(Selected on, string image, Action<PullProgress?> onProgress, Action onPulled, CancellationToken cancellationToken)
    {
        using (selection.Use(on))
        {
            ContainerEngineException? failure = null;
            try
            {
                await PullCoreAsync(image, onProgress, cancellationToken).ConfigureAwait(false);
                onPulled();
            }
            catch (ContainerEngineException ex)
            {
                failure = ex;
            }

            return new Pulled(failure, await engine.InspectImageAsync(image, cancellationToken).ConfigureAwait(false));
        }
    }

    public async Task<IDisposable> EnterImageUseAsync(RunnerImageGate imageGate, CancellationToken cancellationToken)
        => await imageGate.EnterUseAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<HostGpu>> ListContainerGpusAsync(Selected on, string image, CancellationToken cancellationToken)
    {
        using (selection.Use(on))
        {
            return await engine.ListContainerGpusAsync(image, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ImageInfo?> InspectImageAsync(Selected on, string image, CancellationToken cancellationToken)
    {
        using (selection.Use(on))
        {
            return await engine.InspectImageAsync(image, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ContainerInfo?> InspectContainerAsync(Selected on, string container, CancellationToken cancellationToken)
    {
        using (selection.Use(on))
        {
            return await engine.InspectContainerAsync(container, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <param name="Holder">What still has the name; null when it is free.</param>
    /// <param name="StartedAgain">A stopped runner there was started again as it was to be removed.</param>
    public sealed record NameCleared(ContainerInfo? Holder, bool StartedAgain);

    /// <summary>
    /// Frees the runner's name for a launch: a stopped runner of this app there goes, never by force.
    /// What is not this app's to remove (another's container, a rejected one, one that runs) is left and
    /// brought back.
    /// </summary>
    /// <param name="onRemoved">Told at once of a container of a launch that was removed, as it was seen before.</param>
    public async Task<NameCleared> ClearNameAsync(Selected on, string? rejectedId, Action<RemovedLaunch> onRemoved, CancellationToken cancellationToken)
    {
        using (selection.Use(on))
        {
            var existing = await engine.InspectContainerAsync(options.ContainerName, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return new NameCleared(null, false);
            }

            if (!IsOwned(existing) || IsRejected(existing, rejectedId) || existing.Running)
            {
                return new NameCleared(existing, false);
            }

            if (!await RemoveStoppedSeenThroughAsync(Target(existing), cancellationToken).ConfigureAwait(false))
            {
                return new NameCleared(existing, true);
            }

            NoteRemoved(RemovedOf(existing), onRemoved);
            return new NameCleared(null, false);
        }
    }

    /// <summary>Lists this computer's GPUs now.</summary>
    public Task<HostGpuReport> ProbeHostGpusAsync(CancellationToken cancellationToken) => gpus.ProbeAsync(cancellationToken);

    /// <param name="Id">The new container's ID; null when the creation failed.</param>
    /// <param name="GivenUp">It did not answer in time and was given up: the engine may still create the container.</param>
    public sealed record Created(string? Id, ContainerEngineException? Failure = null, bool GivenUp = false);

    /// <summary>
    /// Creates the launch's container, without starting it, given up after <paramref name="timeout"/>.
    /// What it creates runs nothing: also when the engine makes it after this was cancelled or given up.
    /// </summary>
    public async Task<Created> CreateAsync(Selected on, ContainerSpec spec, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var onEngine = selection.Use(on);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return new Created(await engine.CreateAsync(spec, deadline.Token).ConfigureAwait(false));
        }
        catch (ContainerEngineException ex)
        {
            return new Created(null, ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Created(null, new ContainerEngineException($"Docker did not create the runner container within {timeout.TotalSeconds:0} seconds."), GivenUp: true);
        }
    }

    /// <summary>Spawns the start of a created container now, on <paramref name="on"/>; the caller waits for it as a job.</summary>
    public Task StartContainer(Selected on, string id, Func<bool> stillWanted, CancellationToken cancellationToken)
    {
        using (selection.Use(on))
        {
            return engine.StartAsync(id, cancellationToken, stillWanted);
        }
    }

    /// <param name="Settled">
    /// Nothing of the creation is left to look for: it was found and dealt with, or the engine answered
    /// that there is none after a definite refusal. False when it may still appear (the call was given
    /// up, or the engine did not answer): inert either way, it is looked for later, to be removed.
    /// </param>
    /// <param name="Leftover">It is there and the engine would not remove it: its target, removed there later.</param>
    /// <param name="Running">It runs, though this app never started it (someone did): kept, to be supervised.</param>
    /// <param name="Started">The engine saw it run, and it does not any more: gone, for Offloadr to learn.</param>
    /// <param name="Unanswered">The engine did not say what became of it: not even that it never ran.</param>
    public sealed record CreatedLeft(bool Settled, string? Leftover = null, ContainerInfo? Running = null, bool Started = false, bool Unanswered = false);

    /// <summary>
    /// Removes a container that was created and never started (it still holds the runner key): by its ID
    /// when the creation returned one, otherwise by this creation's label, whatever its name. A
    /// container of that name without this label (created by someone else meanwhile) is never touched.
    /// </summary>
    /// <param name="settle">The engine may still be creating it: it is looked for until it shows, for a moment.</param>
    public async Task<CreatedLeft> RemoveCreatedAsync(Selected on, string? id, string attemptId, bool settle, CancellationToken cancellationToken)
    {
        using var onEngine = selection.Use(on);
        try
        {
            var left = id is not null
                ? await engine.InspectContainerAsync(id, cancellationToken).ConfigureAwait(false)
                : await engine.InspectContainerAsync(options.ContainerName, cancellationToken).ConfigureAwait(false);
            if (left is null && id is null && settle)
            {
                left = await WaitForNamedAsync(cancellationToken).ConfigureAwait(false);
            }

            if (id is null && (left is null || !left.Labels.TryGetValue(RunnerSupervisor.LaunchLabel, out var named) || !string.Equals(named, attemptId, StringComparison.Ordinal)))
            {
                // Renamed already (or another took the name): found by this creation's label, whatever its name.
                left = await engine.FindByLabelAsync(RunnerSupervisor.LaunchLabel, attemptId, cancellationToken).ConfigureAwait(false);
            }

            if (left is null ||
                !IsOwned(left) ||
                !left.Labels.TryGetValue(RunnerSupervisor.LaunchLabel, out var leftAttempt) ||
                !string.Equals(leftAttempt, attemptId, StringComparison.Ordinal))
            {
                // Not there. After a call that was given up, the engine may still make it.
                return new CreatedLeft(Settled: id is not null || !settle);
            }

            if (left.Running)
            {
                // Never started by this app, and running (someone started it): never removed by force,
                // and still this app's container, to supervise and to stop.
                return new CreatedLeft(true, Running: left);
            }

            var after = await RemoveUnlessStartedAgainAsync(left, cancellationToken).ConfigureAwait(false);
            if (after.Kept is { } kept)
            {
                logger.LogWarning("Could not remove the runner container that was created and not started: {Error}", Told(kept));
            }

            return new CreatedLeft(true, after.Kept is null ? null : Target(left), after.Running, after.Started || !left.NeverStarted);
        }
        catch (ContainerEngineException ex)
        {
            logger.LogWarning("Could not look for the runner container that was created and not started: {Error}", Told(ex));
            return new CreatedLeft(false, Unanswered: true);
        }
    }

    public enum LaunchLeft
    {
        /// <summary>It runs after all (the daemon's answer was lost): kept.</summary>
        Running,

        /// <summary>
        /// It ran and is not running any more, or is no longer there to say whether it ran: gone, for
        /// Offloadr to learn, not a failed launch.
        /// </summary>
        Exited,

        /// <summary>It was seen created and never started (and is removed): it ran nothing.</summary>
        NeverStarted,

        /// <summary>The engine did not say: still this app's container, by its ID, and looked at again.</summary>
        Unknown,
    }

    /// <param name="Leftover">It is stopped and the engine would not remove it: its target, removed there later.</param>
    /// <param name="Started">The engine saw it run, whatever is learned next: it is no container that never started.</param>
    /// <param name="StillCreated">
    /// It is the leftover, and still only created: after a start the engine never refused, it may be
    /// started yet, and nothing of it is settled until it is removed, or seen exited.
    /// </param>
    /// <param name="SeenCreated">It was seen created, and the engine then said nothing more of it (it may be removed by now).</param>
    public sealed record FailedLaunchLeft(LaunchLeft What, string? Leftover = null, bool Started = false, bool StillCreated = false, bool SeenCreated = false);

    /// <summary>
    /// After a start that failed or was given up: asks the engine what became of the container, which is
    /// known by its ID. One that runs after all may already serve, and is kept. One that does not is
    /// removed (never by force), and whether it ever started goes with the answer: created and never
    /// started, it ran nothing; otherwise it ran, and its executor may have registered.
    /// </summary>
    public async Task<FailedLaunchLeft> LookAfterStartAsync(Selected on, string id, CancellationToken cancellationToken)
    {
        using var onEngine = selection.Use(on);
        var seenCreated = false;
        try
        {
            var left = await engine.InspectContainerAsync(id, cancellationToken).ConfigureAwait(false);
            seenCreated = left is { NeverStarted: true, Running: false };
            if (left is null)
            {
                // Not there (someone removed it): nothing shows it never ran, and it serves nothing now.
                // Told as gone, as the watch tells one that vanished; never as a launch that failed.
                return new FailedLaunchLeft(LaunchLeft.Exited);
            }

            if (left.Running)
            {
                return new FailedLaunchLeft(LaunchLeft.Running);
            }

            // What was seen here counts, whatever becomes of its removal.
            var seen = left.NeverStarted ? LaunchLeft.NeverStarted : LaunchLeft.Exited;
            // Never by force: one the daemon starts between the look and the removal is kept. (An engine
            // that then says nothing of it leaves the outcome open, as below.)
            var after = await RemoveUnlessStartedAgainAsync(left, cancellationToken).ConfigureAwait(false);
            if (after.Kept is { } kept)
            {
                // Stopped, and still there holding the runner key: removed on its engine later.
                logger.LogWarning("Could not remove the runner container after a failed start: {Error}", Told(kept));
            }

            if (after.Unseen)
            {
                // The engine saw it run and says nothing more: it started, and whether it still runs is
                // for the watch to learn. Never told as a launch that failed.
                return new FailedLaunchLeft(LaunchLeft.Unknown, Started: true);
            }

            if (after.Running is not null)
            {
                return new FailedLaunchLeft(LaunchLeft.Running);
            }

            // Removed as it was seen; or started since and gone or stopped again, which is an exit. That
            // the engine showed it as one that ran (exited when it was looked at, or running as it was
            // removed) goes with the answer: after a start the engine refused, only that says an exit is
            // to be told (someone else started it).
            return new FailedLaunchLeft(
                after.Started ? LaunchLeft.Exited : seen,
                after.Kept is null ? null : Target(left),
                Started: after.Started || seen == LaunchLeft.Exited,
                StillCreated: after.Created);
        }
        catch (Exception ex) when (ex is ContainerEngineException or ProcessStartException)
        {
            logger.LogWarning("Could not look at the runner container after a failed start: {Error}", Told(ex));
            return new FailedLaunchLeft(LaunchLeft.Unknown, SeenCreated: seenCreated);
        }
    }

    private async Task<ContainerInfo?> WaitForNamedAsync(CancellationToken cancellationToken)
    {
        var poll = TimeSpan.FromMilliseconds(Math.Min(250, options.CancelledLaunchSettle.TotalMilliseconds / 4));
        var started = time.GetTimestamp();
        ContainerInfo? found = null;
        while (found is null && time.GetElapsedTime(started) < options.CancelledLaunchSettle)
        {
            await Task.Delay(poll, time, cancellationToken).ConfigureAwait(false);
            found = await engine.InspectContainerAsync(options.ContainerName, cancellationToken).ConfigureAwait(false);
        }

        return found;
    }

    /// <summary>The image the just-started runner runs, or null when the engine does not say (the checked one is assumed).</summary>
    public async Task<string?> StartedImageIdAsync(Selected on, string? containerId, CancellationToken cancellationToken)
    {
        try
        {
            using (selection.Use(on))
            {
                return containerId is null
                    ? null
                    : (await engine.InspectContainerAsync(containerId, cancellationToken).ConfigureAwait(false))?.ImageId;
            }
        }
        catch (ContainerEngineException ex)
        {
            logger.LogWarning("Could not read which image the runner started from: {Error}", Told(ex));
            return null;
        }
    }

    /// <summary>
    /// Removes the image a runner ran before this one, now that the new runner runs: only when no name
    /// points at it any more (its tag moved to the newer image), never by force, so the engine keeps it
    /// while a container uses it, and an image another template still names stays.
    /// Each runner image takes many gigabytes; nothing else is pruned.
    /// </summary>
    /// <param name="imageReference">
    /// What the previous runner was started from. A digest (<c>repo@sha256:…</c>) names that very image
    /// for good, though the engine lists no tag for it: such an image is kept, for its template.
    /// </param>
    /// <returns>Null when a pull or launch is under way, which may be about to use the image: tried again later.</returns>
    public async Task<bool?> RemoveSupersededImageAsync(
        string imageId,
        string imageReference,
        Selected on,
        IReadOnlyList<string> pinnedReferences,
        RunnerImageGate imageGate,
        CancellationToken cancellationToken)
    {
        if (imageReference.Contains('@', StringComparison.Ordinal))
        {
            return false;
        }

        using var removal = imageGate.TryEnterRemoval();
        if (removal is null)
        {
            return null;
        }

        try
        {
            using (selection.Use(on))
            {
                var image = await engine.InspectImageAsync(imageId, cancellationToken).ConfigureAwait(false);
                if (image?.RepoTags is not { Count: 0 } || image.RepoDigest is not { } digestReference)
                {
                    return false;
                }

                // Nor while another template pins it by digest, which the engine lists as no tag.
                foreach (var reference in pinnedReferences)
                {
                    if ((await engine.InspectImageAsync(reference, cancellationToken).ConfigureAwait(false))?.Id == imageId)
                    {
                        return false;
                    }
                }

                // Removed by its digest, not its ID: the engine then drops only that reference, and keeps
                // the image if a name was put on it since it was looked at.
                if (await engine.RemoveImageAsync(digestReference, cancellationToken).ConfigureAwait(false) &&
                    await engine.InspectImageAsync(imageId, cancellationToken).ConfigureAwait(false) is null)
                {
                    log($"supervisor: removed the runner image this one replaced ({RunnerSupervisor.ShortImageId(imageId)}).");
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is ContainerEngineException or ProcessStartException)
        {
            // Left for the person to remove; the new runner runs either way.
            log($"supervisor: could not remove the runner image this one replaced: {ex.Message}");
        }

        return false;
    }

    // Watching the container

    /// <param name="Container">The supervised container as the engine has it; null when it is not there.</param>
    /// <param name="LearnedId">Its ID, learned now: its creation never returned it and it was found by its attempt.</param>
    /// <param name="Taker">Another container that has the runner's name now.</param>
    /// <param name="KeyRead">The key it was started with was read (<paramref name="Key"/> may still be null: it has none).</param>
    /// <param name="StatusRead">The agent's status file was read (<paramref name="Status"/> is null when it has none yet).</param>
    /// <param name="NeverStarted">It was found created and never started, and removed: it ran nothing.</param>
    public sealed record Observation(
        ContainerInfo? Container,
        string? LearnedId = null,
        ContainerInfo? Taker = null,
        bool KeyRead = false,
        string? Key = null,
        bool StatusRead = false,
        RunnerStatusSnapshot? Status = null,
        bool NeverStarted = false,
        string? Origin = null);

    /// <summary>
    /// One look at the supervised container: by ID once known (a renamed runner is still the runner, and
    /// a container that takes the name is never taken for it), by its attempt until then.
    /// </summary>
    public async Task<Observation> ObserveAsync(
        Selected on,
        string? id,
        string? attempt,
        bool readKey,
        bool readStatus,
        CancellationToken cancellationToken)
    {
        using var onEngine = selection.Use(on);
        var reference = id ?? options.ContainerName;
        var container = await engine.InspectContainerAsync(reference, cancellationToken).ConfigureAwait(false);
        string? learned = null;
        if (id is null && attempt is not null && (container is null || !IsContainer(container, id, attempt)) &&
            await engine.FindByLabelAsync(RunnerSupervisor.LaunchLabel, attempt, cancellationToken).ConfigureAwait(false) is { } labelled && IsOwned(labelled))
        {
            // Its ID was never learned (its creation did not return it) and it was renamed: found by its
            // attempt, and known by ID from now on, rather than taken for gone.
            learned = NormalizeContainerId(labelled.Id) ?? labelled.Id;
            container = labelled;
            reference = learned;
        }

        ContainerInfo? taker = null;
        if (container is not null && learned is null && !IsContainer(container, id, attempt))
        {
            // Another container took the name between checks: ours is gone, and that one is not
            // followed, read or stopped as if it were.
            taker = container;
            container = null;
        }
        else if (container is null && id is not null &&
                 await engine.InspectContainerAsync(options.ContainerName, cancellationToken).ConfigureAwait(false) is { } named &&
                 !IsContainer(named, id, attempt))
        {
            taker = named;
        }

        if (container is null || !container.Running)
        {
            return new Observation(container, learned, taker);
        }

        string? key = null;
        string? origin = null;
        if (readKey)
        {
            key = await engine.ReadEnvironmentVariableAsync(reference, "RUNNER_SECRET", cancellationToken).ConfigureAwait(false);
            key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
            origin = await engine.ReadEnvironmentVariableAsync(reference, "OFFLOADR_API_GRPC", cancellationToken).ConfigureAwait(false);
            origin = string.IsNullOrWhiteSpace(origin) ? null : origin.Trim();
        }

        var status = readStatus
            ? RunnerSupervisor.ParseStatus(await engine.ReadFileAsync(reference, options.StatusFilePath, cancellationToken).ConfigureAwait(false))
            : null;
        return new Observation(container, learned, taker, readKey, key, readStatus, status, Origin: origin);
    }

    /// <summary>Reads the agent's status from the container; null when it has none. Throws when the engine does not answer.</summary>
    public async Task<RunnerStatusSnapshot?> ReadStatusAsync(Selected on, string container, CancellationToken cancellationToken)
    {
        using (selection.Use(on))
        {
            return RunnerSupervisor.ParseStatus(await engine.ReadFileAsync(container, options.StatusFilePath, cancellationToken).ConfigureAwait(false));
        }
    }

    public enum ExitedRemoval
    {
        /// <summary>It is gone from its engine.</summary>
        Removed,

        /// <summary>It was started again since it was seen stopped: still the runner, and not removed.</summary>
        StartedAgain,

        /// <summary>It exited, and the engine would not remove it: left there, removed later.</summary>
        Kept,
    }

    /// <param name="Started">The engine saw it run since it was seen stopped: whatever it was then, it is no container that never started.</param>
    /// <param name="StillCreated">Kept, and still only created: after a start the engine never refused, it may be started yet.</param>
    public sealed record ExitedRemoved(ExitedRemoval What, string? Problem = null, bool Started = false, bool StillCreated = false);

    /// <summary>
    /// Removes a container seen stopped, as it still holds the runner key: never by force. One the
    /// engine would not remove is looked at again: running (or seen running, with nothing said since),
    /// it is still the runner; only one confirmed stopped is kept as a leftover. Throws when the engine
    /// does not say what became of it: it stays supervised.
    /// </summary>
    public async Task<ExitedRemoved> RemoveExitedAsync(Selected on, ContainerInfo container, CancellationToken cancellationToken)
    {
        using (selection.Use(on))
        {
            var after = await RemoveUnlessStartedAgainAsync(container, cancellationToken).ConfigureAwait(false);
            if (after.Running is not null)
            {
                return new ExitedRemoved(ExitedRemoval.StartedAgain, Started: true);
            }

            return after.Kept is { } kept
                ? new ExitedRemoved(ExitedRemoval.Kept, kept.Message, after.Started, after.Created)
                : new ExitedRemoved(ExitedRemoval.Removed, Started: after.Started);
        }
    }

    public Task FollowLogsAsync(Selected on, string container, int tail, Action<string> onLine, CancellationToken cancellationToken)
    {
        using (selection.Use(on))
        {
            return engine.FollowLogsAsync(container, tail, onLine, cancellationToken);
        }
    }

    // Stopping the container

    /// <param name="Stopped">The container was found, stopped and (unless <paramref name="Leftover"/>) removed.</param>
    /// <param name="NeverCreated">None of this attempt was found anywhere: a cancelled creation may still make it.</param>
    /// <param name="Leftover">It stopped, and the engine would not remove it: its target, for removal later.</param>
    /// <param name="NeverStarted">It was found created and never started: it ran nothing.</param>
    /// <param name="Running">It runs, and its release was not asked for: nothing was stopped yet.</param>
    public sealed record StoppedContainer(bool Stopped, bool NeverCreated, string? Leftover = null, bool NeverStarted = false, ContainerInfo? Running = null);

    /// <summary>
    /// Stops and removes the supervised container, by ID when it is known (a renamed runner is still the
    /// one stopped). After a cancelled creation the engine may still be making it: it is looked
    /// for, for a moment, by name and then by its attempt. Throws when the engine does not answer, and
    /// when it kept a container that may run yet: one that runs, or, with <paramref name="startMayBeUnderWay"/>
    /// (its start was asked for and never refused), one that is still only created.
    /// </summary>
    /// <param name="onRan">Told at once when the engine shows that it ran (it runs, or exited): that stands whatever becomes of the stop.</param>
    /// <param name="onStopped">Told at once when the engine confirmed the stop. From then on a cancellation ends only the removal: the container is stopped, and left for later.</param>
    /// <param name="releaseFirst">Its release was not asked for (it was taken for one that never started, or was not known): found running, it is brought back as that, not stopped.</param>
    public async Task<StoppedContainer> StopContainerAsync(Selected on, string? id, string? attempt, TimeSpan grace, bool startMayBeUnderWay, bool releaseFirst, Action onRan, Action onStopped, CancellationToken cancellationToken)
    {
        using var onEngine = selection.Use(on);
        var existing = await engine.InspectContainerAsync(id ?? options.ContainerName, cancellationToken).ConfigureAwait(false);
        var neverCreated = false;
        if ((existing is null || !IsContainer(existing, id, attempt)) && id is null && attempt is not null)
        {
            // (Another may hold the name meanwhile: that one is never taken for it.)
            existing = existing is not null && !IsContainer(existing, id, attempt) ? null : existing;
            existing ??= await WaitForNamedAsync(cancellationToken).ConfigureAwait(false);

            // Renamed already, before its ID was known (another may hold the name now): found by this
            // attempt's label, whatever its name.
            if (existing is null || !IsContainer(existing, id, attempt))
            {
                existing = await engine.FindByLabelAsync(RunnerSupervisor.LaunchLabel, attempt, cancellationToken).ConfigureAwait(false) ?? existing;
            }

            // None of this attempt anywhere: it was never created, so there is no exit to report.
            neverCreated = existing is null || !IsOwned(existing) || !IsContainer(existing, id, attempt);
        }

        if (existing is null || !IsOwned(existing) || !IsContainer(existing, id, attempt))
        {
            return new StoppedContainer(false, neverCreated);
        }

        var found = existing;
        void Ran()
        {
            onRan();
            Seen(found, ran: true);
        }

        var neverStarted = existing is { NeverStarted: true, Running: false };
        var confirmed = false;
        if (!neverStarted)
        {
            // (One created and never started runs nothing to stop.)
            Ran();
            if (releaseFirst && existing.Running)
            {
                return new StoppedContainer(false, false, Running: existing);
            }

            await engine.StopAsync(Target(existing), grace, cancellationToken).ConfigureAwait(false);
            confirmed = true;
            onStopped();
        }

        try
        {
            // (One that was only created is not removed by force: a start under way in the engine may have
            // completed since, or someone started it. Then it is released and stopped first, as any that runs.)
            if (neverStarted)
            {
                // (Handed over first: removed with the answer lost, nothing would show it any more.)
                Seen(existing, ran: false);
                if (await engine.RemoveStoppedAsync(Target(existing), cancellationToken).ConfigureAwait(false))
                {
                    return new StoppedContainer(true, false, NeverStarted: true);
                }

                Ran();
                return new StoppedContainer(false, false, Running: existing with { Running = true, NeverStarted = false });
            }

            await engine.RemoveAsync(Target(existing), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (confirmed)
        {
            // Out of time for the removal: it is stopped, which is recorded (Offloadr learns it is gone).
            log("warning: the runner container stopped but stays on the container engine for now.");
            return new StoppedContainer(true, false, Target(existing));
        }
        catch (ContainerEngineException ex)
        {
            // Kept. One that was only created was not stopped above, and may have been started since (by
            // a start still under way in the engine): only what it is now says that it does not run.
            // Running, or with an engine that does not say, it stays supervised: this throws.
            // (Unless the engine confirmed a stop: that stands, also when it says nothing more.)
            ContainerInfo? now;
            try
            {
                now = await engine.InspectContainerAsync(Target(existing), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception unseen) when (confirmed && unseen is ContainerEngineException or OperationCanceledException)
            {
                log("warning: the runner container stopped but stays on the container engine for now.");
                return new StoppedContainer(true, false, Target(existing));
            }

            if (now is { Running: true } or { NeverStarted: false })
            {
                Ran();
            }

            if (now is { Running: true })
            {
                throw;
            }

            if (now is null)
            {
                return new StoppedContainer(true, false, NeverStarted: neverStarted);
            }

            if (startMayBeUnderWay && now.MayStartYet)
            {
                // Still only created, and the engine was asked to start it without ever saying it refused:
                // it may start it yet. Not let go of as stopped: it stays supervised, as one that runs.
                throw;
            }

            // Stopped: it serves nothing any more, and is recorded so (Offloadr learns it is gone).
            // Left on its engine, still holding the runner key, it is removed there later.
            log($"warning: {ex.Message} The runner container stopped but stays on the container engine for now.");
            return new StoppedContainer(true, false, Target(existing), NeverStarted: neverStarted && now.NeverStarted);
        }

        return new StoppedContainer(true, false, NeverStarted: neverStarted);
    }

    /// <summary>
    /// Re-checks a container the supervisor kept owning after a stop that failed: one that still runs is
    /// brought back (with its ID, if learned now); a stopped one is removed, never by force.
    /// </summary>
    public async Task<Observation> ResumeOwnedAsync(Selected on, string? id, string? attempt, CancellationToken cancellationToken)
    {
        using var onEngine = selection.Use(on);
        var container = await engine.InspectContainerAsync(id ?? options.ContainerName, cancellationToken).ConfigureAwait(false);
        string? learned = null;
        if (id is null && attempt is not null && (container is null || !IsContainer(container, id, attempt)) &&
            await engine.FindByLabelAsync(RunnerSupervisor.LaunchLabel, attempt, cancellationToken).ConfigureAwait(false) is { } labelled && IsOwned(labelled))
        {
            // Its ID was never learned and it was renamed: found by its attempt, not taken for gone.
            learned = NormalizeContainerId(labelled.Id) ?? labelled.Id;
            container = labelled;
        }

        var ours = container is not null && IsOwned(container) && (learned is not null || IsContainer(container, id, attempt));
        var neverStarted = false;
        if (ours && !container!.Running)
        {
            var after = await RemoveUnlessStartedAgainAsync(container, cancellationToken).ConfigureAwait(false);
            if (after.Kept is { } kept)
            {
                throw kept;
            }

            neverStarted = container.NeverStarted && !after.Started;
            container = after.Running;
        }

        return new Observation(ours && container is { Running: true } ? container : null, learned, NeverStarted: neverStarted && container is null);
    }
}
