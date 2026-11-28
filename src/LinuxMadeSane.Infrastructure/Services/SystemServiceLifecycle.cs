// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Infrastructure.Services;

internal static class SystemServiceLifecycle
{
    internal const string Script = """
import os, sys, stat, json, pathlib, subprocess, hashlib, fcntl, shutil


def command(*args):
    result = subprocess.run(['systemctl', *args], capture_output=True, text=True, timeout=90)
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or 'systemctl operation failed')
    return result.stdout


def trusted_directory(path):
    path = pathlib.Path(path)
    for part in [*reversed(path.parents), path]:
        if not part.exists():
            part.mkdir(mode=0o700)
        info = part.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid not in (0, os.geteuid()) or info.st_mode & 0o022:
            raise RuntimeError('Unsafe directory; no service definition was changed: ' + str(part))
    return path


def regular_unit(path):
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_nlink != 1 or os.path.ismount(path):
        raise RuntimeError('Linked, mounted or untrusted service definitions cannot be removed.')
    return info


def manage(unit, action, backup_root='/var/lib/linux-made-sane-service-backups', unit_root='/etc/systemd/system'):
    import re
    if not re.fullmatch(r'[A-Za-z0-9_][A-Za-z0-9_.:@\\-]*\.service', unit):
        raise RuntimeError('Invalid system service unit.')
    if action not in ('block', 'unblock', 'remove', 'restore'):
        raise RuntimeError('Unsupported service operation.')
    protected = {'dbus.service', 'systemd-journald.service', 'systemd-logind.service', 'systemd-udevd.service'}
    try:
        protected.update(part for part in pathlib.Path('/proc/self/cgroup').read_text().replace('\n', '/').split('/') if part.endswith('.service'))
    except OSError:
        pass
    if unit in protected or unit.startswith('linux-made-sane'):
        raise RuntimeError('This service is required by LMS or the system and cannot be removed or blocked here.')
    units = trusted_directory(unit_root)
    if os.path.ismount(units):
        raise RuntimeError('The unit directory is a mount point; no definition was changed.')
    root = trusted_directory(backup_root)
    directory = trusted_directory(root / hashlib.sha256(unit.encode()).hexdigest())
    with (directory / 'lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        target, saved, metadata = units / unit, directory / 'unit', directory / 'record.json'
        record = json.loads(metadata.read_text()) if metadata.exists() else None
        if action == 'restore' and not record:
            raise RuntimeError('The service backup is unavailable on this host. Nothing was restored or unblocked.')
        if record and record.get('unit') != unit:
            raise RuntimeError('Service backup identity does not match.')
        if action in ('unblock', 'restore'):
            if record and record['local']:
                regular_unit(saved)
                if os.path.lexists(target) and not (target.is_symlink() and os.readlink(target) == '/dev/null'):
                    raise RuntimeError('A service definition already exists; it will not be overwritten.')
            command('unmask', '--', unit)
            if record and record['local']:
                with target.open('xb') as output, saved.open('rb') as source:
                    shutil.copyfileobj(source, output)
                    output.flush(); os.fsync(output.fileno())
                target.chmod(0o644)
            command('daemon-reload')
            return 'Service restored/unblocked. It has not been started or enabled at boot. The unit backup is retained.'
        if not record:
            properties = dict(line.split('=', 1) for line in command('show', '--property=Id,FragmentPath', '--', unit).splitlines() if '=' in line)
            if properties.get('Id') != unit:
                raise RuntimeError('Select the canonical service unit, not an alias.')
            fragment = pathlib.Path(properties.get('FragmentPath', ''))
            local = fragment == target
            if not local and not str(fragment).startswith(('/usr/lib/systemd/system/', '/lib/systemd/system/')):
                raise RuntimeError('Only standalone local units or package-installed service units can be removed here.')
            if local:
                original = regular_unit(target)
                with target.open('rb') as source, saved.open('xb') as output:
                    shutil.copyfileobj(source, output)
                    output.flush(); os.fsync(output.fileno())
                saved.chmod(0o600)
            record = {'unit': unit, 'local': local, 'fragment': str(fragment)}
            with metadata.open('x') as output:
                json.dump(record, output); output.flush(); os.fsync(output.fileno())
            metadata.chmod(0o600)
        command('stop', '--', unit)
        state = command('show', '--property=UnitFileState', '--value', '--', unit).strip()
        if not state.startswith('masked'):
            command('disable', '--', unit)
        if record['local'] and os.path.lexists(target) and not (target.is_symlink() and os.readlink(target) == '/dev/null'):
            regular_unit(target)
            if target.read_bytes() != saved.read_bytes():
                raise RuntimeError('The definition changed since its backup. Nothing was removed; review the current file.')
            target.unlink()  # Exactly one verified unit file; never an application path or directory.
        try:
            command('mask', '--', unit)
            command('daemon-reload')
        except Exception:
            if record['local'] and not os.path.lexists(target):
                with target.open('xb') as output, saved.open('rb') as source:
                    shutil.copyfileobj(source, output)
                target.chmod(0o644)
                command('daemon-reload')
            raise
        return 'Service stopped and blocked from starting. Application files, package files, configuration and data are retained.'


if __name__ == '__main__':
    try:
        print(manage(sys.argv[1], sys.argv[2]))
    except Exception as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)

""";
}
