# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE for details.
import base64
import fcntl
import hashlib
import json
import os
import stat
import sys

LIMIT = 512 * 1024
FLAGS = os.O_CLOEXEC | os.O_NONBLOCK | os.O_NOFOLLOW
EXTENSIONS = {".conf", ".config", ".cfg", ".ini", ".json", ".toml", ".xml",
              ".yaml", ".yml", ".properties", ".env", ".cnf", ".hcl"}


def open_path(path, writable):
    fd = os.open("/", FLAGS | os.O_DIRECTORY)
    try:
        parts = path.split("/")[1:]
        for index, part in enumerate(parts):
            if part in ("", ".", ".."):
                raise ValueError("Invalid configuration path.")
            mode = os.O_DIRECTORY if index < len(parts) - 1 else (os.O_RDWR if writable else os.O_RDONLY)
            child = os.open(part, FLAGS | mode, dir_fd=fd)
            os.close(fd)
            fd = child
        return fd
    except BaseException:
        os.close(fd)
        raise


def list_files(fd, prefix, depth, result):
    with os.scandir(fd) as entries:
        for entry in entries:
            name = entry.name
            if entry.is_symlink():
                continue
            path = prefix + name
            if entry.is_file(follow_symlinks=False):
                if name in ("Dockerfile", "Caddyfile", ".env") or os.path.splitext(name)[1].lower() in EXTENSIONS:
                    result.append(path)
                    if len(result) > 1000:
                        raise ValueError("This mount contains too many configuration files to list.")
            elif depth and entry.is_dir(follow_symlinks=False):
                child = os.open(name, FLAGS | os.O_DIRECTORY, dir_fd=fd)
                try:
                    list_files(child, path + "/", depth - 1, result)
                finally:
                    os.close(child)


def main():
    request = json.load(sys.stdin)
    root, relative, operation = request["root"], request["relative"], request["operation"]
    if not os.path.isabs(root) or root == "/" or (relative and any(p in ("", ".", "..") for p in relative.split("/"))):
        raise ValueError("Invalid mount path.")
    fd = open_path(os.path.join(root, relative) if relative else root, operation == "write")
    try:
        mode = os.fstat(fd).st_mode
        if operation == "list":
            result = []
            if stat.S_ISDIR(mode):
                list_files(fd, "", 6, result)
            elif stat.S_ISREG(mode):
                result.append("")
            print(json.dumps(sorted(result)))
            return
        if not stat.S_ISREG(mode):
            raise ValueError("Select a regular configuration file.")
        with os.fdopen(fd, "r+b" if operation == "write" else "rb", closefd=False) as stream:
            fcntl.flock(fd, fcntl.LOCK_EX if operation == "write" else fcntl.LOCK_SH)
            original = stream.read(LIMIT + 1)
            if len(original) > LIMIT:
                raise ValueError("This configuration file exceeds the 512 KiB editor limit.")
            if operation == "read":
                print(base64.b64encode(original).decode())
                return
            if hashlib.sha256(original).hexdigest() != request["revision"]:
                raise ValueError("The file changed since it was opened. Reload it before saving.")
            content = base64.b64decode(request["content"], validate=True)
            if len(content) > LIMIT:
                raise ValueError("The edited file exceeds the 512 KiB editor limit.")
            def replace(data):
                stream.seek(0)
                stream.write(data)
                stream.truncate()
                stream.flush()
                os.fsync(fd)
            try:
                replace(content)
            except OSError:
                replace(original)
                raise
    finally:
        os.close(fd)


try:
    main()
except (ValueError, OSError) as error:
    print(str(error) if isinstance(error, ValueError) else error.strerror, file=sys.stderr)
    sys.exit(1)
