using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor;

/// <summary>What a runner image on this computer is for.</summary>
public enum RunnerImageUse
{
    /// <summary>The runner runs it now.</summary>
    Running,

    /// <summary>The editor Offloadr or Settings chose runs it: removing it only downloads it again.</summary>
    Chosen,

    /// <summary>For another editor than the one chosen: removing it frees the space until that editor is chosen.</summary>
    NotUsed,

    /// <summary>An earlier version no name points at any more.</summary>
    OlderVersion,
}

/// <param name="Name">The image's runner names, or its repository for an older version.</param>
/// <param name="Size">Counts layers shared with other images too, so removing it can free less.</param>
public sealed record RunnerImage(string Id, string Name, long Size, RunnerImageUse Use, IReadOnlyList<string> Tags, IReadOnlyList<string> Digests)
{
    public bool CanRemove => Use is RunnerImageUse.NotUsed or RunnerImageUse.OlderVersion;

    public string UseText => Use switch
    {
        RunnerImageUse.Running => "Running",
        RunnerImageUse.Chosen => "Your editor",
        RunnerImageUse.NotUsed => "Not used",
        _ => "Older version",
    };
}

public enum ImageRemoval
{
    Removed,

    /// <summary>The engine kept it: a container uses it, or a name outside the runner's points at it.</summary>
    Kept,

    /// <summary>Running or chosen: not removed.</summary>
    NotAllowed,

    /// <summary>The runner is being prepared, started or stopped; asked again once it settles.</summary>
    Busy,
}

/// <summary>
/// Keeps removals of runner images apart from the supervisor's uses of them: a pull, which can move a tag
/// from one image to another, and a launch, whose image must still be there when its container is
/// created. No removal runs while either is under way. Uses may overlap one another; one that starts
/// during a removal waits for it, which takes moments.
/// </summary>
public sealed class RunnerImageGate
{
    private readonly SemaphoreSlim _removal = new(1, 1);
    private int _uses;

    /// <summary>Marks a pull or launch as under way until the result is disposed; waits for a removal in progress first.</summary>
    internal async Task<IDisposable> EnterUseAsync(CancellationToken cancellationToken)
    {
        await _removal.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _uses);
        _removal.Release();
        return new Exit(() => Interlocked.Decrement(ref _uses));
    }

    /// <summary>
    /// Starts a removal unless a pull or launch is under way; null then. A use can only check in before the removal
    /// takes the gate (and is counted) or after it leaves, so a removal never overlaps one.
    /// </summary>
    internal IDisposable? TryEnterRemoval()
    {
        if (!_removal.Wait(0))
        {
            return null;
        }

        if (Volatile.Read(ref _uses) > 0)
        {
            _removal.Release();
            return null;
        }

        return new Exit(() => _removal.Release());
    }

    private sealed class Exit(Action exit) : IDisposable
    {
        private Action? _exit = exit;

        public void Dispose() => Interlocked.Exchange(ref _exit, null)?.Invoke();
    }
}

/// <summary>
/// The runner images Docker keeps on this computer, and removing the ones no runner uses. Only images of
/// the runner's own repositories are listed, and only by their runner names or digests are they removed,
/// never by force: Docker keeps an image a container uses, or one another name points at.
/// </summary>
public sealed class RunnerImages
{
    private readonly Func<SupervisorView> _view;
    private readonly IContainerEngine _engine;
    private readonly IRunnerSettingsStore _settings;
    private readonly RunnerImageGate _gate;

    public RunnerImages(RunnerSupervisor supervisor, IContainerEngine engine, IRunnerSettingsStore settings)
        : this(() => supervisor.View, engine, settings, supervisor.ImageGate)
    {
    }

    internal RunnerImages(Func<SupervisorView> view, IContainerEngine engine, IRunnerSettingsStore settings, RunnerImageGate? gate = null)
    {
        _view = view;
        _engine = engine;
        _settings = settings;
        _gate = gate ?? new RunnerImageGate();
    }

    public async Task<IReadOnlyList<RunnerImage>> ListAsync(CancellationToken cancellationToken = default)
        => Classify(await _engine.ListImagesAsync(cancellationToken).ConfigureAwait(false), _view(), _settings.Current);

