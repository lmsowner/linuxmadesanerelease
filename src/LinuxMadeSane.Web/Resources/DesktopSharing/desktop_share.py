#!/usr/bin/env python3
# Copyright (c) 2026 LMS Developer. All rights reserved.
"""LMS console sharing. Root-only runtime leases; never creates/logs into a desktop."""
import hmac
import fcntl
import grp
from contextlib import contextmanager
import json
import os
from pathlib import Path
import pwd
import re
import secrets
import shutil
import signal
import socket
import subprocess
import sys
import time
import uuid

ROOT = Path('/run/linuxmadesane/desktop-sharing')
TTL = 120


def run(args, *, env=None, data=None, optional=False, timeout=30):
    result = subprocess.run(args, input=data, text=True, capture_output=True, env=env, timeout=timeout)
    if result.returncode and not optional:
        # Do not include command arguments, environment or credential output in errors.
        raise RuntimeError(f'{Path(args[0]).name} could not complete desktop sharing: ' + result.stderr[-1500:])
    return result.stdout


def properties(session):
    if not re.fullmatch(r'[A-Za-z0-9_-]{1,64}', session):
        raise RuntimeError('Invalid graphical session identifier.')
    output = run(['loginctl', 'show-session', session, '-p', 'Id', '-p', 'Name', '-p', 'User', '-p', 'Type', '-p', 'Class',
                  '-p', 'State', '-p', 'Remote', '-p', 'Seat', '-p', 'Display', '-p', 'Leader'], optional=True)
    return dict(line.split('=', 1) for line in output.splitlines() if '=' in line)


def select_target(preferred=None):
    ids = [preferred] if preferred else [line.split()[0] for line in run(['loginctl', 'list-sessions', '--no-legend', '--no-pager']).splitlines() if line.split()]
    if preferred and preferred.startswith('lms-'):
        target = next((item for item in managed_targets() if item['Id'] == preferred), None)
        if target: return target
        raise RuntimeError('The desktop session ended. Click View desktop to start it again.')
    targets = [properties(item) for item in ids]
    targets = [item for item in targets if item.get('Type') in ('x11', 'wayland') and item.get('Class') in ('user', 'user-early')
               and item.get('State') not in ('closing', 'offline') and item.get('User', '').isdigit()]
    if not targets:
        if preferred: raise RuntimeError('This user desktop session has ended. Click View desktop again.')
        targets = managed_targets()
        if not targets: return ensure_desktop()
    targets.sort(key=lambda item: (item.get('Remote') == 'yes', not bool(item.get('Seat')), item.get('State') != 'active', item.get('Class') == 'greeter', item.get('Id', '')))
    target = targets[0]
    account = pwd.getpwuid(int(target['User']))
    target.update(UserName=account.pw_name, Home=account.pw_dir, Gid=account.pw_gid)
    return target


def process_environment(pid):
    try:
        return dict(item.split('=', 1) for item in Path(f'/proc/{pid}/environ').read_bytes().decode(errors='replace').split('\0') if '=' in item)
    except (OSError, ValueError):
        return {}


def target_environment(target):
    uid = int(target['User'])
    candidates = []
    observed = []
    for entry in Path('/proc').iterdir():
        if not entry.name.isdigit():
            continue
        try:
            if entry.stat().st_uid != uid:
                continue
            env = process_environment(entry.name)
            observed.append(env)
            if env.get('XDG_SESSION_ID') == target['Id']:
                candidates.append(env)
        except OSError:
            continue
    env = {key: value for key, value in os.environ.items() if key not in ('DISPLAY', 'XAUTHORITY', 'WAYLAND_DISPLAY', 'DBUS_SESSION_BUS_ADDRESS')}
    env.update(HOME=target['Home'], USER=target['UserName'], LOGNAME=target['UserName'], XDG_RUNTIME_DIR=f'/run/user/{uid}')
    if target.get('Type') == 'wayland':
        displays = [path.name for path in Path(f'/run/user/{uid}').glob('wayland-*') if path.is_socket() and path.stat().st_uid == uid]
        if len(displays) == 1:
            # Modern GNOME starts apps through the user manager without XDG_SESSION_ID.
            # Match the actual user's observed Wayland socket, never another user's environment.
            env['WAYLAND_DISPLAY'] = displays[0]
            candidates = [item for item in observed if item.get('WAYLAND_DISPLAY') == displays[0]] + candidates
    for candidate in candidates:
        for key in ('DISPLAY', 'XAUTHORITY', 'DBUS_SESSION_BUS_ADDRESS', 'WAYLAND_DISPLAY', 'XDG_CURRENT_DESKTOP', 'DESKTOP_SESSION', 'XDG_SESSION_DESKTOP'):
            if candidate.get(key):
                env[key] = candidate[key]
    env.setdefault('DBUS_SESSION_BUS_ADDRESS', f'unix:path=/run/user/{uid}/bus')
    if target.get('Type') == 'x11' and target.get('Display'):
        env['DISPLAY'] = target['Display']
    return env


