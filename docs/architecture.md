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
Host shutdown, Stop and a newer start share that callback ownership.
Joining never uses a cancelled waiting wrapper. A callback failure is reported
by class after the join and cannot skip runtime cleanup or enter an acknowledgement
message. Startup acknowledgement retries remain independent of that physical join.

Each command stream also owns its admitted start, GPU and
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
For a tracked runtime, both Stop and unexpected exit join one context-owned
quiescence operation before invoking the final sidecar snapshot hook. The account
and data remain available to that hook. A failed quiescence skips the hook and
retirement, retains the context, and allows a later retry; successful quiescence
is reused by final cleanup rather than starting a concurrent reaper.
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
policies must allow pidfd_open and pidfd_send_signal. Startup validation pins the
agent itself, probes signal zero without delivering a signal, and closes the probe
descriptor before registration or work admission. Unsupported or denied operations
produce a fixed diagnostic and fail startup. Real account/descendant
teardown, publication failures, all sidecar owners and deployed kernels still
require qualification; controlled process tests alone cannot establish them.

The prelaunch dependency-sync helper uses the process-only phase before waiting
for output EOF. Its success, failure and cancellation paths join the original
process exit, quiescence task and both readers. Cancellation cannot replace that
join with a cancelled waiting wrapper, and a reaper failure fails startup. This
helper retains the account and data for its startup owner; it does not retire
the session or establish the lifetime of the editor child or bridge.

## Prompt handoff

A prompt reaches the runner as a transient handoff (`SubmitPromptCommand`) that
the control plane keeps only until its `expires_utc`. The runner runs a handoff at
most once and nothing is recovered or replayed:

1. It checks the handoff's identity and that its runtime (`RuntimeRef`) is the
   session's current runtime. A handoff for any other runtime is dropped before it
   is granted.
2. It asks `GrantPromptExecution` with its registered process `instance_id`,
   retrying transient failures with the same request. Only `GRANTED` lets it
   prepare downloads, artifacts and preflights and call the editor. Any other
   outcome, or a refused grant, drops the delivery with no native I/O and no
   acknowledgement. An interrupt or Stop after the grant was sent doesn't abandon
   it. The prompt's work ends at once, so Stop never waits for a grant, and the
   handoff is remembered as granted, so no redelivery runs it here. The grant
   settles in the background for up to 30 more seconds; `GRANTED` then ends in
   `NOT_EXECUTED` without preparing anything, since another session may be active
   by then. A grant still unanswered is released by the platform on Stop, a new
   runner process or its expiry.
3. After `GRANTED`, every path ends in exactly one `AcknowledgePromptResult`: the
   editor's response (`NATIVE_RESPONSE`, with `body_omitted` when the body was too
   large to relay), `NOT_EXECUTED` when nothing reached the editor, or `UNKNOWN`
   once native I/O started. The acknowledgement is retried on transient failures
   until shutdown; a refusal, such as `FAILED_PRECONDITION` for an expired handoff,
   is dropped.

One handler runs per handoff in a process. A redelivery is refused while its
handler or its held model demand is active, and for 65 minutes after a grant,
because the platform answers a retried grant from the same process with `GRANTED`
again. The platform keeps a handoff 60 minutes from acceptance, so this covers its
whole life; it is timed on the monotonic clock, so neither wall clock can shorten
it. The memory is bounded at 16,384 handoffs: at the bound new handoffs are
refused, and the platform delivers them again, instead of forgetting one early. Forge `queue/data` streams its chunks as
`http.response.chunk` session events and is acknowledged when the stream ends:
`NATIVE_RESPONSE` with the HTTP status after a normal end, `UNKNOWN` when it broke.
A failed chunk report never stops the native reader, but the acknowledgement then
carries diagnostic `prompt_stream_incomplete`, so the platform cuts the page's stream
instead of ending it as complete (a report that timed out may still be published late).
ComfyUI's `prompt_id` is read from its response; the bridge maps it to the handoff
to tag the prompt's WebSocket events and to end its model demand. Process-local
memory and spool cleanup still need their own retention enforcement; this does
not establish complete data deletion.

## Prompt model-demand identity

Physical prompt execution owns a cloned command. Cancellation and model-demand
tracking use a separate immutable identity: handoff, session and exact GPU
runtime, plus the response-stream classification. Once
execution hands off its acknowledgement, held demand retains that identity and
its cancellation/settling tasks. It does not retain the command or prompt body.
Queue probes capture only the queue reader, session/runtime and accepted prompt
ID, independently from the command callback's closure.

