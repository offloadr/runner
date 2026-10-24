# Architecture

## Expired prompt receipts

Prompt execution permission includes explicit content availability. An expired
or unspecified receipt cannot start generation, probe native recovery, prepare
artifacts or create another acknowledgement, even if its permission flag is
contradictory. Recorded permission and outcome facts remain distinct from access
to the original content. Unspecified or unknown availability is an explicit
protocol error, rather than a silent successful return. Recovery-only command lifetimes, local memory and spool
cleanup still require their own finite retention enforcement; this check alone
does not establish complete data deletion.

## Purpose

The runner executes GPU work for the Offloadr control plane. It connects out to the control plane over gRPC, authenticated by a runner key, and never exposes an inbound port. One runner serves one editor session at a time.

The same container images run on users' own GPUs (personal runners) and on GPUs Offloadr rents (hosted runners). Both are built from this repository by `docker-bake.hcl`.

## Inside the container

The agent is the container's entry point and runs as root. It:

1. Sends `RegisterRunner` heartbeats with its resources and the editor templates the image supports. Heartbeats are the only liveness signal.
2. Holds a `CommandStream` open and executes start, stop, prompt, restart, relay and GPU power commands with exact identities, acknowledging each one idempotently.
3. Starts the editor (ComfyUI or Forge Neo) per session as a child process under a dedicated Linux user, with a private virtualenv layered on the image's root-owned one.
4. Relays editor HTTP requests and websocket frames over the command and event lanes, uploads outputs, and mirrors workspace files (`user`, `custom_nodes`, inputs) from the control plane.
5. Downloads models with aria2, which runs as an unprivileged account and writes only to directories prepared for it; downloads into a session home are staged outside it and moved in by the agent. For ComfyUI, model files start as sparse placeholders; `liboffloadr_model_vfs.so` is preloaded into the editor and blocks each read until aria2 has the bytes it needs (demand-aware hydration).
6. Writes `/run/offloadr/status.json` so a local supervisor can tell whether a session is active.
7. In sealed mode, keeps each session's user data under a root-owned directory on a memory-backed root (`/dev/shm/offloadr` by default), stages workspace seeds there, keeps editor output off the container log, makes session processes non-dumpable and evicts uploaded artifacts when the root runs low. Code, models and compiled caches stay in the disk-backed home. Transfers are sealed with per-session reader keys and the control plane's output recipient key (`docs/sealed-envelope.md`, `Offloadr.Runner.Core/Sealing`). See "Sealed Storage" in `hosting.md`.

### gRPC channel lifetime

Each channel created by `GrpcChannelManager` owns its HTTP handler and pooled
connections, including the optional tracing wrapper. Disposing the channel
disposes that handler chain and closes its connections. A successful RPC leaves
the connection pooled until channel disposal or another transport lifetime event;
channel callers must retain and dispose their channels at the owning lifetime.

### Transient command redelivery

Transient HTTP and client-message commands are admitted by request ID and an
immutable SHA-256 fingerprint of the serialized command. Only delivery ID and
attempt are excluded: transport redelivery can change those fields. A changed
payload, destination, runtime identity, kind or deadline under an existing ID is
rejected with 409 before joining an execution or replaying a response. Dispatch
captures its command before asynchronous execution, and a conflict leaves the
original operation and retained response intact.
Previously unseen requests addressed to another runner return 403 without
reserving their request identity or reaching a local runtime.

Completed replies and their 32-byte fingerprints share the existing 256-entry,
64 MiB serialized-result budget. Eviction drops both together. Separate bounded
executed markers still refuse execution when the reply has been evicted; these
markers last until the original deadline. This is process-local protection, not
persistent deduplication across a runner process replacement, nor proof that an
uncertain native mutation did not execute. In-flight delivery lists, transport
buffers and total process memory are separate resource boundaries.

## Artifact upload progress

Artifact uploads use a size-aware elapsed allowance across retries, with separate
metadata, body-progress and completion/receipt windows. Expiry cancels the actual
call without claiming rollback or a saved file; unchanged snapshots retain their
opaque upload ID. See [artifact upload budgets](artifact-upload-budgets.md) for
clocks, limits, Stop accounting and the remaining qualification boundary.

## Projects

| Project | Role | Platform |
| --- | --- | --- |
| Offloadr.Runner.Contracts | generated gRPC/protobuf code and shared helpers | any |
| Offloadr.Runner.Core | gRPC channels, runner-key auth, registration backoff, options, log and telemetry relays, aria2 backend and transfer index, status file | any |
| Offloadr.Runner.Linux | command loop, session process manager, Linux user isolation, VFS shim and IPC, hydration coordinator, workspace and artifact mirroring, local model projection, GPU power and telemetry | Linux |
| Offloadr.Runner.Agent | entry point and `Dockerfile` | Linux container |
| Offloadr.Runner.Supervisor | runs the runner container on a desktop: settings, Docker CLI, launch plans, supervision | Windows, macOS, Linux |
| Offloadr.Runner.UI | Razor pages for status, settings and logs | any Blazor host |
| Offloadr.Runner.Desktop | MAUI Blazor Hybrid window, encrypted key storage, close flow | Windows |

