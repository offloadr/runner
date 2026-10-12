#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 1 ]]; then
  echo 'Usage: record-image-channels.sh <output-json>' >&2
  exit 1
fi

refs=(
  ghcr.io/offloadr/runner-comfyui:latest
  ghcr.io/offloadr/runner-comfyui:master
  ghcr.io/offloadr/runner-forge-neo:latest
  ghcr.io/offloadr/runner-forge-neo:neo
)
snapshot_dir="$(mktemp -d)"
trap 'rm -rf "$snapshot_dir"' EXIT
digests=()
for ref in "${refs[@]}"; do
  # Hash the raw manifest so an image index keeps its digest, not a child image's.
  skopeo inspect --raw "docker://$ref" > "$snapshot_dir/manifest.json"
  digest="$(skopeo manifest-digest "$snapshot_dir/manifest.json")"
  if [[ ! "$digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
    echo "Invalid registry digest for $ref" >&2
    exit 1
  fi
  digests+=("$digest")
  printf '{"%s":"%s"}\n' "$ref" "$digest" >> "$snapshot_dir/channels.jsonl"
done

# Publish the record only when every channel was read successfully. The workflow
# requires this job and its artifact upload before any build may overwrite tags.
jq -s add "$snapshot_dir/channels.jsonl" > "$1"
printf '### Previous runner channel digests\n\n| Channel | Digest before publication |\n| --- | --- |\n'
for i in "${!refs[@]}"; do
  printf '| `%s` | `%s` |\n' "${refs[$i]}" "${digests[$i]}"
done
