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

## M0 status: exit criterion met (2026-11-12)
Every open question in PLAN.md §5 is resolved or has an accepted fallback. The #1 risk (does the game accept
a plain-text profile.sii?) is retired: yes, it adopts the edit and re-encrypts on its next save.

Accepted fallbacks (unverified because no real data uses them):
- `compatible_versions` without `*`: prefix rule, cross-checked against the game's own log verdicts.
- HashFS with a non-zero salt: salted lookup, then retry unsalted. All 38 real archives have salt 0.
- 20-character name limit for CJK names: counted as characters.

Optional, not blocking M1: S6 (direct Steam Cloud writes; the MVP keeps cloud profiles read-only) and the oracle
comparisons against SII_Decrypt and Extractor. S11 (drag & drop on .NET 10) runs at the start of M1.

| Spike | Topic | Status | Report |
|---|---|---|---|
| S1 | ScsC / BSII decoding | ✅ Done (oracle comparison pending) | [S1-profile-decode.md](S1-profile-decode.md) |
| S2 | Game accepts plain-text profile.sii | ✅ Accepted; the game re-encrypts on its next save, keeping our edit | [S2-plain-text-write.md](S2-plain-text-write.md) |
| S3 | UI order ↔ `active_mods[i]` | ✅ Confirmed in-game: index 0 = bottom | [S1](S1-profile-decode.md#load-order-direction) |
| S4 | Duplicate/rename field changes | ✅ Only `profile_name` needs changing; saves don't reference the profile | [S4-duplicate-profile.md](S4-duplicate-profile.md) |
| S5 | `g_save_format` | ✅ Documented (PLAN.md §5.0) | — |
| S6 | Steam Cloud write safety | ⏳ Needs throwaway cloud profile | — |
| S7 | Profile name rules | ✅ ≤ 20 characters, no emoji, Windows-reserved characters allowed | [S7-profile-name-rules.md](S7-profile-name-rules.md) |
| S8 | Package readers on real data | ✅ Done | [S8-mod-scan.md](S8-mod-scan.md) |
| S9 | `AEM!` / `MZ` packages | ✅ Closed: no decryption; names from profiles' `active_mods` (2 of 6), the game's log verdicts, else the file name. The game keeps no mod-info cache | [S8-mod-scan.md](S8-mod-scan.md#opaque-packages-s9-preview) |
| S10 | HashFS v2 details | ✅ Layout confirmed on 10 real archives; all real archives use salt 0 (oracle comparison optional) | [S8-mod-scan.md](S8-mod-scan.md#hashfs-v2) |
| S11 | gong-wpf-dragdrop on .NET 10 | ⏳ M1 | — |
