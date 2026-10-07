# Copyright (c) Linux Made Sane.
# Licensed under the Business Source License 1.1. See LICENSE for details.
"""Portable configuration recovery. Never starts services or applies old networking."""
import argparse
import hashlib
import json
import os
import pathlib
import re
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    checksum = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            checksum.update(block)
    return checksum.hexdigest()


def safe_path(root, relative):
    path = pathlib.Path(relative)
    require(not path.is_absolute() and path.parts and all(p not in (".", "..") for p in path.parts), "Unsafe recovery path")
    result = root / path
    require(not any(p.is_symlink() for p in [result, *result.parents]), "Recovery path contains a symbolic link")
    return result


def collect(bundle, specification):
    manifest = specification
    owner = bundle.stat()
    manifest["formatVersion"] = 1
    manifest["createdUtc"] = __import__("datetime").datetime.now(__import__("datetime").timezone.utc).isoformat()
    for location in manifest["locations"]:
        source = pathlib.Path(location["source"])
        location["present"] = source.exists()
        location["isDirectory"] = source.is_dir()
        if not source.exists():
            continue
        destination = bundle / "persistent-settings" / location["id"]
        if source.is_dir():
            # Configured roots may be symlinks; children must not silently escape them.
            for base, directories, files in os.walk(source.resolve()):
                for name in directories + files:
                    require(not (pathlib.Path(base) / name).is_symlink(), "Persistent settings contain a symbolic link; review the configured storage path before backing up")
            shutil.copytree(source, destination)
        else:
            destination.mkdir(parents=True)
            shutil.copy2(source, destination / source.name)
    inventories = {}
    commands = {
        "packages": ["dpkg-query", "-W", "-f=${binary:Package}\t${Version}\n"],
        "services": ["systemctl", "list-unit-files", "--no-pager", "--no-legend"],
        "interfaces": ["ip", "-j", "address", "show"],
        "routes": ["ip", "-j", "route", "show", "table", "all"],
        "disks": ["lsblk", "--json", "--output", "NAME,TYPE,FSTYPE,UUID,MOUNTPOINTS"],
    }
    for name, command in commands.items():
        try:
            result = subprocess.run(command, capture_output=True, text=True, timeout=30, check=False)
            inventories[name] = {"available": result.returncode == 0, "output": result.stdout, "error": result.stderr}
        except (OSError, subprocess.TimeoutExpired) as error:
            inventories[name] = {"available": False, "error": type(error).__name__}
    (bundle / "host-inventory.json").write_text(json.dumps(inventories, indent=2))
    etc = pathlib.Path(manifest["hostConfigurationSource"])
    require(etc.is_dir(), "Host configuration directory is missing")
    manifest["userSshConfiguration"] = []
    if (etc / "passwd").is_file():
        for line in (etc / "passwd").read_text().splitlines():
            account = line.split(":")
            if len(account) != 7:
                continue
            username, _, uid, gid, _, home, _ = account
            source = pathlib.Path(home) / ".ssh"
            if not source.is_dir():
                continue
            require(pathlib.Path(username).name == username and username not in (".", ".."), "Unsafe account name")
            destination = safe_path(bundle, "user-ssh/" + username)
            def ignore_runtime(directory, names):
                ignored = []
                for name in names:
                    path = pathlib.Path(directory) / name
                    require(not path.is_symlink(), "User SSH configuration contains a symbolic link; review it before backing up")
                    if not path.is_file() and not path.is_dir(): ignored.append(name)
                return ignored
            shutil.copytree(source, destination, ignore=ignore_runtime)
            manifest["userSshConfiguration"].append({"username": username, "uid": int(uid), "gid": int(gid), "sourceHome": home, "bundlePath": str(destination.relative_to(bundle))})
    archive = bundle / "host-configuration.tar"
    result = subprocess.run(["tar", "--acls", "--xattrs", "--numeric-owner", "--one-file-system", "-cpf", str(archive), "-C", str(etc.parent), "--", etc.name], capture_output=True, text=True, timeout=300, check=False)
    require(result.returncode == 0, "Could not capture all host configuration; the backup is not complete: " + result.stderr)
    manifest["files"] = []
    for path in sorted(bundle.rglob("*")):
        require(not path.is_symlink(), "Recovery bundle must not contain symbolic links")
        if path.is_file() and path.name != "recovery-manifest.json":
            os.chown(path, owner.st_uid, owner.st_gid)
            path.chmod(0o600)
            manifest["files"].append({"path": str(path.relative_to(bundle)), "sha256": digest(path), "bytes": path.stat().st_size})
        elif path.is_dir():
            os.chown(path, owner.st_uid, owner.st_gid)
            path.chmod(0o700)
    (bundle / "recovery-manifest.json").write_text(json.dumps(manifest, indent=2))
    (bundle / "recovery-manifest.json").chmod(0o600)
    os.chown(bundle / "recovery-manifest.json", owner.st_uid, owner.st_gid)


