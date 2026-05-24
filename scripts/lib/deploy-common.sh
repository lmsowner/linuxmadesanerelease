#!/usr/bin/env bash

# Copyright (c) Richard D. Kiernan.
# Licensed under the Business Source License 1.1. See LICENSE for details.


set -euo pipefail

lms_repo_root() {
  local script_dir
  script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  cd "$script_dir/../.." >/dev/null 2>&1 && pwd
}

lms_log() {
  printf '[lms] %s\n' "$*"
}

lms_die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

lms_require_command() {
  command -v "$1" >/dev/null 2>&1 || lms_die "required command not found: $1"
}

lms_shell_quote() {
  printf '%q' "$1"
}

lms_is_truthy() {
  case "${1:-}" in
    true|True|TRUE|1|yes|Yes|YES|on|On|ON) return 0 ;;
    *) return 1 ;;
  esac
}

lms_has_systemd() {
  command -v systemctl >/dev/null 2>&1 && [[ -d /run/systemd/system ]]
}

lms_install_host_packages() {
  local packages=("$@")
  [[ ${#packages[@]} -gt 0 ]] || return

  case "${INSTALL_SYSTEM_PACKAGES:-true}" in
    false|False|FALSE|0|no|No|NO|off|Off|OFF)
      lms_log "Skipping host package install: ${packages[*]}"
      return
      ;;
  esac

  if [[ "${LMS_DEST_ROOT:-}" != "" ]]; then
    lms_log "Skipping host package install while staging under LMS_DEST_ROOT: ${packages[*]}"
    return
  fi

  if [[ "$(id -u)" -ne 0 ]]; then
    lms_log "Skipping host package install because the installer is not running as root: ${packages[*]}"
    return
  fi

  if ! command -v apt-get >/dev/null 2>&1; then
    lms_log "No supported package manager found. Install manually: ${packages[*]}"
    return
  fi

  local missing=()
  local package
  if command -v dpkg-query >/dev/null 2>&1; then
    for package in "${packages[@]}"; do
      if ! dpkg-query -W -f='${Status}' "$package" 2>/dev/null | grep -q 'install ok installed'; then
        missing+=("$package")
      fi
    done
  else
    missing=("${packages[@]}")
  fi

  if [[ ${#missing[@]} -eq 0 ]]; then
    lms_log "Host packages already installed: ${packages[*]}"
    return
  fi

  lms_log "Installing host packages: ${missing[*]}"
  DEBIAN_FRONTEND=noninteractive apt-get update
  DEBIAN_FRONTEND=noninteractive apt-get install -y -- "${missing[@]}"
}

lms_install_optional_host_packages() {
  local packages=("$@")
  [[ ${#packages[@]} -gt 0 ]] || return

  case "${INSTALL_SYSTEM_PACKAGES:-true}" in
    false|False|FALSE|0|no|No|NO|off|Off|OFF)
      lms_log "Skipping optional host package install: ${packages[*]}"
      return
      ;;
  esac

  if [[ "${LMS_DEST_ROOT:-}" != "" ]]; then
    lms_log "Skipping optional host package install while staging under LMS_DEST_ROOT: ${packages[*]}"
    return
  fi

  if [[ "$(id -u)" -ne 0 ]]; then
    lms_log "Skipping optional host package install because the installer is not running as root: ${packages[*]}"
    return
  fi

  if ! command -v apt-get >/dev/null 2>&1; then
    lms_log "No supported package manager found for optional host packages: ${packages[*]}"
    return
  fi

  local missing=()
  local package
  if command -v dpkg-query >/dev/null 2>&1; then
    for package in "${packages[@]}"; do
      if ! dpkg-query -W -f='${Status}' "$package" 2>/dev/null | grep -q 'install ok installed'; then
        missing+=("$package")
      fi
    done
  else
    missing=("${packages[@]}")
  fi

  if [[ ${#missing[@]} -eq 0 ]]; then
    lms_log "Optional host packages already installed: ${packages[*]}"
    return
  fi

  local available=()
  if command -v apt-cache >/dev/null 2>&1; then
    for package in "${missing[@]}"; do
      if apt-cache show "$package" >/dev/null 2>&1; then
        available+=("$package")
      else
        lms_log "Optional host package is not available from configured apt sources: $package"
      fi
    done
  else
    available=("${missing[@]}")
  fi

  [[ ${#available[@]} -gt 0 ]] || return
  lms_log "Installing optional host packages: ${available[*]}"
  DEBIAN_FRONTEND=noninteractive apt-get update || {
    lms_log "Could not refresh apt metadata for optional host packages"
    return
  }

  for package in "${available[@]}"; do
    DEBIAN_FRONTEND=noninteractive apt-get install -y -- "$package" ||
      lms_log "Optional host package could not be installed: $package"
  done
}

lms_enable_openssh_server() {
  if [[ "${LMS_DEST_ROOT:-}" != "" ]]; then
    return
  fi

  if ! lms_has_systemd; then
    lms_log "systemd not detected; skipping OpenSSH service enable/start"
    return
  fi

  if systemctl list-unit-files ssh.service >/dev/null 2>&1; then
    lms_log "Enabling OpenSSH server"
    systemctl enable --now ssh.service
    return
  fi

  if systemctl list-unit-files sshd.service >/dev/null 2>&1; then
    lms_log "Enabling OpenSSH server"
    systemctl enable --now sshd.service
    return
  fi

  lms_log "OpenSSH server unit was not found; install or start sshd before using local terminal sessions"
}

lms_remove_caddy_packaged_default_site() {
  local file="/etc/caddy/Caddyfile"
  local backup
  local temp_file

  [[ "${LMS_DEST_ROOT:-}" == "" ]] || return 0
  [[ "$(id -u)" -eq 0 ]] || return 0
  [[ -f "$file" ]] || return 0

  temp_file="$(mktemp)"
  awk '
function delta(line, tmp, opens, closes) {
  tmp=line; opens=gsub(/\{/, "{", tmp);
  tmp=line; closes=gsub(/\}/, "}", tmp);
  return opens - closes;
}
BEGIN { skip=0; depth=0; block=""; has_root=0; has_file_server=0; }
skip == 0 && $0 ~ /^[[:space:]]*:80[[:space:]]*\{/ {
  skip=1; depth=delta($0); block=$0 ORS; has_root=0; has_file_server=0; next;
}
skip == 1 {
  block=block $0 ORS;
  if ($0 ~ /^[[:space:]]*root[[:space:]]+\*[[:space:]]+\/usr\/share\/caddy[[:space:]]*$/) has_root=1;
  if ($0 ~ /^[[:space:]]*file_server[[:space:]]*$/) has_file_server=1;
  depth += delta($0);
  if (depth <= 0) {
    if (!(has_root && has_file_server)) printf "%s", block;
    skip=0; depth=0; block="";
  }
  next;
}
{ print; }
END { if (skip == 1) printf "%s", block; }
' "$file" > "$temp_file"

  if cmp -s "$file" "$temp_file"; then
    rm -f "$temp_file"
    return 0
  fi

  backup="/etc/caddy/Caddyfile.lms-backup-$(date +%Y%m%d%H%M%S)"
  cp -a "$file" "$backup" || true
  install -m 0644 "$temp_file" "$file"
  rm -f "$temp_file"
  lms_log "Removed the packaged Caddy :80 static site from $file so LMS does not claim port 80 by default"
}

lms_enable_caddy_service() {
  if [[ "${LMS_DEST_ROOT:-}" != "" ]]; then
    return
  fi

  if ! lms_has_systemd; then
    lms_log "systemd not detected; skipping Caddy service enable/start"
    return
  fi

  if [[ "$(id -u)" -ne 0 ]]; then
    lms_log "Skipping Caddy service enable/start because the installer is not running as root"
    return
  fi

  if ! systemctl list-unit-files caddy.service >/dev/null 2>&1; then
    lms_log "Caddy service unit was not found; Edge Gateway will report Caddy as unavailable"
    return
  fi

  lms_remove_caddy_packaged_default_site

  lms_log "Enabling Caddy service"
  if ! systemctl enable caddy.service >/dev/null 2>&1; then
    lms_log "Caddy service is installed, but systemd could not enable it"
    return
  fi

  if systemctl restart caddy.service >/dev/null 2>&1; then
    return
  fi

  lms_log "Caddy service is installed and enabled, but it did not start cleanly; Edge Gateway will show the Caddy status inside LMS"
  systemctl show caddy.service -p ActiveState -p SubState -p Result -p ExecMainStatus 2>/dev/null |
    sed 's/^/[lms] Caddy /' >&2 || true
  if [[ "${LMS_VERBOSE:-}" == "1" ]]; then
    systemctl --no-pager --full status caddy.service >&2 || true
  fi
}

lms_prepare_desktop_session_socket_directory() {
  local socket_path="$1"
  local service_user="$2"
  local service_group="$3"
  local socket_dir

  [[ "${LMS_DEST_ROOT:-}" == "" ]] || return 0
  [[ "$(id -u)" -eq 0 ]] || return 0
  [[ -n "$socket_path" ]] || return 0

  socket_dir="$(dirname "$socket_path")"
  case "$socket_dir" in
    /run/linuxmadesane|/run/linuxmadesane/*)
      ;;
    *)
      lms_log "Desktop Assistant broker socket directory is custom; systemd must prepare $socket_dir"
      return 0
      ;;
  esac

  mkdir -p "$socket_dir"
  chown "$service_user:$service_group" "$socket_dir" 2>/dev/null || true
  chmod 0755 "$socket_dir" 2>/dev/null || true
}

lms_wait_for_file_socket() {
  local socket_path="$1"
  local timeout_seconds="${2:-30}"

  [[ -n "$socket_path" ]] || return 1

  for _ in $(seq 1 "$timeout_seconds"); do
    if [[ -S "$socket_path" ]]; then
      return 0
    fi

    sleep 1
  done

  return 1
}

lms_prepare_local_ssh_runner() {
  local service_user="$1"
  local service_group="$2"
  local config_root="$3"
  local runner_user="$4"
  local runner_group="$5"
  local runner_home="$6"
  local enable_local_sudo="${7:-true}"
  local runner_workspace="${8:-$runner_home/workspace}"

  if [[ "${LMS_DEST_ROOT:-}" != "" ]]; then
    lms_log "Skipping localhost SSH runner while staging under LMS_DEST_ROOT"
    return
  fi

  lms_require_command useradd
  lms_require_command ssh-keygen

  if getent group "$runner_group" >/dev/null 2>&1; then
    :
  else
    groupadd --system "$runner_group"
  fi

  if id -u "$runner_user" >/dev/null 2>&1; then
    usermod --shell /bin/bash "$runner_user" >/dev/null 2>&1 || true
    mkdir -p "$runner_home"
  else
    useradd \
      --system \
      --gid "$runner_group" \
      --home-dir "$runner_home" \
      --create-home \
      --shell /bin/bash \
      "$runner_user"
  fi

  passwd -l "$runner_user" >/dev/null 2>&1 || true

  local ssh_dir="$runner_home/.ssh"
  local workspace_dir="$runner_workspace"
  local key_dir="$config_root/ssh"
  local key_file="$key_dir/lms_local_runner_ed25519"
  local public_key_file="$key_file.pub"
  local authorized_keys="$ssh_dir/authorized_keys"

  mkdir -p "$ssh_dir" "$workspace_dir" "$key_dir"
  if [[ ! -s "$key_file" || ! -s "$public_key_file" ]]; then
    rm -f "$key_file" "$public_key_file"
    ssh-keygen -q -t ed25519 -N "" -C "linux-made-sane-local-runner@$(hostname 2>/dev/null || printf localhost)" -f "$key_file"
  fi

  touch "$authorized_keys"
  if ! grep -Fxq "$(cat "$public_key_file")" "$authorized_keys"; then
    cat "$public_key_file" >> "$authorized_keys"
    printf '\n' >> "$authorized_keys"
  fi

  chown -R "$runner_user:$runner_group" "$runner_home"
  chown -R "$runner_user:$runner_group" "$workspace_dir"
  usermod -a -G "$runner_group" "$service_user" >/dev/null 2>&1 || true
  chmod 750 "$runner_home"
  chmod 770 "$workspace_dir"
  chmod 700 "$ssh_dir"
  chmod 600 "$authorized_keys"
  chown -R "$service_user:$service_group" "$key_dir"
  chmod 750 "$key_dir"
  chmod 600 "$key_file"
  chmod 644 "$public_key_file"

  if lms_is_truthy "$enable_local_sudo"; then
    lms_write_local_sudoers "$service_user" "$runner_user"
  fi

  lms_log "Prepared localhost SSH runner $runner_user with key-only authentication"
}

lms_write_local_sudoers() {
  local service_user="$1"
  local runner_user="$2"
  local sudoers_file="/etc/sudoers.d/linux-made-sane"

  command -v sudo >/dev/null 2>&1 || {
    lms_log "sudo is not installed; skipping passwordless sudo policy"
    return
  }

  cat > "$sudoers_file" <<SUDOERS
$service_user ALL=(ALL) NOPASSWD:ALL
$runner_user ALL=(ALL) NOPASSWD:ALL
SUDOERS
  chmod 440 "$sudoers_file"

  if command -v visudo >/dev/null 2>&1; then
    visudo -cf "$sudoers_file" >/dev/null
  fi
}

lms_detect_installer_identity() {
  local username="${SUDO_USER:-${USER:-}}"
  if [[ -z "$username" ]]; then
    username="$(id -un 2>/dev/null || true)"
  fi

  LMS_INSTALLER_USERNAME="$username"
  LMS_INSTALLER_UID=""
  LMS_INSTALLER_HOME=""
  LMS_INSTALLER_SHELL=""
  LMS_INSTALLER_INSTALLED_AT_UTC="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

  if [[ -n "$username" ]]; then
    LMS_INSTALLER_UID="$(id -u "$username" 2>/dev/null || true)"
    if command -v getent >/dev/null 2>&1; then
      local passwd_entry
      passwd_entry="$(getent passwd "$username" 2>/dev/null || true)"
      if [[ -n "$passwd_entry" ]]; then
        LMS_INSTALLER_HOME="$(printf '%s' "$passwd_entry" | cut -d: -f6)"
        LMS_INSTALLER_SHELL="$(printf '%s' "$passwd_entry" | cut -d: -f7)"
      fi
    fi
  fi
}

lms_prepare_desktop_helper_access() {
  local service_group="$1"

  if [[ "${LMS_DEST_ROOT:-}" != "" ]]; then
    lms_log "Skipping desktop helper group membership while staging under LMS_DEST_ROOT"
    return
  fi

  if [[ "$(id -u)" -ne 0 ]]; then
    lms_log "Skipping desktop helper group membership because the installer is not running as root"
    return
  fi

  local username="${LMS_INSTALLER_USERNAME:-}"
  if [[ -z "$username" || "$username" == "root" ]]; then
    lms_log "Desktop helper installed; add each GUI user that should report sessions to the $service_group group"
    return
  fi

  if ! id "$username" >/dev/null 2>&1; then
    lms_log "Desktop helper installed; installer user $username was not found for group membership"
    return
  fi

  usermod -a -G "$service_group" "$username" >/dev/null 2>&1 || true
  lms_log "Granted $username access to the LMS desktop helper socket via group $service_group"
  lms_log "Desktop helper launcher will use the new group membership immediately when possible"
}

lms_disable_user_debug_desktop_helper_files() {
  local username="$1"
  local unit_name="$2"
  local user_home="${LMS_INSTALLER_HOME:-}"
  local passwd_entry primary_group backup_dir timestamp moved=false
  local local_unit local_autostart wants_link

  if [[ -z "$user_home" && -n "$username" ]] && command -v getent >/dev/null 2>&1; then
    passwd_entry="$(getent passwd "$username" 2>/dev/null || true)"
    if [[ -n "$passwd_entry" ]]; then
      user_home="$(printf '%s' "$passwd_entry" | cut -d: -f6)"
    fi
  fi

  [[ -n "$user_home" && -d "$user_home" ]] || return 0

  backup_dir="$user_home/.local/share/linuxmadesane/disabled-desktop-helper-debug"
  timestamp="$(date -u +%Y%m%d%H%M%S)"
  local_unit="$user_home/.config/systemd/user/$unit_name"
  wants_link="$user_home/.config/systemd/user/default.target.wants/$unit_name"
  local_autostart="$user_home/.config/autostart/linux-made-sane-desktop-helper.desktop"

  if [[ -f "$local_unit" ]] &&
     grep -Eq 'local debug|dev-desktop-helper-launcher|/tmp/linux-made-sane-desktop-session\.sock' "$local_unit"; then
    mkdir -p "$backup_dir"
    mv "$local_unit" "$backup_dir/$unit_name.$timestamp"
    rm -f "$wants_link"
    moved=true
  fi

  if [[ -f "$local_autostart" ]] &&
     grep -Eq 'local debug|dev-desktop-helper-launcher|/tmp/linux-made-sane-desktop-session\.sock' "$local_autostart"; then
    mkdir -p "$backup_dir"
    mv "$local_autostart" "$backup_dir/linux-made-sane-desktop-helper.desktop.$timestamp"
    moved=true
  fi

  if [[ "$moved" == "true" ]]; then
    primary_group="$(id -gn "$username" 2>/dev/null || true)"
    if [[ -n "$primary_group" ]]; then
      chown -R "$username:$primary_group" "$backup_dir" >/dev/null 2>&1 || true
    fi

    lms_log "Disabled stale local debug Desktop Assistant helper files for $username"
  fi
}

lms_write_desktop_helper_launcher() {
  local launcher_path="$1"
  local service_group="$2"
  local executable_path="$3"
  local socket_path="${4:-/run/linuxmadesane/desktop-session.sock}"
  local local_lms_url="${5:-http://127.0.0.1:5080/desktop-assistant}"
  local tray_icon_path="${6:-}"
  local launcher_dir executable_dir quoted_group quoted_executable quoted_executable_dir quoted_socket quoted_local_lms_url quoted_tray_icon

  launcher_dir="$(dirname "$launcher_path")"
  executable_dir="$(dirname "$executable_path")"
  quoted_group="$(lms_shell_quote "$service_group")"
  quoted_executable="$(lms_shell_quote "$executable_path")"
  quoted_executable_dir="$(lms_shell_quote "$executable_dir")"
  quoted_socket="$(lms_shell_quote "$socket_path")"
  quoted_local_lms_url="$(lms_shell_quote "$local_lms_url")"
  quoted_tray_icon="$(lms_shell_quote "$tray_icon_path")"

  mkdir -p "$launcher_dir"
  cat > "$launcher_path" <<LAUNCHER
#!/usr/bin/env bash
set -euo pipefail

export NO_AT_BRIDGE=1
export LMS_DESKTOP_HELPER_SOCKET=$quoted_socket
export LMS_DESKTOP_HELPER_LMS_URL=$quoted_local_lms_url
export LMS_DESKTOP_HELPER_TRAY_ICON=$quoted_tray_icon
open_window="\${LMS_DESKTOP_HELPER_OPEN_WINDOW:-false}"

if [[ "\${LMS_DESKTOP_HELPER_SG_ACTIVE:-}" != "1" ]] &&
   command -v sg >/dev/null 2>&1 &&
   getent group $quoted_group >/dev/null 2>&1 &&
   ! id -nG | tr ' ' '\\n' | grep -qx $quoted_group; then
  group_entry="\$(getent group $quoted_group)"
  group_members="\${group_entry##*:}"
  current_user="\$(id -un)"
  if [[ ",\$group_members," == *",\$current_user,"* ]]; then
    exec sg $quoted_group -c "LMS_DESKTOP_HELPER_SG_ACTIVE=1 LMS_DESKTOP_HELPER_OPEN_WINDOW=\$(printf '%q' "\$open_window") exec \$(printf '%q' "\$0")"
  fi
fi

cd $quoted_executable_dir
exec $quoted_executable
LAUNCHER
  chmod 755 "$launcher_path"
}

lms_run_as_user() {
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

lms_enable_desktop_helper_global_unit() {
  local unit_name="$1"

  if [[ "${LMS_DEST_ROOT:-}" != "" ]]; then
    return 0
  fi

  if [[ "$(id -u)" -ne 0 ]]; then
    return 0
  fi

  if ! lms_has_systemd; then
    return 0
  fi

  systemctl --global enable "$unit_name" >/dev/null 2>&1 ||
    lms_log "Desktop helper installed; could not enable the user service globally"
}

lms_add_desktop_helper_user() {
  local candidate="$1"
  local -n target_users="$2"
  local existing

  [[ -n "$candidate" && "$candidate" != "root" ]] || return 0
  id "$candidate" >/dev/null 2>&1 || return 0

  for existing in "${target_users[@]}"; do
    if [[ "$existing" == "$candidate" ]]; then
      return 0
    fi
  done

  target_users+=("$candidate")
}

lms_collect_desktop_helper_users() {
  local -n target_users="$1"
  local session_id username session_class session_state session_type

  lms_add_desktop_helper_user "${LMS_INSTALLER_USERNAME:-}" target_users

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
    lms_add_desktop_helper_user "$username" target_users
  done < <(loginctl list-sessions --no-legend 2>/dev/null || true)
}

lms_start_desktop_helper_for_user() {
  local username="$1"
  local unit_name="$2"
  local user_id runtime_dir bus_path

  if [[ -z "$username" || "$username" == "root" ]] || ! id "$username" >/dev/null 2>&1; then
    return 1
  fi

  user_id="$(id -u "$username" 2>/dev/null || true)"
  runtime_dir="/run/user/$user_id"
  bus_path="$runtime_dir/bus"
  if [[ -z "$user_id" || ! -d "$runtime_dir" || ! -S "$bus_path" ]]; then
    return 1
  fi

  local -a user_env=(
    "XDG_RUNTIME_DIR=$runtime_dir"
    "DBUS_SESSION_BUS_ADDRESS=unix:path=$bus_path"
  )

  lms_run_as_user "$username" env "${user_env[@]}" systemctl --user disable "$unit_name" >/dev/null 2>&1 || true
  lms_disable_user_debug_desktop_helper_files "$username" "$unit_name"

  lms_run_as_user "$username" env "${user_env[@]}" systemctl --user import-environment \
    DISPLAY \
    WAYLAND_DISPLAY \
    XDG_CURRENT_DESKTOP \
    XDG_SESSION_DESKTOP \
    DESKTOP_SESSION \
    XDG_SESSION_TYPE \
    XAUTHORITY >/dev/null 2>&1 || true

  if lms_run_as_user "$username" env "${user_env[@]}" systemctl --user daemon-reload >/dev/null 2>&1 &&
     lms_run_as_user "$username" env "${user_env[@]}" systemctl --user enable --now "$unit_name" >/dev/null 2>&1 &&
     lms_run_as_user "$username" env "${user_env[@]}" systemctl --user restart "$unit_name" >/dev/null 2>&1; then
    lms_log "Started Desktop Assistant helper for $username"
    return 0
  fi

  return 1
}

lms_try_start_desktop_helper_for_installer_user() {
  local unit_name="$1"
  local username
  local -a usernames=()
  local started=false

  if [[ "${LMS_DEST_ROOT:-}" != "" ]]; then
    return 0
  fi

  if ! lms_has_systemd; then
    lms_log "Desktop helper installed; it will start from desktop autostart on graphical login"
    return 0
  fi

  lms_collect_desktop_helper_users usernames

  for username in "${usernames[@]}"; do
    if lms_start_desktop_helper_for_user "$username" "$unit_name"; then
      started=true
    fi
  done

  if [[ "$started" == "true" ]]; then
    return 0
  else
    lms_log "Desktop helper installed; it will start automatically on the next graphical login"
  fi
}

lms_write_update_helper() {
  local service_user="$1"
  local base_url="${2:-https://www.linuxmadesane.com}"
  local helper_path="${3:-/usr/local/sbin/linux-made-sane-update}"

  if [[ "${LMS_DEST_ROOT:-}" != "" ]]; then
    lms_log "Skipping update helper while staging under LMS_DEST_ROOT"
    return
  fi

  if [[ "$(id -u)" -ne 0 ]]; then
    lms_log "Skipping update helper because the installer is not running as root"
    return
  fi

  local helper_dir
  helper_dir="$(dirname "$helper_path")"
  mkdir -p "$helper_dir"

  cat > "$helper_path" <<HELPER
#!/usr/bin/env bash
set -euo pipefail

INSTALL_URL="\${LMS_INSTALL_URL:-$base_url/install.sh}"
SOURCE="\${LMS_SOURCE:-lms-auto-update}"
SERVICE_UNIT="\${LMS_SERVICE_UNIT:-linux-made-sane.service}"
CURRENT_DIR="\${LMS_CURRENT_DIR:-/opt/linuxmadesane/ce/current}"
EXPECT_SERVICE_ACTIVE=true

if ! command -v curl >/dev/null 2>&1; then
  echo "curl is required" >&2
  exit 127
fi

BACKGROUND=false
INSTALL_ARGS=()
for argument in "\$@"; do
  case "\$argument" in
    --background|--detached|--no-wait)
      BACKGROUND=true
      ;;
    --no-start|--uninstall|--purge)
      EXPECT_SERVICE_ACTIVE=false
      INSTALL_ARGS+=("\$argument")
      ;;
    --start)
      EXPECT_SERVICE_ACTIVE=true
      INSTALL_ARGS+=("\$argument")
      ;;
    *)
      INSTALL_ARGS+=("\$argument")
      ;;
  esac
done

if [[ -z "\${LMS_UPDATE_DETACHED:-}" ]] && command -v systemd-run >/dev/null 2>&1 && [[ -d /run/systemd/system ]]; then
  UNIT_NAME="linux-made-sane-self-update-\$(date -u +%Y%m%d%H%M%S)"
  SYSTEMD_RUN_ARGS=(
    --unit="\$UNIT_NAME"
    --collect
    --property=Type=exec
  )
  if [[ "\$BACKGROUND" != "true" ]]; then
    SYSTEMD_RUN_ARGS+=(--wait)
  fi

  exec systemd-run "\${SYSTEMD_RUN_ARGS[@]}" \
    env LMS_UPDATE_DETACHED=1 LMS_INSTALL_URL="\$INSTALL_URL" LMS_SOURCE="\$SOURCE" LMS_BASE_URL="$base_url" "\$0" "\${INSTALL_ARGS[@]}"
fi

PREVIOUS_CURRENT_TARGET=""
SERVICE_WAS_ACTIVE=false
if [[ -e "\$CURRENT_DIR" || -L "\$CURRENT_DIR" ]]; then
  PREVIOUS_CURRENT_TARGET="\$(readlink -f "\$CURRENT_DIR" 2>/dev/null || true)"
fi

if command -v systemctl >/dev/null 2>&1 && [[ -d /run/systemd/system ]] && systemctl is-active --quiet "\$SERVICE_UNIT"; then
  SERVICE_WAS_ACTIVE=true
fi

rollback_self_update() {
  local reason="\$1"
  echo "Linux Made Sane self-update failed: \$reason" >&2
  if [[ -n "\$PREVIOUS_CURRENT_TARGET" && -d "\$PREVIOUS_CURRENT_TARGET" ]]; then
    echo "Rolling back to \$PREVIOUS_CURRENT_TARGET" >&2
    ln -sfn "\$PREVIOUS_CURRENT_TARGET" "\$CURRENT_DIR" || true
  fi

  if command -v systemctl >/dev/null 2>&1 && [[ -d /run/systemd/system ]] && { [[ "\$SERVICE_WAS_ACTIVE" == "true" ]] || [[ -n "\$PREVIOUS_CURRENT_TARGET" ]]; }; then
    systemctl daemon-reload >/dev/null 2>&1 || true
    systemctl restart "\$SERVICE_UNIT" >/dev/null 2>&1 || true
  fi
}

verify_self_update_active() {
  command -v systemctl >/dev/null 2>&1 || return 0
  [[ -d /run/systemd/system ]] || return 0
  for _ in \$(seq 1 30); do
    if systemctl is-active --quiet "\$SERVICE_UNIT"; then
      return 0
    fi

    sleep 1
  done

  systemctl --no-pager --full status "\$SERVICE_UNIT" >&2 || true
  return 1
}

if ! curl -fsSL "\$INSTALL_URL" | env LMS_SOURCE="\$SOURCE" LMS_BASE_URL="$base_url" bash -s -- --install "\${INSTALL_ARGS[@]}"; then
  rollback_self_update "installer returned a non-zero exit code"
  exit 1
fi

if [[ "\$EXPECT_SERVICE_ACTIVE" == "true" ]] && ! verify_self_update_active; then
  rollback_self_update "service did not become active after install"
  exit 1
fi
HELPER
  chmod 755 "$helper_path"
  chown root:root "$helper_path" 2>/dev/null || true

  command -v sudo >/dev/null 2>&1 || {
    lms_log "sudo is not installed; skipping update helper sudo policy"
    return
  }

  local sudoers_file="/etc/sudoers.d/linux-made-sane-update"
  cat > "$sudoers_file" <<SUDOERS
$service_user ALL=(root) NOPASSWD: $helper_path
$service_user ALL=(root) NOPASSWD: $helper_path *
SUDOERS
  chmod 440 "$sudoers_file"

  if command -v visudo >/dev/null 2>&1; then
    visudo -cf "$sudoers_file" >/dev/null
  fi

  lms_log "Prepared LMS update helper $helper_path"
}

lms_print_access_urls() {
  local service_port="${1:-5080}"

  printf '\nOpen LMS from a browser that can reach this machine:\n'

  local seen=" "
  lms_add_setup_url() {
    local host="$1"
    host="${host%% *}"
    host="${host%/}"
    [[ -n "$host" ]] || return 0
    [[ "$host" == "localhost" || "$host" == "127.0.0.1" || "$host" == "::1" ]] && return 0

    if [[ "$host" == *:* && "$host" != \[*\] ]]; then
      host="[$host]"
    fi

    case "$seen" in
      *" $host "*) return 0 ;;
    esac

    seen="$seen$host "
    printf '  http://%s:%s/\n' "$host" "$service_port"
  }

  lms_add_setup_url "$(hostname -f 2>/dev/null || true)"
  lms_add_setup_url "$(hostname -s 2>/dev/null || true)"
  lms_add_setup_url "$(hostname 2>/dev/null || true)"

  if command -v ip >/dev/null 2>&1; then
    while read -r address; do
      lms_add_setup_url "$address"
    done < <(ip -o -4 addr show scope global 2>/dev/null | awk '{ split($4, parts, "/"); print parts[1] }')
  fi

  if command -v hostname >/dev/null 2>&1; then
    for address in $(hostname -I 2>/dev/null || true); do
      lms_add_setup_url "$address"
    done
  fi

  printf '  http://127.0.0.1:%s/  (server console only)\n' "$service_port"
}

lms_reset_dir() {
  local path="$1"
  rm -rf "$path"
  mkdir -p "$path"
}

lms_abs_path() {
  local path="$1"
  if [[ -d "$path" ]]; then
    (cd "$path" >/dev/null 2>&1 && pwd)
    return
  fi

  local directory
  directory="$(cd "$(dirname "$path")" >/dev/null 2>&1 && pwd)"
  printf '%s/%s\n' "$directory" "$(basename "$path")"
}

lms_create_tarball() {
  local source_dir="$1"
  local destination="$2"

  mkdir -p "$(dirname "$destination")"
  tar -C "$(dirname "$source_dir")" -czf "$destination" "$(basename "$source_dir")"
}

lms_resolve_version_date() {
  if [[ -n "${LINUX_MADE_SANE_VERSION:-}" ]]; then
    lms_extract_version_date "$LINUX_MADE_SANE_VERSION"
    return
  fi

  if [[ -n "${VERSION:-}" ]]; then
    lms_extract_version_date "$VERSION"
    return
  fi

  if [[ -n "${LINUX_MADE_SANE_VERSION_DATE:-}" ]]; then
    printf '%s\n' "$LINUX_MADE_SANE_VERSION_DATE"
    return
  fi

  if [[ -n "${VERSION_DATE:-}" ]]; then
    printf '%s\n' "$VERSION_DATE"
    return
  fi

  date -u +%Y.%m.%d.%H.%M
}

lms_resolve_version_revision() {
  if [[ -n "${LINUX_MADE_SANE_VERSION:-}" ]]; then
    lms_extract_version_revision "$LINUX_MADE_SANE_VERSION"
    return
  fi

  if [[ -n "${VERSION:-}" ]]; then
    lms_extract_version_revision "$VERSION"
    return
  fi

  if [[ -n "${LINUX_MADE_SANE_VERSION_REVISION:-}" ]]; then
    printf '%s\n' "$LINUX_MADE_SANE_VERSION_REVISION"
    return
  fi

  if [[ -n "${VERSION_REVISION:-}" ]]; then
    printf '%s\n' "$VERSION_REVISION"
    return
  fi

  printf '0\n'
}

lms_resolve_version() {
  if [[ -n "${LINUX_MADE_SANE_VERSION:-}" ]]; then
    printf '%s\n' "$LINUX_MADE_SANE_VERSION"
    return
  fi

  if [[ -n "${VERSION:-}" ]]; then
    printf '%s\n' "$VERSION"
    return
  fi

  printf 'v%s\n' "$(lms_resolve_version_date)"
}

lms_extract_version_date() {
  local version="$1"
  if [[ "$version" =~ ^v([0-9]{4}\.[0-9]{2}\.[0-9]{2}\.[0-9]{2}\.[0-9]{2})$ ]]; then
    printf '%s\n' "${BASH_REMATCH[1]}"
    return
  fi

  if [[ "$version" =~ ^([0-9]{4}\.[0-9]{2}\.[0-9]{2})\.([0-9]+)$ ]]; then
    printf '%s\n' "${BASH_REMATCH[1]}"
    return
  fi

  lms_die "version must match vyyyy.MM.dd.HH.mm, got: $version"
}

lms_extract_version_revision() {
  local version="$1"
  if [[ "$version" =~ ^v[0-9]{4}\.[0-9]{2}\.[0-9]{2}\.[0-9]{2}\.[0-9]{2}$ ]]; then
    printf '0\n'
    return
  fi

  if [[ "$version" =~ ^([0-9]{4}\.[0-9]{2}\.[0-9]{2})\.([0-9]+)$ ]]; then
    printf '%s\n' "${BASH_REMATCH[2]}"
    return
  fi

  lms_die "version must match vyyyy.MM.dd.HH.mm, got: $version"
}

lms_resolve_next_version_revision() {
  local version_date="$1"
  local repo_root package_dir public_site_root date_regex max_revision path base revision

  repo_root="${REPO_ROOT:-$(lms_repo_root)}"
  package_dir="${PACKAGE_DIR:-$repo_root/artifacts/packages}"
  public_site_root="$repo_root/artifacts/public-site"
  date_regex="${version_date//./\\.}"
  max_revision=-1

  shopt -s nullglob
  for path in \
    "$package_dir"/release-manifest-"$version_date".*.json \
    "$package_dir"/linux-made-sane-*-"$version_date".*-*.tar.gz \
    "$public_site_root"/community/"$version_date".* \
    "$public_site_root"/pro/"$version_date".*
  do
    base="$(basename "$path")"
    if [[ "$base" =~ $date_regex\.([0-9]+) ]]; then
      revision="${BASH_REMATCH[1]}"
      if (( revision > max_revision )); then
        max_revision="$revision"
      fi
    fi
  done
  shopt -u nullglob

  if (( max_revision < 0 )); then
    printf '0\n'
    return
  fi

  printf '%s\n' "$((max_revision + 1))"
}

lms_validate_version() {
  local version="$1"
  [[ "$version" =~ ^v[0-9]{4}\.[0-9]{2}\.[0-9]{2}\.[0-9]{2}\.[0-9]{2}$ ||
     "$version" =~ ^[0-9]{4}\.[0-9]{2}\.[0-9]{2}\.[0-9]+$ ]] ||
    lms_die "version must match vyyyy.MM.dd.HH.mm, got: $version"
}

lms_find_latest_artifact() {
  local pattern="$1"
  local directory="$2"
  local matches=()

  shopt -s nullglob
  matches=("$directory"/$pattern)
  shopt -u nullglob

  [[ ${#matches[@]} -gt 0 ]] || return 1

  ls -1t "${matches[@]}" | head -n 1
}

lms_unpack_artifact() {
  local artifact_path="$1"
  local scratch_dir="$2"

  case "$artifact_path" in
    *.tar.gz|*.tgz)
      mkdir -p "$scratch_dir"
      tar -xzf "$artifact_path" -C "$scratch_dir"
      local extracted_entries=("$scratch_dir"/*)
      [[ ${#extracted_entries[@]} -eq 1 ]] || lms_die "expected one top-level entry in $artifact_path"
      printf '%s\n' "${extracted_entries[0]}"
      ;;
    *)
      printf '%s\n' "$artifact_path"
      ;;
  esac
}

lms_render_systemd_unit() {
  local template_path="$1"
  local destination_path="$2"
  local description="$3"
  local service_user="$4"
  local service_group="$5"
  local working_directory="$6"
  local env_file="$7"
  local exec_start="$8"

  sed \
    -e "s|__DESCRIPTION__|$(printf '%s' "$description" | sed 's/[&|]/\\&/g')|g" \
    -e "s|__SERVICE_USER__|$(printf '%s' "$service_user" | sed 's/[&|]/\\&/g')|g" \
    -e "s|__SERVICE_GROUP__|$(printf '%s' "$service_group" | sed 's/[&|]/\\&/g')|g" \
    -e "s|__WORKING_DIRECTORY__|$(printf '%s' "$working_directory" | sed 's/[&|]/\\&/g')|g" \
    -e "s|__ENV_FILE__|$(printf '%s' "$env_file" | sed 's/[&|]/\\&/g')|g" \
    -e "s|__EXEC_START__|$(printf '%s' "$exec_start" | sed 's/[&|]/\\&/g')|g" \
    "$template_path" > "$destination_path"
}

lms_render_desktop_helper_file() {
  local template_path="$1"
  local destination_path="$2"
  local socket_path="$3"
  local exec_start="$4"
  local local_lms_url="${5:-http://127.0.0.1:5080/desktop-assistant}"
  local tray_icon_path="${6:-}"

  sed \
    -e "s|__SOCKET_PATH__|$(printf '%s' "$socket_path" | sed 's/[&|]/\\&/g')|g" \
    -e "s|__LOCAL_LMS_URL__|$(printf '%s' "$local_lms_url" | sed 's/[&|]/\\&/g')|g" \
    -e "s|__TRAY_ICON_PATH__|$(printf '%s' "$tray_icon_path" | sed 's/[&|]/\\&/g')|g" \
    -e "s|__EXEC_START__|$(printf '%s' "$exec_start" | sed 's/[&|]/\\&/g')|g" \
    "$template_path" > "$destination_path"
}

lms_write_env_file() {
  local destination_path="$1"
  shift

  : > "$destination_path"
  for line in "$@"; do
    printf '%s\n' "$line" >> "$destination_path"
  done
}

lms_prepare_live_service_user() {
  local service_user="$1"
  local service_group="$2"
  local home_directory="$3"

  # Security contract: this account runs the LMS web service and owns LMS data.
  # Privileged local access is configured separately by lms_prepare_local_ssh_runner.
  if getent group "$service_group" >/dev/null 2>&1; then
    :
  else
    groupadd --system "$service_group"
  fi

  if id "$service_user" >/dev/null 2>&1; then
    :
  else
    useradd \
      --system \
      --gid "$service_group" \
      --home-dir "$home_directory" \
      --create-home \
      --shell /usr/sbin/nologin \
      "$service_user"
  fi
}

lms_maybe_chown() {
  local owner="$1"
  local path="$2"
  if [[ "${LMS_DEST_ROOT:-}" == "" ]]; then
    chown -R "$owner" "$path"
  fi
}

lms_maybe_systemctl_reload() {
  if [[ "${LMS_DEST_ROOT:-}" != "" ]]; then
    return
  fi

  command -v systemctl >/dev/null 2>&1 || return
  systemctl daemon-reload
}

lms_maybe_systemctl_enable() {
  local unit_name="$1"
  if [[ "${LMS_DEST_ROOT:-}" != "" ]]; then
    return
  fi

  command -v systemctl >/dev/null 2>&1 || return
  systemctl enable "$unit_name"
}

lms_current_release_target() {
  local current_dir="$1"
  if [[ -e "$current_dir" || -L "$current_dir" ]]; then
    readlink -f "$current_dir" 2>/dev/null || true
  fi
}

lms_systemctl_is_active() {
  local unit_name="$1"
  [[ "${LMS_DEST_ROOT:-}" == "" ]] || return 1
  lms_has_systemd || return 1
  systemctl is-active --quiet "$unit_name"
}

lms_rollback_current_release() {
  local current_dir="$1"
  local unit_name="$2"
  local previous_current_target="$3"
  local should_restart="${4:-true}"

  lms_log "Install failed; attempting to restore the previous release"
  if [[ -n "$previous_current_target" && -d "$previous_current_target" ]]; then
    ln -sfn "$previous_current_target" "$current_dir" || true
    lms_log "Restored current release link to $previous_current_target"
  fi

  if lms_is_truthy "$should_restart" && lms_has_systemd; then
    systemctl daemon-reload >/dev/null 2>&1 || true
    systemctl restart "$unit_name" >/dev/null 2>&1 || true
  fi
}

lms_maybe_systemctl_restart() {
  local unit_name="$1"
  if [[ "${LMS_DEST_ROOT:-}" != "" ]]; then
    return
  fi

  lms_has_systemd || return
  systemctl restart "$unit_name"
  for _ in $(seq 1 30); do
    if systemctl is-active --quiet "$unit_name"; then
      return 0
    fi

    sleep 1
  done

  systemctl --no-pager --full status "$unit_name" >&2 || true
  return 1
}
