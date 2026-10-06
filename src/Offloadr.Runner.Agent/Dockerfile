# Runner image: the RunnerAgent, aria2 and the model VFS shim layered on an editor image.
# Build all published variants with `docker buildx bake` (see docker-bake.hcl); the
# defaults below produce the ComfyUI `latest` variant. Build context: repository root.

ARG FINAL_BASE_IMAGE=ghcr.io/radiatingreverberations/comfyui-extensions:latest
ARG EDITOR_RUNTIME=comfyui
ARG RUNNER_SUPPORTED_EDITOR_TEMPLATES=comfyui-latest,comfyui-master
ARG COMFY_ENTRYPOINT=/comfyui/entrypoint.base.sh
ARG COMFY_WORKDIR=/comfyui
ARG COMFY_PORT=8188
ARG EDITOR_READY_PATH=/system_stats
ARG RUNNER_VFS_ROOTS_VALUE=/comfyui/models
ARG RUNNER_PERSISTENT_RANGE_HYDRATION_VALUE=1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_XMLDOC_MODE=skip

RUN apt-get update && apt-get install -y --no-install-recommends \
      gcc libc6-dev \
  && rm -rf /var/lib/apt/lists/*

ARG BUILD_CONFIGURATION=Release
# Commit the image is built from; recorded in the agent's reported version.
ARG SOURCE_REVISION=

WORKDIR /src

# Restore layer
COPY ["global.json", "Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["src/Offloadr.Runner.Contracts/Offloadr.Runner.Contracts.csproj", "src/Offloadr.Runner.Contracts/"]
COPY ["src/Offloadr.Runner.Core/Offloadr.Runner.Core.csproj", "src/Offloadr.Runner.Core/"]
COPY ["src/Offloadr.Runner.Linux/Offloadr.Runner.Linux.csproj", "src/Offloadr.Runner.Linux/"]
COPY ["src/Offloadr.Runner.Agent/Offloadr.Runner.Agent.csproj", "src/Offloadr.Runner.Agent/"]
RUN dotnet restore "src/Offloadr.Runner.Agent/Offloadr.Runner.Agent.csproj" -r linux-x64

COPY contracts/ contracts/
COPY src/ src/

RUN dotnet publish src/Offloadr.Runner.Agent/Offloadr.Runner.Agent.csproj \
    -c $BUILD_CONFIGURATION -r linux-x64 --self-contained true \
    -o /app/publish \
    -p:UseAppHost=true \
    -p:SourceRevisionId=$SOURCE_REVISION \
    --no-restore

# Build the LD_PRELOAD shim for on-demand model fetching.
RUN mkdir -p /app/publish/vfs \
    && gcc -fPIC -shared -O2 -o /app/publish/vfs/liboffloadr_model_vfs.so \
       src/Offloadr.Runner.Linux/VfsShim/model_vfs.c -ldl -pthread

FROM ${FINAL_BASE_IMAGE} AS final
ARG EDITOR_RUNTIME
ARG RUNNER_SUPPORTED_EDITOR_TEMPLATES
ARG COMFY_ENTRYPOINT
ARG COMFY_WORKDIR
ARG COMFY_PORT
ARG EDITOR_READY_PATH
ARG RUNNER_VFS_ROOTS_VALUE
ARG RUNNER_PERSISTENT_RANGE_HYDRATION_VALUE
ARG SOURCE_REVISION=
# Release version of the image; empty for development builds. Replaces the base image's label.
ARG IMAGE_VERSION=

LABEL org.opencontainers.image.source="https://github.com/offloadr/runner" \
      org.opencontainers.image.revision="${SOURCE_REVISION}" \
      org.opencontainers.image.version="${IMAGE_VERSION}" \
      org.opencontainers.image.licenses="AGPL-3.0-only" \
      org.opencontainers.image.title="Offloadr runner (${EDITOR_RUNTIME})"

WORKDIR /app

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

RUN apt-get update && apt-get install -y --no-install-recommends aria2 \
  && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /opt/offloadr/lib /opt/offloadr/seed/custom_nodes /opt/offloadr/seed/input \
  && if [ -d /comfyui/custom_nodes ]; then cp -a /comfyui/custom_nodes/. /opt/offloadr/seed/custom_nodes/; fi \
  && if [ -d /comfyui/input ]; then cp -a /comfyui/input/. /opt/offloadr/seed/input/; fi \
  && if [ -d /opt/venv ]; then chown -R root:root /opt/venv && chmod -R go-w,a+rX /opt/venv; fi
COPY --from=build /app/publish ./
COPY --from=build /app/publish/vfs /opt/offloadr/lib

ENV RUNNER_VFS_LIB=/opt/offloadr/lib/liboffloadr_model_vfs.so \
    RUNNER_MODEL_FETCH_SOCKET=/tmp/runner-agent/model-fetch.sock \
    RUNNER_STATUS_FILE=/run/offloadr/status.json \
    EDITOR_RUNTIME=${EDITOR_RUNTIME} \
    EDITOR_READY_PATH=${EDITOR_READY_PATH} \
    RUNNER_SUPPORTED_EDITOR_TEMPLATES=${RUNNER_SUPPORTED_EDITOR_TEMPLATES} \
    COMFY_ENTRYPOINT=${COMFY_ENTRYPOINT} \
    COMFY_WORKDIR=${COMFY_WORKDIR} \
    COMFY_PORT=${COMFY_PORT} \
    RUNNER_VFS_ROOTS=${RUNNER_VFS_ROOTS_VALUE} \
    RUNNER_PERSISTENT_RANGE_HYDRATION=${RUNNER_PERSISTENT_RANGE_HYDRATION_VALUE} \
    RUNNER_VFS_EXTS=.safetensors,.pt,.bin,.ckpt \
    RUNNER_VFS_LOG=0

ENTRYPOINT ["./RunnerAgent"]
