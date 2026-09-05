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
LMS_DATABASE_CONNECTION_STRING="${LMS_DATABASE_CONNECTION_STRING:-}"
LMS_DATA_PROTECTION_KEY_DIRECTORY="${LMS_DATA_PROTECTION_KEY_DIRECTORY:-}"
INSTALL_DESKTOP_HELPER="${INSTALL_DESKTOP_HELPER:-auto}"
DESKTOP_HELPER_UNIT="${DESKTOP_HELPER_UNIT:-linux-made-sane-desktop-helper.service}"
DESKTOP_HELPER_SOCKET_PATH="${DESKTOP_HELPER_SOCKET_PATH:-/run/linuxmadesane/desktop-session.sock}"
DESKTOP_HELPER_MANAGER_PATH="${DESKTOP_HELPER_MANAGER_PATH:-/usr/local/sbin/linux-made-sane-desktop-helper-setup}"
DESKTOP_HELPER_MANAGER_CONFIG_PATH="${DESKTOP_HELPER_MANAGER_CONFIG_PATH:-/etc/linuxmadesane/ce-desktop-helper.conf}"
DESKTOP_HELPER_PREFERENCE_PATH="${DESKTOP_HELPER_PREFERENCE_PATH:-/etc/linuxmadesane/ce-desktop-helper.enabled}"
DESKTOP_HELPER_SUDOERS_PATH="${DESKTOP_HELPER_SUDOERS_PATH:-/etc/sudoers.d/linux-made-sane-desktop-helper}"
DESKTOP_HELPER_LAUNCHER_PATH="${DESKTOP_HELPER_LAUNCHER_PATH:-/usr/local/lib/linuxmadesane/linux-made-sane-desktop-helper-launcher}"

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
    --desktop-helper) INSTALL_DESKTOP_HELPER=true; shift ;;
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
DESKTOP_HELPER_MANAGER_FILE="${PREFIX}${DESKTOP_HELPER_MANAGER_PATH}"
DESKTOP_HELPER_MANAGER_CONFIG_FILE="${PREFIX}${DESKTOP_HELPER_MANAGER_CONFIG_PATH}"
DESKTOP_HELPER_PREFERENCE_FILE="${PREFIX}${DESKTOP_HELPER_PREFERENCE_PATH}"
DESKTOP_HELPER_SUDOERS_FILE="${PREFIX}${DESKTOP_HELPER_SUDOERS_PATH}"
DESKTOP_HELPER_LAUNCHER_FILE="${PREFIX}${DESKTOP_HELPER_LAUNCHER_PATH}"
EXECUTABLE_PATH="$CURRENT_DIR/LinuxMadeSane.Web"
if [[ -z "$LMS_DATABASE_CONNECTION_STRING" ]]; then
  LMS_DATABASE_CONNECTION_STRING="$(lms_read_env_value "$ENV_FILE" "ConnectionStrings__LinuxMadeSane" || true)"
fi
if [[ -z "$LMS_DATA_PROTECTION_KEY_DIRECTORY" ]]; then
  LMS_DATA_PROTECTION_KEY_DIRECTORY="$(lms_read_env_value "$ENV_FILE" "DataProtection__KeyDirectory" || true)"
fi
LMS_DATABASE_CONNECTION_STRING="${LMS_DATABASE_CONNECTION_STRING:-Data Source=${DATA_ROOT}/linuxmadesane.db}"
LMS_DATA_PROTECTION_KEY_DIRECTORY="${LMS_DATA_PROTECTION_KEY_DIRECTORY:-${DATA_ROOT}/protection-keys}"
PREVIOUS_CURRENT_TARGET="$(lms_current_release_target "$CURRENT_DIR")"
PREVIOUS_ENV_FILE=""
SERVICE_WAS_ACTIVE=false
if lms_systemctl_is_active "$SERVICE_UNIT"; then
  SERVICE_WAS_ACTIVE=true
