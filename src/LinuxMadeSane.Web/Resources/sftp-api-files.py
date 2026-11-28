# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE for details.
"""Run a single file operation as the SFTP user inside its chroot."""
import shutil
import errno
import json
import os
import pwd
import secrets
import stat
import sys


def execute(user, root, operation, path, transfer_path):
    transfer = None
    if operation in ("upload", "replace", "download"):
        # LMS creates this file with owner-only permissions outside the SFTP chroot.
        flags = os.O_WRONLY if operation == "download" else os.O_RDONLY
        handle = os.open(transfer_path, flags | os.O_NOFOLLOW)
        transfer = os.fdopen(handle, "wb" if operation == "download" else "rb")
    account = pwd.getpwnam(user)
    if account.pw_uid == 0:
        raise PermissionError()
    parts = path.split("/") if path else []
    if any(p in ("", ".", "..") for p in parts) or (not parts and operation != "list"):
        raise ValueError()
    directory = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
    # Resolve and pin every root-owned component before dropping privilege.
    fd = os.open("/", directory)
    for part in root.strip("/").split("/"):
        child = os.open(part, directory, dir_fd=fd)
        os.close(fd)
        fd = child
        info = os.fstat(fd)
        if info.st_uid != 0 or info.st_mode & 0o022:
            raise PermissionError()
    os.fchdir(fd)
    os.close(fd)
    os.chroot(".")
    os.chdir("/")
    os.setgroups([])
    os.setgid(account.pw_gid)
    os.setuid(account.pw_uid)
    fd = os.open("/files", directory)
    for part in parts if operation == "list" else parts[:-1]:
        child = os.open(part, directory, dir_fd=fd)
        os.close(fd)
        fd = child
    if operation == "list":
        entries = []
        with os.scandir(fd) as items:
            for item in items:
                info = item.stat(follow_symlinks=False)
                folder = stat.S_ISDIR(info.st_mode)
                if folder or stat.S_ISREG(info.st_mode):
                    if len(entries) == 10000:
                        sys.exit(6)
                    entries.append(dict(name=item.name, isDirectory=folder, size=info.st_size))
        print(json.dumps(sorted(entries, key=lambda item: item["name"])))
        return
    name = parts[-1]
    if operation != "upload":
        info = os.stat(name, dir_fd=fd, follow_symlinks=False)
        if not stat.S_ISREG(info.st_mode):
            raise PermissionError()
    if operation == "unlink":
        os.unlink(name, dir_fd=fd)
        return
    if operation == "download":
        with os.fdopen(os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=fd), "rb") as source:
            if not stat.S_ISREG(os.fstat(source.fileno()).st_mode):
                raise PermissionError()
            if os.fstat(source.fileno()).st_size > 50 * 1024 * 1024:
                sys.exit(6)
            total = 0
            while chunk := source.read(65536):
                total += len(chunk)
                if total > 50 * 1024 * 1024:
                    sys.exit(6)
                transfer.write(chunk)
            transfer.close()
        return
    if operation not in ("upload", "replace"):
        raise ValueError()
    tmp = ".lms-" + secrets.token_hex(16)
    tempfd = os.open(tmp, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600, dir_fd=fd)
    try:
        with os.fdopen(tempfd, "wb") as dest:
            shutil.copyfileobj(transfer, dest, 65536)
        if operation == "replace":
            os.replace(tmp, name, src_dir_fd=fd, dst_dir_fd=fd)
        else:
            os.link(tmp, name, src_dir_fd=fd, dst_dir_fd=fd, follow_symlinks=False)
    finally:
        try:
            os.unlink(tmp, dir_fd=fd)
        except FileNotFoundError:
            pass


try:
    execute(*sys.argv[1:6])
except FileNotFoundError:
    sys.exit(3)
except FileExistsError:
    sys.exit(4)
except (ValueError, NotADirectoryError, PermissionError):
    sys.exit(5)
except OSError as error:
    sys.exit(5 if error.errno == errno.ELOOP else 7)
