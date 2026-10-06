# Third-party notices

The runner source code in this repository is licensed under the GNU Affero General Public License v3.0 only (see `LICENSE`). It uses or ships with the components below, each under its own license.

## .NET packages used by the agent

| Package | License |
| --- | --- |
| Aria2.NET | MIT |
| Newtonsoft.Json (via Aria2.NET) | MIT |
| Google.Protobuf | BSD-3-Clause |
| Grpc.Net.Client, Grpc.Net.Common, Grpc.Core.Api, Grpc.Tools (build only) | Apache-2.0 |
| Microsoft.Extensions.* and the .NET runtime (self-contained in the image) | MIT |

## Runner container images

Each image layers the agent, `aria2` and the model VFS shim (`liboffloadr_model_vfs.so`, built from `src/Offloadr.Runner.Linux/VfsShim/model_vfs.c`) on a public editor image. The complete build sources of every layer are public:

| Layer | Source | License |
| --- | --- | --- |
| Runner agent and VFS shim | this repository | AGPL-3.0-only |
| aria2 (distribution package) | https://github.com/aria2/aria2 | GPL-2.0-or-later with OpenSSL exception |
| ComfyUI images `comfyui-extensions` | https://github.com/radiatingreverberations/comfyui-docker (build), https://github.com/Comfy-Org/ComfyUI (application) | MIT (build files), GPL-3.0 (ComfyUI) |
| Forge Neo images `sd-webui-forge-neo` | https://github.com/radiatingreverberations/sd-webui-forge-neo-docker (build), https://github.com/Haoming02/sd-webui-forge-classic (application, `neo` branch) | MIT (build files), AGPL-3.0 (Forge Neo) |
| CUDA, ROCm and CPU Python base images | https://github.com/offloadr/base | MIT (build files); bundled frameworks such as PyTorch keep their own licenses |

The ComfyUI extension images also bundle custom nodes and Python packages; each keeps the license stated in its own repository or package metadata.
