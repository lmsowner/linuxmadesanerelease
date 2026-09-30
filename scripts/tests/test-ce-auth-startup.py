#!/usr/bin/env python3
# Copyright (c) Richard D. Kiernan.
# Licensed under the Business Source License 1.1. See LICENSE for details.
"""Exercise the exported CE authentication/recovery routes in an isolated instance.

Run after building CE in Release. Never use an installed LMS database or recovery code.
"""
import hashlib
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
        challenge.write_text(json.dumps({
            "purpose": "linux-made-sane-temporary-setup", "version": 1,
            "challengeId": "ce-regression", "salt": "ce-test-salt",
            "codeHash": hashlib.sha256(b"ce-test-salt:A1B2C3D4").hexdigest(),
            "attempts": 0, "createdAtUtc": now.isoformat(),
            "expiresAtUtc": (now + timedelta(minutes=10)).isoformat(),
        }))
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
        client = urllib.request.build_opener(NoRedirect)

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
                check(status == 302 and headers.get("Location", "").startswith("/setup?")
                      and "lms.temporary-setup=" in headers["Set-Cookie"],
                      "A valid setup code must authorize recovery and return to setup.")
                print("PASS setup code: recovery authorized", flush=True)

                challenge.unlink()
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
