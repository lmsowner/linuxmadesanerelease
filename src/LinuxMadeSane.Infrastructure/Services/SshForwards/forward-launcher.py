# Copyright (c) Richard D. Kiernan.
# Licensed under the Business Source License 1.1. See LICENSE for details.
# Credentials arrive only through the supervised process's stdin, never argv, env or disk.
import ctypes, json, os, signal, socket, sys

config = json.loads(sys.stdin.readline())
# A crashed LMS process must not leave an orphan listener that blocks recovery.
ctypes.CDLL(None).prctl(1, signal.SIGTERM)
if os.getppid() != config['supervisorPid']:
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
    # Keep the anonymous key file in this broker: OpenSSH closes inherited file descriptors.
    while True:
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
