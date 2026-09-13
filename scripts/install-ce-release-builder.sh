#!/usr/bin/env bash

# Copyright (c) Linux Made Sane.
# Licensed under the Business Source License 1.1. See LICENSE for details.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
BUILDER_USER="${BUILDER_USER:-linuxmadesane-builder}"
BUILDER_GROUP="${BUILDER_GROUP:-linuxmadesane}"
WORK_ROOT="${WORK_ROOT:-/var/lib/linuxmadesane/release-builder}"
COMMUNITY_RELEASE_ROOT="${COMMUNITY_RELEASE_ROOT:-/var/lib/linuxmadesane/public-site/releases/community}"
SOURCE_REPOSITORY="${SOURCE_REPOSITORY:-https://github.com/lmsowner/linuxmadesanerelease.git}"
SOURCE_BRANCH="${SOURCE_BRANCH:-main}"
PUBLIC_BASE_URL="${PUBLIC_BASE_URL:-https://www.linuxmadesane.com}"
ENV_FILE="${ENV_FILE:-/etc/linuxmadesane/ce-release-builder.env}"
EXECUTABLE="${EXECUTABLE:-/usr/local/sbin/linux-made-sane-ce-autopublish}"
SERVICE_FILE="/etc/systemd/system/linux-made-sane-ce-builder.service"
TIMER_FILE="/etc/systemd/system/linux-made-sane-ce-builder.timer"
RUN_NOW=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --run-now) RUN_NOW=true; shift ;;
    --source-repository) SOURCE_REPOSITORY="$2"; shift 2 ;;
    --source-branch) SOURCE_BRANCH="$2"; shift 2 ;;
    *) printf 'error: unknown argument: %s\n' "$1" >&2; exit 1 ;;
  esac
done

[[ "$(id -u)" -eq 0 ]] || { printf 'error: run this installer as root\n' >&2; exit 1; }
for command in curl dotnet flock git python3 sha256sum systemctl tar; do
  command -v "$command" >/dev/null 2>&1 || { printf 'error: required command not found: %s\n' "$command" >&2; exit 1; }
done

getent group "$BUILDER_GROUP" >/dev/null 2>&1 || groupadd --system "$BUILDER_GROUP"
if ! id "$BUILDER_USER" >/dev/null 2>&1; then
  useradd --system --gid "$BUILDER_GROUP" --home-dir "$WORK_ROOT" --create-home --shell /usr/sbin/nologin "$BUILDER_USER"
fi

mkdir -p "$WORK_ROOT" "$COMMUNITY_RELEASE_ROOT" "$(dirname "$ENV_FILE")" "$(dirname "$EXECUTABLE")"
install -m 0755 "$REPO_ROOT/scripts/auto-publish-ce.sh" "$EXECUTABLE"
cat > "$ENV_FILE" <<EOF
SOURCE_REPOSITORY=$SOURCE_REPOSITORY
SOURCE_BRANCH=$SOURCE_BRANCH
WORK_ROOT=$WORK_ROOT
COMMUNITY_RELEASE_ROOT=$COMMUNITY_RELEASE_ROOT
PUBLIC_BASE_URL=$PUBLIC_BASE_URL
RUNTIME=linux-x64
EOF
chmod 0644 "$ENV_FILE"
chown -R "$BUILDER_USER:$BUILDER_GROUP" "$WORK_ROOT" "$COMMUNITY_RELEASE_ROOT"
chmod 0755 "$WORK_ROOT"
chmod 2755 "$COMMUNITY_RELEASE_ROOT"

sed \
  -e "s|__BUILDER_USER__|$BUILDER_USER|g" \
  -e "s|__BUILDER_GROUP__|$BUILDER_GROUP|g" \
  -e "s|__ENV_FILE__|$ENV_FILE|g" \
  -e "s|__WORK_ROOT__|$WORK_ROOT|g" \
  -e "s|__COMMUNITY_RELEASE_ROOT__|$COMMUNITY_RELEASE_ROOT|g" \
  -e "s|__EXEC_START__|$EXECUTABLE|g" \
  "$REPO_ROOT/deploy/systemd/linux-made-sane-ce-builder.service.template" > "$SERVICE_FILE"
install -m 0644 "$REPO_ROOT/deploy/systemd/linux-made-sane-ce-builder.timer.template" "$TIMER_FILE"

systemctl daemon-reload
systemctl enable --now linux-made-sane-ce-builder.timer
if [[ "$RUN_NOW" == "true" ]]; then
  systemctl start linux-made-sane-ce-builder.service
fi

printf 'Automatic CE release builder installed.\nservice: %s\ntimer: %s\nsource: %s (%s)\n' \
  "$SERVICE_FILE" "$TIMER_FILE" "$SOURCE_REPOSITORY" "$SOURCE_BRANCH"
