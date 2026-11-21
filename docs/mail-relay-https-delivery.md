# Mail Relay HTTPS delivery

LMS retains its Postfix SMTP listener, sender/source restrictions, persistent queue,
queue scheduling and logging. Existing installations remain on their existing
SMTP transport; no configuration migration selects an API automatically.

Mail Relay has four provider tabs: **LMS / Edge Gateway**, **Microsoft 365**,
**Gmail** and **Amazon SES**. The LMS tab retains its existing direct SMTP,
DNS, device and application controls. Each cloud tab lists its own saved
connections, including multiple accounts with the same provider, and offers
**Add connection**, guided setup and **I already have settings**.

**Save connection** stores settings for independent tests. **Check connection** and
**Send test email** use that connection without changing the installed delivery
route or using the live notification/OTP queue. The live route remains clearly
labelled. Saving/testing does not activate a connection. Switching live mail is a
separate, collapsed **Use this connection for live mail** action with explicit
confirmation, and requires saved, checked and successfully tested settings.
Before a deliberate switch, LMS preserves the previous route as a saved record.
Tabs retain their selection during status refreshes. Credentials
are encrypted through the existing LMS secret store. Setup drafts persist only
IDs and secret references in the existing database. Preparing a draft does not
change the active delivery route. Only explicitly activating a connection runs the existing provisioning job;
it requires the normal elevated host permissions. Saving test connections does not.

## Microsoft 365

The guided path prepares a one-year RSA application certificate in encrypted LMS
storage. Enter the sender mailbox and choose **Register with Microsoft 365**.
LMS runs its existing PowerShell provisioning flow as its service account and
shows the Microsoft device sign-in link/code in the setup dialog. Complete both
Graph and Exchange administrator sign-ins with accounts in the same tenant.
LMS saves the Entra tenant, application and service-principal IDs before starting
Exchange authorisation. Closing the page, cancelling, a timeout or a later failure
retains that checkpoint in the encrypted-credential-backed setup draft. Reopen the
connection and choose **Continue mailbox authorisation** to retry Exchange without
repeating Entra registration. Changing the sender or replacing an expired certificate
starts a separate application registration; existing live credentials are preserved.
LMS records Exchange connection success separately from verified mailbox permissions.
Setup validates that the authenticated Exchange session exposes the required
provisioning commands before changing Exchange resources. Resource discovery uses
explicit list-and-match reads with errors propagated; an unavailable lookup is not
treated as an absent resource. Scope creation first checks for an existing equivalent single-mailbox scope, even
when its name differs from the current registration's generated name. LMS reuses that
scope without changing, deleting or renaming it, and binds only this application's
sending role to it. Matching accepts a single primary-address equality with balanced
outer parentheses; compound/wildcard, exclusive and server scopes are not reused.
Conflicting named scopes are left untouched. A concurrent equivalent creation is
handled by a bounded re-read and reuse, rather than another blind creation attempt.
Mailbox authorisation is still verified before the draft becomes ready. Post-login failures retain a bounded, redacted Exchange
response beside the setup controls, including the failing command. Credential fields,
bearer/JWT tokens, key blocks and URL credentials/query strings are removed. Raw SDK
responses are not logged. This distinguishes read failures, creation failures and
missing command access instead of attributing every failure to account permissions.
Post-login problems identify the operation (mailbox lookup, Exchange application,
sending scope, role assignment or permission verification) and remain visible after
reopening the draft. The verified-completion event saves the ready state directly,
even if the browser disconnects before the dialog's final continuation. Best-effort
session cleanup cannot invalidate already verified mailbox permissions.
Browser sign-in success does not mean mailbox authorisation succeeded: connection
checking stays disabled until guided registration completes. Saved IDs can be inspected
under **Saved application details**. Older working/manual configurations remain usable
without re-registration. An older incomplete draft retains its usable certificate, so
retrying resolves the same application through the existing deterministic upsert. The initial Graph and Exchange authorisations are separate;
this flow does not reuse a Graph access token against Exchange or give the sending
application tenant-wide administrator permissions to avoid a sign-in.
Tenant and application IDs are captured automatically; no application secret or
manual ID entry is required in guided setup. LMS checks for a usable PowerShell
runtime automatically. When missing or too old, it uses its existing elevated
command runner to install the required native dependencies and a private Microsoft
PowerShell 7.6.9 runtime under `/opt/linuxmadesane/tools/powershell/7.6.9`.
Official x64/arm64 release archives are pinned by SHA-256 and validated before use;
existing system PowerShell installations are preserved. The workflow installs
missing `Microsoft.Graph.Authentication` (2.41.2) and `ExchangeOnlineManagement`
(3.10.1) modules for the unprivileged service account from PowerShell Gallery.
Users are not asked to install these tools. Cancellation waits for any in-progress
package transaction to finish safely before stopping registration. It uses device-code administrator sign-in, uploads
only the public certificate, creates/resumes a dedicated single-tenant application
and grants Exchange **Application Mail.Send** scoped to the selected mailbox.
The entered address may be an alias or sign-in name. LMS resolves its primary SMTP
address and directory object ID through Exchange, displays the primary address that
messages will use, and continues assigning permissions in the same session. It does
not change the Microsoft mailbox, add aliases or enable alias sending. The original
registration key and certificate are retained across resume, including after address
resolution. Graph submission targets the resolved directory ID, so primary email and
sign-in-name differences do not break delivery. If sender/tenant/application settings
are edited, an old resolved mailbox ID is not used for the changed identity.
Check the connection, then send a test. Raw SDK output and sign-in codes are not persisted in application logs. Registration is cancellable; cancelling or failing setup does not change live mail.

