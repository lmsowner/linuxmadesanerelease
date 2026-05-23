#!/usr/bin/env bash

# Copyright (c) Richard D. Kiernan.
# Licensed under the Business Source License 1.1. See LICENSE for details.


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
START_SERVICE="${START_SERVICE:-true}"
INSTALL_SYSTEM_PACKAGES="${INSTALL_SYSTEM_PACKAGES:-true}"
CONFIGURE_LOCAL_SSH="${CONFIGURE_LOCAL_SSH:-true}"
ENABLE_LOCAL_SUDO="${ENABLE_LOCAL_SUDO:-true}"
RUNNER_USER="${RUNNER_USER:-linuxmadesane-runner}"
RUNNER_GROUP="${RUNNER_GROUP:-linuxmadesane-runner}"
RUNNER_HOME="${RUNNER_HOME:-/var/lib/linuxmadesane/runner}"
RUNNER_WORKSPACE="${RUNNER_WORKSPACE:-${RUNNER_HOME}/workspace}"
LMS_BASE_URL="${LMS_BASE_URL:-https://www.linuxmadesane.com}"
UPDATE_HELPER_PATH="${UPDATE_HELPER_PATH:-/usr/local/sbin/linux-made-sane-update}"
INSTALL_DESKTOP_HELPER="${INSTALL_DESKTOP_HELPER:-true}"
DESKTOP_HELPER_UNIT="${DESKTOP_HELPER_UNIT:-linux-made-sane-desktop-helper.service}"
DESKTOP_HELPER_SOCKET_PATH="${DESKTOP_HELPER_SOCKET_PATH:-/run/linuxmadesane/desktop-session.sock}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --artifact) ARTIFACT_PATH="$2"; shift 2 ;;
    --install-root) INSTALL_ROOT="$2"; shift 2 ;;
    --data-root) DATA_ROOT="$2"; shift 2 ;;
    --config-root) CONFIG_ROOT="$2"; shift 2 ;;
    --dest-root) LMS_DEST_ROOT="$2"; shift 2 ;;
    --service-port) SERVICE_PORT="$2"; shift 2 ;;
    --start) START_SERVICE=true; shift ;;
    --no-start) START_SERVICE=false; shift ;;
    --skip-system-packages) INSTALL_SYSTEM_PACKAGES=false; shift ;;
    --no-local-ssh) CONFIGURE_LOCAL_SSH=false; shift ;;
    --no-local-sudo) ENABLE_LOCAL_SUDO=false; shift ;;
    --no-desktop-helper) INSTALL_DESKTOP_HELPER=false; shift ;;
    *) lms_die "unknown argument: $1" ;;
  esac
done

DESKTOP_HELPER_LOCAL_LMS_URL="${DESKTOP_HELPER_LOCAL_LMS_URL:-http://127.0.0.1:${SERVICE_PORT}/desktop-assistant}"

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
SYSTEMD_USER_ROOT_ABS="${PREFIX}/etc/systemd/user"
XDG_AUTOSTART_ROOT_ABS="${PREFIX}/etc/xdg/autostart"
RELEASE_ID="${PACKAGE_VERSION_SAFE}-$(date -u +%Y%m%d%H%M%S)"
RELEASE_DIR="$INSTALL_ROOT_ABS/releases/$RELEASE_ID"
CURRENT_DIR="$INSTALL_ROOT_ABS/current"
ENV_FILE="$CONFIG_ROOT_ABS/service.env"
UNIT_FILE="$SYSTEMD_ROOT_ABS/$SERVICE_UNIT"
DESKTOP_HELPER_UNIT_FILE="$SYSTEMD_USER_ROOT_ABS/$DESKTOP_HELPER_UNIT"
DESKTOP_HELPER_AUTOSTART_FILE="$XDG_AUTOSTART_ROOT_ABS/linux-made-sane-desktop-helper.desktop"
EXECUTABLE_PATH="$CURRENT_DIR/LinuxMadeSane.Web"
DESKTOP_HELPER_EXECUTABLE_PATH="$CURRENT_DIR/desktop-helper/LinuxMadeSane.DesktopHelper"
DESKTOP_HELPER_LAUNCHER_PATH="$CURRENT_DIR/desktop-helper/linux-made-sane-desktop-helper-launcher.sh"
PREVIOUS_CURRENT_TARGET="$(lms_current_release_target "$CURRENT_DIR")"
SERVICE_WAS_ACTIVE=false
if lms_systemctl_is_active "$SERVICE_UNIT"; then
  SERVICE_WAS_ACTIVE=true
fi
ROLLBACK_ARMED=false

