# Managed SFTP server and application API

A customer uploads a file over SSH to their LMS-managed SFTP account. An application such as OpenPlan Flow uses HTTPS to provision that account, select its login method, collect completed uploads and optionally remove a file after import. Each customer sees a private filesystem with a writable `/files` directory. Flow uses one application credential to manage the customer accounts it creates.

- [Endpoint reference with request bodies, parameter limits, responses and examples in curl, Python, C# and Java](endpoints.md)
- [Worked integrations for provisioning, key rotation, authentication changes, collection and offboarding](workflows.md)

## Set up the two connections

| Connection | Destination | Credential |
|---|---|---|
| Customer’s SFTP client | Customer-facing SSH host and port | Managed Linux username and the password, private key or both required for that account |
| Flow backend | HTTPS listener selected in LMS, followed by `/api/sftpserver` | Application bearer token, sent from an allowed IP address |

1. In **SFTP Server → Setup**, choose the base SFTP path (default `/srv/lms-sftp`) and default sign-in method. **Apply configuration** validates the managed OpenSSH configuration. Each managed account receives its own chroot and writable files directory. The host default helps an administrator create users in the UI. API callers supply the login mode explicitly.
2. Open **API and documentation** and create an application credential. Select the LMS interface and enter the allowed source IPs or CIDRs as a comma-separated list. Copy the **SFTP API base URL** from the credential row and save the issued token in the integration’s secret store.
3. Connect directly over HTTPS with certificate verification enabled. A DNS name works when its certificate is valid and it resolves to the selected local address. LMS checks the actual connection peer. Behind a proxy or NAT, establish which peer LMS sees before choosing an allowlist. Forwarded client headers do not establish source identity.
4. Test the connection from the application host. Substitute the actual origin and token in both commands. In Windows PowerShell use `curl.exe`.

```sh
curl --fail --show-error -H "Authorization: Bearer YOUR_API_TOKEN" "https://sftp.example.net:5443/api/sftpserver/"
curl --fail --show-error -H "Authorization: Bearer YOUR_API_TOKEN" "https://sftp.example.net:5443/api/sftpserver/server/status"
```

The first response contains `{"service":"LMS SFTP API","version":1}`. Before onboarding a customer, check that `managed`, `sshActive` and `configurationValid` are all true in the second response. An administrator can inspect accounts in **Users** and **User Details**, and review configuration changes, per-user history and rollback snapshots in **Audit / History**. Bootstrap, host repair and service restarts remain administrator operations.

## Manage the customer’s SSH access

In **Users**, open a managed account to change its login method, reset its password, register keys or suspend access. **User Details** shows its folder, keys and audit history. The administrator can choose which SFTP operations the customer may perform, including reading, uploading, creating, deleting and renaming files or folders. Creating a file requires write access. Moving an upload into place also needs the appropriate create, delete and rename permissions. Have the customer reconnect after changing these settings. The application API exposes the account, key and file operations listed in the reference. Customer SFTP permission switches are managed on the server page.

## Move a Flow integration to LMS

Configure the LMS API base URL, application token and customer-facing SSH host/port as separate settings. When building a request, retain the entire base path. For example, resolving a leading `/users` against the origin would discard `/api/sftpserver`. The endpoint reference gives the full path for each call.

| Step | What Flow should do |
|---|---|
| Identify the customer | Choose a stable lowercase Linux username. Call **GET** `/api/sftpserver/users/{username}/exists`. A true result means this credential owns the account. False also covers names already owned elsewhere. |
| Provision | On false, **POST** `/api/sftpserver/users` with the username, mode and required credential fields. Expect 201. After a timeout or conflict, read ownership and account details again. A name that remains unavailable needs operator attention. |
| Reconcile | **GET** `/api/sftpserver/users/{username}` returns `username`, `enabled`, `authenticationMode` and `hasPublicKey`. Key presence alone cannot identify which key was installed. Coordinate any replacement with the customer. |
| Change login requirements | **PUT** the verified public key to `/api/sftpserver/users/{username}/public-key`, then **PUT** the target mode to `/api/sftpserver/users/{username}/auth`. Read the account back. **GET** `/api/sftpserver/users/{username}/public-key-example` supplies onboarding text. |
| Collect uploads | **GET** `/api/sftpserver/users/{username}/files`. Skip directories or list them separately. Download the selected file with **GET** `/api/sftpserver/users/{username}/files/content?path=…`. |
| Clean up | After a successful durable import, and only if cleanup is enabled, **DELETE** `/api/sftpserver/users/{username}/files?path=…`. Preserve the full relative path from the listing. |

Existing manually created accounts and accounts owned by another credential are outside the application’s scope. Rotating or reissuing the token on the **same credential** preserves ownership. A new credential has a separate account inventory. For cutover, provision a test account, upload a uniquely named file over SFTP, import it with cleanup disabled, then verify cleanup with another test file. Plan separately for transferring outstanding files and changing customer connection settings before retiring the previous service.

## Give a customer key-based access

| `authenticationMode` | Fields for account creation | Required at SFTP login |
|---|---|---|
| `PasswordOnly` | `password` | Account password |
| `PublicKeyOnly` | `publicKey` | Matching private key |
| `PasswordAndPublicKey` | `password` and `publicKey` | Both factors |

On the customer's machine, run OpenSSH in a private directory and choose a passphrase. Use a fresh filename for each new key pair.

```sh
ssh-keygen -t ed25519 -f customer-sftp -C "customer SFTP uploads"
ssh-keygen -lf customer-sftp.pub
```

The customer keeps `customer-sftp` and sends the single line from `customer-sftp.pub` to the operator. Verify the fingerprint through your agreed channel. For a new account, send the public line in the create request. For an existing account, **PUT public-key** replaces all registered keys. Register the key before switching to a mode that requires it. To remove keys, switch to `PasswordOnly` first. Supply a fresh password when switching from `PublicKeyOnly` to a password-capable mode.

Test from the customer's machine using the customer-facing SSH address. Add `-P PORT` if the SSH listener uses a custom port. Verify the server's host-key fingerprint on first connection.

```sh
sftp -o IdentitiesOnly=yes -i ./customer-sftp import_user@sftp.example.net
```

At the SFTP prompt, run `cd /files`. A sender can upload `orders-1042.csv.part` and rename it to `orders-1042.csv` when complete. Agree on a final naming convention with Flow so polling cannot pick up a partly written file.

## File paths, limits and retries

SFTP sees `/files/incoming/orders.csv`. The API sees `incoming/orders.csv`. A directory listing returns each entry’s **name**, **isDirectory** flag and **size** in bytes. Join its name to the relative directory you listed and URL-encode the query value once. Omit `path` to list the upload root.

- Files may contain up to **50 MiB**. Uploads and replacements use one multipart field named `file`, with a **51 MiB** total request limit. JSON requests have a 65,536-byte body limit. A folder can list up to 10,000 entries. Divide large incoming queues into folders.
- New uploads require an existing parent directory. POST protects an existing file from overwrite. PUT replaces an existing regular file. File operations run with the managed account’s filesystem permissions and refuse symbolic links and parent traversal.
- Use unique final filenames and keep track of successfully imported deliveries. Downloading leaves the source intact. Deletion addresses a **path**, so reusing a filename could delete a newer delivery. After a timeout, reconcile the import result before retrying cleanup.
- Disabling an account blocks new customer logins. The owning application can still collect remaining files. Revoking the application token stops its API calls. Deleting the account permanently removes the managed account, keys and upload directory. Drain it first.

## Troubleshoot a request

| HTTP status | What to check |
|---|---|
| 400 | Check parameter names, username, enum spelling, key format and relative path. |
| 401 | Check the bearer token and whether it was revoked or rotated. Every endpoint, including API health, requires it. |
| 403 | Check TLS, the selected LMS address and the actual source IP. For a particular file, also check account permissions. |
| 404 | Verify account ownership, filename and the relative folder path. |
| 409 | Check for an unavailable username, an existing upload, a key still required by the login mode, or an unsuccessful host change. Review the SFTP audit history. |
| 413 / 415 | Check the transfer or listing limits and use multipart form data for uploads and replacements. |
| 429 | Four file operations can be active concurrently per LMS process. Back off before retrying. |
| 503 | The file helper could not complete the operation. Check host status and audit history before retrying. |

A 204 response has no body. Parse downloads as binary data. Some failures have an empty body, so use the HTTP status before attempting to parse JSON.

## Audit history

Choose a UTC date under **Audit / History** to read that day’s log, newest first. Each line shows the time, outcome and event. Expand it for details. Use **Older** and **Newer** to page through busy days. The account view shows the same log filtered to that user. The log covers the last 24 hours, including the part of yesterday within that window. LMS deletes expired audit entries at startup and once a minute while running.
