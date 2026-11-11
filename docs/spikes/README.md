# Milestone 0 spikes

All spikes run through `Dispatch.Spike` and read the real game folders **read-only**: files are copied into
`spike-output/` (git-ignored, because it contains decoded personal profile data) and only the copies are processed.

```
dotnet run --project src/Dispatch.Spike -- profiles   # S1
dotnet run --project src/Dispatch.Spike -- mods       # S8
dotnet run --project src/Dispatch.Spike -- probe <archive.scs> [path ...]   # HashFS diagnostics
```

| Spike | Topic | Status | Report |
|---|---|---|---|
| S1 | ScsC / BSII decoding | ✅ Done (oracle comparison pending) | [S1-profile-decode.md](S1-profile-decode.md) |
| S2 | Game accepts plain-text profile.sii | ⏳ Needs in-game check (`DispatchTest`) | — |
| S3 | UI order ↔ `active_mods[i]` | 🟡 Strong evidence, needs in-game check | [S1-profile-decode.md](S1-profile-decode.md#load-order-direction) |
| S4 | Duplicate/rename field changes | ⏳ Needs in-game check | — |
| S5 | `g_save_format` | ✅ Documented (PLAN.md §5.0) | — |
| S6 | Steam Cloud write safety | ⏳ Needs throwaway cloud profile | — |
| S7 | Profile name rules | ⏳ Needs in-game check | — |
| S8 | Package readers on real data | ✅ Done | [S8-mod-scan.md](S8-mod-scan.md) |
| S9 | `AEM!` / `MZ` packages | 🟠 Started, decision needed | [S8-mod-scan.md](S8-mod-scan.md#opaque-packages-s9-preview) |
| S10 | HashFS v2 details | ✅ Layout confirmed on 10 real archives (oracle comparison pending) | [S8-mod-scan.md](S8-mod-scan.md#hashfs-v2) |
| S11 | gong-wpf-dragdrop on .NET 10 | ⏳ M1 | — |