rollback_failed_install() {
  local exit_code="$?"
  [[ "$ROLLBACK_ARMED" == "true" ]] || exit "$exit_code"

  ROLLBACK_ARMED=false
  local restart_previous="$START_SERVICE"
  if [[ "$SERVICE_WAS_ACTIVE" == "true" ]]; then
    restart_previous=true
  fi

  lms_rollback_current_release "$CURRENT_DIR" "$SERVICE_UNIT" "$PREVIOUS_CURRENT_TARGET" "$restart_previous"
  exit "$exit_code"
}

mkdir -p "$RELEASE_DIR" "$DATA_ROOT_ABS" "$CONFIG_ROOT_ABS" "$SYSTEMD_ROOT_ABS"
if [[ "$INSTALL_DESKTOP_HELPER" == "true" ]]; then
  mkdir -p "$SYSTEMD_USER_ROOT_ABS" "$XDG_AUTOSTART_ROOT_ABS"
fi

lms_install_host_packages sudo openssh-server openssh-client caddy ffmpeg samba-common-bin smbclient cifs-utils
if [[ "$INSTALL_DESKTOP_HELPER" == "true" ]]; then
  lms_install_optional_host_packages libayatana-appindicator3-1 gnome-shell-extension-appindicator
fi

if [[ "$LMS_DEST_ROOT" == "" ]]; then
  lms_prepare_live_service_user "$SERVICE_USER" "$SERVICE_GROUP" "$INSTALL_ROOT"
fi

if lms_is_truthy "$CONFIGURE_LOCAL_SSH"; then
  lms_enable_openssh_server
  lms_prepare_local_ssh_runner "$SERVICE_USER" "$SERVICE_GROUP" "$CONFIG_ROOT_ABS" "$RUNNER_USER" "$RUNNER_GROUP" "$RUNNER_HOME" "$ENABLE_LOCAL_SUDO" "$RUNNER_WORKSPACE"
fi

lms_detect_installer_identity
if [[ "$INSTALL_DESKTOP_HELPER" == "true" ]]; then
  lms_prepare_desktop_helper_access "$SERVICE_GROUP"
fi

ROLLBACK_ARMED=true
trap rollback_failed_install ERR

cp -a "$APP_SOURCE"/. "$RELEASE_DIR"/
ln -sfn "$RELEASE_DIR" "$CURRENT_DIR"

lms_write_env_file \
  "$ENV_FILE" \
  "ASPNETCORE_ENVIRONMENT=Production" \
  "ASPNETCORE_URLS=http://0.0.0.0:${SERVICE_PORT}" \
  "ConnectionStrings__LinuxMadeSane=Data Source=${DATA_ROOT}/linuxmadesane.db" \
  "DataProtection__KeyDirectory=${DATA_ROOT}/protection-keys" \
  "LocalHostBootstrap__Username=${RUNNER_USER}" \
  "LocalHostBootstrap__PrivateKeyPath=${CONFIG_ROOT}/ssh/lms_local_runner_ed25519" \
  "LocalHostBootstrap__DefaultWorkingDirectory=${RUNNER_WORKSPACE}" \
  "LocalHostBootstrap__Port=22" \
  "InitialSetupBootstrap__InstallerUsername=${LMS_INSTALLER_USERNAME}" \
  "InitialSetupBootstrap__InstallerUserId=${LMS_INSTALLER_UID}" \
  "InitialSetupBootstrap__InstallerHomeDirectory=${LMS_INSTALLER_HOME}" \
  "InitialSetupBootstrap__InstallerShell=${LMS_INSTALLER_SHELL}" \
  "InitialSetupBootstrap__InstalledAtUtc=${LMS_INSTALLER_INSTALLED_AT_UTC}" \
  "ApplicationUpdates__Enabled=true" \
  "ApplicationUpdates__ManifestUrl=${LMS_BASE_URL}/api/downloads/manifest" \
  "ApplicationUpdates__InstallScriptUrl=${LMS_BASE_URL}/install.sh" \
  "ApplicationUpdates__Edition=community" \
  "ApplicationUpdates__Rid=linux-x64" \
  "ApplicationUpdates__CheckIntervalMinutes=360" \
  "ApplicationUpdates__InstallAutomatically=false" \
  "ApplicationUpdates__UpdateHelperPath=${UPDATE_HELPER_PATH}" \
  "DesktopSession__SocketPath=${DESKTOP_HELPER_SOCKET_PATH}"

lms_render_systemd_unit \
  "$REPO_ROOT/deploy/systemd/linux-made-sane.service.template" \
  "$UNIT_FILE" \
  "$SERVICE_DESCRIPTION" \
  "$SERVICE_USER" \
  "$SERVICE_GROUP" \
  "$CURRENT_DIR" \
  "$ENV_FILE" \
  "$EXECUTABLE_PATH"

