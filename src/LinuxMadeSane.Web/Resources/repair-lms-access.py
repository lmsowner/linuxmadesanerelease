# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE for details.
"""Restore authenticated LMS UI access using the active service's own database."""
import ipaddress
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import urllib.parse

UNIT = 'linux-made-sane.service'


def database_for_service():
    pid = int(subprocess.check_output(['systemctl', 'show', UNIT, '-p', 'MainPID', '--value'], text=True))
    if pid <= 0:
        raise ValueError('The LMS service has no running process.')
    proc = Path('/proc') / str(pid)
    env = dict(entry.split(b'=', 1) for entry in (proc / 'environ').read_bytes().split(b'\0') if b'=' in entry)
    connection = env.get(b'ConnectionStrings__LinuxMadeSane', b'').decode()
    for part in connection.split(';'):
        key, _, value = part.partition('=')
        if key.strip().lower() in ('data source', 'datasource', 'filename'):
            path = Path(value.strip().strip('"'))
            if not path.is_absolute():
                path = (proc / 'cwd').resolve() / path
            break
    else:
        raise ValueError('The active LMS database could not be identified.')
    # Any SQLite journal/WAL created here must belong to the LMS service user.
    if os.geteuid() == 0:
        identity = dict(line.split(':', 1) for line in (proc / 'status').read_text().splitlines() if ':' in line)
        uid = int(identity['Uid'].split()[1])
        gid = int(identity['Gid'].split()[1])
        os.setgroups([int(group) for group in identity['Groups'].split()])
        os.setgid(gid)
        os.setuid(uid)
    return path


def update_rule(db, peer, operation, stamp):
    address = ipaddress.ip_address(peer)
    address = getattr(address, 'ipv4_mapped', None) or address
    db.execute('BEGIN IMMEDIATE')
    if operation == 'revert':
        # Compare the operation timestamp so another administrator's edits win.
        db.execute("""UPDATE trusted_network_entries SET IsEnabled=0
            WHERE IsBuiltIn=1 AND AddressOrCidr=? AND IsEnabled=1
            AND IsAuthenticationEnabled=1 AND IsTrustedAccessEnabled=0 AND UpdatedAtUtc=?""",
            ('0.0.0.0/0' if address.version == 4 else '::/0', stamp))
        return 'reverted'
    if operation != 'restore':
        raise ValueError('Unknown repair operation.')
    rows = db.execute("""SELECT AddressOrCidr,IsBuiltIn,IsEnabled,IsAuthenticationEnabled,IsTrustedAccessEnabled
                         FROM trusted_network_entries""")
    matches = [(ipaddress.ip_network(row[0], strict=False), row) for row in rows]
    matches = [(net, row) for net, row in matches if address in net]
    if not matches:
        return 'no-match'
    net, row = max(matches, key=lambda pair: (pair[0].prefixlen, not pair[1][1]))
    if net.prefixlen != 0 or not row[1] or row[2] or not row[3] or row[4]:
        return 'not-default-denial'
    usable = db.execute("""SELECT 1 FROM security_users u WHERE u.IsEnabled=1 AND
        (coalesce(u.OtpSecretReference,'')<>'' OR EXISTS
        (SELECT 1 FROM security_passkey_credentials p WHERE p.UserId=u.Id)) LIMIT 1""").fetchone()
    if not usable:
        return 'needs-enrollment'
    db.execute("""UPDATE trusted_network_entries SET IsEnabled=1,UpdatedAtUtc=?
                  WHERE AddressOrCidr=? AND IsBuiltIn=1""", (stamp, row[0]))
    return 'changed'


def main():
    operation, stamp, connection = sys.argv[1:]
    if operation == 'start':
        loaded = subprocess.check_output(['systemctl', 'show', UNIT, '-p', 'LoadState', '--value'], text=True).strip()
        if loaded != 'loaded':
            raise ValueError('The installed LMS system service was not found.')
        state = subprocess.run(['systemctl', 'is-active', UNIT], capture_output=True, text=True).stdout.strip()
        if state in ('inactive', 'failed'):
            subprocess.run(['systemctl', 'start', UNIT], check=True, timeout=30)
            return 'started'
        if state not in ('active', 'activating'):
            raise ValueError('LMS is changing service state. Retry once it settles.')
        return 'running'
    path = database_for_service()
    with sqlite3.connect('file:' + urllib.parse.quote(str(path)) + '?mode=rw', uri=True, timeout=5) as db:
        return update_rule(db, connection.split()[0], operation, stamp)


if __name__ == '__main__':
    try:
        print(main())
    except (OSError, sqlite3.Error, subprocess.SubprocessError, ValueError, IndexError) as error:
        print('Could not repair LMS: ' + str(error), file=sys.stderr)
        sys.exit(1)
