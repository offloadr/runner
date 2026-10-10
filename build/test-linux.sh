#!/usr/bin/env bash
# Runs the full runner test suite, including the Linux-native tests, in a disposable
# .NET SDK container as root (session isolation tests need useradd). Works from
# Windows (Docker Desktop) and Linux. Extra arguments are passed to `dotnet test`.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
# Docker Desktop on Windows needs a Windows path for the bind mount (Git Bash: pwd -W).
repo="$(pwd -W 2>/dev/null || pwd)"
filter_args=("$@")
MSYS_NO_PATHCONV=1 docker run --rm \
  -v "${repo}:/repo:ro" \
  -e RUNNER_NATIVE_TESTS_REQUIRED=1 \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -c '
set -euo pipefail
apt-get update -qq >/dev/null
apt-get install -y -qq --no-install-recommends gcc libc6-dev aria2 >/dev/null
mkdir /work
tar -C /repo --exclude=./.git --exclude="*/bin" --exclude="*/obj" -cf - . | tar -xf - -C /work
cd /work
dotnet build runner.slnx -warnaserror -nologo -v q
dotnet test tests/Offloadr.Runner.Tests --no-build "$@"
' bash "${filter_args[@]}"
