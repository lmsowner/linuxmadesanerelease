# Linux Made Sane Releases

This repository is the public release channel for Linux Made Sane.

Linux Made Sane is a local-first Linux administration web app for managing hosts, terminals, files, services, shares, remote access, AI-assisted operations, and media-library workflows.

## Quick Install

On a Linux machine with systemd:

```bash
curl -fsSL https://raw.githubusercontent.com/lmsowner/linuxmadesanerelease/main/install.sh | sudo bash
```

Then open:

```text
http://127.0.0.1:5080
```

For a different port:

```bash
curl -fsSL https://raw.githubusercontent.com/lmsowner/linuxmadesanerelease/main/install.sh | sudo PORT=5095 bash
```

For a specific release:

```bash
curl -fsSL https://raw.githubusercontent.com/lmsowner/linuxmadesanerelease/main/install.sh | sudo VERSION=2026.05.07.0 bash
```

## Supported Linux Builds

Release assets are published as self-contained tarballs for:

- `linux-x64`
- `linux-arm64`
- `linux-arm`

The installer detects the runtime ID from `uname -m`. Override it with `RID=linux-arm64` or `RID=linux-arm` when testing unusual Raspberry Pi or distro images.

For early distro testing, CE package tarballs may be served directly from this repository under `packages/` until the formal GitHub Releases flow is used.

## Source

This repository also contains the CE source tree needed to build the public app. See [CE source](docs/SOURCE.md).

Build from source:

```bash
dotnet build src/LinuxMadeSane.Web/LinuxMadeSane.Web.csproj
```

## Optional Host Tools

The core app installs without these tools, but some workflows need them:

- `ffmpeg` for media preview/transcoding
- Samba client tooling for share discovery
- `cifs-utils` for mount workflows

The installer tries to install those packages when it recognizes the distro package manager. Use `--skip-host-packages` when you manage packages separately.

## Security Model

LMS is an automation control plane. The installer creates a dedicated `linuxmadesane` service account for the web service, but it does not silently grant root access.

For unattended terminals, runbooks, patching, and service repair, configure a dedicated LMS runner account with key-based login and deliberate passwordless sudo. See [Security model](docs/SECURITY.md).

## More Docs

- [Install details](docs/INSTALL.md)
- [Security model](docs/SECURITY.md)
- [Release assets](docs/RELEASES.md)
- [CE source](docs/SOURCE.md)
- [Distro and Raspberry Pi testing](docs/TESTING.md)

This public repository intentionally contains CE source, installer/docs, and CE release assets only. Pro packages, portal packages, private configuration, and credentials are not part of the public release channel.
