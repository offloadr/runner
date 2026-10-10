# Offloadr runner

The GPU runner for [Offloadr](https://offloadr.studio). It connects to the Offloadr control plane, runs ComfyUI or Forge Neo sessions on your GPU, and streams the work back to the editor in your browser. It only makes outbound connections.

The runner images Offloadr uses on its own hosted GPUs are built from this repository too.

## Run it

You need Docker with NVIDIA GPU support and a personal runner key from Settings in the Offloadr web app.

```bash
docker run --rm -it --pull always --gpus all -e RUNNER_SECRET=<your-runner-key> ghcr.io/offloadr/runner-comfyui:latest
```

| Image | Editor |
| --- | --- |
| `ghcr.io/offloadr/runner-comfyui:latest` | ComfyUI, current release |
| `ghcr.io/offloadr/runner-comfyui:master` | ComfyUI, master branch |
| `ghcr.io/offloadr/runner-forge-neo:latest` | Forge Neo, current release |
| `ghcr.io/offloadr/runner-forge-neo:neo` | Forge Neo, neo branch |

[docs/hosting.md](docs/hosting.md) covers local model folders, environment variables and troubleshooting.

## Build and test

```bash
dotnet build runner.slnx
```

```bash
docker buildx bake comfyui-latest --load
```

The full test suite needs Linux, root, `gcc` and `aria2c`. From any machine with Docker:

```bash
./build/test-linux.sh
```

[docs/architecture.md](docs/architecture.md) explains how the runner works and how the code is organised.

## License

[AGPL-3.0-only](LICENSE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
