# Release Assets

Public GitHub Releases should contain CE assets only.

Expected files for each release:

```text
linux-made-sane-ce-<version>-linux-x64.tar.gz
linux-made-sane-ce-<version>-linux-arm64.tar.gz
linux-made-sane-ce-<version>-linux-arm.tar.gz
SHA256SUMS
release-manifest-<version>.json
```

The installer downloads the matching tarball for the detected runtime ID and verifies it against `SHA256SUMS` when available.

During early distro testing, the same CE files may also be committed under:

```text
packages/<version>/
packages/latest.txt
```

That repo-hosted package path is a fallback so the one-line installer can work before the formal GitHub Release upload process is in place.

Do not upload Pro, portal-local, private manifests, private checksums, app source, local config files, database files, or credentials to the public release repository.
