# Runner image variants. Local build of one variant:
#   docker buildx bake comfyui-latest --load
# CI sets SOURCE_REVISION and pins each editor image by digest (see .github/workflows); REGISTRY
# and BASE_REGISTRY default to ghcr.io/offloadr.

variable "REGISTRY" {
  default = "ghcr.io/offloadr"
}

variable "BASE_REGISTRY" {
  default = "ghcr.io/offloadr"
}

variable "SOURCE_REVISION" {
  default = ""
}

# RunnerProtocol.RUNNER_PROTOCOL_CURRENT of this source, and the protocol the bare channel tags
# currently serve. CI sets both; a local build leaves them empty and gets the bare tag only.
variable "PROTOCOL" {
  default = ""
}

variable "PROMOTED_PROTOCOL" {
  default = ""
}

# Each image gets <channel>-p<protocol>, which Offloadr's launch plan names, and <channel>-<commit>.
# The bare channel tag (what `docker run …:latest` pulls) moves only while this build's protocol is
# the promoted one, so a merged protocol change reaches no runner before Offloadr speaks it, while
# an upstream editor refresh on the promoted protocol still publishes to every tag at once.
function "tags_for" {
  params = [repository, channel]
  result = concat(
    PROTOCOL == "" || PROTOCOL == PROMOTED_PROTOCOL ? ["${REGISTRY}/${repository}:${channel}"] : [],
    PROTOCOL == "" ? [] : ["${REGISTRY}/${repository}:${channel}-p${PROTOCOL}"],
    SOURCE_REVISION == "" ? [] : ["${REGISTRY}/${repository}:${channel}-${substr(SOURCE_REVISION, 0, 12)}"],
  )
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
  tags     = tags_for("runner-comfyui", "latest")
}

target "comfyui-master" {
  inherits = ["_comfyui"]
  args = {
    FINAL_BASE_IMAGE                  = "${BASE_REGISTRY}/comfyui-extensions:master"
    RUNNER_SUPPORTED_EDITOR_TEMPLATES = "comfyui-master"
  }
  tags     = tags_for("runner-comfyui", "master")
}

target "forge-neo-neo" {
  inherits = ["_forge-neo"]
  args = {
    FINAL_BASE_IMAGE                  = "${BASE_REGISTRY}/sd-webui-forge-neo:neo"
    RUNNER_SUPPORTED_EDITOR_TEMPLATES = "forge-neo:neo"
  }
  tags     = tags_for("runner-forge-neo", "neo")
}

target "forge-neo-latest" {
  inherits = ["_forge-neo"]
  args = {
    FINAL_BASE_IMAGE                  = "${BASE_REGISTRY}/sd-webui-forge-neo:latest"
    RUNNER_SUPPORTED_EDITOR_TEMPLATES = "forge-neo:latest"
  }
  tags     = tags_for("runner-forge-neo", "latest")
}
