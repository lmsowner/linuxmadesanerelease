#!/usr/bin/env python3
# Copyright (c) 2026 LMS Developer. All rights reserved.
"""Install the unmodified TigerVNC Wayland server where the distro predates it."""
import hashlib
import fcntl
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import urllib.request

VERSION = '1.16.2'
SOURCE = 'https://github.com/TigerVNC/tigervnc/archive/refs/tags/v1.16.2.tar.gz'
SHA256 = 'b107c0c8b8a962594281690366c24186e95c2ea4a169acbc0076aa62ed01f467'
ROOT = Path('/var/lib/linuxmadesane/desktop-tools')


def install():
    ROOT.mkdir(parents=True, exist_ok=True)
    with (ROOT / '.tigervnc-install.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        return install_locked()


def install_locked():
    server = shutil.which('w0vncserver')
    password = shutil.which('tigervncpasswd') or shutil.which('vncpasswd')
    if server and password:
        return server, password
    destination = ROOT / ('tigervnc-' + VERSION)
    server, password = destination / 'w0vncserver', destination / 'vncpasswd'
    if server.is_file() and password.is_file():
        return str(server), str(password)
    ROOT.mkdir(parents=True, exist_ok=True)
    # All subprocess output stays on stderr, separate from the helper's JSON response.
    def run(args):
        subprocess.run(args, check=True, stdout=sys.stderr, stderr=sys.stderr, timeout=900)
    run(['env', 'DEBIAN_FRONTEND=noninteractive', 'apt-get', '-o', 'DPkg::Lock::Timeout=120', 'update'])
    run(['env', 'DEBIAN_FRONTEND=noninteractive', 'apt-get', '-o', 'DPkg::Lock::Timeout=120', 'install', '-y', '--no-install-recommends',
         'build-essential', 'cmake', 'pkg-config', 'libx11-dev', 'libxrandr-dev', 'libxdamage-dev', 'libxtst-dev', 'libxfixes-dev',
         'libpixman-1-dev', 'libjpeg-dev', 'zlib1g-dev', 'libpam0g-dev', 'libglib2.0-dev', 'libpipewire-0.3-dev',
         'uuid-dev', 'libwayland-dev', 'libxkbcommon-dev'])
    with tempfile.TemporaryDirectory(prefix='tigervnc-build-', dir=ROOT) as temporary:
        work = Path(temporary)
        archive = work / 'source.tar.gz'
        with urllib.request.urlopen(SOURCE, timeout=60) as response:
            archive.write_bytes(response.read())
        if hashlib.sha256(archive.read_bytes()).hexdigest() != SHA256:
            raise RuntimeError('TigerVNC source verification failed.')
        source = work / 'source'; source.mkdir()
        run(['tar', '-xzf', str(archive), '--strip-components=1', '--no-same-owner', '-C', str(source)])
        build = work / 'build'
        run(['cmake', '-S', str(source), '-B', str(build), '-DBUILD_VIEWER=OFF', '-DENABLE_WAYLAND=ON',
             '-DENABLE_H264=OFF', '-DENABLE_NLS=OFF', '-DENABLE_GNUTLS=OFF', '-DENABLE_NETTLE=OFF', '-DENABLE_SYSTEMD=OFF', '-DENABLE_PWQUALITY=OFF'])
        run(['cmake', '--build', str(build), '--parallel', '2', '--target', 'w0vncserver', 'vncpasswd'])
        destination.mkdir(exist_ok=True)
        for binary, target in [(build / 'unix/w0vncserver/w0vncserver', server), (build / 'unix/vncpasswd/vncpasswd', password)]:
            shutil.copyfile(binary, target); target.chmod(0o755)
        shutil.copyfile(source / 'LICENCE.TXT', destination / 'LICENCE.TXT')
    return str(server), str(password)