    /// <summary>Removes the image with <paramref name="imageId"/> when it is still one no runner uses.</summary>
    public async Task<ImageRemoval> RemoveAsync(string imageId, CancellationToken cancellationToken = default)
    {
        if (Busy(_view()))
        {
            // It may be about to use the image; the list is shown again once it settles.
            return ImageRemoval.Busy;
        }

        // Not while the runner pulls or launches an image, which can move a tag or needs the image.
        using var removal = _gate.TryEnterRemoval();
        if (removal is null)
        {
            return ImageRemoval.Busy;
        }

        // Looked at again, not taken from the page: the editor may have changed since it was drawn.
        var stored = (await _engine.ListImagesAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Id == imageId);
        if (stored is null)
        {
            return ImageRemoval.Removed;
        }

        // And again before each step that removes something: an editor chosen meanwhile (a launch of it waits
        // for this removal) keeps its image, and what was already removed stays removed.
        ImageRemoval? Stop() => Busy(_view()) ? ImageRemoval.Busy
            : Classify([stored], _view(), _settings.Current).FirstOrDefault() is { CanRemove: true } ? null
            : ImageRemoval.NotAllowed;
        if (Stop() is { } stop)
        {
            return stop;
        }

        var image = Classify([stored], _view(), _settings.Current).Single();

        foreach (var tag in image.Tags)
        {
            // A pull outside the app since the listing may have moved the tag to another image, which is not
            // the one asked for and stays. A digest reference cannot move, so those below need no such check.
            if ((await _engine.InspectImageAsync(tag, cancellationToken).ConfigureAwait(false))?.Id != imageId)
            {
                continue;
            }

            if (Stop() is { } stopped)
            {
                return stopped;
            }

            await _engine.RemoveImageAsync(tag, cancellationToken).ConfigureAwait(false);
        }

        // An image left without a name keeps its digests, which the runner's repositories' go with. One
        // that still has a name (someone tagged it themselves) is theirs and stays.
        if (await _engine.InspectImageAsync(imageId, cancellationToken).ConfigureAwait(false) is { RepoTags.Count: 0 })
        {
            foreach (var digest in image.Digests)
            {
                if (Stop() is { } stopped)
                {
                    return stopped;
                }

                await _engine.RemoveImageAsync(digest, cancellationToken).ConfigureAwait(false);
            }
        }

        return await _engine.InspectImageAsync(imageId, cancellationToken).ConfigureAwait(false) is null
            ? ImageRemoval.Removed
            : ImageRemoval.Kept;
    }

    private static bool Busy(SupervisorView view)
        => view.State is SupervisorState.Checking or SupervisorState.ResolvingPlan or SupervisorState.Pulling
            or SupervisorState.Starting or SupervisorState.Stopping;

    /// <summary>The runner images among <paramref name="images"/>: what each is for, the running and chosen ones first.</summary>
    internal static IReadOnlyList<RunnerImage> Classify(IEnumerable<StoredImage> images, SupervisorView view, RunnerSettings settings)
    {
        var plan = view.Plan ?? BuiltInLaunchPlanCatalog.Plan();
        var templates = plan.Templates.Concat(BuiltInLaunchPlanCatalog.Templates);
        if (view.ActiveTemplate is { } active)
        {
            templates = templates.Append(active);
        }

        var repositories = templates.Select(template => Repository(template.Image)).ToHashSet(StringComparer.Ordinal);
        var wanted = new[] { plan.Choose(settings)?.Image, view.ActiveTemplate?.Image }.OfType<string>().ToArray();
        var running = view.IsActive || view.State == SupervisorState.Stopping ? view.Image?.Id : null;

        var result = new List<RunnerImage>();
        foreach (var image in images)
        {
            var tags = image.RepoTags.Where(tag => repositories.Contains(Repository(tag))).ToArray();
            var digests = image.RepoDigests.Where(digest => repositories.Contains(Repository(digest))).ToArray();
            if (tags.Length == 0 && digests.Length == 0)
            {
                continue;
            }

            var use = image.Id == running ? RunnerImageUse.Running
                : wanted.Any(reference => Names(image, reference)) ? RunnerImageUse.Chosen
                : tags.Length > 0 ? RunnerImageUse.NotUsed
                : RunnerImageUse.OlderVersion;
            var name = tags.Length > 0 ? string.Join(", ", tags) : Repository(digests[0]);
            result.Add(new RunnerImage(image.Id, name, image.Size, use, tags, digests));
        }

        return result.OrderBy(image => image.Use).ThenBy(image => image.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The repository of an image reference: without its tag or digest.</summary>
    internal static string Repository(string reference)
    {
        var at = reference.IndexOf('@', StringComparison.Ordinal);
        var name = at >= 0 ? reference[..at] : reference;
        var colon = name.LastIndexOf(':');
        return colon > name.LastIndexOf('/') ? name[..colon] : name;
    }

    private static bool Names(StoredImage image, string reference)
    {
        var at = reference.IndexOf('@', StringComparison.Ordinal);

        // Docker lists a digest as repository@digest, without the tag a reference may also carry (repo:tag@digest),
        // and reads a reference without a tag as :latest.
        return at >= 0
            ? image.RepoDigests.Contains(Repository(reference) + reference[at..], StringComparer.Ordinal)
            : image.RepoTags.Contains(Repository(reference) == reference ? reference + ":latest" : reference, StringComparer.Ordinal);
    }
}