The Exchange RBAC grant is deliberately not accompanied by an unscoped Entra
Mail.Send grant. These permissions are additive; adding a broad Entra grant can
allow other mailboxes. Existing applications may instead use their already
configured, appropriately restricted application credentials. The manual path accepts an encrypted private certificate (.pfx/.p12) and keeps an existing-secret option for compatibility. The existing LMS
Microsoft notification settings can be imported; its credential is copied into
a relay-owned encrypted reference so resetting the relay does not remove the
notification credential. Both sending paths reuse the same Graph application
credential implementation.

After Exchange verifies a grant, the Graph sending API can still reject requests while
Microsoft's permission cache catches up. Microsoft documents a 30-minute to two-hour
cache interval; Test-ServicePrincipalAuthorization bypasses that cache. For a verified
mailbox with a Graph access-denied response, LMS displays the error code and explains
this possible delay without asking for another registration. Other denial codes retain
their specific, redacted provider response. This hint does not silently retry, activate
another route or convert every 403 into a temporary failure. Leave send attempts idle
for 30 minutes before testing again. See [Microsoft's cache limitations](https://learn.microsoft.com/en-us/exchange/permissions-exo/application-rbac#limitations).

Application token acquisition does not prove mailbox permissions. The send test
validates the selected mailbox. Exchange permission propagation can take time.
The administrator remains responsible for tenant consent and mailbox policy.
Certificate renewal requires reviewing registration again before expiry; LMS does
not silently grant new access.

- [Graph MIME sendMail](https://learn.microsoft.com/en-us/graph/api/user-sendmail?view=graph-rest-1.0)
- [Exchange application RBAC and additive permissions](https://learn.microsoft.com/en-us/exchange/permissions-exo/application-rbac)
- [Exchange PowerShell host requirements](https://learn.microsoft.com/en-us/powershell/exchange/exchange-online-powershell-v2?view=exchange-ps)
- [Certificate application credentials](https://learn.microsoft.com/en-us/entra/identity-platform/certificate-credentials)

## Personal Gmail and Google Workspace

Google API/authentication dependencies ship with LMS; no Google Cloud CLI,
PowerShell or manually installed SDK is required. OAuth application settings and
Google consent remain account-authorisation steps, not host prerequisites.

Personal accounts use a **customer-owned Web application OAuth client**. Enable
Gmail API, configure Google Auth Platform consent and register the exact callback
shown by LMS. Enter the client ID/secret, select **Connect Google Account**, finish
consent and return to **Check connection**. Only `gmail.send` is requested. Consent
uses a short-lived, single-use state ticket and PKCE; refresh tokens are encrypted
and renewed automatically during sending. The browser never receives the stored
refresh token. A denied or revoked grant requires reconnecting.

Use the same HTTPS LMS origin for setup and callback. For a private server, an
SSH browser tunnel to a registered localhost callback is an option: the callback
must reach the same LMS instance. A remote server's loopback address is not the
administrator's workstation. No centrally managed LMS Google application or
public vendor callback is assumed. External Google apps in **Testing** mode issue
Gmail refresh tokens that expire after seven days; use an appropriate production
or Workspace-internal consent configuration for unattended sending.

Workspace administrators can instead select domain-wide delegation and upload a
service-account JSON key into encrypted storage. Enable Gmail API and authorise
that service account's numeric OAuth client ID in Workspace Admin with only
`https://www.googleapis.com/auth/gmail.send`. Enter the mailbox to impersonate.
This is for Workspace, not personal Gmail. This first implementation uses the
encrypted key path for delegation; keyless IAM sign-JWT delegation is not added.

Use the connected mailbox or an authorised sending identity. Google may enforce
or rewrite From according to its send-as policy; check the test message. Sender
aliases, consent verification and sending limits remain Google-controlled.

- [Google web application OAuth](https://developers.google.com/identity/protocols/oauth2/web-server)
- [Refresh token expiry](https://developers.google.com/identity/protocols/oauth2)
- [Gmail MIME submission](https://developers.google.com/workspace/gmail/api/guides/sending)

## Amazon SES

Select the AWS region, domain/email identity and sender, then choose **Prepare SES
identity**. The bundled AWS SDK authenticates using the selected service role or
encrypted AWS credentials. No AWS CLI or PowerShell installation is required.
LMS confirms the account with STS, creates the identity only if it is missing,
retrieves Easy DKIM DNS tokens **and their signing hosted zone**, reports
sandbox/production status and generates an IAM policy restricted to that account,
region, identity and From address. Review
and attach this policy through your IAM administration process. It does not grant
itself IAM administration rights or request production access automatically.

Prefer the AWS SDK credential chain available to the LMS service: instance/workload
roles or renewable short-lived credentials. SDK role-profile support includes
SecurityToken. An administrator's interactive CLI login is not automatically the
LMS service account's credential. Cross-account role trust/external IDs belong in
the customer's existing AWS role/profile configuration, not a vendor AWS account.
Static credentials and optional session tokens are an encrypted fallback; manually
supplied session tokens must be replaced on expiry.

Publish the exact SES verification records at your DNS provider. Preserve existing
MX, SPF and DMARC. DKIM signing is performed by SES. In the SES sandbox recipients
must also be verified; AWS must approve production access. **Check connection**
checks sending-enabled and identity status; **Send test email** additionally
checks the actual sending permission. No IAM keys are generated or printed by
the setup script.

- [SES raw MIME and attachments](https://docs.aws.amazon.com/ses/latest/dg/send-email-raw.html)
- [SES identity and DNS setup](https://docs.aws.amazon.com/ses/latest/dg/getting-started-sending.html)

## Queue and delivery outcomes

The additional Postfix `pipe(8)` transport runs as an unprivileged container user.
It passes each original queue ID, envelope sender/recipient batch and CRLF MIME to a
fixed loopback LMS endpoint on port 5080. That endpoint checks the original
connection address/Host and a separate protected bearer credential. Forwarded
headers and browser sessions do not authorise submission. It follows no redirects
and sends provider requests only to HTTPS endpoints. Existing installations must
retain their standard loopback HTTP listener for the native queue bridge.

Postfix is the only message queue and retry worker. The existing SQLite database
stores only submission receipts, never message bodies. A durable claim precedes
submission. Confirmed acceptance is remembered across retries/restarts. Explicit
throttling defers with Retry-After; credential/configuration errors retain queued
mail for correction. Native permanent failures use normal Postfix bounce rules.
No automatic cross-provider fallback is implemented.

A timeout, interrupted submission or uncertain server response pauses automatic
resending for that attempt. **Delivery needs attention** appears on the main relay
screen. After checking provider mail tracing, explicitly confirm acceptance or
review/authorise another attempt. Resending may produce a duplicate. Resolution
waits five minutes to avoid racing an active submission. The existing queue applies
that decision on its next attempt. Switching provider/sender requires an empty
queue; repairing credentials for the same provider/sender remains available.

API transports are staged deferred until their settings have been saved, avoiding
sending through the previously configured provider during a route change. Direct
SMTP continues using its existing configuration. Resetting local application
credentials clears API credentials and setup drafts without needing a successful
cloud sign-in; it does not delete cloud app registrations.

API acceptance is **not** final recipient delivery. Provider message IDs, when
available, accompany native queue IDs for tracing. Cloud providers control the SMTP
MAIL FROM/return path and bounce handling; arbitrary envelope senders are not promised. Providers can replace headers
such as Message-ID/Date according to their API behaviour.

SES receives raw MIME and an explicit destination. Graph/Gmail derive recipients
from MIME headers. Native batches contain up to 50 recipients: ordinary messages
retain their To/CC list, blind recipients are added only to Bcc, and header-only
recipients are removed. For larger or partially retried batches, visible recipient
lists are restricted to that batch to avoid sending extra copies. MIME bodies and
attachment bytes remain unchanged. MimeKit.Core parses RFC mailbox/group address headers without rebuilding the MIME body. If the native queue regroups recipients after
an earlier accepted/uncertain batch, overlapping envelope receipts pause automatic
submission for operator review rather than risk duplicates.

SES and Gmail retain the relay's 25 MiB total MIME limit. Graph's simple MIME API
uses a conservative 2.8 MB raw MIME limit to stay below its encoded request limit.
Large Graph upload-session workflows and their additional permissions are outside
this change. Security message Expires headers are checked
before submission; expired mail is not sent. Existing SMTP expiry behaviour remains.

## Verification

Run `dotnet test tests/LinuxMadeSane.MailRelay.Tests` for the focused CE-only suite.
It includes existing direct SMTP/provisioning/store/rendering regressions plus
mocked Graph/Gmail/SES requests, encrypted credential references, OAuth/PKCE,
revoked grants, quota/timeout classification, attachments/recipients and durable
submission receipts. No live provider credentials are required. The new tests are
also included in the main test project.

An opt-in disposable native Postfix fixture can export its image context by setting
`LMS_MAIL_TRANSPORT_FIXTURE_DIR` for the focused test suite. Run `tests/LinuxMadeSane.MailRelay.Tests/verify-postfix-transport.sh` for the disposable container integration check. It uses no host mail service or cloud accounts. Live provider authorisation and
recipient inbox arrival remain administrator acceptance checks after review.

## Isolated Microsoft provisioning regression checks

The source-controlled PowerShell fixture replaces Microsoft modules and commands with
local mocks; it requires no cloud credentials and performs no tenant operations.
Generate the scripts from the actual setup code, then run the native cases with an
available PowerShell 7.6+ executable:

```sh
LMS_MAIL_UI_FIXTURE_DIR=/tmp/lms-mail-ui-review dotnet test tests/LinuxMadeSane.MailRelay.Tests
python3 tests/LinuxMadeSane.MailRelay.Tests/Fixtures/run-microsoft-provisioning-fixtures.py --generated-dir /tmp/lms-mail-ui-review --pwsh pwsh
```

The cases cover differently named equivalent scopes, existing application/certificate
resume, concurrent creation, and rejection of broad, exclusive or malformed filters,
alongside authentication, read/create failure and completion handling.