Core must not reference Linux. A future host that runs on Windows or macOS, such as a desktop supervisor or a mode that attaches to a locally running ComfyUI, references Core only.

### Known coupling

The split follows today's dependency graph. These services sit in Linux only because they still reference Linux-only types. Moving them to Core is the first step towards any non-container runtime.

| Service | Linux-only dependency |
| --- | --- |
| `ServiceClientManager` | `SessionProcessManager` (concrete), `GpuPowerCommandExecutor`, `LocalModelProjector`, `ResourceDetection` |
| `ArtifactUploadService` | `SessionProcessManager.SessionPaths` |
| `WorkspaceMirrorService` | `SessionProcessManager.SessionPaths`, `LinuxSecureDirectoryRoot`, `LinuxPathCanonicalizer` |
| `ModelDownloadService` | `DemandAwareModelHydrationCoordinator`, `LinuxFileIdentityReader`, `LocalModelProjector` |
| `ComfySessionEventRelay` | calls `ServiceClientManager` for acknowledgement retries |

## Supervisor

`RunnerSupervisor` keeps one runner container alive on a person's machine, for the desktop app. It never registers as a runner itself; the container does that with the same key.

1. It validates the settings, checks that Docker answers with Linux containers, and asks an `ILaunchPlanProvider` which image matches the person's editor template.
2. It pulls that image, falling back to a local copy when the registry is unreachable, and runs it detached as `offloadr-runner` with `--gpus all`. The runner key reaches Docker through the process environment, never the command line.
3. While running, it watches the container, follows its log, and reads the agent's status file. An unexpected exit restarts the container after a jittered backoff.
4. The image comes from `GetRunnerLaunchPlan` on `RunnerControlService`, authenticated with the runner key. It returns the templates, their images and the owner's selected editor template. Until a control plane implements it, the call fails with `UNIMPLEMENTED` and the supervisor uses the built-in list of published images with the template chosen in Settings. The server must treat this call as a catalog query, never as a heartbeat.
5. When the wanted image or settings change, it first downloads the new image in the background while the current runner keeps serving, then restarts once the status file shows no active session (read twice, a moment apart, right before the stop), and otherwise waits until the session ends. Without a status file it leaves the restart to the person. A session assigned in the instant between that last read and the stop can still be interrupted; closing that gap needs the control plane to hold back new assignments while the supervisor switches, which is not designed yet.
6. Containers carry labels with the editor template, a fingerprint of their configuration, the launch that created them and an ID kept in the app's data folder. On launch the supervisor adopts a matching container left running by an earlier session of the same app, and it never touches a container with the same name that it did not create, including a runner that another copy of the app (another data folder, or another computer using the same engine) started.

## Desktop app

The desktop app is a MAUI Blazor Hybrid window around `Offloadr.Runner.UI`, following the structure of a shared Razor UI with thin hosts. MAUI does not run hosted services, so `DesktopRuntime` starts them in order: a single-instance lock first, then the supervisor. The runner key is encrypted with ASP.NET Core Data Protection, whose key ring is protected with DPAPI. Settings, keys and logs live in `%LOCALAPPDATA%\OffloadrRunner`, or in `OFFLOADR_RUNNER_DATA_DIRECTORY` when set. The app lives in the Windows notification area: closing the window hides it, and the tray menu opens it, starts or stops the runner, or quits. `--minimized` starts it hidden; the Settings switch for starting at sign-in registers exactly that in the person's own Run key, and the app re-points the registration at itself if it has moved. The tray icon carries a dot for the runner state: green while running, amber while starting, downloading or restarting, red when it needs attention. Shortly after start and then daily, the app reads the latest release of this repository from the GitHub API and shows a banner with a download link when it is newer; it never downloads or installs anything itself. Quitting stops the runner container; the app asks first when the runner is active, and warns when a session would be interrupted. The tray icon uses `Shell_NotifyIcon` directly, with the app window subclassed to receive its messages.

## Artifact upload identity

Upload IDs are issued by the control plane and retained with the securely opened
snapshot through retries, queue removal and catalog refresh. The bounded uncertain
snapshot set applies visible backpressure rather than forgetting an old ID. Remote
deletion preserves the live file's last acknowledged binding while disabling its
eviction/readback promise. A returning catalog entry reuses a saved local version only when the full catalog receipt still matches, including its object key. A digest match independently proves the current bytes; a different receipt never grants eviction authority over old local bytes. Missing-path watcher events release retained capacity
and wake owed scans, including replacement of an entire output/temp root. Only
bindings beneath the event path receive filesystem inspection. Catalog readbacks
release their queued or inactive pre-ID budgets when no uncertain binding remains.
Catalog recognition preserves same-ID receipt recovery for uncertain unchanged
snapshots; a hash match cannot substitute for the operation receipt. A separate
bounded set keeps elapsed budgets across automatic
rescans, including failures before ID issuance; only a changed version or explicit
later refresh starts a new allowance. See [upload identity and retirement](artifact-upload-receipts.md).

## Origin

The agent was developed alongside the Offloadr control plane and moved here without its earlier history. Its behavior is unchanged apart from three additions: it reports its own version instead of the .NET runtime's, logs where its source is at startup, and writes the status file.
