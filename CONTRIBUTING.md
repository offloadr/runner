# Contributing

Thanks for helping with the Offloadr runner. Issues and pull requests are welcome.

## Licensing

The runner is licensed under AGPL-3.0-only. By submitting a contribution you agree that it is licensed under the same terms. There is no contributor license agreement, and per-file license headers are not required.

## Before you open a pull request

- Read `AGENTS.md`. Its command, session and identity rules apply to everyone, not only to coding agents.
- Keep the change focused, and describe what it changes and how you tested it.
- Never put runner keys, signed URLs or other secrets in code, tests, logs or issues.

## Checks

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
