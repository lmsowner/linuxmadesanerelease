# Worked SFTP integrations

[Server setup and integration guide](README.md) · [Endpoint reference](endpoints.md)

Choose a language and start with its connection example. Run the workflow you need against a test account. Replace the example HTTPS origin throughout. The file example verifies a local download and leaves cleanup off. In an import service, place the delete call after your application has durably committed the import. An existing local copy alone cannot establish that your application processed it.

## C#

Use a .NET console project with implicit usings enabled. Place the connection code in Program.cs, followed by the workflow you want to run. The examples read token.txt and, when needed, customer-password.txt from a private working directory. In an application, obtain those secrets from its secret store.

<details><summary>Connect and check readiness</summary>

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
client.Timeout = TimeSpan.FromSeconds(60);
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
    "Bearer", File.ReadAllText("token.txt").TrimEnd('\r', '\n'));

async Task<byte[]> Call(HttpMethod method, string url, JsonObject? body = null)
{
    using var request = new HttpRequestMessage(method, url);
    if (body != null) request.Content = JsonContent.Create(body);
    using var response = await client.SendAsync(request);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsByteArrayAsync();
}
async Task<JsonNode> Get(string url) =>
    JsonNode.Parse(await Call(HttpMethod.Get, url)) ?? throw new InvalidDataException("Expected JSON");

Console.WriteLine(await Get("https://sftp.example.net:5443/api/sftpserver/"));
var status = await Get("https://sftp.example.net:5443/api/sftpserver/server/status");
if (!(bool)status["managed"]! || !(bool)status["sshActive"]! || !(bool)status["configurationValid"]!)
    throw new InvalidOperationException("Check managed SFTP setup and SSH in LMS.");
```

</details>

<details><summary>Check ownership and provision</summary>

```csharp
async Task<bool> Owned() =>
    (bool)(await Get("https://sftp.example.net:5443/api/sftpserver/users/import_user/exists"))["exists"]!;
if (!await Owned())
{
    var mode = "PublicKeyOnly"; // Or PasswordOnly or PasswordAndPublicKey
    var body = new JsonObject { ["username"] = "import_user", ["authenticationMode"] = mode };
    if (mode != "PublicKeyOnly") body["password"] = File.ReadAllText("customer-password.txt").TrimEnd('\r', '\n');
    if (mode != "PasswordOnly") body["publicKey"] = File.ReadAllText("customer.pub").Trim();
    try { await Call(HttpMethod.Post, "https://sftp.example.net:5443/api/sftpserver/users", body); }
    catch (HttpRequestException e) when (e.StatusCode is null or HttpStatusCode.Conflict)
    {
        if (!await Owned()) throw;
    }
    catch (TaskCanceledException) { if (!await Owned()) throw; }
}
Console.WriteLine(await Get("https://sftp.example.net:5443/api/sftpserver/users/import_user"));
```

</details>

<details><summary>Register or rotate the public key</summary>

```csharp
var publicKey = File.ReadAllText("customer.pub").Trim();
await Call(HttpMethod.Put, "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key",
    new JsonObject { ["publicKey"] = publicKey });
Console.WriteLine(await Get("https://sftp.example.net:5443/api/sftpserver/users/import_user"));
```

</details>

<details><summary>Change the authentication mode</summary>

```csharp
var target = "PasswordAndPublicKey"; // Or PublicKeyOnly or PasswordOnly
var account = await Get("https://sftp.example.net:5443/api/sftpserver/users/import_user");
var current = (string)account["authenticationMode"]!;
if (current != target)
{
    if (target != "PasswordOnly" && !(bool)account["hasPublicKey"]!)
        await Call(HttpMethod.Put, "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key",
            new JsonObject { ["publicKey"] = File.ReadAllText("customer.pub").Trim() });
    var settings = new JsonObject { ["authenticationMode"] = target };
    if (current == "PublicKeyOnly")
        settings["password"] = File.ReadAllText("customer-password.txt").TrimEnd('\r', '\n');
    await Call(HttpMethod.Put, "https://sftp.example.net:5443/api/sftpserver/users/import_user/auth", settings);
}
account = await Get("https://sftp.example.net:5443/api/sftpserver/users/import_user");
if ((string)account["authenticationMode"]! != target || !(bool)account["enabled"]!)
    throw new InvalidOperationException("Review the account before sending login instructions.");
