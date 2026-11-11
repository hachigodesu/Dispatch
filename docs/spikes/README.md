# Milestone 0 spikes

All spikes run through `Dispatch.Spike`. The S1/S8 commands read the real game folders **read-only**: files are
copied into `spike-output/` (git-ignored, because it contains decoded personal profile data) and only the copies
are processed. The in-game spike commands are the only ones that write, and only to local profiles named
`DispatchTest…`. They refuse to run while the game is open and back up first.

```
dotnet run --project src/Dispatch.Spike -- profiles   # S1
dotnet run --project src/Dispatch.Spike -- mods       # S8
dotnet run --project src/Dispatch.Spike -- probe <archive.scs> [path ...]   # HashFS diagnostics
dotnet run --project src/Dispatch.Spike -- roundtrip  # decoded profiles re-encode byte-identically (8/8)
```

The in-game spikes (S2–S4, S7) are in [in-game-checklist.md](in-game-checklist.md).

| Spike | Topic | Status | Report |
|---|---|---|---|
| S1 | ScsC / BSII decoding | ✅ Done (oracle comparison pending) | [S1-profile-decode.md](S1-profile-decode.md) |
| S2 | Game accepts plain-text profile.sii | ⏳ Ready: needs in-game check | [in-game-checklist.md](in-game-checklist.md) |
| S3 | UI order ↔ `active_mods[i]` | 🟡 Strong evidence; ready for in-game check | [S1](S1-profile-decode.md#load-order-direction), [checklist](in-game-checklist.md) |
| S4 | Duplicate/rename field changes | ⏳ Ready: needs in-game check | [in-game-checklist.md](in-game-checklist.md) |
| S5 | `g_save_format` | ✅ Documented (PLAN.md §5.0) | — |
| S6 | Steam Cloud write safety | ⏳ Needs throwaway cloud profile | — |
| S7 | Profile name rules | ⏳ Ready: needs in-game check | [in-game-checklist.md](in-game-checklist.md) |
| S8 | Package readers on real data | ✅ Done | [S8-mod-scan.md](S8-mod-scan.md) |
| S9 | `AEM!` / `MZ` packages | ✅ Closed: no decryption; names from profiles' `active_mods` (2 of 6), the game's log verdicts, else the file name. The game keeps no mod-info cache | [S8-mod-scan.md](S8-mod-scan.md#opaque-packages-s9-preview) |
| S10 | HashFS v2 details | ✅ Layout confirmed on 10 real archives (oracle comparison pending) | [S8-mod-scan.md](S8-mod-scan.md#hashfs-v2) |
| S11 | gong-wpf-dragdrop on .NET 10 | ⏳ M1 | — |