Prompt work retains the original execution and held-demand release tasks. Task
publication only wakes joiners, which then await those originals. Stop closes
matching admission until its join ends, requests each physical/demand cancellation
outside the admission lock, and joins the original cancellation callbacks even
when one fails. Concurrent Stops share those original callback tasks. A demand
handoff during Stop remains owned.
Execution drops its command and callback references before joining cleanup, so a
held cancellation callback need not retain completed prompt content. Once settling
ends, the registry drops that task reference as well, since a faulted task can
retain arbitrary exception data. The inner model-demand lifetime also owns its
first cancellation-callback task. Native rejection, failed preparation and queue
withdrawal request cancellation without running callbacks under the command's
handoff lock. Settling joins every returned original demand task and its callbacks
before releasing their lifetime, even if a callback fails. Preparation that returns
no demand still joins its registered callbacks. An original queue probe must
also finish; cancelling its token does not stand in for its completion. Host shutdown
refuses new work; interrupt still preserves queued demand until terminal withdrawal
or Stop. Controlled task/reference tests do not establish native queue cancellation,
transport delivery, complete data deletion or physical memory erasure.

An accepted prompt keeps model demand while queued or while its queue cannot be
read. Its terminal event withdraws it once the bridge mapped its prompt id to the
handoff; an unmapped prompt's demand ends when it leaves the queue. Terminal withdrawal, Stop, replacement and shutdown keep their existing
ownership and joining rules. This change releases these runner-owned references;
it does not erase native queues/history, framework buffers or operating-system
memory, or establish a retention policy for every other copy.

## Purpose

The runner executes GPU work for the Offloadr control plane. It connects out to the control plane over gRPC, authenticated by a runner key, and never exposes an inbound port. One runner serves one editor session at a time.

The same container images run on users' own GPUs (personal runners) and on GPUs Offloadr rents (hosted runners). Both are built from this repository by `docker-bake.hcl`.

## Inside the container

The agent is the container's entry point and runs as root. It:

1. Sends `RegisterRunner` heartbeats as an executor, with its resources, the editor templates the image supports, its `RuntimeStatus` (the session it holds, that session's runtime and the runtime's phase), and the launch it was started for, if any (`RUNNER_LAUNCH_ID`). Heartbeats are the only liveness signal.
2. Holds a `CommandStream` open and executes start, stop, prompt, relay and GPU power commands with exact identities, acknowledging each one idempotently.
3. Starts the editor (ComfyUI or Forge Neo) per session as a child process under a dedicated Linux user, with a private virtualenv layered on the image's root-owned one.
4. Relays editor HTTP requests and websocket frames over the command and event lanes, uploads outputs, and mirrors workspace files (`user`, `custom_nodes`, inputs) from the control plane.
5. Downloads models with aria2, which runs as an unprivileged account and writes only to directories prepared for it; downloads into a session home are staged outside it and moved in by the agent. Forge Neo's models live in a runner-wide cache that sessions cannot write, linked file by file into each session's own models folder. For ComfyUI, model files start as sparse placeholders; `liboffloadr_model_vfs.so` is preloaded into the editor and blocks each read until aria2 has the bytes it needs (demand-aware hydration).
6. Writes `/run/offloadr/status.json` so a local supervisor can tell whether a session is active.
7. In sealed mode, keeps each session's user data under a root-owned directory on a memory-backed root (`/dev/shm/offloadr` by default), stages workspace seeds there, keeps editor output off the container log, makes session processes non-dumpable and evicts uploaded artifacts when the root runs low. Code, models and compiled caches stay in the disk-backed home. Transfers are sealed with per-session reader keys and the control plane's output recipient key (`docs/sealed-envelope.md`, `Offloadr.Runner.Core/Sealing`). See "Sealed Storage" in `hosting.md`.

### Runtime starts and Stop

Protocol 5 names every runtime with a `RuntimeRef`: a `runtime_id` minted for one
`StartRuntimeCommand`, and a `runtime_order` from one sequence the control plane
shares across sessions and runners. Two runtimes are the same when their ids are.
The higher order is the newer one, in any session. `lifecycle_generation` stays on
the start only to match GPU power commands.

The runner keeps this state in memory for the process lifetime; it survives a
reconnect:

- the session it holds, that session's runtime and the runtime's phase;
- the newest start it adopted, of any session;
- the highest order it has settled: a start adopted or ended not ready, or a Stop's
  cutoff recorded. Every status carries it (`settled_runtime_order`), and it stays
  below a start that is admitted and not yet adopted;
- a Stop cutoff per session, in a bounded map (16,384 sessions). Evicting one
  raises a runner-wide floor to its cutoff, so the evicted session's old start is
  still refused;
- the recorded outcome of each recent startup, by runtime.

A start is classified and its startup published in one step, before the next
command is read:

1. **Older work is refused.** A start at or below its session's Stop cutoff or the
   floor, below the newest start adopted, or at that start's order with another
   id, is acknowledged `ready = false`. So is any other start at or below the
   settled order: a status may have told the control plane that nothing at or
   below it starts here. It touches nothing.
2. **The same runtime replays.** A repeated start of the runtime adopted joins its
   startup in flight, or is answered with its recorded acknowledgement. Nothing is
   launched again. One asking for other sealing is refused.
3. **Newer work replaces.** The startup in flight is cancelled and awaited. Prompts
   of every other runtime are cancelled. The session's older child is stopped as a
   restart's quiesce did: its relayed requests are cancelled and drained, then it
   is stopped and the session cleaned up. Another session's child is pre-empted.
   Then the runtime is adopted, launched and acknowledged.

A relaunch keeps the session's sealing, whatever its lifecycle generation. One that omits sealing gets the session's;
one that would weaken it, or change or drop its output recipient, is refused after
the older child stopped. The runner adds no timer for a fenced child that waits for
its replacement: the platform's timeouts, Stop and session expiry cover it.

Stop makes one comparison. When the session has a runtime, child or startup with
an order above `up_to_runtime_order`, the Stop is acknowledged `Superseded` and does
nothing. Otherwise the runner records the cutoff, cancels the session's prompts and
startup, drains its relayed requests, stops and cleans up, and acknowledges
`Performed` with the session's count of unsaved artifacts. The acknowledgement is
stored and replayed for a redelivered Stop. The session's sealed-lifecycle notes
are dropped once, after the first acknowledgement goes through, and only while every
runtime the session adopted here is at or below the cutoff, including one that has
since exited or been pre-empted. The highest order each session adopted is kept in
the same bound as the cutoffs, with a floor for evicted sessions; an evicted session
keeps its notes when in doubt. Starts and Stops are checked against
each other under one gate, so a Stop either sees a startup or has fenced it. A
newer start admitted while a Stop of its session runs waits, after cancelling the
startup it replaces, until that Stop has cleaned up, dropped the session's sealing
and taken its count; it touches nothing before. The wait ends on failure and
shutdown too, and a later Stop or start can still cancel the waiting start. Two
Stops of one session chain: a start waits until both have ended, in either order.
A Stop never waits for a startup above its cutoff.

A start that fails or is cancelled drops another session the runner still holds
with no child and no startup: a newer start superseded it, so the heartbeat stops
reporting it. A failed relaunch keeps its own session.

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
the affected request retains callback failure after completion, so its capturing
drain makes Stop fail and a relaunch fail. A completed failed request stays
owned until a matching drain observes it. That drain joins and consumes only its
captured roots; unrelated sessions and later teardown attempts remain usable.
Callback failures are observed with class-only diagnostics. Registry shutdown
closes admission and joins current and retired original completion roots.

Stop and a relaunch keep matching admission closed while joining requests and
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

`RunnerSupervisor` makes a person's GPU available to Offloadr, for the desktop app, and runs the runner containers Offloadr asks for (see "Launchers" in [contracts-sync.md](contracts-sync.md)). The app registers as the runner's launcher; the agent in a container registers as its executor, with the same key.

It is built as one loop that owns every decision, so that nothing changes between a check and the action it guards:

- **One owner.** All of the supervisor's state is read and written on its loop, with no lock. Other threads (the UI, the settings store, the launcher link) hand it immutable values and wake it: what the person asked for is a record of counts swapped whole, the settings and the link's state are snapshots. What they read back, the view and the offer the heartbeat sends, are immutable values the loop published.
- **No waiting on the loop.** Every call to Docker or Offloadr lives in `SupervisorEffects`, which holds none of the supervisor's state and cannot reach it. The loop runs such a call as a job in a slot (the main procedure, the watch of the container, the link's run, reports, maintenance, the launch plan, the log) and gets its result back as a message. The loop's only wait is for the next message or timer.
- **Procedures are run again, not resumed.** Going online, a launch, a Stop and an idle release each take several jobs. None is a suspended method holding locals: each is run again from its top on every tick, against that tick's inputs, and goes as far as the results recorded so far allow. What it keeps from an earlier tick is a named field, chosen on purpose (the settings snapshot a start registers for, say).
- **What is owed is data.** A launch outcome to deliver, a container gone to report, a stopped container an engine kept, a container whose fate is unknown: each is recorded (most of it on disk) and worked off on every tick, whatever else goes on and whatever state the runner is in.
- **Signals, not shared state.** Two things must reach a job at once, on the thread that causes them: a Stop, and Settings naming another address or key. They cancel a token the job was given (a launcher Offloadr has not accepted yet ends; a release not sent yet is not sent). The loop learns of the same change on its next tick and decides everything else.

