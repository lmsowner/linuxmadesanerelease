# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE for details.
"""Run the portable enrollment worker with the active installation's runtime and identity."""
import base64
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

# Use the destination's app-local .NET host, including its native SQLite library.
# This only starts a separate CLI process. The installed web process is never restarted.
RUN_WORKER = """
import ctypes as c, os, sys
from pathlib import Path
root, app = map(Path, sys.argv[1:])
lib = c.CDLL(str(root / 'libhostfxr.so'))
class Parameters(c.Structure):
    _fields_ = [('size', c.c_size_t), ('host_path', c.c_char_p), ('dotnet_root', c.c_char_p)]
params = Parameters(c.sizeof(Parameters), os.fsencode(root / 'LinuxMadeSane.Web'), os.fsencode(root))
handle = c.c_void_p()
argv = (c.c_char_p * 1)(os.fsencode(app))
rc = lib.hostfxr_initialize_for_dotnet_command_line(1, argv, c.byref(params), c.byref(handle))
if rc != 0:
    sys.exit(1)
try:
    code = lib.hostfxr_run_app(handle)
finally:
    lib.hostfxr_close(handle)
sys.exit(code)
"""


def run():
    pid = int(subprocess.check_output(['/usr/bin/systemctl', 'show', 'linux-made-sane.service', '-p', 'MainPID', '--value'], text=True))
    if pid <= 0:
        raise ValueError('inactive')
    proc = Path('/proc') / str(pid)
    program = (proc / 'exe').resolve(strict=True)
    if program.name != 'LinuxMadeSane.Web':
        raise ValueError('unsupported host')
    root = program.parent
    cwd = (proc / 'cwd').resolve(strict=True)
    entries = dict(item.split(b'=', 1) for item in (proc / 'environ').read_bytes().split(b'\0') if b'=' in item)
    env = {'PATH': '/usr/bin:/bin', 'ConnectionStrings__LinuxMadeSane': os.fsdecode(entries[b'ConnectionStrings__LinuxMadeSane'])}
    if b'DataProtection__KeyDirectory' in entries:
        env['DataProtection__KeyDirectory'] = os.fsdecode(entries[b'DataProtection__KeyDirectory'])
    if os.geteuid() == 0:
        identity = dict(line.split(':', 1) for line in (proc/'status').read_text().splitlines() if ':' in line)
        os.setgroups(list(map(int, identity['Groups'].split())))
        os.setgid(int(identity['Gid'].split()[1]))
        os.setuid(int(identity['Uid'].split()[1]))
    envelope = json.loads(sys.stdin.buffer.readline(1024 * 1024))
    with tempfile.TemporaryDirectory(prefix='lms-enrol-') as work:
        directory = Path(work)
        worker = directory / 'LinuxMadeSane.AccountEnrollment.dll'
        worker.write_bytes(base64.b64decode(envelope['Worker'], validate=True))
        for file in root.iterdir():
            if file.name != worker.name and file.is_file() and (file.suffix in ('.so', '.dll') or '.so.' in file.name):
                (directory/file.name).symlink_to(file)
        for suffix in ['.runtimeconfig.json', '.deps.json']:
            worker.with_suffix(suffix).symlink_to(root / ('LinuxMadeSane.Web' + suffix))
        return subprocess.run([sys.executable, '-I', '-c', RUN_WORKER, str(root), str(worker)], cwd=cwd, env=env,
            input=json.dumps(envelope['Request']).encode(), timeout=40).returncode


if __name__ == '__main__':
    try:
        sys.exit(run())
    except (OSError, ValueError, KeyError, subprocess.SubprocessError):
        sys.exit('The active LMS installation could not run account enrollment.')
