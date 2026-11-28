#!/usr/bin/env python3
# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE for details.
"""Merge CE installer defaults without discarding installed service settings."""
import os
from pathlib import Path
import sys
from urllib.parse import urlsplit


def merge(existing, defaults):
    lines = existing.splitlines()
    values = {line.split('=', 1)[0]: line.split('=', 1)[1]
              for line in lines if '=' in line and not line.lstrip().startswith('#')}
    updates = dict(line.split('=', 1) for line in defaults)
    # Listener URLs belong to the existing installation, including its TLS endpoint.
    if values.get('ASPNETCORE_URLS'):
        updates['ASPNETCORE_URLS'] = values['ASPNETCORE_URLS']
    effective = values | updates
    https = next((urlsplit(url.strip()) for url in effective.get('ASPNETCORE_URLS', '').strip('"\'').split(';')
                  if url.strip().startswith('https://')), None)
    if https is not None:
        if 'Server__EnableHttpsRedirection' not in effective:
            updates['Server__EnableHttpsRedirection'] = 'true'
        if 'ASPNETCORE_HTTPS_PORT' not in effective:
            updates['ASPNETCORE_HTTPS_PORT'] = str(https.port or 443)
    result = []
    for line in lines:
        key = line.split('=', 1)[0]
        if key in updates:
            result.append(f'{key}={updates.pop(key)}')
        else:
            result.append(line)
    result.extend(f'{key}={value}' for key, value in updates.items())
    return '\n'.join(result) + '\n'


if __name__ == '__main__':
    destination = Path(sys.argv[1])
    output = merge(destination.read_text() if destination.exists() else '', sys.argv[2:])
    # Preserve existing owner/mode; new service configuration contains secrets.
    fd = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, 'w') as stream:
        stream.write(output)