1. Start validates the settings, checks that Docker answers with Linux containers, and runs `nvidia-smi` in a throwaway container of a runner image this computer has (a first start downloads the one for the editor in Settings). The check is repeated only when the engine or the GPU choice changes, or after sleep. Then the launcher link (`ILauncherLink`) registers and follows `LauncherStream`; the computer is **available** with no container running. An automatic start (with the app, after an update) retries Docker and GPU trouble with a backoff; one the person pressed faults instead.
2. When the latest `LauncherState` names a launch, it pulls that launch's image (falling back to a local copy when the registry is unreachable), then **creates** the container (`docker create`, as `offloadr-runner` with `--gpus all`, `RUNNER_LAUNCH_ID` and labels naming the launch and the Offloadr address and runner it is for) and, once the link may start it, **starts** it by its ID. The two are apart on purpose. A container that is only created runs nothing, so a creation that is cancelled or given up can leave nothing running unseen: whatever the engine still makes of it is inert, is looked for by its label for a while, and is removed with nothing to report. And a start that fails or is given up is a start of a container known by its ID: the engine is asked what became of it (it runs after all and is kept; it never started, so the launch reports `FAILED`; it ran and exited, or is no longer there to say, so that it is gone is reported instead). While the engine does not say, neither report goes out: the container stays supervised and the watch settles it. `FAILED` needs proof that the container never ran: the engine refused the start itself, and the container was then seen created. After a start that was given up or whose answer was lost, the engine may still start it, and one that started and exited before it was removed would leave nothing to say so; such a container is removed and reported gone, which lets the allocation launch again. Reported gone only once it is removed, or seen exited: one that is still only created and that the engine will not remove may be started yet, so it stays supervised (a Stop does not let go of it, and an uninstall leaves its record as it is) until the engine removes it, runs it, or shows it exited. Found that way after a crash, nothing is offered on its engine until then. The runner key reaches Docker through the process environment, never the command line. A newer launch, a withdrawal, a change of address or key, or Stop ends a launch in flight (removing a container it created and had not started); it reports the outcome Offloadr asked for, and never one for a Stop or host shutdown.
3. While a container runs, it watches it, follows its log and reads the agent's status file. An exit is not restarted: the next session's launch starts a new container. A session it was serving is shown as interrupted. Every container the app makes is noted in `launched-containers` in the data folder until Offloadr knows it is gone, as created before its creation is asked for, as started right before the engine is asked to start it, and as refused once the engine refused that start (seen created, it then ran nothing; no longer there to say, it is reported gone). One stopped beside the supervised container, or by an uninstall, is noted there as well when its release is asked (`asking`) and granted (`released`), before it is stopped: a granted one is never taken up again, only stopped. After a crash, or at a Stop, one that was only created and is not there ran nothing and is reported as nothing; one whose start was asked for and is not there is gone, which Offloadr is told, since nothing shows it never ran. One that exits, or that a Stop or Quit stopped without a granted release, is reported with `ReleaseRunner` and `container_exited`, so Offloadr ends its session rather than wait on it. A launch whose start failed after its container had started and exited is reported that way too, with no `FAILED`: its executor may have registered, and the exit report lets the allocation launch again where `FAILED` would fail it. A container that was created and never started ran nothing, and gets `FAILED` alone. The report needs no registered launcher (Offloadr records the launcher instance, it does not check it): it goes out as soon as the app knows, also after a takeover or with AutoStart off, for a runner the current settings or a run since the app started name; the launcher never registers just to send it. Only the Offloadr that launched a container can be told, so the address and key each container was started with are kept, encrypted like the key in the settings, in `launched-credentials` until Offloadr knows that container is gone: the report still goes out after Settings name another address or key, and after the container itself is no longer there to read them from. A container counts as gone at a start only when the engine and context it was started on answer and no container with its launch label runs there. A stopped one the engine would not remove (it still holds the runner key) is noted with its engine in `leftover-containers` and removed later, also after a restart.
4. A container stays warm after its session. After the warm time (10 minutes), right away once the settings it was started with changed, or when another launch needs the runner, the supervisor asks Offloadr to release it and stops it only on a grant. A grant is noted in `released-launch` until the stop, so a container released before a crash is stopped, not reused, at the next start. A container launched for another address or key, or by an app version before launchers, is never stopped automatically; Stop or Quit stops it.
5. The catalog of editors and images comes from `GetRunnerLaunchPlan` on `RunnerControlService`, authenticated with the runner key, and lists the editors the launcher offers; a launch names its own image. Until a control plane implements it, the call fails with `UNIMPLEMENTED` and the supervisor uses the built-in list of published images. The server must treat this call as a catalog query, never as a heartbeat.
6. Containers carry labels with the editor template, a fingerprint of their configuration, the creation that made them, the launch and an ID kept in the app's data folder. On launch the supervisor adopts a container left running by an earlier session of the same app, and it never touches a container with the same name that it did not create, including a runner that another copy of the app (another data folder, or another computer using the same engine) started.
7. A launcher that another app took over while it ran stays off until the person presses Start (so two computers do not keep taking the runner from each other), also across app starts and updates (`taken-over` in the data folder names the address and a hash of the key). Start takes the runner back in one go, also when the takeover happened while this app was off. A launcher too old for Offloadr asks for an app update. When the engine or GPU it offers changes, the heartbeat's `offer_revision` rises and a launch Offloadr chose against an older one reports `FAILED`, so no launch runs on hardware it was not chosen for; this needs no new launcher instance, which could take the runner from another computer.
8. While available with no container, Docker is checked every 30 seconds on the engine and context the GPU check passed on; a launch that finds Docker or the GPU check failing reports `FAILED` and the computer is not offered until they pass again. The GPUs offered are this computer's, as far as the GPU check saw them in a container; when this computer cannot list them (an engine on another computer, `nvidia-smi` failing here, or containers seeing other GPUs than it lists), a throwaway container is asked for their names and memory, and with none listed the GPU check fails. The offer's revision covers the engine, the GPU chosen and each GPU offered. With a container running, the launcher instead stays registered but offers no editor and no GPU, so that container can still be released once idle; a GPU check on another engine then waits for it to be gone rather than download an image meanwhile. A container this launcher could not release (started by an app version before launchers, or for another Offloadr address or runner key) blocks every launch, so the launcher does not register at all until it is gone: registering would only take the runner from another computer that serves it. Before a warm container is released for a launch, containers of that launch's own image must reach the GPU: the check that passed may have run in another image, and one can need a newer driver. The container is then created from that same image, by its ID when Offloadr named it by a tag (its name is kept in a label), so a tag moved meanwhile changes nothing. Offering nothing is an offer with its own `offer_revision`, which retires launches chosen before. While the engine only does not answer (or a list of GPUs comes back empty), and the launch of the supervised container is still named, the launcher keeps the hardware and revision it last offered and lists no editors instead, until a check of that engine and GPU is answered that containers do not get it (a check of another engine or GPU chosen meanwhile says nothing of what was offered): a higher one would retire that launch and fence its container, which may have started and not registered yet.

## Desktop app

