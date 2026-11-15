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
/// The runner images Docker keeps on this computer, and removing the ones no runner uses. Only images of
/// the runner's own repositories are listed, and only by their runner names or digests are they removed,
/// never by force: Docker keeps an image a container uses, or one another name points at.
/// </summary>
public sealed class RunnerImages
{
    private readonly Func<SupervisorView> _view;
    private readonly IContainerEngine _engine;
    private readonly IRunnerSettingsStore _settings;

    public RunnerImages(RunnerSupervisor supervisor, IContainerEngine engine, IRunnerSettingsStore settings)
        : this(() => supervisor.View, engine, settings)
    {
    }

    internal RunnerImages(Func<SupervisorView> view, IContainerEngine engine, IRunnerSettingsStore settings)
    {
        _view = view;
        _engine = engine;
        _settings = settings;
    }

    public async Task<IReadOnlyList<RunnerImage>> ListAsync(CancellationToken cancellationToken = default)
        => Classify(await _engine.ListImagesAsync(cancellationToken).ConfigureAwait(false), _view(), _settings.Current);

    /// <summary>Removes the image with <paramref name="imageId"/> when it is still one no runner uses.</summary>
    public async Task<ImageRemoval> RemoveAsync(string imageId, CancellationToken cancellationToken = default)
    {
        if (_view().State is SupervisorState.Checking or SupervisorState.ResolvingPlan or SupervisorState.Pulling
            or SupervisorState.Starting or SupervisorState.Stopping)
        {
            // It may be about to use the image; the list is shown again once it settles.
            return ImageRemoval.Busy;
        }

        // Looked at again, not taken from the page: the editor may have changed since it was drawn.
        var image = (await ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Id == imageId);
        if (image is null)
        {
            return ImageRemoval.Removed;
        }

        if (!image.CanRemove)
        {
            return ImageRemoval.NotAllowed;
        }

        foreach (var tag in image.Tags)
        {
            // A pull since the listing may have moved the tag to another image, which is not the one asked
            // for and stays. A digest reference cannot move, so those below need no such check.
            if ((await _engine.InspectImageAsync(tag, cancellationToken).ConfigureAwait(false))?.Id == imageId)
            {
                await _engine.RemoveImageAsync(tag, cancellationToken).ConfigureAwait(false);
            }
        }

        // An image left without a name keeps its digests, which the runner's repositories' go with. One
        // that still has a name (someone tagged it themselves) is theirs and stays.
        if (await _engine.InspectImageAsync(imageId, cancellationToken).ConfigureAwait(false) is { RepoTags.Count: 0 })
        {
            foreach (var digest in image.Digests)
            {
                await _engine.RemoveImageAsync(digest, cancellationToken).ConfigureAwait(false);
            }
        }

        return await _engine.InspectImageAsync(imageId, cancellationToken).ConfigureAwait(false) is null
            ? ImageRemoval.Removed
            : ImageRemoval.Kept;
    }

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
