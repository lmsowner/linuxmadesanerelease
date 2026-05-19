# CE Source

This repository contains the public Linux Made Sane CE source subset needed to build the Community app.

Linux Made Sane Community Edition is source-available under the Business Source License 1.1. Internal business use is allowed for free; hosted, MSP, SaaS, white-label, resale, and third-party managed-service use require a commercial license from Richard D. Kiernan.

Redistributions must preserve the root license, notice, trademark, commercial licensing, and third-party notices.

Included projects:

- `src/LinuxMadeSane.Web`
- `src/LinuxMadeSane.Application`
- `src/LinuxMadeSane.Infrastructure`
- `src/LinuxMadeSane.Core`
- `src/LinuxMadeSane.Connect.Protocol`

Not included:

- public website project
- Pro/Enterprise packages and code
- portal projects and deployment details
- licensing secrets and private manifests
- local runtime databases and private artifacts

Build from source:

```bash
dotnet restore src/LinuxMadeSane.Web/LinuxMadeSane.Web.csproj
dotnet build src/LinuxMadeSane.Web/LinuxMadeSane.Web.csproj
```

Run locally:

```bash
dotnet run --project src/LinuxMadeSane.Web
```

Create a CE package:

```bash
./scripts/publish-ce.sh
```

Run a first-start SQLite smoke test:

```bash
./scripts/smoke-cold-start.sh
```
