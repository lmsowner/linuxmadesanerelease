# Install Details

## One-Line Install

```bash
curl -fsSL https://raw.githubusercontent.com/lmsowner/linuxmadesanerelease/main/install.sh | sudo bash
```

The public installer:

- detects `linux-x64`, `linux-arm64`, or `linux-arm`
- downloads the matching CE tarball from GitHub Releases, or from the repo-hosted `packages/` fallback used for early distro testing
- verifies `SHA256SUMS` when available
- installs to `/opt/linuxmadesane/ce`
- stores data in `/var/lib/linuxmadesane/ce`
- writes config to `/etc/linuxmadesane/ce/service.env`
- creates the `linux-made-sane.service` systemd unit
- starts the service and checks `/healthz`

## Install Options

```bash
sudo VERSION=2026.05.06.1 RID=linux-arm64 PORT=5095 bash install.sh
```

Supported environment variables:

- `VERSION`: release version or `latest`
- `RID`: `linux-x64`, `linux-arm64`, or `linux-arm`
- `PORT`: HTTP port, default `5080`
- `INSTALL_ROOT`: default `/opt/linuxmadesane/ce`
- `DATA_ROOT`: default `/var/lib/linuxmadesane/ce`
- `CONFIG_ROOT`: default `/etc/linuxmadesane/ce`
- `SERVICE_UNIT`: default `linux-made-sane.service`
- `INSTALL_HOST_PACKAGES`: set `false` to skip optional host packages
- `RAW_BASE_URL`: override the raw GitHub content base URL for repo-hosted package testing

Supported flags:

```bash
sudo bash install.sh --version 2026.05.06.1 --rid linux-arm64 --port 5095
sudo bash install.sh --skip-host-packages
sudo bash install.sh --no-start
```

## Service Commands

```bash
sudo systemctl status linux-made-sane.service
sudo journalctl -u linux-made-sane.service -n 100 --no-pager
sudo systemctl restart linux-made-sane.service
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
