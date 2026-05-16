# Release Assets

Current Community Edition release assets are served by the public website:

```text
https://www.linuxmadesane.com
```

The canonical installer is:

```bash
curl -fsSL https://www.linuxmadesane.com/install.sh | sudo bash
```

Short link:

```bash
curl -fsSL https://bit.ly/4tCQKCN | sudo bash
```

The public repository remains a safe public CE source/docs surface. It must not receive the public website project, Pro/Enterprise packages, portal packages, private manifests, license secrets, databases, local configuration, credentials, or proprietary implementation details.

Expected files for each release:

```text
linux-made-sane-ce-<version>-linux-x64.tar.gz
linux-made-sane-ce-<version>-linux-arm64.tar.gz
linux-made-sane-ce-<version>-linux-arm.tar.gz
SHA256SUMS
release-manifest-<version>.json
```

The installer downloads the matching tarball for the detected runtime ID from the website and verifies it against `SHA256SUMS` when available.

The website serves release assets from its configured Community release directory. The private build workflow stages CE tarballs there after the private release matrix is built.

The old repo-hosted package fallback is retired for current releases. Do not add new package tarballs under:

```text
packages/<version>/
packages/latest.txt
```

Current public repository commits should not include release tarballs. Build artifacts are staged into the website release directories by the private build workflow, and download testing should use `https://www.linuxmadesane.com/install.sh`.
