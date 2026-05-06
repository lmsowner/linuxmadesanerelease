# CE Source

This repository contains the Linux Made Sane CE source tree needed to build the public CE app.

Included projects:

- `src/LinuxMadeSane.Web`
- `src/LinuxMadeSane.Application`
- `src/LinuxMadeSane.Infrastructure`
- `src/LinuxMadeSane.Core`
- `src/LinuxMadeSane.Connect.Protocol`

Excluded private/non-CE projects:

- `LinuxMadeSane.Connect.Client`
- `LinuxMadeSane.Portal.Web`
- `LinuxMadeSane.Portal.Core`
- `LinuxMadeSane.Portal.Infrastructure`
- private portal deployment docs
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