The desktop app is a MAUI Blazor Hybrid window around `Offloadr.Runner.UI`, following the structure of a shared Razor UI with thin hosts. MAUI does not run hosted services, so `DesktopRuntime` starts them in order: a single-instance lock first, then the supervisor. The runner key is encrypted with ASP.NET Core Data Protection, whose key ring is protected with DPAPI. Settings, keys and logs live in `%LOCALAPPDATA%\OffloadrRunner`, or in `OFFLOADR_RUNNER_DATA_DIRECTORY` when set. The app lives in the Windows notification area: closing the window hides it, and the tray menu opens it, starts or stops the runner, or quits. `--minimized` starts it hidden; the Settings switch for starting at sign-in registers exactly that in the person's own Run key, and the app re-points the registration at itself if it has moved. The release is a Velopack installer: it installs per user to `%LOCALAPPDATA%\app.offloadr.runner`, whose `current` folder each update replaces, so the sign-in entry names the installer's launcher next to it, and WebView2 keeps its profile under the data folder (`webview`). The entry point is `Platforms/Windows/Program.cs`, which lets the installer's hooks run before anything else; on uninstall they remove the sign-in entry and a runner container the app left running, and tell Offloadr of a launched one they stopped (`container_exited`, with the address and key in the data folder, or those its own container was started with when Settings name another runner by now, registering no launcher), since no later start of the app may. Settings, keys and logs are not in the install folder and stay. The tray icon carries a dot for the runner state: green while running, amber while starting, downloading or restarting, red when it needs attention. Shortly after start and then daily, an installed app asks the installer's update manager for a newer release of this repository on GitHub and downloads it in the background (`AppUpdateService` over `IAppUpdateSource`). It installs only when the person restarts the app from the banner, or once the app quits; never at start, where a second launch would replace the files of the copy that is running. That restart tells the supervisor to leave the runner container running (`KeepRunnerOnShutdown`), and the new version adopts it like after a crash, so a session is not interrupted; it waits while the runner is being checked, prepared, started or stopped. The old version leaves a note (`resume-after-update`, `UpdateHandoff`) when the computer was wanted available (`RunnerWanted`: started, and no Stop asked for since), and the new one makes it available again, around a container it reconnected to or without one. Quitting stops the runner container; the app asks first when the runner is active, and warns when a session would be interrupted. The tray icon uses `Shell_NotifyIcon` directly, with the app window subclassed to receive its messages.

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

## Optional diagnostic delivery bounds

The runner and session log relays capture each line's original UTC and monotonic
clock before queueing it. A line may be delivered for at most five minutes;
retries and reconnection preserve its timestamp, sequence and producer identity.
Either clock expiry or an observed rollback permanently retires its delivery
window. Expired backlog is discarded before batching or another upload. The
existing 4,096-entry, 64-entry/1 MiB batch and 16 KiB message limits remain.

Each report attempt is cancelled after at most five seconds, or the remaining
original age when shorter. The sender awaits that original sink task before
retrying or moving to another batch. Cancellation alone does not establish that
an uncooperative task has ended: it remains joined. Queue and batch references
are released at sender completion; this does not claim CLR-wide physical erasure
or termination of an arbitrary transport that ignores cancellation. Captured
connection generation prevents a line still being prepared during replacement
from adopting the next runner connection. Session lines keep their original
writing-runtime identity.

Retry diagnostics retain only fixed counts and error classes, not exception
objects or arbitrary detail. Optional log expiry does not admit runtime activity
or complete a physical acknowledgement; those existing owners are independent.
The log path remains a best-effort presentation lane. The Windows desktop
diagnostic files, and the agent console with `RUNNER_SEALED_STORAGE=required` (as
on every hosted runner) or `RUNNER_CONSOLE_LOGS=metadata`, emit only a UTC
timestamp, severity, a fixed component label and a fixed failure class. These
sinks never invoke the arbitrary message formatter or inspect state, scopes,
event names, exception messages or stacks. Category strings are mapped to fixed
component labels and are not retained. Otherwise the agent console, on its
owner's machine, writes the formatted message and exception after the same
labels; the output of sealed sessions' editors stays off it either way.
The separate account-protected runner and session forwarding lanes keep their
existing content delivery, limits and original capture deadlines.

Desktop metadata uses invariant UTC hourly filenames, capped at 64 KiB each.
Files expire 24 hours after the filename's hour; an active app sweeps each minute,
and startup and writes also sweep. A stopped app cannot run the sweep; the next
startup removes expired files. Startup failures use the same metadata sink.
The app removes its retired `runner-yyyy-MM-dd.log` and `desktop-startup.log`
files without reading their content, preserving unrelated files. Unwritable
folders cannot create a raw-content fallback. These are app-owned cleanup attempts,
not proof that old files on a real host, external collectors, backups or native
histories have been erased. Those copies need their own protection and disposition.

Thirty-four focused cases pass: the four original regression cases, eleven
clock/join/retry/connection-race controls and nineteen prior relay cases. These
are controlled sink/clock tests, not real gRPC, full Linux, Windows/GPU or
published-release qualification.
