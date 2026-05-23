# CE Source

This repository contains the public Linux Made Sane CE source subset needed to build the Community app.

Linux Made Sane Community Edition is source-available under the Business Source License 1.1. Internal business use is allowed for free; hosted, MSP, SaaS, white-label, resale, and third-party managed-service use require a commercial license from Richard D. Kiernan.

Redistributions must preserve the root license, notice, trademark, commercial licensing, and third-party notices.

Desktop Assistant and the local Desktop Helper are part of Community Edition. Future paid desktop functionality may add remote desktop help, team workflows, or managed support paths, but the local GUI-session helper belongs in the CE baseline.

The Desktop Helper uses Avalonia and related rendering packages for the native Linux helper window and tray surface. Native tray/status-notifier support and desktop runtime libraries come from the user's Linux distribution and remain under their own upstream or distribution licenses; preserve `THIRD-PARTY-NOTICES.md` when redistributing LMS.

Included projects:

- `src/LinuxMadeSane.Web`
- `src/LinuxMadeSane.Application`
- `src/LinuxMadeSane.Infrastructure`
- `src/LinuxMadeSane.Core`
- `src/LinuxMadeSane.Connect.Protocol`
- `src/LinuxMadeSane.DesktopHelper`

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
dotnet build src/LinuxMadeSane.DesktopHelper/LinuxMadeSane.DesktopHelper.csproj
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
