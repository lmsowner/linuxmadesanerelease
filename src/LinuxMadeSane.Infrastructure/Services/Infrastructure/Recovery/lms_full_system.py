# Copyright (c) Linux Made Sane.
# Licensed under the Business Source License 1.1. See LICENSE for details.
"""ReaR orchestration only. Never formats disks or performs recovery on a live host."""
import argparse
import json
import os
import pathlib
import re
import shlex
import shutil
import subprocess
import tempfile


def run(arguments, timeout=30, environment=None):
    result = subprocess.run(arguments, capture_output=True, text=True, timeout=timeout, env=environment)
    if result.returncode:
        raise ValueError(" ".join(arguments[:2]) + " failed: " + result.stderr[-4000:])
    return result.stdout


def mounts():
    return json.loads(run(["findmnt", "--json", "--list", "--output", "TARGET,SOURCE,FSTYPE,OPTIONS"]))["filesystems"]


def inside(path, root):
    return path == root or path.startswith(root.rstrip("/") + "/")


def disk_ancestors(source):
    source = source.split("[", 1)[0]
    data = json.loads(run(["lsblk", "--json", "--inverse", "--paths", "--output", "NAME,TYPE", source]))
    disks = set()
    def visit(node):
        if node["type"] == "disk": disks.add(node["name"])
        for child in node.get("children", []): visit(child)
    for node in data["blockdevices"]: visit(node)
    return disks


def discover(repository):
    if os.uname().machine not in ("x86_64", "amd64"):
        raise ValueError("Full-system boot recovery currently supports x86-64 hosts. Configuration and selected-folder backups remain available on this host.")
    repository = str(pathlib.Path(repository).resolve(strict=True))
    entries = mounts()
    root = next(item for item in entries if item["target"] == "/")
    if not root["source"].startswith("/dev/") or root["fstype"] not in ("ext2", "ext3", "ext4", "xfs"):
        raise ValueError("Boot recovery currently requires a directly accessible ext or XFS Linux root disk. Containers, overlay roots and unsupported storage layouts cannot create a reliable recovery image.")
    if not list(pathlib.Path("/boot").glob("vmlinuz*")):
        raise ValueError("The installed Linux kernel is not available in /boot. This host cannot create boot recovery media; ask its hosting provider about image recovery.")
    destination = max((item for item in entries if inside(repository, item["target"])), key=lambda item: len(item["target"]))
    if destination["target"] == "/":
        raise ValueError("Full-system backup needs a separate mounted backup disk or network share, not a folder on the root filesystem.")
    if destination["fstype"] not in ("ext2", "ext3", "ext4", "xfs", "vfat", "cifs", "smb3", "nfs", "nfs4"):
        raise ValueError("Boot recovery currently requires backup storage on a separate Linux disk, SMB share or NFS share. This destination uses " + destination["fstype"] + "; selected-folder backups remain available.")
    backup_disks = disk_ancestors(destination["source"]) if destination["source"].startswith("/dev/") else set()
    if destination["source"].startswith("/dev/") and not backup_disks:
        raise ValueError("The backup mount does not resolve to a separate physical disk. Select a different disk or an SMB/NFS share.")
    root_disks = disk_ancestors(root["source"])
    for item in entries:
        if item["target"] in ("/boot", "/boot/efi") and item["source"].startswith("/dev/"):
            root_disks.update(disk_ancestors(item["source"]))
    if backup_disks & root_disks:
        raise ValueError("The backup destination is on the Linux system disk. Select a different disk or a network share so disk failure does not destroy the backup too.")
    protected_ids = set()
    for disk in sorted(backup_disks):
        devices = json.loads(run(["lsblk", "--json", "--paths", "--output", "NAME,UUID,PTUUID,PARTUUID,WWN", disk]))["blockdevices"]
        disk_ids = set()
        def identify(node):
            for field in ("uuid", "ptuuid", "partuuid", "wwn"):
                value = node.get(field)
                if value and re.fullmatch(r"[A-Za-z0-9:._-]+", value): disk_ids.add(value)
            # Protect the physical disk itself. A child-only identifier must
            # not be mistaken for a stable identity of its containing disk.
        for device in devices: identify(device)
        if not disk_ids:
            raise ValueError("The backup disk has no stable UUID or storage identifier. Boot recovery cannot safely protect it from disk recreation; use an identifiable backup disk or SMB/NFS share.")
        protected_ids.update(disk_ids)
    local = []
    excluded = ["/proc", "/sys", "/dev", "/run", "/tmp", "/var/tmp", repository, "/var/lib/rear", "/var/log/rear"]
    excluded_mounts = []
    for item in entries:
        target, source, kind = item["target"], item["source"], item["fstype"]
        if target != "/" and (inside(target, destination["target"]) or (source.startswith("/dev/") and backup_disks and disk_ancestors(source) & backup_disks) or (kind == "squashfs" and source.startswith("/dev/loop")) or kind in ("nfs", "nfs4", "cifs", "smb3", "fuse.sshfs") or not source.startswith("/dev/")):
            excluded.append(target)
            excluded_mounts.append(target)
            continue
        if source.startswith("/dev/"):
            if kind not in ("ext2", "ext3", "ext4", "xfs", "vfat"):
                raise ValueError("Local filesystem " + target + " uses " + kind + "; recovery support has not been verified. No full-system backup was started.")
            local.append(target)
    return {"architecture": "x86_64", "firmware": "UEFI" if pathlib.Path("/sys/firmware/efi").exists() else "BIOS", "destinationFilesystem": destination["fstype"], "sources": sorted(set(local)), "excludes": sorted(set(excluded)), "excludedMounts": sorted(set(excluded_mounts)), "backupDisks": sorted(backup_disks), "protectedBackupIds": sorted(protected_ids), "repository": repository,
            "warning": "Live backup: running databases and Docker workloads need an application-consistent backup or must be stopped. A restore drill has not been performed."}