def x11_auth(target, env):
    if target.get('Managed'):
        return target['Display'], target['Authority']
    display = env.get('DISPLAY', '')
    if not re.fullmatch(r':\d+(?:\.\d+)?', display):
        raise RuntimeError('The running X11 desktop did not publish its display address.')
    # Match the actual X server command for this display; never guess a display-manager cookie path.
    for process in Path('/proc').iterdir():
        if not process.name.isdigit():
            continue
        try:
            args = (process / 'cmdline').read_bytes().decode(errors='replace').split('\0')
            if args and Path(args[0]).name in ('Xorg', 'X', 'Xvfb') and display.split('.')[0] in args and '-auth' in args:
                path = args[args.index('-auth') + 1]
                if not path.startswith('/'):
                    path = str((process / 'cwd').resolve() / path)
                if Path(path).is_file():
                    return display, path
        except (OSError, IndexError):
            continue
    for path in (env.get('XAUTHORITY'), str(Path(target['Home']) / '.Xauthority')):
        if path and Path(path).is_file():
            return display, path
    raise RuntimeError('The existing X11 desktop’s display authentication could not be located.')


def as_user(target, args, env):
    return run(['runuser', '-u', target['UserName'], '--', *args], env=env)


def lease_dir(lease):
    return ROOT / uuid.UUID(lease).hex


@contextmanager
def lock(path):
    with open(path, 'a') as handle:
        os.chmod(path, 0o600)
        fcntl.flock(handle, fcntl.LOCK_EX)
        yield


def active_tokens(state):
    return {key: expiry for key, expiry in state.get('tokens', {state['token']: state['expires']}).items() if expiry > time.time()}


def response(state, token):
    return dict(leaseId=state['lease'], leaseToken=token, protocol=state['protocol'], username=state['username'],
                password=state['password'], port=state['port'], sessionId=state['target']['Id'])


def write_state(directory, state):
    temporary = directory / 'state.new'
    temporary.write_text(json.dumps(state))
    temporary.chmod(0o600)
    os.replace(temporary, directory / 'state.json')


def read_state(directory, token):
    state = json.loads((directory / 'state.json').read_text())
    if not any(hmac.compare_digest(key, token) for key in state.get('tokens', {state['token']: state['expires']})):
        raise RuntimeError('This desktop sharing lease is unavailable.')
    return state


def free_port():
    with socket.socket() as server:
        server.bind(('127.0.0.1', 0))
        return server.getsockname()[1]


def describe(target):
    return dict(sessionId=target['Id'], user=target['UserName'], desktopType=target['Type'], protocol='vnc')


def start(preferred=None):
    target = select_target(preferred)
    ROOT.mkdir(parents=True, exist_ok=True)
    ROOT.chmod(0o711)
    with lock(ROOT / ('session-' + target['User'] + '-' + target['Id'] + '.lock')):
        for directory in ROOT.iterdir():
            if not directory.is_dir() or not (directory / 'ready').is_file():
                continue
            try:
                with lock(directory / '.lock'):
                    state = json.loads((directory / 'state.json').read_text())
                    if state.get('protocol') != 'vnc': continue
                    if state['target']['Id'] != target['Id'] or state['target']['User'] != target['User'] or not active_tokens(state):
                        continue
                    if run(['systemctl', 'is-active', 'lms-desktop-share-' + state['lease']], optional=True).strip() != 'active':
                        continue
                    token = secrets.token_hex(32)
                    state['tokens'] = dict(active_tokens(state), **{token: time.time() + TTL})
                    write_state(directory, state)
                    return response(state, token)
            except (OSError, ValueError):
                continue
        return start_new(target)


