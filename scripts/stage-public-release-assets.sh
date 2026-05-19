#!/usr/bin/env bash

# Copyright (c) Richard D. Kiernan.
# Licensed under the Business Source License 1.1. See LICENSE for details.


set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/lib/deploy-common.sh
source "$SCRIPT_DIR/lib/deploy-common.sh"

lms_require_command sha256sum

REPO_ROOT="$(lms_repo_root)"
PUBLIC_SITE_ROOT="$REPO_ROOT/src/LinuxMadeSane.PublicSite"
PUBLIC_SITE_APPSETTINGS="$PUBLIC_SITE_ROOT/appsettings.json"
APP_VERSION="$(lms_resolve_version)"
RUNTIMES="${RUNTIMES:-linux-x64 linux-arm64 linux-arm}"
PACKAGE_DIR="${PACKAGE_DIR:-$REPO_ROOT/artifacts/packages}"

read_public_site_setting() {
  local key="$1"
  local fallback="$2"
  local value

  value="$(
    sed -nE "s/^[[:space:]]*\"$key\"[[:space:]]*:[[:space:]]*\"([^\"]*)\".*/\1/p" "$PUBLIC_SITE_APPSETTINGS" |
      head -n 1
  )"

  printf '%s\n' "${value:-$fallback}"
}

resolve_public_site_directory() {
  local configured="$1"
  if [[ "$configured" = /* ]]; then
    printf '%s\n' "$configured"
    return
  fi

  (cd "$PUBLIC_SITE_ROOT" >/dev/null 2>&1 && mkdir -p "$configured" && cd "$configured" >/dev/null 2>&1 && pwd)
}

COMMUNITY_RELEASE_ROOT="${COMMUNITY_RELEASE_ROOT:-$(resolve_public_site_directory "$(read_public_site_setting CommunityReleaseDirectory "../../artifacts/public-site/community")")}"
PRO_RELEASE_ROOT="${PRO_RELEASE_ROOT:-$(resolve_public_site_directory "$(read_public_site_setting ProReleaseDirectory "../../artifacts/public-site/pro")")}"
COMMUNITY_ASSET_DIR="$COMMUNITY_RELEASE_ROOT/$APP_VERSION"
PRO_ASSET_DIR="$PRO_RELEASE_ROOT/$APP_VERSION"

lms_validate_version "$APP_VERSION"

stage_edition() {
  local edition="$1"
  local source_prefix="$2"
  local destination_dir="$3"
  local label="$4"

  lms_reset_dir "$destination_dir"

  local checksum_path="$destination_dir/SHA256SUMS"
  local manifest_path="$destination_dir/release-manifest-${APP_VERSION}.json"

  declare -a artifacts=()
  declare -a artifact_runtimes=()
  declare -a artifact_sha256=()
  declare -a artifact_sizes=()

  for runtime in $RUNTIMES; do
    local artifact_name="${source_prefix}-${APP_VERSION}-${runtime}.tar.gz"
    local artifact_path="$PACKAGE_DIR/$artifact_name"
    [[ -f "$artifact_path" ]] || lms_die "missing $label package: $artifact_path"

    cp "$artifact_path" "$destination_dir/$artifact_name"

    local sha
    local size
    sha="$(sha256sum "$destination_dir/$artifact_name" | awk '{print $1}')"
    size="$(wc -c < "$destination_dir/$artifact_name" | tr -d ' ')"

    artifacts+=("$destination_dir/$artifact_name")
    artifact_runtimes+=("$runtime")
    artifact_sha256+=("$sha")
    artifact_sizes+=("$size")
  done

  : > "$checksum_path"
  for index in "${!artifacts[@]}"; do
    printf '%s  %s\n' "${artifact_sha256[$index]}" "$(basename "${artifacts[$index]}")" >> "$checksum_path"
  done

  {
    printf '{\n'
    printf '  "version": "%s",\n' "$APP_VERSION"
    printf '  "builtUtc": "%s",\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    printf '  "edition": "%s",\n' "$edition"
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
  } > "$manifest_path"

  lms_log "$label release assets staged for public website"
  printf 'version: %s\nedition: %s\npath: %s\nchecksums: %s\nmanifest: %s\n' \
    "$APP_VERSION" "$edition" "$destination_dir" "$checksum_path" "$manifest_path"
}

stage_edition "community" "linux-made-sane-ce" "$COMMUNITY_ASSET_DIR" "Community"
stage_edition "pro" "linux-made-sane-pro" "$PRO_ASSET_DIR" "Pro"
