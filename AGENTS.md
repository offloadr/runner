# AGENTS.md

## What this repository is

The Offloadr runner: the agent that executes GPU work for the Offloadr control plane, and the container images it ships in. The same images serve personal runners (users' own GPUs) and hosted runners (GPUs Offloadr rents). Licensed AGPL-3.0-only.

## Repository map

- `contracts/`: protobuf for the runner protocol, copied from the control plane. See `docs/contracts-sync.md`.
- `src/Offloadr.Runner.Contracts`: C# generated from `contracts/` at build time (Grpc.Tools), plus small shared helpers.
- `src/Offloadr.Runner.Core`: transport, authentication, registration backoff, logging relays, model transfer (aria2), status file. Must not depend on Linux-only code.
- `src/Offloadr.Runner.Linux`: the in-container session runtime: command loop, per-session Linux users, editor child process, model VFS shim and its IPC, demand-aware hydration, workspace/artifact mirroring.
- `src/Offloadr.Runner.Agent`: the container entry point (assembly `RunnerAgent`) and `Dockerfile`.
- `docker-bake.hcl`: the four published image variants.
- `tests/Offloadr.Runner.Tests`: NUnit suite. Linux-native tests live in `Linux/` and need root, `gcc` and `aria2c`.
- `docs/`: architecture, hosting guide, contracts sync.

## Coding standards

- Follow the existing style of the file you edit. Keep changes scoped to the task.
- Do not hand-edit generated code; change `contracts/` instead.
- Core must not reference Offloadr.Runner.Linux. Desktop and supervisor hosts must never reference Offloadr.Runner.Linux.
- Never log secrets, signed URLs, plaintext artifact keys or bearer metadata.

## Agent boundary

- The agent is an authenticated external executor of exact control-plane commands. It does not own durable allocation, lifecycle, restart, Stop, prompt or metering decisions.
- Heartbeat registration (`RegisterRunner`) is the authoritative liveness lane; other traffic does not silently refresh liveness.
- Keep long-running status, command, event, artifact/workspace and operational lanes independently cancellable and reconnectable.

## Command and session rules

- Validate and retain exact runner, session, lifecycle generation, epoch, revision, attempt, command and runtime-instance identity through execution and acknowledgement.
- Make exact command retries idempotent. Never apply an older command to a newer runtime or logical session.
- Retry acknowledgements with the same result and identity without re-executing a completed action.
- Distinguish host shutdown cancellation, workflow cancellation, Stop and command failure. Host shutdown is never a successful acknowledgement.
- Preserve the logical runner session through an intentional child-runtime restart.
- Clear matching logical session and runtime identity state after failed start/launch cleanup or unexpected child exit, so heartbeats do not protect a dead process.
- Cleanup tolerates partially started sidecars, already-stopped processes and repeated calls.
- Gate runtime-specific probes and behavior explicitly by runtime kind (ComfyUI, Forge Neo, future runtimes).
- Preserve Linux-only assumptions in Offloadr.Runner.Linux unless adding explicit multi-platform support.

## Testing rules

- Cover duplicate and stale commands, acknowledgement replay, reconnect ordering, cancellation classes, and Stop during startup or restart.
- Verify failed start/launch and unexpected-exit cleanup clears only the matching logical session and runtime identity.
- Test runtime-kind gating, bridge connection identity (Active/Heartbeat/Inactive), and idempotent process/download/workspace/artifact cleanup.
- Linux-native tests call `LinuxTestPrerequisites` and are ignored elsewhere; `RUNNER_NATIVE_TESTS_REQUIRED=1` turns a missing prerequisite into a failure (CI sets it).

## Validation commands

- Build: `dotnet build runner.slnx -warnaserror`
- Format: `dotnet format runner.slnx --verify-no-changes`
- Tests on Linux: `dotnet test runner.slnx`
- Full suite from Windows (runs in a Linux container): `./build/test-linux.sh`
- Contracts lint: `buf lint contracts`
- One image: `docker buildx bake comfyui-latest --load`

## Commits

- Commit as the identity configured in this checkout. Keep subjects imperative and short.
- Do not reference private repositories, internal plans or internal environments in commits, code or docs.