def start_new(target):
    env = target_environment(target)
    lease = uuid.uuid4().hex
    ROOT.mkdir(parents=True, exist_ok=True)
    ROOT.chmod(0o711)
    directory = lease_dir(lease)
    directory.mkdir(mode=0o711)
    state = dict(lease=lease, token=secrets.token_hex(32), expires=time.time() + TTL, target=target,
                 protocol='vnc', username='', password=secrets.token_urlsafe(24), port=free_port(), mode='new')
    try:
        if target['Type'] == 'x11':
            state['display'], state['authority'] = x11_auth(target, env)
            if not shutil.which('x11vnc'):
                run(['env', 'DEBIAN_FRONTEND=noninteractive', 'apt-get', '-o', 'DPkg::Lock::Timeout=120', 'update'], timeout=600)
                run(['env', 'DEBIAN_FRONTEND=noninteractive', 'apt-get', '-o', 'DPkg::Lock::Timeout=120', 'install', '-y', '--no-install-recommends', 'x11vnc'], timeout=600)
            state['password'] = secrets.token_urlsafe(12)[:8]
            # A root-only password file avoids passwords in argv, logs or viewer URLs.
            (directory / 'vnc.pass').write_text(state['password'] + '\n')
            (directory / 'vnc.pass').chmod(0o600)
        else:
            from install_wayland_vnc import install
            state['server'], password_tool = install()
            state['password'] = secrets.token_urlsafe(12)[:8]
            # vncpasswd emits binary bytes; do not decode or log them.
            password = subprocess.run([password_tool, '-f'], input=(state['password'] + '\n').encode(), capture_output=True, check=True)
            (directory / 'vnc.pass').write_bytes(password.stdout)
            (directory / 'vnc.pass').chmod(0o600)
            os.chown(directory / 'vnc.pass', int(target['User']), int(target['Gid']))
            state['environment'] = {key: env[key] for key in ('HOME', 'USER', 'LOGNAME', 'XDG_RUNTIME_DIR', 'DBUS_SESSION_BUS_ADDRESS', 'WAYLAND_DISPLAY', 'XDG_CURRENT_DESKTOP') if key in env}
        state['expires'] = time.time() + TTL
        state['tokens'] = {state['token']: state['expires']}
        write_state(directory, state)
        run(['systemd-run', '--quiet', '--collect', '--unit=lms-desktop-share-' + lease, '--property=RuntimeMaxSec=8h',
             '--property=KillMode=control-group', sys.executable, str(Path(__file__).resolve()), 'worker', lease])
        deadline = time.time() + 75
        while time.time() < deadline:
            if (directory / 'ready').is_file():
                return response(state, state['token'])
            if (directory / 'error').is_file():
                raise RuntimeError((directory / 'error').read_text())
            time.sleep(0.15)
        raise RuntimeError('The running desktop could not start its sharing connection.')
    except Exception:
        run(['systemctl', 'stop', 'lms-desktop-share-' + lease], optional=True)
        shutil.rmtree(directory, ignore_errors=True)
        raise