fi
INSTALL_DESKTOP_HELPER="$(lms_resolve_desktop_helper_choice \
  "$INSTALL_DESKTOP_HELPER" \
  "$DESKTOP_HELPER_PREFERENCE_FILE" \
  "$DESKTOP_HELPER_UNIT_FILE" \
  "$DESKTOP_HELPER_AUTOSTART_FILE" \
  "$DESKTOP_HELPER_UNIT")"
ROLLBACK_ARMED=false

rollback_failed_install() {
  local exit_code="$?"
  [[ "$ROLLBACK_ARMED" == "true" ]] || exit "$exit_code"

  ROLLBACK_ARMED=false
  local restart_previous="$START_SERVICE"
  if [[ "$SERVICE_WAS_ACTIVE" == "true" ]]; then
    restart_previous=true
  fi

  if [[ -n "$PREVIOUS_ENV_FILE" && -f "$PREVIOUS_ENV_FILE" ]]; then
    mv -f -- "$PREVIOUS_ENV_FILE" "$ENV_FILE" || true
  fi

  lms_rollback_current_release "$CURRENT_DIR" "$SERVICE_UNIT" "$PREVIOUS_CURRENT_TARGET" "$restart_previous"
  exit "$exit_code"
}

mkdir -p "$RELEASE_DIR" "$DATA_ROOT_ABS" "$CONFIG_ROOT_ABS" "$SYSTEMD_ROOT_ABS" "$SYSTEMD_USER_ROOT_ABS"

lms_install_host_packages sudo openssh-server openssh-client caddy ffmpeg samba-common-bin smbclient cifs-utils
if [[ "$INSTALL_DESKTOP_HELPER" == "true" ]]; then
  lms_install_optional_host_packages libayatana-appindicator3-1
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

if [[ -f "$ENV_FILE" ]]; then
  PREVIOUS_ENV_FILE="$ENV_FILE.lms-update-backup"
  cp -a -- "$ENV_FILE" "$PREVIOUS_ENV_FILE"
fi

ROLLBACK_ARMED=true
trap rollback_failed_install ERR

cp -a "$APP_SOURCE"/. "$RELEASE_DIR"/
ln -sfn "$RELEASE_DIR" "$CURRENT_DIR"

lms_write_env_file \
  "$ENV_FILE" \
  "ASPNETCORE_ENVIRONMENT=Production" \
  "ASPNETCORE_URLS=http://0.0.0.0:${SERVICE_PORT}" \
  "ConnectionStrings__LinuxMadeSane=${LMS_DATABASE_CONNECTION_STRING}" \
  "DataProtection__KeyDirectory=${LMS_DATA_PROTECTION_KEY_DIRECTORY}" \
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

if [[ -f "$RELEASE_DIR/optional/desktop-helper.tar.gz" ]]; then
  lms_render_desktop_helper_file \
    "$REPO_ROOT/deploy/systemd/linux-made-sane-desktop-helper.service.template" \
    "$DESKTOP_HELPER_UNIT_FILE" \
    "$DESKTOP_HELPER_SOCKET_PATH" \
    "$DESKTOP_HELPER_LAUNCHER_PATH" \
    "$DESKTOP_HELPER_LOCAL_LMS_URL" \
    "$CURRENT_DIR/wwwroot/images/lms-logo-192.png"

  if ! lms_install_desktop_helper_manager \
    "$RELEASE_DIR/tools/linux-made-sane-desktop-helper-setup" \
    "$DESKTOP_HELPER_MANAGER_FILE" \
    "$DESKTOP_HELPER_MANAGER_PATH" \
    "$DESKTOP_HELPER_MANAGER_CONFIG_FILE" \
    "$DESKTOP_HELPER_MANAGER_CONFIG_PATH" \
    "$DESKTOP_HELPER_PREFERENCE_PATH" \
    "$DESKTOP_HELPER_SUDOERS_FILE" \
    "$SERVICE_USER" \
    "$DESKTOP_HELPER_UNIT" \
    "/etc/systemd/user/$DESKTOP_HELPER_UNIT" \
    "/etc/xdg/autostart/linux-made-sane-desktop-helper.desktop" \
    "$DESKTOP_HELPER_LAUNCHER_PATH" \
    "$INSTALL_ROOT/current/desktop-helper/LinuxMadeSane.DesktopHelper" \
    "$DESKTOP_HELPER_SOCKET_PATH" \
    "$DESKTOP_HELPER_LOCAL_LMS_URL" \
    "$INSTALL_ROOT/current/wwwroot/images/lms-logo-192.png" \
    "$SERVICE_GROUP" \
    "$LMS_INSTALLER_USERNAME" \
    "$RELEASE_DIR/tools/linux-made-sane-desktop-helper-launcher" \
    "$DESKTOP_HELPER_LAUNCHER_FILE" \
    "$INSTALL_ROOT/current/desktop-helper" \
    "$INSTALL_ROOT/current/optional/desktop-helper.tar.gz" \
    "$INSTALL_ROOT"; then
    lms_log "Desktop Helper setup command could not be installed; the core LMS install will continue"
  fi
