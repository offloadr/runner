# Runner contracts

`contracts/offloadr/` holds the protobuf definitions of the runner protocol: the files the runner imports, and nothing else.

| File | Contents |
| --- | --- |
| `runner/v1/runner_agent.proto` | `RunnerProtocol`, `RunnerControlService`, `RunnerArtifactService`, `RunnerWorkspaceService` and their messages, including the runtime telemetry snapshot |
| `common/v1/{gpus,models,gpu_control}.proto` | shared enums and GPU control messages |
| `editor_runtime/v1/editor_proxy.proto` | editor artifact metadata and runtime request kinds |

These five files are identical on both sides. The control plane's `common/v1/prompt.proto` describes its own prompt records and is not part of the runner protocol.

C# is generated at build time by `Grpc.Tools` in `src/Offloadr.Runner.Contracts`, so building needs only the .NET SDK. Lint with `buf lint contracts`.

## Protocol version

`RunnerProtocol.RUNNER_PROTOCOL_CURRENT` in `runner_agent.proto` is the one protocol this build speaks. The agent sends it in `RegisterRunnerRequest.protocol_version`, `CommandStreamRequest.protocol_version` and the `x-offloadr-runner-protocol` header of every call. A desktop app registering as a launcher sends it the same way, in `LauncherStreamRequest.protocol_version` too, so a protocol change needs a desktop app release as well as new images.

The control plane accepts exactly its own version:

- **Registration** answers any other version with `accepted = false`, a `RegistrationRejection` (`RUNNER_OUTDATED` or `SERVER_OUTDATED`), its own `server_protocol_version` and a message. Fields 1–3 of `RegisterRunnerResponse` never change, so every past runner can read the refusal.
- **Every other call** without the current header is refused with `FAILED_PRECONDITION`. Only `RegisterRunner` and `GetRunnerLaunchPlan` are ungated.
- **The agent** stops on a protocol rejection instead of retrying: it cleans up its sessions as for a shutdown and exits with code 3. The desktop supervisor then restarts it, which asks Offloadr's launch plan for the image to run and downloads it. On `REGISTRATION_REJECTION_EXECUTOR_FENCED` ([Launchers](#launchers)) it also stops and cleans up, but exits with code 4: only a new container can register. Any other refusal is still retried.

## Changing the protocol

The Offloadr control plane implements the server side of these services, so a contract change has to land on both sides:

