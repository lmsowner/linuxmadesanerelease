#!/usr/bin/env python3
# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE for details.
"""Preserve legacy release-local LMS state without overwriting persistent files."""
import argparse
import datetime
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3

NAMES = ('share-mounts', 'rdp-optimizer', 'sftp-backups')


def migrate(previous, data, database, fstab):
    previous, data, database, fstab = map(Path, (previous, data, database, fstab))
    data.mkdir(parents=True, exist_ok=True)
    roots = [previous]
    resolved = previous.resolve()
    if resolved.parent.name == 'releases':
        roots.extend(sorted((p for p in resolved.parent.iterdir() if p.is_dir() and not p.is_symlink()),
                            key=lambda p: p.name, reverse=True))
    owner = database.stat() if database.exists() else data.stat()
    copied = 0

    def ensure_directory(path):
        if path.exists():
            if path.is_symlink():
                raise ValueError('Persistent storage must not contain symlinked migration targets')
            return
        ensure_directory(path.parent)
        path.mkdir(mode=0o700)
        if os.geteuid() == 0:
            os.chown(path, owner.st_uid, owner.st_gid)

    def copy_tree(source, destination):
        nonlocal copied
        if source.is_symlink() or not source.exists() or source.resolve() == destination.resolve():
            return
        if source.is_dir():
            ensure_directory(destination)
            for child in source.iterdir():
                copy_tree(child, destination / child.name)
        elif source.is_file() and not destination.exists():
            # Open with restrictive permissions from creation; never overwrite existing state.
            ensure_directory(destination.parent)
            with source.open('rb') as src, os.fdopen(os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), 'wb') as dst:
                if os.geteuid() == 0:
                    os.fchown(dst.fileno(), owner.st_uid, owner.st_gid)
                shutil.copyfileobj(src, dst)
            shutil.copystat(source, destination)
            os.chmod(destination, 0o600)
            copied += 1

    def rebase(value, category):
        if not value:
            return value
        path = Path(value)
        marker = '/data/' + category + '/'
        if marker not in str(path) or not path.is_absolute():
            return value
        prefix, tail = str(path).rsplit(marker, 1)
        relative = Path(tail)
        if '..' in relative.parts:
            raise ValueError('Unsafe legacy storage path')
        source_root = Path(prefix) / 'data' / category
        destination = data / category / relative
        copy_tree(source_root, data / category)
        return str(destination)

    for root in roots:
        for name in NAMES:
            old = root / 'data' / name
            copy_tree(old, data / name)

    backup = data / 'storage-migration-backups'
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    changed = 0
    managed = []
    if database.exists():
        with sqlite3.connect(database) as db:
            tables = {row[0] for row in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
            updates = []
            for table, column, marker, option in (
                    ('sshfs_mounts', 'IdentityFilePath', 'lms-sshfs-mount:', 'IdentityFile='),
                    ('remote_share_mounts', 'CredentialFilePath', 'lms-remote-mount:', 'credentials=')):
                if table not in tables:
                    continue
                for id_, old in db.execute(f'SELECT Id, {column} FROM {table}'):
                    new = rebase(old, 'share-mounts')
                    managed.append((marker + id_.replace('-', '').lower(), option, old, new))
                    if new != old:
                        updates.append((table, column, new, id_))
            if 'sftp_backup_snapshots' in tables:
                for id_, storage, files in db.execute('SELECT Id, StorageDirectory, FilesJson FROM sftp_backup_snapshots'):
                    new_storage = rebase(storage, 'sftp-backups')
                    entries = json.loads(files)
                    updated_files = False
                    for entry in entries:
                        for key in ('backupPath', 'BackupPath'):
                            if key in entry:
                                new = rebase(entry[key], 'sftp-backups')
                                updated_files |= new != entry[key]
                                entry[key] = new
                    if new_storage != storage:
                        updates.append(('sftp_backup_snapshots', 'StorageDirectory', new_storage, id_))
                    if updated_files:
                        updates.append(('sftp_backup_snapshots', 'FilesJson', json.dumps(entries), id_))
            if updates:
                backup.mkdir(mode=0o700, exist_ok=True)
                backup_path = backup / f'{stamp}.db'
                with sqlite3.connect(backup_path) as target:
                    db.backup(target)
                os.chmod(backup_path, 0o600)
                with db:
                    for table, column, value, id_ in updates:
                        db.execute(f'UPDATE {table} SET {column}=? WHERE Id=?', (value, id_))
                changed = len(updates)

    # Snapshot metadata embeds absolute backup paths. Update only backup-path fields.
    for path in (data / 'rdp-optimizer' / 'snapshots').glob('**/*.json'):
        document = json.loads(path.read_text())
        modified = False
        def visit(item):
            nonlocal modified
            if isinstance(item, dict):
                for key, value in item.items():
                    if key.lower() == 'backuppath' and isinstance(value, str):
                        new = rebase(value, 'rdp-optimizer')
                        modified |= new != value
                        item[key] = new
                    else:
                        visit(value)
            elif isinstance(item, list):
                for child in item:
                    visit(child)
        visit(document)
        if modified:
            path.write_text(json.dumps(document, indent=2) + '\n')

    # Exact managed marker + option match: unrelated fstab entries remain byte-for-byte intact.
    if fstab.exists():
        original = fstab.read_text()
        lines = original.splitlines(keepends=True)
        for index, line in enumerate(lines):
            for marker, option, old, new in managed:
                if new and re.search(r'#\s*' + re.escape(marker) + r'(?:\s|$)', line):
                    def escape(value):
                        return value.replace('\\', '\\134').replace(' ', '\\040').replace('\t', '\\011')
                    def replace_option(match):
                        stored = re.sub(r'\\([0-7]{3})', lambda m: chr(int(m[1], 8)), match[1])
                        if rebase(stored, 'share-mounts') == new:
                            return option + escape(new)
                        return match[0]
                    line = re.sub(re.escape(option) + r'([^,\s]+)', replace_option, line)
            lines[index] = line
        updated = ''.join(lines)
        if updated != original:
            backup.mkdir(mode=0o700, exist_ok=True)
            shutil.copy2(fstab, backup / f'{stamp}.fstab')
            os.chmod(backup / f'{stamp}.fstab', 0o600)
            # Preserve the existing inode, owner and permissions of /etc/fstab.
            with fstab.open('w') as stream:
                stream.write(updated)
                stream.flush()
                os.fsync(stream.fileno())
    marker = data / 'persistent-storage-v1.json'
    marker.write_text(json.dumps({'completedAtUtc': stamp, 'copiedFiles': copied, 'updatedPaths': changed}) + '\n')
    os.chmod(marker, 0o600)
    if os.geteuid() == 0:
        os.chown(marker, owner.st_uid, owner.st_gid)
    print(f'Persistent storage migration: copied {copied} files; updated {changed} saved paths.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--previous-release', required=True)
    parser.add_argument('--data-root', required=True)
    parser.add_argument('--connection-string', default='')
    parser.add_argument('--fstab', default='/etc/fstab')
    parser.add_argument('--prefix', default='')
    args = parser.parse_args()
    database = str(Path(args.data_root) / 'linuxmadesane.db')
    if args.connection_string:
        match = re.search(r'(?:^|;)\s*(?:Data Source|DataSource|Filename)\s*=\s*("[^"]*"|\x27[^\x27]*\x27|[^;]+)', args.connection_string, re.I)
        if not match:
            raise ValueError('Cannot resolve the configured SQLite database path')
        database = match[1].strip().strip('"\x27')
        if not Path(database).is_absolute():
            database = str(Path(args.previous_release) / database)
        elif args.prefix:
            database = args.prefix.rstrip('/') + database
    migrate(args.previous_release, args.data_root, database, args.fstab)


if __name__ == '__main__':
    main()
