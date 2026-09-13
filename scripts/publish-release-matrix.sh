#!/usr/bin/env bash

# Copyright (c) Linux Made Sane.
# Licensed under the Business Source License 1.1. See LICENSE for details.


set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/lib/deploy-common.sh
source "$SCRIPT_DIR/lib/deploy-common.sh"

lms_require_command dotnet
lms_require_command sha256sum

REPO_ROOT="$(lms_repo_root)"
REQUIRE_PUSHED_GIT_STATE="${REQUIRE_PUSHED_GIT_STATE:-true}"
CONFIGURATION="${CONFIGURATION:-Release}"
SELF_CONTAINED="${SELF_CONTAINED:-true}"
HOST_RUNTIMES="${HOST_RUNTIMES:-${RUNTIMES:-linux-x64 linux-arm64 linux-arm}}"
CE_RUNTIMES="${CE_RUNTIMES:-$HOST_RUNTIMES}"
PRO_RUNTIMES="${PRO_RUNTIMES:-$HOST_RUNTIMES}"
PORTAL_LOCAL_RUNTIMES="${PORTAL_LOCAL_RUNTIMES:-linux-x64}"
PUBLIC_SITE_RUNTIMES="${PUBLIC_SITE_RUNTIMES:-linux-x64}"
EDITIONS="${EDITIONS:-ce pro portal-local}"
APP_VERSION="$(lms_resolve_version)"
PACKAGE_DIR="${PACKAGE_DIR:-$REPO_ROOT/artifacts/packages}"
MANIFEST_PATH="${MANIFEST_PATH:-$PACKAGE_DIR/release-manifest-${APP_VERSION}.json}"
CHECKSUM_PATH="${CHECKSUM_PATH:-$PACKAGE_DIR/SHA256SUMS}"
STAGE_PUBLIC_SITE_RELEASES="${STAGE_PUBLIC_SITE_RELEASES:-true}"
CLEAN_OLD_RELEASE_OUTPUTS="${CLEAN_OLD_RELEASE_OUTPUTS:-true}"
if lms_is_truthy "$REQUIRE_PUSHED_GIT_STATE"; then
  lms_require_clean_pushed_release_source "$REPO_ROOT"
fi
SOURCE_COMMIT="$(lms_release_source_commit "$REPO_ROOT")"

lms_validate_version "$APP_VERSION"
mkdir -p "$PACKAGE_DIR"

declare -a artifacts=()
declare -a artifact_editions=()
declare -a artifact_runtimes=()
declare -a artifact_sha256=()
declare -a artifact_sizes=()

release_cleanup_enabled() {
  case "$CLEAN_OLD_RELEASE_OUTPUTS" in
    false|False|FALSE|0|no|No|NO|off|Off|OFF)
      return 1
      ;;
    *)
      return 0
      ;;
  esac
}

cleanup_old_release_outputs() {
  release_cleanup_enabled || return 0

  lms_log "Cleaning old generated release packages and staging output"
  find "$PACKAGE_DIR" -maxdepth 1 -type f \( \
      -name 'linux-made-sane-*.tar.gz' \
      -o -name 'release-manifest-*.json' \
      -o -name 'SHA256SUMS' \
    \) -delete

  if [[ -d "$REPO_ROOT/artifacts/publish" ]]; then
    find "$REPO_ROOT/artifacts/publish" -mindepth 1 -maxdepth 1 -type d -name 'linux-made-sane-*' -exec rm -rf {} +
  fi

  if [[ -d "$REPO_ROOT/artifacts/public-site/community" ]]; then
    find "$REPO_ROOT/artifacts/public-site/community" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
  fi

  if [[ -d "$REPO_ROOT/artifacts/public-site/pro" ]]; then
    find "$REPO_ROOT/artifacts/public-site/pro" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
  fi
}

edition_runtimes() {
  local edition="$1"
  case "$edition" in
    ce)
      printf '%s\n' "$CE_RUNTIMES"
      ;;
    pro)
      printf '%s\n' "$PRO_RUNTIMES"
      ;;
    portal-local)
      printf '%s\n' "$PORTAL_LOCAL_RUNTIMES"
      ;;
    public-site)
      printf '%s\n' "$PUBLIC_SITE_RUNTIMES"
      ;;
    *)
      lms_die "unknown release edition: $edition"
      ;;
  esac
}

