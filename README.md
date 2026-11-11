# Dispatch

A mod manager for Euro Truck Simulator 2 (Windows).

Status: **v1 (M0–M4 plus v1 features)**, version 0.5.0. See [PLAN.md](PLAN.md),
[docs/walkthrough-m1.md](docs/walkthrough-m1.md) for how the code is organised, and
[docs/manual-tests.md](docs/manual-tests.md) for the checks that need a person.

What it does: shows every mod (any package format) with compatibility warnings; edits each profile's load order
(drag & drop); duplicates and renames profiles; saves and shares load-order presets; backs up before every change,
with one-click restore; installs and uninstalls mods; finds duplicate and outdated copies; lets you tag and annotate mods;
copies load orders between profiles; launches the game through Steam. It never writes while the game is running.

Dispatch is not affiliated with or endorsed by SCS Software. "Euro Truck Simulator 2"
is a trademark of SCS Software.

## Build

Requires the .NET 10 SDK.

```
dotnet build
dotnet test
dotnet run --project src/Dispatch.App
```

Dispatch's own data (settings, cache, logs, presets, **backups**) lives in `%LOCALAPPDATA%\Dispatch`.

Release build (tests → self-contained single-file exe → installer if Inno Setup 6 is installed):

```
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

## Licence

Public domain — see [LICENSE](LICENSE) (The Unlicense). Third-party packages keep their
own licences, listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
