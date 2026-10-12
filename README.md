# Offloadr runner

The GPU runner for [Offloadr](https://offloadr.studio). It connects to the Offloadr control plane, runs ComfyUI or Forge Neo sessions on your GPU, and streams the work back to the editor in your browser. It only makes outbound connections.

The runner images Offloadr uses on its own hosted GPUs are built from this repository too.

## Run it

You need an NVIDIA GPU, Docker with GPU support, and a personal runner key from Settings in the Offloadr web app.

### Desktop app (Windows)

Download `OffloadrRunner-<version>-windows-x64.zip` from the [releases](https://github.com/offloadr/runner/releases), extract it and run `OffloadrRunner.exe`. It needs Docker Desktop with the WSL 2 backend and the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/), which Windows 11 includes; on Windows 10, install it if the app's window stays blank. Paste your runner key under Settings and press Start runner. The app picks the runner image for your editor, downloads it and keeps it running. Closing the window leaves it in the notification area, where a coloured dot on the icon shows the runner's state; quit from the tray icon to stop the runner. Settings can make the app start in the notification area when you sign in to Windows, and the app tells you when a newer release is out.

### Docker

On Linux, or if you prefer the command line:

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

The desktop app needs Windows and the `maui-windows` workload:

```bash
dotnet build runner.desktop.slnx
```

The full test suite needs Linux, root, `gcc` and `aria2c`. From any machine with Docker:

```bash
./build/test-linux.sh
```

[docs/architecture.md](docs/architecture.md) explains how the runner works and how the code is organised. [CONTRIBUTING.md](CONTRIBUTING.md) lists the checks to run before a pull request.

## License

[AGPL-3.0-only](LICENSE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
