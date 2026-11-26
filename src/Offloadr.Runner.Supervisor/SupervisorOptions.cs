namespace Offloadr.Runner.Supervisor;

public sealed class SupervisorOptions
{
    public string ContainerName { get; init; } = "offloadr-runner";

    public string StatusFilePath { get; init; } = "/run/offloadr/status.json";

    public TimeSpan MonitorInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The agent rewrites its status file at least every 15 seconds. A snapshot that has not changed
    /// for this long, by this machine's clock, says nothing about whether a session is active.
    /// </summary>
    public TimeSpan StatusStaleAfter { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The runner's stop grace period when Windows shuts down or the person signs out: Windows ends the app
    /// within seconds, and Docker stops with it.
    /// </summary>
    public TimeSpan FastStopTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a container that was started counts as not matched by its executor when nothing says otherwise
    /// (the launcher went offline): Offloadr itself gives up the launch well before that.
    /// </summary>
    public TimeSpan LaunchMatchLimit { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>What closing the app may take beyond the runner's stop grace period: what is under way, the release, the look and the removal around the stop.</summary>
    public TimeSpan ShutdownMargin { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The same, when Windows shuts down or the person signs out.</summary>
    public TimeSpan FastShutdownMargin { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// After a Stop cancels a creation, how long the engine may still be making the container the
    /// command asked for; the cleanup watches for it this long.
    /// </summary>
    public TimeSpan CancelledLaunchSettle { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Backoff of an automatic attempt to make the runner available (Docker still starting, say).</summary>
    public TimeSpan RetryMinBackoff { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan RetryMaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a container stays after its last session, so the next one on the same editor starts at once.</summary>
    public TimeSpan WarmTime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long creating the container, and then starting it, may each take before it is given up.</summary>
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a container is still looked for after it was let go of unseen (a cancelled or given-up
    /// creation may still make it): one that shows meanwhile was never started, and is removed.
    /// </summary>
    public TimeSpan LateContainerWatch { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How often Docker is checked while the computer is available with no container.</summary>
    public TimeSpan EngineCheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a Stop or Quit waits for Offloadr to release a container before stopping it anyway.</summary>
    public TimeSpan StopReleaseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a launch chosen for an older offer waits, in flight, for Offloadr to accept a heartbeat with
    /// the current one (which retires it) before it is reported FAILED all the same.
    /// </summary>
    public TimeSpan StaleOfferWait { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How soon a release that could not reach Offloadr is asked again.</summary>
    public TimeSpan ReleaseRetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    public int LogCapacity { get; init; } = 2000;

    public bool StopContainerOnShutdown { get; init; } = true;
}