def worker(lease):
    directory = lease_dir(lease)
    state = json.loads((directory / 'state.json').read_text())
    child = None
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(0))
    try:
        log = open(directory / 'backend.log', 'a')
        (directory / 'backend.log').chmod(0o600)
        if state['target']['Type'] == 'x11':
            child = subprocess.Popen(['x11vnc', '-norc', '-display', state['display'], '-auth', state['authority'], '-passwdfile', str(directory / 'vnc.pass'),
                                      '-localhost', '-rfbport', str(state['port']), '-forever', '-shared', '-nolookup', '-noxdamage', '-noshm', '-no6'], stdout=log, stderr=log)
        else:
            env = dict(os.environ, **state['environment'])
            child = subprocess.Popen(['runuser', '-u', state['target']['UserName'], '--', state['server'],
                '-interface', '127.0.0.1', '-localhost', '-rfbport', str(state['port']), '-PasswordFile', str(directory / 'vnc.pass'),
                '-SecurityTypes', 'VncAuth', '-AlwaysShared', '-RememberDisplayChoice', 'Always'], env=env, stdout=log, stderr=log)
        deadline = time.time() + 60
        while time.time() < deadline:
            if child and child.poll() is not None:
                raise RuntimeError('The sharing server could not attach to the current display.')
            with socket.socket() as check:
                check.settimeout(2)
                ready = check.connect_ex(('127.0.0.1', state['port'])) == 0
                if ready: ready = check.recv(12).startswith(b'RFB ')
            if ready:
                (directory / 'ready').touch()
                break
            time.sleep(0.2)
        else:
            raise RuntimeError('The sharing server did not become ready for the existing desktop.')
        while True:
            current = json.loads((directory / 'state.json').read_text())
            if not active_tokens(current) or child and child.poll() is not None:
                break
            time.sleep(1)
    except Exception as error:
        detail = ''
        try:
            detail = (directory / 'backend.log').read_text()[-1800:].replace(state['password'], '[redacted]').replace(state['token'], '[redacted]')
        except OSError:
            pass
        (directory / 'error').write_text(str(error) + ('\n' + detail if detail else ''))
        (directory / 'error').chmod(0o600)
        time.sleep(2)
    finally:
        if child and child.poll() is None:
            child.terminate()
            try:
                child.wait(timeout=3)
            except subprocess.TimeoutExpired:
                child.kill()
        shutil.rmtree(directory, ignore_errors=True)


DESKTOPS = Path('/run/linuxmadesane-desktops')


def managed_targets():
    if not DESKTOPS.exists():
        return []
    targets = []
    for path in DESKTOPS.glob('*/target.json'):
        try:
            target = json.loads(path.read_text())
            if run(['systemctl', 'is-active', 'lms-desktop-session-' + target['User']], optional=True).strip() == 'active':
                targets.append(target)
        except (OSError, ValueError):
            pass
    return targets


def configured_account():
    choices = Path('/etc/linuxmadesane/local-desktop')
    preference = json.loads(choices.read_text()) if choices.exists() else {}
    user = preference.get('UserName')
    if not user:
        legacy = Path('/etc/linuxmadesane/remote-desktop-users.json')
        users = json.loads(legacy.read_text()) if legacy.exists() else {}
        if len(users) == 1: user = next(iter(users))
    if user: return pwd.getpwnam(user)
    raise RuntimeError('No desktop is running. Choose a desktop and user in Desktop Setup, or sign in to the desktop first.')


def configured_session(account):
    choices = Path('/etc/linuxmadesane/local-desktop')
    preference = json.loads(choices.read_text()) if choices.exists() else {}
    session = preference.get('Sessions', {}).get(account.pw_name)
    if session: return session
    # Existing installations may only have the former XFCE preference. Resolve its real entry.
    profile = preference.get('Users', {}).get(account.pw_name, preference.get('Profile') if preference.get('UserName') == account.pw_name else None)
    legacy = Path('/etc/linuxmadesane/remote-desktop-users.json')
    users = json.loads(legacy.read_text()) if legacy.exists() else {}
    if profile == 1 or profile is None and users.get(account.pw_name) == 'XFCE':
        entry = Path('/usr/share/xsessions/xfce.desktop')
        if entry.is_file():
            fields = dict(line.split('=', 1) for line in entry.read_text().splitlines() if line.startswith(('Name=', 'Exec=')))
            if fields.get('Exec'): return dict(Id='xfce', Name=fields.get('Name', 'XFCE'), Command=fields['Exec'], Type='x11')
    raise RuntimeError('No desktop is running. Choose the desktop in Desktop Setup, then sign in to it.')


