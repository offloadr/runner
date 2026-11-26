using System.Collections.Concurrent;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hardware;
using Offloadr.Runner.Supervisor.Launcher;
using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Processes;
using Offloadr.Runner.Supervisor.Settings;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Supervisor.Tests;

internal sealed class FakeContainerEngine : IContainerEngine
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FakeContainer> _containers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _images = new(StringComparer.Ordinal);
    private Action<string>? _logSink;
    private TaskCompletionSource _logEnded = new();

    public ContainerEngineInfo Info { get; set; } = new(true, ContainerEngineKind.Docker, "docker", "29.0.0", "linux", "Docker Desktop", false, null);

    public bool PullFails { get; set; }

    /// <summary>Awaited inside PullAsync, for example to hold a pull until it is cancelled.</summary>
    public Func<CancellationToken, Task>? OnPull { get; set; }

    /// <summary>Replaced with the current time on every read, like an agent that keeps writing its status.</summary>
    public const string StatusNow = "$NOW";

    public string? StatusJson { get; set; }

    public string LogLine { get; set; } = "runner log line";

    public List<ContainerSpec> Runs { get; } = [];

    public List<string> Pulls { get; } = [];

    public int Stops { get; private set; }

    /// <summary>The grace period each stop gave the runner.</summary>
    public List<TimeSpan> StopTimeouts { get; } = [];

    /// <summary>Thrown by ProbeAsync, for example while Docker is still starting.</summary>
    public Exception? ProbeFailure { get; set; }

    /// <summary>Thrown by InspectContainerAsync.</summary>
    public Exception? InspectFailure { get; set; }

    /// <summary>Awaited inside the start once the container runs, for example to hold the CLI until it is cancelled.</summary>
    public Func<CancellationToken, Task>? OnRun { get; set; }

    /// <summary>Awaited inside ProbeAsync, for example to hold a slow check.</summary>
    public Func<CancellationToken, Task>? OnProbe { get; set; }

    /// <summary>Engines (by executable) that do not answer.</summary>
    public ConcurrentDictionary<string, bool> UnavailableEngines { get; } = new(StringComparer.Ordinal);

    /// <summary>What CurrentContextAsync reports.</summary>
    public string? CurrentContext { get; set; }

    /// <summary>Whether the context CurrentContextAsync reports reaches an engine on another computer.</summary>
    public bool CurrentContextRemote { get; set; }

    /// <summary>Thrown by CurrentContextAsync, as when `docker context show` fails.</summary>
    public Exception? CurrentContextFailure { get; set; }

    /// <summary>Engines (by executable) probed so far.</summary>
    public ConcurrentDictionary<string, bool> ProbedEngines { get; } = new(StringComparer.Ordinal);

    /// <summary>Engines whose context was asked for before they were ever probed.</summary>
    public ConcurrentDictionary<string, bool> ContextsReadUnprobed { get; } = new(StringComparer.Ordinal);

    public Task<ContainerEnginePin?> CurrentContextAsync(CancellationToken cancellationToken)
    {
        if (Executable() is { } executable && !ProbedEngines.ContainsKey(executable))
        {
            ContextsReadUnprobed[executable] = true;
        }

        return CurrentContextFailure is { } failure
            ? Task.FromException<ContainerEnginePin?>(failure)
            : Task.FromResult(CurrentContext is null ? null : new ContainerEnginePin(CurrentContext, ContainerEngineKind.Docker) { Remote = CurrentContextRemote });
    }

    public async Task<ContainerEngineInfo> ProbeAsync(CancellationToken cancellationToken)
    {
        if (Executable() is { } probed)
        {
            ProbedEngines[probed] = true;
        }

        if (OnProbe is { } onProbe)
        {
            await onProbe(cancellationToken);
        }

        if (Executable() is { } executable && UnavailableEngines.ContainsKey(executable))
        {
            return ContainerEngineInfo.Unavailable($"{executable} does not answer.", executable);
        }

        return ProbeFailure is { } failure ? throw failure : Info;
    }

    public async Task PullAsync(string image, Action<string> onProgress, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Pulls.Add(image);
        }

        onProgress($"{image}: Pulling");
        foreach (var line in PullLines)
        {
            onProgress(line);
        }

        if (OnPull is not null)
        {
            await OnPull(cancellationToken);
        }

        if (PullFails)
        {
            throw new ContainerEngineException($"Could not pull {image}.", PullFailureOutput, EngineOperation.Pull);
        }

        lock (_gate)
        {
            if (!PullLeavesNoImage)
            {
                _images.Add(image);
            }
        }
    }

    /// <summary>Removes an image by name, as a cleanup outside the app would.</summary>
    public void RemoveImageNamed(string image)
    {
        lock (_gate)
        {
            _images.Remove(image);
        }
    }

    /// <summary>A pull succeeds, but the image is gone right after (removed by a cleanup outside the app, say).</summary>
    public bool PullLeavesNoImage { get; set; }

    /// <summary>What a pull prints before OnPull runs.</summary>
    public IReadOnlyList<string> PullLines { get; set; } = [];

    /// <summary>What a failing pull prints last.</summary>
    public string PullFailureOutput { get; set; } = "registry unreachable";

    /// <summary>Thrown by InspectImageAsync, as when Docker is not installed.</summary>
    public Exception? InspectImageFailure { get; set; }

    public Task<ImageInfo?> InspectImageAsync(string image, CancellationToken cancellationToken)
    {
        if (InspectImageFailure is { } failure)
        {
            return Task.FromException<ImageInfo?>(failure);
        }

        lock (_gate)
        {
            if (image.StartsWith("sha256:", StringComparison.Ordinal))
            {
                // By ID, as for an image no name may point at any more.
                // A digest reference is no name: the engine lists only tags.
                var tags = _images.Where(reference => !reference.Contains('@', StringComparison.Ordinal) && ImageId(reference) == image).ToArray();
                return Task.FromResult(tags.Length > 0 || (_seenImageIds.Contains(image) && !RemovedImages.Contains(image))
                    ? new ImageInfo(image, DigestReference(image), tags)
                    : null);
            }

            if (!_images.Contains(image))
            {
                return Task.FromResult<ImageInfo?>(null);
            }

            var id = ImageId(image);
            _seenImageIds.Add(id);
            return Task.FromResult<ImageInfo?>(new ImageInfo(id, image + "@sha256:digest", [image]));
        }
    }

    private readonly HashSet<string> _seenImageIds = new(StringComparer.Ordinal);

    /// <summary>The digest reference an image pulled from a registry keeps after its tag moves.</summary>
    public static string DigestReference(string imageId) => $"ghcr.io/offloadr/runner-comfyui@{imageId}";

    /// <summary>The image IDs removed, in order.</summary>
    public List<string> RemovedImages { get; } = [];

    /// <summary>What RemoveImageAsync was asked to remove, in order.</summary>
    public List<string> RemovalRequests { get; } = [];

    /// <summary>Thrown by RemoveImageAsync, as when the engine does not answer.</summary>
    public Exception? RemoveImageFailure { get; set; }

    /// <summary>Runs as a removal reaches the engine, for example to tag the image meanwhile.</summary>
    public Action? BeforeRemoveImage { get; set; }

    public Task<bool> RemoveImageAsync(string reference, CancellationToken cancellationToken)
    {
        if (RemoveImageFailure is { } failure)
        {
            return Task.FromException<bool>(failure);
        }

        BeforeRemoveImage?.Invoke();
        lock (_gate)
        {
            RemovalRequests.Add(reference);
            var imageId = reference.StartsWith("sha256:", StringComparison.Ordinal) ? reference : reference[(reference.IndexOf('@', StringComparison.Ordinal) + 1)..];
            var tags = _images.Where(name => !name.Contains('@', StringComparison.Ordinal) && ImageId(name) == imageId).ToArray();
            if (_containers.Values.Any(container => container.ImageId == imageId) ||
                !_seenImageIds.Contains(imageId) ||
                RemovedImages.Contains(imageId))
            {
                // Kept for a container that uses it, unless forced, or already gone.
                return Task.FromResult(false);
            }

            if (reference.StartsWith("sha256:", StringComparison.Ordinal) ? tags.Length > 1 : tags.Length > 0)
            {
                // By ID the engine removes an image with a single tag, tag and all, and refuses one with
                // several; by a digest reference it drops only that reference while a name remains.
                return Task.FromResult(!reference.StartsWith("sha256:", StringComparison.Ordinal));
            }

            foreach (var name in _images.Where(name => ImageId(name) == imageId).ToArray())
            {
                _images.Remove(name);
            }

            RemovedImages.Add(imageId);
            return Task.FromResult(true);
        }
    }

    /// <summary>What ListImagesAsync returns.</summary>
    public List<StoredImage> StoredImages { get; } = [];

    public Task<IReadOnlyList<StoredImage>> ListImagesAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<StoredImage>>([.. StoredImages]);
        }
    }

    public void AddImage(string image)
    {
        lock (_gate)
        {
            _images.Add(image);
        }
    }

    /// <summary>The next creation finds the name taken by a container this app did not create.</summary>
    public bool NextRunConflict { get; set; }

    /// <summary>The labels of the container that wins the name in a run conflict (none: someone else's).</summary>
    public IReadOnlyDictionary<string, string> ConflictLabels { get; set; } = new Dictionary<string, string>();

    /// <summary>Thrown by the next creation only, before anything is created (the executable did not start).</summary>
    public Exception? NextRunStartFailure { get; set; }

    /// <summary>Thrown by the next start only: the container stays created, and never started.</summary>
    public Exception? NextRunFailure { get; set; }

    /// <summary>Runs once as the next start is spawned, before its process would start (a Stop pressed then, a tag moved).</summary>
    public Action? BeforeRun { get; set; }

    /// <summary>Awaited inside the next creation, before anything is created, for example to hold it or have it given up.</summary>
    public Func<ContainerSpec, CancellationToken, Task>? OnCreate { get; set; }

    /// <summary>
    /// Replaces the whole of the next start, for example to fail it while the container runs, or has
    /// exited. With it set, the creation makes nothing itself: the override decides what exists, and
    /// what it puts under the runner's name is the container that creation returned.
    /// </summary>
    public Func<ContainerSpec, CancellationToken, Task<string>>? RunOverride { get; set; }

    private readonly Dictionary<string, (ContainerSpec Spec, Func<ContainerSpec, CancellationToken, Task<string>>? Override)> _created = new(StringComparer.Ordinal);
    private int _creations;

    /// <summary>How many containers were created (started or not).</summary>
    public int Creations => Volatile.Read(ref _creations);

    public async Task<string> CreateAsync(ContainerSpec spec, CancellationToken cancellationToken)
    {
        if (OnCreate is { } onCreate)
        {
            OnCreate = null;
            await onCreate(spec, cancellationToken);
        }

        // As the real engine: a call cancelled before its process starts starts none.
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var id = $"runner-container-{Interlocked.Increment(ref _creations):D4}";
            if (RunOverride is { } runOverride)
            {
                RunOverride = null;
                _created[id] = (spec, runOverride);
                return id;
            }

            if (_containers.ContainsKey(Key(spec.Name)))
            {
                throw new ContainerEngineException("The container name is already in use.");
            }

            if (NextRunConflict)
            {
                // Someone else created a container of that name just before; the creation fails on the name.
                NextRunConflict = false;
                _containers[Key(spec.Name)] = new FakeContainer("someone-elses-image", true, null, ConflictLabels, "sha256:other", Id: "someone-elses-container");
                throw new ContainerEngineException("Could not start the runner container.", "The container name is already in use.");
            }

            if (NextRunStartFailure is { } notStarted)
            {
                // The executable did not start, so nothing was created.
                NextRunStartFailure = null;
                throw notStarted;
            }

            _containers[Key(spec.Name)] = new FakeContainer(spec.Image, false, null, new Dictionary<string, string>(spec.Labels), ImageId(spec.Image), new Dictionary<string, string>(spec.Environment), id) { NeverStarted = true };
            _created[id] = (spec, null);
            return id;
        }
    }

    public async Task StartAsync(string container, CancellationToken cancellationToken, Func<bool>? stillWanted = null)
    {
        (ContainerSpec Spec, Func<ContainerSpec, CancellationToken, Task<string>>? Override) created;
        lock (_gate)
        {
            if (!_created.TryGetValue(container, out created))
            {
                throw new ContainerEngineException("No such container.");
            }
        }

        if (created.Override is { } runOverride)
        {
            await runOverride(created.Spec, cancellationToken);
            return;
        }

        var beforeRun = BeforeRun;
        BeforeRun = null;
        beforeRun?.Invoke();

        // As the real engine: a call cancelled, or not wanted any more, before its process starts starts none, and says so.
        if (cancellationToken.IsCancellationRequested || stillWanted?.Invoke() == false)
        {
            throw new Offloadr.Runner.Supervisor.Processes.ProcessNotSpawnedException(cancellationToken);
        }

        lock (_gate)
        {
            Runs.Add(created.Spec);
            RunExecutables.Add(Executable());
            if (NextRunFailure is { } failure)
            {
                // Stays created and never started, as when the GPU cannot be attached.
                NextRunFailure = null;
                throw failure;
            }

            if (Find(container) is { } key)
            {
                _containers[key] = _containers[key] with { Running = true, ExitCode = null, NeverStarted = false };
            }

            _seenImageIds.Add(ImageId(created.Spec.Image));
        }

        if (OnRun is not null)
        {
            await OnRun(cancellationToken);
        }
    }

    /// <param name="neverStarted">Created and never started, from the moment it exists (no look sees it as one that exited).</param>
    public void AddContainer(string name, string image, bool running, IReadOnlyDictionary<string, string> labels, IReadOnlyDictionary<string, string>? environment = null, bool neverStarted = false)
    {
        lock (_gate)
        {
            // What a start override places for a creation is the container that creation returned.
            var created = labels.TryGetValue(RunnerSupervisor.LaunchLabel, out var attempt)
                ? _created.Where(entry => entry.Value.Override is not null &&
                                          entry.Value.Spec.Labels.TryGetValue(RunnerSupervisor.LaunchLabel, out var wanted) && wanted == attempt)
                    .Select(entry => entry.Key)
                    .FirstOrDefault()
                : null;
            _containers[Key(name)] = new FakeContainer(image, running, running ? null : 0, labels, ImageId(image), environment, created ?? $"added-container-{Guid.NewGuid():N}")
            {
                NeverStarted = neverStarted && !running,
            };
            _seenImageIds.Add(ImageId(image));
        }
    }

    /// <summary>Starts a stopped container again, as `docker start` or a restart policy does.</summary>
    public void Restart(string name)
    {
        lock (_gate)
        {
            _containers[Key(name)] = _containers[Key(name)] with { Running = true, ExitCode = null, NeverStarted = false };
        }
    }

    public void Exit(string name, int exitCode)
    {
        lock (_gate)
        {
            _containers[Key(name)] = _containers[Key(name)] with { Running = false, ExitCode = exitCode };
        }
    }

    /// <summary>Renames a container, as `docker rename` does.</summary>
    public void Rename(string name, string newName)
    {
        lock (_gate)
        {
            _containers[Key(newName)] = _containers[Key(name)];
            _containers.Remove(Key(name));
        }
    }

    public bool Exists(string name)
    {
        lock (_gate)
        {
            return _containers.Keys.Any(key => key.EndsWith("|" + name, StringComparison.Ordinal));
        }
    }

    public Task<ContainerInfo?> InspectContainerAsync(string name, CancellationToken cancellationToken)
    {
        if (NotAnswering() is { } down)
        {
            return Task.FromException<ContainerInfo?>(down);
        }

        if (InspectFailure is { } failure)
        {
            return Task.FromException<ContainerInfo?>(failure);
        }

        Interlocked.Exchange(ref _onNextInspect, null)?.Invoke();
        OnInspect?.Invoke(name);
        lock (_gate)
        {
            return Task.FromResult(Find(name) is { } key && _containers.TryGetValue(key, out var container)
                ? new ContainerInfo(container.Id ?? "id-" + name, container.Image, container.Running, container.ExitCode, container.Labels, container.ImageId) { NeverStarted = container.NeverStarted }
                : null);
        }
    }

    /// <summary>How many label lookups still fail, as when Docker hiccups.</summary>
    public int FindByLabelFailures { get; set; }

    /// <summary>Runs once, at the next FindByLabelAsync, before it looks (a Stop pressed during the look, say).</summary>
    public Action? BeforeNextFindByLabel { get; set; }

    public Task<ContainerInfo?> FindByLabelAsync(string label, string value, CancellationToken cancellationToken)
    {
        if (NotAnswering() is { } down)
        {
            return Task.FromException<ContainerInfo?>(down);
        }

        var before = BeforeNextFindByLabel;
        BeforeNextFindByLabel = null;
        before?.Invoke();

        if (FindByLabelFailures > 0)
        {
            FindByLabelFailures--;
            return Task.FromException<ContainerInfo?>(new ContainerEngineException("docker ps timed out."));
        }

        lock (_gate)
        {
            var prefix = Key(string.Empty);
            var found = _containers.FirstOrDefault(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                                                            entry.Value.Labels.TryGetValue(label, out var labelled) && labelled == value);
            return Task.FromResult(found.Value is { } container
                ? new ContainerInfo(container.Id ?? "id-" + found.Key[prefix.Length..], container.Image, container.Running, container.ExitCode, container.Labels, container.ImageId) { NeverStarted = container.NeverStarted }
                : null);
        }
    }

    /// <summary>How many environment reads still fail, as when Docker hiccups.</summary>
    public int EnvironmentReadFailures { get; set; }

    /// <summary>Only reads of this variable fail (null: any).</summary>
    public string? EnvironmentReadFailuresFor { get; set; }

    /// <summary>Runs once at the start of the next ReadEnvironmentVariableAsync, for example to make the container exit.</summary>
    public Action? BeforeEnvironmentRead { get; set; }

    public Task<string?> ReadEnvironmentVariableAsync(string container, string name, CancellationToken cancellationToken)
    {
        var beforeRead = BeforeEnvironmentRead;
        BeforeEnvironmentRead = null;
        beforeRead?.Invoke();
        lock (_gate)
        {
            if (EnvironmentReadFailures > 0 && (EnvironmentReadFailuresFor is null || EnvironmentReadFailuresFor == name))
            {
                EnvironmentReadFailures--;
                return Task.FromException<string?>(new ContainerEngineException("docker container inspect timed out."));
            }

            return Task.FromResult(Find(container) is { } key && _containers.TryGetValue(key, out var found) && found.Environment?.TryGetValue(name, out var value) == true
                ? value
                : null);
        }
    }

    /// <summary>Runs once at the start of the next ReadFileAsync, for example to put another container under the name.</summary>
    public Action? BeforeRead { get; set; }

    public Task<string?> ReadFileAsync(string container, string path, CancellationToken cancellationToken)
    {
        var beforeRead = BeforeRead;
        BeforeRead = null;
        beforeRead?.Invoke();
        if (NotAnswering() is { } down)
        {
            return Task.FromException<string?>(down);
        }

        lock (_gate)
        {
            return Task.FromResult(Find(container) is { } key && _containers.TryGetValue(key, out var found) && found.Running
                ? StatusJson?.Replace(StatusNow, DateTimeOffset.UtcNow.ToString("O"), StringComparison.Ordinal)
                : null);
        }
    }

    /// <summary>Writes a line to the log stream of the container being followed.</summary>
    public void WriteLog(string line) => Volatile.Read(ref _logSink)?.Invoke(line);

    /// <summary>The tail each follow asked for, in order.</summary>
    public List<int> LogTails { get; } = [];

    /// <summary>Ends the current follow on its own, as when the engine restarts.</summary>
    public void DropLogs(Exception? failure = null)
    {
        TaskCompletionSource ended;
        lock (_gate)
        {
            ended = _logEnded;
        }

        if (failure is null)
        {
            ended.TrySetResult();
        }
        else
        {
            ended.TrySetException(failure);
        }
    }

    public async Task FollowLogsAsync(string container, int tail, Action<string> onLine, CancellationToken cancellationToken)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            LogTails.Add(tail);
            _logEnded = ended;
        }

        Volatile.Write(ref _logSink, onLine);
        if (tail > 0)
        {
            onLine(LogLine);
        }

        try
        {
            await ended.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Thrown by the next StopAsync only, as when docker stop passes its deadline once.</summary>
    public Exception? NextStopFailure { get; set; }

    /// <summary>Runs once at the start of the next StopAsync, for example to put another container under the name.</summary>
    public Action? BeforeStop { get; set; }

    public Task StopAsync(string container, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var beforeStop = BeforeStop;
        BeforeStop = null;
        beforeStop?.Invoke();

        if (NotAnswering() is { } down)
        {
            return Task.FromException(down);
        }

        lock (_gate)
        {
            if (NextStopFailure is { } failure)
            {
                NextStopFailure = null;
                return Task.FromException(failure);
            }

            Stops++;
            StopTimeouts.Add(timeout);
            StopExecutables.Add(Executable());
            if (Find(container) is { } key && _containers.TryGetValue(key, out var found))
            {
                _containers[key] = found with { Running = false, ExitCode = 0 };
            }
        }

        // The container's own shutdown takes a while, like a runner draining its work.
        return StopDuration > TimeSpan.Zero ? Task.Delay(StopDuration, cancellationToken) : Task.CompletedTask;
    }

    /// <summary>How long a stop takes.</summary>
    public TimeSpan StopDuration { get; set; }

    /// <summary>Thrown once, by the next forced removal.</summary>
    public ContainerEngineException? NextRemoveFailure { get; set; }

    /// <summary>Runs once, at the next forced removal, before it does anything (a container started at that moment, say).</summary>
    public Action? BeforeNextRemove { get; set; }

    /// <summary>Forced removals never return (until they are cancelled), and remove nothing.</summary>
    public bool RemovalHangs { get; set; }

    public Task RemoveAsync(string container, CancellationToken cancellationToken)
    {
        if (RemovalHangs)
        {
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (NotAnswering() is { } down)
        {
            return Task.FromException(down);
        }

        var before = BeforeNextRemove;
        BeforeNextRemove = null;
        before?.Invoke();

        if (NextRemoveFailure is { } failure)
        {
            NextRemoveFailure = null;
            return Task.FromException(failure);
        }

        lock (_gate)
        {
            if (Find(container) is { } key)
            {
                _containers.Remove(key);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Runs at every InspectContainerAsync, before it looks, with the name or ID asked for.</summary>
    public Action<string>? OnInspect { get; set; }

    /// <summary>Runs once, at the next InspectContainerAsync, before it looks (a container that exits at that moment, say).</summary>
    public Action? OnNextInspect
    {
        get => Volatile.Read(ref _onNextInspect);
        set => Volatile.Write(ref _onNextInspect, value);
    }

    private Action? _onNextInspect;

    /// <summary>What RemoveStoppedAsync fails with, as when the engine will not let go of a stopped container.</summary>
    public ContainerEngineException? RemoveStoppedFailure { get; set; }

    /// <summary>Leaves the container created and never started, as a crash between Docker creating and starting it does.</summary>
    public void MarkNeverStarted(string name)
    {
        lock (_gate)
        {
            _containers[Key(name)] = _containers[Key(name)] with { Running = false, ExitCode = null, NeverStarted = true };
        }
    }

    /// <summary>Runs once, at the next RemoveStoppedAsync, before it looks (a container started again meanwhile, say).</summary>
    public Action? BeforeNextRemoveStopped
    {
        get => Volatile.Read(ref _beforeNextRemoveStopped);
        set => Volatile.Write(ref _beforeNextRemoveStopped, value);
    }

    private Action? _beforeNextRemoveStopped;

    /// <summary>Awaited once, after the next RemoveStoppedAsync removed the container and before it says so.</summary>
    public Func<Task>? OnNextRemovedStopped
    {
        get => Volatile.Read(ref _onNextRemovedStopped);
        set => Volatile.Write(ref _onNextRemovedStopped, value);
    }

    private Func<Task>? _onNextRemovedStopped;

    public Task<bool> RemoveStoppedAsync(string container, CancellationToken cancellationToken)
    {
        var removal = RemoveStoppedCoreAsync(container);
        return removal.IsCompletedSuccessfully && removal.Result && Interlocked.Exchange(ref _onNextRemovedStopped, null) is { } hold
            ? HoldAsync(hold)
            : removal;

        static async Task<bool> HoldAsync(Func<Task> hold)
        {
            await hold();
            return true;
        }
    }

    private Task<bool> RemoveStoppedCoreAsync(string container)
    {
        if (NotAnswering() is { } down)
        {
            return Task.FromException<bool>(down);
        }

        Interlocked.Exchange(ref _beforeNextRemoveStopped, null)?.Invoke();
        if (RemoveStoppedFailure is { } kept)
        {
            return Task.FromException<bool>(kept);
        }

        lock (_gate)
        {
            if (Find(container) is { } key && _containers.TryGetValue(key, out var found))
            {
                if (found.Running)
                {
                    return Task.FromResult(false);
                }

                _containers.Remove(key);
            }
        }

        return Task.FromResult(true);
    }

    private int _gpuChecks;

    /// <summary>How often the GPU check ran.</summary>
    public int GpuChecks => Volatile.Read(ref _gpuChecks);

    /// <summary>What the GPU check answers.</summary>
    public GpuAccessCheck GpuCheck { get; set; } = new(true, $"GPU 0: NVIDIA GeForce RTX 4090 (UUID: {GpuUuid})");

    /// <summary>What the GPU check answers for containers of one image, when it is not <see cref="GpuCheck"/>.</summary>
    public Func<string, GpuAccessCheck?>? GpuCheckOf { get; set; }

    /// <summary>The UUID the GPU check lists, as `nvidia-smi -L` prints it.</summary>
    public const string GpuUuid = "GPU-aaaaaaaa-1111-2222-3333-444444444444";

    /// <summary>The images the GPU check ran in, in order.</summary>
    public ConcurrentQueue<string> GpuCheckImages { get; } = new();

    /// <summary>What ListContainerGpusAsync reports: the GPUs a container on this engine gets.</summary>
    public IReadOnlyList<HostGpu> ContainerGpus { get; set; } = [new HostGpu("NVIDIA GeForce RTX 4090", 24564, GpuUuid)];

    /// <summary>How often the containers were asked for their GPUs.</summary>
    public int ContainerGpuLists => Volatile.Read(ref _containerGpuLists);

    private int _containerGpuLists;

    /// <summary>The images ListContainerGpusAsync was asked with, in order.</summary>
    public ConcurrentQueue<string> ContainerGpuImages { get; } = new();

    /// <summary>Awaited inside every listing of the containers' GPUs, with how many there have been (to hold one, say).</summary>
    public Func<int, Task>? OnContainerGpuList { get; set; }

    public async Task<IReadOnlyList<HostGpu>> ListContainerGpusAsync(string image, CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref _containerGpuLists);
        ContainerGpuImages.Enqueue(image);

        // (What it lists is what was there when it was asked: a change made by the hook comes after.)
        var listed = ContainerGpus;
        if (OnContainerGpuList is { } onList)
        {
            await onList(count);
        }

        return listed;
    }

    /// <summary>Awaited once, inside the next GPU check (to hold it while Settings change, say).</summary>
    public Func<Task>? OnNextGpuCheck { get; set; }

    public async Task<GpuAccessCheck> CheckGpuAccessAsync(string image, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _gpuChecks);
        GpuCheckImages.Enqueue(image);
        var hold = OnNextGpuCheck;
        OnNextGpuCheck = null;
        if (hold is not null)
        {
            await hold();
        }

        return GpuCheckOf?.Invoke(image) ?? GpuCheck;
    }

    /// <summary>The engine executable in use; each engine sees only the containers it created.</summary>
    public Func<string?> Executable { get; set; } = static () => null;

    public List<string?> RunExecutables { get; } = [];

    public List<string?> StopExecutables { get; } = [];

    public bool ExistsOn(string? executable, string name)
    {
        lock (_gate)
        {
            return _containers.ContainsKey((executable ?? string.Empty) + "|" + name);
        }
    }

    /// <summary>Removes a container on <paramref name="executable"/>'s engine, as someone at that engine does (whether or not it answers this app).</summary>
    public void RemoveOn(string? executable, string name)
    {
        lock (_gate)
        {
            _containers.Remove((executable ?? string.Empty) + "|" + name);
        }
    }

    /// <summary>Commands on an engine in <see cref="UnavailableEngines"/> fail, as when Docker quit.</summary>
    private ContainerEngineException? NotAnswering()
        => Executable() is { } executable && UnavailableEngines.ContainsKey(executable)
            ? new ContainerEngineException($"{executable} does not answer.")
            : null;

    private string Key(string name) => (Executable() ?? string.Empty) + "|" + name;

    /// <summary>The container a name or (full or short) ID refers to on the current engine, as the CLI resolves it.</summary>
    private string? Find(string nameOrId)
    {
        if (_containers.ContainsKey(Key(nameOrId)))
        {
            return Key(nameOrId);
        }

        var prefix = Key(string.Empty);
        var byId = _containers
            .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                            entry.Value.Id is { } id &&
                            id.StartsWith(nameOrId, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key)
            .FirstOrDefault();
        return byId;
    }

    /// <summary>The image ID a reference names locally; set one to simulate a tag that moved.</summary>
    public ConcurrentDictionary<string, string> ImageIds { get; } = new(StringComparer.Ordinal);

    private string ImageId(string image)
        => ImageIds.TryGetValue(image, out var id) ? id : image.StartsWith("sha256:", StringComparison.Ordinal) ? image : "sha256:local";

    private sealed record FakeContainer(string Image, bool Running, int? ExitCode, IReadOnlyDictionary<string, string> Labels, string ImageId, IReadOnlyDictionary<string, string>? Environment = null, string? Id = null)
    {
        /// <summary>Created and never started, as Docker leaves a container whose GPU it could not attach.</summary>
        public bool NeverStarted { get; init; }
    }
}

/// <summary>
/// A launcher link the test drives: a run goes online at once and lasts until cancelled or
/// <see cref="End"/>; <see cref="Name"/> names a launch on a live stream.
/// </summary>
internal sealed class FakeLauncherLink : ILauncherLink
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, LaunchReport> _outcomes = new(StringComparer.OrdinalIgnoreCase);
    private LauncherLinkState _state = LauncherLinkState.Offline;
    private TaskCompletionSource<LauncherLinkState>? _end;
    private long _run;
    private long _runCount;
    private ulong _sequence;
    private string? _target;

    public LauncherLinkState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public event EventHandler? StateChanged;

    /// <summary>The settings each run was started with, in order.</summary>
    public ConcurrentQueue<RunnerSettings> Runs { get; } = new();

    /// <summary>Final states the next runs end with at their first heartbeat, in order (a takeover found then, say).</summary>
    public ConcurrentQueue<LauncherLinkState> FirstHeartbeats { get; } = new();

    /// <summary>Runs once, as the next run registers, before Offloadr accepts it (a Stop pressed then, say).</summary>
    public Action? BeforeNextOnline { get; set; }

    /// <summary>How many runs Offloadr accepted.</summary>
    public int Onlines => Volatile.Read(ref _onlines);

    private int _onlines;

    /// <summary>Runs once, as the next run is cancelled (a Stop pressed at that moment, say).</summary>
    public Action? OnNextCancel { get; set; }

    /// <summary>Final states the next cancelled runs end with, in order (a heartbeat refused as the run stops, say).</summary>
    public ConcurrentQueue<LauncherLinkState> CancelEndsWith { get; } = new();

    public ConcurrentQueue<LaunchReport> Reports { get; } = new();

    public ConcurrentQueue<(string LaunchId, string? ExecutorInstanceId, string Target)> Releases { get; } = new();

    /// <summary>How Offloadr answers a release.</summary>
    public Func<string, ReleaseResult> ReleaseAnswer { get; set; } = _ => new ReleaseResult(true, true, string.Empty);

    /// <summary>Awaited inside ReleaseAsync, for example to hold a release while something else happens.</summary>
    public Func<CancellationToken, Task>? OnRelease { get; set; }

    /// <summary>False holds every start, as while the stream is down.</summary>
    public bool StartsAllowed { get; set; } = true;

    /// <summary>Has whoever listens look at the link again, as any change of its state does.</summary>
    public void Touch() => StateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>The profile the running run reads for its heartbeats.</summary>
    public Func<LauncherProfile>? Profile { get; private set; }

    /// <summary>A run is online: its stream may name launches.</summary>
    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _run != 0 && _state.Connection == LauncherConnection.Online;
            }
        }
    }

    public string? LaunchTarget
    {
        get
        {
            lock (_gate)
            {
                return _run == 0 ? null : _target;
            }
        }
    }

    /// <summary>The launch target a run with <paramref name="settings"/> has: never the key itself.</summary>
    public static string TargetOf(RunnerSettings settings) => RunnerSupervisor.LaunchTargetFor(settings);

    public async Task RunAsync(RunnerSettings settings, Func<LauncherProfile> profile, CancellationToken cancellationToken)
    {
        TaskCompletionSource<LauncherLinkState> end;
        lock (_gate)
        {
            if (_run != 0)
            {
                throw new InvalidOperationException("The launcher link is already running.");
            }

            _run = Interlocked.Increment(ref _runCount);
            _target = TargetOf(settings);
            end = _end = new TaskCompletionSource<LauncherLinkState>(TaskCreationOptions.RunContinuationsAsynchronously);
            Profile = profile;
        }

        Runs.Enqueue(settings);
        try
        {
            Set(_ => new LauncherLinkState(LauncherConnection.Connecting, null, false, null));
            profile();
            if (FirstHeartbeats.TryDequeue(out var final))
            {
                Set(_ => final);
                return;
            }

            var beforeOnline = BeforeNextOnline;
            BeforeNextOnline = null;
            beforeOnline?.Invoke();
            if (cancellationToken.IsCancellationRequested)
            {
                // Ended before Offloadr accepted it.
                Set(_ => LauncherLinkState.Offline);
                return;
            }

            // A launch named meanwhile stays named.
            Interlocked.Increment(ref _onlines);
            Set(state => state with { Connection = LauncherConnection.Online, Message = null, StreamLive = true });
            try
            {
                var ended = await end.Task.WaitAsync(cancellationToken);
                Set(_ => ended);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var onCancel = OnNextCancel;
                OnNextCancel = null;
                onCancel?.Invoke();
                Set(_ => CancelEndsWith.TryDequeue(out var last) ? last : LauncherLinkState.Offline);
            }
        }
        finally
        {
            lock (_gate)
            {
                _run = 0;
            }
        }
    }

    /// <summary>
    /// Names a launch on the live stream, with a higher sequence than any before, checked against the
    /// offer the run sends now (or <paramref name="offerRevision"/>, an older one, say).
    /// </summary>
    public LaunchRequest Name(string editorTemplate, string runnerImage, ulong? offerRevision = null)
    {
        var revision = offerRevision ?? Profile?.Invoke().OfferRevision ?? 0;
        LaunchRequest launch;
        lock (_gate)
        {
            launch = new LaunchRequest(Guid.NewGuid().ToString(), ++_sequence, editorTemplate, runnerImage, revision) { Run = _run };
        }

        Set(state => state with { StreamLive = true, Launch = launch });
        return launch;
    }

    /// <summary>Names a launch again on the live stream, as Offloadr does with one still pending for this launcher instance.</summary>
    public LaunchRequest NameAgain(LaunchRequest launch)
    {
        LaunchRequest again;
        lock (_gate)
        {
            again = launch with { Run = _run };
        }

        Set(state => state with { StreamLive = true, Launch = again });
        return again;
    }

    /// <summary>The latest state names no launch.</summary>
    public void Withdraw() => Set(state => state with { Launch = null });

    /// <summary>The registration answers differently (refused, unreachable) while the run goes on.</summary>
    public void SetConnection(LauncherConnection connection, string? message)
        => Set(state => state with { Connection = connection, Message = message, StreamLive = connection == LauncherConnection.Online });

    /// <summary>The stream breaks (or comes back): the launch stays named, and nothing may start meanwhile.</summary>
    public void SetStreamLive(bool live) => Set(state => state with { StreamLive = live });

    /// <summary>Ends the running run with a final state, as Offloadr refusing it for good does.</summary>
    public void End(LauncherConnection connection, string message)
    {
        TaskCompletionSource<LauncherLinkState>? end;
        lock (_gate)
        {
            end = _end;
        }

        end?.TrySetResult(new LauncherLinkState(connection, message, false, null));
    }

    /// <summary>The offer_revision a heartbeat was last accepted with: the run's profile, unless held back.</summary>
    public ulong AcceptedOfferRevision => HoldAcceptedOfferRevision ?? (Running ? Profile?.Invoke().OfferRevision ?? 0 : 0);

    /// <summary>Holds the accepted offer_revision back (a heartbeat with the new one not answered yet).</summary>
    public ulong? HoldAcceptedOfferRevision { get; set; }

    public bool MayStart(LaunchRequest launch, long? streamDrops = null)
    {
        lock (_gate)
        {
            return (streamDrops is not { } checkedAt || checkedAt == StreamDrops) && StartsAllowed && _run != 0 && launch.Run == _run && _state.StreamLive && _state.Launch == launch &&
                   !_outcomes.ContainsKey(launch.LaunchId);
        }
    }

    /// <summary>Sees every report as it is submitted.</summary>
    public Action<LaunchReport>? ReportObserver { get; set; }

    /// <summary>Runs once, as the next start is asked for, before it is checked (a Stop pressed at that moment, say).</summary>
    public Action? OnNextTryStart { get; set; }

    public bool TryStart(LaunchRequest launch, Action dispatch, long? streamDrops = null)
    {
        var before = OnNextTryStart;
        OnNextTryStart = null;
        before?.Invoke();
        lock (_gate)
        {
            if (!MayStart(launch) || (streamDrops is { } checkedAt && checkedAt != StreamDrops))
            {
                return false;
            }

            dispatch();
            return true;
        }
    }

    /// <summary>Runs once, as the next terminal report waits to be submitted (the link is busy), before it is taken.</summary>
    public Action? OnNextOutcomeSubmitted { get; set; }

    /// <summary>Runs as progress is sent, on the thread that sends it (one that stalls, say).</summary>
    public Action<LaunchReport>? OnProgress { get; set; }

    /// <summary>While set and not completed, outcomes are submitted and not delivered yet: the link keeps sending them.</summary>
    public Task? OutcomeGate { get; set; }

    public async Task ReportAsync(LaunchReport report, CancellationToken cancellationToken)
    {
        if (report.IsTerminal && OnNextOutcomeSubmitted is { } waiting)
        {
            OnNextOutcomeSubmitted = null;
            waiting();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // As the real link: a cancelled submission records nothing.
            return;
        }

        if (!report.IsTerminal)
        {
            if (OnProgress is { } onProgress)
            {
                onProgress(report);
            }

            // As the real link: progress never follows an outcome.
            if (_outcomes.ContainsKey(report.LaunchId))
            {
                return;
            }
        }

        ReportObserver?.Invoke(report);
        if (!report.IsTerminal)
        {
            Reports.Enqueue(report);
            return;
        }

        if (_outcomes.TryAdd(report.LaunchId, report))
        {
            Reports.Enqueue(report);
        }

        if (OutcomeGate is { } gate)
        {
            // Recorded, and resent until it is delivered (or the wait for that is given up).
            await gate.WaitAsync(cancellationToken);
        }
    }

    public LaunchReport? OutcomeOf(string launchId) => _outcomes.TryGetValue(launchId, out var outcome) ? outcome : null;

    /// <summary>The phases reported for <paramref name="launch"/>, in order.</summary>
    public IReadOnlyList<RunnerLaunchPhase> PhasesOf(LaunchRequest launch)
        => [.. Reports.Where(report => report.LaunchId == launch.LaunchId).Select(report => report.Phase)];

    /// <summary>The releases sent with container_exited: containers reported gone.</summary>
    public ConcurrentQueue<(string LaunchId, string? ExecutorInstanceId, string Target)> ExitReports { get; } = new();

    /// <summary>Awaited as a report of a gone container is sent, before it is taken (to hold one that Offloadr does not answer, say).</summary>
    public Func<string, CancellationToken, Task>? OnExitReport { get; set; }

    /// <summary>How Offloadr answers a release that reports a container gone.</summary>
    public Func<string, ReleaseResult> ExitReportAnswer { get; set; } = _ => new ReleaseResult(true, true, string.Empty);

    /// <summary>Runs once, as the next release is about to be sent (a withdrawal at that moment, say).</summary>
    public Action? BeforeNextRelease { get; set; }

    public async Task<ReleaseResult> ReleaseAsync(string launchId, string? executorInstanceId, string launchTarget, CancellationToken cancellationToken,
        LaunchRequest? onlyFor = null, CancellationToken sendUnless = default, bool containerExited = false, RunnerSettings? reportAs = null,
        long? streamDrops = null)
    {
        if (containerExited)
        {
            // Reports a container that is gone: answered on its own, so release counts stay as they were.
            // Sent by the run for that target, or as the settings that name it (no registration needed).
            if ((launchTarget != LaunchTarget || !Running) && (reportAs is null || TargetOf(reportAs) != launchTarget))
            {
                return new ReleaseResult(false, false, "Not connected.");
            }

            if (OnExitReport is { } onExitReport)
            {
                await onExitReport(launchId, cancellationToken);
            }

            ExitReports.Enqueue((launchId, executorInstanceId, launchTarget));
            return ExitReportAnswer(launchId);
        }

        var before = BeforeNextRelease;
        BeforeNextRelease = null;
        before?.Invoke();
        lock (_gate)
        {
            if ((onlyFor is not null && (!MayStart(onlyFor) || (streamDrops is { } checkedAt && checkedAt != StreamDrops))) || sendUnless.IsCancellationRequested)
            {
                return new ReleaseResult(false, false, "No longer named.", Sent: false);
            }

            Releases.Enqueue((launchId, executorInstanceId, launchTarget));
        }

        if (OnRelease is { } onRelease)
        {
            await onRelease(cancellationToken);
        }

        // Asked by the run for that target, or, with no run for it, as the settings that name it.
        return launchTarget == LaunchTarget || (reportAs is not null && onlyFor is null && TargetOf(reportAs) == launchTarget)
            ? ReleaseAnswer(launchId)
            : new ReleaseResult(false, false, "Another target.", Sent: false);
    }

    private void Set(Func<LauncherLinkState, LauncherLinkState?> change)
    {
        lock (_gate)
        {
            var next = change(_state);
            if (next is null || next == _state)
            {
                return;
            }

            if (_state.StreamLive && !next.StreamLive)
            {
                Interlocked.Increment(ref _streamDrops);
            }

            _state = next;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private long _streamDrops;

    public long StreamDrops => Interlocked.Read(ref _streamDrops);
}

internal sealed class FakeLaunchPlanProvider : ILaunchPlanProvider
{
    private LaunchPlan _plan = BuiltInLaunchPlanCatalog.Plan();

    public LaunchPlan Plan
    {
        get => Volatile.Read(ref _plan);
        set => Volatile.Write(ref _plan, value);
    }

    /// <summary>Awaited inside GetAsync, for example to hold a refresh while more requests queue up.</summary>
    public Func<CancellationToken, Task>? OnGet { get; set; }

    /// <summary>Settings whose key the control plane rejects.</summary>
    public Func<RunnerSettings, bool>? Rejects { get; set; }

    private int _calls;

    /// <summary>How often the plan was asked for.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public async Task<LaunchPlan> GetAsync(RunnerSettings settings, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (OnGet is { } onGet)
        {
            await onGet(cancellationToken);
        }

        if (Rejects?.Invoke(settings) == true)
        {
            throw new LaunchPlanRejectedException("Offloadr did not accept the runner key.");
        }

        return Plan;
    }
}