1. Change the `.proto` files here and the agent code that uses them in one pull request, and raise `RUNNER_PROTOCOL_CURRENT`.
2. Keep the five shared protobuf definitions identical on both sides and regenerate every affected consumer. Change the current protocol directly, without legacy layouts or compatibility shims: an outdated runner is refused and updated, never served.
3. Release the coordinated runner and control-plane changes together, in an announced window: a protocol change ends running sessions of runners that have not updated yet. [Image publishing](image-publishing.md#protocol-tags) explains how the images follow. Schema declarations alone do not mean the corresponding behavior is implemented.

## Runtime identity and restart

Protocol 5 names every runtime with a `RuntimeRef`: a `runtime_id` the control plane mints for one `StartRuntimeCommand`, and a `runtime_order` from one sequence shared by every session and runner. Any two orders compare: higher is newer. It replaces the lifecycle generation, runtime epoch and runtime instance in every message that carried them. `StartRuntimeCommand.lifecycle_generation` stays only to match GPU power commands.

- **One start.** `StartRuntimeCommand` (`start_runtime`) starts a session's first runtime and every relaunch. It replaces `StartSessionCommand`, `LaunchEditorRuntimeCommand` and `QuiesceEditorRuntimeCommand`. `AcknowledgeRuntimeStart` answers it, named by its runtime. Restart revisions, attempts and assignment sequences are gone.
- **Older work is refused.** The runner refuses a start at or below its session's Stop cutoff, below the newest start it adopted (any session), or at that start's order with another id. It keeps cutoffs for 16,384 sessions; evicting one raises a floor to its cutoff, so the evicted session's old start is still refused.
- **The same runtime replays.** A repeated start of the runtime adopted joins its startup or gets its recorded acknowledgement again. Nothing is launched twice.
- **Newer work replaces.** A newer start for the same session stops its current child first, as Quiesce did; one for another session pre-empts that session. A relaunch carries the session's sealing and may not weaken it.
- **Stop is always fenced.** `StopSessionCommand.up_to_runtime_order` is the Stop's cutoff, from the same sequence. When the session has a runtime, child or startup above it, the Stop is acknowledged `Superseded` and does nothing. Otherwise the runner records the cutoff, stops and cleans up. A newer start of the session that arrives meanwhile waits until that Stop is done, so the Stop never stops it or counts its losses. `AcknowledgeSessionStopRequest.up_to_runtime_order` (11) echoes it.

The runner keeps this state in memory for its process lifetime, through reconnects. A new process registers a new `instance_id` and starts with none: the control plane fences stale work for a fresh executor itself.

## Launchers

Protocol 6 lets a personal runner be available with no runner container running. The desktop app registers under the runner's id as a **launcher**; the agent in a container registers as an **executor**, as before. `RegisterRunnerRequest.role` says which, and the control plane refuses `RUNNER_ROLE_UNSPECIFIED`.

| | Launcher (desktop app) | Executor (agent in the container) |
| --- | --- | --- |
| Registers while | the app is open, availability is switched on, and its Docker and GPU checks pass | its container runs |
| `supported_editor_templates` | the editors it can start | the editors this image runs |
| `runtime`, `sealed_storage` | unset | as before |
| Liveness | its own: availability to launch only. It refreshes no executor liveness and protects no session | unchanged |

Both keep their own `instance_id`. A launcher's registration never supersedes the executor's instance, never ends a session, and never runs the executor's report handling.

When a session needs an editor on a runner that has no eligible executor for it but has a fresh launcher listing it, the control plane names a launch in the `LauncherState` it sends on `LauncherStream`. The app starts that image with `RUNNER_LAUNCH_ID` set to `launch_id`, and the agent echoes it in `RegisterRunnerRequest.launch_id`. Everything from the first executor registration on is unchanged.

**Launch rules**

- A runner has at most one current launch, and it belongs to one waiting allocation. The allocation keeps waiting on that launch after `STARTED`; retries never create a second launch while the first is unresolved.
- A runner has one launcher at a time, and the newest instance wins. When another launcher instance registers (the app restarted, or the same key is used on another computer), the superseded instance's registrations are refused with `REGISTRATION_REJECTION_LAUNCHER_SUPERSEDED` and its stream is closed. That launcher stops registering and stays offline until the person starts it again, so two computers do not keep taking the runner from each other.
- A launch also belongs to the launcher instance it was issued to. On a takeover the control plane retires that launch, if no executor has matched it yet (see below), and issues the waiting allocation a new one, with a new `launch_id` and a higher `launch_sequence`. A container the old instance still starts then echoes a retired `launch_id` and is refused. A launch already matched is kept, and its executor serves the allocation as usual.
- `LauncherState` is authoritative, not a command log. The control plane sends it first on every stream and again whenever the current launch changes. A launch in flight that the latest state does not name is withdrawn: `SUPERSEDED` when the state names a launch with a higher `launch_sequence`, `CANCELLED` when it names none. So a cancellation missed while the stream was down is applied on reconnect.
- The control plane sends `LauncherStreamKeepAlive` at least every 15 seconds while it sends nothing else. A launcher that has received nothing for 45 seconds treats its stream as broken and reconnects.
- The launcher may download while its stream is down, but starts a container only while its stream is live (a message within the last 45 seconds) and the state that stream sent last still names the launch. After a reconnect it waits for the new stream's first state.
- `STARTED` means the container was started, not that an executor is ready. The control plane then waits a bounded time for an executor that echoes this `launch_id` and is eligible. An executor with the right template but another `launch_id`, or none, does not complete the launch.
- The eligible executor registration that matches the launch is what completes it, whether or not `STARTED` has arrived yet: the two travel on separate calls, and the launcher that owes the report may have been superseded meanwhile. A `STARTED` that arrives later is accepted as a repeat.
- A cancelled, timed-out or replaced allocation retires its launch, and the next state no longer names it. So does an accepted `FAILED` for a launch no executor has matched, in the same transaction, so a container that started after all is fenced. A late report never revives a retired launch.
- Only a launch no executor has matched yet can be retired. The first accepted executor registration that echoes a launch's `launch_id` and is eligible for its allocation (for one, it supports the launch's `editor_template`) matches it; a refused or ineligible one matches nothing, under the lock allocation takes, so either that registration finds the launch retired and is refused, or the launch is matched and is never retired afterwards. The executor of a matched launch is an ordinary executor: if its allocation went away meanwhile, it is simply idle, and the launcher keeps it warm and releases it as any other.
- A launched executor never replaces a busy one. While another executor instance of the runner holds an open session or a pending Start or Stop, a registration echoing a launch is refused without a rejection reason (so the agent retries) and matches nothing; the launch then completes once that executor is idle, or its container was reported exited (`container_exited`), or the launch times out and is retired, after which the container's next registration is fenced. This applies to another container only: an executor whose `launch_id` equals the registered executor's is the same container's agent restarted, and replaces it as usual. The `launch_id` names the container, not the process, so the instance it replaces is fenced in the same transaction, as a released one is: a late registration of the dead agent can never take the runner back from its replacement or end that replacement's session.
- A matched launch stays the current launch, named in every `LauncherState`, until the launcher it was issued to reports a terminal phase for it (`STARTED`, or `FAILED` when it lost track of the container it started) or is superseded; a launcher that took over is never sent it. Only then does the state stop naming it, so a launcher never sees a launch that succeeded as withdrawn, and never reports `CANCELLED` for a container that is serving. A terminal report for a matched launch never retires it or touches its executor.
- An executor registration carrying a retired, released or unknown `launch_id`, or a released `instance_id`, is refused with `REGISTRATION_REJECTION_EXECUTOR_FENCED` before anything else is touched. The control plane mints every `launch_id`: one it has never seen comes from another environment, or from before its data was recreated, and old launch rows can be deleted without lifting a fence. A key's first registration carrying a `launch_id` is therefore always fenced, since a launch is only created for an existing runner. It never replaces the current executor instance, refreshes liveness or ends a session, so a late container cannot displace one that is serving. The agent exits on that refusal, so such a container ends by itself even when no launcher is left to stop it.
- A terminal phase (`STARTED`, `FAILED`, `SUPERSEDED`, `CANCELLED`) is the acknowledgement. The launcher resends it with the same content until it is accepted, and the control plane accepts a repeat. Host shutdown never reports `STARTED`.
- While a launch is in flight the launcher reports at least every 30 seconds, in whatever phase it is in. Silence therefore means the launcher is gone, not that a download is slow.
- A launcher's registration carries `offer_revision`, which increases within the launcher instance whenever what it offers changes: the GPU or the container engine it launches on. The control plane stamps each launch with the `offer_revision` of the launcher registration it checked the allocation against, read together with the resources it checked (`LaunchRunnerCommand.offer_revision`). The launcher reports `FAILED` for a launch whose revision is not its current one, since it was chosen for hardware this computer no longer offers; the waiting allocation can then be offered a new launch against the current registration. An offer change therefore never needs a new launcher instance, which could take the runner from another computer.
- A state naming a launch whose container already runs, after `STARTED`, changes nothing. A container left from another launch is released and stopped first; if the release is refused (it holds a session), the launch reports `FAILED`.

