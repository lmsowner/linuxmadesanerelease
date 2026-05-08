#!/usr/bin/env bash
set -euo pipefail

LMS_PUBLIC_INSTALL_URL="${LMS_PUBLIC_INSTALL_URL:-https://www.linuxmadesane.com/install.sh}"

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

if command -v curl >/dev/null 2>&1; then
  curl -fsSL "$LMS_PUBLIC_INSTALL_URL" | bash -s -- "$@"
  exit $?
fi

if command -v wget >/dev/null 2>&1; then
  wget -qO- "$LMS_PUBLIC_INSTALL_URL" | bash -s -- "$@"
  exit $?
fi

die "curl or wget is required to fetch $LMS_PUBLIC_INSTALL_URL"
