# AGENTS.md

## What this repository is

The Offloadr runner: the agent that executes GPU work for the Offloadr control plane, and the container images it ships in. The same images serve personal runners (users' own GPUs) and hosted runners (GPUs Offloadr rents). Licensed AGPL-3.0-only.

## Repository map

- `contracts/`: protobuf for the runner protocol, copied from the control plane. See `docs/contracts-sync.md`.
- `src/Offloadr.Runner.Contracts`: C# generated from `contracts/` at build time (Grpc.Tools), plus small shared helpers.
- `src/Offloadr.Runner.Core`: transport, authentication, registration backoff, logging relays, model transfer (aria2), status file, the local model inventory (scanner and sync). Must not depend on Linux-only code.
- `src/Offloadr.Runner.Linux`: the in-container session runtime: command loop, per-session Linux users, editor child process, model VFS shim and its IPC, demand-aware hydration, workspace/artifact mirroring.
- `src/Offloadr.Runner.Agent`: the container entry point (assembly `RunnerAgent`) and `Dockerfile`.
- `src/Offloadr.Runner.Supervisor`: runs the runner container on a desktop (settings, Docker CLI, launch plans, supervision). Never shipped in the image.
- `src/Offloadr.Runner.UI`: Razor class library with the app's pages (MudBlazor). No logic beyond presentation; it reads `RunnerSupervisor`.
- `src/Offloadr.Runner.Desktop`: Windows MAUI Blazor Hybrid host (assembly `OffloadrRunner`). Built from `runner.desktop.slnx`, which needs the `maui-windows` workload; `runner.slnx` builds everything else on any OS.
- `build/`: `test-linux.sh` (full suite in a container), `publish-windows.ps1` (desktop app and installer), `verify-windows-signatures.ps1` (its signatures) and `verify-published-image.sh` (post-push registry proof for image releases).
- `docker-bake.hcl`: the four published image variants.
- `tests/Offloadr.Runner.Tests`: NUnit suite. Linux-native tests live in `Linux/` and need root, `gcc` and `aria2c`.
- `tests/Offloadr.Runner.Supervisor.Tests`: supervisor tests with fakes; they run on any OS. `RUNNER_REAL_DOCKER_IMAGE=<image>` enables one test against the real Docker engine, and `RUNNER_REAL_API_ORIGIN` with `RUNNER_REAL_KEY` one against a real control plane.
- `docs/`: architecture, hosting guide, contracts sync, and the manual GPU release checklist (`release-checklist.md`), since CI has no GPUs.

## Coding standards

- Follow the existing style of the file you edit. Keep changes scoped to the task.
- Do not hand-edit generated code; change `contracts/` instead.
- Core must not reference Offloadr.Runner.Linux. Supervisor, UI and Desktop must never reference Offloadr.Runner.Linux.
- Never log secrets, signed URLs, plaintext artifact keys or bearer metadata.

## Protocol compatibility policy

- One current protocol (`RunnerProtocol.RUNNER_PROTOCOL_CURRENT`). Coordinated protocol changes raise it and update both sides directly; an outdated runner is refused and told to update. Do not add backward-compatibility shims or preserve superseded wire layouts. See `docs/contracts-sync.md`.
- A runtime is named by `RuntimeRef` (`runtime_id`, `runtime_order`). The Stop acknowledgement uses `command_id = 3`, string `instance_id = 4` and the echoed `up_to_runtime_order = 11`. Earlier layouts are intentionally unsupported.
- Keep all five shared definitions identical with the control plane and regenerate affected consumers as described in `docs/contracts-sync.md`.

## Agent boundary

- The agent is an authenticated external executor of exact control-plane commands. It does not own durable allocation, lifecycle, restart, Stop, prompt or metering decisions.
- Heartbeat registration (`RegisterRunner`) is the authoritative liveness lane; other traffic does not silently refresh liveness.
- The agent registers as `RUNNER_ROLE_EXECUTOR`. The desktop app may register the same runner as `RUNNER_ROLE_LAUNCHER`: that heartbeat means only "can start these editors". It never stands in for executor liveness or protects a session, and the launcher starts a container only for the launch the latest `LauncherState` on a connected stream names (`docs/contracts-sync.md`, Launchers). Shared registration code lives in Core (`RunnerRegistrationLoop`).
- Keep long-running status, command, event, artifact/workspace and operational lanes independently cancellable and reconnectable.

## Command and session rules

- Validate and retain exact runner, session, command and runtime (`RuntimeRef`) identity through execution and acknowledgement. Two runtimes are the same when their `runtime_id` is; the higher `runtime_order` is newer, across sessions. `lifecycle_generation` only matches GPU power commands.
- A start is refused when its order is at or below its session's Stop cutoff or the eviction floor, below the newest start adopted, or equal to it with another id; any start but a repeat of the adopted runtime is also refused at or below the settled order. A repeated start of the adopted runtime joins its startup or replays its recorded acknowledgement; it never launches twice. A newer start replaces: it cancels the startup in flight, stops the session's older child (or pre-empts another session), then launches.
- A Stop compares once: a runtime, child or startup of its session above its cutoff makes it superseded, and it does nothing. Otherwise it records the cutoff before cleanup.
- The settled order (`RuntimeStatus.settled_runtime_order`) rises when a start is adopted, when a start ends not ready (before its acknowledgement is sent) and when a Stop records its cutoff. It never reaches a start that is admitted and not yet adopted, and it is read in one snapshot with the session. Nothing starts at or below a value a status carried.
- Never apply an older command to a newer runtime or logical session.
- Retry acknowledgements with the same result and identity without re-executing a completed action.
- Distinguish host shutdown cancellation, workflow cancellation, Stop and command failure. Host shutdown is never a successful acknowledgement.
- Preserve the logical runner session through an intentional child-runtime restart. A relaunch keeps the session's sealing and may not weaken it.
- Clear matching logical session and runtime identity state after failed start cleanup or unexpected child exit, so heartbeats do not protect a dead process.
- Cleanup tolerates partially started sidecars, already-stopped processes and repeated calls.
- Gate runtime-specific probes and behavior explicitly by runtime kind (ComfyUI, Forge Neo, future runtimes).
- Preserve Linux-only assumptions in Offloadr.Runner.Linux unless adding explicit multi-platform support.

## Supervisor loop

- `RunnerSupervisor` state is read and written on its loop alone, with no lock. Other threads hand it immutable values (`Intent`, the settings, the link's state) and wake it; they read back only what it published (`View`, the offer).
- The loop never waits for Docker or Offloadr. Those calls live in `SupervisorEffects`, which holds no supervisor state, and run as jobs in a slot; a result comes back as a message and is recorded as a fact. Do not add an `await` to the loop, or state to `SupervisorEffects`.
- A procedure that takes several jobs (going online, a launch, a Stop, an idle release) is run again from its top on every tick. Keep nothing across a job in a local: what a later tick needs is a named field of the procedure, and every check is made against the tick's inputs.
- What is owed (a launch outcome, a container gone, a stopped container an engine kept, a container of unknown fate) is data, worked off on every tick in every state. Do not tie it to the procedure that incurred it.
- A signal that must reach a job at once from another thread (a Stop, Settings naming another key) cancels a token from the fence. Never share mutable state for it.
- A container is created (`CreateAsync`), then started by its ID (`StartAsync`); never the two in one engine call. A created container runs nothing, so nothing this app did not start runs unseen, and a start that fails is always a start of a container that can be asked about.
- The last check that a launch may start is made right before the engine's process is created (`ProcessSpec.StillWanted`), off the loop and outside the link's lock: creating a process can stall, and neither the loop nor the link may wait for it. The same for a release that makes room: checked right before the call, which is made outside the lock. A state that arrives in the instant after that check is answered as one that arrives after the start (the withdrawal is reported, and the container released before it is stopped).
- A container that started and exited is reported with `container_exited`, never `FAILED`; one that never started gets `FAILED` alone. While the engine does not say which, neither is sent.
- "Never started" needs proof: the engine refused the start itself (`ContainerEngineException.Refused`) and the container was then seen created. After a start whose answer was lost or given up, a container seen created may still have run, and is reported gone. The engine refusing to remove a container because it runs is proof that it started.

## Testing rules

- Cover duplicate and stale commands, acknowledgement replay, reconnect ordering, cancellation classes, and Stop during startup or restart.
- Cover the start and Stop orderings: a Stop before an older delayed start, an older Stop after a newer start, a repeated start while launching and after it finished, a newer start for the same session, an evicted Stop cutoff, and a start for an older assignment. Cover the settled order for each: refused, failed before adoption, admitted and not yet adopted, a cutoff with no session held, and a superseded Stop.
- Verify failed start and unexpected-exit cleanup clears only the matching logical session and runtime identity.
- Test runtime-kind gating, native connection bridge state and reconnects, the heartbeat's runtime status, and idempotent process/download/workspace/artifact cleanup.
- Linux-native tests call `LinuxTestPrerequisites` and are ignored elsewhere; `RUNNER_NATIVE_TESTS_REQUIRED=1` turns a missing prerequisite into a failure (CI sets it).

## Validation commands

- Build: `dotnet build runner.slnx -warnaserror`
- Format: `dotnet format runner.slnx --verify-no-changes`
- Tests on Linux: `dotnet test runner.slnx`
- Full suite from Windows (runs in a Linux container): `./build/test-linux.sh`
- Contracts lint: `buf lint contracts`
- One image: `docker buildx bake comfyui-latest --load`
- Desktop app (Windows): `dotnet build runner.desktop.slnx -warnaserror`; set `OFFLOADR_RUNNER_DATA_DIRECTORY` to a scratch folder when trying it, so your own settings and runner key are untouched.

## Commits

- Commit as the identity configured in this checkout. Keep subjects imperative and short.
- Do not reference private repositories, internal plans or internal environments in commits, code or docs.
