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
  if [[ -n "${LINUX_MADE_SANE_VERSION_DATE:-}" ]]; then
    printf '%s\n' "$LINUX_MADE_SANE_VERSION_DATE"
    return
  fi

  if [[ -n "${VERSION_DATE:-}" ]]; then
    printf '%s\n' "$VERSION_DATE"
    return
  fi

  date -u +%Y.%m.%d
}

lms_resolve_version_revision() {
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

  printf '%s.%s\n' "$(lms_resolve_version_date)" "$(lms_resolve_version_revision)"
}

lms_validate_version() {
  local version="$1"
  [[ "$version" =~ ^[0-9]{4}\.[0-9]{2}\.[0-9]{2}\.[0-9]+$ ]] ||
    lms_die "version must match yyyy.MM.dd.revision, got: $version"
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