Console.WriteLine(await Get("https://sftp.example.net:5443/api/sftpserver/users/import_user/auth-guide"));
```

</details>

<details><summary>Collect a file and optionally remove the source</summary>

```csharp
bool deleteAfterDownload = false;
var entries = (await Get("https://sftp.example.net:5443/api/sftpserver/users/import_user/files")).AsArray();
if (entries.Any(e => (string?)e!["name"] == "orders-1042.csv" && !(bool)e["isDirectory"]!))
{
    var bytes = await Call(HttpMethod.Get, "https://sftp.example.net:5443/api/sftpserver/users/import_user/files/content?path=orders-1042.csv");
    if (!File.Exists("orders-1042.csv"))
    {
        using var output = new FileStream("orders-1042.csv", FileMode.CreateNew);
        output.Write(bytes);
    }
    if (!bytes.SequenceEqual(File.ReadAllBytes("orders-1042.csv")))
        throw new IOException("Local copy differs. Source retained.");
    if (deleteAfterDownload)
        await Call(HttpMethod.Delete, "https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=orders-1042.csv");
}
```

</details>

<details><summary>Suspend, restore or retire an account</summary>

```csharp
Console.Write("Action for import_user (disable, enable or DELETE import_user): ");
switch (Console.ReadLine())
{
    case "disable": await Call(HttpMethod.Put, "https://sftp.example.net:5443/api/sftpserver/users/import_user/disable"); break;
    case "enable": await Call(HttpMethod.Put, "https://sftp.example.net:5443/api/sftpserver/users/import_user/enable"); break;
    case "DELETE import_user": await Call(HttpMethod.Delete, "https://sftp.example.net:5443/api/sftpserver/users/import_user"); break;
    default: Console.WriteLine("No change made."); break;
}
```

</details>

## Java

Use Java 17 or later and Gson (com.google.code.gson:gson). Save SftpClient.java and the selected workflow class in the same project and package. Run SftpClient to check the connection, then run the workflow’s main method. Supply token.txt and, when needed, customer-password.txt in a private working directory or read them from your application’s secret store.

<details><summary>Connect and check readiness</summary>

```java
import com.google.gson.*;
import java.io.*;
import java.net.*;
import java.net.http.*;
import java.nio.file.*;
import java.time.Duration;

class SftpClient {
    static final HttpClient client = HttpClient.newHttpClient();
    static String text(String file) throws IOException { return Files.readString(Path.of(file)).strip(); }
    static class ApiError extends IOException {
        final int status;
        ApiError(int status) { super("HTTP " + status); this.status = status; }
    }
    static byte[] call(String method, String url, JsonObject body) throws Exception {
        var builder = HttpRequest.newBuilder(URI.create(url)).timeout(Duration.ofSeconds(60))
            .header("Authorization", "Bearer " + text("token.txt"));
        var data = HttpRequest.BodyPublishers.noBody();
        if (body != null) {
            builder.header("Content-Type", "application/json");
            data = HttpRequest.BodyPublishers.ofString(body.toString());
        }
        var response = client.send(builder.method(method, data).build(), HttpResponse.BodyHandlers.ofByteArray());
        if (response.statusCode() / 100 != 2) throw new ApiError(response.statusCode());
        return response.body();
    }
    static JsonElement get(String url) throws Exception {
        return JsonParser.parseString(new String(call("GET", url, null), java.nio.charset.StandardCharsets.UTF_8));
    }
    public static void main(String[] args) throws Exception {
        System.out.println(get("https://sftp.example.net:5443/api/sftpserver/"));
        var status = get("https://sftp.example.net:5443/api/sftpserver/server/status").getAsJsonObject();
        for (String key : new String[]{"managed", "sshActive", "configurationValid"})
            if (!status.get(key).getAsBoolean()) throw new IOException("Check SFTP readiness: " + key);
    }
}
```

</details>

<details><summary>Check ownership and provision</summary>

```java
import java.io.IOException;
import com.google.gson.JsonObject;

