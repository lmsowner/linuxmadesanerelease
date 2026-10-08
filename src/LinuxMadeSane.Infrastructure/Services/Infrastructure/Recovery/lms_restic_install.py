# Copyright (c) Linux Made Sane.
# Licensed under the Business Source License 1.1. See LICENSE for details.
"""Install the latest stable official restic binary after signature/hash validation."""
import bz2
import hashlib
import json
import os
import pathlib
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.request

FINGERPRINT = 'CF8F18F2844575973F79D4E191A6868BD3F7A907'
TARGET = pathlib.Path('/usr/local/bin/restic')


def download(url, limit):
    request = urllib.request.Request(url, headers={'User-Agent': 'LinuxMadeSane-restic-installer', 'Accept': 'application/vnd.github+json' if url.startswith('https://api.github.com/') else '*/*'})
    with urllib.request.urlopen(request, timeout=60) as response:
        data = response.read(limit + 1)
    if len(data) > limit:
        raise RuntimeError('Restic download exceeds its expected size limit')
    return data


def run(args):
    result = subprocess.run(args, capture_output=True, text=True, timeout=60)
    if result.returncode:
        raise RuntimeError('Restic verification failed: ' + result.stderr.strip())
    return result.stdout


def install(verify_only=False):
    arch = {'x86_64': 'amd64', 'aarch64': 'arm64', 'arm64': 'arm64', 'i386': '386', 'i686': '386'}.get(platform.machine())
    if arch is None:
        raise RuntimeError('Automatic restic installation is not available for this CPU architecture')
    release = json.loads(download('https://api.github.com/repos/restic/restic/releases/latest', 2 * 1024 * 1024))
    tag = release['tag_name']
    if release.get('draft') or release.get('prerelease') or not re.fullmatch(r'v\d+\.\d+\.\d+', tag):
        raise RuntimeError('The official release feed did not return a stable restic release')
    version = tag[1:]
    asset = f'restic_{version}_linux_{arch}.bz2'
    base = f'https://github.com/restic/restic/releases/download/{tag}/'
    with tempfile.TemporaryDirectory(prefix='lms-restic-install-') as temp:
        work = pathlib.Path(temp)
        home = work / 'gnupg'
        home.mkdir(mode=0o700)
        key = work / 'key.asc'
        key.write_bytes(download('https://restic.net/gpg-key-alex.asc', 128 * 1024))
        gpg = ['gpg', '--batch', '--no-options', '--homedir', str(home)]
        keys = run(gpg + ['--with-colons', '--show-keys', str(key)])
        fingerprints = [line.split(':')[9] for line in keys.splitlines() if line.startswith('fpr:')]
        if FINGERPRINT not in fingerprints:
            raise RuntimeError('The restic signing key does not match the official pinned fingerprint')
        run(gpg + ['--import', str(key)])
        sums = work / 'SHA256SUMS'
        signature = work / 'SHA256SUMS.asc'
        sums.write_bytes(download(base + sums.name, 128 * 1024))
        signature.write_bytes(download(base + signature.name, 128 * 1024))
        status = run(gpg + ['--status-fd', '1', '--verify', str(signature), str(sums)])
        valid = [line.split() for line in status.splitlines() if line.startswith('[GNUPG:] VALIDSIG ')]
        if not any(line[2] == FINGERPRINT or line[-1] == FINGERPRINT for line in valid):
            raise RuntimeError('The release checksums were not signed by the official restic signing key')
        matches = [line.split()[0] for line in sums.read_text().splitlines() if len(line.split()) == 2 and line.split()[1].lstrip('*') == asset]
        if len(matches) != 1 or not re.fullmatch('[a-fA-F0-9]{64}', matches[0]):
            raise RuntimeError('The signed checksums do not identify the required restic binary')
        compressed = download(base + asset, 64 * 1024 * 1024)
        if hashlib.sha256(compressed).hexdigest() != matches[0].lower():
            raise RuntimeError('The restic binary checksum failed; the installed binary has not been changed')
        binary = bz2.BZ2Decompressor().decompress(compressed, max_length=128 * 1024 * 1024)
        if len(binary) >= 128 * 1024 * 1024:
            raise RuntimeError('The decompressed restic binary exceeds its size limit')
        candidate = work / 'restic'
        candidate.write_bytes(binary)
        candidate.chmod(0o755)
        reported = run([str(candidate), 'version']).strip()
        if not reported.startswith('restic ' + version + ' '):
            raise RuntimeError('The downloaded restic executable reports an unexpected version')
        if '--insecure-no-password' not in run([str(candidate), 'help']):
            raise RuntimeError('The current restic release does not support password-free backups')
        if verify_only:
            print('Verified official stable release, signing key, signature, checksum and executable: ' + reported)
            return
        TARGET.parent.mkdir(parents=True, exist_ok=True)
        if TARGET.is_symlink():
            raise RuntimeError('The existing /usr/local/bin/restic is a symlink; it was preserved')
        descriptor, staged = tempfile.mkstemp(prefix='.lms-restic-', dir=TARGET.parent)
        try:
            with os.fdopen(descriptor, 'wb') as output:
                output.write(binary)
                output.flush()
                os.fsync(output.fileno())
            os.chmod(staged, 0o755)
            if TARGET.exists():
                backup = TARGET.with_name('restic.lms-previous')
                if backup.is_symlink():
                    raise RuntimeError('The existing restic rollback path is a symlink; installation was cancelled')
                shutil.copy2(TARGET, backup)
            os.replace(staged, TARGET)
        finally:
            if os.path.exists(staged):
                os.unlink(staged)
        print('Installed verified latest stable release: ' + reported + '. Distribution-managed /usr/bin/restic and backup repositories were preserved.')


if __name__ == '__main__':
    try:
        install('--verify-only' in sys.argv)
    except Exception as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