internal sealed class FakeHostGpuProbe : IHostGpuProbe
{
    /// <summary>What the probe lists.</summary>
    public HostGpuReport Report { get; set; } = new([new HostGpu("NVIDIA GeForce RTX 4090", 24564)], null);

    private int _probes;

    /// <summary>How often this computer's GPUs were listed.</summary>
    public int Probes => Volatile.Read(ref _probes);

    public Task<HostGpuReport> ProbeAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _probes);
        return Task.FromResult(Report);
    }
}

internal sealed class FakeSettingsStore(RunnerSettings settings) : IRunnerSettingsStore
{
    private RunnerSettings _current = settings;

    public RunnerSettings Current => Volatile.Read(ref _current);

    public event Action<RunnerSettings>? Changed;

    public Task SaveAsync(RunnerSettings settings, CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _current, settings);
        Changed?.Invoke(settings);
        return Task.CompletedTask;
    }
}

internal sealed class ReversingSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => "protected:" + new string(plaintext.Reverse().ToArray());

    public string Unprotect(string protectedValue)
        => protectedValue.StartsWith("protected:", StringComparison.Ordinal)
            ? new string(protectedValue["protected:".Length..].Reverse().ToArray())
            : throw new InvalidOperationException("The key ring does not match.");
}

/// <summary>Answers process calls from a script keyed by the first argument.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<string, ProcessResult> _results = new(StringComparer.Ordinal);

    public List<ProcessSpec> Calls { get; } = [];

    /// <summary>Answers calls whose first argument (or first two, such as "context inspect") match.</summary>
    public FakeProcessRunner On(string firstArguments, int exitCode, string stdout = "", string stderr = "")
    {
        _results[firstArguments] = new ProcessResult(exitCode, stdout, stderr);
        return this;
    }

    /// <summary>Awaited before answering a call, for example to hang until the call is cancelled, or to throw.</summary>
    public Func<ProcessSpec, CancellationToken, Task>? OnRun { get; set; }

    /// <summary>Answers a call by what it asks, before the answers set with <see cref="On"/>; null leaves it to those.</summary>
    public Func<ProcessSpec, ProcessResult?>? Answer { get; set; }

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add(spec);
        }

        if (OnRun is { } onRun)
        {
            await onRun(spec, cancellationToken);
        }

        if (Answer?.Invoke(spec) is { } answer)
        {
            return answer;
        }

        if (spec.Arguments.Count > 1 && _results.TryGetValue(spec.Arguments[0] + " " + spec.Arguments[1], out var specific))
        {
            return specific;
        }

        var key = spec.Arguments.Count > 0 ? spec.Arguments[0] : string.Empty;
        return _results.TryGetValue(key, out var result) ? result : new ProcessResult(0, string.Empty, string.Empty);
    }

    public async Task<int> StreamAsync(ProcessSpec spec, Action<string> onLine, CancellationToken cancellationToken)
    {
        var result = await RunAsync(spec, cancellationToken);
        foreach (var line in (result.StandardOutput + result.StandardError).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            onLine(line.TrimEnd('\r'));
        }

        return result.ExitCode;
    }
}

