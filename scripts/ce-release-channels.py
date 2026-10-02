#!/usr/bin/env python3
# Copyright (c) Richard D. Kiernan.
# Licensed under the Business Source License 1.1. See LICENSE for details.
"""Publish development or explicitly promote its exact CE artifact to stable."""
import argparse, datetime, fcntl, hashlib, json, os, pathlib, re, shutil, tarfile, tempfile

def load(root):
    path = root / 'channels.json'
    return json.loads(path.read_text()) if path.exists() else {'development': None, 'stable': None}

def verify(root, version):
    if not re.fullmatch(r'v\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}', version):
        raise ValueError('Invalid CE version')
    directory = root / version
    if directory.is_symlink(): raise ValueError('CE release directories must not be symlinks')
    manifest = json.loads((directory / ('release-manifest-' + version + '.json')).read_text())
    if manifest['version'] != version or manifest['edition'] not in ('ce', 'community'):
        raise ValueError('CE manifest markers do not match')
    assets = manifest['artifacts']
    if len(assets) != 1 or assets[0]['runtime'] != 'linux-x64':
        raise ValueError('CE channels currently require exactly one linux-x64 tarball')
    asset = assets[0]
    name = 'linux-made-sane-ce-' + version + '-linux-x64.tar.gz'
    if asset['file'] != name: raise ValueError('Unexpected CE artifact filename')
    path = directory / name
    if path.is_symlink() or path.stat().st_size != asset['sizeBytes']: raise ValueError('Invalid CE artifact size or path')
    digest = hashlib.sha256()
    with path.open('rb') as f:
        for block in iter(lambda: f.read(1024 * 1024), b''): digest.update(block)
    if digest.hexdigest() != asset['sha256']: raise ValueError('CE checksum mismatch')
    with tarfile.open(path, 'r:gz') as archive:
        package = 'linux-made-sane-ce-' + version + '-linux-x64/'
        for name, expected in [('edition.txt', 'ce'), ('version.txt', version), ('source-commit.txt', manifest['sourceCommit'])]:
            if archive.extractfile(package + name).read().decode().strip() != expected:
                raise ValueError('CE archive marker mismatch: ' + name)
    return asset, manifest

def update(root, operation, version, expected_sha=None, approved_by=None):
    root = pathlib.Path(root).resolve()
    root.mkdir(parents=True, exist_ok=True)
    with (root / '.channel-lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        state = load(root)
        asset, manifest = verify(root, version)
        if state.get('stable'):
            stable_asset, _ = verify(root, state['stable'])
            if stable_asset['sha256'] != state.get('stableSha256'):
                raise ValueError('Approved stable checksum changed')
        if state.get('development') == version and state.get('developmentSha256') not in (None, asset['sha256']):
            raise ValueError('Published versions are immutable')
        if operation == 'publish-development':
            previous = state.get('development')
            if previous and version < previous: raise ValueError('Development releases must roll forward')
            state.update(development=version, developmentSha256=asset['sha256'], developmentSourceCommit=manifest['sourceCommit'])
        elif operation == 'promote-stable':
            if version != state.get('development'): raise ValueError('Only the current development release can be promoted')
            if asset['sha256'] != expected_sha or not approved_by: raise ValueError('Explicit approval name and matching SHA256 are required')
            if state.get('stable') and version < state['stable']: raise ValueError('Stable releases must roll forward')
            state.update(stable=version, stableApprovedBy=approved_by,
                         stableApprovedAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                         stableSha256=asset['sha256'], stableSourceCommit=manifest['sourceCommit'])
        else: raise ValueError('Unknown channel operation')
        # A broken stable reference must block pruning rather than lose its approved artifact.
        if state.get('stable'): verify(root, state['stable'])
        fd, temporary = tempfile.mkstemp(dir=root, prefix='.channels-')
        try:
            with os.fdopen(fd, 'w') as f: json.dump(state, f, indent=2); f.write('\n')
            os.chmod(temporary, 0o664)
            os.replace(temporary, root / 'channels.json')
        finally:
            if os.path.exists(temporary): os.unlink(temporary)
        keep = {state.get('development'), state.get('stable')}
        for directory in root.iterdir():
            if directory.is_dir() and re.fullmatch(r'v\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}', directory.name) and directory.name not in keep:
                shutil.rmtree(directory)
        return state

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=['publish-development', 'promote-stable'])
    parser.add_argument('--root', required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--sha256')
    parser.add_argument('--approved-by')
    args = parser.parse_args()
    print(json.dumps(update(args.root, args.operation, args.version, args.sha256, args.approved_by), indent=2))
