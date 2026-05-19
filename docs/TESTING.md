# Distro And Raspberry Pi Testing

Use the public installer on clean machines where possible. That gives the closest signal to a real user install.

## Minimum Smoke Test

```bash
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash
curl -fsS http://127.0.0.1:5080/healthz
sudo systemctl status linux-made-sane.service
```

Record:

- distro name and version
- CPU architecture from `uname -m`
- selected release asset
- whether `/healthz` returns HTTP 200
- first-run database path
- any missing optional host packages

## Raspberry Pi Notes

Use `linux-arm64` for 64-bit Raspberry Pi OS or Ubuntu Server images:

```bash
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo env RID=linux-arm64 bash
```

Use `linux-arm` for 32-bit images:

```bash
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo env RID=linux-arm bash
```

## Useful Diagnostics

```bash
uname -a
cat /etc/os-release
systemctl status linux-made-sane.service
journalctl -u linux-made-sane.service -n 200 --no-pager
ls -lah /opt/linuxmadesane/ce /var/lib/linuxmadesane/ce /etc/linuxmadesane/ce
```

## Roll Forward

Re-run the installer to update to the latest available Community release:

```bash
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash
```

Short link:

```bash
curl -fsSL https://bit.ly/4tCQKCN | sudo bash
```

The public installer is expected to install the current Community release only.

## Remove Test Install

```bash
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash -s -- --uninstall
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash -s -- --purge
```
