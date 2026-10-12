#!/usr/bin/env bash
# Proves that pushed runner images are exactly what the build produced. A green build step alone
# is not proof of a release: every tag must resolve in the registry to the digest the build
# reported, and the image must carry this commit's revision plus the source and license labels.
#
# Usage: verify-published-image.sh <expected-digest> <expected-revision> <image-ref>...
# Needs skopeo. Extra skopeo flags, such as --tls-verify=false for a local registry, go in SKOPEO_FLAGS.
set -euo pipefail

if (( $# < 3 )); then
  echo "usage: $0 <expected-digest> <expected-revision> <image-ref>..." >&2
  exit 2
fi

expected_digest="$1"
expected_revision="$2"
shift 2
read -r -a skopeo_flags <<< "${SKOPEO_FLAGS:-}"
expected_source="https://github.com/offloadr/runner"
expected_license="AGPL-3.0-only"
workdir="$(mktemp -d)"
trap 'rm -rf "$workdir"' EXIT
failures=0

fail() {
  echo "FAIL $1" >&2
  failures=$((failures + 1))
}

# Registries can briefly lag behind a push; retry reads before calling a tag missing.
with_retries() {
  local attempt
  for attempt in 1 2 3 4 5; do
    if "$@"; then
      return 0
    fi
    sleep $((attempt * 3))
  done
  return 1
}

label() {
  local ref="$1" name="$2"
  skopeo inspect "${skopeo_flags[@]}" --override-os linux --override-arch amd64 \
    --format "{{ index .Labels \"$name\" }}" "docker://$ref"
}

if [[ ! "$expected_digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
  fail "the build reported no image digest ('$expected_digest'); nothing proves an image was pushed"
fi

for ref in "$@"; do
  manifest="$workdir/manifest.json"
  if ! with_retries skopeo inspect "${skopeo_flags[@]}" --raw "docker://$ref" > "$manifest"; then
    fail "$ref: not found in the registry"
    continue
  fi

  digest="$(skopeo manifest-digest "$manifest")"
  if [[ "$digest" != "$expected_digest" ]]; then
    fail "$ref: resolves to $digest, but the build pushed $expected_digest"
  else
    echo "ok   $ref -> $digest"
  fi

  revision="$(label "$ref" org.opencontainers.image.revision)"
  [[ "$revision" == "$expected_revision" ]] \
    || fail "$ref: revision label is '$revision', expected '$expected_revision'"
  source_url="$(label "$ref" org.opencontainers.image.source)"
  [[ "$source_url" == "$expected_source" ]] \
    || fail "$ref: source label is '$source_url', expected '$expected_source'"
  license="$(label "$ref" org.opencontainers.image.licenses)"
  [[ "$license" == "$expected_license" ]] \
    || fail "$ref: license label is '$license', expected '$expected_license'"
done

if (( failures > 0 )); then
  echo "$failures check(s) failed; this publish does not count as a release." >&2
  exit 1
fi

echo "All $# tag(s) verified for revision $expected_revision."
