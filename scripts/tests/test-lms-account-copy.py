#!/usr/bin/env python3
# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE for details.
"""Exercise two disposable published CE installations; arguments: web executable, worker DLL, SSH launcher.
Run as an unprivileged user in a private network namespace. Never use installed databases.
"""
import base64
import contextlib
import concurrent.futures
import hashlib
import hmac
import json
import os
from pathlib import Path
import shutil
import socket
import sqlite3
import struct
import subprocess
import sys
import tempfile
import time
import urllib.request
import urllib.parse
import urllib.error
import uuid

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args):
        return None

client = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
web, bundle, launcher = map(lambda p: Path(p).resolve(strict=True), sys.argv[1:])
worker = base64.b64encode(bundle.read_bytes()).decode()

@contextlib.contextmanager
def destination():
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        for settings in web.parent.glob('appsettings*.json'):
            shutil.copy(settings, root)
        with socket.socket() as sock:
            sock.bind(('127.0.0.1',0))
            url = 'http://127.0.0.1:' + str(sock.getsockname()[1])
        db = root/'lms.db'
        env = os.environ | {'ASPNETCORE_URLS':url, 'Server__Urls__0':url, 'HOME':tmp,
            'ConnectionStrings__LinuxMadeSane':'Data Source='+str(db),'DataProtection__KeyDirectory':str(root/'keys'),
            'AccessRecovery__ChallengePath':str(root/'unused'), 'ApplicationUpdates__Enabled':'false'}
        with (root/'fixture.log').open('w') as log:
            app = subprocess.Popen([web,'--contentRoot',tmp], cwd=root, env=env,stdout=log,stderr=log)
            try:
                for _ in range(150):
                    if app.poll() is not None: raise RuntimeError('Fixture exited: ' + ' '.join(line.strip() for line in (root/'fixture.log').read_text().splitlines() if 'address' in line.lower() or 'bind to' in line.lower())[:550])
                    try:
                        if client.open(url+'/healthz', timeout=2).status == 200: break
                    except OSError: pass
                    time.sleep(.2)
                else: raise RuntimeError('Fixture failed to start')
                # Only replace the service manager PID lookup. Use the actual process env,
                # active binary, crypto store and SSH command input/output protocol.
                selector = "import sys; from unittest.mock import patch; from runpy import run_path\nwith patch('subprocess.check_output', return_value=sys.argv[2]): run_path(sys.argv[1],run_name='__main__')"
                def run(req):
                    args = [sys.executable,'-I','-c',selector,str(launcher),str(app.pid)]
                    reply = subprocess.run(args,input=json.dumps(dict(Worker=worker,Request=req)),
                        capture_output=True,text=True,timeout=50)
                    if reply.returncode: raise RuntimeError(reply.stderr[:1800])
                    return json.loads(reply.stdout)
                yield run, db, url
            finally:
                app.kill()
                app.wait()

def otp(reply, offset_steps=0):
    seed = urllib.parse.parse_qs(urllib.parse.urlsplit(reply['OtpUri']).query)['secret'][0]
    digest = hmac.new(base64.b32decode(seed),struct.pack('>Q',int(time.time())//30 + offset_steps),hashlib.sha1).digest()
    offset = digest[-1]&15
    return '%06d' % ((struct.unpack('>I',digest[offset:offset+4])[0]&0x7fffffff)%1000000)

def main():
    assert os.geteuid() != 0
    req = dict(OperationId=str(uuid.uuid4()), Email='account@example.test', SessionMinutes=480, Issuer='LMS (east)', Ticket=None, Code=None)
    previous = None
    for name in ('east', 'west'):
        with destination() as (run, db, url):
            params = req | dict(OperationId=str(uuid.uuid4()), Issuer='LMS ('+name+')')
            check = params | dict(CheckOnly=True, Email=params['Email'].upper())
            with sqlite3.connect(db) as conn:
                counts = conn.execute('select (select count(*) from security_users), (select count(*) from protected_secrets)').fetchone()
            assert run(check) == dict(Status='missing', Ticket=None, OtpUri=None)
            with sqlite3.connect(db) as conn:
                assert counts == conn.execute('select (select count(*) from security_users), (select count(*) from protected_secrets)').fetchone()
            pending = run(params)
            assert pending['Status'] == 'pending'
            valid = {otp(pending, step) for step in (-1, 0, 1)}
            wrong = next(str(n).zfill(6) for n in range(10) if str(n).zfill(6) not in valid)
            assert run(params | dict(Ticket=pending['Ticket'], Code=wrong))['Status']=='invalid'
            if previous:
                assert pending['OtpUri'] != previous['OtpUri']
                assert run(params | dict(Ticket=previous['Ticket'], Code=otp(previous)))['Status']=='expired'
            with sqlite3.connect(db) as conn:
                assert conn.execute('select count(*) from security_users').fetchone()==(0,)
            verified = params | dict(Ticket=pending['Ticket'], Code=otp(pending))
            with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
                assert all(reply['Status']=='verified' for reply in pool.map(run, [verified, verified]))
            assert run(params | dict(OperationId=str(uuid.uuid4())))['Status']=='exists'
            form = urllib.parse.urlencode(dict(email=req['Email'],otpCode=otp(pending),returnUrl='/')).encode()
            try: client.open(url+'/auth/login',form,timeout=8)
            except urllib.error.HTTPError as e: assert e.code==302 and 'error=' not in e.headers.get('Location','')
            with sqlite3.connect(db) as conn:
                row = conn.execute('select IsEnabled,LastLoginAtUtc,SessionLifetimeMinutes,IsLocalAccountManaged,LinuxUsername from security_users').fetchone()
                assert row[0] == 1 and row[1] and row[2:] == (480,0,'')
                assert conn.execute('select count(*) from security_passkey_credentials').fetchone() == (0,)
            assert run(check) == dict(Status='exists', Ticket=None, OtpUri=None)
            with sqlite3.connect(db) as conn:
                conn.execute('update security_users set IsEnabled=0 where NormalizedEmail=?', (check['Email'],))
            assert run(check)['Status']=='exists'
            previous = pending
    print('Two independent QR codes, invalid PIN, abandoned enrollment, foreign ticket, verified logins, retry and existing-account preservation passed.')
if __name__=='__main__':
    main()
