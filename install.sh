#!/usr/bin/env bash

set -euo pipefail

REPO="${REPO:-lmsowner/linuxmadesanerelease}"
EDITION="${EDITION:-ce}"
VERSION="${VERSION:-latest}"
RID="${RID:-}"
PORT="${PORT:-5080}"
INSTALL_ROOT="${INSTALL_ROOT:-/opt/linuxmadesane/ce}"
DATA_ROOT="${DATA_ROOT:-/var/lib/linuxmadesane/ce}"
CONFIG_ROOT="${CONFIG_ROOT:-/etc/linuxmadesane/ce}"
SERVICE_USER="${SERVICE_USER:-linuxmadesane}"
SERVICE_GROUP="${SERVICE_GROUP:-linuxmadesane}"
SERVICE_UNIT="${SERVICE_UNIT:-linux-made-sane.service}"
START_SERVICE="${START_SERVICE:-true}"
INSTALL_HOST_PACKAGES="${INSTALL_HOST_PACKAGES:-true}"
RAW_BASE_URL="${RAW_BASE_URL:-https://raw.githubusercontent.com/$REPO/main}"

log() {
  printf '[lms-release] %s\n' "$*"
}

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || die "required command not found: $1"
}

detect_rid() {
  local machine
  machine="$(uname -m)"
  case "$machine" in
    x86_64|amd64) printf 'linux-x64\n' ;;
    aarch64|arm64) printf 'linux-arm64\n' ;;
    armv7l|armv6l|armhf|arm) printf 'linux-arm\n' ;;
    *) die "unsupported CPU architecture: $machine. Set RID manually." ;;
  esac
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="$2"; shift 2 ;;
    --rid) RID="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    --install-root) INSTALL_ROOT="$2"; shift 2 ;;
    --data-root) DATA_ROOT="$2"; shift 2 ;;
    --config-root) CONFIG_ROOT="$2"; shift 2 ;;
    --service-unit) SERVICE_UNIT="$2"; shift 2 ;;
    --no-start) START_SERVICE=false; shift ;;
    --skip-host-packages) INSTALL_HOST_PACKAGES=false; shift ;;
    *) die "unknown argument: $1" ;;
  esac
done

[[ "$EDITION" == "ce" ]] || die "public installer supports CE only"
[[ "$(id -u)" -eq 0 ]] || die "run as root, for example: curl -fsSL https://raw.githubusercontent.com/$REPO/main/install.sh | sudo bash"

RID="${RID:-$(detect_rid)}"

require_command curl
require_command tar

install_host_packages() {
  case "$INSTALL_HOST_PACKAGES" in
    false|False|FALSE|0|no|No|NO|off|Off|OFF)
      log "Skipping optional host package install"
      return
      ;;
  esac

  local packages=()
  local install_command=()

  if command -v apt-get >/dev/null 2>&1; then
    packages=(ffmpeg samba-common-bin smbclient cifs-utils)
    install_command=(apt-get install -y --)
    DEBIAN_FRONTEND=noninteractive apt-get update || {
      log "Package metadata refresh failed; continuing without optional host packages"
      return
    }
    DEBIAN_FRONTEND=noninteractive "${install_command[@]}" "${packages[@]}" || log "Optional host package install failed; LMS core install will continue"
    return
  fi

  if command -v dnf >/dev/null 2>&1; then
    packages=(ffmpeg samba-client cifs-utils)
    dnf install -y "${packages[@]}" || log "Optional host package install failed; LMS core install will continue"
    return
  fi

  if command -v yum >/dev/null 2>&1; then
    packages=(ffmpeg samba-client cifs-utils)
    yum install -y "${packages[@]}" || log "Optional host package install failed; LMS core install will continue"
    return
  fi

  if command -v zypper >/dev/null 2>&1; then
    packages=(ffmpeg samba-client cifs-utils)
    zypper --non-interactive install "${packages[@]}" || log "Optional host package install failed; LMS core install will continue"
    return
  fi

  if command -v pacman >/dev/null 2>&1; then
    packages=(ffmpeg smbclient cifs-utils)
    pacman -Sy --noconfirm "${packages[@]}" || log "Optional host package install failed; LMS core install will continue"
    return
  fi

  log "No supported package manager found; skipping optional host packages"
}

