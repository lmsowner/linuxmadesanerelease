# LMS remote desktop

Hosts > **Remote Desktop (RDP/VNC)** owns connection configuration. Connect opens the same resizable popup interface used by Terminal and File Browser. The viewer contains the standard Apache Guacamole client, with only an LMS title, a disconnect control and iframe focus handling. A browser may choose to present the popup as a new tab; permit popups for LMS. No Guacamole login is exposed.

## Reuse and version compatibility

The implementation reuses managed hosts, shared encrypted saved credentials, the SQLite database, authenticated LMS cookies and session policy, discovery scopes, compact buttons, dialogs, credential picker/editor, privileged command execution, infrastructure package installation, service management and configuration recovery. RDP and VNC are appended credential kinds; they never enter SSH credential matching.

Both official Docker components are pinned to **Apache Guacamole 1.6.0**. The Java provider compiles against `guacamole-ext:1.6.0`, uses `AbstractAuthenticationProvider`, `AbstractAuthenticatedUser` and `SimpleUserContext`, and runs under Java 21 in the official web image. It neither implements a protocol nor forks the web client. Dependencies are provided by Guacamole; the extension JAR does not bundle an authentication database.

Compatibility references:

- [Apache release and compatibility notes](https://guacamole.apache.org/releases/1.6.0/)
- [Supported custom authentication extension API](https://guacamole.apache.org/doc/1.6.0/gug/custom-auth.html)
- [1.6.0 extension API](https://guacamole.apache.org/doc/1.6.0/guacamole-ext/)
- [Docker deployment](https://guacamole.apache.org/doc/1.6.0/gug/guacamole-docker.html)
- [RDP/VNC connection options](https://guacamole.apache.org/doc/1.6.0/gug/configuring-guacamole.html)
- [Apache security reports](https://guacamole.apache.org/security/)

The verified official image sets `WEBAPP_CONTEXT=guacamole`; the deployment explicitly declares `/guacamole/` as browser entry metadata. This is separate from its `/api/languages` health endpoint.

## Trust and launch

Connection rows contain owner, optional existing host ID, target, port, protocol, credential ID and non-secret options. There is no target password column. LMS resolves passwords from its existing encrypted repository only when the extension redeems a launch. Host reconciliation matches the existing inventory without inventing another host record.

Connect first opens a prepared popup during a browser click. Its same-origin launch page obtains LMS antiforgery protection and submits a POST. LMS checks the existing session, enabled account, owner, target configuration and engine health. It creates a 256-bit opaque grant, hashes it server-side, binds it to one user, connection and session, and atomically consumes it within 30 seconds. Grant expiry does not end an already established session.

LMS itself posts this grant to the internal Guacamole token endpoint: the grant never enters browser URLs, history, storage or HTML. The extension calls a private Unix socket HTTP listener with a separate encrypted LMS service key. There is no public bridge endpoint. The approved destination is resolved and pinned server-side; loopback, unspecified, link-local/metadata and multicast addresses are denied. The browser cannot supply a target, port or credential.

The actual Guacamole authentication token stays in LMS memory. The client sees only the harmless marker `lms`; both real authentication and HTTP tunnel tokens stay server-side; its API and tunnel requests are relayed under the user's LMS cookie and bound session. Proxy routing excludes administration, connection parameters and sharing credentials, including nested tunnel APIs. Guacamole sharing is disabled. Both WebSocket and HTTP tunnel routes use the same guards. Cookie binding prevents a different LMS login from reusing an existing window. The stock client requires a narrowly scoped `unsafe-eval` CSP allowance for its own JIT expressions; other LMS pages retain their existing policy.

## Revocation

Logout cancels bound relays immediately. Connection edit/delete and engine stop/restart/upgrade/uninstall cancel active sessions. A five-second monitor rechecks account state/revision, connection ownership and the complete saved configuration snapshot, associated host, credentials and LMS session expiry. Any account update conservatively invalidates the desktop's account snapshot. Deleting/disabling an account, deleting the host/credential or removing its protected secret ends authorisation. Permission changes are implemented through these same LMS account/ownership guards.

Cleanup uses Guacamole's supported token deletion endpoint to invalidate the upstream authentication session and close its tunnels. Failed deletion retains the token for retry; an unavailable engine cannot be promised immediate upstream cleanup, although the browser relay is cancelled immediately. The configured Guacamole idle API timeout is five minutes. HTTP polling reads normally abort during handover and do not imply a disconnect; abandoned HTTP sessions are also reaped after five minutes without browser activity. Explicit Disconnect and logout cancel either transport immediately. Tokens/grants/active sessions are never restored after LMS restarts.

## Installation and operations

Nothing installs on page load or application startup. Users can configure connections before installation. First Connect requests explicit installation consent and resumes after successful installation. Cancelling closes the prepared popup. Install uses existing LMS Docker package/service mechanisms if Docker is absent. Containers are managed by privileged commands through LMS's existing runner, with restart policy, capability restrictions and ownership labels. Operations refuse unmanaged name collisions. Guacd has no published port. Guacamole binds only to LMS host loopback and is accessible to users through the authenticated LMS proxy. No new public tunnel, standalone application, database or host-level protocol packages are required.

The internal key is supplied using Docker stdin and atomically written into container tmpfs; it is reprovisioned before launch after container restarts. Tomcat access logging is disabled in the packaged configuration so internal token-bearing tunnel requests are not logged. Connection-attempt failures log types and stages rather than credential-bearing exception bodies. Health covers the web client and running managed protocol service; Logs includes both services. Install, Start, Stop, Restart, Upgrade and Uninstall recover through the same manager. Stop/uninstall preserve connection metadata, encrypted credentials and bridge metadata. Uninstall retains its managed network if other containers are still attached, without removing or disconnecting those containers.

The temporary optional RDP drive is held in guacd tmpfs and removed during session cleanup. No durable shared drive or third-party credential store is created.

## Backup and routing

Existing SQLite backup includes connection rows and host/credential references. Configuration recovery includes the remote-desktop **config** directory (version/bridge reference, extension and non-secret Guacamole/Tomcat settings). Its **run** directory, socket, volatile key and active sessions are excluded. Existing LMS protected-secret/key backup handles credentials separately. Restores with missing protected credentials show **Configure credentials** and disable Connect until repaired.

All browser paths remain under the existing LMS origin. Existing LMS/Edge Gateway/Cloudflare routing must forward WebSocket upgrades as it does for LMS Terminal. No separate hostname is introduced. Test the actual customer routing chain before rollout; the automated disposable ingress verifies HTTP and WebSocket forwarding but does not provision a Cloudflare account.

## Verification and operational limits

Unit coverage includes rejection of stale connection snapshots at equal revision numbers, atomic concurrent redemption, replay/expiry/user/connection/session binding, cookie ownership, credential encryption/domain/password-only VNC, CRUD/reconciliation, account/credential revocation, unsafe destinations, nested parameter/admin filtering, discovery without authentication, SQLite backup restoration and cleanup retry.

The disposable integration application exercises the production installer, provider, endpoints, proxy and real panel with TigerVNC and xrdp targets. The browser test checks separate native popups, real rendered desktops and keyboard input, no second login, blocked credential/admin APIs, target passwords absent from browser storage, logout and service lifecycle, preservation on uninstall, installation cancellation and approved recovery. See `tests/RemoteDesktopIntegration` for commands.

RDP certificate verification is enabled by default. Connections explicitly accepting an untrusted/self-signed certificate require confirmation. Because targets are pinned to an IP before guacd connects, strictly verified certificates must be valid for that selected address. A hostname-only certificate may fail verification; configure a suitable certificate or explicitly approve this target's trust exception. No SSH-forwarded desktop route is invented: the Linux engine must reach the target directly over LAN, tailnet or an explicitly configured route. Audio, clipboard, drive redirection, display resize, Windows NLA/domain policy and actual Cloudflare/Edge Gateway deployment need customer-environment verification; the disposable RDP target is xrdp, not Windows.

## Rebuild the bridge

```bash
docker run --rm --mount type=bind,source="$PWD/guacamole-lms",target=/work \
  --workdir /work maven:3.9.11-eclipse-temurin-21 mvn -B package
cp guacamole-lms/target/guacamole-lms-1.6.0.jar src/LinuxMadeSane.Web/Resources/RemoteDesktop/
```

The checked-in JAR ships as application content, so installing the engine does not require Maven or Java packages on the LMS host. The CE source export includes its rebuildable source and the same packaged resource.


## Change locations

- `Core/Models/RemoteDesktop`, `IRemoteDesktopConnectionStore`, Infrastructure persistence/store: connection metadata.
- Infrastructure credential services, discovery, diagnostics and configuration recovery: existing credential, inventory, installation and backup integration.
- `Web/Services/RemoteDesktop` and `RemoteDesktopEndpointExtensions`: engine lifecycle, grants, authorisation, internal bridge, revocation and authenticated HTTP/WebSocket proxy.
- `Hosts.razor`, `RemoteDesktopPanel`, existing credential picker/editor and `terminal.js`: native tab, compact controls, credentials and popouts.
- `guacamole-lms` and `Web/Resources/RemoteDesktop`: version-compatible extension source/JAR and Tomcat logging policy.
- `tests/LinuxMadeSane.Tests/Web/RemoteDesktopTests.cs`, `tests/remote-desktop-popup.test.mjs`, `tests/RemoteDesktopIntegration`: unit, popup and disposable integration verification.
- CE project/export templates: package the same engine bridge and include its rebuildable CE source.


## Recorded verification

On 12 October 2026: 45 targeted .NET checks and 21 relevant JavaScript checks passed. The full .NET suite passed 2,126 tests with one skip and three existing source-check failures (`DriveUsageHelper_IsHandledBeforeWebApplicationStartup`, `MaintenanceService_UsesFixedPrivilegedArgumentsWithoutShellCommands`, `ConnectAs_IsAvailableOnlyFromTheFileContextMenu`). A broader JavaScript run also reproduced 12 failures in the unchanged reconnect test/module; the remote desktop popup, marquee, firewall and sidebar checks passed.

Disposable native-popup checks passed for RDP and VNC, including real keyboard delivery, forbidden parameter/admin APIs, target passwords absent from browser storage, logout, approved pinned upgrade, stop/start/restart, metadata/credential preservation on uninstall, cancellation closing the prepared window, approved reinstall and established-session revocation on account disable. The same acceptance path passed with WebSocket desktop transport deliberately failed to exercise HTTP fallback.