/// <summary>The system clock, with a wall clock that can be moved (as after a resume or a correction).</summary>
internal sealed class ShiftedTimeProvider : TimeProvider
{
    private long _offsetTicks;

    public TimeSpan Offset
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
        set => Interlocked.Exchange(ref _offsetTicks, value.Ticks);
    }

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
}

/// <summary>An update feed with one release, which downloads in three steps.</summary>
internal sealed class FakeAppUpdateSource : Offloadr.Runner.Supervisor.Hosting.IAppUpdateSource
{
    public bool IsInstalled { get; set; } = true;

    public Version? Pending { get; set; }

    public Version? Latest { get; set; }

    public Exception? CheckFailure { get; set; }

    public int Checks { get; private set; }

    public int Downloads { get; private set; }

    public IReadOnlyList<string>? AppliedWith { get; private set; }

    public bool? Restarts { get; private set; }

    public Task<Version?> CheckAsync(CancellationToken cancellationToken)
    {
        Checks++;
        return CheckFailure is { } failure ? Task.FromException<Version?>(failure) : Task.FromResult(Latest);
    }

    public Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken)
    {
        Downloads++;
        progress(1);
        progress(3);
        progress(50);
        progress(100);
        return Task.CompletedTask;
    }

    public void ApplyAfterExit(bool restart, IReadOnlyList<string> arguments)
    {
        Restarts = restart;
        AppliedWith = arguments;
    }
}
