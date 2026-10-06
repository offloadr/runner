# Hosting the runner (Docker)

This guide describes how to run the runner image on your own GPU host.

It is primarily for personal runner installs. Hosted runners run the same images, built from this repository; Offloadr provisions their host, runner key and lifecycle.

## Runtime Requirements

- Docker with Linux containers.
- NVIDIA GPU with container GPU support (`--gpus all`).
- Linux host or Windows host using Docker Desktop (PowerShell flow below).
- A personal runner key from Settings (`Personal Runner Keys`).
- The isolation mode requires root privileges inside the container because session setup uses `useradd`, `chown`, and `userdel`.
- The published runner image defaults to root, so `--user root` is not required unless your runtime overrides the container user.

## Offloadr API Connectivity

- `OFFLOADR_API_GRPC`: native gRPC endpoint exposed on the public app host (TLS, HTTP/2).
- The runner defaults to `https://offloadr.studio` when `OFFLOADR_API_GRPC` is not set. Set it explicitly for non-prod environments.
- Offloadr API endpoint must use `https://` when runner secret auth is enabled.
- Optional: `RUNNER_GRPC_TRACE_HTTP=1` to log low-level gRPC HTTP transport traffic.

## Startup-first Quick Start

User-facing image:

- `ghcr.io/offloadr/runner-comfyui:latest`
- `ghcr.io/offloadr/runner-forge-neo:neo`

`latest` matches the stable ComfyUI editor channel and `master` the ComfyUI master channel. Forge Neo uses `ghcr.io/offloadr/runner-forge-neo:neo` for the default Forge route and `:latest` for the latest Forge template. Hosted runners use the same images. Runner registration advertises supported editor templates, so a ComfyUI runner is not offered for a Forge editor and a Forge runner is not offered for ComfyUI.

Linux (bash):

```bash
docker run --rm -it \
  --pull always --gpus all \
  -e RUNNER_SECRET=replace-with-your-runner-key \
  ghcr.io/offloadr/runner-comfyui:latest
```

Windows (PowerShell, Docker Desktop):

```powershell
docker run --rm -it `
  --pull always --gpus all `
  -e RUNNER_SECRET=replace-with-your-runner-key `
  ghcr.io/offloadr/runner-comfyui:latest
```

For non-prod environments, add `-e OFFLOADR_API_GRPC=<environment-origin>`.

## Key Environment Variables

- Required for startup
  - `RUNNER_SECRET` (required; pre-provisioned secret tied to owner)
- Offloadr API
  - `OFFLOADR_API_GRPC` (optional; defaults to `https://offloadr.studio`; use a public HTTPS gRPC endpoint)
- Optional identity/retry tuning
  - `RUNNER_ID` (optional UUID override; primarily for Offloadr-managed hosted runners)
  - `REG_MIN_BACKOFF_SEC`
  - `REG_MAX_BACKOFF_SEC`
  - `REG_RPC_TIMEOUT_SEC`
- Optional local model discovery (ephemeral local-only inventory)
  - `RUNNER_LOCAL_MODELS_DIR` (mounted directory to scan, typically `/local`; auto-detected when `/local` exists)
  - `RUNNER_LOCAL_MODELS_SCAN_INTERVAL_SEC` (scan interval, default `15`)
- Optional session runtime
  - `RUNNER_SEED_VENV_PATH` (root-owned seed virtualenv used to create each writable session-local `.venv`, default `/opt/venv`)
  - `RUNNER_UV_BINARY` (runner-owned `uv` executable used to create session virtualenvs, default `/usr/local/bin/uv`)
  - `RUNNER_PERSISTENT_RANGE_HYDRATION` (image-owned runtime capability; `1` for persistent ComfyUI model roots and `0` for Forge Neo)
  - `RUNNER_VFS_LOG` (`1` enables verbose native shim and VFS IPC tracing; default `0`)
- Optional live runtime telemetry
  - `RUNNER_GPU_STATS_INTERVAL_SEC` (poll interval for active-session runtime telemetry via `nvidia-smi`, `/proc/meminfo`, and the model download filesystem; default `5`, `0` disables)
- Optional aria2 download tuning
  - `ARIA2_MAX_CONNECTION_PER_SERVER` (same-file connections per host, default `16`)
  - `ARIA2_SPLIT` (same-file split count, default `16`)
  - `ARIA2_MIN_SPLIT_SIZE` (minimum chunk size before splitting, default `8M`)
  - `ARIA2_FILE_ALLOCATION` (file allocation mode, default `falloc`)
  - `RUNNER_MODEL_DOWNLOAD_RETRY_INITIAL_SEC` (initial delay after a transient terminal aria2 error, default `2`)
  - `RUNNER_MODEL_DOWNLOAD_RETRY_MAX_SEC` (maximum delay between transient retries, default `60`)