class Provision extends SftpClient {
    static boolean owned() throws Exception {
        return get("https://sftp.example.net:5443/api/sftpserver/users/import_user/exists")
            .getAsJsonObject().get("exists").getAsBoolean();
    }
    public static void main(String[] args) throws Exception {
        if (!owned()) {
            String mode = "PublicKeyOnly"; // Or PasswordOnly or PasswordAndPublicKey
            var body = new JsonObject();
            body.addProperty("username", "import_user");
            body.addProperty("authenticationMode", mode);
            if (!mode.equals("PublicKeyOnly")) body.addProperty("password", text("customer-password.txt"));
            if (!mode.equals("PasswordOnly")) body.addProperty("publicKey", text("customer.pub"));
            try { call("POST", "https://sftp.example.net:5443/api/sftpserver/users", body); }
            catch (ApiError e) { if (e.status != 409 || !owned()) throw e; }
            catch (IOException e) { if (!owned()) throw e; }
        }
        System.out.println(get("https://sftp.example.net:5443/api/sftpserver/users/import_user"));
    }
}
```

</details>

<details><summary>Register or rotate the public key</summary>

```java
import com.google.gson.JsonObject;

class RegisterKey extends SftpClient {
    public static void main(String[] args) throws Exception {
        var body = new JsonObject();
        body.addProperty("publicKey", text("customer.pub"));
        call("PUT", "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key", body);
        System.out.println(get("https://sftp.example.net:5443/api/sftpserver/users/import_user"));
    }
}
```

</details>

<details><summary>Change the authentication mode</summary>

```java
import com.google.gson.JsonObject;

class ChangeMode extends SftpClient {
    public static void main(String[] args) throws Exception {
        String target = "PasswordAndPublicKey"; // Or PublicKeyOnly or PasswordOnly
        var account = get("https://sftp.example.net:5443/api/sftpserver/users/import_user").getAsJsonObject();
        String current = account.get("authenticationMode").getAsString();
        if (!current.equals(target)) {
            if (!target.equals("PasswordOnly") && !account.get("hasPublicKey").getAsBoolean()) {
                var key = new JsonObject();
                key.addProperty("publicKey", text("customer.pub"));
                call("PUT", "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key", key);
            }
            var settings = new JsonObject();
            settings.addProperty("authenticationMode", target);
            if (current.equals("PublicKeyOnly")) settings.addProperty("password", text("customer-password.txt"));
            call("PUT", "https://sftp.example.net:5443/api/sftpserver/users/import_user/auth", settings);
        }
        account = get("https://sftp.example.net:5443/api/sftpserver/users/import_user").getAsJsonObject();
        if (!target.equals(account.get("authenticationMode").getAsString()) || !account.get("enabled").getAsBoolean())
            throw new IllegalStateException("Review the account before sending login instructions.");
        System.out.println(get("https://sftp.example.net:5443/api/sftpserver/users/import_user/auth-guide"));
    }
}
```

</details>

<details><summary>Collect a file and optionally remove the source</summary>

```java
import java.nio.file.*;
import java.util.Arrays;

class Collect extends SftpClient {
    public static void main(String[] args) throws Exception {
        boolean deleteAfterDownload = false;
        var entries = get("https://sftp.example.net:5443/api/sftpserver/users/import_user/files").getAsJsonArray();
        for (var item : entries) {
            var file = item.getAsJsonObject();
            if (file.get("isDirectory").getAsBoolean() || !file.get("name").getAsString().equals("orders-1042.csv")) continue;
            var bytes = call("GET", "https://sftp.example.net:5443/api/sftpserver/users/import_user/files/content?path=orders-1042.csv", null);
            var local = Path.of("orders-1042.csv");
            if (!Files.exists(local)) Files.write(local, bytes, StandardOpenOption.CREATE_NEW);
            if (!Arrays.equals(bytes, Files.readAllBytes(local))) throw new java.io.IOException("Local copy differs. Source retained.");
            if (deleteAfterDownload)
                call("DELETE", "https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=orders-1042.csv", null);
        }
    }
}
```

</details>

<details><summary>Suspend, restore or retire an account</summary>

```java
import java.util.Scanner;

