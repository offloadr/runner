#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 4 && $# != 5 ]]; then
  echo 'Usage: restore-image-channels.sh <comfyui-latest-digest> <comfyui-master-digest> <forge-neo-latest-digest> <forge-neo-neo-digest> [promoted-protocol]' >&2
  exit 1
fi

refs=(
  ghcr.io/offloadr/runner-comfyui:latest
  ghcr.io/offloadr/runner-comfyui:master
  ghcr.io/offloadr/runner-forge-neo:latest
  ghcr.io/offloadr/runner-forge-neo:neo
)
digests=("${@:1:4}")
# The promoted protocol's -p<N> tags move with the bare channels, so a rollback restores them too.
protocol="${5:-}"
if [[ -n "$protocol" && ! "$protocol" =~ ^[0-9]+$ ]]; then
  echo "Invalid protocol: $protocol" >&2
  exit 1
fi

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
  targets=("${refs[$i]}")
  if [[ -n "$protocol" ]]; then
    targets+=("${refs[$i]}-p$protocol")
  fi
  for target in "${targets[@]}"; do
    skopeo copy --all --preserve-digests "docker://$source" "docker://$target"
    actual="$(skopeo inspect --format '{{.Digest}}' "docker://$target")"
    if [[ "$actual" != "${digests[$i]}" ]]; then
      echo "Restored channel digest mismatch for $target: $actual" >&2
      exit 1
    fi
    echo "Restored $target to $actual"
  done
done
