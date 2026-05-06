# Distro And Raspberry Pi Testing

Use the public installer on clean machines where possible. That gives the closest signal to a real user install.

## Minimum Smoke Test

```bash
curl -fsSL https://raw.githubusercontent.com/lmsowner/linuxmadesanerelease/main/install.sh | sudo bash
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
curl -fsSL https://raw.githubusercontent.com/lmsowner/linuxmadesanerelease/main/install.sh | sudo RID=linux-arm64 bash
```

Use `linux-arm` for 32-bit images:

```bash
curl -fsSL https://raw.githubusercontent.com/lmsowner/linuxmadesanerelease/main/install.sh | sudo RID=linux-arm bash
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

Re-run the installer with a newer `VERSION`. Releases are installed into versioned folders and `current` is updated to point at the newest release.

## Remove Test Install

```bash
sudo systemctl disable --now linux-made-sane.service
sudo rm -f /etc/systemd/system/linux-made-sane.service
sudo systemctl daemon-reload
sudo rm -rf /opt/linuxmadesane/ce /var/lib/linuxmadesane/ce /etc/linuxmadesane/ce
```
