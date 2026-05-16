# Security Model

Linux Made Sane is a local-first Linux administration and automation app. It can run terminals, execute runbooks, manage services, patch systems, manage shares, and operate on files. That is powerful by design.

## Runner Access

The installer creates a dedicated `linuxmadesane` service account for running the web service and owning LMS data.

For the local machine, the installer also prepares localhost SSH automation by default:

- creates a dedicated `linuxmadesane-runner` account
- creates key-based localhost SSH access for LMS-managed terminal and automation workflows
- uses `/var/lib/linuxmadesane/runner/workspace` as the default writable working directory
- enables non-interactive local sudo for LMS automation unless disabled

This is intended to make a fresh Community install usable without asking for a QR code, password prompt, or manual localhost credential setup.

Disable those behaviours during install when you want to configure access manually:

```bash
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash -s -- --no-local-ssh
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash -s -- --no-local-sudo
```

For remote managed hosts, use deliberate key-based access. Avoid shared human passwords, avoid broad root credentials, and only grant passwordless sudo where you are comfortable allowing LMS to automate privileged work.

sudo-marked LMS runbooks use non-interactive sudo. If the runner account is not allowed to elevate without a password, the operation should fail clearly instead of hanging on a password prompt.

## Credential Handling

LMS should keep credentials in protected secret storage and out of logs. Do not place API keys, private keys, OTP secrets, passwords, database files, or local configuration in the public release repository.

## Public Repository Boundary

This public repository is for the Community Edition source subset, public docs, and release-channel metadata. Do not publish the public website project, Pro/Enterprise packages, portal packages, license secrets, private manifests, local configuration, databases, credentials, or proprietary implementation details here.
