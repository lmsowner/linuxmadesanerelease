#!/usr/bin/env bash

# Copyright (c) Richard D. Kiernan.
# Licensed under the Business Source License 1.1. See LICENSE for details.


set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/lib/deploy-common.sh
source "$SCRIPT_DIR/lib/deploy-common.sh"

lms_require_command dotnet

REPO_ROOT="$(lms_repo_root)"
CONFIGURATION="${CONFIGURATION:-Release}"
RUNTIME="${RUNTIME:-linux-x64}"
SELF_CONTAINED="${SELF_CONTAINED:-false}"
APP_VERSION="$(lms_resolve_version)"
VERSION_DATE="$(lms_resolve_version_date)"
VERSION_REVISION="$(lms_resolve_version_revision)"
PACKAGE_NAME="linux-made-sane-ce-${APP_VERSION}-${RUNTIME}"
PACKAGE_ROOT="${OUTPUT_ROOT:-$REPO_ROOT/artifacts/publish/$PACKAGE_NAME}"
PACKAGE_TARBALL="${PACKAGE_TARBALL:-$REPO_ROOT/artifacts/packages/${PACKAGE_NAME}.tar.gz}"
DESKTOP_HELPER_OUTPUT_DIR="$PACKAGE_ROOT/app/desktop-helper"

lms_validate_version "$APP_VERSION"
lms_reset_dir "$PACKAGE_ROOT"
mkdir -p "$PACKAGE_ROOT/app" "$DESKTOP_HELPER_OUTPUT_DIR"

lms_log "Publishing CE package to $PACKAGE_ROOT/app"
dotnet publish \
  "$REPO_ROOT/src/LinuxMadeSane.Web/LinuxMadeSane.Web.csproj" \
  -c "$CONFIGURATION" \
  -r "$RUNTIME" \
  --self-contained "$SELF_CONTAINED" \
  -o "$PACKAGE_ROOT/app" \
  /p:LinuxMadeSaneVersion="$APP_VERSION" \
  /p:LinuxMadeSaneVersionDate="$VERSION_DATE" \
  /p:LinuxMadeSaneVersionRevision="$VERSION_REVISION" \
  /p:LinuxMadeSaneSkipPluginPackaging=true

if find "$PACKAGE_ROOT/app" -path '*/.playwright' -type d -prune -print -quit | grep -q .; then
  lms_die "Playwright assets were published into the CE package. Remove demo tooling from the host package before release."
fi

lms_log "Publishing desktop session helper to $DESKTOP_HELPER_OUTPUT_DIR"
dotnet publish \
  "$REPO_ROOT/src/LinuxMadeSane.DesktopHelper/LinuxMadeSane.DesktopHelper.csproj" \
  -c "$CONFIGURATION" \
  -r "$RUNTIME" \
  --self-contained "$SELF_CONTAINED" \
  -o "$DESKTOP_HELPER_OUTPUT_DIR" \
  /p:LinuxMadeSaneVersion="$APP_VERSION" \
  /p:LinuxMadeSaneVersionDate="$VERSION_DATE" \
  /p:LinuxMadeSaneVersionRevision="$VERSION_REVISION"

printf 'ce\n' > "$PACKAGE_ROOT/edition.txt"
printf '%s\n' "$APP_VERSION" > "$PACKAGE_ROOT/version.txt"
printf '%s\n' "$APP_VERSION" > "$PACKAGE_ROOT/app/version.txt"
lms_create_tarball "$PACKAGE_ROOT" "$PACKAGE_TARBALL"

lms_log "CE package ready:"
printf 'version: %s\nartifact: %s\n' "$APP_VERSION" "$PACKAGE_TARBALL"
