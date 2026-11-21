# Copyright (c) 2026 LMS Developer. All rights reserved.
"""Invoked by the privileged command runner, never by the web service directly."""
import fcntl
import os
import pathlib
import stat
import sys
import tempfile


def empty_mount(path):
    target = pathlib.Path(path)
    if any(part.is_symlink() for part in [target, *target.parents]):
        raise RuntimeError('The mount folder must not be a symbolic link.')
    try:
        info = target.stat()
    except FileNotFoundError:
        # Probe the closest existing ancestor with privileged visibility.
        parent = target.parent
        while not parent.exists():
            parent = parent.parent
        if not parent.is_dir() or not os.access(parent, os.W_OK | os.X_OK):
            raise RuntimeError('The mount folder cannot be created.')
        return
    if not stat.S_ISDIR(info.st_mode) or any(target.iterdir()):
        raise RuntimeError('The mount folder already contains files or is not a directory.')


def field(value):
    if not value or '\n' in value or '\r' in value or '\x00' in value:
        raise RuntimeError('Invalid persistent mount field.')
    return value.replace('\\', '\\134').replace(' ', '\\040').replace('\t', '\\011')


def fstab(path, values, check_target=None):
    target = pathlib.Path(path)
    if target.is_symlink() or not target.is_file():
        raise RuntimeError('fstab must be an existing regular file, not a symbolic link.')
    lock_fd = os.open(str(target) + '.lms-lock', os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    with os.fdopen(lock_fd, 'w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        info = target.stat()
        original = target.read_text()
        if check_target:
            escaped_target = field(check_target)
            for line in original.splitlines():
                parts = line.split()
                if parts and not line.lstrip().startswith('#') and len(parts) > 1 and parts[1] == escaped_target and '# linuxmadesane-storage' not in line:
                    raise RuntimeError('An unmanaged fstab entry already uses this mount folder.')
        fd, temporary = tempfile.mkstemp(prefix='.lms-fstab-', dir=target.parent)
        try:
            with os.fdopen(fd, 'w') as staged:
                os.fchmod(staged.fileno(), stat.S_IMODE(info.st_mode))
                os.fchown(staged.fileno(), info.st_uid, info.st_gid)
                if values:
                    if len(values) != 6:
                        raise RuntimeError('Six persistent mount fields are required.')
                    escaped = [field(value) for value in values]
                    if escaped[4] not in ('0', '1') or escaped[5] not in ('0', '1', '2'):
                        raise RuntimeError('Invalid fstab dump or check order.')
                    marker = '# linuxmadesane-storage'
                    lines = []
                    for line in original.splitlines():
                        parts = line.split()
                        if parts and not line.lstrip().startswith('#') and len(parts) > 1 and parts[1] == escaped[1]:
                            if marker not in line:
                                raise RuntimeError('An unmanaged fstab entry already uses this mount folder. No changes made.')
                            continue
                        lines.append(line)
                    lines.append(' '.join(escaped) + ' ' + marker)
                    content = '\n'.join(lines) + '\n'
                else:
                    content = original
                staged.write(content)
                staged.flush()
                os.fsync(staged.fileno())
            if values:
                # Keep the previous contents before atomically replacing fstab.
                backup_fd, backup = tempfile.mkstemp(prefix=target.name + '.lms-backup-', dir=target.parent)
                with os.fdopen(backup_fd, 'w') as saved:
                    saved.write(original)
                    saved.flush()
                    os.fsync(saved.fileno())
                os.replace(temporary, target)
                directory = os.open(target.parent, os.O_RDONLY | os.O_DIRECTORY)
                try:
                    os.fsync(directory)
                finally:
                    os.close(directory)
                print('Persistent mount saved; previous fstab retained at ' + backup)
            else:
                print('Privileged fstab read and atomic-write staging succeeded.')
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)


def main():
    if os.geteuid() != 0:
        raise RuntimeError('Storage administration requires root privileges through sudo.')
    mode, *args = sys.argv[1:]
    if mode == 'mount-check':
        empty_mount(args[0])
    elif mode == 'fstab-check':
        fstab(args[0], [], args[1] if len(args) > 1 else None)
    elif mode == 'fstab-write':
        fstab(args[0], args[1:])
    elif mode != 'privilege-check':
        raise RuntimeError('Unknown storage operation.')


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
