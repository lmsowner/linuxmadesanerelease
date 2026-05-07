# Security Model

Linux Made Sane is a local-first Linux administration and automation app. It can run terminals, execute runbooks, manage services, patch systems, manage shares, and operate on files. That is powerful by design.

## Runner Access

The installer creates a dedicated `linuxmadesane` service account for running the web service and owning LMS data. That account is not automatically granted root access.

For unattended elevated workflows, configure a dedicated LMS runner account on each managed machine:

- use public/private key login rather than a shared human password
- avoid password login for the runner account
- grant passwordless sudo only where you are comfortable allowing LMS to automate privileged work
- prefer a scoped sudoers rule or root-owned LMS helper over blanket access where possible

sudo-marked LMS runbooks use non-interactive sudo. If the runner account is not allowed to elevate without a password, the operation should fail clearly instead of hanging on a password prompt.

## Credential Handling

LMS should keep credentials in protected secret storage and out of logs. Do not place API keys, private keys, OTP secrets, passwords, database files, or local configuration in the public release repository.

## Transparency

LMS is open source so operators can inspect what the runner account is allowed to do, how credentials are handled, and where privileged actions are routed. The operational trade-off is intentional: LMS makes administration easier when you explicitly grant it controlled access to administer the machine.
