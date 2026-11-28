#!/usr/bin/env python3
# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE for details.
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
import shutil
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import urllib.parse
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
    app = Path(os.environ.get("LMS_AUTH_TEST_APP", ROOT / "src/LinuxMadeSane.Web/bin/Release/net10.0/LinuxMadeSane.Web.dll"))
    check(app.exists(), "Build the CE Web project in Release before running this check.")
    with tempfile.TemporaryDirectory(prefix="lms-ce-auth-") as directory:
        workspace = Path(directory)
        # Runtime-created share/cache folders must stay inside this disposable
        # content root, including when the test is run as root in a network namespace.
        for name in ("appsettings.json", "appsettings.Production.json"):
            source = ROOT / "src/LinuxMadeSane.Web" / name
            if source.exists():
                shutil.copyfile(source, workspace / name)
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

        def setup_post(path, values):
            status, body, _ = request("/setup")
            check(status == 200, "Setup form must be available.")
            token = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', body)
            check(token is not None, "Setup actions must include CSRF protection.")
            return request(path, urllib.parse.urlencode(values | {"__RequestVerificationToken": token.group(1)}).encode())

        with (workspace / "startup.log").open("w+") as log:
            process = subprocess.Popen(
                ["dotnet", str(app), "--contentRoot", str(workspace)],
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

                for login_path in ("/login", "/LMSMFALogin"):
                    status, _, headers = request(login_path)
                    check(status == 302 and headers.get("Location") == "/setup", "No account must mean setup, never an empty login form.")
                status, body, _ = request("/setup")
                check(status == 200 and "Temporary Setup Code" in body, "Fresh setup must render.")
                status, _, headers = request("/auth/setup/authorize", b"temporarySetupCode=A1B2-C3D4")
                check(status == 302 and headers.get("Location") == "/setup" and
                      "lms.temporary-setup=" in headers["Set-Cookie"],
                      "A valid fresh code must lead to access setup.")
                for path in ("/", "/ai/providers", "/settings?tab=trusted-networks"):
                    status, _, headers = request(path)
                    check(status == 302 and headers.get("Location") == "/setup", "Unfinished setup must not hand off into the app.")
                check(datetime.fromisoformat(re.sub(r"(\.\d{6})\d+", r"\1", json.loads(challenge.read_text())["expiresAtUtc"])) > datetime.now(timezone.utc) + timedelta(hours=23),
                      "Consuming the code must provide a resumable setup session, not a ten-minute lockout.")
                check(request("/auth/setup/network-access", b"")[0] == 400, "Network changes require a valid setup CSRF token.")
                saved_cookies = list(cookies)
                status, _, headers = setup_post("/auth/setup/network-access", {})
                check(status == 302 and headers.get("Location") == "/setup?networkAccess=enabled", "Explicit network access choice must be saved.")
                status, body, _ = request("/setup")
                check(status == 200 and "Sign-in is switched off" in body and "can access LMS without signing in" in body,
                      "Auth-off confirmation must explain who can access LMS and offer MFA afterwards.")
                check(request("/")[0] == 200, "Open LMS must work immediately after the network choice.")
                cookies.clear()
                check(request("/")[0] == 200, "Saved auth-off choice must work without the installer cookie.")
                check(request("/setup")[0] == 200, "A trusted local user must be able to return and set up MFA without reinstalling.")
                with sqlite3.connect(workspace / "lms.db") as database:
                    check(database.execute("SELECT COUNT(*) FROM security_users").fetchone()[0] == 0, "Auth-off must not invent an account.")
                    database.execute("UPDATE trusted_network_entries SET IsAuthenticationEnabled=1")
                for cookie in saved_cookies:
                    cookies.set_cookie(cookie)
                print("PASS setup: no empty login, scoped/resumable setup, explicit auth-off and return to MFA", flush=True)
                status, body, _ = request("/setup")
                check(status == 200 and "Register LMS login" in body,
                      "After entering the code, a fresh install must render account registration.")
                status, _, headers = setup_post("/auth/initial-setup/start",
                    {"email": "first@example.test", "linuxUsername": "cecheck", "returnUrl": "/"})
                check(status == 302 and "error=" not in headers.get("Location", ""),
                      "Fresh account registration must succeed.")
                status, body, _ = request("/setup")
                secret_match = re.search(r"<code\b[^>]*>([A-Z2-7 ]+)</code>", body)
                check(status == 200 and secret_match is not None, "Pending setup must render the MFA key.")
                secret = secret_match.group(1).replace(" ", "")
                invalid_status, _, invalid_headers = setup_post("/auth/initial-setup/verify",
                    {"userId": re.search(r'name="userId"[^>]*value="([^"]+)"', body).group(1), "otpCode": "invalid", "returnUrl": "/"})
                check(invalid_status == 302 and "error=" in invalid_headers.get("Location", ""), "Invalid MFA must not complete setup.")
                with sqlite3.connect(workspace / "lms.db") as database:
                    check(database.execute("SELECT IsEnabled FROM security_users WHERE Email='first@example.test'").fetchone()[0] == 0,
                          "An unverified MFA account must stay disabled.")
                # Model successful setup MFA without changing the test machine's Linux
                # accounts, SSH configuration or network rules. LastLogin remains NULL.
                with sqlite3.connect(workspace / "lms.db") as database:
                    database.execute("UPDATE security_users SET IsEnabled=1 WHERE Email='first@example.test'")
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
                      headers.get("Location", "").startswith(("/auth/setup-passkey?", "/LMSMFAAuth/setup-passkey?"))),
                      f"The first normal MFA login must succeed and leave setup: {status}, {headers.get('Location')}")
                with sqlite3.connect(workspace / "lms.db") as database:
                    check(database.execute("SELECT LastLoginAtUtc FROM security_users "
                          "WHERE Email='first@example.test'").fetchone()[0] is not None,
                          "The first real login must be recorded.")
                check(request("/setup")[0] == 200 and not challenge.exists(),
                      "Successful first login must close the temporary setup challenge while allowing authenticated security setup.")
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
                         UpdatedAtUtc, LastLoginAtUtc, DomainOtpSecretReferencesJson)
                        VALUES (?, ?, ?, ?, 1, 720, 0, '', 0, 'test-only', ?, ?, ?, '{}')""",
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
