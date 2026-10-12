#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 4 ]]; then
  echo 'Usage: restore-image-channels.sh <comfyui-latest-digest> <comfyui-master-digest> <forge-neo-latest-digest> <forge-neo-neo-digest>' >&2
  exit 1
fi

refs=(
  ghcr.io/offloadr/runner-comfyui:latest
  ghcr.io/offloadr/runner-comfyui:master
  ghcr.io/offloadr/runner-forge-neo:latest
  ghcr.io/offloadr/runner-forge-neo:neo
)
digests=("$@")

# Validate every requested source before changing any channel.
for i in "${!refs[@]}"; do
  digest="${digests[$i]}"
  if [[ ! "$digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
    echo "Invalid digest for ${refs[$i]}" >&2
    exit 1
  fi
done
for i in "${!refs[@]}"; do
  source="${refs[$i]%:*}@${digests[$i]}"
  actual="$(skopeo inspect --format '{{.Digest}}' "docker://$source")"
  if [[ "$actual" != "${digests[$i]}" ]]; then
    echo "Source digest mismatch for $source: $actual" >&2
    exit 1
  fi
done

for i in "${!refs[@]}"; do
  source="${refs[$i]%:*}@${digests[$i]}"
  skopeo copy --all --preserve-digests "docker://$source" "docker://${refs[$i]}"
  actual="$(skopeo inspect --format '{{.Digest}}' "docker://${refs[$i]}")"
  if [[ "$actual" != "${digests[$i]}" ]]; then
    echo "Restored channel digest mismatch for ${refs[$i]}: $actual" >&2
    exit 1
  fi
  echo "Restored ${refs[$i]} to $actual"
done
