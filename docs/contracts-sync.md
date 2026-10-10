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
2. Keep changes wire-compatible: add fields and RPCs, and never renumber or reuse field numbers. A runner and a control plane on adjacent versions must interoperate.
3. The control plane picks up the same `.proto` change before, or together with, the release that depends on it. Until then the agent must tolerate `UNIMPLEMENTED` for a new RPC and treat new fields as unset.

## Pending on the control plane

These contract additions are in the runner already. Each is optional, so the runner works with a control plane that does not send them yet, but the protection it gives only starts once the control plane does.

| Change | What the control plane does | What the runner does |
| --- | --- | --- |
| `StopSessionCommand` fields `command_id` (3), `lifecycle_generation` (4), `runtime_epoch` (5), `runtime_instance_id` (6), echoed in `AcknowledgeSessionStopRequest` | Sends the identity of the runtime a Stop is meant for | Ignores, but acknowledges, a Stop older than the session's current runtime; a Stop without `lifecycle_generation` stays unfenced |
| `StartSessionCommand.assignment_sequence` (13) | Increments a per-runner sequence each time it assigns a session to the runner, across sessions | Refuses a start older than the newest assignment it has seen, so a delayed start cannot replace a newer session |
| Lifecycle generations after a Stop | Gives a session a higher `lifecycle_generation` whenever it starts that session again after stopping it | Refuses starts for a session it stopped or replaced at that generation or older |
