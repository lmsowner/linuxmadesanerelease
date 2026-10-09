# Copyright (c) Richard D. Kiernan.
# Licensed under the Business Source License 1.1. See LICENSE for details.
# Credentials arrive only through the supervised process's stdin, never argv, env or disk.
import ctypes, json, os, select, signal, socket, sys

config = json.loads(sys.stdin.readline())
# PR_SET_PDEATHSIG watches the spawning THREAD. .NET pool threads can retire
# while LMS remains alive, which used to terminate healthy SSH tunnels.
# Watch the supervisor process instead; the broker below owns this watch.
if os.getppid() != config['supervisorPid']:
    sys.exit(1)
supervisor_pid = config['supervisorPid']
supervisor_fd = None
if hasattr(os, 'pidfd_open'):
    try:
        supervisor_fd = os.pidfd_open(supervisor_pid)
    except OSError:
        pass

def process_identity(pid):
    try:
        with open('/proc/' + str(pid) + '/stat') as info:
            # Field 22 is start time. The process name may itself contain spaces.
            fields = info.read().rsplit(')', 1)[1].split()
            return None if fields[0] == 'Z' else fields[19]
    except (OSError, IndexError):
        return None

supervisor_identity = process_identity(supervisor_pid)
if supervisor_fd is None and supervisor_identity is None:
    sys.exit(1)
private_key = config.pop('privateKey')
key_fd = None
if private_key:
    key_fd = os.memfd_create('lms-ssh-key', 0)
    os.fchmod(key_fd, 0o600)
    os.write(key_fd, private_key.encode())
    os.lseek(key_fd, 0, os.SEEK_SET)
    os.set_inheritable(key_fd, True)
private_key = None

sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
sock.bind(config['socket'])
os.chmod(config['socket'], 0o600)
sock.listen(4)
parent = os.getpid()
child = os.fork()
if child == 0:
    # Do not leave a password broker behind if SSH exits or the LMS supervisor is stopped.
    ctypes.CDLL(None).prctl(1, signal.SIGTERM)
    if os.getppid() != parent:
        os._exit(1)
    watch = select.poll()
    watch.register(sock, select.POLLIN)
    if supervisor_fd is not None:
        watch.register(supervisor_fd, select.POLLIN)
    # Keep the anonymous key file in this broker: OpenSSH closes inherited file descriptors.
    while True:
        events = dict(watch.poll(1000))
        stopped = (supervisor_fd is not None and supervisor_fd in events) or (
            supervisor_fd is None and process_identity(supervisor_pid) != supervisor_identity)
        if stopped:
            if os.getppid() == parent:
                os.write(2, b'LMS supervisor process exited; closing its SSH forward.\n')
                os.kill(parent, signal.SIGTERM)
            os._exit(0)
        if sock.fileno() not in events:
            continue
        conn, _ = sock.accept()
        with conn:
            conn.settimeout(5)
            prompt = conn.recv(4096).decode().lower()
            answer = config['passphrase'] if 'passphrase' in prompt else config['password'] if 'password' in prompt else ''
            conn.sendall(answer.encode())
else:
    sock.close()
    args = config['args']
    if key_fd is not None:
        args = [arg.replace('LMS_MEMORY_KEY', '/proc/' + str(child) + '/fd/' + str(key_fd)) for arg in args]
    os.environ['SSH_ASKPASS'] = config['askpass']
    os.environ['SSH_ASKPASS_REQUIRE'] = 'force'
    os.environ['DISPLAY'] = 'lms-ssh-forward'
    os.environ['LMS_SSH_ASKPASS_SOCKET'] = config['socket']
    config = None
    os.execv('/usr/bin/ssh', ['ssh'] + args)