if [[ "$INSTALL_DESKTOP_HELPER" == "true" && -x "$RELEASE_DIR/desktop-helper/LinuxMadeSane.DesktopHelper" ]]; then
  lms_write_desktop_helper_launcher \
    "$DESKTOP_HELPER_LAUNCHER_PATH" \
    "$SERVICE_GROUP" \
    "$DESKTOP_HELPER_EXECUTABLE_PATH" \
    "$DESKTOP_HELPER_SOCKET_PATH" \
    "$DESKTOP_HELPER_LOCAL_LMS_URL" \
    "$CURRENT_DIR/wwwroot/images/lms-logo-192.png"
  lms_render_desktop_helper_file \
    "$REPO_ROOT/deploy/systemd/linux-made-sane-desktop-helper.service.template" \
    "$DESKTOP_HELPER_UNIT_FILE" \
    "$DESKTOP_HELPER_SOCKET_PATH" \
    "$DESKTOP_HELPER_LAUNCHER_PATH" \
    "$DESKTOP_HELPER_LOCAL_LMS_URL" \
    "$CURRENT_DIR/wwwroot/images/lms-logo-192.png"
  lms_render_desktop_helper_file \
    "$REPO_ROOT/deploy/xdg/linux-made-sane-desktop-helper.desktop.template" \
    "$DESKTOP_HELPER_AUTOSTART_FILE" \
    "$DESKTOP_HELPER_SOCKET_PATH" \
    "$DESKTOP_HELPER_LAUNCHER_PATH" \
    "$DESKTOP_HELPER_LOCAL_LMS_URL" \
    "$CURRENT_DIR/wwwroot/images/lms-logo-192.png"
fi

lms_write_update_helper "$SERVICE_USER" "$LMS_BASE_URL" "$UPDATE_HELPER_PATH"

lms_maybe_chown "$SERVICE_USER:$SERVICE_GROUP" "$INSTALL_ROOT_ABS"
lms_maybe_chown "$SERVICE_USER:$SERVICE_GROUP" "$DATA_ROOT_ABS"
lms_maybe_chown "$SERVICE_USER:$SERVICE_GROUP" "$CONFIG_ROOT_ABS"
lms_prepare_desktop_session_socket_directory "$DESKTOP_HELPER_SOCKET_PATH" "$SERVICE_USER" "$SERVICE_GROUP"

lms_maybe_systemctl_reload
lms_maybe_systemctl_enable "$SERVICE_UNIT"
if [[ "$START_SERVICE" == "true" ]]; then
  lms_maybe_systemctl_restart "$SERVICE_UNIT"
  if [[ "$INSTALL_DESKTOP_HELPER" == "true" && "$LMS_DEST_ROOT" == "" ]] && lms_has_systemd; then
    if ! lms_wait_for_file_socket "$DESKTOP_HELPER_SOCKET_PATH" 30; then
      lms_log "Desktop Assistant broker socket was not created at $DESKTOP_HELPER_SOCKET_PATH"
      systemctl --no-pager --full status "$SERVICE_UNIT" >&2 || true
      exit 1
    fi
  fi
fi
lms_enable_caddy_service
if [[ "$INSTALL_DESKTOP_HELPER" == "true" && -f "$DESKTOP_HELPER_UNIT_FILE" ]]; then
  lms_enable_desktop_helper_global_unit "$DESKTOP_HELPER_UNIT"
  lms_try_start_desktop_helper_for_installer_user "$DESKTOP_HELPER_UNIT"
fi

ROLLBACK_ARMED=false
trap - ERR

lms_log "CE install complete"
printf 'version: %s\nservice unit: %s\ninstall root: %s\ndata root: %s\nconfig file: %s\nport: %s\n' \
  "$PACKAGE_VERSION" "$SERVICE_UNIT" "$INSTALL_ROOT_ABS" "$DATA_ROOT_ABS" "$ENV_FILE" "$SERVICE_PORT"
printf 'local SSH runner: %s@localhost:22\n' "$RUNNER_USER"
if [[ "$INSTALL_DESKTOP_HELPER" == "true" && -f "$DESKTOP_HELPER_UNIT_FILE" ]]; then
  printf 'desktop helper: %s\n' "$DESKTOP_HELPER_UNIT_FILE"
  printf 'desktop helper socket: %s\n' "$DESKTOP_HELPER_SOCKET_PATH"
  printf 'desktop tray URL: %s\n' "$DESKTOP_HELPER_LOCAL_LMS_URL"
  printf 'start helper in the current GUI session: systemctl --user enable --now %s\n' "$DESKTOP_HELPER_UNIT"
fi
lms_print_access_urls "$SERVICE_PORT"
printf 'update command: sudo %s\n' "$UPDATE_HELPER_PATH"