**Release rules**

- Before stopping a container it started, for any reason, the launcher asks `ReleaseRunner`, naming the container's `launch_id`, which is required, and, if it could read the agent's status file, its executor instance. The launch is what names the container: an agent restarted inside it keeps its `launch_id` but gets a new instance, so a release by instance alone could miss it.
- The control plane decides atomically, under the lock allocation takes. A release without a `launch_id` is refused. Each identity that is set is looked up among registered executors. If they find different executors, or the instance finds an executor registered with another `launch_id`, the release is refused.
- If either finds a registered executor, the release is granted only while it is the current executor instance with no open session and no pending Start or Stop. Granting makes that executor unavailable in the same transaction, before the answer is sent, so nothing is assigned to it while the launcher stops it.
- If neither does (the agent failed to start, or wrote its status file but has not registered yet), the release is granted. So a launch whose agent never comes up does not block the next one.
- A container without a `launch_id` is never stopped automatically: a launch that would replace it reports `FAILED`, and the app asks the person to stop it, with the usual warning about a running session. Such a container can only come from an app version before protocol 6 running a protocol 6 image: an agent on an older protocol is refused at registration and exits by itself.
- A granted release fences every identity it names, `launch_id` and `executor_instance_id`, for good: a registration carrying a fenced one is refused, as above, whenever it arrives. A replacement container always has new identities, so it is unaffected. Repeating a granted release is granted again.
- The launcher stops the container by itself (idle, replaced, settings changed) only after `released = true`. When Offloadr cannot be reached it keeps the container and asks again. When the person stops the runner or quits the app, it stops the container anyway, after warning that a running session would end.
- When a container the launcher started is no longer running (it exited or was killed, or the launcher stopped it without a granted release because the person stopped the runner or quit the app), the launcher sends `ReleaseRunner` with `container_exited = true`, as soon as it knows, including at its next start. The control plane then does not check that the executor is idle. It ends the session that executor held, completes its pending Stop, cancels its pending Start, and fences both identities, so the runner is offered again and the next session launches a new container. When no executor has matched the launch it names (the container exited before its agent registered), that launch is retired in the same transaction, so the waiting allocation is issued a new launch at once rather than at its timeout. The other release rules (a `launch_id` is required; an instance registered under another `launch_id` is refused; a repeat is granted again) apply unchanged. The launcher resends it until it is answered, and sends it only for a container it started, named by that container's `launch_id`.
- `ReleaseRunnerRequest.instance_id` names the launcher instance asking. It is recorded, not checked: a release is decided on the executor's state, so a launcher that was just superseded can still stop its own idle container.
- If the app stops after a release is granted but before the container is stopped, the next app start repeats the release and stops it; it never reuses that container.