Aria2 baseline notes:

- The runner now defaults to a single-mirror same-file profile of `16/16/8M/falloc`.
- Queue-level download concurrency is fixed at `1`; the connection and split settings only parallelize pieces within that one active model transfer.
- Raw aria2 console output is suppressed because per-connection retries are not authoritative for the overall transfer and redirected source URLs may contain signed query parameters. RunnerAgent reports structured transfer progress and terminal failures through aria2 RPC state instead.
- `falloc` is the intended Linux default for modern filesystems such as ext4, xfs, and btrfs.
- Persistent ComfyUI model roots use demand-aware hydration: registered placeholders are sparse files sized to the catalog length, and only aria2's completed-piece bitfield makes a byte range readable. Forge Neo remains on full prompt-time hydration until it has a persistent model cache.
- Model download speed shown in the web UI comes from aria2's current `downloadSpeed`; the browser does not infer speed from activity timestamps.
- After aria2 exhausts its own attempts, the runner resumes transiently failed model downloads indefinitely with capped exponential backoff. Timeouts, network/DNS failures, low-speed aborts, unfinished transfers, and temporary server overloads retry until the session is cancelled; unknown errors (including TLS/certificate failures), permanent source, authentication, checksum, path, and filesystem failures remain terminal.
- On the full-hydration path, an existing file with the expected size is accepted before the runner starts a download, but never between retry attempts. Range-managed files never use length as readiness evidence.

## Workspace Mirroring

- External GPU runners now mirror shared Comfy workspace state before ComfyUI starts.
- `/comfyui/user` and `/comfyui/custom_nodes` are hydrated eagerly from ZIP seed archives streamed directly over authenticated gRPC.
- `/comfyui/input` is seeded from committed `input` artifacts as placeholder files and fetched lazily on first read through the runner VFS socket.
- Each session gets a private writable `.venv` created from the root-owned seed virtualenv at `RUNNER_SEED_VENV_PATH` using `RUNNER_UV_BINARY`. The runner recursively validates that seeded payloads are not group/other writable, materializes seed `bin` tools into the session venv, copies seed package metadata, and symlinks package payload directories back to the seed, so package inspection sees baked packages while new non-conflicting session installs stay local to that session.
- Committed `output` files and runner-written `temp` files are uploaded back through the artifact service. Runner-side mutations under `user`, `custom_nodes`, and `input` are not persisted.
- The runner image keeps bundled fallback seeds for `custom_nodes` and `input`; the `input` seed is session-local and is not copied into the shared artifact store.

Example with host-local model mount and scanning enabled:

```bash
docker run --rm -it \
  --pull always --gpus all \
  -v /host/models:/local:ro \
  -e OFFLOADR_API_GRPC=<environment-origin> \
  -e RUNNER_SECRET=replace-with-your-runner-key \
  -e RUNNER_LOCAL_MODELS_DIR=/local \
  -e RUNNER_LOCAL_MODELS_SCAN_INTERVAL_SEC=15 \
  ghcr.io/offloadr/runner-comfyui:latest
```

Forge Neo example:

```bash
docker run --rm -it \
  --pull always --gpus all \
  -v /host/models:/local:ro \
  -e OFFLOADR_API_GRPC=<environment-origin> \
  -e RUNNER_SECRET=replace-with-your-runner-key \
  -e RUNNER_LOCAL_MODELS_DIR=/local \
  ghcr.io/offloadr/runner-forge-neo:neo
```

Local model notes:

- Local model entries are ephemeral and local-only (not persisted in the shared catalog DB).
- Matching studio-backed models are marked in Installed while the corresponding personal runner is active.
- The runner reports only opaque selection hashes derived from `filename + size + category`; raw local filenames and directory paths do not leave the runner.
- Runtime model folders only receive symlinks for registered studio models whose selection hash matches the active runner's local inventory.
- When a prompt references a catalog model whose filename exists locally but whose size or category differs, the runner logs the mismatched metadata and downloads the catalog version instead of silently substituting different weights.
- Runner-only local files do not appear in Installed until you add the same model to your studio catalog.
- If you add/remove files under the mounted directory, the runner auto-rescans and updates inventory on the next scan interval. Unchanged inventories refresh by digest only to keep the Offloadr API lease alive. The Model Manager also supports manual refresh.