def validate(bundle):
    require(not bundle.is_symlink(), "Recovery bundle must not be a symbolic link")
    require(not any(p.is_symlink() for p in bundle.rglob("*")), "Recovery bundle contains symbolic links")
    file = bundle / "recovery-manifest.json"
    require(file.is_file() and not file.is_symlink(), "This snapshot has no complete recovery manifest. Older LMS backups only support limited manual recovery.")
    manifest = json.loads(file.read_text())
    require(manifest.get("formatVersion") == 1, "Unsupported recovery bundle version")
    listed = set()
    for entry in manifest["files"]:
        path = safe_path(bundle, entry["path"])
        require(path.is_file() and path.stat().st_size == entry["bytes"] and digest(path) == entry["sha256"], "Missing or damaged recovery file: " + entry["path"])
        require(entry["path"] not in listed, "Duplicate recovery manifest entry")
        listed.add(entry["path"])
    actual = {str(p.relative_to(bundle)) for p in bundle.rglob("*") if p.is_file() and p.name != "recovery-manifest.json"}
    require(actual == listed, "Recovery bundle contains unverified files")
    require("linuxmadesane.db" in listed and "host-configuration.tar" in listed and "host-inventory.json" in listed, "Incomplete configuration recovery bundle")
    keys = list((bundle / "protection-keys").glob("key-*.xml"))
    require(keys, "LMS credential protection keys are missing")
    for key in keys:
        require(ET.parse(key).getroot().tag == "key", "Invalid LMS protection key")
    connection = sqlite3.connect((bundle / "linuxmadesane.db").absolute().as_uri() + "?mode=ro&immutable=1", uri=True)
    try:
        require(connection.execute("pragma integrity_check").fetchone()[0] == "ok", "LMS database integrity check failed")
        require(connection.execute("select count(*) from sqlite_master where type='table' and name='protected_secrets'").fetchone()[0] == 1, "Not an LMS database")
    finally:
        connection.close()
    return manifest


