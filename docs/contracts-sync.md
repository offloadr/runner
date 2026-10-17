# Runner contracts

`contracts/offloadr/` holds the protobuf definitions of the runner protocol: the files the runner imports, and nothing else.

| File | Contents |
| --- | --- |
| `runner/v1/runner_agent.proto` | `RunnerControlService`, `RunnerArtifactService`, `RunnerWorkspaceService` and their messages |
| `runner/v1/allocator.proto` | runtime telemetry snapshot |
| `common/v1/{gpus,models,prompt,gpu_control}.proto` | shared enums and prompt relay messages |
| `editor_runtime/v1/editor_proxy.proto` | editor artifact metadata and runtime request kinds |

C# is generated at build time by `Grpc.Tools` in `src/Offloadr.Runner.Contracts`, so building needs only the .NET SDK. Lint with `buf lint contracts`.

## Changing the protocol

The Offloadr control plane implements the server side of these services, so a contract change has to land on both sides:

1. Change the `.proto` files here and the agent code that uses them in one pull request.
2. Keep the seven shared protobuf definitions identical on both sides and regenerate every affected consumer. The platform is pre-deployment: change the current protocol directly, without legacy layouts or compatibility shims.
3. Release the coordinated runner and control-plane changes together. Schema declarations alone do not mean the corresponding behavior is implemented.

## Current Stop identity

Every Stop requires a nonempty UUID `command_id` (field 3). Its acknowledgement echoes the captured nonce and the registered runner process in string `instance_id` (field 4). Acknowledgement fields 5–7 echo lifecycle generation, runtime epoch and runtime instance when supplied. Invalid command or missing process identity never enters cleanup. Acknowledgements retry the captured result after cleanup; failed cleanup or host shutdown never acknowledges success.

## Pending on the control plane

These declarations are shared, but the server behavior below remains pending. The sealed-transfer rows follow `docs/sealed-envelope.md`, which defines the envelope format. Runtime fences apply only when the control plane supplies them; the command nonce and runner process identity above are always required.

| Change | What the control plane does | What the runner does |
| --- | --- | --- |
| `StopSessionCommand` fields `lifecycle_generation` (4), `runtime_epoch` (5), `runtime_instance_id` (6), echoed as acknowledgement fields 5–7 | Sends the identity of the runtime a Stop is meant for | Ignores, but acknowledges, a Stop older than the session's current runtime; a Stop without `lifecycle_generation` stays unfenced |
| `StartSessionCommand.assignment_sequence` (13) | Increments a per-runner sequence each time it assigns a session to the runner, across sessions | Refuses a start older than the newest assignment it has seen, so a delayed start cannot replace a newer session |
| Lifecycle generations after a Stop | Gives a session a higher `lifecycle_generation` whenever it starts that session again after stopping it | Refuses starts for a session it stopped or replaced at that generation or older |
| `RegisterRunnerRequest.sealed_storage` (8) | Offers sealed sessions only to runners that report `memory_workspace_available` and the sealing algorithm; launches hosted runners with `RUNNER_SEALED_STORAGE=required` and enough `/dev/shm` (see "Sealed Storage" in `hosting.md`) | Reports its mode, whether its memory-backed root can take a session, the budget, and the algorithm ids it accepts |
| `StartSessionCommand.sealing` (14), `AcknowledgeSessionStartRequest.sealing` (8), `LaunchEditorRuntimeCommand.sealing` (18), `AcknowledgeEditorRuntimeLaunchRequest.sealing` (14) | Sends `required` with an `output_recipient` whose private key it holds, for every sealed session, on its start and on every relaunch; treats a launch acknowledged without the expected sealing as failed | Refuses a start or relaunch that requires sealing unless its memory workspace is available and the recipient is valid; a relaunch without sealing keeps the start's (or, when this runner did not see the start, that of the lifecycle's first launch that carried sealing); a relaunch may not weaken it or change its recipient; acknowledges what the runtime got |
| `reader_key` on `ReadArtifact` (5), `ReadWorkspaceFile` (4) and `ReadWorkspaceSeedArchive` (3) requests, `RunnerReadStreamMetadata.envelope` (5) | Authorizes every read exactly as without a key: the reader key is an encryption destination, never authorization, so a retired runner credential with a fresh key recovers nothing. Then seals the response to it, re-wrapping a stored sealed upload or encrypting fresh, and inside a sealed session always answers sealed | Sends its per-session reader key on every read and decrypts sealed responses into a temporary file it publishes only after the whole payload validates; inside a sealed session a response without an envelope fails the read |
| `RunnerReadRequester`, as `requester` on `ReadArtifact` (6), `ReadWorkspaceFile` (5) and `ReadWorkspaceSeedArchive` (4) requests | Authorizes each read against exactly the named runner, registered process (`instance_id`) and session runtime, together with the runner's current assignment; allows a runtime that is starting or relaunching to read before it is ready; refuses a retired process or runtime; takes `session_id` for a sealed response's envelope identity | Names itself, its process and, on every read, the session runtime that read is for (fixed when that runtime's uploader or mirror started, never a newer runtime's) |
| `RunnerArtifactUploadMetadata.envelope` (15) | Stores the ciphertext with its envelope and opens it with the output recipient's private key | Seals every upload of a session with an output recipient, with a fresh data key per upload and retry; omits `sha256` |
| `AcknowledgeSessionStopRequest.unsaved_artifact_count` (8) | Never reports a session as fully saved while the count is not zero | Counts the artifacts that failed or were still uploading when the session's data was removed |
| `AcknowledgeSessionStopRequest.unsaved_artifacts_unknown` (9) | Never reports a session as fully saved while it is set, whatever the count | Set when the count is incomplete: the uploads did not stop in time, or an artifact folder could not be read |
| `RunnerControlService.GetRunnerLaunchPlan` RPC | Returns the runner images per editor template and the owner's selected template, without touching runner liveness | The desktop supervisor uses it to choose the image; until it is implemented the supervisor falls back to its built-in list of published images |
