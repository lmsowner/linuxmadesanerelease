#!/usr/bin/env bash

# Copyright (c) Richard D. Kiernan.
# Licensed under the Business Source License 1.1. See LICENSE for details.

set -euo pipefail

log() { printf '[lms-ce-builder] %s\n' "$*"; }
die() { printf '[lms-ce-builder] error: %s\n' "$*" >&2; exit 1; }
require() { command -v "$1" >/dev/null 2>&1 || die "required command not found: $1"; }

SOURCE_REPOSITORY="${SOURCE_REPOSITORY:-https://github.com/lmsowner/linuxmadesanerelease.git}"
SOURCE_BRANCH="${SOURCE_BRANCH:-main}"
WORK_ROOT="${WORK_ROOT:-/var/lib/linuxmadesane/release-builder}"
CHECKOUT_ROOT="${CHECKOUT_ROOT:-$WORK_ROOT/source}"
PACKAGE_DIR="${PACKAGE_DIR:-$WORK_ROOT/packages}"
STATE_FILE="${STATE_FILE:-$WORK_ROOT/last-successful-release}"
LOCK_FILE="${LOCK_FILE:-$WORK_ROOT/release.lock}"
COMMUNITY_RELEASE_ROOT="${COMMUNITY_RELEASE_ROOT:-/var/lib/linuxmadesane/public-site/releases/community}"
PUBLIC_BASE_URL="${PUBLIC_BASE_URL:-https://www.linuxmadesane.com}"
RUNTIME="${RUNTIME:-linux-x64}"

for command in curl dotnet flock git python3 sha256sum tar; do
  require "$command"
done

mkdir -p "$WORK_ROOT" "$PACKAGE_DIR" "$COMMUNITY_RELEASE_ROOT"
exec 9>"$LOCK_FILE"
flock -n 9 || { log "Another CE release check is already running."; exit 0; }

if [[ ! -d "$CHECKOUT_ROOT/.git" ]]; then
  log "Cloning $SOURCE_REPOSITORY ($SOURCE_BRANCH)."
  git clone --filter=blob:none --branch "$SOURCE_BRANCH" --single-branch "$SOURCE_REPOSITORY" "$CHECKOUT_ROOT"
else
  git -C "$CHECKOUT_ROOT" remote set-url origin "$SOURCE_REPOSITORY"
fi

git -C "$CHECKOUT_ROOT" fetch --quiet --prune origin "refs/heads/$SOURCE_BRANCH:refs/remotes/origin/$SOURCE_BRANCH"
source_commit="$(git -C "$CHECKOUT_ROOT" rev-parse "origin/$SOURCE_BRANCH")"
last_commit="$(sed -n '1p' "$STATE_FILE" 2>/dev/null || true)"
if [[ "$source_commit" == "$last_commit" ]]; then
  log "No CE source changes since $source_commit."
  exit 0
fi

git -C "$CHECKOUT_ROOT" checkout --quiet --detach --force "$source_commit"
git -C "$CHECKOUT_ROOT" clean -ffdqx
version="$(tr -d '\r\n' < "$CHECKOUT_ROOT/VERSION")"
private_source_commit="$(tr -d '\r\n' < "$CHECKOUT_ROOT/PRIVATE-SOURCE-COMMIT" 2>/dev/null || true)"
[[ "$version" =~ ^v[0-9]{4}\.[0-9]{2}\.[0-9]{2}\.[0-9]{2}\.[0-9]{2}$ ]] ||
  die "invalid VERSION in $source_commit: $version"

latest_version="$(
  curl -fsS --max-time 30 "$PUBLIC_BASE_URL/api/downloads/manifest" 2>/dev/null |
    python3 -c 'import json,sys; print(json.load(sys.stdin).get("latestCommunityVersion", ""))' 2>/dev/null || true
)"
live_manifest="$COMMUNITY_RELEASE_ROOT/$version/release-manifest-$version.json"
live_source_commit="$(
  python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("sourceCommit", ""))' "$live_manifest" 2>/dev/null || true
)"
if [[ "$version" == "$latest_version" ]] &&
   { [[ "$source_commit" == "$live_source_commit" ]] ||
     [[ -n "$private_source_commit" && "$private_source_commit" == "$live_source_commit" ]]; }; then
  printf '%s\n%s\n' "$source_commit" "$version" > "$STATE_FILE.tmp"
  mv "$STATE_FILE.tmp" "$STATE_FILE"
  log "Release $version for public source $source_commit is already live; state synchronized."
  exit 0
