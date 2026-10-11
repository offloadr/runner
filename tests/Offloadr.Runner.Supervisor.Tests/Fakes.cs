using System.Collections.Concurrent;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hardware;
using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Processes;
using Offloadr.Runner.Supervisor.Settings;

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

    /// <summary>Thrown by ProbeAsync, for example while Docker is still starting.</summary>
    public Exception? ProbeFailure { get; set; }

    /// <summary>Thrown by InspectContainerAsync.</summary>
    public Exception? InspectFailure { get; set; }

    /// <summary>Awaited inside RunDetachedAsync after the container exists, for example to hold the CLI until it is cancelled.</summary>
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
        if (OnPull is not null)
        {
            await OnPull(cancellationToken);
        }

        if (PullFails)
        {
            throw new ContainerEngineException($"Could not pull {image}.", "registry unreachable");
        }

        lock (_gate)
        {
            _images.Add(image);
        }
    }

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
            return Task.FromResult(_images.Contains(image) ? new ImageInfo(ImageId(image), image + "@sha256:digest") : null);
        }
    }

    public void AddImage(string image)
    {
        lock (_gate)
        {
            _images.Add(image);
        }
    }

    /// <summary>The next RunDetachedAsync finds the name taken by a container this app did not create.</summary>
    public bool NextRunConflict { get; set; }

    /// <summary>The labels of the container that wins the name in a run conflict (none: someone else's).</summary>
    public IReadOnlyDictionary<string, string> ConflictLabels { get; set; } = new Dictionary<string, string>();

    /// <summary>Thrown by the next RunDetachedAsync only, before anything is created (the executable did not start).</summary>
    public Exception? NextRunStartFailure { get; set; }

    /// <summary>Thrown by the next RunDetachedAsync only, after it created the container.</summary>
    public Exception? NextRunFailure { get; set; }

    /// <summary>Runs once at the start of the next RunDetachedAsync, for example to move a tag.</summary>
    public Action? BeforeRun { get; set; }

    /// <summary>Replaces the whole of RunDetachedAsync, for example to create the container only later.</summary>
    public Func<ContainerSpec, CancellationToken, Task<string>>? RunOverride { get; set; }

    public async Task<string> RunDetachedAsync(ContainerSpec spec, CancellationToken cancellationToken)
    {
        if (RunOverride is { } runOverride)
        {
            return await runOverride(spec, cancellationToken);
        }

        var beforeRun = BeforeRun;
        BeforeRun = null;
        beforeRun?.Invoke();
        string id;
        lock (_gate)
        {
            if (_containers.ContainsKey(Key(spec.Name)))
            {
                throw new ContainerEngineException("The container name is already in use.");
            }

            Runs.Add(spec);
            RunExecutables.Add(Executable());
            if (NextRunConflict)
            {
                // Someone else created a container of that name just before; the run fails on the name.
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

            if (NextRunFailure is { } failure)
            {
                // Created but never started, as when the GPU cannot be attached.
                NextRunFailure = null;
                _containers[Key(spec.Name)] = new FakeContainer(spec.Image, false, null, new Dictionary<string, string>(spec.Labels), ImageId(spec.Image), Id: $"runner-container-{Runs.Count:D4}");
                throw failure;
            }

            id = $"runner-container-{Runs.Count:D4}";
            _containers[Key(spec.Name)] = new FakeContainer(spec.Image, true, null, new Dictionary<string, string>(spec.Labels), ImageId(spec.Image), new Dictionary<string, string>(spec.Environment), id);
        }

        if (OnRun is not null)
        {
            await OnRun(cancellationToken);
        }

        return id;
    }

    public void AddContainer(string name, string image, bool running, IReadOnlyDictionary<string, string> labels, IReadOnlyDictionary<string, string>? environment = null)
    {
        lock (_gate)
        {
            _containers[Key(name)] = new FakeContainer(image, running, running ? null : 0, labels, ImageId(image), environment, $"added-container-{Guid.NewGuid():N}");
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

        lock (_gate)
        {
            return Task.FromResult(Find(name) is { } key && _containers.TryGetValue(key, out var container)
                ? new ContainerInfo(container.Id ?? "id-" + name, container.Image, container.Running, container.ExitCode, container.Labels, container.ImageId)
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

    public Task RemoveAsync(string container, CancellationToken cancellationToken)
    {
        if (NotAnswering() is { } down)
        {
            return Task.FromException(down);
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

    public Task<GpuAccessCheck> CheckGpuAccessAsync(string image, CancellationToken cancellationToken)
        => Task.FromResult(new GpuAccessCheck(true, "GPU 0: NVIDIA GeForce RTX 4090"));

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
        return _containers
            .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                            entry.Value.Id is { } id &&
                            id.StartsWith(nameOrId, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key)
            .FirstOrDefault();
    }

    /// <summary>The image ID a reference names locally; set one to simulate a tag that moved.</summary>
    public ConcurrentDictionary<string, string> ImageIds { get; } = new(StringComparer.Ordinal);

    private string ImageId(string image) => ImageIds.TryGetValue(image, out var id) ? id : "sha256:local";

    private sealed record FakeContainer(string Image, bool Running, int? ExitCode, IReadOnlyDictionary<string, string> Labels, string ImageId, IReadOnlyDictionary<string, string>? Environment = null, string? Id = null);
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
    public Task<HostGpuReport> ProbeAsync(CancellationToken cancellationToken)
        => Task.FromResult(new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564)], null));
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

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        Calls.Add(spec);
        if (OnRun is { } onRun)
        {
            await onRun(spec, cancellationToken);
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