def prepare(bundle, manifest, destination, data_root, app_root, keys_root, mappings, database_file_name=None):
    # This produces a reviewable restore tree. It cannot overwrite a running host.
    require(not destination.exists(), "Choose a new, empty recovery preparation directory")
    for value in (data_root, app_root, keys_root):
        require(value.is_absolute() and value != pathlib.Path("/") and ".." not in value.parts, "Choose dedicated absolute target directories, not the filesystem root")
    require(manifest.get("edition") == "ce", "This recovery workflow currently supports Community Edition only")
    database_file_name = database_file_name or manifest["databaseFileName"]
    require(pathlib.Path(database_file_name).name == database_file_name and database_file_name not in (".", ".."), "Invalid recovery database file name")
    with tempfile.TemporaryDirectory(prefix="lms-recovery-", dir=destination.parent) as temporary:
        work = pathlib.Path(temporary)
        tree = work / "root"
        operations = []
        def add(source, target, category):
            require(target.is_absolute() and ".." not in target.parts and target != pathlib.Path("/"), "Unsafe restore target")
            require(not any(operation["target"] == str(target) for operation in operations), "Conflicting restore targets")
            staged = safe_path(tree, str(target).lstrip("/"))
            staged.parent.mkdir(parents=True, exist_ok=True)
            if source.is_dir():
                shutil.copytree(source, staged)
            else:
                shutil.copy2(source, staged)
            operations.append({"source": str(staged.relative_to(work)), "target": str(target), "category": category})
        add(bundle / "linuxmadesane.db", data_root / database_file_name, "LMS database")
        add(bundle / "protection-keys", keys_root, "Credential protection keys")
        for name in ("appsettings.json", "appsettings.Production.json"):
            if (bundle / name).is_file():
                add(bundle / name, app_root / name, "Application settings — review old paths and listen addresses")
        for location in manifest["locations"]:
            if not location["present"]:
                continue
            require(pathlib.Path(location["id"]).name == location["id"] and location["id"] not in (".", ".."), "Invalid persistent store identifier")
            relative = pathlib.Path(location["relative"])
            require(not relative.is_absolute() and ".." not in relative.parts, "Invalid persistent store relative path")
            if location["id"] in mappings:
                target = pathlib.Path(mappings[location["id"]])
            elif location["base"] == "data":
                target = data_root / location["relative"]
            elif location["base"] == "application":
                target = app_root / location["relative"]
            else:
                raise ValueError("Map custom storage location explicitly: --map " + location["id"] + "=/new/path")
            source = bundle / "persistent-settings" / location["id"]
            if not location["isDirectory"]:
                source = source / pathlib.Path(location["source"]).name
            add(source, target, location["id"])
        shutil.copy2(bundle / "host-configuration.tar", work / "host-configuration.tar")
        shutil.copy2(bundle / "host-inventory.json", work / "host-inventory.json")
        if (bundle / "user-ssh").is_dir():
            shutil.copytree(bundle / "user-ssh", work / "user-ssh-for-review")
        plan = {"formatVersion": 1, "sourceHost": manifest["hostname"], "sourceVersion": manifest["version"], "targetApplicationDirectory": str(app_root), "operations": operations,
                "requiresReview": ["Network interfaces, IPs and DNS", "Disk UUIDs and mount paths", "Linux users, groups and service ownership", "SSH host identity and authorized keys", "Caddy upstreams and public routes", "Scheduled tasks and automatic service startup", "Application settings, environment overrides and relocated paths"],
                "instructions": "Install LMS CE at the same or a newer version. Keep LMS and affected services stopped. Review this tree and host-configuration.tar before applying files. Regenerate systemd, schedules and routes using existing LMS management after mapping this host's interfaces and disks. Do not replace /etc wholesale. No live files or services have been changed."}
        plan["files"] = [{"path": str(p.relative_to(work)), "sha256": digest(p)} for p in sorted(tree.rglob("*")) if p.is_file()]
        (work / "restore-plan.json").write_text(json.dumps(plan, indent=2))
        for path in work.rglob("*"):
            if path.is_file(): path.chmod(0o600)
            elif path.is_dir(): path.chmod(0o700)
        shutil.move(str(work), destination)
    destination.chmod(0o700)


