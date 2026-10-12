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

These declarations are shared, but the server behavior below remains pending. Runtime fences apply only when the control plane supplies them; the command nonce and runner process identity above are always required.

| Change | What the control plane does | What the runner does |
| --- | --- | --- |
| `StopSessionCommand` fields `lifecycle_generation` (4), `runtime_epoch` (5), `runtime_instance_id` (6), echoed as acknowledgement fields 5–7 | Sends the identity of the runtime a Stop is meant for | Ignores, but acknowledges, a Stop older than the session's current runtime; a Stop without `lifecycle_generation` stays unfenced |
| `StartSessionCommand.assignment_sequence` (13) | Increments a per-runner sequence each time it assigns a session to the runner, across sessions | Refuses a start older than the newest assignment it has seen, so a delayed start cannot replace a newer session |
| Lifecycle generations after a Stop | Gives a session a higher `lifecycle_generation` whenever it starts that session again after stopping it | Refuses starts for a session it stopped or replaced at that generation or older |
| `RunnerControlService.GetRunnerLaunchPlan` RPC | Returns the runner images per editor template and the owner's selected template, without touching runner liveness | The desktop supervisor uses it to choose the image; until it is implemented the supervisor falls back to its built-in list of published images |
