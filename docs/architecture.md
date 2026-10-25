# Architecture

## Agent producer shutdown

The entry point starts command, runtime telemetry, sealed eviction and status-file
producers inside one agent lifetime. Factories are invoked within its cleanup
boundary, so partial setup still owns every task already started. A failed
producer requests peer cancellation; the command listener also fails the agent
if it ends before shutdown. Disabled optional producers may finish normally.

Shutdown callers share the original cancellation-callback task. The lifetime
joins all original producers and cancellation callbacks, then stops and joins
the session manager before returning to the scope that disposes sidecars and
channels. Cancellation does not count as a successful command acknowledgement.
An uncooperative producer holds shutdown open rather than leaving its work
running against disposed dependencies. Setup, producer, callback and session
cleanup errors remain failures. Signal registrations are removed before the
lifetime's cancellation source is disposed.

The command listener separately owns its original status, local-model and
command-stream tasks. A failed or prematurely ended required lane cancels its
peers. Partial attachment and producer setup still drain GPU work, prompts and
startup work; a failed drain cannot skip the others or later detachments. Download
progress callbacks share that lifetime's cancellation and admission gate. An old
captured reporter cannot admit work after shutdown, while admitted original
callbacks and cancellation callbacks must finish before detachment completes.

Startup publishes its original physical task before execution and keeps the
active slot until both execution and its original cancellation callbacks end.
Host shutdown, Stop, Quiesce and replacement share that callback ownership.
Joining never uses a cancelled waiting wrapper. A callback failure is reported
by class after the join and cannot skip runtime cleanup or enter an acknowledgement
message. Startup acknowledgement retries remain independent of that physical join.

Each command stream also owns its admitted start, launch, quiesce, GPU and
transient-request tasks, and the detached Stop and prompt acknowledgement tasks.
Stop's physical handler remains awaited in stream order. Stream reconnects keep
these owners; shutdown closes admission and joins original descendants before
disposing the request gates. A command failure cancels control peers immediately,
even when another original command is still being joined. Failure facts and idle
completed task records do not retain arbitrary exception details. Prompt ACK
retry factories are built outside the physical command callback's closure.
Controlled stream tests use the production sink with held fake RPC responses;
they do not establish real transport cancellation or physical deadline budgets.

All four control channels and their sink belong to the outer entry-point scope,
before any relay is created. They therefore remain alive through producer,
session, sidecar and relay cleanup. The final entry-point exception handler exits
nonzero and prints only the exception class. Other diagnostic paths still require
their own content, encryption and retention review.

Controlled factory and callback tests exercise this ownership order. They do not
establish complete signal handling with real transports, native descendants,
GPU workloads or provider shutdown deadlines.

The GPU executor publishes both activation and power-delivery roots before they
can execute, closes both admission paths under one gate, and shares one original
drain task between shutdown callers. Each operation owns its original cancellation
callbacks through completion. Drain joins those original operation and callback
roots before closing its cancellation source. Completed observation records keep
failure facts without retaining arbitrary action exceptions; shutdown reports a
content-free failure. Controlled tests cover held activation and grant callbacks,
peer cancellation, caller shutdown and managed reference release. They do not
establish real GPU effects, SDK deadlines, native cleanup or complete memory erasure.

## Sidecar operation ownership

Artifact and workspace services own public preparation and read operations before
publishing a session resource. Closing global admission prevents a late resource
from becoming active; already admitted preparation must finish its own cleanup.
Removing a resource from the active map retains its original stop task. Repeated
Stop and global disposal join that same owner, including a stop already in flight.
An old failed preparation cannot remove a replacement resource.

Each session resource closes admission before taking its operation snapshot.
Original initialization, catalog refresh, shared downloads, upload workers,
synchronous filesystem borrowers and admitted watcher callbacks remain owned
through completion and cancellation callbacks. A caller may stop waiting for a
shared read without transferring ownership away from the session. Partial setup
and repeated disposal use the same join. Watchers are removed again after all
borrowers finish so an earlier admitted callback cannot resurrect one after Stop.

The uploader may drain its queue briefly before cancellation. Slow-wait warnings
and bounded conservative loss accounting never complete the original stop.
An upload owns its response task from call construction, including when a request
write or stream completion fails. Cancellation requests are followed by joins;
cleanup errors do not replace the body's original failure. Failure of one sidecar
cannot skip the other's original join, and cleanup failure remains a content-free
failure rather than a successful Stop.

These ownership rules and controlled tests do not establish real SDK/HTTP2
deadline budgets, native account/key cleanup, GPU behavior, all-copy privacy,
backup/restore or release qualification.

## Runtime activity delivery preparation

The required activity report carries only captured process/runtime identity and
three sealing flags. Its transport constructs a fresh request for each attempt
and requires an accepted server timestamp for success. No output line, prompt,
result, exception detail or client timestamp belongs to this fact.

The process-scoped delivery primitive latches the first observation before
optional work. Concurrent signals share one fact. Transient transport failures
and NotReady retry the original values with capped backoff; exact success ends
delivery, while retired, invalid, conflicting or unknown outcomes fail closed.
Each attempt has a finite deadline. A separate finite admission budget starts at
the first observation using a monotonic clock and is never renewed by retries.
Disposal cancels and joins the original delivery task, including an uncooperative
attempt that has not yet returned. The process lifetime must consume rejection
or expiry and stop only its captured child.

