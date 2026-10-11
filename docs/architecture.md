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

The desktop app is a MAUI Blazor Hybrid window around `Offloadr.Runner.UI`, following the structure of a shared Razor UI with thin hosts. MAUI does not run hosted services, so `DesktopRuntime` starts them in order: a single-instance lock first, then the supervisor. The runner key is encrypted with ASP.NET Core Data Protection, whose key ring is protected with DPAPI. Settings, keys and logs live in `%LOCALAPPDATA%\OffloadrRunner`, or in `OFFLOADR_RUNNER_DATA_DIRECTORY` when set. The app lives in the Windows notification area: closing the window hides it, and the tray menu opens it, starts or stops the runner, or quits. `--minimized` starts it hidden, for a login shortcut. Quitting stops the runner container; the app asks first when the runner is active, and warns when a session would be interrupted. The tray icon uses `Shell_NotifyIcon` directly, with the app window subclassed to receive its messages.

## Origin

The agent was developed alongside the Offloadr control plane and moved here without its earlier history. Its behavior is unchanged apart from three additions: it reports its own version instead of the .NET runtime's, logs where its source is at startup, and writes the status file.
