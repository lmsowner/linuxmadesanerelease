#!/usr/bin/env python3
# Copyright (c) Richard D. Kiernan.
# Licensed under the Business Source License 1.1. See LICENSE for details.
"""Exercise the exported CE authentication/recovery routes in an isolated instance.

Run after building CE in Release. Never use an installed LMS database or recovery code.
"""
import base64
import hashlib
import hmac
import http.cookiejar
import re
import struct
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timedelta, timezone

ROOT = Path(__file__).resolve().parents[2]


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args):
        return None


def check(condition, message):
    if not condition:
        raise RuntimeError(message)


def main():
    app = ROOT / "src/LinuxMadeSane.Web/bin/Release/net10.0/LinuxMadeSane.Web.dll"
    check(app.exists(), "Build the CE Web project in Release before running this check.")
    with tempfile.TemporaryDirectory(prefix="lms-ce-auth-") as directory:
        workspace = Path(directory)
        challenge = workspace / "access-recovery.json"
        now = datetime.now(timezone.utc)
        challenge_payload = json.dumps({
            "purpose": "linux-made-sane-temporary-setup", "version": 1,
            "challengeId": "ce-regression", "salt": "ce-test-salt",
            "codeHash": hashlib.sha256(b"ce-test-salt:A1B2C3D4").hexdigest(),
            "attempts": 0, "createdAtUtc": now.isoformat(),
            "expiresAtUtc": (now + timedelta(minutes=10)).isoformat(),
        })
        challenge.write_text(challenge_payload)
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        base_url = f"http://127.0.0.1:{port}"
        environment = os.environ | {
            "ASPNETCORE_ENVIRONMENT": "Production",
            "ASPNETCORE_URLS": base_url, "Server__Urls__0": base_url,
            "ConnectionStrings__LinuxMadeSane": f"Data Source={workspace}/lms.db",
            "DataProtection__KeyDirectory": str(workspace / "keys"),
            "AccessRecovery__ChallengePath": str(challenge),
            "ApplicationUpdates__Enabled": "false",
        }
        cookies = http.cookiejar.CookieJar()
        client = urllib.request.build_opener(NoRedirect, urllib.request.HTTPCookieProcessor(cookies))

        def request(path, data=None):
            try:
                response = client.open(urllib.request.Request(base_url + path, data=data), timeout=5)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                headers = dict(response.headers)
                headers["Set-Cookie"] = "; ".join(response.headers.get_all("Set-Cookie", []))
                return response.status, response.read().decode(), headers

        with (workspace / "startup.log").open("w+") as log:
            process = subprocess.Popen(
                ["dotnet", str(app), "--contentRoot", str(ROOT / "src/LinuxMadeSane.Web")],
                cwd=workspace, env=environment, stdout=log, stderr=log,
            )
            try:
                for _ in range(100):
                    if process.poll() is not None:
                        log.seek(0)
                        raise RuntimeError("CE exited during startup:\n" + log.read()[-5000:])
                    try:
                        if request("/healthz")[0] == 200:
                            break
                    except (urllib.error.URLError, TimeoutError):
                        pass
                    time.sleep(0.2)
                else:
                    raise RuntimeError("CE did not become healthy.")

                # A fresh database must allow leaving setup for the first normal login.
                status, body, _ = request("/setup")
                check(status == 200 and "Temporary Setup Code" in body, "Fresh setup must render.")
                status, _, headers = request("/auth/setup/authorize", b"temporarySetupCode=A1B2-C3D4")
                check(status == 302 and headers.get("Location") == "/settings?tab=trusted-networks" and
                      "lms.temporary-setup=" in headers["Set-Cookie"],
                      "A valid fresh code must grant entry without an account.")
                check(request("/")[0] == 200,
                      "A valid recovery code must open LMS even with no accounts.")
                check(request("/ai/providers")[0] == 200,
                      "Recovery access must allow LMS pages without a login redirect.")
                with sqlite3.connect(workspace / "lms.db") as database:
                    check(database.execute("SELECT COUNT(*) FROM security_users").fetchone()[0] == 0,
                          "Recovery entry must not require or invent an account.")
                status, body, _ = request("/settings?tab=trusted-networks")
                check(status == 200 and "Network Interfaces" in body and "Auth off" in body,
                      "Recovery must land in the full interface security screen with editable controls.")
                saved_cookies = list(cookies)
                cookies.clear()
                status, _, headers = request("/")
                check(status == 302 and headers.get("Location", "").startswith("/setup?"),
                      "An unfinished recovery without its session must return to setup.")
                # Exercise persisted interface policy without modifying host networking.
                with sqlite3.connect(workspace / "lms.db") as database:
                    database.execute("UPDATE trusted_network_entries SET IsAuthenticationEnabled=0")
                check(request("/")[0] == 200,
                      "Turning interface authentication off must permit direct access without any account or recovery cookie.")
                with sqlite3.connect(workspace / "lms.db") as database:
                    database.execute("UPDATE trusted_network_entries SET IsAuthenticationEnabled=1")
                for cookie in saved_cookies:
                    cookies.set_cookie(cookie)
                print("PASS recovery: full security landing, unfinished session returns to setup, auth-off permits entry", flush=True)
                status, body, _ = request("/setup")
                check(status == 200 and "Register LMS login" in body,
                      "After entering the code, a fresh install must render account registration.")
                status, _, headers = request("/auth/initial-setup/start",
                    b"email=first%40example.test&linuxUsername=cecheck&returnUrl=%2F")
                check(status == 302 and "error=" not in headers.get("Location", ""),
                      "Fresh account registration must succeed.")
                status, body, _ = request("/setup")
                secret_match = re.search(r"<code\b[^>]*>([A-Z2-7 ]+)</code>", body)
                check(status == 200 and secret_match is not None, "Pending setup must render the MFA key.")
                secret = secret_match.group(1).replace(" ", "")
                # Model successful setup MFA without changing the test machine's Linux
                # accounts, SSH configuration or network rules. LastLogin remains NULL.
                with sqlite3.connect(workspace / "lms.db") as database:
                    database.execute("UPDATE security_users SET IsEnabled=1 WHERE Email='first@example.test'")
                    database.execute("UPDATE trusted_network_entries SET IsAuthenticationEnabled=0")
                status, body, _ = request("/setup")
                check(status == 200 and "Complete your first login" in body,
                      "Setup must offer the first login after MFA setup.")
                cookies.clear()
                for path in ("/login", "/LMSMFALogin"):
                    status, body, headers = request(path)
                    check(status == 200 and "Continue with passkey" in body,
                          f"First login must render instead of returning to setup: {path}, "
                          f"status={status}, Location={headers.get('Location')}")
                counter = struct.pack(">Q", int(time.time()) // 30)
                key = base64.b32decode(secret + "=" * (-len(secret) % 8))
                digest = hmac.new(key, counter, hashlib.sha1).digest()
                offset = digest[-1] & 15
                code = f"{(struct.unpack('>I', digest[offset:offset + 4])[0] & 0x7fffffff) % 1000000:06d}"
                status, _, headers = request("/auth/login",
                    f"email=first%40example.test&otpCode={code}&returnUrl=%2F".encode())
                check(status == 302 and (headers.get("Location") == "/" or
                      headers.get("Location", "").startswith("/auth/setup-passkey?")),
                      "The first normal MFA login must succeed and leave setup.")
                with sqlite3.connect(workspace / "lms.db") as database:
                    check(database.execute("SELECT LastLoginAtUtc FROM security_users "
                          "WHERE Email='first@example.test'").fetchone()[0] is not None,
                          "The first real login must be recorded.")
                check(request("/setup")[0] == 404 and not challenge.exists(),
                      "Successful first login must close the temporary setup challenge.")
                print("PASS fresh install: code, registration, first login and recovery closure", flush=True)
                cookies.clear()
                fresh_challenge = json.loads(challenge_payload)
                fresh_challenge["createdAtUtc"] = datetime.now(timezone.utc).isoformat()
                challenge.write_text(json.dumps(fresh_challenge))

                # Model an existing account whose last login predates a newly minted
                # installer challenge. This tests recovery after a reinstall, rather
                # than redirecting a brand-new installation to initial setup.
                with sqlite3.connect(workspace / "lms.db") as database:
                    database.execute(
                        """INSERT INTO security_users
                        (Id, Email, NormalizedEmail, LinuxUsername, IsEnabled,
                         SessionLifetimeMinutes, SshAuthenticationMode, AuthorizedKeyEntries,
                         IsLocalAccountManaged, OtpSecretReference, CreatedAtUtc,
                         UpdatedAtUtc, LastLoginAtUtc)
                        VALUES (?, ?, ?, ?, 1, 720, 0, '', 0, 'test-only', ?, ?, ?)""",
                        (str(uuid.uuid4()).upper(), "cecheck@example.test", "CECHECK@EXAMPLE.TEST",
                         "cecheck", (now - timedelta(days=2)).isoformat(),
                         (now - timedelta(days=2)).isoformat(), (now - timedelta(days=1)).isoformat()),
                    )
                for path, text in [("/LMSMFALogin", "Continue with passkey"),
                                   ("/setup", "Temporary Setup Code")]:
                    status, body, headers = request(path)
                    check(status == 200 and text in body,
                          f"{path} must render its form: got {status}, Location={headers.get('Location')}")
                    print(f"PASS {path}: form rendered without a login redirect", flush=True)

                status, body, headers = request(
                    "/LMSMFALogin?returnUrl=%2Fedge-auth%2Freturn%3Ftarget%3D"
                    "https%253A%252F%252Fexample.test%252F&error=MFA%2Fpasskey%20required.")
                check(status == 200 and "Continue with passkey" in body,
                      "The gateway return URL must not create another login redirect.")
                print("PASS gateway return URL: passkey login rendered", flush=True)

                status, _, headers = request(
                    "/auth/setup/authorize", b"temporarySetupCode=A1B2-C3D4&returnUrl=%2F")
                check(status == 302 and headers.get("Location") == "/settings?tab=trusted-networks"
                      and "lms.temporary-setup=" in headers["Set-Cookie"],
                      "A valid code must authorize recovery and enter LMS.")
                check(request("/")[0] == 200, "Reinstall recovery code must open LMS.")
                print("PASS setup code: recovery authorized", flush=True)

                challenge.unlink()
                # A recovery cookie alone cannot bypass access after challenge closure.
                with sqlite3.connect(workspace / "lms.db") as database:
                    database.execute("UPDATE trusted_network_entries SET IsAuthenticationEnabled=1")
                status, _, headers = request("/")
                check(status == 302 and headers.get("Location", "").startswith("/login"),
                      "Recovery entry must end when the temporary challenge closes.")
                status, _, headers = request("/setup")
                check(status == 404 and not headers.get("Location"),
                      "An unavailable setup challenge must return 404 without a login redirect.")
                print("PASS unavailable setup: 404 without a login redirect", flush=True)
            finally:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()


if __name__ == "__main__":
    main()
