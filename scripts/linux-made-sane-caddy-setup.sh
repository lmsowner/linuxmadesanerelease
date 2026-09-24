#!/usr/bin/env bash
# Copyright (c) Linux Made Sane.
# Licensed under the Business Source License 1.1. See LICENSE for details.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/lib/deploy-common.sh"
[[ "$(id -u)" == 0 ]] || lms_die "Caddy setup requires administrator privileges"
lms_ensure_caddy_source_binding
