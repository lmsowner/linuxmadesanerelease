# Linux Made Sane Community Edition

This repository is the public Community Edition release and documentation surface for Linux Made Sane.

Linux Made Sane is a local-first Linux administration web app for managing hosts, terminals, files, services, shares, remote access, AI-assisted operations, and media-library workflows.

Current Community downloads are served by the public website:

```text
https://www.linuxmadesane.com
```

Public links:

- Website: https://www.linuxmadesane.com
- LinkedIn: https://www.linkedin.com/in/richardkiernan/
- Public Community Edition repository: https://github.com/lmsowner/linuxmadesanerelease

Do not put the public website project, Pro/Enterprise packages, portal packages, private manifests, license secrets, private configuration, databases, credentials, or proprietary implementation details in this repository.

## Quick Install

On a Linux machine with systemd:

```bash
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash
```

Short link:

```bash
curl -fsSL https://bit.ly/4tCQKCN | sudo bash
```

Then open:

```text
http://127.0.0.1:5080
```

Re-run the same install command to update an existing Community install in place.

Uninstall:

```bash
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash -s -- --uninstall
```

Remove app, data, and config:

```bash
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash -s -- --purge
```

For a different port:

```bash
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash -s -- --port 5095
```

## What The Installer Does

- detects `linux-x64`, `linux-arm64`, or `linux-arm`
- downloads the current matching CE tarball from `https://www.linuxmadesane.com`
- installs to `/opt/linuxmadesane/ce`
- stores data in `/var/lib/linuxmadesane/ce`
- writes config to `/etc/linuxmadesane/ce/service.env`
- creates and manages the `linux-made-sane.service` systemd unit
- installs useful host packages on apt-based systems unless disabled
- configures localhost SSH access for LMS automation unless disabled
- creates a writable local runner workspace for terminals, files, runbooks, and scheduled jobs
- installs the local update helper used by LMS-managed updates
- stops an existing service before replacing files during an update

## Supported Linux Builds

Release assets are published as self-contained tarballs for:

- `linux-x64`
- `linux-arm64`
- `linux-arm`

The installer detects the runtime ID from `uname -m`. Override it with `RID=linux-arm64` or `RID=linux-arm` when testing unusual Raspberry Pi or distro images.

## Source

This repository contains the public CE source subset needed to build the Community app. See [CE source](docs/SOURCE.md).

Build from source:

```bash
dotnet build src/LinuxMadeSane.Web/LinuxMadeSane.Web.csproj
```

## More Docs

- [Install details](docs/INSTALL.md)
- [Security model](docs/SECURITY.md)
- [Release assets](docs/RELEASES.md)
- [CE source](docs/SOURCE.md)
- [Distro and Raspberry Pi testing](docs/TESTING.md)

## License

Linux Made Sane Community Edition is source-available under the Business Source License 1.1.

It is free for personal, homelab, educational, non-profit, and internal commercial use.

A commercial license from Richard D. Kiernan is required for hosted, MSP, SaaS, white-label, resale, or third-party managed-service use.

Linux Made Sane is provided as-is, with no warranties, express or implied. You are responsible for how you install, configure, expose, and operate it, and you use it at your own risk.

Redistributions must retain copyright, license, trademark, and third-party notices.

See [LICENSE.md](LICENSE.md), [NOTICE](NOTICE), [COMMERCIAL-LICENSE.md](COMMERCIAL-LICENSE.md), [TRADEMARKS.md](TRADEMARKS.md), and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