publish_one() {
  local edition="$1"
  local runtime="$2"
  local script_path
  local artifact_name

  case "$edition" in
    ce)
      script_path="$SCRIPT_DIR/publish-ce.sh"
      artifact_name="linux-made-sane-ce-${APP_VERSION}-${runtime}.tar.gz"
      ;;
    pro)
      script_path="$SCRIPT_DIR/publish-pro.sh"
      artifact_name="linux-made-sane-pro-${APP_VERSION}-${runtime}.tar.gz"
      ;;
    portal-local)
      script_path="$SCRIPT_DIR/publish-portal-local.sh"
      artifact_name="linux-made-sane-portal-local-${APP_VERSION}-${runtime}.tar.gz"
      ;;
    public-site)
      script_path="$SCRIPT_DIR/publish-public-site.sh"
      artifact_name="linux-made-sane-public-site-${APP_VERSION}-${runtime}.tar.gz"
      ;;
    *)
      lms_die "unknown release edition: $edition"
      ;;
  esac

  local artifact_path="$PACKAGE_DIR/$artifact_name"
  lms_log "Publishing $edition for $runtime"
  CONFIGURATION="$CONFIGURATION" \
    RUNTIME="$runtime" \
    SELF_CONTAINED="$SELF_CONTAINED" \
    LINUX_MADE_SANE_VERSION="$APP_VERSION" \
    PACKAGE_TARBALL="$artifact_path" \
    "$script_path"

  local sha size
  sha="$(sha256sum "$artifact_path" | awk '{print $1}')"
  size="$(wc -c < "$artifact_path" | tr -d ' ')"

  artifacts+=("$artifact_path")
  artifact_editions+=("$edition")
  artifact_runtimes+=("$runtime")
  artifact_sha256+=("$sha")
  artifact_sizes+=("$size")
}

cleanup_old_release_outputs

for edition in $EDITIONS; do
  for runtime in $(edition_runtimes "$edition"); do
    publish_one "$edition" "$runtime"
  done
done

: > "$CHECKSUM_PATH"
for index in "${!artifacts[@]}"; do
  printf '%s  %s\n' "${artifact_sha256[$index]}" "$(basename "${artifacts[$index]}")" >> "$CHECKSUM_PATH"
done

{
  printf '{\n'
  printf '  "version": "%s",\n' "$APP_VERSION"
  printf '  "builtUtc": "%s",\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf '  "sourceCommit": "%s",\n' "$SOURCE_COMMIT"
  printf '  "selfContained": %s,\n' "$SELF_CONTAINED"
  printf '  "artifacts": [\n'
  for index in "${!artifacts[@]}"; do
    [[ "$index" -eq 0 ]] || printf ',\n'
    printf '    {\n'
    printf '      "edition": "%s",\n' "${artifact_editions[$index]}"
    printf '      "runtime": "%s",\n' "${artifact_runtimes[$index]}"
    printf '      "file": "%s",\n' "$(basename "${artifacts[$index]}")"
    printf '      "sha256": "%s",\n' "${artifact_sha256[$index]}"
    printf '      "sizeBytes": %s\n' "${artifact_sizes[$index]}"
    printf '    }'
  done
  printf '\n  ]\n'
  printf '}\n'
} > "$MANIFEST_PATH"

lms_log "Release matrix complete"
printf 'version: %s\nmanifest: %s\nchecksums: %s\n' "$APP_VERSION" "$MANIFEST_PATH" "$CHECKSUM_PATH"

edition_enabled() {
  local target="$1"
  local edition
  for edition in $EDITIONS; do
    [[ "$edition" == "$target" ]] && return 0
  done

  return 1
}

case "$STAGE_PUBLIC_SITE_RELEASES" in
  false|False|FALSE|0|no|No|NO|off|Off|OFF)
    lms_log "Skipping public website release asset staging"
    ;;
  *)
    declare -a stage_editions=()
    if edition_enabled ce; then
      stage_editions+=("community")
    fi

    if edition_enabled pro; then
      stage_editions+=("pro")
    fi

    if [[ "${#stage_editions[@]}" -gt 0 ]]; then
      lms_log "Staging ${stage_editions[*]} release assets for the public website"
      PACKAGE_DIR="$PACKAGE_DIR" \
        LINUX_MADE_SANE_VERSION="$APP_VERSION" \
        LINUX_MADE_SANE_SOURCE_COMMIT="$SOURCE_COMMIT" \
        RUNTIMES="$HOST_RUNTIMES" \
        STAGE_EDITIONS="${stage_editions[*]}" \
        "$SCRIPT_DIR/stage-public-release-assets.sh"
    else
      lms_log "Skipping public website release asset staging because no public host editions were built"
    fi
    ;;
esac