Hosted runners and runners started by hand have no launcher. With no fresh launcher for a runner, the control plane behaves as without this section.

Two executors registering for one runner (the same key in two containers, for example on two computers) still replace each other, as they do without launchers, except that a launched executor waits for a busy one as above: this section fences only launches that were retired or released.

## Native connections and runtime status

Protocol 4 leaves native editor sockets to the runner. The control plane no longer reserves or tracks them: the runner reports each new connection with a `bridge_state` session event, and the control plane sends a client's editor state again when the connection id changes ([native connections](bridge-connections.md)). Client messages name only the session, client and runtime.

Every registration carries `RuntimeStatus` instead of the active session id: the session the runner holds, the runtime it runs and that runtime's phase.

| Phase | When |
| --- | --- |
| `NONE` | No session, or a session held with no tracked editor process |
| `STARTING` | A start of the named runtime is running |
| `READY` | The named runtime finished starting ready; `sealing` says what it got. Reported from the moment the startup knows its outcome, before the acknowledgement is sent, so a lost acknowledgement does not hold it back |
| `FAILED` | The named runtime failed to start and the runner still holds its session; `message` (at most 512 bytes) says why |
| `STOPPING` | A Stop of the session is running |

The phase is a report: the start acknowledgement stays the owner of a start's result, and the control plane may complete a relaunch from a `READY` status only for the exact runtime it is starting. `RuntimeStatus.runtime` names it.

