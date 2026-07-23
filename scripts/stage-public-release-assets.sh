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
LIVE_PUBLIC_SITE_RELEASE_ROOT="${LIVE_PUBLIC_SITE_RELEASE_ROOT:-/var/lib/linuxmadesane/public-site/releases}"
APP_VERSION="$(lms_resolve_version)"
RUNTIMES="${RUNTIMES:-linux-x64 linux-arm64 linux-arm}"
PACKAGE_DIR="${PACKAGE_DIR:-$REPO_ROOT/artifacts/packages}"
STAGE_EDITIONS="${STAGE_EDITIONS:-${EDITIONS:-community pro}}"
KEEP_ONLY_LATEST_PUBLIC_SITE_RELEASE="${KEEP_ONLY_LATEST_PUBLIC_SITE_RELEASE:-true}"

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

if [[ -z "${COMMUNITY_RELEASE_ROOT:-}" && -d "$LIVE_PUBLIC_SITE_RELEASE_ROOT" ]]; then
  COMMUNITY_RELEASE_ROOT="$LIVE_PUBLIC_SITE_RELEASE_ROOT/community"
else
  COMMUNITY_RELEASE_ROOT="${COMMUNITY_RELEASE_ROOT:-$(resolve_public_site_directory "$(read_public_site_setting CommunityReleaseDirectory "../../artifacts/public-site/community")")}"
fi

if [[ -z "${PRO_RELEASE_ROOT:-}" && -d "$LIVE_PUBLIC_SITE_RELEASE_ROOT" ]]; then
  PRO_RELEASE_ROOT="$LIVE_PUBLIC_SITE_RELEASE_ROOT/pro"
else
  PRO_RELEASE_ROOT="${PRO_RELEASE_ROOT:-$(resolve_public_site_directory "$(read_public_site_setting ProReleaseDirectory "../../artifacts/public-site/pro")")}"
fi
COMMUNITY_ASSET_DIR="$COMMUNITY_RELEASE_ROOT/$APP_VERSION"
PRO_ASSET_DIR="$PRO_RELEASE_ROOT/$APP_VERSION"

lms_validate_version "$APP_VERSION"

stage_enabled() {
  local target="$1"
  local requested

  for requested in $STAGE_EDITIONS; do
    case "$requested" in
      ce|community)
        [[ "$target" == "community" ]] && return 0
        ;;
      pro)
        [[ "$target" == "pro" ]] && return 0
        ;;
      portal-local|public-site)
        ;;
      *)
        lms_die "unknown public release staging edition: $requested"
        ;;
    esac
  done

  return 1
}

keep_only_latest_public_site_release_enabled() {
  case "$KEEP_ONLY_LATEST_PUBLIC_SITE_RELEASE" in
    false|False|FALSE|0|no|No|NO|off|Off|OFF)
      return 1
      ;;
    *)
      return 0
      ;;
  esac
}

prune_edition_releases() {
  local edition_root="$1"
  local keep_version="$2"
  local label="$3"

  keep_only_latest_public_site_release_enabled || return 0
  [[ -d "$edition_root" ]] || return 0

  lms_log "Pruning old $label public website releases"
  find "$edition_root" \
    -mindepth 1 \
    -maxdepth 1 \
    -type d \
    ! -name "$keep_version" \
    -exec rm -rf {} +
}

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

staged_any=false

if stage_enabled community; then
  stage_edition "community" "linux-made-sane-ce" "$COMMUNITY_ASSET_DIR" "Community"
  prune_edition_releases "$COMMUNITY_RELEASE_ROOT" "$APP_VERSION" "Community"
  staged_any=true
fi

if stage_enabled pro; then
  stage_edition "pro" "linux-made-sane-pro" "$PRO_ASSET_DIR" "Pro"
  prune_edition_releases "$PRO_RELEASE_ROOT" "$APP_VERSION" "Pro"
  staged_any=true
fi

[[ "$staged_any" == true ]] || lms_die "no public release editions selected for staging"
