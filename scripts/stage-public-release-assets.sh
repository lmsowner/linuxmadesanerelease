#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/lib/deploy-common.sh
source "$SCRIPT_DIR/lib/deploy-common.sh"

lms_require_command sha256sum

REPO_ROOT="$(lms_repo_root)"
APP_VERSION="$(lms_resolve_version)"
RUNTIMES="${RUNTIMES:-linux-x64 linux-arm64 linux-arm}"
PACKAGE_DIR="${PACKAGE_DIR:-$REPO_ROOT/artifacts/packages}"
PUBLIC_ASSET_DIR="${PUBLIC_ASSET_DIR:-$REPO_ROOT/artifacts/public-release-assets/$APP_VERSION}"
CHECKSUM_PATH="$PUBLIC_ASSET_DIR/SHA256SUMS"
MANIFEST_PATH="$PUBLIC_ASSET_DIR/release-manifest-${APP_VERSION}.json"

lms_validate_version "$APP_VERSION"
lms_reset_dir "$PUBLIC_ASSET_DIR"

declare -a artifacts=()
declare -a artifact_runtimes=()
declare -a artifact_sha256=()
declare -a artifact_sizes=()

for runtime in $RUNTIMES; do
  artifact_name="linux-made-sane-ce-${APP_VERSION}-${runtime}.tar.gz"
  artifact_path="$PACKAGE_DIR/$artifact_name"
  [[ -f "$artifact_path" ]] || lms_die "missing CE package: $artifact_path"

  cp "$artifact_path" "$PUBLIC_ASSET_DIR/$artifact_name"

  sha="$(sha256sum "$PUBLIC_ASSET_DIR/$artifact_name" | awk '{print $1}')"
  size="$(wc -c < "$PUBLIC_ASSET_DIR/$artifact_name" | tr -d ' ')"

  artifacts+=("$PUBLIC_ASSET_DIR/$artifact_name")
  artifact_runtimes+=("$runtime")
  artifact_sha256+=("$sha")
  artifact_sizes+=("$size")
done

: > "$CHECKSUM_PATH"
for index in "${!artifacts[@]}"; do
  printf '%s  %s\n' "${artifact_sha256[$index]}" "$(basename "${artifacts[$index]}")" >> "$CHECKSUM_PATH"
done

{
  printf '{\n'
  printf '  "version": "%s",\n' "$APP_VERSION"
  printf '  "builtUtc": "%s",\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf '  "edition": "ce",\n'
  printf '  "artifacts": [\n'
  for index in "${!artifacts[@]}"; do
    [[ "$index" -eq 0 ]] || printf ',\n'
    printf '    {\n'
    printf '      "runtime": "%s",\n' "${artifact_runtimes[$index]}"
    printf '      "file": "%s",\n' "$(basename "${artifacts[$index]}")"
    printf '      "sha256": "%s",\n' "${artifact_sha256[$index]}"
    printf '      "sizeBytes": %s\n' "${artifact_sizes[$index]}"
    printf '    }'
  done
  printf '\n  ]\n'
  printf '}\n'
} > "$MANIFEST_PATH"

lms_log "Public CE release assets staged"
printf 'version: %s\npath: %s\nchecksums: %s\nmanifest: %s\n' "$APP_VERSION" "$PUBLIC_ASSET_DIR" "$CHECKSUM_PATH" "$MANIFEST_PATH"