Protocol 4 sent the runtime as lifecycle generation, runtime epoch and runtime instance (fields 2–4); protocol 5 sends `runtime` (8).

`RuntimeStatus.adopted_runtime_order` (9) is the highest `runtime_order` the process had adopted when it sampled the status, read together with the rest of the status; 0 before any. A Start the runner took as its current runtime counts (it launched it, joined the startup in flight, or replayed its recorded acknowledgement); a refused Start does not. It never decreases within a process, and a new process starts at 0. The control plane does not act on a report whose adopted order is below a first Start the same process has since acknowledged ready; it answers retry. Without it, a heartbeat sampled before a first Start, arriving after that Start was acknowledged, would read as "no session" and end it.

## Deleted artifacts

Protocol 3 lets the control plane say which artifacts were deleted, so the runner does not have to guess from an artifact's absence. Every artifact listing names those deleted since the runner's session began, and the runner removes its local copy of each; see the table below and [artifact upload receipts](artifact-upload-receipts.md).

## Prompt handoff

Protocol 2 hands a prompt to the runner as a transient `SubmitPromptCommand`, identified by `handoff_id` and fenced by the runtime it was accepted for (`runtime`, a `RuntimeRef` since protocol 5). The control plane keeps the handoff until `expires_utc` and may redeliver it until it is granted.

- The runner asks `GrantPromptExecution` with its registered process `instance_id` (the one on `CommandStreamRequest`) and the handoff's runtime, and runs the handoff only on `GRANTED`. A retry from the same process after a lost reply is answered `GRANTED` again, so the runner retries transient grant failures with the same request and never runs a handoff twice in one process. It remembers a granted handoff for 65 minutes on its monotonic clock, which relies on the control plane never granting a handoff more than 60 minutes after acceptance. An interrupt or Stop doesn't abandon a grant already sent: `GRANTED` still ends in `NOT_EXECUTED`.
- `ALREADY_GRANTED`, `MISSING_OR_EXPIRED`, `TARGET_NOT_CURRENT`, `ACCOUNT_UNAVAILABLE` or a refused grant: the runner drops the delivery without native I/O and without an acknowledgement.
- After `GRANTED` the runner sends exactly one `AcknowledgePromptResult`: `NATIVE_RESPONSE` with the editor's status, body and content type (`body_omitted` with diagnostic `prompt_native_response_body_omitted` when the body exceeded what it relays, or diagnostic `prompt_stream_incomplete` for a streamed response one of whose chunk reports failed), `NOT_EXECUTED` when nothing reached the editor (`prompt_preparation_incomplete`, `model_downloads_disabled`), or `UNKNOWN` once native I/O started (`prompt_native_result_unknown`). Resending the same result after a lost reply is accepted; the control plane answers `FAILED_PRECONDITION` for a handoff that expired, which the runner does not retry.
- Nothing is recovered or replayed: there is no evidence report and no recovery delivery. ComfyUI's prompt id comes from its own response; the platform injects none. Session events carry the `handoff_id` they belong to.

## Current Stop identity

Every Stop requires a nonempty UUID `command_id` (field 3) and carries its cutoff, `up_to_runtime_order` (7). Its acknowledgement echoes the captured nonce, the registered runner process in string `instance_id` (field 4) and the cutoff (field 11). Invalid command or missing process identity never enters cleanup. Acknowledgements retry the captured result after cleanup; failed cleanup or host shutdown never acknowledges success. A redelivered Stop gets its stored acknowledgement.

## Runner log relay identity

Each `RunnerLogEntry` carries a nonempty UUID `relay_instance_id`, created once
for the relay lifetime and preserved through reconnection and retries. A restarted
relay gets a new UUID and may restart its sequence at one. Duplicate suppression
keeps the old relay's watermark; sequence one alone never proves a restart.
Missing or invalid relay identities are dropped. Coordinate the current producer
and server together; there is no missing-field compatibility fallback. This
identity neither authenticates a runner nor grants runtime activity authority.

