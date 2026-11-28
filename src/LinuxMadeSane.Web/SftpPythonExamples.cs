// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Web;

internal static class SftpPythonExamples
{
    internal const string Connect = """
        from getpass import getpass
        from pathlib import Path
        import requests

        client = requests.Session()
        client.headers["Authorization"] = "Bearer " + getpass("LMS application token: ")

        def call(method, url, **options):
            response = client.request(method, url, timeout=60, allow_redirects=False, **options)
            response.raise_for_status()
            if not 200 <= response.status_code < 300:
                raise RuntimeError(f"Unexpected HTTP status {response.status_code}")
            return response

        print(call("GET", "https://sftp.example.net:5443/api/sftpserver/").json())
        status = call("GET", "https://sftp.example.net:5443/api/sftpserver/server/status").json()
        if not all(status[key] for key in ("managed", "sshActive", "configurationValid")):
            raise RuntimeError("Check managed SFTP setup and the SSH service in LMS.")
        """;

    internal const string Provision = """
        def owned():
            return call("GET", "https://sftp.example.net:5443/api/sftpserver/users/import_user/exists").json()["exists"]

        if not owned():
            mode = "PublicKeyOnly"  # Or PasswordOnly or PasswordAndPublicKey
            body = {"username": "import_user", "authenticationMode": mode}
            if mode in ("PasswordOnly", "PasswordAndPublicKey"):
                body["password"] = getpass("New customer SFTP password: ")
            if mode in ("PublicKeyOnly", "PasswordAndPublicKey"):
                body["publicKey"] = Path(input("Customer public key file: ")).read_text().strip()
            try:
                call("POST", "https://sftp.example.net:5443/api/sftpserver/users", json=body)
            except (requests.Timeout, requests.ConnectionError):
                if not owned():
                    raise  # Check again before a later retry.
            except requests.HTTPError as error:
                if error.response.status_code != 409 or not owned():
                    raise  # Inspect the conflict before choosing another name.

        account = call("GET", "https://sftp.example.net:5443/api/sftpserver/users/import_user").json()
        print(account["username"], account["authenticationMode"], account["enabled"])
        """;

    internal const string RegisterKey = """
        key = Path("customer.pub").read_text().strip()
        call("PUT", "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key",
             json={"publicKey": key})
        print(call("GET", "https://sftp.example.net:5443/api/sftpserver/users/import_user").json())
        """;

    internal const string ChangeMode = """
        target = "PasswordAndPublicKey"  # Or PublicKeyOnly or PasswordOnly
        account = call("GET", "https://sftp.example.net:5443/api/sftpserver/users/import_user").json()
        if account["authenticationMode"] != target:
            settings = {"authenticationMode": target}
            if target in ("PublicKeyOnly", "PasswordAndPublicKey") and not account["hasPublicKey"]:
                key = Path(input("Customer public key file: ")).read_text().strip()
                call("PUT", "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key",
                     json={"publicKey": key})
            if account["authenticationMode"] == "PublicKeyOnly" and target != "PublicKeyOnly":
                settings["password"] = getpass("New customer SFTP password: ")
            call("PUT", "https://sftp.example.net:5443/api/sftpserver/users/import_user/auth", json=settings)

        account = call("GET", "https://sftp.example.net:5443/api/sftpserver/users/import_user").json()
        if account["authenticationMode"] != target or not account["enabled"]:
            raise RuntimeError("Review the account in LMS before sending login instructions.")
        guide = call("GET", "https://sftp.example.net:5443/api/sftpserver/users/import_user/auth-guide").json()
        print(guide["command"], guide["uploadDirectory"])
        """;

    internal const string Import = """
        delete_after_download = False
        entries = call("GET", "https://sftp.example.net:5443/api/sftpserver/users/import_user/files").json()
        if any(e["name"] == "orders-1042.csv" and not e["isDirectory"] for e in entries):
            data = call("GET", "https://sftp.example.net:5443/api/sftpserver/users/import_user/files/content?path=orders-1042.csv").content
            local = Path("orders-1042.csv")
            if not local.exists():
                with local.open("xb") as output:
                    output.write(data)
            if local.read_bytes() != data:
                raise RuntimeError("Local copy differs. Source retained.")
            if delete_after_download:
                call("DELETE", "https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=orders-1042.csv")
        """;

    internal const string Offboard = """
        action = input("Action for import_user (disable, enable or DELETE import_user): ")
        if action == "disable":
            call("PUT", "https://sftp.example.net:5443/api/sftpserver/users/import_user/disable")
        elif action == "enable":
            call("PUT", "https://sftp.example.net:5443/api/sftpserver/users/import_user/enable")
        elif action == "DELETE import_user":
            call("DELETE", "https://sftp.example.net:5443/api/sftpserver/users/import_user")
        else:
            print("No change made.")
        """;
}
