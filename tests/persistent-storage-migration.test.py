# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE for details.
import importlib.util
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('migration', Path(__file__).parents[1] / 'scripts/migrate-persistent-storage.py')
migration = importlib.util.module_from_spec(spec)
spec.loader.exec_module(migration)


class PersistentStorageTests(unittest.TestCase):
    def test_preserves_files_rebases_records_and_only_owned_fstab_entries(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); old = root / 'releases/v1'; new = root / 'releases/v2'; data = root / 'persistent'
            old.mkdir(parents=True); new.mkdir(); data.mkdir()
            key = old / 'data/share-mounts/sshfs-keys/abc.key'; key.parent.mkdir(parents=True); key.write_text('private test key')
            credential = old / 'data/share-mounts/credentials/def.cred'; credential.parent.mkdir(); credential.write_text('test credential')
            snapshot = old / 'data/sftp-backups/snapshots/one/config'; snapshot.parent.mkdir(parents=True); snapshot.write_text('backup')
            rdp = old / 'data/rdp-optimizer/snapshots/one/snapshot.json'; rdp.parent.mkdir(parents=True)
            rdp.write_text(json.dumps({'FileBackups': [{'BackupPath': str(rdp.parent / 'files/config'), 'SourcePath': '/etc/config'}]}))
            dbpath = data / 'linuxmadesane.db'
            with sqlite3.connect(dbpath) as db:
                db.execute('create table sshfs_mounts(Id text, IdentityFilePath text)'); db.execute('insert into sshfs_mounts values(?,?)', ('ABC', str(key)))
                db.execute('create table remote_share_mounts(Id text, CredentialFilePath text)'); db.execute('insert into remote_share_mounts values(?,?)', ('DEF', str(credential)))
                db.execute('create table sftp_backup_snapshots(Id text, StorageDirectory text, FilesJson text)')
                db.execute('insert into sftp_backup_snapshots values(?,?,?)', ('one', str(snapshot.parent), json.dumps([{'sourcePath':'/etc/config', 'backupPath':str(snapshot)}])))
            unmanaged = f'other /mnt/other fuse.sshfs IdentityFile={key} 0 0 # unrelated\n'
            fstab = root / 'fstab'; fstab.write_text(unmanaged + f'host /mnt/owned fuse.sshfs IdentityFile={key},reconnect 0 0 # lms-sshfs-mount:abc\n' + f'//host/share /mnt/smb cifs credentials={credential},nofail 0 0 # lms-remote-mount:def\n')
            migration.migrate(new, data, dbpath, fstab)
            self.assertIn(unmanaged, fstab.read_text()); self.assertIn(f'IdentityFile={data}/share-mounts/sshfs-keys/abc.key,', fstab.read_text())
            self.assertIn(f'credentials={data}/share-mounts/credentials/def.cred,', fstab.read_text())
            with sqlite3.connect(dbpath) as db:
                self.assertEqual(str(data/'share-mounts/sshfs-keys/abc.key'), db.execute('select IdentityFilePath from sshfs_mounts').fetchone()[0])
                storage, files = db.execute('select StorageDirectory, FilesJson from sftp_backup_snapshots').fetchone()
                self.assertTrue(storage.startswith(str(data))); self.assertTrue(json.loads(files)[0]['backupPath'].startswith(str(data)))
            persistent_key = data/'share-mounts/sshfs-keys/abc.key'
            self.assertEqual('private test key', persistent_key.read_text()); self.assertEqual(0o600, persistent_key.stat().st_mode & 0o777)
            self.assertTrue(key.exists()); self.assertEqual('backup', (data/'sftp-backups/snapshots/one/config').read_text())
            self.assertIn(str(data/'rdp-optimizer'), (data/'rdp-optimizer/snapshots/one/snapshot.json').read_text())
            persistent_key.write_text('newer persistent key')
            before = fstab.read_text()
            migration.migrate(new, data, dbpath, fstab)
            self.assertEqual('newer persistent key', persistent_key.read_text()); self.assertEqual(before, fstab.read_text())
            # Restart after a partially completed migration must also repair fstab.
            fstab.write_text(before.replace(str(data/'share-mounts/sshfs-keys/abc.key'), str(key)))
            migration.migrate(new, data, dbpath, fstab)
            self.assertEqual(before, fstab.read_text())

    def test_missing_legacy_key_rebases_without_creating_empty_key(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); old = root/'releases/v1'; old.mkdir(parents=True); data = root/'persistent';data.mkdir()
            dbpath = data/'linuxmadesane.db'; old_key = old/'data/share-mounts/sshfs-keys/abc.key'
            with sqlite3.connect(dbpath) as db:
                db.execute('create table sshfs_mounts(Id text, IdentityFilePath text)'); db.execute('insert into sshfs_mounts values(?,?)', ('ABC', str(old_key)))
            fstab = root/'fstab';fstab.write_text(f'host /mnt/test fuse.sshfs IdentityFile={old_key} 0 0 # lms-sshfs-mount:abc\n')
            migration.migrate(old, data, dbpath, fstab)
            self.assertIn(str(data/'share-mounts/sshfs-keys/abc.key'), fstab.read_text())
            self.assertFalse((data/'share-mounts/sshfs-keys/abc.key').exists())


if __name__ == '__main__': unittest.main()