Runtime telemetry notes:

- Active-session runtime telemetry is best-effort only and does not affect runner startup or session health.
- GPU metrics and power-limit changes depend on `nvidia-smi` being available inside the runner container/runtime.
- Power changes require a durable one-time execution grant for the exact editor,
  GPU lifecycle/allocation/generation and runner session. The runner refreshes
  physical min/max bounds before applying explicit watts; it never clamps or
  retargets a command. Lost acknowledgements retry the original result only.
  Ambiguous execution remains unknown. Replacement session activation waits for
  an in-flight physical command (and its native process) to finish, independently
  of acknowledgement retries. See [GPU control workflows](gpu-control-workflows.md).
- Current telemetry includes first-visible-GPU metrics, system RAM, and the filesystem backing `ARIA2_DOWNLOAD_DIR` (default `/models`).
- The runner now keeps multiple persistent native gRPC channels:
  - one status lane for explicit `RegisterRunner` heartbeats
  - one command lane for start/stop/prompt/power-limit delivery and acks
  - one session-event lane for websocket prompt traffic
  - one ops lane for logs, download progress, runtime telemetry, and local model sync
  - one separate artifact/workspace lane for bulk file transfer
- Heartbeats stay unary, but they reuse the same long-lived status channel; the runner does not reconnect a fresh gRPC channel for every heartbeat.

Runtime restart notes:

- An authenticated CPU ComfyUI-Manager v2 reboot can cause Offloadr to issue durable `QUIESCE` and `LAUNCH` commands without restarting the runner container or releasing its GPU.
- `QUIESCE` cancels startup, prompt preparation, transient work, downloads, artifact work, workspace mirrors, websocket bridges, and the managed child, then removes the session's ephemeral runtime home while retaining runner caches and logical registration.
- `LAUNCH` uses control-plane-generated command/runtime identities and fresh CPU `user` and `custom_nodes` archives. Retries are separate durable commands; RunnerAgent does not mint retry identities.
- Every child-originated event, log, download update, telemetry sample, artifact, and bridge report carries lifecycle generation, runtime epoch, and runtime instance. A bridge connection also has its own reconnect identity.
- A failed child remains unavailable while the runner and allocation stay active. Another Manager reboot retries; allocation Stop always wins.

## Runner Keys

- Create and revoke personal runner keys in the Offloadr web app under Settings, Personal Runner Keys. The key is shown once.
- Store the key only in the runner host's secret management. The control plane keeps only a hash of it.
- Runner identity is derived from `RUNNER_SECRET`, so restarting with the same key keeps the same runner identity.
- `RUNNER_ID` (a UUID) overrides the derived identity. It is meant for hosted runners that Offloadr provisions, not for personal installs.
- One key is one runner. If the same key is used from several hosts at once, the latest accepted heartbeat wins.
- Hosted runner keys are minted per allocation attempt, and hosted runners use the same registration and command path as personal runners after startup.

## Startup Validation Behavior

Runner startup fails fast with a clear aggregated error list when prerequisites are missing or invalid (for example: bad URLs, non-writable directories, missing `aria2c`, missing `COMFY_ENTRYPOINT`, missing `RUNNER_SEED_VENV_PATH`, missing `RUNNER_UV_BINARY`, or missing user-management binaries).

## Status File

The image sets `RUNNER_STATUS_FILE=/run/offloadr/status.json`. The agent rewrites that file whenever its active session changes and at least every 15 seconds:

```json
{
  "runner_id": "…",
  "instance_id": "…",
  "version": "0.1.0+<commit>",
  "supported_editor_templates": ["comfyui-latest", "comfyui-master"],
  "active_session_id": "",
  "updated_utc": "2026-10-06T12:00:00+00:00"
}
```

A supervisor can read it with `docker exec <container> cat /run/offloadr/status.json`. An empty `active_session_id` means restarting the container will not interrupt a session. The file never leaves the container; unset `RUNNER_STATUS_FILE` to disable it.

## Building the Images

All published variants are defined in `docker-bake.hcl` at the repository root:

```bash
docker buildx bake comfyui-latest --load
```

The targets are `comfyui-latest`, `comfyui-master`, `forge-neo-neo` and `forge-neo-latest`. Each layers the agent, aria2 and the model VFS shim on a public editor image; see `THIRD-PARTY-NOTICES.md` for where those come from.