resolve_latest_asset_url() {
  local api_json
  api_json="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest")"
  printf '%s\n' "$api_json" |
    grep -Eo "https://[^\"]+/linux-made-sane-${EDITION}-[^\"]+-${RID}\.tar\.gz" |
    head -n 1
}

resolve_latest_checksums_url() {
  local api_json
  api_json="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest")"
  printf '%s\n' "$api_json" |
    grep -Eo "https://[^\"]+/SHA256SUMS" |
    head -n 1
}

resolve_repo_latest_version() {
  curl -fsSL "$RAW_BASE_URL/packages/latest.txt" | tr -d '\r\n[:space:]'
}

resolve_repo_asset_url() {
  local version="$1"
  printf '%s/packages/%s/linux-made-sane-%s-%s-%s.tar.gz\n' "$RAW_BASE_URL" "$version" "$EDITION" "$version" "$RID"
}

resolve_repo_checksums_url() {
  local version="$1"
  printf '%s/packages/%s/SHA256SUMS\n' "$RAW_BASE_URL" "$version"
}

TMP_DIR="$(mktemp -d)"
cleanup() {
  rm -rf "$TMP_DIR"
}
trap cleanup EXIT

if [[ "$VERSION" == "latest" ]]; then
  PACKAGE_URL="$(resolve_latest_asset_url || true)"
  CHECKSUM_URL="$(resolve_latest_checksums_url || true)"
  if [[ -z "${PACKAGE_URL:-}" ]]; then
    RESOLVED_VERSION="$(resolve_repo_latest_version || true)"
    [[ -n "$RESOLVED_VERSION" ]] || die "could not resolve latest GitHub Release or repo-hosted package version"
    VERSION="$RESOLVED_VERSION"
    PACKAGE_URL="$(resolve_repo_asset_url "$VERSION")"
    CHECKSUM_URL="$(resolve_repo_checksums_url "$VERSION")"
  fi
else
  PACKAGE_URL="https://github.com/$REPO/releases/download/$VERSION/linux-made-sane-${EDITION}-${VERSION}-${RID}.tar.gz"
  CHECKSUM_URL="https://github.com/$REPO/releases/download/$VERSION/SHA256SUMS"
fi

[[ -n "${PACKAGE_URL:-}" ]] || die "could not resolve release asset for edition=$EDITION rid=$RID version=$VERSION"

PACKAGE_PATH="$TMP_DIR/package.tar.gz"
CHECKSUM_PATH="$TMP_DIR/SHA256SUMS"

log "Downloading $PACKAGE_URL"
if ! curl -fL "$PACKAGE_URL" -o "$PACKAGE_PATH"; then
  if [[ "$VERSION" == "latest" ]]; then
    RESOLVED_VERSION="$(resolve_repo_latest_version || true)"
  else
    RESOLVED_VERSION="$VERSION"
  fi

  [[ -n "${RESOLVED_VERSION:-}" ]] || die "release package download failed and no repo-hosted version could be resolved"
  PACKAGE_URL="$(resolve_repo_asset_url "$RESOLVED_VERSION")"
  CHECKSUM_URL="$(resolve_repo_checksums_url "$RESOLVED_VERSION")"
  log "GitHub Release asset unavailable; trying repo-hosted package $PACKAGE_URL"
  curl -fL "$PACKAGE_URL" -o "$PACKAGE_PATH"
  VERSION="$RESOLVED_VERSION"
fi

if [[ -n "${CHECKSUM_URL:-}" ]] && curl -fsSL "$CHECKSUM_URL" -o "$CHECKSUM_PATH"; then
  if command -v sha256sum >/dev/null 2>&1; then
    PACKAGE_NAME="$(basename "$PACKAGE_URL")"
    EXPECTED_SHA="$(awk -v file="$PACKAGE_NAME" '$2 == file { print $1 }' "$CHECKSUM_PATH")"
    if [[ -n "$EXPECTED_SHA" ]]; then
      ACTUAL_SHA="$(sha256sum "$PACKAGE_PATH" | awk '{ print $1 }')"
      [[ "$EXPECTED_SHA" == "$ACTUAL_SHA" ]] || die "checksum mismatch for $PACKAGE_NAME"
      log "Checksum verified"
    else
      log "Checksum file did not include $PACKAGE_NAME; continuing"
    fi
  else
    log "sha256sum not found; checksum verification skipped"
  fi
