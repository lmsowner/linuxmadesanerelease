# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE for details.
"""Hash on the owning host. Only same-directory, no-replace renames; never unlink files."""
import collections, ctypes, fcntl, hashlib, json, os, re, signal, stat, sys

DESTINATION = "LMS Duplicates"
MAX_FILES = 20000
MAX_COPIES = 2000
warnings = []

def warn(message):
    if len(warnings) < 100:
        warnings.append(message)

def identity(value):
    return ":".join(str(n) for n in (value.st_dev, value.st_ino, value.st_size, value.st_mtime_ns, value.st_ctime_ns))

def directory_identity(value):
    return str(value.st_dev) + ":" + str(value.st_ino)

def valid_name(name):
    return isinstance(name, str) and name not in ("", ".", "..", DESTINATION) and "/" not in name and "\0" not in name

def mounts():
    result = []
    if sys.platform.startswith("linux"):
        # Kernel mount table, not a shell command; escaped whitespace must be decoded.
        with open("/proc/self/mountinfo", encoding="utf-8") as stream:
            for line in stream:
                fields = line.split()
                separator = fields.index("-")
                path = re.sub(r"\\([0-7]{3})", lambda m: chr(int(m[1], 8)), fields[4])
                result.append((path, fields[separator + 1]))
    return result

def ensure_host_local(path, table):
    owners = [(mount, kind) for mount, kind in table if path == mount or path.startswith(mount.rstrip("/") + "/")]
    if owners:
        _, kind = max(owners, key=lambda value: len(value[0]))
        if kind.startswith(("nfs", "cifs", "smb", "fuse", "9p", "ceph", "gluster", "afs", "coda", "lustre")):
            raise ValueError("This folder is a network mount. Open its owning server using the SSH file browser to de-duplicate there, without transferring file contents to LMS.")

def fingerprint(rootfd, name, expected=None):
    if not valid_name(name):
        raise ValueError("Invalid file name in duplicate plan.")
    fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=rootfd)
    try:
        before = os.fstat(fd)
        if not stat.S_ISREG(before.st_mode) or before.st_dev != os.fstat(rootfd).st_dev:
            raise ValueError("Not a regular file on this filesystem.")
        if expected is not None and identity(before) != expected:
            raise ValueError("File changed since the scan.")
        digest = hashlib.sha256()
        while True:
            block = os.read(fd, 1024 * 1024)
            if not block:
                break
            digest.update(block)
        after = os.fstat(fd)
        current = os.stat(name, dir_fd=rootfd, follow_symlinks=False)
        if identity(before) != identity(after) or identity(before) != identity(current):
            raise ValueError("File changed while hashing.")
        return digest.hexdigest(), {"Name": name, "SizeBytes": before.st_size, "Identity": identity(before)}
    finally:
        os.close(fd)

def rename_without_replace(sourcefd, source, destinationfd, destination):
    libc = ctypes.CDLL(None, use_errno=True)
    if sys.platform.startswith("linux") and hasattr(libc, "renameat2"):
        rename = libc.renameat2
        flag = 1  # Linux RENAME_NOREPLACE
    elif sys.platform == "darwin" and hasattr(libc, "renameatx_np"):
        rename = libc.renameatx_np
        flag = 4  # RENAME_EXCL from the platform's sys/stdio.h
    else:
        raise ValueError("This host does not support safe, no-overwrite file moves.")
    rename.argtypes = (ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint)
    rename.restype = ctypes.c_int
    if rename(sourcefd, os.fsencode(source), destinationfd, os.fsencode(destination), flag) != 0:
        error = ctypes.get_errno()
        raise OSError(error, os.strerror(error), destination)

def keeper_order(file):
    stem, extension = os.path.splitext(file["Name"])
    numbered = re.search(r"\s*\(\d+\)$", stem)
    return (numbered is not None, file["Name"].casefold(), file["Name"])

def scan_folder(rootfd, root, table):
    sizes = collections.defaultdict(list)
    scanned = 0
    hashed = 0
    with os.scandir(rootfd) as entries:
        for entry in entries:
            if scanned >= MAX_FILES:
                raise ValueError("This folder contains more than 20,000 files. Work with a smaller folder.")
            if not valid_name(entry.name):
                continue
            try:
                metadata = entry.stat(follow_symlinks=False)
                if not stat.S_ISREG(metadata.st_mode) or metadata.st_dev != os.fstat(rootfd).st_dev:
                    continue
                if any(os.path.join(root, entry.name) == mount for mount, _ in table):
                    continue
                scanned += 1
                sizes[metadata.st_size].append((entry.name, identity(metadata)))
            except OSError as error:
                warn(entry.name + ": " + str(error))
    groups = []
    copies = 0
    for names in sizes.values():
        if len(names) < 2:
            continue
        digests = collections.defaultdict(list)
        for name, expected in names:
            try:
                digest, file = fingerprint(rootfd, name, expected)
                hashed += 1
                digests[digest].append(file)
            except (OSError, ValueError) as error:
                warn(name + ": " + str(error))
        for digest, files in digests.items():
            if len(files) < 2:
                continue
            files.sort(key=keeper_order)
            copies += len(files) - 1
            if copies > MAX_COPIES:
                raise ValueError("More than 2,000 duplicates found. Work with a smaller folder.")
            groups.append({"Sha256": digest, "Keep": files[0], "Duplicates": files[1:]})
    return scanned, hashed, groups

