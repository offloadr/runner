# Runner image variants. Local build of one variant:
#   docker buildx bake comfyui-latest --load
# CI sets REGISTRY, SOURCE_REVISION and PUSH_TAGS (see .github/workflows).

variable "REGISTRY" {
  default = "ghcr.io/offloadr"
}

variable "BASE_REGISTRY" {
  default = "ghcr.io/radiatingreverberations"
}

variable "SOURCE_REVISION" {
  default = ""
}

group "default" {
  targets = ["comfyui-latest", "comfyui-master", "forge-neo-neo", "forge-neo-latest"]
}

target "_runner" {
  context    = "."
  dockerfile = "src/Offloadr.Runner.Agent/Dockerfile"
  platforms  = ["linux/amd64"]
  args = {
    SOURCE_REVISION = SOURCE_REVISION
  }
}

target "_comfyui" {
  inherits = ["_runner"]
  args = {
    EDITOR_RUNTIME                     = "comfyui"
    COMFY_ENTRYPOINT                   = "/comfyui/entrypoint.base.sh"
    COMFY_WORKDIR                      = "/comfyui"
    COMFY_PORT                         = "8188"
    EDITOR_READY_PATH                  = "/system_stats"
    RUNNER_VFS_ROOTS_VALUE             = "/comfyui/models"
    RUNNER_PERSISTENT_RANGE_HYDRATION_VALUE = "1"
  }
}

target "_forge-neo" {
  inherits = ["_runner"]
  args = {
    EDITOR_RUNTIME                     = "forge-neo"
    COMFY_ENTRYPOINT                   = "/sd-webui-forge-neo/entrypoint.sh"
    COMFY_WORKDIR                      = "/sd-webui-forge-neo"
    COMFY_PORT                         = "7860"
    EDITOR_READY_PATH                  = "/"
    RUNNER_VFS_ROOTS_VALUE             = ""
    RUNNER_PERSISTENT_RANGE_HYDRATION_VALUE = "0"
  }
}

target "comfyui-latest" {
  inherits = ["_comfyui"]
  # Each image advertises only the editor template it is built from.
  args = {
    FINAL_BASE_IMAGE                  = "${BASE_REGISTRY}/comfyui-extensions:latest"
    RUNNER_SUPPORTED_EDITOR_TEMPLATES = "comfyui-latest"
  }
  tags     = ["${REGISTRY}/runner-comfyui:latest"]
}

target "comfyui-master" {
  inherits = ["_comfyui"]
  args = {
    FINAL_BASE_IMAGE                  = "${BASE_REGISTRY}/comfyui-extensions:master"
    RUNNER_SUPPORTED_EDITOR_TEMPLATES = "comfyui-master"
  }
  tags     = ["${REGISTRY}/runner-comfyui:master"]
}

target "forge-neo-neo" {
  inherits = ["_forge-neo"]
  args = {
    FINAL_BASE_IMAGE                  = "${BASE_REGISTRY}/sd-webui-forge-neo:neo"
    RUNNER_SUPPORTED_EDITOR_TEMPLATES = "forge-neo:neo"
  }
  tags     = ["${REGISTRY}/runner-forge-neo:neo"]
}

target "forge-neo-latest" {
  inherits = ["_forge-neo"]
  args = {
    FINAL_BASE_IMAGE                  = "${BASE_REGISTRY}/sd-webui-forge-neo:latest"
    RUNNER_SUPPORTED_EDITOR_TEMPLATES = "forge-neo:latest"
  }
  tags     = ["${REGISTRY}/runner-forge-neo:latest"]
}