def ensure_desktop():
    account = configured_account()
    if account.pw_uid < 1000 or account.pw_uid >= 65534 or account.pw_shell.endswith(('nologin', 'false')):
        raise RuntimeError('Choose a regular Linux user account for the desktop.')
    session = configured_session(account)
    if session['Type'] != 'x11':
        raise RuntimeError('No desktop is running. Sign in to the selected ' + session['Name'] + ' desktop, then choose View desktop.')
    DESKTOPS.mkdir(parents=True, exist_ok=True)
    DESKTOPS.chmod(0o755)
    with lock(DESKTOPS / (str(account.pw_uid) + '.lock')):
        current = next((target for target in managed_targets() if target['User'] == str(account.pw_uid)), None)
        if current:
            return current
        if not shutil.which('Xvfb') or not shutil.which('xauth') or not shutil.which('xprop'):
            run(['env', 'DEBIAN_FRONTEND=noninteractive', 'apt-get', '-o', 'DPkg::Lock::Timeout=120', 'update'], timeout=600)
            run(['env', 'DEBIAN_FRONTEND=noninteractive', 'apt-get', '-o', 'DPkg::Lock::Timeout=120', 'install', '-y', '--no-install-recommends', 'xvfb', 'xauth', 'dbus-x11', 'x11-utils'], timeout=600)
        directory = DESKTOPS / str(account.pw_uid)
        directory.mkdir(exist_ok=True, mode=0o755)
        directory.chmod(0o755)
        for name in ('target.json', 'error'):
            (directory / name).unlink(missing_ok=True)
        run(['systemd-run', '--quiet', '--collect', '--unit=lms-desktop-session-' + str(account.pw_uid),
             '--property=KillMode=control-group', '--property=TimeoutStopSec=8s', sys.executable, str(Path(__file__).resolve()), 'desktop-worker', account.pw_name, json.dumps(session)])
        deadline = time.time() + 60
        while time.time() < deadline:
            if (directory / 'target.json').exists():
                return json.loads((directory / 'target.json').read_text())
            if (directory / 'error').exists():
                raise RuntimeError((directory / 'error').read_text())
            time.sleep(.2)
        run(['systemctl', 'stop', 'lms-desktop-session-' + str(account.pw_uid)], optional=True)
        raise RuntimeError('The selected desktop could not start. The desktop log has the details.')