class Offboard extends SftpClient {
    public static void main(String[] args) throws Exception {
        System.out.print("Action for import_user (disable, enable or DELETE import_user): ");
        switch (new Scanner(System.in).nextLine()) {
            case "disable" -> call("PUT", "https://sftp.example.net:5443/api/sftpserver/users/import_user/disable", null);
            case "enable" -> call("PUT", "https://sftp.example.net:5443/api/sftpserver/users/import_user/enable", null);
            case "DELETE import_user" -> call("DELETE", "https://sftp.example.net:5443/api/sftpserver/users/import_user", null);
            default -> System.out.println("No change made.");
        }
    }
}
```

</details>

## curl

Use Bash, curl 7.82 or later, and jq. Put the connection block first in a script, followed by the workflow to run. Start it with Bash. The token prompt hides your input.

<details><summary>Connect and check readiness</summary>

```bash
set -euo pipefail
umask 077
read -rsp 'LMS application token: ' token; echo
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
printf 'Authorization: Bearer %s\n' "$token" > "$work/header"
unset token
api() {
    curl --silent --show-error --fail --max-time 60 -H "@$work/header" "$@"
}
api "https://sftp.example.net:5443/api/sftpserver/" | jq .
api "https://sftp.example.net:5443/api/sftpserver/server/status" |
    jq -e '.managed and .sshActive and .configurationValid' >/dev/null
```

</details>

<details><summary>Check ownership and provision</summary>

```bash
owned() {
    api "https://sftp.example.net:5443/api/sftpserver/users/import_user/exists" |
        jq -r '.exists | if type == "boolean" then . else error("Invalid response") end'
}
exists=$(owned)
if [[ $exists == false ]]; then
    mode=PublicKeyOnly  # PasswordOnly, PublicKeyOnly or PasswordAndPublicKey
    jq -n --arg mode "$mode" '{username:"import_user", authenticationMode:$mode}' > "$work/body"
    if [[ $mode != PublicKeyOnly ]]; then
        read -rsp 'New customer password: ' password; echo
        printf %s "$password" | jq -Rs --slurpfile b "$work/body" '$b[0] + {password:.}' > "$work/new"
        unset password
        mv "$work/new" "$work/body"
    fi
    if [[ $mode != PasswordOnly ]]; then
        jq --rawfile key customer.pub '. + {publicKey:($key | rtrimstr("\n") | rtrimstr("\r"))}' "$work/body" > "$work/new"
        mv "$work/new" "$work/body"
    fi
    rc=0
    status=$(curl --silent --show-error --max-time 60 -H "@$work/header" --json "@$work/body" \
        -o "$work/result" -w '%{http_code}' "https://sftp.example.net:5443/api/sftpserver/users") || rc=$?
    rm "$work/body"
    if [[ $rc != 0 || $status == 409 ]]; then
        [[ $(owned) == true ]] || { echo 'Account creation unconfirmed. Check before retrying.' >&2; exit 1; }
    elif [[ $status != 201 ]]; then
        echo "Create returned HTTP $status" >&2; exit 1
    fi
fi
api "https://sftp.example.net:5443/api/sftpserver/users/import_user" | jq .
```

</details>

<details><summary>Register or rotate the public key</summary>

```bash
jq -n --rawfile key customer.pub '{publicKey:($key | rtrimstr("\n") | rtrimstr("\r"))}' |
    api -X PUT --json @- "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key"
