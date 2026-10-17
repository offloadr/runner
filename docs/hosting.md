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

`latest` matches the stable ComfyUI editor channel and `master` the ComfyUI master channel. Forge Neo uses `ghcr.io/offloadr/runner-forge-neo:neo` for the default Forge route and `:latest` for the latest Forge template. Hosted runners use the same images. Each image advertises only the editor template it is built from, so a runner is offered only for workspaces on that exact editor build: a `runner-comfyui:latest` runner serves `comfyui-latest` workspaces, not `comfyui-master` or Forge ones.

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

## Selecting one physical GPU

Set `OFFLOADR_GPU_UUID` to a complete UUID reported by
`nvidia-smi --query-gpu=uuid,name --format=csv,noheader`. For example, add
`-e OFFLOADR_GPU_UUID=GPU-xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` to the Docker
command, replacing the placeholder with the selected device's UUID.

The runner queries resources and telemetry for that UUID, applies power commands
to that device, and sets the child editor's `CUDA_VISIBLE_DEVICES` to the same
UUID. If you also supply `CUDA_VISIBLE_DEVICES`, it must contain that exact UUID;
conflicting or ordinal masks are rejected rather than silently overridden.
Changing the selection requires restarting the runner container. The selector
and agent credentials are not inherited by the editor child.

This matters on hosts where Docker or CUDA visibility does not also restrict
NVML enumeration, including Windows/WSL configurations. CUDA visibility alone
does not bind `nvidia-smi` discovery, telemetry or power commands. An explicit
selection prevents the runner from advertising other GPUs or applying its power
command to device zero. A missing selected device reports unavailable GPU data;
it never falls back to another device.

GPU selection is not a security isolation boundary. The host runtime determines
which devices the container can access, and untrusted child code can change its
own CUDA environment. Continue to use appropriate host/container isolation.

## Key Environment Variables

- Required for startup
  - `RUNNER_SECRET` (required; pre-provisioned secret tied to owner)
- Offloadr API
  - `OFFLOADR_API_GRPC` (optional; defaults to `https://offloadr.studio`; use a public HTTPS gRPC endpoint)
