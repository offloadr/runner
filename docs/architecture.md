# Architecture

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

## Projects

| Project | Role | Platform |
| --- | --- | --- |
| Offloadr.Runner.Contracts | generated gRPC/protobuf code and shared helpers | any |
| Offloadr.Runner.Core | gRPC channels, runner-key auth, registration backoff, options, log and telemetry relays, aria2 backend and transfer index, status file | any |
| Offloadr.Runner.Linux | command loop, session process manager, Linux user isolation, VFS shim and IPC, hydration coordinator, workspace and artifact mirroring, local model projection, GPU power and telemetry | Linux |
| Offloadr.Runner.Agent | entry point and `Dockerfile` | Linux container |

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

## Origin

The agent was developed alongside the Offloadr control plane and moved here without its earlier history. Its behavior is unchanged apart from three additions: it reports its own version instead of the .NET runtime's, logs where its source is at startup, and writes the status file.
