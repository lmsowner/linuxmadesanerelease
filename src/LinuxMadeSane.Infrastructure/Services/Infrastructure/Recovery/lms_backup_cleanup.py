# Copyright (c) Linux Made Sane.
# Licensed under the Business Source License 1.1. See LICENSE for details.
"""Remove only LMS's temporary staging bundle, including root-owned recovery files."""
import os
import pathlib
import re
import shutil
import sys
import json
import subprocess


def command(args):
    result = subprocess.run(args, capture_output=True, text=True, timeout=60)
    if result.returncode: raise ValueError('Temporary recovery workspace cleanup failed: ' + result.stderr.strip())
    return result.stdout


def release_workspace(path):
    marker = path.with_name(path.name + '.workspace.json')
    if not marker.exists(): return
    if marker.is_symlink() or marker.stat().st_uid != 0:
        raise ValueError('Refusing an untrusted recovery workspace record')
    state = json.loads(marker.read_text())
    suffix = path.name.removeprefix('lms-backup-staging-')
    mapper = 'lms-recovery-' + suffix
    image = pathlib.Path(state['image'])
    loop = state.get('loop', '')
    if state['mapper'] != mapper or not image.is_absolute() or image.name != 'work.luks' or image.parent.name != '.lms-recovery-work-' + suffix or image.is_symlink():
        raise ValueError('Refusing an unexpected temporary recovery image')
    if loop and not re.fullmatch(r'/dev/loop[0-9]+', loop):
        raise ValueError('Refusing an unexpected recovery loop device')
    mounted = subprocess.run(['findmnt', '--noheadings', '--mountpoint', str(path), '--output', 'SOURCE'], capture_output=True, text=True)
    if mounted.returncode == 0:
        if mounted.stdout.strip() != '/dev/mapper/' + mapper:
            raise ValueError('Refusing to unmount a different filesystem at the staging path')
        command(['umount', str(path)])
    if pathlib.Path('/dev/mapper/' + mapper).exists(): command(['cryptsetup', 'close', mapper])
    if loop:
        current = subprocess.run(['losetup', '--noheadings', '--output', 'BACK-FILE', loop], capture_output=True, text=True)
        if current.returncode == 0 and current.stdout.strip():
            if current.stdout.strip() != str(image): raise ValueError('Refusing to detach a different loop device')
            command(['losetup', '--detach', loop])
    storage = json.loads(command(['findmnt', '--json', '--target', str(image.parent.parent), '--output', 'TARGET,SOURCE,FSTYPE']))['filesystems'][0]
    if storage != state['storageMount']:
        raise ValueError('Reconnect the backup share to remove its temporary encrypted image; its original mount is unavailable')
    if image.exists(): image.unlink()
    if image.parent.exists(): image.parent.rmdir()  # Never recursively remove anything on backup storage.
    marker.unlink()


def cleanup(bundle, base='/dev/shm'):
    path = pathlib.Path(bundle)
    expected = pathlib.Path(base)
    if str(expected) != '/dev/shm' and (not expected.is_absolute() or expected.name != 'backup-staging' or expected.is_symlink()):
        raise ValueError('Refusing an unrecognized staging base directory')
    if not path.is_absolute() or path.parent != expected or not re.fullmatch(r'lms-backup-staging-[0-9a-f]{32}', path.name):
        raise ValueError('Refusing cleanup outside an LMS backup staging directory')
    if path.is_symlink():
        raise ValueError('Refusing cleanup of a symlink staging directory')
    release_workspace(path)
    if os.path.lexists(path):
        shutil.rmtree(path)


if __name__ == '__main__':
    try:
        cleanup(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else '/dev/shm')
    except Exception as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