fi

lms_write_update_helper \
  "$SERVICE_USER" \
  "$LMS_BASE_URL" \
  "$UPDATE_HELPER_PATH" \
  "$INSTALL_ROOT" \
  "$DATA_ROOT" \
  "$CONFIG_ROOT" \
  "$SERVICE_GROUP" \
  "$SERVICE_UNIT" \
  "$SERVICE_PORT" \
  "$LMS_DATABASE_CONNECTION_STRING" \
  "$LMS_DATA_PROTECTION_KEY_DIRECTORY"

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
      lms_log "Continuing because the LMS web service is running; Desktop Assistant can be repaired from Setup."
    fi
  fi
fi
lms_enable_caddy_service
ROLLBACK_ARMED=false
trap - ERR
if [[ -n "$PREVIOUS_ENV_FILE" && -f "$PREVIOUS_ENV_FILE" ]]; then
  rm -f -- "$PREVIOUS_ENV_FILE"
fi

if [[ "$LMS_DEST_ROOT" == "" && -x "$DESKTOP_HELPER_MANAGER_FILE" ]]; then
  if ! "$DESKTOP_HELPER_MANAGER_FILE" "$([[ "$INSTALL_DESKTOP_HELPER" == "true" ]] && printf enable || printf disable)"; then
    lms_log "Desktop Helper choice could not be applied; the verified core LMS service remains installed"
  fi
elif [[ "$LMS_DEST_ROOT" != "" ]]; then
  mkdir -p "$(dirname "$DESKTOP_HELPER_PREFERENCE_FILE")"
  printf '%s\n' "$INSTALL_DESKTOP_HELPER" > "$DESKTOP_HELPER_PREFERENCE_FILE"
fi

lms_log "CE install complete"
printf 'version: %s\nservice unit: %s\ninstall root: %s\ndata root: %s\nconfig file: %s\nport: %s\n' \
  "$PACKAGE_VERSION" "$SERVICE_UNIT" "$INSTALL_ROOT_ABS" "$DATA_ROOT_ABS" "$ENV_FILE" "$SERVICE_PORT"
printf 'local SSH runner: %s@localhost:22\n' "$RUNNER_USER"
if [[ "$INSTALL_DESKTOP_HELPER" == "true" && -f "$DESKTOP_HELPER_UNIT_FILE" ]]; then
  printf 'desktop helper: %s\n' "$DESKTOP_HELPER_UNIT_FILE"
  printf 'desktop helper socket: %s\n' "$DESKTOP_HELPER_SOCKET_PATH"
  printf 'desktop tray URL: %s\n' "$DESKTOP_HELPER_LOCAL_LMS_URL"
  printf 'start helper in the current GUI session: systemctl --user enable --now %s\n' "$DESKTOP_HELPER_UNIT"
else
  printf 'desktop helper: disabled (enable it from Desktop Assistant > Setup)\n'
fi
lms_print_access_urls "$SERVICE_PORT"
printf 'update command: sudo %s\n' "$UPDATE_HELPER_PATH"