fi
if [[ -n "$latest_version" && ( "$version" == "$latest_version" || "$version" < "$latest_version" ) ]]; then
  die "source $source_commit uses $version, which is not newer than live release $latest_version"
fi

log "Building CE $version from pushed commit $source_commit."
EDITIONS=ce \
HOST_RUNTIMES="$RUNTIME" \
CE_RUNTIMES="$RUNTIME" \
PACKAGE_DIR="$PACKAGE_DIR" \
STAGE_PUBLIC_SITE_RELEASES=false \
CLEAN_OLD_RELEASE_OUTPUTS=true \
REQUIRE_PUSHED_GIT_STATE=true \
RELEASE_GIT_REMOTE=origin \
RELEASE_GIT_BRANCH="$SOURCE_BRANCH" \
"$CHECKOUT_ROOT/scripts/publish-release-matrix.sh"

artifact="$PACKAGE_DIR/linux-made-sane-ce-$version-$RUNTIME.tar.gz"
manifest="$PACKAGE_DIR/release-manifest-$version.json"
[[ -f "$artifact" && -f "$manifest" ]] || die "CE build did not produce the expected release files"
(cd "$PACKAGE_DIR" && sha256sum -c SHA256SUMS)

verify_root="$(mktemp -d "$WORK_ROOT/verify.XXXXXX")"
cleanup() { rm -rf -- "$verify_root"; }
trap cleanup EXIT
tar -xzf "$artifact" -C "$verify_root"
package_root="$verify_root/linux-made-sane-ce-$version-$RUNTIME"
[[ "$(cat "$package_root/edition.txt")" == "ce" ]] || die "package edition marker is not ce"
[[ "$(cat "$package_root/version.txt")" == "$version" ]] || die "package version marker does not match $version"
[[ "$(cat "$package_root/source-commit.txt")" == "$source_commit" ]] || die "package source commit marker does not match $source_commit"
"$CHECKOUT_ROOT/scripts/smoke-cold-start.sh" --app "$package_root/app/LinuxMadeSane.Web"

log "Staging verified CE $version for the public website."
PACKAGE_DIR="$PACKAGE_DIR" \
COMMUNITY_RELEASE_ROOT="$COMMUNITY_RELEASE_ROOT" \
PRO_RELEASE_ROOT="$WORK_ROOT/pro-not-managed" \
LINUX_MADE_SANE_VERSION="$version" \
LINUX_MADE_SANE_SOURCE_COMMIT="$source_commit" \
RUNTIMES="$RUNTIME" \
STAGE_EDITIONS=community \
KEEP_ONLY_LATEST_PUBLIC_SITE_RELEASE=true \
"$CHECKOUT_ROOT/scripts/stage-public-release-assets.sh"

expected_size="$(wc -c < "$artifact" | tr -d ' ')"
expected_sha="$(sha256sum "$artifact" | awk '{print $1}')"
public_manifest="$(curl -fsS --retry 6 --retry-delay 2 --max-time 30 "$PUBLIC_BASE_URL/api/downloads/manifest")"
PUBLIC_MANIFEST="$public_manifest" python3 - "$version" "$expected_size" "$expected_sha" <<'PY'
import json
import os
import sys

version, expected_size, expected_sha = sys.argv[1], int(sys.argv[2]), sys.argv[3]
manifest = json.loads(os.environ["PUBLIC_MANIFEST"])
if manifest.get("latestCommunityVersion") != version:
    raise SystemExit(f"public manifest does not report {version} as latest Community release")
release = next((item for item in manifest.get("community", []) if item.get("version") == version), None)
if release is None or release.get("sizeBytes") != expected_size or release.get("sha256") != expected_sha:
    raise SystemExit("public manifest artifact metadata does not match the verified package")
PY

content_range="$(
  curl -fsS --retry 3 --max-time 30 -r 0-0 -D - -o /dev/null \
    "$PUBLIC_BASE_URL/downloads/community/releases/$version/$RUNTIME?source=automatic-release-verification" |
    tr -d '\r' | awk 'BEGIN { IGNORECASE=1 } /^content-range:/ { print $0 }'
)"
[[ "$content_range" == *"/$expected_size" ]] || die "public download did not report the complete artifact length"

printf '%s\n%s\n' "$source_commit" "$version" > "$STATE_FILE.tmp"
mv "$STATE_FILE.tmp" "$STATE_FILE"
log "Published and externally verified CE $version from $source_commit."
