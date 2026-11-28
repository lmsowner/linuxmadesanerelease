#!/usr/bin/env python3
# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE for details.
"""Run in a disposable Linux network namespace with 192.0.2.18/32 assigned to lo.
Arguments: path to unpacked CE executable, path to the repair resource.
The running service, database, accounts, systemctl lookup and recovery code are fixtures.
"""
import base64
import hashlib
import hmac
import http.cookiejar
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import struct
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args):
        return None


def main():
    app, script = map(lambda path: Path(path).resolve(strict=True), sys.argv[1:])
    ip = "192.0.2.18"
    base = f"http://{ip}:5080"
    assert os.geteuid() != 0, "Run the fixture as an unprivileged user."
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        db = root / "fixture.db"
        challenge = root / "setup.json"
        now = datetime.now(timezone.utc)
        challenge.write_text(json.dumps(dict(purpose="linux-made-sane-temporary-setup", version=1,
            challengeId="repair-fixture", salt="test-salt", codeHash=hashlib.sha256(b"test-salt:A1B2C3D4").hexdigest(),
            attempts=0, createdAtUtc=now.isoformat(), expiresAtUtc=(now + timedelta(hours=1)).isoformat())))
        for settings in app.parent.glob("appsettings*.json"):
            shutil.copy(settings, root)
        env = os.environ | {"ASPNETCORE_URLS": base, "Server__Urls__0": base, "Setup__RequireHttps": "false",
            "ConnectionStrings__LinuxMadeSane": f"Data Source={db}", "DataProtection__KeyDirectory": str(root/"keys"),
            "AccessRecovery__ChallengePath": str(challenge), "ApplicationUpdates__Enabled": "false", "HOME": tmp}
        cookies = http.cookiejar.CookieJar()
        client = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect(), urllib.request.HTTPCookieProcessor(cookies))

        def request(path, form=None):
            try:
                response = client.open(base + path, None if form is None else urllib.parse.urlencode(form).encode(), timeout=8)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                return response.status, response.read().decode(), response.headers.get("Location", "")

        with (root/"output.log").open("w+") as log:
            process = subprocess.Popen([str(app), "--contentRoot", tmp], cwd=tmp, env=env, stdout=log, stderr=log)
            try:
                for _ in range(120):
                    if process.poll() is not None:
                        log.seek(0)
                        raise RuntimeError(log.read()[-3000:])
                    try:
                        if request("/healthz")[0] == 200:
                            break
                    except OSError:
                        pass
                    time.sleep(.25)
                else:
                    raise RuntimeError("Fixture did not open its health endpoint.")
                # Only the repair subprocess sees this unit lookup. It resolves the disposable PID.
                ctl = root/"systemctl"
                ctl.write_text(f'#!/bin/sh\n[ "$*" = "show linux-made-sane.service -p MainPID --value" ] || exit 1\necho {process.pid}\n')
                ctl.chmod(0o700)
                repair_env = env | {"PATH": tmp + ":" + env["PATH"]}
                stamp = "2026-01-01 00:00:00.0000000+00:00"

                def repair(op, peer=ip):
                    return subprocess.check_output([sys.executable, str(script), op, stamp, f"{peer} 44000 {ip} 22"],
                        env=repair_env, text=True).strip()

                # Register a real OTP secret without provisioning a Linux account or changing sshd.
                assert request("/auth/setup/authorize", {"temporarySetupCode": "A1B2-C3D4"})[0] == 302
                body = request("/setup")[1]
                token = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', body)
                form = {"email":"fixture@example.test", "linuxUsername":"fixtureuser", "returnUrl":"/"}
                if token:
                    form["__RequestVerificationToken"] = token[1]
                status, _, location = request("/auth/initial-setup/start", form)
                assert status == 302 and "error=" not in location, (status, location)
                body = request("/setup")[1]
                secret = re.search(r"<code\b[^>]*>([A-Z2-7 ]+)</code>", body)[1].replace(" ", "")
                assert repair("restore") == "needs-enrollment"
                with sqlite3.connect(db) as con:
                    con.execute("UPDATE security_users SET IsEnabled=1 WHERE Email='fixture@example.test'")
                cookies.clear()
                challenge.unlink(missing_ok=True)
                assert request("/healthz")[0] == 200 and request("/")[0] == 404 and request("/login")[0] == 404
                print("Confirmed stable CE health 200, public home and login 404", flush=True)
                assert repair("restore", "127.0.0.1") == "not-default-denial"
                assert repair("restore") == "changed"
                assert repair("restore") == "not-default-denial"
                status, _, location = request("/")
                assert status == 302 and "login" in location.lower(), (status, location)
                status, body, _ = request(location)
                assert status == 200 and "Continue with passkey" in body and "_framework/blazor" in body and "auth-login-form" in body
                assert "LinuxMadeSane.Web" in body or "lms-logo-" in body
                key = base64.b32decode(secret + "=" * (-len(secret) % 8))
                digest = hmac.new(key, struct.pack(">Q", int(time.time()) // 30), hashlib.sha1).digest()
                offset = digest[-1] & 15
                otp = (struct.unpack(">I", digest[offset:offset+4])[0] & 0x7fffffff) % 1000000
                status, _, location = request("/auth/login", {"email":form["email"], "otpCode":f"{otp:06d}", "returnUrl":"/"})
                assert status == 302 and "error=" not in location, (status, location)
                with sqlite3.connect(db) as con:
                    assert con.execute("SELECT LastLoginAtUtc FROM security_users WHERE Email=?", (form["email"],)).fetchone()[0]
                    assert con.execute("SELECT IsEnabled FROM trusted_network_entries WHERE AddressOrCidr='::/0'").fetchone() == (0,)
                cookies.clear()
                assert repair("revert") == "reverted" and request("/")[0] == 404
                # A more specific operator-defined rule takes precedence, even when disabled.
                with sqlite3.connect(db) as con:
                    con.execute("""INSERT INTO trusted_network_entries
                        (Id, Label, Description, AddressOrCidr, IsEnabled, IsBuiltIn, IsAuthenticationEnabled,
                         IsTrustedAccessEnabled, DeniedResponseMode, CreatedAtUtc, UpdatedAtUtc)
                        SELECT '22539714-6387-4B96-8DF4-B49536408F48', 'Fixture deny', '', ?, 0, 0, 1, 0,
                         DeniedResponseMode, CreatedAtUtc, UpdatedAtUtc FROM trusted_network_entries
                         WHERE AddressOrCidr='0.0.0.0/0'""", (ip + "/32",))
                assert repair("restore") == "not-default-denial" and request("/")[0] == 404
                print("Verified public MFA sign-in, recorded first login, untouched IPv6 and custom policies, and rollback", flush=True)
            finally:
                process.kill()
                process.wait()


if __name__ == "__main__":
    main()