def build(bundle, plan):
    owner = bundle.stat()
    workspace = pathlib.Path(tempfile.mkdtemp(prefix="lms-", dir="/var/lib/rear"))
    workspace.chmod(0o700)
    try:
        config = workspace / "config"
        state = workspace / "state"
        work = workspace / "build"
        config.mkdir(mode=0o700); state.mkdir(mode=0o700); work.mkdir(mode=0o700)
        media = bundle / "boot-recovery"
        media.mkdir(mode=0o700)
        # No password is persisted in ReaR configuration or copied into the ISO.
        restore = workspace / "restore-system.sh"
        restore.write_text('#!/bin/bash\n' + 'bundle=' + shlex.quote(str(bundle)) + '\nstate=' + shlex.quote('/run/' + workspace.name + '-restore') + '\n' + '''
set -euo pipefail
set +x
unset RESTIC_PASSWORD RESTIC_PASSWORD_FILE RESTIC_PASSWORD_COMMAND
if [[ "${1:-}" == prepare ]]; then
    [[ -f /etc/rear-release ]] || { echo 'Boot the recovery image first.' >&2; exit 1; }
    [[ "$(findmnt -rn -T /run -o FSTYPE)" == tmpfs ]] || { echo 'Private recovery authentication requires the RAM-backed /run filesystem.' >&2; exit 1; }
    umask 077
    [[ ! -L "$state" ]] || exit 1
    mkdir -p -- "$state"; chmod 700 "$state"
    trap 'rm -rf -- "$state"' EXIT
    printf '%s\\n' 'Before any disk changes: connect or mount your backup storage.' > /dev/tty
    printf '%s' 'Repository path: ' > /dev/tty
    read -r repository < /dev/tty
    [[ "$repository" == /* && -d "$repository" && -f "$repository/config" ]] || { echo 'Enter the full repository path; mount the backup storage first.' > /dev/tty; exit 1; }
    printf '%s' 'Backup password (Enter if not password protected): ' > /dev/tty
    IFS= read -r -s password < /dev/tty
    printf '\\n' > /dev/tty
    printf '%s' "$password" > "$state/password"
    unset password
    export RESTIC_PASSWORD_FILE="$state/password"
    password_options=()
    if [[ ! -s "$state/password" ]] && restic help | grep -q -- '--insecure-no-password'; then password_options=(--insecure-no-password); fi
    snapshot=$(restic "${password_options[@]}" --repo "$repository" snapshots --path "$bundle" --tag lms-full-system,lms-backup-complete --json 2> /dev/tty | jq -er 'if length == 1 then .[0].id else error("No unique completed backup matches this recovery image. Use its matching ISO and repository.") end' 2> /dev/tty)
    [[ "$snapshot" =~ ^[a-f0-9]{64}$ ]] || exit 1
    printf '%s\\n' 'Checking encrypted backup data before any disk changes; this may take time.' > /dev/tty
    restic "${password_options[@]}" --repo "$repository" check --read-data < /dev/tty > /dev/tty 2>&1
    restic "${password_options[@]}" --repo "$repository" dump "$snapshot" "$bundle/FULL-SYSTEM-RECOVERY.txt" > /dev/null 2> /dev/tty
    printf '%s' "$repository" > "$state/repository"
    printf '%s' "$snapshot" > "$state/snapshot"
    printf 'Verified matching completed snapshot: %s\\nReview the target disk mapping before approving recreation.\\n' "$snapshot" > /dev/tty
    unset RESTIC_PASSWORD_FILE
    trap - EXIT
    exit 0
fi
trap 'rm -rf -- "$state"' EXIT
target="${1:-/mnt/local}"
[[ "$target" != / && -d "$target" && "$(findmnt -rn -T "$target" -o TARGET)" == "$target" ]] || { echo "Refusing restore outside the mounted recovery target." >&2; exit 1; }
[[ -f "$state/password" && -f "$state/repository" && -f "$state/snapshot" ]] || { echo 'Run recovery authentication before disk recreation.' > /dev/tty; exit 1; }
repository=$(cat "$state/repository"); snapshot=$(cat "$state/snapshot")
export RESTIC_PASSWORD_FILE="$state/password"
password_options=()
if [[ ! -s "$state/password" ]] && restic help | grep -q -- '--insecure-no-password'; then password_options=(--insecure-no-password); fi
restic "${password_options[@]}" --repo "$repository" restore "$snapshot" --target "$target" < /dev/tty > /dev/tty 2>&1
unset RESTIC_PASSWORD_FILE
''')
        specification = json.loads((bundle / "recovery-specification.json").read_text())
        data_root = specification["sourceDataDirectory"]
        keys_root = specification["sourceKeysDirectory"]
        filename = specification["databaseFileName"]
        if not data_root.startswith("/") or not keys_root.startswith("/") or keys_root in ("/", "/etc", "/usr", "/var", "/opt", "/home", "/root", "/boot", "/dev", "/proc", "/sys", "/run") or ".." in pathlib.Path(keys_root).parts or pathlib.Path(filename).name != filename:
            raise ValueError("Invalid source paths for matched LMS database recovery")
        # The OS backup excludes the live SQLite file. Activate the consistent
        # bundled database and matched keys before ReaR finishes and the host boots.
        restore.write_text(restore.read_text() + "\n" +
            'target="${1:-/mnt/local}"\n' +
            '[[ "$target" != / && -d "$target" ]] || exit 1\n' +
            "bundle=" + shlex.quote(str(bundle)) + "\n" +
            "database=" + shlex.quote(data_root.rstrip("/") + "/" + filename) + "\n" +
            "keys=" + shlex.quote(keys_root) + "\n" +
            # Resolve restored absolute symlinks inside the restored system,
            # never against the rescue host. Preserve unrelated key-folder files.
            'chroot "$target" /bin/bash -euc \'\n' +
            'bundle="$1"; database="$2"; keys="$3"\n' +
            '[[ -f "$bundle/linuxmadesane.db" && -d "$bundle/protection-keys" ]] || { echo "Matched LMS recovery bundle is missing; do not reboot." >&2; exit 1; }\n' +
            'mkdir -p -- "$(dirname "$database")" "$keys"\n' +
            'cp -a -- "$bundle/linuxmadesane.db" "$database"\n' +
            'rm -f -- "$database-wal" "$database-shm"\n' +
            'find -H "$keys" -maxdepth 1 -type f -name "key-*.xml" -delete\n' +
            'cp -a -- "$bundle/protection-keys/." "$keys/"\n' +
            '\' lms-recovery "$bundle" "$database" "$keys"\n')
        restore.chmod(0o700)
        quoted = shlex.quote
        # ReaR's generated rescue.conf restores VAR_DIR to /var/lib/rear.
        # Its documented pre-recovery hook activates the isolated disk inventory
        # in rescue RAM only, leaving an existing host's ReaR state untouched.
        prepare_recovery = 'test -f /etc/rear-release || { echo "Recovery is only allowed from the boot recovery image." >&2; exit 1; }; cp -a -- ' + quoted(str(state) + '/.') + ' "$VAR_DIR/" || exit 1; /bin/bash ' + quoted(str(restore)) + ' prepare || exit 1'
        prepare_recovery += '; AddExitTask ' + quoted('rm -rf -- ' + quoted('/run/' + workspace.name + '-restore'))
        lines = ["VAR_DIR=" + quoted(str(state)), "DISKLAYOUT_FILE=" + quoted(str(state / "layout/disklayout.conf")), "OUTPUT=ISO", "BACKUP=EXTERNAL", "OUTPUT_URL=", "BACKUP_URL=", "ISO_DIR=" + quoted(str(media)), "SSH_FILES=no", "SSH_UNPROTECTED_PRIVATE_KEYS=no", "USE_STATIC_NETWORKING=no", "USE_DHCLIENT=yes", "REQUIRED_PROGS+=( restic findmnt )", "COPY_AS_IS=( \"$SHARE_DIR\" \"$VAR_DIR\" " + quoted(str(restore)) + " )", "EXTERNAL_RESTORE=" + quoted("/bin/bash " + quoted(str(restore)) + ' "$TARGET_FS_ROOT"'), "AUTOEXCLUDE_PATH=()", "EXCLUDE_MOUNTPOINTS+=( " + " ".join(quoted(x) for x in plan["excludedMounts"]) + " )", "EXCLUDE_COMPONENTS+=( " + " ".join(quoted(x) for x in plan["backupDisks"]) + " )", "COPY_AS_IS_EXCLUDE+=( 'etc/linuxmadesane/*' 'var/lib/linuxmadesane/*' 'root/.ssh/*' 'home/*/.ssh/*' 'etc/shadow' 'etc/gshadow' 'etc/ssl/private/*' 'etc/NetworkManager/system-connections/*' )"]
        lines.append("PRE_RECOVERY_SCRIPT=" + quoted(prepare_recovery))
        lines.append("REQUIRED_PROGS+=( jq chroot )")
        # ReaR persists these stable IDs in rescue.conf and removes the matching
        # disks from target candidates, including when /dev names have changed.
        lines.append("WRITE_PROTECTED_IDS+=( " + " ".join(quoted(value) for value in plan.get("protectedBackupIds", [])) + " )")
        helper = {"cifs": "mount.cifs", "smb3": "mount.cifs", "nfs": "mount.nfs", "nfs4": "mount.nfs"}.get(plan.get("destinationFilesystem"))
        if helper:
            lines.append("REQUIRED_PROGS+=( " + helper + " )")
        (config / "local.conf").write_text("\n".join(lines) + "\n")
        (config / "local.conf").chmod(0o600)
        run(["rear", "-c", str(config), "-v", "mkrescue"], 1800, {**os.environ, "TMPDIR": str(work)})
        isos = list(media.glob("*.iso"))
        if len(isos) != 1 or isos[0].stat().st_size < 1024 * 1024:
            raise ValueError("ReaR did not produce one complete boot recovery ISO. No full-system backup was saved.")
        boot_report = run(["xorriso", "-indev", "stdio:" + str(isos[0]), "-report_el_torito", "plain"], 60)
        if "El Torito boot img" not in boot_report:
            raise ValueError("The recovery ISO has no validated boot image. No full-system backup was saved.")
        plan["recoveryMedia"] = {"file": isos[0].name, "bootCatalogueValidated": True, "restoreTested": False}
        for path in [media, *media.rglob("*")]:
            os.chown(path, owner.st_uid, owner.st_gid)
            path.chmod(0o700 if path.is_dir() else 0o600)
        (bundle / "full-system-recovery.json").write_text(json.dumps(plan, indent=2))
        (bundle / "FULL-SYSTEM-RECOVERY.txt").write_text('''FULL SYSTEM RECOVERY — ReaR + encrypted restic
If password protected, keep the repository password and access to its storage independently of this host.
For NAS storage, also keep its server/share and NAS credentials independently.
Saved NAS passwords are not embedded in the ISO; mount the share at the rescue
console before entering the repository path. The matching mount helper is included.
Before failure: restore boot-recovery/*.iso from this encrypted snapshot to a private
folder. Keep recovery media offline; exported files are decrypted and contain host
recovery information. Boot the ISO in an ISOLATED recovery test before relying on it.

After disk/server loss: use the same CPU architecture and BIOS/UEFI boot mode shown
in full-system-recovery.json. Boot the matching ISO from your provider console or recovery
media. Connect the backup storage without using a disk intended for restoration.
Run rear recover. Carefully review its disk mapping before confirming: recovery
WILL repartition the selected target disks. Different hardware needs explicit review.
Before disk changes, enter the repository path and encryption password privately
on the console. The ISO finds its matching completed snapshot automatically and
checks the encrypted backup data. The password is held only in private rescue RAM
and removed after restore; it is never embedded in the ISO or sent to AI.
ReaR rebuilds the disk layout and bootloader; restic restores Linux and local data.
The backup destination, network share contents and virtual/runtime filesystems were
excluded; see full-system-recovery.json for the exact source and exclusion inventory.

This is a live file backup. Independently back up databases or stop applications to
ensure consistent data. Use the included matched LMS database/protection-key bundle
for LMS configuration recovery before starting LMS. Review hardware, IPs, mounts,
SSH identities and public routes before reconnecting recovered services.

A successful backup or ISO validation is NOT proof of a successful boot restore.
Test on disposable disks/VMs; verify Linux boot, LMS login, credentials and workloads,
and retain the dated results and snapshot ID in the backup plan's recovery notes.
''')
    finally:
        shutil.rmtree(workspace, ignore_errors=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository", required=True)
    parser.add_argument("--bundle")
    args = parser.parse_args()
    try:
        plan = discover(args.repository)
        if args.bundle:
            build(pathlib.Path(args.bundle), plan)
        print(json.dumps(plan))
    except Exception as error:
        print(str(error), file=__import__("sys").stderr)
        raise SystemExit(1)
