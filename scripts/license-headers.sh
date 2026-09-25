#!/usr/bin/env bash

# Copyright (c) Linux Made Sane.
# Licensed under the Business Source License 1.1. See LICENSE for details.


set -euo pipefail

usage() {
  cat <<'USAGE'
Usage:
  scripts/license-headers.sh --check [--root PATH] [--license-file LICENSE]
  scripts/license-headers.sh --apply [--root PATH] [--license-file LICENSE]

Adds or verifies Linux Made Sane copyright and BSL notices on owned source
files. Vendored third-party browser assets are intentionally excluded.
USAGE
}

MODE=""
ROOT="."
LICENSE_FILE="LICENSE"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --check)
      MODE="check"
      shift
      ;;
    --apply)
      MODE="apply"
      shift
      ;;
    --root)
      ROOT="${2:-}"
      [[ -n "$ROOT" ]] || { printf 'error: --root requires a path\n' >&2; exit 1; }
      shift 2
      ;;
    --license-file)
      LICENSE_FILE="${2:-}"
      [[ -n "$LICENSE_FILE" ]] || { printf 'error: --license-file requires a value\n' >&2; exit 1; }
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      printf 'error: unknown argument: %s\n' "$1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

[[ -n "$MODE" ]] || { usage >&2; exit 1; }
[[ -d "$ROOT" ]] || { printf 'error: root does not exist: %s\n' "$ROOT" >&2; exit 1; }

ROOT="$(cd "$ROOT" && pwd)"

header_for() {
  case "$1" in
    *.cs)
      printf '// Copyright (c) Linux Made Sane.\n// Licensed under the Business Source License 1.1. See %s for details.\n\n' "$LICENSE_FILE"
      ;;
    *.razor)
      printf '@* Copyright (c) Linux Made Sane.\n   Licensed under the Business Source License 1.1. See %s for details. *@\n\n' "$LICENSE_FILE"
      ;;
    *.css|*.js)
      printf '/* Copyright (c) Linux Made Sane.\n * Licensed under the Business Source License 1.1. See %s for details. */\n\n' "$LICENSE_FILE"
      ;;
    *.sh)
      printf '# Copyright (c) Linux Made Sane.\n# Licensed under the Business Source License 1.1. See %s for details.\n\n' "$LICENSE_FILE"
      ;;
  esac
}

has_header() {
  local file="$1"
  head -n 12 "$file" | grep -Fq 'Copyright (c)' &&
    head -n 12 "$file" | grep -Fq 'Licensed under the Business Source License 1.1.'
}

copy_mode() {
  local source_file="$1"
  local target_file="$2"
  local mode

  if chmod --reference="$source_file" "$target_file" 2>/dev/null; then
    return
  fi

  mode="$(stat -f '%Lp' "$source_file" 2>/dev/null || stat -c '%a' "$source_file")"
  chmod "$mode" "$target_file"
}

normalize_owned_credit() {
  local file="$1"
  local temp_file
  has_header "$file" || return 0
  temp_file="$(mktemp)"
  # Canonicalize only an existing LMS header; never rewrite upstream notices.
  sed -E '1,12s/^((\/\/|\/\*|@\*|#) Copyright \(c\) ).*\.$/\1Linux Made Sane./' "$file" > "$temp_file"
  copy_mode "$file" "$temp_file"
  mv "$temp_file" "$file"
}

remove_utf8_bom() {
  local file="$1"
  local temp_file

  if ! LC_ALL=C grep -q $'\xEF\xBB\xBF' "$file"; then
    return
  fi

  temp_file="$(mktemp)"
  LC_ALL=C sed $'s/\xEF\xBB\xBF//g' "$file" > "$temp_file"
  copy_mode "$file" "$temp_file"
  mv "$temp_file" "$file"
}

apply_header() {
  local file="$1"
  local temp_file
  temp_file="$(mktemp)"

  if [[ "$file" == *.sh ]] && head -n 1 "$file" | grep -q '^#!'; then
    head -n 1 "$file" > "$temp_file"
    printf '\n' >> "$temp_file"
    header_for "$file" >> "$temp_file"
    tail -n +2 "$file" >> "$temp_file"
  else
    header_for "$file" > "$temp_file"
    cat "$file" >> "$temp_file"
  fi

  copy_mode "$file" "$temp_file"
  mv "$temp_file" "$file"
}

collect_files() {
  find "$ROOT" \
    \( -type d \( -name '.git' \
       -o -name '.run' \
       -o -name '.idea' \
       -o -name 'artifacts' \
       -o -name 'bin' \
       -o -name 'obj' \
       -o -name 'data' \
       -o -name 'packages' \
       -o -name 'TestResults' \) \) -prune \
    -o \( -path "$ROOT/src/LinuxMadeSane.Web/wwwroot/lib" \
       -o -path "$ROOT/src/LinuxMadeSane.Web/wwwroot/images" \) -prune \
    -o -type f \( -name '*.cs' -o -name '*.razor' -o -name '*.css' -o -name '*.js' -o -name '*.sh' \) \
    -print0
}

missing=0
checked=0

while IFS= read -r -d '' file; do
  checked=$((checked + 1))
  if [[ "$MODE" == "apply" ]]; then
    remove_utf8_bom "$file"
    normalize_owned_credit "$file"
  fi

  if has_header "$file"; then
    continue
  fi

  if [[ "$MODE" == "check" ]]; then
    printf 'missing license header: %s\n' "${file#$ROOT/}"
    missing=$((missing + 1))
  else
    apply_header "$file"
  fi
done < <(collect_files)

if [[ "$MODE" == "check" && "$missing" -gt 0 ]]; then
  printf 'checked %d files; %d missing headers\n' "$checked" "$missing" >&2
  exit 1
fi

if [[ "$MODE" == "check" ]]; then
  printf 'checked %d files; all owned source files have license headers\n' "$checked"
else
  printf 'applied license headers across %d owned source files\n' "$checked"
fi