## Launch plan

`RunnerControlService.GetRunnerLaunchPlan` returns, per editor template, the runner image on Offloadr's own protocol (`<channel>-p<N>`, or a digest the operator pinned), and the owner's open editor's template (empty when none is open, so the supervisor's own setting wins). It doesn't touch runner liveness and isn't protocol-gated, so an outdated supervisor still learns which image to run. When the control plane can't be reached, the supervisor falls back to its built-in list of the bare channel tags, which serve the promoted protocol.

## Pending on the control plane

These declarations are shared, but the server behavior below remains pending. The sealed-transfer rows follow `docs/sealed-envelope.md`, which defines the envelope format. The command nonce, runner process identity and runtime fences above are always required.

| Change | What the control plane does | What the runner does |
| --- | --- | --- |
| `RegisterRunnerRequest.role` (11), `launch_id` (12) | Refuses an unspecified role; keeps launcher registrations apart from the executor's instance, liveness and report handling ([Launchers](#launchers)); completes a launch only with the executor that echoes its `launch_id` | The agent registers as `EXECUTOR` and echoes `RUNNER_LAUNCH_ID`, which must be a UUID when set; it exits with code 4 on `REGISTRATION_REJECTION_EXECUTOR_FENCED` |
| `RuntimeStatus.adopted_runtime_order` (9) | Records a ready first-Start acknowledgement's order on the runner (reset when the executor instance changes or the runner is released); accepts a registration from the same instance whose adopted order is lower for liveness only, and answers retry without acting on its report | Reports the newest Start order it adopted, in the same snapshot as `session_id`, `runtime` and `phase` |
| `ReleaseRunnerRequest.container_exited` (5) | Ends the session of the executor it names, completes its pending Stop, cancels its pending Start, unregisters it and fences both identities, without checking that it is idle; retires the launch it names if no executor matched it | The desktop app sends it for a container it started that exited, or that it stopped without a granted release, until answered |
| `RegisterRunnerRequest.offer_revision` (13), `LaunchRunnerCommand.offer_revision` (5) | Stamps each launch with the launcher's `offer_revision` it checked the allocation against | The desktop app raises it when its GPU or engine changes, and reports `FAILED` for a launch with another revision |
| `LauncherStream`, `ReportRunnerLaunch`, `ReleaseRunner` | Offers a runner with a fresh launcher for the editors it lists; names one launch per waiting allocation in `LauncherState`, first on every stream and on every change, and stops naming it with the allocation; grants a release only under the rules in [Launchers](#launchers) | The desktop app's launcher is not built yet; until it is, no runner registers as `LAUNCHER` |
| `RegisterRunnerRequest.sealed_storage` (8) | Offers sealed sessions only to runners that report `memory_workspace_available` and the sealing algorithm; launches hosted runners with `RUNNER_SEALED_STORAGE=required`, which also keeps their console to metadata, and enough `/dev/shm` (see "Sealed Storage" and "Console Logs" in `hosting.md`) | Reports its mode, whether its memory-backed root can take a session, the budget, and the algorithm ids it accepts |
| `StartRuntimeCommand.sealing` (14), `AcknowledgeRuntimeStartRequest.sealing` (8) | Sends `required` with an `output_recipient` whose private key it holds, for every sealed session, on its first start and on every relaunch; treats a start acknowledged without the expected sealing as failed | Refuses a start that requires sealing unless its memory workspace is available and the recipient is valid; a later start of the session without sealing keeps the session's (that of its first start, or of a later one that ran with stronger sealing), whatever its lifecycle generation; it may not weaken it or change its recipient; acknowledges what the runtime got |
| `reader_key` on `ReadArtifact` (5), `ReadWorkspaceFile` (4) and `ReadWorkspaceSeedArchive` (3) requests, `RunnerReadStreamMetadata.envelope` (5) | Authorizes every read exactly as without a key: the reader key is an encryption destination, never authorization, so a retired runner credential with a fresh key recovers nothing. Then freshly seals the authorized plaintext response to it, and inside a sealed session always answers sealed | Sends its per-session reader key on every read and decrypts sealed responses into a temporary file it publishes only after the whole payload validates; inside a sealed session a response without an envelope fails the read |
| `RunnerReadRequester`, as `requester` on `ListArtifacts` (4), `ReadArtifact` (6), `ReadWorkspaceFile` (5) and `ReadWorkspaceSeedArchive` (4) requests | Authorizes catalog metadata and each read against exactly the named runner, registered process (`instance_id`) and session runtime, together with the runner's current assignment; allows a runtime that is starting or relaunching to read before it is ready; refuses a retired process or runtime; takes `session_id` for a sealed response's envelope identity | Names itself, its process and the session runtime that each catalog query or read is for (fixed when that runtime's uploader or mirror started, never a newer runtime's) |
| Upload `instance_id` (16), start acknowledgement `instance_id` (9) | Binds delivery and acceptance to the registered process and exact runtime assignment | Retains the uploader/mirror process and runtime snapshot for queueing and retries; echoes its process in readiness acknowledgements |
| `RunnerArtifactServiceListArtifactsResponse.deleted_artifacts` (2) | Names the artifacts deleted since the requester's session began (the user removed them from their files, or a temporary one expired), each as it was stored: its catalog name and `created_utc`. Reads them in the same snapshot as the listed artifacts. A permanently deleted artifact, or an expired temporary one, is named only until its row is removed. At most the newest 2,000. One replaced by another upload is not named | Removes its unchanged local copy of exactly each output or temporary artifact named, and a placeholder left under that name, before applying the listing; so it is neither counted as unsaved at Stop nor uploaded again. A copy a process has open is left as it is and tried again with the next listing. A copy whose artifact is neither listed nor named stays known, is not uploaded again, and counts as unsaved |
| `RunnerArtifactServiceUploadArtifactResponse.outcome` (1) | Returns `Committed` only with committed artifact metadata; returns `RejectedStaleRuntime` when authority retired | Marks a matching local version saved or eligible for eviction only for `Committed` with matching metadata |
| Upload `upload_id` (17), upload response `upload_id` (4) | Binds one producer snapshot to its original durable publication result; exact duplicate recovery must not publish another version | Obtains an issued ID for each new secure opened-file snapshot; retains it through retries, queue removal and catalog refresh, and requires the committed response to echo it |
| `RunnerArtifactService.IssueUploadIdentity`, retired-upload outcome | Issues ordered IDs under exact assignment authority and rejects collected IDs before new publication | Keeps the issued ID for the same snapshot; retirement never marks it saved or permits a fresh ID for that snapshot |
| `AcknowledgeSessionStopRequest.cleanup_disposition` (10) | Separates cleanup completion from first explicit saving evidence; unspecified/superseded remain unknown; validates performed cleanup's echoed cutoff | Reports Performed only after actual cleanup, Superseded with unknown evidence otherwise; replays the original result |
| `RunnerArtifactUploadMetadata.envelope` (15) | Opens the transfer with the output recipient private key and streams plaintext in memory into independent Vault-backed artifact encryption | Seals every upload of a session with an output recipient, with a fresh data key per upload and retry; omits `sha256` |
| `AcknowledgeSessionStopRequest.unsaved_artifact_count` (8) | Preserves known losses across processes; complete zero means no unsaved artifacts reported | Counts the artifacts that failed or were still uploading when the session's data was removed, over all its runtimes; a superseded Stop reports none and leaves them for the Stop that performs cleanup |
| `AcknowledgeSessionStopRequest.unsaved_artifacts_unknown` (9) | Preserves uncertainty while it is set, including when the count is zero | Set when the count is incomplete: the uploads did not stop in time, or an artifact folder could not be read |