fi

install_host_packages

EXTRACT_ROOT="$TMP_DIR/extract"
mkdir -p "$EXTRACT_ROOT"
tar -xzf "$PACKAGE_PATH" -C "$EXTRACT_ROOT"
PACKAGE_ROOT="$(find "$EXTRACT_ROOT" -mindepth 1 -maxdepth 1 -type d | head -n 1)"
[[ -d "$PACKAGE_ROOT/app" ]] || die "release package did not contain an app directory"

PACKAGE_VERSION="$(sed -n '1p' "$PACKAGE_ROOT/version.txt" 2>/dev/null | tr -d '\r\n')"
PACKAGE_VERSION="${PACKAGE_VERSION:-unknown}"
PACKAGE_VERSION_SAFE="$(printf '%s' "$PACKAGE_VERSION" | tr -c '0-9A-Za-z._-' '-')"
RELEASE_ID="${PACKAGE_VERSION_SAFE}-$(date -u +%Y%m%d%H%M%S)"
RELEASE_DIR="$INSTALL_ROOT/releases/$RELEASE_ID"
CURRENT_DIR="$INSTALL_ROOT/current"
ENV_FILE="$CONFIG_ROOT/service.env"
UNIT_FILE="/etc/systemd/system/$SERVICE_UNIT"

# Security contract: the installer creates a dedicated account for the LMS web
# service and data ownership. It does not silently grant root access. Elevated
# unattended automation requires an explicit runner/sudoers decision by the
# operator; sudo-marked LMS runbooks use non-interactive sudo and fail clearly
# when passwordless elevation is not configured.
if getent group "$SERVICE_GROUP" >/dev/null 2>&1; then
  :
else
  groupadd --system "$SERVICE_GROUP"
fi

if id "$SERVICE_USER" >/dev/null 2>&1; then
  :
else
  useradd --system --gid "$SERVICE_GROUP" --home-dir "$INSTALL_ROOT" --create-home --shell /usr/sbin/nologin "$SERVICE_USER"
fi

mkdir -p "$RELEASE_DIR" "$DATA_ROOT" "$CONFIG_ROOT"
cp -a "$PACKAGE_ROOT/app"/. "$RELEASE_DIR"/
chmod +x "$RELEASE_DIR/LinuxMadeSane.Web" 2>/dev/null || true
ln -sfn "$RELEASE_DIR" "$CURRENT_DIR"

cat > "$ENV_FILE" <<EOF
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://0.0.0.0:${PORT}
ConnectionStrings__LinuxMadeSane='Data Source=${DATA_ROOT}/linuxmadesane.db'
EOF

cat > "$UNIT_FILE" <<EOF
[Unit]
Description=Linux Made Sane Service
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=${SERVICE_USER}
Group=${SERVICE_GROUP}
WorkingDirectory=${CURRENT_DIR}
EnvironmentFile=${ENV_FILE}
ExecStart=${CURRENT_DIR}/LinuxMadeSane.Web
Restart=on-failure
RestartSec=5
KillSignal=SIGINT
SyslogIdentifier=linux-made-sane

[Install]
WantedBy=multi-user.target
EOF

chown -R "$SERVICE_USER:$SERVICE_GROUP" "$INSTALL_ROOT" "$DATA_ROOT" "$CONFIG_ROOT"
systemctl daemon-reload
systemctl enable "$SERVICE_UNIT"

if [[ "$START_SERVICE" == "true" ]]; then
  systemctl restart "$SERVICE_UNIT"
  for _ in $(seq 1 30); do
    if curl -fsS "http://127.0.0.1:${PORT}/healthz" >/dev/null 2>&1; then
      log "Health check passed"
      break
    fi
    sleep 1
  done
fi

log "Install complete"
printf 'version: %s\nservice: %s\nurl: http://127.0.0.1:%s\n' "$PACKAGE_VERSION" "$SERVICE_UNIT" "$PORT"
