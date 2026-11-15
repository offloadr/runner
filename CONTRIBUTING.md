# Contributing

We are not looking for pull requests right now.

Bug reports are welcome as [issues](https://github.com/offloadr/runner/issues). If you use the desktop app, attach the file from Save diagnostics on its Logs or About page; it never holds your runner key. Never put runner keys, signed URLs or other secrets in an issue. Report security problems privately, as described in [SECURITY.md](SECURITY.md).

## Checks for maintainers

Read `AGENTS.md` first. Its command, session and identity rules apply to everyone, not only to coding agents.

```bash
dotnet build runner.slnx -warnaserror
```

```bash
dotnet format runner.slnx --verify-no-changes
```

```bash
dotnet test runner.slnx
```

The agent tests need Linux, root, `gcc` and `aria2c`. From Windows or macOS, run them in a container:

```bash
./build/test-linux.sh
```

The desktop app needs Windows and the `maui-windows` workload:

```bash
dotnet build runner.desktop.slnx -warnaserror
```

For each release of the runner's own code, someone with an NVIDIA GPU works through [docs/release-checklist.md](docs/release-checklist.md), because CI has no GPUs.

If you change `contracts/`, run `buf lint contracts` and read `docs/contracts-sync.md`, because the control plane has to accept the same change.