def desktop_worker(user, session=None):
    account = pwd.getpwnam(user)
    session = session or configured_session(account)
    directory = DESKTOPS / str(account.pw_uid)
    authority = directory / 'Xauthority'
    xserver = desktop = integration = None
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(0))
    try:
        run(['systemctl', 'start', 'user@' + str(account.pw_uid) + '.service'])
        authority.touch(mode=0o600)
        authority.chmod(0o600)
        cookie = secrets.token_hex(16)
        run(['xauth', '-f', str(authority), 'add', ':0', '.', cookie])
        os.chown(authority, account.pw_uid, account.pw_gid)
        read_fd, write_fd = os.pipe()
        log = open(directory / 'desktop.log', 'a')
        (directory / 'desktop.log').chmod(0o600)
        xserver = subprocess.Popen(['runuser', '-u', user, '--', 'Xvfb', '-displayfd', str(write_fd), '-screen', '0', '1440x900x24',
                                    '-nolisten', 'tcp', '-noreset', '-auth', str(authority)], pass_fds=(write_fd,), stdout=log, stderr=log)
        os.close(write_fd)
        import select
        if not select.select([read_fd], [], [], 15)[0]:
            raise RuntimeError('The desktop display could not start.')
        display = ':' + os.read(read_fd, 32).decode().strip()
        os.close(read_fd)
        if not re.fullmatch(r':\d+', display):
            raise RuntimeError('The desktop display did not provide an address.')
        run(['xauth', '-f', str(authority), 'add', display, '.', cookie])
        os.chown(authority, account.pw_uid, account.pw_gid)
        manager = Path('/usr/local/sbin/linux-made-sane-desktop-helper-setup')
        broker_socket = Path('/run/linuxmadesane/desktop-session.sock')
        if manager.is_file() and broker_socket.exists() and run([str(manager), 'status'], optional=True).strip() == 'enabled':
            group = grp.getgrgid(broker_socket.stat().st_gid).gr_name
            if group == 'linuxmadesane':
                run(['usermod', '-a', '-G', group, user])
        env = dict(os.environ, HOME=account.pw_dir, USER=user, LOGNAME=user, DISPLAY=display, XAUTHORITY=str(authority),
                   XDG_RUNTIME_DIR='/run/user/' + str(account.pw_uid), XDG_SESSION_TYPE='x11', XDG_CURRENT_DESKTOP=session['Name'], DESKTOP_SESSION=session['Id'], XDG_SESSION_DESKTOP=session['Id'], XDG_SESSION_ID='lms-' + str(account.pw_uid))
        env.pop('DBUS_SESSION_BUS_ADDRESS', None)
        desktop = subprocess.Popen(['runuser', '-u', user, '--', 'dbus-run-session', '--', '/bin/sh', '-c', 'exec ' + session['Command']], env=env, cwd=account.pw_dir, stdout=log, stderr=log)
        # Wait for a real window manager on this display, not merely the X server socket.
        session_env = None
        for _ in range(150):
            if desktop.poll() is not None or xserver.poll() is not None:
                raise RuntimeError('The selected desktop stopped before it was ready.')
            for entry in Path('/proc').iterdir():
                try:
                    candidate = process_environment(entry.name) if entry.name.isdigit() else {}
                    if candidate.get('XDG_SESSION_ID') == env['XDG_SESSION_ID'] and candidate.get('DBUS_SESSION_BUS_ADDRESS'):
                        session_env = candidate; break
                except OSError: continue
            if session_env:
                result = run(['runuser', '-u', user, '--', 'xprop', '-root', '_NET_SUPPORTING_WM_CHECK'], env=env, optional=True)
                window = re.search(r'window id # (0x[0-9a-fA-F]+)', result)
                if window and int(window.group(1), 16): break
            time.sleep(.2)
        else:
            raise RuntimeError('The selected desktop did not finish starting.')
        launcher = Path('/usr/local/lib/linuxmadesane/linux-made-sane-desktop-helper-launcher')
        if launcher.is_file() and manager.is_file() and run([str(manager), 'status'], optional=True).strip() == 'enabled':
            helper_env = dict(env)
            if session_env and session_env.get('DBUS_SESSION_BUS_ADDRESS'):
                helper_env['DBUS_SESSION_BUS_ADDRESS'] = session_env['DBUS_SESSION_BUS_ADDRESS']
            integration = subprocess.Popen(['runuser', '-u', user, '--', str(launcher)], env=helper_env,
                                           cwd=account.pw_dir, stdout=log, stderr=log)
        target = dict(Id=env['XDG_SESSION_ID'], Name=user, User=str(account.pw_uid), Type='x11', Class='user', State='active', Remote='yes',
                      Seat='', Display=display, Desktop=session['Name'], UserName=user, Home=account.pw_dir, Gid=account.pw_gid, Managed=True, Authority=str(authority))
        temporary = directory / 'target.new'
        temporary.write_text(json.dumps(target)); temporary.chmod(0o644)
        os.replace(temporary, directory / 'target.json')
        desktop.wait()
    except Exception as error:
        (directory / 'error').write_text(str(error)); (directory / 'error').chmod(0o600)
    finally:
        (directory / 'target.json').unlink(missing_ok=True)
        for child in (integration, desktop, xserver):
            if child and child.poll() is None:
                child.terminate()
                try: child.wait(timeout=3)
                except subprocess.TimeoutExpired: child.kill()


def same_helper_display(target, env, other):
    key = 'DISPLAY' if target['Type'] == 'x11' else 'WAYLAND_DISPLAY'
    expected = env.get(key, '')
    actual = other.get(key, '')
    if key == 'DISPLAY':
        expected, actual = expected.split('.')[0], actual.split('.')[0]
    return bool(expected) and actual == expected


