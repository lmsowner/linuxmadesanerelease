# CE Source

This repository contains the public Linux Made Sane CE source subset needed to build the Community app.

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
