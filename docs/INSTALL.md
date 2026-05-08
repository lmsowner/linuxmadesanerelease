# Install Details

## One-Line Install

```bash
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash
```

The public installer:

- detects `linux-x64`, `linux-arm64`, or `linux-arm`
- downloads the matching CE tarball from `https://www.linuxmadesane.com`
- verifies `SHA256SUMS` when available
- installs to `/opt/linuxmadesane/ce`
- stores data in `/var/lib/linuxmadesane/ce`
- writes config to `/etc/linuxmadesane/ce/service.env`
- creates the `linux-made-sane.service` systemd unit
- creates a dedicated `linuxmadesane` service account for the LMS web service
- configures localhost SSH runner access for terminal, runbook, and scheduled automation workflows unless disabled
- stops any existing `linux-made-sane.service` before replacing application files during an update
- starts the service and checks `/healthz`

See [Security model](SECURITY.md) before changing runner, SSH, or sudo defaults.

## Install Options

```bash
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash -s -- --port 5095
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo env LMS_VERSION=2026.05.08.0 bash
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo env RID=linux-arm64 bash
```

Supported environment variables:

- `LMS_VERSION`: release version
- `RID`: `linux-x64`, `linux-arm64`, or `linux-arm`
- `LMS_SERVICE_PORT`: HTTP port, default `5080`
- `LMS_INSTALL_ROOT`: default `/opt/linuxmadesane/ce`
- `LMS_DATA_ROOT`: default `/var/lib/linuxmadesane/ce`
- `LMS_CONFIG_ROOT`: default `/etc/linuxmadesane/ce`
- `LMS_SERVICE_UNIT`: default `linux-made-sane.service`
- `LMS_INSTALL_SYSTEM_PACKAGES`: set `false` to skip apt package installation
- `LMS_CONFIGURE_LOCAL_SSH`: set `false` to skip localhost SSH runner setup
- `LMS_ENABLE_LOCAL_SUDO`: set `false` to skip passwordless sudo setup for local automation
- `LMS_BASE_URL`: override the public website base URL for staging tests

Supported flags:

```bash
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash -s -- --port 5095
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash -s -- --no-system-packages
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash -s -- --no-local-ssh
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash -s -- --no-local-sudo
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash -s -- --no-start
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash -s -- --uninstall
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash -s -- --purge
```

## Service Commands

```bash
sudo systemctl status linux-made-sane.service
sudo systemctl start linux-made-sane.service
sudo systemctl stop linux-made-sane.service
sudo systemctl restart linux-made-sane.service
sudo journalctl -u linux-made-sane.service -n 100 --no-pager
```

## Cold Start

The app creates its SQLite database on first startup. A healthy first start should create:

```text
/var/lib/linuxmadesane/ce/linuxmadesane.db
```

The health endpoint should return HTTP 200:

```bash
curl -fsS http://127.0.0.1:5080/healthz
```
