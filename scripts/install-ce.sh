#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/lib/deploy-common.sh
source "$SCRIPT_DIR/lib/deploy-common.sh"

REPO_ROOT="$(lms_repo_root)"
ARTIFACT_PATH="${ARTIFACT_PATH:-}"
INSTALL_ROOT="${INSTALL_ROOT:-/opt/linuxmadesane/ce}"
DATA_ROOT="${DATA_ROOT:-/var/lib/linuxmadesane/ce}"
CONFIG_ROOT="${CONFIG_ROOT:-/etc/linuxmadesane/ce}"
LMS_DEST_ROOT="${LMS_DEST_ROOT:-}"
SERVICE_USER="${SERVICE_USER:-linuxmadesane}"
SERVICE_GROUP="${SERVICE_GROUP:-linuxmadesane}"
SERVICE_UNIT="${SERVICE_UNIT:-linux-made-sane.service}"
SERVICE_DESCRIPTION="Linux Made Sane Service"
SERVICE_PORT="${SERVICE_PORT:-5080}"
START_SERVICE="${START_SERVICE:-false}"
INSTALL_SYSTEM_PACKAGES="${INSTALL_SYSTEM_PACKAGES:-true}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --artifact) ARTIFACT_PATH="$2"; shift 2 ;;
    --install-root) INSTALL_ROOT="$2"; shift 2 ;;
    --data-root) DATA_ROOT="$2"; shift 2 ;;
    --config-root) CONFIG_ROOT="$2"; shift 2 ;;
    --dest-root) LMS_DEST_ROOT="$2"; shift 2 ;;
    --service-port) SERVICE_PORT="$2"; shift 2 ;;
    --start) START_SERVICE=true; shift ;;
    --skip-system-packages) INSTALL_SYSTEM_PACKAGES=false; shift ;;
    *) lms_die "unknown argument: $1" ;;
  esac
done

if [[ -z "$ARTIFACT_PATH" ]]; then
  ARTIFACT_PATH="$(lms_find_latest_artifact "linux-made-sane-ce-*.tar.gz" "$REPO_ROOT/artifacts/packages" || true)"
fi

[[ -n "$ARTIFACT_PATH" ]] || lms_die "no CE artifact found under $REPO_ROOT/artifacts/packages"
ARTIFACT_PATH="$(lms_abs_path "$ARTIFACT_PATH")"
[[ -e "$ARTIFACT_PATH" ]] || lms_die "artifact not found: $ARTIFACT_PATH"

STAGING_DIR="$(mktemp -d)"
cleanup() {
  rm -rf "$STAGING_DIR"
}
trap cleanup EXIT

PACKAGE_ROOT="$(lms_unpack_artifact "$ARTIFACT_PATH" "$STAGING_DIR")"
APP_SOURCE="$PACKAGE_ROOT/app"
[[ -d "$APP_SOURCE" ]] || lms_die "artifact does not contain an app directory: $PACKAGE_ROOT"
PACKAGE_VERSION="$(sed -n '1p' "$PACKAGE_ROOT/version.txt" 2>/dev/null | tr -d '\r\n')"
PACKAGE_VERSION="${PACKAGE_VERSION:-unknown}"
PACKAGE_VERSION_SAFE="$(printf '%s' "$PACKAGE_VERSION" | tr -c '0-9A-Za-z._-' '-')"

PREFIX="${LMS_DEST_ROOT%/}"
INSTALL_ROOT_ABS="${PREFIX}${INSTALL_ROOT}"
DATA_ROOT_ABS="${PREFIX}${DATA_ROOT}"
CONFIG_ROOT_ABS="${PREFIX}${CONFIG_ROOT}"
SYSTEMD_ROOT_ABS="${PREFIX}/etc/systemd/system"
RELEASE_ID="${PACKAGE_VERSION_SAFE}-$(date -u +%Y%m%d%H%M%S)"
RELEASE_DIR="$INSTALL_ROOT_ABS/releases/$RELEASE_ID"
CURRENT_DIR="$INSTALL_ROOT_ABS/current"
ENV_FILE="$CONFIG_ROOT_ABS/service.env"
UNIT_FILE="$SYSTEMD_ROOT_ABS/$SERVICE_UNIT"
EXECUTABLE_PATH="$CURRENT_DIR/LinuxMadeSane.Web"

mkdir -p "$RELEASE_DIR" "$DATA_ROOT_ABS" "$CONFIG_ROOT_ABS" "$SYSTEMD_ROOT_ABS"

lms_install_host_packages ffmpeg samba-common-bin smbclient cifs-utils

if [[ "$LMS_DEST_ROOT" == "" ]]; then
  lms_prepare_live_service_user "$SERVICE_USER" "$SERVICE_GROUP" "$INSTALL_ROOT"
fi

cp -a "$APP_SOURCE"/. "$RELEASE_DIR"/
ln -sfn "$RELEASE_DIR" "$CURRENT_DIR"

lms_write_env_file \
  "$ENV_FILE" \
  "ASPNETCORE_ENVIRONMENT=Production" \
  "ASPNETCORE_URLS=http://0.0.0.0:${SERVICE_PORT}" \
  "ConnectionStrings__LinuxMadeSane=Data Source=${DATA_ROOT}/linuxmadesane.db"

lms_render_systemd_unit \
  "$REPO_ROOT/deploy/systemd/linux-made-sane.service.template" \
  "$UNIT_FILE" \
  "$SERVICE_DESCRIPTION" \
  "$SERVICE_USER" \
  "$SERVICE_GROUP" \
  "$CURRENT_DIR" \
  "$ENV_FILE" \
  "$EXECUTABLE_PATH"

lms_maybe_chown "$SERVICE_USER:$SERVICE_GROUP" "$INSTALL_ROOT_ABS"
lms_maybe_chown "$SERVICE_USER:$SERVICE_GROUP" "$DATA_ROOT_ABS"
lms_maybe_chown "$SERVICE_USER:$SERVICE_GROUP" "$CONFIG_ROOT_ABS"

lms_maybe_systemctl_reload
lms_maybe_systemctl_enable "$SERVICE_UNIT"
if [[ "$START_SERVICE" == "true" ]]; then
  lms_maybe_systemctl_restart "$SERVICE_UNIT"
fi

lms_log "CE install complete"
printf 'version: %s\nservice unit: %s\ninstall root: %s\ndata root: %s\nconfig file: %s\nport: %s\n' \
  "$PACKAGE_VERSION" "$SERVICE_UNIT" "$INSTALL_ROOT_ABS" "$DATA_ROOT_ABS" "$ENV_FILE" "$SERVICE_PORT"
