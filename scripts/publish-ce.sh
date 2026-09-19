#!/usr/bin/env bash

# Copyright (c) Linux Made Sane.
# Licensed under the Business Source License 1.1. See LICENSE for details.


set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/lib/deploy-common.sh
source "$SCRIPT_DIR/lib/deploy-common.sh"

lms_require_command dotnet
lms_require_command python3

REPO_ROOT="$(lms_repo_root)"
REQUIRE_PUSHED_GIT_STATE="${REQUIRE_PUSHED_GIT_STATE:-true}"
if lms_is_truthy "$REQUIRE_PUSHED_GIT_STATE"; then
  lms_require_clean_pushed_release_source "$REPO_ROOT"
fi
SOURCE_COMMIT="${LINUX_MADE_SANE_SOURCE_COMMIT:-$(lms_release_source_commit "$REPO_ROOT")}"
CONFIGURATION="${CONFIGURATION:-Release}"
RUNTIME="${RUNTIME:-linux-x64}"
SELF_CONTAINED="${SELF_CONTAINED:-false}"
APP_VERSION="$(lms_resolve_version)"
VERSION_DATE="$(lms_resolve_version_date)"
VERSION_REVISION="$(lms_resolve_version_revision)"
PACKAGE_NAME="linux-made-sane-ce-${APP_VERSION}-${RUNTIME}"
PACKAGE_ROOT="${OUTPUT_ROOT:-$REPO_ROOT/artifacts/publish/$PACKAGE_NAME}"
PACKAGE_TARBALL="${PACKAGE_TARBALL:-$REPO_ROOT/artifacts/packages/${PACKAGE_NAME}.tar.gz}"
DESKTOP_HELPER_OUTPUT_DIR="$(mktemp -d)"
DESKTOP_HELPER_ARCHIVE_DIR="$PACKAGE_ROOT/app/optional"
TOOLS_OUTPUT_DIR="$PACKAGE_ROOT/app/tools"

cleanup() {
  rm -rf -- "$DESKTOP_HELPER_OUTPUT_DIR"
}
trap cleanup EXIT

lms_validate_version "$APP_VERSION"
lms_reset_dir "$PACKAGE_ROOT"
mkdir -p "$PACKAGE_ROOT/app" "$DESKTOP_HELPER_ARCHIVE_DIR" "$TOOLS_OUTPUT_DIR"

lms_log "Refreshing About package credits from the CE dependency graph"
for project in Web DesktopHelper; do
  dotnet restore "$REPO_ROOT/src/LinuxMadeSane.$project/LinuxMadeSane.$project.csproj" -r "$RUNTIME" /p:SelfContained="$SELF_CONTAINED"
done
python3 "$REPO_ROOT/scripts/generate-about-credits.py"
if lms_is_truthy "$REQUIRE_PUSHED_GIT_STATE" &&
   [[ -n "$(git -C "$REPO_ROOT" status --porcelain --untracked-files=normal)" ]]; then
  lms_die "release generation changed tracked source; commit and push the generated files before publishing"
fi

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
  /p:DebugType=None \
  /p:DebugSymbols=false \
  /p:PathMap="$REPO_ROOT=/_/lms" \
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
  /p:LinuxMadeSaneVersionRevision="$VERSION_REVISION" \
  /p:DebugType=None \
  /p:DebugSymbols=false \
  /p:PathMap="$REPO_ROOT=/_/lms"

tar -czf "$DESKTOP_HELPER_ARCHIVE_DIR/desktop-helper.tar.gz" -C "$DESKTOP_HELPER_OUTPUT_DIR" .

install -m 0755 \
  "$REPO_ROOT/scripts/linux-made-sane-desktop-helper-setup.sh" \
  "$TOOLS_OUTPUT_DIR/linux-made-sane-desktop-helper-setup"
install -m 0755 \
  "$REPO_ROOT/scripts/linux-made-sane-desktop-helper-launcher.sh" \
  "$TOOLS_OUTPUT_DIR/linux-made-sane-desktop-helper-launcher"
install -m 0755 "$REPO_ROOT/scripts/linux-made-sane-caddy-setup.sh" "$TOOLS_OUTPUT_DIR/linux-made-sane-caddy-setup"
install -D -m 0644 "$REPO_ROOT/scripts/lib/deploy-common.sh" "$TOOLS_OUTPUT_DIR/lib/deploy-common.sh"

printf 'ce\n' > "$PACKAGE_ROOT/edition.txt"
printf 'ce\n' > "$PACKAGE_ROOT/app/edition.txt"
printf '%s\n' "$APP_VERSION" > "$PACKAGE_ROOT/version.txt"
printf '%s\n' "$APP_VERSION" > "$PACKAGE_ROOT/app/version.txt"
printf '%s\n' "$SOURCE_COMMIT" > "$PACKAGE_ROOT/source-commit.txt"
printf '%s\n' "$SOURCE_COMMIT" > "$PACKAGE_ROOT/app/source-commit.txt"
lms_create_tarball "$PACKAGE_ROOT" "$PACKAGE_TARBALL"
lms_cleanup_expanded_release_output "$PACKAGE_ROOT" "${OUTPUT_ROOT:+true}"

lms_log "CE package ready:"
printf 'version: %s\nartifact: %s\n' "$APP_VERSION" "$PACKAGE_TARBALL"
