#!/usr/bin/env bash

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

if ! command -v curl >/dev/null 2>&1; then
  echo "curl is required" >&2
  exit 127
fi

if [[ -z "\${LMS_UPDATE_DETACHED:-}" ]] && command -v systemd-run >/dev/null 2>&1 && [[ -d /run/systemd/system ]]; then
  UNIT_NAME="linux-made-sane-self-update-\$(date -u +%Y%m%d%H%M%S)"
  exec systemd-run \
    --unit="\$UNIT_NAME" \
    --collect \
    --wait \
    --pipe \
    --property=Type=exec \
    env LMS_UPDATE_DETACHED=1 LMS_INSTALL_URL="\$INSTALL_URL" LMS_SOURCE="\$SOURCE" LMS_BASE_URL="$base_url" "\$0" "\$@"
fi

curl -fsSL "\$INSTALL_URL" | env LMS_SOURCE="\$SOURCE" LMS_BASE_URL="$base_url" bash -s -- --install --start "\$@"
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

lms_maybe_systemctl_restart() {
  local unit_name="$1"
  if [[ "${LMS_DEST_ROOT:-}" != "" ]]; then
    return
  fi

  command -v systemctl >/dev/null 2>&1 || return
  systemctl restart "$unit_name"
}
