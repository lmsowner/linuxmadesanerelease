#!/usr/bin/env bash

# Copyright (c) Richard D. Kiernan.
# Licensed under the Business Source License 1.1. See LICENSE for details.

set -euo pipefail

CONFIG_FILE="${LMS_DESKTOP_HELPER_CONFIG_FILE:-/etc/linuxmadesane/ce-desktop-helper.conf}"

if [[ -f "$CONFIG_FILE" ]]; then
  # The installer owns this file and writes shell-quoted values only.
  # shellcheck disable=SC1090
  source "$CONFIG_FILE"
fi

PREFERENCE_FILE="${LMS_DESKTOP_HELPER_PREFERENCE_FILE:-/etc/linuxmadesane/ce-desktop-helper.enabled}"
UNIT_NAME="${LMS_DESKTOP_HELPER_UNIT:-linux-made-sane-desktop-helper.service}"
UNIT_FILE="${LMS_DESKTOP_HELPER_UNIT_FILE:-/etc/systemd/user/$UNIT_NAME}"
AUTOSTART_FILE="${LMS_DESKTOP_HELPER_AUTOSTART_FILE:-/etc/xdg/autostart/linux-made-sane-desktop-helper.desktop}"
LAUNCHER_PATH="${LMS_DESKTOP_HELPER_LAUNCHER_PATH:-/usr/local/lib/linuxmadesane/linux-made-sane-desktop-helper-launcher}"
EXECUTABLE_PATH="${LMS_DESKTOP_HELPER_EXECUTABLE_PATH:-/opt/linuxmadesane/ce/current/desktop-helper/LinuxMadeSane.DesktopHelper}"
HELPER_DIRECTORY="${LMS_DESKTOP_HELPER_DIRECTORY:-/opt/linuxmadesane/ce/current/desktop-helper}"
PAYLOAD_ARCHIVE="${LMS_DESKTOP_HELPER_PAYLOAD_ARCHIVE:-/opt/linuxmadesane/ce/current/optional/desktop-helper.tar.gz}"
INSTALL_ROOT="${LMS_DESKTOP_HELPER_INSTALL_ROOT:-/opt/linuxmadesane/ce}"
SOCKET_PATH="${LMS_DESKTOP_HELPER_SOCKET_PATH:-/run/linuxmadesane/desktop-session.sock}"
LOCAL_LMS_URL="${LMS_DESKTOP_HELPER_LOCAL_LMS_URL:-http://127.0.0.1:5080/desktop-assistant}"
TRAY_ICON_PATH="${LMS_DESKTOP_HELPER_TRAY_ICON_PATH:-/opt/linuxmadesane/ce/current/wwwroot/images/lms-logo-192.png}"
SERVICE_GROUP="${LMS_DESKTOP_HELPER_SERVICE_GROUP:-linuxmadesane}"
INSTALLER_USERNAME="${LMS_DESKTOP_HELPER_INSTALLER_USERNAME:-}"

log() {
  printf '[lms-desktop-helper] %s\n' "$*" >&2
}

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

has_systemd() {
  command -v systemctl >/dev/null 2>&1 && [[ -d /run/systemd/system ]]
}

is_truthy() {
  case "${1:-}" in
    true|True|TRUE|1|yes|Yes|YES|on|On|ON|enabled|Enabled|ENABLED) return 0 ;;
    *) return 1 ;;
  esac
}

is_enabled() {
  if [[ -f "$PREFERENCE_FILE" ]]; then
    is_truthy "$(sed -n '1p' "$PREFERENCE_FILE" 2>/dev/null | tr -d '\r\n')"
    return
  fi

  return 1
}

graphical_desktop_status() {
  if ! has_systemd; then
    printf 'managed=false\n'
    printf 'default-target=unknown\n'
    printf 'display-manager=unavailable\n'
    printf 'display-manager-active=false\n'
    return
  fi

  local default_target display_manager display_manager_active=false
  default_target="$(systemctl get-default 2>/dev/null || printf 'unknown')"
  display_manager="$(systemctl show display-manager.service -p Id --value 2>/dev/null || true)"
  [[ -n "$display_manager" ]] || display_manager="not installed"
  if systemctl is-active --quiet display-manager.service 2>/dev/null; then
    display_manager_active=true
  fi

  printf 'managed=true\n'
  printf 'default-target=%s\n' "$default_target"
  printf 'display-manager=%s\n' "$display_manager"
  printf 'display-manager-active=%s\n' "$display_manager_active"
}

disable_graphical_desktop() {
  require_root
  has_systemd || die "systemd is required to disable graphical desktop startup"

  # A headless host must not retain a graphical-session agent that can be
  # restarted by a user unit or XDG autostart entry on a later login.
  disable_helper >/dev/null
  systemctl set-default multi-user.target >/dev/null
  if systemctl is-active --quiet display-manager.service 2>/dev/null; then
    systemctl stop display-manager.service
  fi

  graphical_desktop_status
}