def move_copies(rootfd, root, table, plan, moved):
    if directory_identity(os.fstat(rootfd)) != plan.get("RootIdentity"):
        raise ValueError("The folder changed since the scan. Scan again.")
    groups = plan.get("Groups")
    if not isinstance(groups, list) or sum(len(g.get("Duplicates", [])) for g in groups) > MAX_COPIES:
        raise ValueError("Invalid or oversized duplicate plan.")
    if not groups:
        return
    destination = os.path.join(root, DESTINATION)
    if any(destination == mount for mount, _ in table):
        raise ValueError("LMS Duplicates is a mount point. Choose a different folder before proceeding.")
    try:
        os.mkdir(DESTINATION, 0o700, dir_fd=rootfd)
    except FileExistsError:
        pass
    destfd = os.open(DESTINATION, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=rootfd)
    try:
        if os.fstat(destfd).st_dev != os.fstat(rootfd).st_dev:
            raise ValueError("LMS Duplicates must be on the same filesystem.")
        for group in groups:
            keep = group["Keep"]
            for copy in group["Duplicates"]:
                try:
                    if copy["Name"] == keep["Name"]:
                        raise ValueError("The kept file cannot be moved.")
                    if any(os.path.join(root, copy["Name"]) == mount for mount, _ in table):
                        raise ValueError("Mount-point files are not moved.")
                    digest, _ = fingerprint(rootfd, keep["Name"], keep["Identity"])
                    other, _ = fingerprint(rootfd, copy["Name"], copy["Identity"])
                    if digest != group["Sha256"] or other != digest:
                        raise ValueError("Contents no longer match. Scan again.")
                    if identity(os.stat(keep["Name"], dir_fd=rootfd, follow_symlinks=False)) != keep["Identity"]:
                        raise ValueError("Kept file changed. Scan again.")
                    if identity(os.stat(copy["Name"], dir_fd=rootfd, follow_symlinks=False)) != copy["Identity"]:
                        raise ValueError("Duplicate changed. Scan again.")
                    # Pin the destination and verify it still denotes the visible child directory.
                    if directory_identity(os.stat(DESTINATION, dir_fd=rootfd, follow_symlinks=False)) != directory_identity(os.fstat(destfd)):
                        raise ValueError("The destination folder changed. Scan again.")
                    stem, extension = os.path.splitext(copy["Name"])
                    for suffix in range(10000):
                        name = copy["Name"] if suffix == 0 else stem + " [" + str(suffix) + "]" + extension
                        try:
                            rename_without_replace(rootfd, copy["Name"], destfd, name)
                            moved.append({"SourceName": copy["Name"], "DestinationName": name})
                            break
                        except FileExistsError:
                            continue
                    else:
                        raise ValueError("No unused destination name was available.")
                except (OSError, ValueError) as error:
                    warn(copy["Name"] + ": " + str(error))
        # Best effort durability; some network/appliance filesystems reject directory fsync.
        for fd in (destfd, rootfd):
            try:
                os.fsync(fd)
            except OSError:
                pass
    finally:
        os.close(destfd)

def main():
    signal.alarm(1800)
    try:
        os.nice(10)
    except OSError:
        pass
    request = json.loads(sys.stdin.buffer.read(4 * 1024 * 1024 + 1))
    root = request["RootPath"]
    if not isinstance(root, str) or not os.path.isabs(root) or "\0" in root:
        raise ValueError("Choose an absolute folder path.")
    root = os.path.normpath(root)
    table = mounts()
    ensure_host_local(os.path.realpath(root), table)
    rootfd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        try:
            fcntl.flock(rootfd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise ValueError("Another de-duplication is already working on this folder. Try again when it finishes.")
        plan = request.get("Scan")
        moved = []
        if plan is None:
            scanned, hashed, groups = scan_folder(rootfd, root, table)
        else:
            scanned, hashed, groups = plan["FilesScanned"], plan["FilesHashed"], plan["Groups"]
            move_copies(rootfd, root, table, plan, moved)
        print(json.dumps({"RootPath": root, "RootIdentity": directory_identity(os.fstat(rootfd)),
            "FilesScanned": scanned, "FilesHashed": hashed, "Groups": groups, "MovedFiles": moved, "Warnings": warnings}))
    finally:
        os.close(rootfd)

try:
    main()
except (OSError, ValueError, KeyError, TypeError) as error:
    sys.stderr.write(str(error) + "\n")
    sys.exit(1)