def repair_helper():
    target = select_target()
    manager = Path('/usr/local/sbin/linux-made-sane-desktop-helper-setup')
    launcher = Path('/usr/local/lib/linuxmadesane/linux-made-sane-desktop-helper-launcher')
    if not manager.is_file() or not launcher.is_file():
        raise RuntimeError('Update LMS to restore the Desktop Helper installer, then retry Repair Desktop Helper.')
    run([str(manager), 'repair'], timeout=120)
    env = target_environment(target)
    socket_path = Path('/run/linuxmadesane/desktop-session.sock')
    if not socket_path.exists():
        raise RuntimeError('The LMS desktop connection service is unavailable. Restart LMS, then retry the repair.')
    group = grp.getgrgid(socket_path.stat().st_gid).gr_name
    if group != 'linuxmadesane':
        raise RuntimeError('The desktop connection socket has an unexpected access group. Check the LMS service configuration.')
    run(['usermod', '-a', '-G', group, target['UserName']])
    unit = 'lms-desktop-assistant-' + target['User']
    run(['systemctl', 'stop', unit], optional=True, timeout=30)
    # Only replace helpers in this account and display; other desktops remain connected.
    for entry in Path('/proc').iterdir():
        try:
            if not entry.name.isdigit() or entry.stat().st_uid != int(target['User']): continue
            if (entry / 'exe').resolve().name != 'LinuxMadeSane.DesktopHelper': continue
            other = process_environment(entry.name)
            if same_helper_display(target, env, other): os.kill(int(entry.name), signal.SIGTERM)
        except (OSError, ProcessLookupError): pass
    DESKTOPS.mkdir(exist_ok=True, parents=True); DESKTOPS.chmod(0o755)
    file = DESKTOPS / ('assistant-' + target['User'] + '.env')
    allowed = ('HOME', 'USER', 'LOGNAME', 'DISPLAY', 'XAUTHORITY', 'WAYLAND_DISPLAY', 'DBUS_SESSION_BUS_ADDRESS', 'XDG_RUNTIME_DIR', 'XDG_CURRENT_DESKTOP', 'DESKTOP_SESSION', 'XDG_SESSION_DESKTOP')
    values = {key: env[key] for key in allowed if key in env}
    values.update(XDG_SESSION_TYPE=target['Type'], XDG_SESSION_ID=target['Id'], LMS_DESKTOP_HELPER_OPEN_WINDOW='true')
    file.write_text('\n'.join(key + '=' + json.dumps(value) for key, value in values.items()) + '\n'); file.chmod(0o600)
    binding = 'lms-desktop-session-' + target['User'] + '.service' if target.get('Managed') else 'session-' + target['Id'] + '.scope'
    run(['systemctl', 'reset-failed', unit], optional=True)
    run(['systemd-run', '--quiet', '--collect', '--unit=' + unit, '--uid=' + target['UserName'],
         '--property=BindsTo=' + binding, '--property=TimeoutStopSec=8s', '--property=EnvironmentFile=' + str(file), str(launcher)])
    for _ in range(100):
        pid = int(run(['systemctl', 'show', unit, '-p', 'MainPID', '--value']).strip() or '0')
        if pid and Path(f'/proc/{pid}/exe').resolve().name == 'LinuxMadeSane.DesktopHelper':
            return dict(user=target['UserName'], processId=pid)
        time.sleep(.1)
    raise RuntimeError('The helper could not open in this desktop. Check that View desktop opens the screen, then retry Repair Desktop Helper.')


def main():
    if os.geteuid() != 0:
        raise RuntimeError('Desktop sharing must run through the LMS system service.')
    action = sys.argv[1]
    if action == 'desktop-worker':
        desktop_worker(sys.argv[2], json.loads(sys.argv[3]) if len(sys.argv) > 3 else None); return
    if action == 'worker':
        worker(sys.argv[2])
        return
    body = json.load(sys.stdin)
    if action == 'repair':
        result = repair_helper()
    elif action == 'inspect':
        result = describe(select_target(body.get('sessionId')))
    elif action == 'start':
        result = start(body.get('sessionId'))
    elif action in ('renew', 'stop'):
        directory = lease_dir(body['leaseId'])
        try:
            with lock(directory / '.lock'):
                state = read_state(directory, body['leaseToken'])
                tokens = active_tokens(state)
                active = body['leaseToken'] in tokens and run(['systemctl', 'is-active', 'lms-desktop-share-' + state['lease']], optional=True).strip() == 'active'
                if action == 'renew' and active:
                    tokens[body['leaseToken']] = time.time() + TTL
                elif action == 'stop':
                    tokens.pop(body['leaseToken'], None)
                state['tokens'] = tokens
                write_state(directory, state)
                stop = not tokens
            if stop:
                run(['systemctl', 'stop', 'lms-desktop-share-' + state['lease']], optional=True)
                shutil.rmtree(directory, ignore_errors=True)
            result = dict(active=action == 'renew' and active)
        except FileNotFoundError:
            result = dict(active=False)

    else:
        raise RuntimeError('Unsupported desktop sharing action.')
    print(json.dumps(result))


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(json.dumps(dict(error=str(error))))
        sys.exit(1)