def apply_prepared(prepared, target_root, service_user, confirmed):
    require(confirmed, "Review the prepared files first, then supply --confirm-replace")
    require(os.geteuid() == 0, "Configuration activation requires an administrator")
    require(target_root.is_absolute() and target_root.is_dir() and not target_root.is_symlink(), "Choose an existing mounted target root")
    plan = json.loads((prepared / "restore-plan.json").read_text())
    require(plan.get("formatVersion") == 1, "Unsupported restore plan")
    application = safe_path(target_root, plan["targetApplicationDirectory"].lstrip("/"))
    require((application / "edition.txt").read_text().strip() == "ce", "Install LMS Community Edition on the replacement before recovery")
    def release_version(value):
        match = re.match(r"^v(\d{4})\.(\d{2})\.(\d{2})\.(\d{2})\.(\d{2})(?:-|$)", value)
        require(match is not None, "LMS release version could not be verified")
        return tuple(map(int, match.groups()))
    require(release_version((application / "version.txt").read_text().strip()) >= release_version(plan["sourceVersion"]), "Install the same or a newer LMS version; configuration cannot be restored into an older release")
    require(not any(p.is_symlink() for p in prepared.rglob("*")), "Prepared recovery contains symbolic links")
    expected = {entry["path"] for entry in plan["files"]}
    actual = {str(p.relative_to(prepared)) for p in (prepared / "root").rglob("*") if p.is_file()}
    require(expected == actual, "Prepared recovery contains missing or unverified files")
    for entry in plan["files"]:
        require(digest(safe_path(prepared, entry["path"])) == entry["sha256"], "Prepared configuration has changed. Prepare a fresh reviewed plan before applying it.")
    # Never activate over a running installation. Offline disk recovery has no running LMS.
    if target_root == pathlib.Path("/"):
        result = subprocess.run(["systemctl", "is-active", "linux-made-sane.service"], capture_output=True, text=True, check=False)
        require(result.returncode in (3, 4), "Stop LMS and confirm its service is inactive before configuration recovery")
    passwd = (target_root / "etc/passwd").read_text().splitlines()
    users = [line.split(":") for line in passwd if line.split(":")[0] == service_user]
    require(len(users) == 1, "Install LMS on the target first; its service account must exist")
    uid, gid = int(users[0][2]), int(users[0][3])
    operations = []
    for operation in plan["operations"]:
        source = safe_path(prepared, operation["source"])
        require(source.is_file() or source.is_dir(), "Prepared source is missing")
        target = safe_path(target_root, operation["target"].lstrip("/"))
        require(not any(p.is_symlink() for p in target.rglob("*")) if target.is_dir() else True, "Existing target contains symbolic links")
        operations.append((source, target))
    for _, target in operations:
        require(not any(target != other and (target in other.parents or other in target.parents) for _, other in operations), "Overlapping restore targets")
    rollback = pathlib.Path(tempfile.mkdtemp(prefix="lms-configuration-rollback-", dir=target_root / "var/tmp"))
    rollback.chmod(0o700)
    completed = []
    try:
        for index, (source, target) in enumerate(operations):
            target.parent.mkdir(parents=True, exist_ok=True)
            original = rollback / str(index)
            existed = target.exists()
            if existed:
                shutil.move(str(target), original)
            completed.append((target, original, existed))
            if source.is_dir():
                shutil.copytree(source, target)
            else:
                shutil.copy2(source, target)
            for item in [target, *target.rglob("*")] if target.is_dir() else [target]:
                os.chown(item, uid, gid)
                item.chmod(0o700 if item.is_dir() else 0o600)
            if source.name == "linuxmadesane.db":
                for suffix in ("-wal", "-shm"):
                    sidecar = pathlib.Path(str(target) + suffix)
                    if sidecar.exists():
                        saved = rollback / (str(index) + suffix)
                        shutil.move(str(sidecar), saved)
                        completed.append((sidecar, saved, True))
        (rollback / "rollback.json").write_text(json.dumps([{"target": str(t), "original": str(o), "existed": existed} for t, o, existed in completed], indent=2))
        (rollback / "rollback.json").chmod(0o600)
    except Exception:
        for target, original, existed in reversed(completed):
            if target.is_dir(): shutil.rmtree(target)
            elif target.exists(): target.unlink()
            if existed: shutil.move(str(original), target)
        raise
    print("LMS configuration activated with replacement service-account ownership. Rollback files: " + str(rollback))
    print("Host configuration, networking and services were NOT activated. Review the archive and recovery instructions before starting LMS.")


def main():
    parser = argparse.ArgumentParser(description="Validate and prepare LMS configuration recovery without changing live services.")
    parser.add_argument("--bundle", type=pathlib.Path)
    parser.add_argument("--collect", type=pathlib.Path)
    parser.add_argument("--prepare", type=pathlib.Path)
    parser.add_argument("--data-root", type=pathlib.Path)
    parser.add_argument("--app-root", type=pathlib.Path)
    parser.add_argument("--keys-root", type=pathlib.Path)
    parser.add_argument("--map", action="append", default=[])
    parser.add_argument("--database-file-name")
    parser.add_argument("--apply-prepared", type=pathlib.Path)
    parser.add_argument("--target-root", type=pathlib.Path, default=pathlib.Path("/"))
    parser.add_argument("--service-user", default="linuxmadesane")
    parser.add_argument("--confirm-replace", action="store_true")
    args = parser.parse_args()
    if args.apply_prepared:
        apply_prepared(args.apply_prepared, args.target_root, args.service_user, args.confirm_replace)
        return
    require(args.bundle is not None, "Specify --bundle")
    if args.collect:
        collect(args.bundle, json.loads(args.collect.read_text()))
    manifest = validate(args.bundle)
    print("Database integrity checked; protection keys and configuration file checksums validated.")
    print("Source host: " + manifest["hostname"] + " · CE version: " + manifest["version"])
    if args.prepare:
        require(all((args.data_root, args.app_root, args.keys_root)), "Specify the replacement host's data, application and key directories")
        mappings = dict(value.split("=", 1) for value in args.map)
        prepare(args.bundle, manifest, args.prepare, args.data_root, args.app_root, args.keys_root, mappings, args.database_file_name)
        print("Recovery tree prepared. Review restore-plan.json. No live files or services were changed.")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print("Configuration recovery stopped: " + str(error), file=sys.stderr)
        sys.exit(1)