The session manager captures this scope and the original transfer-sealing flags
before starting readers. The first nonempty read signals it before line buffering;
a bridge data frame signals only its matching current runtime before size limits,
parsing, suppression or optional enqueueing. Program owns a separate physical
channel, using the same runner-secret interceptor factory as the other control
lanes, and joins the asynchronous session manager before disposing the channel.
The existing ready timeout bounds activity admission from its first observation;
it remains separate from the physical readiness clock, heartbeat and prompt authority.

Admission has a separate decision and original-transport completion. Expiry can
stop the captured child while an uncooperative call remains pending, but cleanup
still cancels and joins that call. An accepted decision remains immutable during
later Stop. The candidate still requires native account/descendant, actual wire,
GPU and release qualification, plus coordinated retirement of diagnostic activity
authority. Source wiring alone proves none of those acceptance gates.

## Process retirement preparation

The isolation strategy exposes a process-only quiescence phase. It reaps the
session's processes while retaining its account, home, shared residue and IPC.
Final cleanup repeats the ownership check and reaping before removing data and
the account. Both phases resolve the current account uid, reject a changed,
root or invalid uid, and avoid acting on a remembered uid when the account is
already gone. This does not protect against arbitrary concurrent host-root
account changes.

The bounded output reader passes cancellation to its original read and discards
late output after cancellation. Its returned task still owns that read until
it ends; callers must join it rather than cancel only a waiting wrapper. These
operations prepare the required order: stop writers, join readers and activity,
then retire the account, data and keys. The manager retains pending starts and
exit monitors, rejects starts during Stop/shutdown, and coalesces cleanup so a
concurrent caller waits for its owner. Stop joins the captured monitor; that
monitor never joins itself. Shutdown cancels and joins original starts, stops
and monitors before releasing dependencies. Once the account is retired, a
failed sealed-data deletion retains its key and pending context; retries remove
the remaining data without reaping or retiring that uid again. A pinned Linux
pidfd targets the
original child for TERM/KILL, with no numeric-pid fallback. Kernels and container
policies must allow pidfd_open and pidfd_send_signal. Real account/descendant
teardown, publication failures, all sidecar owners and deployed kernels still
require qualification; controlled process tests alone cannot establish them.

The prelaunch dependency-sync helper uses the process-only phase before waiting
for output EOF. Its success, failure and cancellation paths join the original
process exit, quiescence task and both readers. Cancellation cannot replace that
join with a cancelled waiting wrapper, and a reaper failure fails startup. This
helper retains the account and data for its startup owner; it does not retire
the session or establish the lifetime of the editor child or bridge.

## Expired prompt receipts

Prompt execution permission includes explicit content availability. An expired
or unspecified receipt cannot start generation, probe native recovery, prepare
artifacts or create another acknowledgement, even if its permission flag is
contradictory. Recorded permission and outcome facts remain distinct from access
to the original content. Unspecified or unknown availability is an explicit
protocol error, rather than a silent successful return. Recovery-only command
lifetimes, local memory and spool cleanup still require their own finite retention enforcement; this check alone
does not establish complete data deletion.

## Prompt model-demand identity

Physical prompt execution owns a cloned command. Cancellation and model-demand
tracking use a separate immutable identity: command, session, submission, native
prompt and exact GPU runtime, plus the response-stream classification. Once
execution hands off its acknowledgement, held demand retains that identity and
its cancellation/settling tasks. It does not retain the command or prompt body.
Queue probes capture only the queue reader, session/runtime and accepted prompt
ID, independently from the command callback's closure.

Prompt work retains the original execution and held-demand release tasks. Task
publication only wakes joiners, which then await those originals. Stop closes
matching admission until its join ends, requests each physical/demand cancellation
outside the admission lock, and joins the original cancellation callbacks even
when one fails. Concurrent Stops share those original callback tasks. A demand
handoff during Stop and a held demand replaced by recovery both remain owned.
Execution drops its command and callback references before joining cleanup, so a
held cancellation callback need not retain completed prompt content. Once settling
ends, the registry drops that task reference as well, since a faulted task can
retain arbitrary exception data. Host shutdown
refuses new work; interrupt still preserves queued demand until terminal withdrawal
or Stop. Controlled task/reference tests do not establish native queue cancellation,
transport delivery, complete data deletion or physical memory erasure.

An accepted prompt keeps model demand while queued or while its queue cannot be
read. Terminal withdrawal, Stop, replacement and shutdown keep their existing
ownership and joining rules. This change releases these runner-owned references;
it does not erase native queues/history, framework buffers or operating-system
memory, or establish a retention policy for every other copy.

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

Each admitted transient request owns its cancellation source and the original
callback task until its physical execution and asynchronous lease completion
finish. Cancellation retires matching active entries without dropping these
owners. A failed or held callback cannot skip cancellation of other requests;
callback failures are observed with class-only diagnostics. Registry shutdown
closes admission and joins current and retired original completion roots.

Stop and Quiesce keep matching admission closed while joining requests and
performing physical runtime cleanup. Their fence remains in place after the
request drain finishes, until cleanup ends; unrelated sessions remain independent.
These rules cover runner-local ownership. Controlled dispatch and handler tests
do not establish actual HTTP cancellation, account/key cleanup, transport deadlines,
or complete content retention and deletion.

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
eviction/readback promise. A returning catalog entry reuses a saved local version
only when the full catalog receipt still matches, including its object key. A
digest match independently proves the current bytes; a different receipt never
grants eviction authority over old local bytes. Missing-path watcher events
release retained capacity
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
