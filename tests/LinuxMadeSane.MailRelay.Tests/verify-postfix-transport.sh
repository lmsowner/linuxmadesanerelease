#!/usr/bin/env bash

# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE.md for details.

set -euo pipefail
# Disposable integration fixture: no host ports, volumes, mail service or cloud access.
repo_root=$(cd "$(dirname "$0")/../.." && pwd)
fixture_dir=$(mktemp -d "${TMPDIR:-/tmp}/lms-mail-transport.XXXXXX")
image_tag="lms-mail-api-verification:$$"
cleanup() { docker image rm "$image_tag" >/dev/null 2>&1 || true; rm -rf "$fixture_dir"; }
trap cleanup EXIT
cd "$repo_root"
LMS_MAIL_TRANSPORT_FIXTURE_DIR="$fixture_dir" dotnet test tests/LinuxMadeSane.MailRelay.Tests \
  --filter FullyQualifiedName~ApiTransportUsesNativeQueueUnprivilegedPipeAndLeavesListenerRulesIntact

tar -xf "$fixture_dir/context.tar" -C "$fixture_dir"
docker build -t "$image_tag" "$fixture_dir"
docker run --rm --network none --entrypoint python3 \
  -v "$repo_root/tests/LinuxMadeSane.MailRelay.Tests/Fixtures/verify-postfix-api.py:/fixture.py:ro" \
  "$image_tag" /fixture.py
