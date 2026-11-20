# Runner contracts

`contracts/offloadr/` holds the protobuf definitions of the runner protocol: the files the runner imports, and nothing else.

| File | Contents |
| --- | --- |
| `runner/v1/runner_agent.proto` | `RunnerProtocol`, `RunnerControlService`, `RunnerArtifactService`, `RunnerWorkspaceService` and their messages, including the runtime telemetry snapshot |
| `common/v1/{gpus,models,gpu_control}.proto` | shared enums and GPU control messages |
| `editor_runtime/v1/editor_proxy.proto` | editor artifact metadata, runtime request kinds and the editor bridge types |

These five files are identical on both sides. The control plane's `common/v1/prompt.proto` describes its own prompt records and is not part of the runner protocol.

C# is generated at build time by `Grpc.Tools` in `src/Offloadr.Runner.Contracts`, so building needs only the .NET SDK. Lint with `buf lint contracts`.

## Protocol version

`RunnerProtocol.RUNNER_PROTOCOL_CURRENT` in `runner_agent.proto` is the one protocol this build speaks. The agent sends it in `RegisterRunnerRequest.protocol_version`, `CommandStreamRequest.protocol_version` and the `x-offloadr-runner-protocol` header of every call.

The control plane accepts exactly its own version:

- **Registration** answers any other version with `accepted = false`, a `RegistrationRejection` (`RUNNER_OUTDATED` or `SERVER_OUTDATED`), its own `server_protocol_version` and a message. Fields 1–3 of `RegisterRunnerResponse` never change, so every past runner can read the refusal.
- **Every other call** without the current header is refused with `FAILED_PRECONDITION`. Only `RegisterRunner` and `GetRunnerLaunchPlan` are ungated.
- **The agent** stops on a protocol rejection instead of retrying: it cleans up its sessions as for a shutdown and exits with code 3. The desktop supervisor then restarts it, which asks Offloadr's launch plan for the image to run and downloads it. Any other refusal is still retried.

## Changing the protocol

The Offloadr control plane implements the server side of these services, so a contract change has to land on both sides:

1. Change the `.proto` files here and the agent code that uses them in one pull request, and raise `RUNNER_PROTOCOL_CURRENT`.
2. Keep the five shared protobuf definitions identical on both sides and regenerate every affected consumer. Change the current protocol directly, without legacy layouts or compatibility shims: an outdated runner is refused and updated, never served.
3. Release the coordinated runner and control-plane changes together, in an announced window: a protocol change ends running sessions of runners that have not updated yet. [Image publishing](image-publishing.md#protocol-tags) explains how the images follow. Schema declarations alone do not mean the corresponding behavior is implemented.

## Deleted artifacts

Protocol 3 lets the control plane say which artifacts were deleted, so the runner does not have to guess from an artifact's absence. Every artifact listing names those deleted since the runner's session began, and the runner removes its local copy of each; see the table below and [artifact upload receipts](artifact-upload-receipts.md).

## Prompt handoff

Protocol 2 hands a prompt to the runner as a transient `SubmitPromptCommand`, identified by `handoff_id` and fenced by the runtime it was accepted for (`lifecycle_generation`, `runtime_epoch`, `runtime_instance_id`). The control plane keeps the handoff until `expires_utc` and may redeliver it until it is granted.

- The runner asks `GrantPromptExecution` with its registered process `instance_id` (the one on `CommandStreamRequest`) and the handoff's runtime, and runs the handoff only on `GRANTED`. A retry from the same process after a lost reply is answered `GRANTED` again, so the runner retries transient grant failures with the same request and never runs a handoff twice in one process. It remembers a granted handoff for 65 minutes on its monotonic clock, which relies on the control plane never granting a handoff more than 60 minutes after acceptance. An interrupt or Stop doesn't abandon a grant already sent: `GRANTED` still ends in `NOT_EXECUTED`.
- `ALREADY_GRANTED`, `MISSING_OR_EXPIRED`, `TARGET_NOT_CURRENT`, `ACCOUNT_UNAVAILABLE` or a refused grant: the runner drops the delivery without native I/O and without an acknowledgement.
- After `GRANTED` the runner sends exactly one `AcknowledgePromptResult`: `NATIVE_RESPONSE` with the editor's status, body and content type (`body_omitted` with diagnostic `prompt_native_response_body_omitted` when the body exceeded what it relays, or diagnostic `prompt_stream_incomplete` for a streamed response one of whose chunk reports failed), `NOT_EXECUTED` when nothing reached the editor (`prompt_preparation_incomplete`, `model_downloads_disabled`), or `UNKNOWN` once native I/O started (`prompt_native_result_unknown`). Resending the same result after a lost reply is accepted; the control plane answers `FAILED_PRECONDITION` for a handoff that expired, which the runner does not retry.
- Nothing is recovered or replayed: there is no evidence report and no recovery delivery. ComfyUI's prompt id comes from its own response; the platform injects none. Session events carry the `handoff_id` they belong to.

## Current Stop identity

Every Stop requires a nonempty UUID `command_id` (field 3). Its acknowledgement echoes the captured nonce and the registered runner process in string `instance_id` (field 4). Acknowledgement fields 5–7 echo lifecycle generation, runtime epoch and runtime instance when supplied. Invalid command or missing process identity never enters cleanup. Acknowledgements retry the captured result after cleanup; failed cleanup or host shutdown never acknowledges success.

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

These declarations are shared, but the server behavior below remains pending. The sealed-transfer rows follow `docs/sealed-envelope.md`, which defines the envelope format. Runtime fences apply only when the control plane supplies them; the command nonce and runner process identity above are always required.

| Change | What the control plane does | What the runner does |
| --- | --- | --- |
| `StopSessionCommand` fields `lifecycle_generation` (4), `runtime_epoch` (5), `runtime_instance_id` (6), echoed as acknowledgement fields 5–7 | Sends the identity of the runtime a Stop is meant for | Ignores, but acknowledges, a Stop older than the session's current runtime; one for an older lifecycle still reports that lifecycle's unsaved artifacts; a Stop without `lifecycle_generation` stays unfenced |
| `StartSessionCommand.assignment_sequence` (13) | Increments a per-runner sequence each time it assigns a session to the runner, across sessions | Refuses a start older than the newest assignment it has seen, so a delayed start cannot replace a newer session |
| Lifecycle generations after a Stop | Gives a session a higher `lifecycle_generation` whenever it starts that session again after stopping it | Refuses starts for a session it stopped or replaced at that generation or older |
| `RegisterRunnerRequest.sealed_storage` (8) | Offers sealed sessions only to runners that report `memory_workspace_available` and the sealing algorithm; launches hosted runners with `RUNNER_SEALED_STORAGE=required`, which also keeps their console to metadata, and enough `/dev/shm` (see "Sealed Storage" and "Console Logs" in `hosting.md`) | Reports its mode, whether its memory-backed root can take a session, the budget, and the algorithm ids it accepts |
| `StartSessionCommand.sealing` (14), `AcknowledgeSessionStartRequest.sealing` (8), `LaunchEditorRuntimeCommand.sealing` (18), `AcknowledgeEditorRuntimeLaunchRequest.sealing` (14) | Sends `required` with an `output_recipient` whose private key it holds, for every sealed session, on its start and on every relaunch; treats a launch acknowledged without the expected sealing as failed | Refuses a start or relaunch that requires sealing unless its memory workspace is available and the recipient is valid; a relaunch without sealing keeps the start's (or, when this runner did not see the start, that of the lifecycle's first launch that carried sealing); a relaunch may not weaken it or change its recipient; acknowledges what the runtime got |
| `reader_key` on `ReadArtifact` (5), `ReadWorkspaceFile` (4) and `ReadWorkspaceSeedArchive` (3) requests, `RunnerReadStreamMetadata.envelope` (5) | Authorizes every read exactly as without a key: the reader key is an encryption destination, never authorization, so a retired runner credential with a fresh key recovers nothing. Then freshly seals the authorized plaintext response to it, and inside a sealed session always answers sealed | Sends its per-session reader key on every read and decrypts sealed responses into a temporary file it publishes only after the whole payload validates; inside a sealed session a response without an envelope fails the read |
| `RunnerReadRequester`, as `requester` on `ListArtifacts` (4), `ReadArtifact` (6), `ReadWorkspaceFile` (5) and `ReadWorkspaceSeedArchive` (4) requests | Authorizes catalog metadata and each read against exactly the named runner, registered process (`instance_id`) and session runtime, together with the runner's current assignment; allows a runtime that is starting or relaunching to read before it is ready; refuses a retired process or runtime; takes `session_id` for a sealed response's envelope identity | Names itself, its process and the session runtime that each catalog query or read is for (fixed when that runtime's uploader or mirror started, never a newer runtime's) |
| Upload `instance_id` (16), Start acknowledgement `instance_id` (9), Launch acknowledgement `instance_id` (15) | Binds delivery and acceptance to the registered process and exact runtime assignment | Retains the uploader/mirror process and runtime snapshot for queueing and retries; echoes its process in readiness acknowledgements |
| `RunnerArtifactServiceListArtifactsResponse.deleted_artifacts` (2) | Names the artifacts deleted since the requester's session began (the user removed them from their files, or a temporary one expired), each as it was stored: its catalog name and `created_utc`. Reads them in the same snapshot as the listed artifacts, and keeps naming a permanently deleted one for as long as a session that was running when it was deleted goes on. At most the newest 2,000. One replaced by another upload is not named | Removes its unchanged local copy of exactly each output or temporary artifact named, and a placeholder left under that name, before applying the listing; so it is neither counted as unsaved at Stop nor uploaded again. A copy a process has open is left as it is and tried again with the next listing |
| `RunnerArtifactServiceUploadArtifactResponse.outcome` (1) | Returns `Committed` only with committed artifact metadata; returns `RejectedStaleRuntime` when authority retired | Marks a matching local version saved or eligible for eviction only for `Committed` with matching metadata |
| Upload `upload_id` (17), upload response `upload_id` (4) | Binds one producer snapshot to its original durable publication result; exact duplicate recovery must not publish another version | Obtains an issued ID for each new secure opened-file snapshot; retains it through retries, queue removal and catalog refresh, and requires the committed response to echo it |
| `RunnerArtifactService.IssueUploadIdentity`, retired-upload outcome | Issues ordered IDs under exact assignment authority and rejects collected IDs before new publication | Keeps the issued ID for the same snapshot; retirement never marks it saved or permits a fresh ID for that snapshot |
| `AcknowledgeSessionStopRequest.cleanup_disposition` (10) | Separates cleanup completion from first explicit saving evidence; unspecified/superseded remain unknown; validates performed cleanup's echoed runtime | Reports Performed only after actual cleanup, Superseded with unknown evidence otherwise; replays the original result |
| `RunnerArtifactUploadMetadata.envelope` (15) | Opens the transfer with the output recipient private key and streams plaintext in memory into independent Vault-backed artifact encryption | Seals every upload of a session with an output recipient, with a fresh data key per upload and retry; omits `sha256` |
| `AcknowledgeSessionStopRequest.unsaved_artifact_count` (8) | Preserves known losses across processes; complete zero means no unsaved artifacts reported | Counts the artifacts that failed or were still uploading when the session's data was removed |
| `AcknowledgeSessionStopRequest.unsaved_artifacts_unknown` (9) | Preserves uncertainty while it is set, including when the count is zero | Set when the count is incomplete: the uploads did not stop in time, or an artifact folder could not be read |
