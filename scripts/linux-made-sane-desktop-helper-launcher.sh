#!/usr/bin/env bash

# Copyright (c) Richard D. Kiernan.
# Licensed under the Business Source License 1.1. See LICENSE for details.

set -euo pipefail

CONFIG_FILE="${LMS_DESKTOP_HELPER_CONFIG_FILE:-/etc/linuxmadesane/ce-desktop-helper.conf}"
if [[ -f "$CONFIG_FILE" ]]; then
  # shellcheck disable=SC1090
  source "$CONFIG_FILE"
fi

EXECUTABLE_PATH="${LMS_DESKTOP_HELPER_EXECUTABLE_PATH:-/opt/linuxmadesane/ce/current/desktop-helper/LinuxMadeSane.DesktopHelper}"
SOCKET_PATH="${LMS_DESKTOP_HELPER_SOCKET_PATH:-/run/linuxmadesane/desktop-session.sock}"
LOCAL_LMS_URL="${LMS_DESKTOP_HELPER_LOCAL_LMS_URL:-http://127.0.0.1:5080/desktop-assistant}"
TRAY_ICON_PATH="${LMS_DESKTOP_HELPER_TRAY_ICON_PATH:-/opt/linuxmadesane/ce/current/wwwroot/images/lms-logo-192.png}"
SERVICE_GROUP="${LMS_DESKTOP_HELPER_SERVICE_GROUP:-linuxmadesane}"

export NO_AT_BRIDGE=1
export LMS_DESKTOP_HELPER_SOCKET="$SOCKET_PATH"
export LMS_DESKTOP_HELPER_LMS_URL="$LOCAL_LMS_URL"
export LMS_DESKTOP_HELPER_TRAY_ICON="$TRAY_ICON_PATH"
open_window="${LMS_DESKTOP_HELPER_OPEN_WINDOW:-false}"

if [[ "${LMS_DESKTOP_HELPER_SG_ACTIVE:-}" != "1" ]] &&
   command -v sg >/dev/null 2>&1 &&
   getent group "$SERVICE_GROUP" >/dev/null 2>&1 &&
   ! id -nG | tr ' ' '\n' | grep -qx "$SERVICE_GROUP"; then
  group_entry="$(getent group "$SERVICE_GROUP")"
  group_members="${group_entry##*:}"
  current_user="$(id -un)"
  if [[ ",$group_members," == *",$current_user,"* ]]; then
    exec sg "$SERVICE_GROUP" -c "LMS_DESKTOP_HELPER_SG_ACTIVE=1 LMS_DESKTOP_HELPER_OPEN_WINDOW=$(printf '%q' "$open_window") exec $(printf '%q' "$0")"
  fi
fi

[[ -x "$EXECUTABLE_PATH" ]] || {
  printf 'Desktop Helper payload is not installed. Enable it from Desktop Assistant > Setup.\n' >&2
  exit 1
}

cd "$(dirname "$EXECUTABLE_PATH")"
exec "$EXECUTABLE_PATH"
