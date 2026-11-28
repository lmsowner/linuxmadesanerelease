#!/usr/bin/env bash
# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE.md for details.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/lib/deploy-common.sh"
[[ "$(id -u)" == 0 ]] || lms_die "Caddy setup requires administrator privileges"
lms_ensure_caddy_source_binding
