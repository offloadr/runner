# Changelog

What changed in each release of the desktop app and the runner images. Images are published continuously under their channel tags (see [docs/image-publishing.md](docs/image-publishing.md)); this file follows the desktop app's releases, and names image changes that matter to people who run them.

## Unreleased

The first public release.

### Desktop app (Windows)

- An installer (`OffloadrRunner-Setup.exe`) that needs no administrator rights, and updates that download in the background and install on a restart, leaving the runner running.
- First-run Setup: checks Docker and the GPU with links to fix what is missing, takes the runner key, asks whether the runner may download models, and starts the runner with download progress.
- Status shows problems as cards that say what is wrong and what to do, and checks Docker, the GPU and the GPU inside containers.
- Models and storage: the models folder and its size, and the runner images Docker keeps, with removal of those no runner uses. The image a newer one replaced is removed automatically.
- Logs follow new lines; Save diagnostics writes a redacted report for support, never holding the runner key.
- Keeps the computer awake while a session runs, checks the runner again after sleep, and stops it within seconds when Windows shuts down.
- Settings asks before leaving with unsaved changes. About shows the version, update status, licences and source code.
- Extra arguments for ComfyUI and for Forge Neo under Settings, Advanced.
- On a computer with more than one NVIDIA GPU, Settings, Advanced chooses the one the runner uses.

### Runner images

- Model downloads are off unless asked for (`RUNNER_MODEL_DOWNLOADS=on`); without them a runner uses only its local models folder.
- With a writable second mount of the models folder (`RUNNER_MODEL_LIBRARY_DIR`), downloaded models are saved there instead of inside the container.
- Forge Neo keeps its models in a cache shared by its sessions.
- Extra editor arguments: after the image name, or in `RUNNER_EDITOR_ARGS_COMFYUI` and `RUNNER_EDITOR_ARGS_FORGE_NEO`. Flags the runner sets or relies on are refused at startup.
- Image signatures (cosign) and build provenance attestations.
- A runner Offloadr no longer speaks the protocol of stops and says so instead of retrying; the desktop app then downloads the image Offloadr names. Images also carry a `<channel>-p<protocol>` tag, and the bare channel tags move to a new protocol only when Offloadr has switched to it.
