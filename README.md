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
curl -fsSL https://raw.githubusercontent.com/lmsowner/linuxmadesanerelease/main/install.sh | sudo VERSION=2026.05.06.1 bash
```

## Supported Linux Builds

Release assets are published as self-contained tarballs for:

- `linux-x64`
- `linux-arm64`
- `linux-arm`

The installer detects the runtime ID from `uname -m`. Override it with `RID=linux-arm64` or `RID=linux-arm` when testing unusual Raspberry Pi or distro images.

## Optional Host Tools

The core app installs without these tools, but some workflows need them:

- `ffmpeg` for media preview/transcoding
- Samba client tooling for share discovery
- `cifs-utils` for mount workflows

The installer tries to install those packages when it recognizes the distro package manager. Use `--skip-host-packages` when you manage packages separately.

## More Docs

- [Install details](docs/INSTALL.md)
- [Release assets](docs/RELEASES.md)
- [Distro and Raspberry Pi testing](docs/TESTING.md)

This public repository intentionally contains installer/docs only. Application source, Pro packages, portal packages, private configuration, and credentials are not part of the public release channel.
