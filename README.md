# Dispatch

A mod manager for Euro Truck Simulator 2 (Windows).

Status: **Milestone 1: read-only app** (M0 spikes done). See [PLAN.md](PLAN.md) and
[docs/walkthrough-m1.md](docs/walkthrough-m1.md) for how the code is organised.

Dispatch is not affiliated with or endorsed by SCS Software. "Euro Truck Simulator 2"
is a trademark of SCS Software.

## Build

Requires the .NET 10 SDK.

```
dotnet build
dotnet test
dotnet run --project src/Dispatch.App
```

M1 only reads game files. Dispatch's own data (settings, cache, logs) lives in `%LOCALAPPDATA%\Dispatch`.

## Licence

Public domain — see [LICENSE](LICENSE) (The Unlicense). Third-party packages keep their
own licences, listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