enable_graphical_desktop() {
  require_root
  has_systemd || die "systemd is required to enable graphical desktop startup"

  systemctl set-default graphical.target >/dev/null
  if systemctl cat display-manager.service >/dev/null 2>&1; then
    systemctl start display-manager.service
  else
    log "Graphical boot target enabled, but no display manager is installed"
  fi

  graphical_desktop_status
}

validate_helper_directory() {
  [[ "$INSTALL_ROOT" == /* && "$INSTALL_ROOT" != "/" ]] ||
    die "refusing unsafe LMS install root: $INSTALL_ROOT"
  [[ "$HELPER_DIRECTORY" == "${INSTALL_ROOT%/}/current/desktop-helper" ]] ||
    die "refusing unexpected Desktop Helper directory: $HELPER_DIRECTORY"
}

validate_payload_archive() {
  [[ -f "$PAYLOAD_ARCHIVE" ]] || die "Desktop Helper payload archive is missing from $PAYLOAD_ARCHIVE"
  command -v tar >/dev/null 2>&1 || die "tar is required to enable Desktop Helper"
  tar -tzf "$PAYLOAD_ARCHIVE" >/dev/null || die "Desktop Helper payload archive is invalid"

  local entry
  while IFS= read -r entry; do
    [[ -n "$entry" ]] || continue
    case "$entry" in
      /*|../*|*/../*|*/..) die "Desktop Helper payload contains an unsafe path: $entry" ;;
    esac
  done < <(tar -tzf "$PAYLOAD_ARCHIVE")

  if tar -tvzf "$PAYLOAD_ARCHIVE" | awk 'substr($1,1,1) == "l" || substr($1,1,1) == "h" { found=1 } END { exit found ? 0 : 1 }'; then
    die "Desktop Helper payload contains links and was rejected"
  fi
}

install_helper_payload() {
  [[ -x "$EXECUTABLE_PATH" ]] && return 0
  validate_helper_directory
  validate_payload_archive

  local helper_parent temp_directory
  helper_parent="$(dirname "$HELPER_DIRECTORY")"
  mkdir -p "$helper_parent"
  temp_directory="$(mktemp -d "$helper_parent/.desktop-helper.XXXXXX")"
  if ! tar --no-same-owner --no-same-permissions -xzf "$PAYLOAD_ARCHIVE" -C "$temp_directory"; then
    rm -rf -- "$temp_directory"
    die "Desktop Helper payload could not be extracted"
  fi
  if [[ ! -x "$temp_directory/LinuxMadeSane.DesktopHelper" ]]; then
    rm -rf -- "$temp_directory"
    die "Desktop Helper payload does not contain its executable"
  fi

  rm -rf -- "$HELPER_DIRECTORY"
  mv "$temp_directory" "$HELPER_DIRECTORY"
}

remove_helper_payload() {
  validate_helper_directory
  [[ -e "$HELPER_DIRECTORY" || -L "$HELPER_DIRECTORY" ]] || return 0
  rm -rf -- "$HELPER_DIRECTORY"
}

require_root() {
  [[ "$(id -u)" -eq 0 ]] || die "this host setup action must run as root"
}

write_preference() {
  local value="$1"
  local preference_dir temp_file
  preference_dir="$(dirname "$PREFERENCE_FILE")"
  mkdir -p "$preference_dir"
  temp_file="$(mktemp "$preference_dir/.desktop-helper.enabled.XXXXXX")"
  printf '%s\n' "$value" > "$temp_file"
  chmod 0644 "$temp_file"
  chown root:root "$temp_file" 2>/dev/null || true
  mv -f "$temp_file" "$PREFERENCE_FILE"
}

run_as_user() {
  local username="$1"
  shift

  if command -v runuser >/dev/null 2>&1; then
    runuser -u "$username" -- "$@"
    return
  fi

  if command -v sudo >/dev/null 2>&1; then
    sudo -u "$username" "$@"
    return
  fi

  return 127
}

add_user() {
  local candidate="$1"
  local -n users_ref="$2"
  local existing

  [[ -n "$candidate" && "$candidate" != "root" ]] || return 0
  id "$candidate" >/dev/null 2>&1 || return 0
  for existing in "${users_ref[@]}"; do
    [[ "$existing" != "$candidate" ]] || return 0
  done
  users_ref+=("$candidate")
}

collect_active_desktop_users() {
  local -n users_ref="$1"
  local session_id username session_class session_state session_type bus_path user_id user_unit

  add_user "$INSTALLER_USERNAME" users_ref

  for bus_path in /run/user/[0-9]*/bus; do
    [[ -S "$bus_path" ]] || continue
    user_id="${bus_path#/run/user/}"
    user_id="${user_id%/bus}"
    username="$(getent passwd "$user_id" 2>/dev/null | cut -d: -f1)"
    add_user "$username" users_ref
  done

  if has_systemd; then
    while read -r user_unit _; do
      [[ "$user_unit" =~ ^user@([0-9]+)\.service$ ]] || continue
      user_id="${BASH_REMATCH[1]}"
      username="$(getent passwd "$user_id" 2>/dev/null | cut -d: -f1)"
      add_user "$username" users_ref
    done < <(systemctl list-units --type=service --state=running 'user@*.service' --no-legend --plain 2>/dev/null || true)
  fi

  command -v loginctl >/dev/null 2>&1 || return 0

  while read -r session_id _; do
    [[ -n "$session_id" ]] || continue
    username="$(loginctl show-session "$session_id" -p Name --value 2>/dev/null || true)"
    session_class="$(loginctl show-session "$session_id" -p Class --value 2>/dev/null || true)"
    session_state="$(loginctl show-session "$session_id" -p State --value 2>/dev/null || true)"
    session_type="$(loginctl show-session "$session_id" -p Type --value 2>/dev/null || true)"
    [[ "$session_class" == "user" ]] || continue
    [[ "$session_state" == "active" || "$session_state" == "online" || "$session_state" == "closing" ]] || continue
    [[ "$session_type" == "x11" || "$session_type" == "wayland" || "$session_type" == "mir" || -z "$session_type" ]] || continue
    add_user "$username" users_ref
  done < <(loginctl list-sessions --no-legend 2>/dev/null || true)
}

user_systemctl() {
  local username="$1"
  shift
  local user_id runtime_dir bus_path
  user_id="$(id -u "$username" 2>/dev/null || true)"
  runtime_dir="/run/user/$user_id"
  bus_path="$runtime_dir/bus"
  if [[ -n "$user_id" && -d "$runtime_dir" && -S "$bus_path" ]]; then
    if run_as_user "$username" env \
      "XDG_RUNTIME_DIR=$runtime_dir" \
      "DBUS_SESSION_BUS_ADDRESS=unix:path=$bus_path" \
      systemctl --user "$@"; then
      return 0
    fi
  fi

  # Display-manager accounts can keep a systemd user manager alive without a
  # user bus path. Root can still address that manager through systemd's host
  # machine transport, which lets disable stop greeter-owned helper instances.
  if [[ "$(id -u)" -eq 0 ]] && has_systemd; then
    systemctl --user --machine="$username@.host" "$@"
    return
  fi

  return 1
}

grant_socket_access() {
  local username="$1"
  getent group "$SERVICE_GROUP" >/dev/null 2>&1 || return 0
  usermod -a -G "$SERVICE_GROUP" "$username" >/dev/null 2>&1 ||
    log "Could not add $username to the $SERVICE_GROUP group"
}

start_for_user() {
  local username="$1"
  grant_socket_access "$username"
  user_systemctl "$username" daemon-reload >/dev/null 2>&1 || return 1
  user_systemctl "$username" enable --now "$UNIT_NAME" >/dev/null 2>&1 || return 1
  user_systemctl "$username" restart "$UNIT_NAME" >/dev/null 2>&1 || return 1
  log "Started Desktop Helper for $username"
}

stop_for_user() {
  local username="$1"
  user_systemctl "$username" disable --now "$UNIT_NAME" >/dev/null 2>&1 || true
}

disarm_per_user_launch_files() {
  command -v getent >/dev/null 2>&1 || return 0
  local username _ uid gid gecos home shell link local_unit local_autostart backup_dir timestamp moved
  while IFS=: read -r username _ uid gid gecos home shell; do
    [[ -n "$home" && "$home" != "/" ]] || continue
    if [[ -d "$home/.config/systemd/user" ]]; then
      while IFS= read -r -d '' link; do
        rm -f -- "$link"
      done < <(find "$home/.config/systemd/user" -type l -name "$UNIT_NAME" -print0 2>/dev/null)
    fi

    local_unit="$home/.config/systemd/user/$UNIT_NAME"
    local_autostart="$home/.config/autostart/linux-made-sane-desktop-helper.desktop"
    moved=false
    backup_dir="$home/.local/share/linuxmadesane/disabled-desktop-helper"
    timestamp="$(date -u +%Y%m%d%H%M%S)-$$"
    if [[ -e "$local_unit" || -L "$local_unit" ]]; then
      mkdir -p "$backup_dir"
      mv -f -- "$local_unit" "$backup_dir/$UNIT_NAME.$timestamp"
      moved=true
    fi
    if [[ -e "$local_autostart" || -L "$local_autostart" ]]; then
      mkdir -p "$backup_dir"
      mv -f -- "$local_autostart" "$backup_dir/linux-made-sane-desktop-helper.desktop.$timestamp"
      moved=true
    fi
    if [[ "$moved" == "true" ]]; then
      chown -R "$uid:$gid" "$backup_dir" >/dev/null 2>&1 || true
      log "Disabled per-user Desktop Helper launch files for $username"
    fi
  done < <(getent passwd)
}

stop_all_helper_processes() {
  local process_link process_id executable
  local -a process_ids=()

  for process_link in /proc/[0-9]*/exe; do
    executable="$(readlink "$process_link" 2>/dev/null || true)"
    executable="${executable% (deleted)}"
    [[ "${executable##*/}" == "LinuxMadeSane.DesktopHelper" ]] || continue
    process_id="${process_link#/proc/}"
    process_id="${process_id%/exe}"
    [[ "$process_id" =~ ^[0-9]+$ ]] || continue
    process_ids+=("$process_id")
    kill -TERM "$process_id" 2>/dev/null || true
  done

  for _ in {1..50}; do
    local still_running=false
    for process_id in "${process_ids[@]}"; do
      executable="$(readlink "/proc/$process_id/exe" 2>/dev/null || true)"
      executable="${executable% (deleted)}"
      if [[ "${executable##*/}" == "LinuxMadeSane.DesktopHelper" ]]; then
        still_running=true
        break
      fi
    done
    [[ "$still_running" == "true" ]] || return 0
    sleep 0.1
  done

  for process_id in "${process_ids[@]}"; do
    executable="$(readlink "/proc/$process_id/exe" 2>/dev/null || true)"
    executable="${executable% (deleted)}"
    if [[ "${executable##*/}" == "LinuxMadeSane.DesktopHelper" ]]; then
      kill -KILL "$process_id" 2>/dev/null || true
    fi
  done
}

write_autostart_file() {
  local autostart_dir temp_file
  autostart_dir="$(dirname "$AUTOSTART_FILE")"
  mkdir -p "$autostart_dir"
  temp_file="$(mktemp "$autostart_dir/.linux-made-sane-desktop-helper.XXXXXX")"
  cat > "$temp_file" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Linux Made Sane Desktop Helper
Comment=Connects this graphical user session to the local LMS service
Exec=env LMS_DESKTOP_HELPER_SOCKET=$SOCKET_PATH LMS_DESKTOP_HELPER_LMS_URL=$LOCAL_LMS_URL LMS_DESKTOP_HELPER_TRAY_ICON=$TRAY_ICON_PATH $LAUNCHER_PATH
Terminal=false
X-GNOME-Autostart-enabled=true
DESKTOP
  chmod 0644 "$temp_file"
  chown root:root "$temp_file" 2>/dev/null || true
  mv -f "$temp_file" "$AUTOSTART_FILE"
}

enable_helper() {
  require_root
  install_helper_payload
  [[ -x "$LAUNCHER_PATH" ]] || die "Desktop Helper launcher is missing from $LAUNCHER_PATH"
  [[ -f "$UNIT_FILE" ]] || die "Desktop Helper unit is missing from $UNIT_FILE"

  write_autostart_file
  if has_systemd; then
    systemctl --global enable "$UNIT_NAME" >/dev/null 2>&1 ||
      log "Could not enable the user unit globally; desktop autostart remains configured"
  fi

  local username
  local -a users=()
  collect_active_desktop_users users
  for username in "${users[@]}"; do
    start_for_user "$username" ||
      log "Desktop Helper will start for $username at the next graphical login"
  done

  write_preference true
  printf 'enabled\n'
}

disable_helper() {
  require_root
  local username
  local -a users=()

  # Persist off before touching launch state so an interrupted disable cannot
  # be mistaken for opt-in by a concurrent installer or update.
  write_preference false
  if has_systemd; then
    systemctl --global disable "$UNIT_NAME" >/dev/null 2>&1 || true
  fi
  collect_active_desktop_users users
  for username in "${users[@]}"; do
    stop_for_user "$username"
  done
  disarm_per_user_launch_files
  rm -f -- "$AUTOSTART_FILE"
  stop_all_helper_processes
  remove_helper_payload
  printf 'disabled\n'
}

case "${1:-status}" in
  status)
    if is_enabled; then
      printf 'enabled\n'
    else
      printf 'disabled\n'
    fi
    ;;
  enable)
    enable_helper
    ;;
  disable)
    disable_helper
    ;;
  desktop-status)
    graphical_desktop_status
    ;;
  desktop-disable)
    disable_graphical_desktop
    ;;
  desktop-enable)
    enable_graphical_desktop
    ;;
  *)
    die "usage: $(basename "$0") [status|enable|disable|desktop-status|desktop-disable|desktop-enable]"
    ;;
esac