- Optional physical GPU selection
  - `OFFLOADR_GPU_UUID` (complete `GPU-...` UUID; binds resources, telemetry, power and child CUDA visibility)
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
- Optional sealed storage (see [Sealed Storage](#sealed-storage))
  - `RUNNER_SEALED_STORAGE` (`off`, `auto` or `required`; default `auto`. Any other value fails startup)
  - `RUNNER_SEALED_ROOT` (memory-backed directory for sealed session data, default `/dev/shm/offloadr`)
  - `RUNNER_SEALED_MIN_GB` (smallest usable budget in GiB, default `4`)
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

- aria2 runs as the unprivileged `ARIA2_USER` (default `offloadr-aria2`, uid/gid 62000 in the published images), never as root. Before each transfer the agent gives that account's group write access to the directory the download lands in and hands it the placeholder and control file. The state directory (`ARIA2_STATE_DIR`, default `/var/lib/aria2`) is `root:offloadr-aria2` mode `1770`; the RPC secret file in it is readable only by the aria2 account. If you mount it or the model directories from the host, the agent adjusts their group and mode at startup and per transfer. `ARIA2_REQUIRE_USER=1` (set in the images) makes a missing account a startup error; without it the agent falls back to running aria2 as its own user and logs a warning.
- Downloads into a session home (Forge Neo models) are written to `ARIA2_STATE_DIR/staging` and moved into the home by the agent when complete, so a session cannot redirect aria2's writes. Keep the state directory on the same filesystem as `SESSION_ROOT` to avoid a copy.
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
  of acknowledgement retries.
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

## Sealed Storage

Sealed storage keeps a session's user data in memory-backed storage instead of on the host's disk. It reduces what is left behind on a rented machine; it does not hide a running session from whoever controls the host, who can still read its memory.

- `RUNNER_SEALED_STORAGE=required` seals every session. Startup fails unless `RUNNER_SEALED_ROOT` (default `/dev/shm/offloadr`) is on a tmpfs or ramfs, owned by root, writable only by root, and leaves at least `RUNNER_SEALED_MIN_GB` (default 4 GiB) in both free space and container memory headroom. `auto`, the default, seals only sessions whose start asks for it, which the control plane does not do yet. `off` never seals.
- A sealed session's input, output, temp, ComfyUI `user`, `TMPDIR`, `HOME` and Forge Neo data directory (settings, styles and `params.txt`, which holds the last prompt) live under `<root>/<session>`, which only root can change. The virtualenv, models, custom nodes and the caches of compiled code (`XDG_CACHE_HOME`, TorchInductor, Triton, CUDA) stay on disk under `SESSION_ROOT`: `/dev/shm` is mounted `noexec`, so nothing can be executed or loaded as a library from the sealed root. Building and running a binary in `TMPDIR` fails for the same reason; ComfyUI-Manager's dependency sync gets a disk-backed `TMPDIR` of its own.
- The workspace seed archive is staged on the sealed root, editor and dependency-sync output stays off the container log (it is still relayed to the control plane), and processes that load the VFS shim, as well as the agent, are non-dumpable. A static binary that does not load the shim stays dumpable.
- In required mode the agent also removes write access for others from world-writable directories on disk, such as `/tmp`, `/var/tmp` and `/run/lock`, and from everything world-writable inside them, including on disks mounted below them, so a library that ignores `TMPDIR` fails instead of writing there. Startup fails when such a place stays writable, for example on a mount that refuses `chmod`; read-only mounts are fine.
- The agent's own log lines for a sealed session name no artifact or input files, and exceptions are logged by type only, since their messages carry paths. `RUNNER_VFS_LOG=1`, which logs every path the editor opens, is refused in required mode and ignored for sealed sessions in auto mode. A sealed session also needs the VFS shim to load: the agent checks that a process preloading it becomes non-dumpable, not only that the file exists.
- A start that must be sealed is refused before anything is provisioned when the root cannot take it. When the budget falls below half the minimum, uploaded outputs and previews that have not changed for two minutes are replaced by empty placeholders and downloaded again when opened. Nothing ever falls back to disk: once nothing is left to evict, writes fail with "no space left on device".
- The sealed directory is removed when the session stops, fails to start or its editor exits, and at agent startup. A stop first gives pending uploads a few seconds; artifacts that did not upload by then are lost with the session's data, and the agent logs how many.
- What it does not cover: custom nodes can still write next to their own code on disk, models stay on disk, and swap can write memory, tmpfs included, to the host's disk unless the container's swap is off (`memory.swap.max` is `0`) or the tmpfs is mounted `noswap`. The status file reports what the runner can see.
- tmpfs pages count against the container's memory limit, so the sealed budget and the editor's own memory compete.
- Transfers are sealed with the envelope format in `docs/sealed-envelope.md`. Every session runtime has a reader key that exists only in the agent's memory and is released after the runtime's data is removed; every read sends its public key, and a response sealed to it is decrypted into a temporary file that is published only once the whole payload has verified. When the start names an output recipient, every upload is sealed to it with a fresh data key. A start that requires the sealed-session profile is refused unless the memory workspace and a valid recipient are both available, and inside it a read that arrives without an envelope fails rather than being accepted in plaintext. The control plane holds the recipient's private key, so it can read uploads: this is not end-to-end encryption. Until the control plane implements its side, uploads and reads stay plaintext over TLS.

Observed `/dev/shm` on GPU rental providers (one RTX 3090 or 4090 pod each, October 2026):

| Provider | `/dev/shm` by default | Override | Container memory limit | Swap |
| --- | --- | --- | --- | --- |
| Vast.ai | 10 GiB, sized by the provider | `--shm-size` in the template's Docker options | 49 GiB | Host swap partition, but `memory.swap.max` is `0` |
| RunPod | 30 GiB | None offered | 62 GiB | None |
| QuickPod | 64 MiB (Docker's default) | `--shm-size=16g` in Docker Options gives 16 GiB | Not visible (cgroup files absent); host has 32 GiB | 2–4 GiB host swap file, not limited |

For a personal runner, add `--shm-size` to `docker run` together with `-e RUNNER_SEALED_STORAGE=required`.

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
  "supported_editor_templates": ["comfyui-latest"],
  "active_session_id": "",
  "updated_utc": "2026-10-06T12:00:00+00:00",
  "sealed_storage": {
    "mode": "required",
    "available": true,
    "reason": null,
    "root": "/dev/shm/offloadr",
    "capacity_bytes": 10737418240,
    "free_bytes": 10737418240,
    "memory_headroom_bytes": 52177788928,
    "budget_bytes": 10737418240,
    "swap": { "devices_bytes": 64000000000, "cgroup_limit": "0", "tmpfs_noswap": false }
  }
}
```

`sealed_storage.budget_bytes` is the smaller of the sealed root's free space and the container's memory headroom. A supervisor can read it with `docker exec <container> cat /run/offloadr/status.json`. An empty `active_session_id` means restarting the container will not interrupt a session. The file never leaves the container; unset `RUNNER_STATUS_FILE` to disable it.

## Building the Images

All published variants are defined in `docker-bake.hcl` at the repository root:

```bash
docker buildx bake comfyui-latest --load
```

The targets are `comfyui-latest`, `comfyui-master`, `forge-neo-neo` and `forge-neo-latest`. Setting `SOURCE_REVISION` to a commit adds a `<channel>-<commit>` tag next to the channel tag. The `Publish runner images` workflow builds all four and pushes them to `ghcr.io/offloadr`. A publish counts as a release only after `build/verify-published-image.sh` confirms, from the registry itself, that every tag resolves to the digest the build pushed and that the image carries the commit's revision, source and license labels. Each layers the agent, aria2 and the model VFS shim on a public editor image; see `THIRD-PARTY-NOTICES.md` for where those come from.
