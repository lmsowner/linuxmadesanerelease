# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE.md for details.

# Use a release-local SDK so publishing never changes the machine's shared .NET installation.
lms_prepare_release_dotnet() {
  local work="$1" sdk channel host_os host_arch url checksum archive
  channel="$(python3 -c 'import json,sys; print(".".join(json.load(open(sys.argv[1]))["sdk"]["version"].split(".")[:2]))' "$REPO_ROOT/global.json")"
  curl --fail --silent --show-error --location --retry 3 --max-time 90 \
    "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$channel/releases.json" -o "$work/releases.json"
  case "$(uname -s)" in Linux) host_os=linux ;; Darwin) host_os=osx ;; *) lms_die "Unsupported release build OS" ;; esac
  case "$(uname -m)" in x86_64) host_arch=x64 ;; aarch64|arm64) host_arch=arm64 ;; *) lms_die "Unsupported release build architecture" ;; esac
  python3 - "$work/releases.json" "$channel" "$host_os-$host_arch" > "$work/sdk.txt" <<'PY'
import json, re, sys
metadata, channel, rid = sys.argv[1:]
with open(metadata) as file:
    feed = json.load(file)
runtime, sdk = feed["latest-runtime"], feed["latest-sdk"]
for version in (runtime, sdk):
    if not re.fullmatch(re.escape(channel) + r"\.\d+", version):
        sys.exit(f"Unexpected stable .NET {channel} version: {version}")
release = next(r for r in feed["releases"] if r["runtime"]["version"] == runtime and r["aspnetcore-runtime"]["version"] == runtime)
package = next(s for s in release["sdks"] if s["version"] == sdk)
asset = next(f for f in package["files"] if f["rid"] == rid and f["name"].endswith(".tar.gz"))
if not re.fullmatch("[0-9a-fA-F]{128}", asset["hash"]) or not asset["url"].startswith("https://"):
    sys.exit("Invalid SDK release metadata")
print(runtime, sdk, asset["url"], asset["hash"].lower())
PY
  read -r LMS_RELEASE_RUNTIME sdk url checksum < "$work/sdk.txt"
  if [[ "$(dotnet --version 2>/dev/null || true)" != "$sdk" ]]; then
    archive="$work/sdk.tar.gz"
    curl --fail --silent --show-error --location --retry 3 --max-time 600 "$url" -o "$archive"
    python3 - "$archive" "$checksum" <<'PY'
import hashlib, sys
digest = hashlib.sha512()
with open(sys.argv[1], "rb") as file:
    for chunk in iter(lambda: file.read(1024 * 1024), b""):
        digest.update(chunk)
if digest.hexdigest() != sys.argv[2]:
    sys.exit("Release SDK checksum mismatch")
PY
    mkdir -p "$work/sdk"
    tar -xzf "$archive" -C "$work/sdk"
    export DOTNET_ROOT="$work/sdk"
    export PATH="$DOTNET_ROOT:$PATH"
  fi
  [[ "$(dotnet --version)" == "$sdk" ]] || lms_die "Could not select release SDK $sdk"
  lms_log "Building with SDK $sdk for .NET and ASP.NET Core $LMS_RELEASE_RUNTIME"
}

lms_verify_bundled_dotnet() {
  python3 - "$1" "$LMS_RELEASE_RUNTIME" <<'PY'
import json, sys
config, expected = sys.argv[1:]
with open(config) as file:
    frameworks = json.load(file)["runtimeOptions"].get("includedFrameworks", [])
versions = {f["name"]: f["version"] for f in frameworks}
for name in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"):
    if versions.get(name) != expected:
        sys.exit(f"{config}: expected {name} {expected}, found {versions.get(name)}")
PY
}
