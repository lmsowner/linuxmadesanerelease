# SFTP API endpoint reference

[Server setup and integration guide](README.md) · [Worked examples](workflows.md)

Every request uses HTTPS and the application bearer token. Replace `sftp.example.net:5443` with the listener for your credential. Replace `YOUR_API_TOKEN` with a token from your secret store. Username and path examples use a fictional customer account. The curl commands require curl 7.82 or later. On Windows PowerShell use `curl.exe`. Python examples use Requests, C# examples use .NET, and Java examples use Java 17 or later.

| Method | Endpoint | Operation |
|---|---|---|
| GET | `/api/sftpserver/` | [Test the API connection](#test-the-api-connection) |
| GET | `/api/sftpserver/server/status` | [Check SFTP readiness](#check-sftp-readiness) |
| POST | `/api/sftpserver/users` | [Provision an upload account](#provision-an-upload-account) |
| GET | `/api/sftpserver/users` | [List your application’s accounts](#list-your-applications-accounts) |
| GET | `/api/sftpserver/users/{username}` | [Inspect one account](#inspect-one-account) |
| GET | `/api/sftpserver/users/{username}/exists` | [Check account ownership](#check-account-ownership) |
| PUT | `/api/sftpserver/users/{username}/public-key` | [Register or rotate an SSH key](#register-or-rotate-an-ssh-key) |
| PUT | `/api/sftpserver/users/{username}/auth` | [Change login requirements](#change-login-requirements) |
| DELETE | `/api/sftpserver/users/{username}/public-key` | [Remove registered SSH keys](#remove-registered-ssh-keys) |
| GET | `/api/sftpserver/users/{username}/public-key-example` | [Show a customer how to generate a key](#show-a-customer-how-to-generate-a-key) |
| GET | `/api/sftpserver/users/{username}/auth-guide` | [Get connection instructions](#get-connection-instructions) |
| PUT | `/api/sftpserver/users/{username}/password` | [Reset an SFTP password](#reset-an-sftp-password) |
| PUT | `/api/sftpserver/users/{username}/disable` | [Pause customer logins](#pause-customer-logins) |
| PUT | `/api/sftpserver/users/{username}/enable` | [Resume customer logins](#resume-customer-logins) |
| GET | `/api/sftpserver/users/{username}/files` | [Find files waiting for import](#find-files-waiting-for-import) |
| GET | `/api/sftpserver/users/{username}/files/content` | [Download a file for import](#download-a-file-for-import) |
| POST | `/api/sftpserver/users/{username}/files` | [Upload a new file](#upload-a-new-file) |
| PUT | `/api/sftpserver/users/{username}/files/content` | [Replace an existing file](#replace-an-existing-file) |
| DELETE | `/api/sftpserver/users/{username}/files` | [Delete an imported file](#delete-an-imported-file) |
| DELETE | `/api/sftpserver/users/{username}` | [Permanently remove an account](#permanently-remove-an-account) |

## Test the API connection

**GET** `/api/sftpserver/`

Call this when connecting an application for the first time or after rotating its token. A successful response confirms that the HTTPS request reached the SFTP API and passed its token and network checks. It returns the API version.

This call has no parameters or request body.

**Response** · `200 {"service":"LMS SFTP API","version":1}`

<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X GET -H "Authorization: Bearer YOUR_API_TOKEN" "https://sftp.example.net:5443/api/sftpserver/"
```

</details>

<details><summary>Python example</summary>

```python
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
response = requests.get(url, headers=headers, timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/";
using var request = new HttpRequestMessage(new HttpMethod("GET"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");

using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        var body = BodyPublishers.noBody();
        var request = builder.method("GET", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>

## Check SFTP readiness

**GET** `/api/sftpserver/server/status`

Check this before onboarding a customer, or when the API responds but customers cannot connect over SFTP. The three flags tell you whether LMS manages SFTP, the SSH service is active and the host configuration passes validation.

This call has no parameters or request body.

**Response** · `200 {"managed":true,"sshActive":true,"configurationValid":true}`

<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X GET -H "Authorization: Bearer YOUR_API_TOKEN" "https://sftp.example.net:5443/api/sftpserver/server/status"
```

</details>

<details><summary>Python example</summary>

```python
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/server/status'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
response = requests.get(url, headers=headers, timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/server/status";
using var request = new HttpRequestMessage(new HttpMethod("GET"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");

using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/server/status";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        var body = BodyPublishers.noBody();
        var request = builder.method("GET", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>

## Provision an upload account

**POST** `/api/sftpserver/users`

Call this when a customer needs their own SFTP login and upload folder. Supply a unique username, an authentication mode and the password, public key or both required by that mode. The Location header identifies the new account. If the request times out, check whether your credential owns the account before retrying.

| Parameter | Where | Type | Required | Accepted values and behavior |
|---|---|---|---|---|
| `username` | JSON | string | Required | 1–32 characters. Start with a lowercase ASCII letter or underscore. Continue with lowercase letters, digits, underscores or hyphens. Choose an unused account name. |
| `authenticationMode` | JSON | string enum | Required | PasswordOnly, PublicKeyOnly or PasswordAndPublicKey. Names are case-insensitive. Numeric values are rejected. |
| `password` | JSON | string | Conditional | Required for PasswordOnly and PasswordAndPublicKey. Omit for PublicKeyOnly. 1–1,024 characters. No control characters or leading or trailing whitespace. |
| `publicKey` | JSON | string | Conditional | Required for PublicKeyOnly and PasswordAndPublicKey. Optional for PasswordOnly. One OpenSSH public key, up to 16,384 characters on one line. Supply the key type, base64 key and optional comment. Supported types are ssh-ed25519, ssh-rsa, ecdsa-sha2-nistp256, ecdsa-sha2-nistp384, ecdsa-sha2-nistp521, sk-ssh-ed25519@openssh.com and sk-ecdsa-sha2-nistp256@openssh.com. |

**Response** · `201 {"username":"import_user"} plus Location`

Save the following JSON as `request.json` and replace the example values before calling this endpoint. Store any password-bearing file privately.
```json
{"username":"import_user","authenticationMode":"PublicKeyOnly","publicKey":"<your OpenSSH public key>"}
```


<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X POST -H "Authorization: Bearer YOUR_API_TOKEN" --json "@request.json" "https://sftp.example.net:5443/api/sftpserver/users"
```

</details>

<details><summary>Python example</summary>

```python
import json
from pathlib import Path
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/users'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
response = requests.post(url, headers=headers, json=json.loads(Path('request.json').read_text()), timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/users";
using var request = new HttpRequestMessage(new HttpMethod("POST"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");
request.Content = new StringContent(await File.ReadAllTextAsync("request.json"), System.Text.Encoding.UTF8, "application/json");
using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/users";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        builder.header("Content-Type", "application/json");
        var body = BodyPublishers.ofFile(Path.of("request.json"));
        var request = builder.method("POST", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>

## List your application’s accounts

**GET** `/api/sftpserver/users`

Use this to populate an account picker or reconcile the accounts your application provisioned. Each entry includes the username, login mode, key presence and enabled state. An empty list means this credential owns no managed accounts.

This call has no parameters or request body.

**Response** · `200 [{"username":"import_user","authenticationMode":"PublicKeyOnly","hasPublicKey":true,"enabled":true}]`

<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X GET -H "Authorization: Bearer YOUR_API_TOKEN" "https://sftp.example.net:5443/api/sftpserver/users"
```

</details>

<details><summary>Python example</summary>

```python
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/users'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
response = requests.get(url, headers=headers, timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/users";
using var request = new HttpRequestMessage(new HttpMethod("GET"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");

using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/users";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        var body = BodyPublishers.noBody();
        var request = builder.method("GET", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>

## Inspect one account

**GET** `/api/sftpserver/users/{username}`

Look up a customer before changing their login settings or troubleshooting access. Check enabled to see whether login is allowed and hasPublicKey to see whether a key is registered. A 404 means the account is unavailable to this credential.

| Parameter | Where | Type | Required | Accepted values and behavior |
|---|---|---|---|---|
| `username` | URL path | string | Required | 1–32 characters. Start with a lowercase ASCII letter or underscore. Continue with lowercase letters, digits, underscores or hyphens. |

**Response** · `200 {"username":"import_user","authenticationMode":"PublicKeyOnly","hasPublicKey":true,"enabled":true}`

<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X GET -H "Authorization: Bearer YOUR_API_TOKEN" "https://sftp.example.net:5443/api/sftpserver/users/import_user"
```

</details>

<details><summary>Python example</summary>

```python
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/users/import_user'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
response = requests.get(url, headers=headers, timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user";
using var request = new HttpRequestMessage(new HttpMethod("GET"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");

using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        var body = BodyPublishers.noBody();
        var request = builder.method("GET", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>

## Check account ownership

**GET** `/api/sftpserver/users/{username}/exists`

Use this after an interrupted provisioning request to see whether your application already owns the account. A false result can also mean another owner has the name, so account creation can still return a conflict.

| Parameter | Where | Type | Required | Accepted values and behavior |
|---|---|---|---|---|
| `username` | URL path | string | Required | 1–32 characters. Start with a lowercase ASCII letter or underscore. Continue with lowercase letters, digits, underscores or hyphens. |

**Response** · `200 {"exists":true}`

<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X GET -H "Authorization: Bearer YOUR_API_TOKEN" "https://sftp.example.net:5443/api/sftpserver/users/import_user/exists"
```

</details>

<details><summary>Python example</summary>

```python
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/users/import_user/exists'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
response = requests.get(url, headers=headers, timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/exists";
using var request = new HttpRequestMessage(new HttpMethod("GET"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");

using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/exists";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        var body = BodyPublishers.noBody();
        var request = builder.method("GET", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>

## Register or rotate an SSH key

**PUT** `/api/sftpserver/users/{username}/public-key`

Use this when a customer first supplies a key or replaces a device. Send their single-line OpenSSH public key. It replaces all keys registered for this account. For a password-only account, change the authentication mode separately when you are ready to require the key.

| Parameter | Where | Type | Required | Accepted values and behavior |
|---|---|---|---|---|
| `username` | URL path | string | Required | 1–32 characters. Start with a lowercase ASCII letter or underscore. Continue with lowercase letters, digits, underscores or hyphens. |
| `publicKey` | JSON | string | Required | One OpenSSH public key, up to 16,384 characters on one line. Supply the key type, base64 key and optional comment. Supported types are ssh-ed25519, ssh-rsa, ecdsa-sha2-nistp256, ecdsa-sha2-nistp384, ecdsa-sha2-nistp521, sk-ssh-ed25519@openssh.com and sk-ecdsa-sha2-nistp256@openssh.com. |

**Response** · `204`

Save the following JSON as `request.json` and replace the example values before calling this endpoint. Store any password-bearing file privately.
```json
{"publicKey":"<your OpenSSH public key>"}
```


<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X PUT -H "Authorization: Bearer YOUR_API_TOKEN" --json "@request.json" "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key"
```

</details>

<details><summary>Python example</summary>

```python
import json
from pathlib import Path
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
response = requests.put(url, headers=headers, json=json.loads(Path('request.json').read_text()), timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key";
using var request = new HttpRequestMessage(new HttpMethod("PUT"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");
request.Content = new StringContent(await File.ReadAllTextAsync("request.json"), System.Text.Encoding.UTF8, "application/json");
using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        builder.header("Content-Type", "application/json");
        var body = BodyPublishers.ofFile(Path.of("request.json"));
        var request = builder.method("PUT", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>

## Change login requirements

**PUT** `/api/sftpserver/users/{username}/auth`

Use this to move a customer from password login to an SSH key, or to require both factors. Register the public key first for either key-based mode. Switching to PublicKeyOnly disables password login. Supply a new password when moving back from that mode.

| Parameter | Where | Type | Required | Accepted values and behavior |
|---|---|---|---|---|
| `username` | URL path | string | Required | 1–32 characters. Start with a lowercase ASCII letter or underscore. Continue with lowercase letters, digits, underscores or hyphens. |
| `authenticationMode` | JSON | string enum | Required | PasswordOnly, PublicKeyOnly or PasswordAndPublicKey. Names are case-insensitive. Numeric values are rejected. |
| `password` | JSON | string | Conditional | Required when enabling password login from PublicKeyOnly. Omit to keep an existing usable password. 1–1,024 characters. No control characters or leading or trailing whitespace. |

**Response** · `204`

Save the following JSON as `request.json` and replace the example values before calling this endpoint. Store any password-bearing file privately.
```json
{"authenticationMode":"PasswordAndPublicKey","password":"<new password>"}
```


<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X PUT -H "Authorization: Bearer YOUR_API_TOKEN" --json "@request.json" "https://sftp.example.net:5443/api/sftpserver/users/import_user/auth"
```

</details>

<details><summary>Python example</summary>

```python
import json
from pathlib import Path
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/users/import_user/auth'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
response = requests.put(url, headers=headers, json=json.loads(Path('request.json').read_text()), timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/auth";
using var request = new HttpRequestMessage(new HttpMethod("PUT"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");
request.Content = new StringContent(await File.ReadAllTextAsync("request.json"), System.Text.Encoding.UTF8, "application/json");
using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/auth";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        builder.header("Content-Type", "application/json");
        var body = BodyPublishers.ofFile(Path.of("request.json"));
        var request = builder.method("PUT", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>

## Remove registered SSH keys

**DELETE** `/api/sftpserver/users/{username}/public-key`

Use this to retire a customer’s keys while keeping password access. Switch the account to PasswordOnly first. LMS returns 409 if the current mode still requires a key.

| Parameter | Where | Type | Required | Accepted values and behavior |
|---|---|---|---|---|
| `username` | URL path | string | Required | 1–32 characters. Start with a lowercase ASCII letter or underscore. Continue with lowercase letters, digits, underscores or hyphens. |

**Response** · `204 or 409 if the account requires a key`

<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X DELETE -H "Authorization: Bearer YOUR_API_TOKEN" "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key"
```

</details>

<details><summary>Python example</summary>

```python
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
response = requests.delete(url, headers=headers, timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key";
using var request = new HttpRequestMessage(new HttpMethod("DELETE"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");

using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        var body = BodyPublishers.noBody();
        var request = builder.method("DELETE", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>

## Show a customer how to generate a key

**GET** `/api/sftpserver/users/{username}/public-key-example`

Use the returned key-generation command and sample format in customer onboarding instructions. The customer generates a key pair on their device, keeps the private key there and sends you the public key to register.

| Parameter | Where | Type | Required | Accepted values and behavior |
|---|---|---|---|---|
| `username` | URL path | string | Required | 1–32 characters. Start with a lowercase ASCII letter or underscore. Continue with lowercase letters, digits, underscores or hyphens. |

**Response** · `200 JSON with generate and publicKeyExample`

<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X GET -H "Authorization: Bearer YOUR_API_TOKEN" "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key-example"
```

</details>

<details><summary>Python example</summary>

```python
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key-example'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
response = requests.get(url, headers=headers, timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key-example";
using var request = new HttpRequestMessage(new HttpMethod("GET"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");

using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key-example";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        var body = BodyPublishers.noBody();
        var request = builder.method("GET", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>

## Get connection instructions

**GET** `/api/sftpserver/users/{username}/auth-guide`

Use this in a customer welcome screen or support reply. It returns the account’s current login mode, an SFTP command and its upload directory. Supply host and privateKeyPath to tailor the displayed command to the customer’s SSH hostname and local key location.

| Parameter | Where | Type | Required | Accepted values and behavior |
|---|---|---|---|---|
| `username` | URL path | string | Required | 1–32 characters. Start with a lowercase ASCII letter or underscore. Continue with lowercase letters, digits, underscores or hyphens. |
| `host` | Query | string | Optional | SSH hostname or IP address. Defaults to the API request hostname. DNS names are at most 253 characters. Supply the hostname without a scheme or port. |
| `privateKeyPath` | Query | string | Optional | Path printed in the example command. Defaults to ~/.ssh/id_ed25519. Up to 256 characters matching [~/a-zA-Z0-9_][a-zA-Z0-9_./-]*. The client keeps its private key. |

**Response** · `200 {"authenticationMode":"PublicKeyOnly","command":"sftp -i ~/.ssh/import import_user@sftp.example.net","uploadDirectory":"/files"}`

<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X GET -H "Authorization: Bearer YOUR_API_TOKEN" "https://sftp.example.net:5443/api/sftpserver/users/import_user/auth-guide?host=sftp.example.net&privateKeyPath=~/.ssh/import"
```

</details>

<details><summary>Python example</summary>

```python
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/users/import_user/auth-guide?host=sftp.example.net&privateKeyPath=~/.ssh/import'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
response = requests.get(url, headers=headers, timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/auth-guide?host=sftp.example.net&privateKeyPath=~/.ssh/import";
using var request = new HttpRequestMessage(new HttpMethod("GET"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");

using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/auth-guide?host=sftp.example.net&privateKeyPath=~/.ssh/import";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        var body = BodyPublishers.noBody();
        var request = builder.method("GET", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>

## Reset an SFTP password

**PUT** `/api/sftpserver/users/{username}/password`

Use this when a customer forgets their password or you need to rotate it. Send the new password in the JSON body for an account that allows password authentication. Update the customer’s SFTP client before its next login.

| Parameter | Where | Type | Required | Accepted values and behavior |
|---|---|---|---|---|
| `username` | URL path | string | Required | 1–32 characters. Start with a lowercase ASCII letter or underscore. Continue with lowercase letters, digits, underscores or hyphens. |
| `password` | JSON | string | Required | 1–1,024 characters. No control characters or leading or trailing whitespace. |

**Response** · `204`

Save the following JSON as `request.json` and replace the example values before calling this endpoint. Store any password-bearing file privately.
```json
{"password":"<new password>"}
```


<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X PUT -H "Authorization: Bearer YOUR_API_TOKEN" --json "@request.json" "https://sftp.example.net:5443/api/sftpserver/users/import_user/password"
```

</details>

<details><summary>Python example</summary>

```python
import json
from pathlib import Path
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/users/import_user/password'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
response = requests.put(url, headers=headers, json=json.loads(Path('request.json').read_text()), timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/password";
using var request = new HttpRequestMessage(new HttpMethod("PUT"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");
request.Content = new StringContent(await File.ReadAllTextAsync("request.json"), System.Text.Encoding.UTF8, "application/json");
using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/password";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        builder.header("Content-Type", "application/json");
        var body = BodyPublishers.ofFile(Path.of("request.json"));
        var request = builder.method("PUT", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>

## Pause customer logins

**PUT** `/api/sftpserver/users/{username}/disable`

Use this to suspend new customer logins while keeping their account, credentials and uploaded files. Your application can still collect outstanding files with its API token.

| Parameter | Where | Type | Required | Accepted values and behavior |
|---|---|---|---|---|
| `username` | URL path | string | Required | 1–32 characters. Start with a lowercase ASCII letter or underscore. Continue with lowercase letters, digits, underscores or hyphens. |

**Response** · `204`

<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X PUT -H "Authorization: Bearer YOUR_API_TOKEN" "https://sftp.example.net:5443/api/sftpserver/users/import_user/disable"
```

</details>

<details><summary>Python example</summary>

```python
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/users/import_user/disable'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
response = requests.put(url, headers=headers, timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/disable";
using var request = new HttpRequestMessage(new HttpMethod("PUT"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");

using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/disable";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        var body = BodyPublishers.noBody();
        var request = builder.method("PUT", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>

## Resume customer logins

**PUT** `/api/sftpserver/users/{username}/enable`

Use this after a temporary suspension. The customer can log in again with the account’s configured authentication mode and credentials, using the same upload folder.

| Parameter | Where | Type | Required | Accepted values and behavior |
|---|---|---|---|---|
| `username` | URL path | string | Required | 1–32 characters. Start with a lowercase ASCII letter or underscore. Continue with lowercase letters, digits, underscores or hyphens. |

**Response** · `204`

<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X PUT -H "Authorization: Bearer YOUR_API_TOKEN" "https://sftp.example.net:5443/api/sftpserver/users/import_user/enable"
```

</details>

<details><summary>Python example</summary>

```python
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/users/import_user/enable'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
response = requests.put(url, headers=headers, timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/enable";
using var request = new HttpRequestMessage(new HttpMethod("PUT"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");

using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/enable";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        var body = BodyPublishers.noBody();
        var request = builder.method("PUT", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>

## Find files waiting for import

**GET** `/api/sftpserver/users/{username}/files`

Poll this endpoint to discover incoming files. Omit path to list the account’s upload root, or supply a relative folder such as incoming. Check isDirectory before downloading an entry. File sizes are in bytes.

| Parameter | Where | Type | Required | Accepted values and behavior |
|---|---|---|---|---|
| `username` | URL path | string | Required | 1–32 characters. Start with a lowercase ASCII letter or underscore. Continue with lowercase letters, digits, underscores or hyphens. |
| `path` | Query | string | Optional | Defaults to the upload root. Relative to /files. URL-encode the query value. Maximum 4,096 characters and 255 per component. Use forward slashes. Empty components, dot segments, backslashes, control characters and symbolic links are refused. |

**Response** · `200 [{"name":"orders.csv","isDirectory":false,"size":734}]`

<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X GET -H "Authorization: Bearer YOUR_API_TOKEN" "https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=incoming"
```

</details>

<details><summary>Python example</summary>

```python
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=incoming'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
response = requests.get(url, headers=headers, timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=incoming";
using var request = new HttpRequestMessage(new HttpMethod("GET"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");

using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=incoming";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        var body = BodyPublishers.noBody();
        var request = builder.method("GET", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>

## Download a file for import

**GET** `/api/sftpserver/users/{username}/files/content`

After listing a folder, pass the file’s relative path to read its exact bytes. Save or process the response as binary data. The source stays in the upload folder so you can retry a failed import.

| Parameter | Where | Type | Required | Accepted values and behavior |
|---|---|---|---|---|
| `username` | URL path | string | Required | 1–32 characters. Start with a lowercase ASCII letter or underscore. Continue with lowercase letters, digits, underscores or hyphens. |
| `path` | Query | string | Required | Identify one existing regular file. Relative to /files. URL-encode the query value. Maximum 4,096 characters and 255 per component. Use forward slashes. Empty components, dot segments, backslashes, control characters and symbolic links are refused. |

**Response** · `200 application/octet-stream`

<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X GET -H "Authorization: Bearer YOUR_API_TOKEN" -o "orders.csv" "https://sftp.example.net:5443/api/sftpserver/users/import_user/files/content?path=incoming/orders.csv"
```

</details>

<details><summary>Python example</summary>

```python
from pathlib import Path
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/users/import_user/files/content?path=incoming/orders.csv'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
response = requests.get(url, headers=headers, timeout=60)
response.raise_for_status()
Path('orders.csv').write_bytes(response.content)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/files/content?path=incoming/orders.csv";
using var request = new HttpRequestMessage(new HttpMethod("GET"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");

using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
await File.WriteAllBytesAsync("orders.csv", await response.Content.ReadAsByteArrayAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/files/content?path=incoming/orders.csv";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        var body = BodyPublishers.noBody();
        var request = builder.method("GET", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofByteArray());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        Files.write(Path.of("orders.csv"), response.body());
    }
}
```

</details>

## Upload a new file

**POST** `/api/sftpserver/users/{username}/files`

Use this to deliver a new file into the customer’s SFTP area. Send multipart field file. The optional path selects the destination in an existing folder. An existing file returns 409, which protects it from an accidental overwrite.

| Parameter | Where | Type | Required | Accepted values and behavior |
|---|---|---|---|---|
| `username` | URL path | string | Required | 1–32 characters. Start with a lowercase ASCII letter or underscore. Continue with lowercase letters, digits, underscores or hyphens. |
| `path` | Query | string | Optional | Defaults to the uploaded filename in the root. The parent folder must exist. Relative to /files. URL-encode the query value. Maximum 4,096 characters and 255 per component. Use forward slashes. Empty components, dot segments, backslashes, control characters and symbolic links are refused. |
| `file` | Multipart form | binary file | Required | One file part named file, up to 50 MiB. The entire multipart request may be up to 51 MiB. |

**Response** · `201 {"path":"incoming/orders.csv"} or 409 if already present`

<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X POST -H "Authorization: Bearer YOUR_API_TOKEN" -F "file=@orders.csv" "https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=incoming/orders.csv"
```

</details>

<details><summary>Python example</summary>

```python
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=incoming/orders.csv'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
with open('orders.csv', 'rb') as upload:
    response = requests.post(url, headers=headers, files={'file': ('orders.csv', upload)}, timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=incoming/orders.csv";
using var request = new HttpRequestMessage(new HttpMethod("POST"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");
var form = new MultipartFormDataContent();
form.Add(new StreamContent(File.OpenRead("orders.csv")), "file", "orders.csv");
request.Content = form;
using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=incoming/orders.csv";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        var boundary = java.util.UUID.randomUUID().toString();
        builder.header("Content-Type", "multipart/form-data; boundary=" + boundary);
        var body = BodyPublishers.concat(
            BodyPublishers.ofString("--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"orders.csv\"\r\nContent-Type: application/octet-stream\r\n\r\n"),
            BodyPublishers.ofFile(Path.of("orders.csv")),
            BodyPublishers.ofString("\r\n--" + boundary + "--\r\n"));
        var request = builder.method("POST", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>

## Replace an existing file

**PUT** `/api/sftpserver/users/{username}/files/content`

Use this to correct a file already present in the customer’s folder. Send the replacement in multipart field file and identify the existing file with the path query parameter. The next download reads the new contents.

| Parameter | Where | Type | Required | Accepted values and behavior |
|---|---|---|---|---|
| `username` | URL path | string | Required | 1–32 characters. Start with a lowercase ASCII letter or underscore. Continue with lowercase letters, digits, underscores or hyphens. |
| `path` | Query | string | Required | Identify one existing regular file. Relative to /files. URL-encode the query value. Maximum 4,096 characters and 255 per component. Use forward slashes. Empty components, dot segments, backslashes, control characters and symbolic links are refused. |
| `file` | Multipart form | binary file | Required | One file part named file, up to 50 MiB. The entire multipart request may be up to 51 MiB. |

**Response** · `204`

<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X PUT -H "Authorization: Bearer YOUR_API_TOKEN" -F "file=@corrected.csv" "https://sftp.example.net:5443/api/sftpserver/users/import_user/files/content?path=incoming/orders.csv"
```

</details>

<details><summary>Python example</summary>

```python
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/users/import_user/files/content?path=incoming/orders.csv'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
with open('corrected.csv', 'rb') as upload:
    response = requests.put(url, headers=headers, files={'file': ('corrected.csv', upload)}, timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/files/content?path=incoming/orders.csv";
using var request = new HttpRequestMessage(new HttpMethod("PUT"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");
var form = new MultipartFormDataContent();
form.Add(new StreamContent(File.OpenRead("corrected.csv")), "file", "corrected.csv");
request.Content = form;
using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/files/content?path=incoming/orders.csv";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        var boundary = java.util.UUID.randomUUID().toString();
        builder.header("Content-Type", "multipart/form-data; boundary=" + boundary);
        var body = BodyPublishers.concat(
            BodyPublishers.ofString("--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"corrected.csv\"\r\nContent-Type: application/octet-stream\r\n\r\n"),
            BodyPublishers.ofFile(Path.of("corrected.csv")),
            BodyPublishers.ofString("\r\n--" + boundary + "--\r\n"));
        var request = builder.method("PUT", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>

## Delete an imported file

**DELETE** `/api/sftpserver/users/{username}/files`

Use this for optional cleanup after your application has successfully committed an import. Pass the same relative path you downloaded. This removes the source file, so retain it when processing fails or needs a retry.

| Parameter | Where | Type | Required | Accepted values and behavior |
|---|---|---|---|---|
| `username` | URL path | string | Required | 1–32 characters. Start with a lowercase ASCII letter or underscore. Continue with lowercase letters, digits, underscores or hyphens. |
| `path` | Query | string | Required | Identify one existing regular file. Relative to /files. URL-encode the query value. Maximum 4,096 characters and 255 per component. Use forward slashes. Empty components, dot segments, backslashes, control characters and symbolic links are refused. |

**Response** · `204`

<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X DELETE -H "Authorization: Bearer YOUR_API_TOKEN" "https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=incoming/orders.csv"
```

</details>

<details><summary>Python example</summary>

```python
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=incoming/orders.csv'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
response = requests.delete(url, headers=headers, timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=incoming/orders.csv";
using var request = new HttpRequestMessage(new HttpMethod("DELETE"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");

using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=incoming/orders.csv";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        var body = BodyPublishers.noBody();
        var request = builder.method("DELETE", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>

## Permanently remove an account

**DELETE** `/api/sftpserver/users/{username}`

Use this when offboarding a customer and their uploads are no longer needed. Collect any outstanding files first. Deletion removes the managed Linux account, its keys and its upload folder with all its contents. Use disable for a temporary suspension.

| Parameter | Where | Type | Required | Accepted values and behavior |
|---|---|---|---|---|
| `username` | URL path | string | Required | 1–32 characters. Start with a lowercase ASCII letter or underscore. Continue with lowercase letters, digits, underscores or hyphens. |

**Response** · `204`

<details><summary>curl example</summary>

```bash
curl --silent --show-error --fail -X DELETE -H "Authorization: Bearer YOUR_API_TOKEN" "https://sftp.example.net:5443/api/sftpserver/users/import_user"
```

</details>

<details><summary>Python example</summary>

```python
import requests

url = 'https://sftp.example.net:5443/api/sftpserver/users/import_user'
headers = dict(Authorization="Bearer YOUR_API_TOKEN")
response = requests.delete(url, headers=headers, timeout=60)
response.raise_for_status()
print(response.text or response.status_code)
```

</details>

<details><summary>C# example</summary>

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

using var client = new HttpClient();
var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user";
using var request = new HttpRequestMessage(new HttpMethod("DELETE"), url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "YOUR_API_TOKEN");

using var response = await client.SendAsync(request);
response.EnsureSuccessStatusCode();
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());
```

</details>

<details><summary>Java example</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.file.*;

class Example {
    public static void main(String[] args) throws Exception {
        var url = "https://sftp.example.net:5443/api/sftpserver/users/import_user";
        var builder = HttpRequest.newBuilder(URI.create(url))
            .header("Authorization", "Bearer YOUR_API_TOKEN");
        var body = BodyPublishers.noBody();
        var request = builder.method("DELETE", body).build();
        var response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() / 100 != 2)
            throw new java.io.IOException("HTTP " + response.statusCode());
        System.out.println(response.statusCode());
        System.out.println(response.body());
    }
}
```

</details>
