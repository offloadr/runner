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