api "https://sftp.example.net:5443/api/sftpserver/users/import_user" | jq '{authenticationMode,hasPublicKey,enabled}'
```

</details>

<details><summary>Change the authentication mode</summary>

```bash
target=PasswordAndPublicKey  # Or PasswordOnly or PublicKeyOnly
account=$(api "https://sftp.example.net:5443/api/sftpserver/users/import_user")
current=$(jq -r .authenticationMode <<< "$account")
if [[ $current != "$target" ]]; then
    if [[ $target != PasswordOnly ]] && jq -e '.hasPublicKey == false' <<< "$account" >/dev/null; then
        jq -n --rawfile key customer.pub '{publicKey:($key | rtrimstr("\n") | rtrimstr("\r"))}' |
            api -X PUT --json @- "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key"
    fi
    if [[ $current == PublicKeyOnly ]]; then
        read -rsp 'New customer password: ' password; echo
        printf %s "$password" | jq -Rs --arg mode "$target" '{authenticationMode:$mode,password:.}' > "$work/auth"
        unset password
    else
        jq -n --arg mode "$target" '{authenticationMode:$mode}' > "$work/auth"
    fi
    api -X PUT --json "@$work/auth" "https://sftp.example.net:5443/api/sftpserver/users/import_user/auth"
    rm "$work/auth"
fi
api "https://sftp.example.net:5443/api/sftpserver/users/import_user" |
    jq -e --arg mode "$target" '.enabled and .authenticationMode == $mode' >/dev/null
api "https://sftp.example.net:5443/api/sftpserver/users/import_user/auth-guide" | jq .
```

</details>

<details><summary>Collect a file and optionally remove the source</summary>

```bash
delete_after_download=false
listing=$(api "https://sftp.example.net:5443/api/sftpserver/users/import_user/files")
if jq -e 'any(.[]; .name == "orders-1042.csv" and .isDirectory == false)' <<< "$listing" >/dev/null; then
    api "https://sftp.example.net:5443/api/sftpserver/users/import_user/files/content?path=orders-1042.csv" -o "$work/content"
    if [[ ! -e orders-1042.csv ]]; then
        (set -o noclobber; cat "$work/content" > orders-1042.csv)
    fi
    cmp "$work/content" orders-1042.csv
    if [[ $delete_after_download == true ]]; then
        api -X DELETE "https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=orders-1042.csv"
    fi
fi
```

</details>

<details><summary>Suspend, restore or retire an account</summary>

```bash
read -rp 'Action for import_user (disable, enable or DELETE import_user): ' action
case "$action" in
    disable) api -X PUT "https://sftp.example.net:5443/api/sftpserver/users/import_user/disable" ;;
    enable) api -X PUT "https://sftp.example.net:5443/api/sftpserver/users/import_user/enable" ;;
    'DELETE import_user') api -X DELETE "https://sftp.example.net:5443/api/sftpserver/users/import_user" ;;
    *) echo 'No change made.' ;;
esac
```

</details>

## Python

Use Python 3 with the requests package. Put the connection code first in your script, followed by the workflow to run. The token prompt hides your input.

<details><summary>Connect and check readiness</summary>

```python
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
```

</details>

<details><summary>Check ownership and provision</summary>

```python
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
```

</details>

<details><summary>Register or rotate the public key</summary>

```python
key = Path("customer.pub").read_text().strip()
call("PUT", "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key",
     json={"publicKey": key})
print(call("GET", "https://sftp.example.net:5443/api/sftpserver/users/import_user").json())
```

</details>

<details><summary>Change the authentication mode</summary>

```python
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
```

</details>

<details><summary>Collect a file and optionally remove the source</summary>

```python
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
```

</details>

<details><summary>Suspend, restore or retire an account</summary>

```python
action = input("Action for import_user (disable, enable or DELETE import_user): ")
if action == "disable":
    call("PUT", "https://sftp.example.net:5443/api/sftpserver/users/import_user/disable")
elif action == "enable":
    call("PUT", "https://sftp.example.net:5443/api/sftpserver/users/import_user/enable")
elif action == "DELETE import_user":
    call("DELETE", "https://sftp.example.net:5443/api/sftpserver/users/import_user")
else:
    print("No change made.")
```

</details>
