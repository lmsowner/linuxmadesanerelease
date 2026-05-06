#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/lib/deploy-common.sh
source "$SCRIPT_DIR/lib/deploy-common.sh"

REPO_ROOT="$(lms_repo_root)"
PROJECT_PATH="$REPO_ROOT/src/LinuxMadeSane.Web/LinuxMadeSane.Web.csproj"
APP_EXECUTABLE="${APP_EXECUTABLE:-}"
SMOKE_ROOT="${SMOKE_ROOT:-}"
KEEP_SMOKE_ROOT="${KEEP_SMOKE_ROOT:-false}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --app) APP_EXECUTABLE="$2"; shift 2 ;;
    --project) PROJECT_PATH="$2"; shift 2 ;;
    --smoke-root) SMOKE_ROOT="$2"; shift 2 ;;
    --keep) KEEP_SMOKE_ROOT=true; shift ;;
    *) lms_die "unknown argument: $1" ;;
  esac
done

if [[ -z "$SMOKE_ROOT" ]]; then
  SMOKE_ROOT="$(mktemp -d)"
fi

cleanup() {
  if [[ "$KEEP_SMOKE_ROOT" != "true" ]]; then
    rm -rf "$SMOKE_ROOT"
  fi
}
trap cleanup EXIT

mkdir -p "$SMOKE_ROOT"
DB_PATH="$SMOKE_ROOT/linuxmadesane.db"

export ASPNETCORE_ENVIRONMENT=Production
export ASPNETCORE_URLS=http://127.0.0.1:0
export ConnectionStrings__LinuxMadeSane="Data Source=$DB_PATH"

if [[ -n "$APP_EXECUTABLE" ]]; then
  APP_EXECUTABLE="$(lms_abs_path "$APP_EXECUTABLE")"
  [[ -x "$APP_EXECUTABLE" ]] || lms_die "published app executable is not executable: $APP_EXECUTABLE"
  lms_log "Running published cold-start smoke: $APP_EXECUTABLE"
  (cd "$(dirname "$APP_EXECUTABLE")" && "$APP_EXECUTABLE" --smoke-startup)
else
  lms_require_command dotnet
  PROJECT_PATH="$(lms_abs_path "$PROJECT_PATH")"
  lms_log "Running source cold-start smoke: $PROJECT_PATH"
  dotnet run --project "$PROJECT_PATH" -- --smoke-startup
fi

[[ -s "$DB_PATH" ]] || lms_die "startup smoke did not create a SQLite database: $DB_PATH"

lms_log "Cold-start smoke complete"
printf 'database: %s\n' "$DB_PATH"
