# S4: Duplicating a profile

**Date:** 2026-11-12 · **Game:** 1.62.2.1 · **Result:** ✅ pass

## What was run
`s4-duplicate DispatchTest DispatchTest2` with the game closed:
1. Refused up front if a profile with the new name existed (local or `steam_profiles`). A second run was refused as expected.
2. Copied the whole folder (saves, album, controls, everything) to a non-hex staging folder
   (`profiles\.dispatch-staging-<guid>`), which the game ignores.
3. Changed **only `profile_name`** in `profile.sii` and `profile.bak.sii` (written as plain text, as in S2).
4. Re-checked that the game was closed, then renamed the staging folder to `44697370617463685465737432` in one move.

A file-by-file comparison afterwards: only `profile.sii` and `profile.bak.sii` differ; every other file is byte-identical.

## In-game result
- Both `DispatchTest` and `DispatchTest2` appear in the profile list with their own names.
- `DispatchTest2` has the copied save, the same mods in the same order, and the same truck and money.
- After driving and saving in `DispatchTest2`, `DispatchTest` was unaffected. Each profile wrote only its
  own files, and the game re-encrypted both `profile.sii` files with the correct `profile_name`.

## Do saves reference the profile?
No. A decoded copy of `DispatchTest2`'s autosave (`info.sii` text, `game.sii` BSII, 5.7 M chars) contains neither
the profile name nor the folder's hex name. `info.sii` holds the save's own `name`, time, version and mod `dependencies`.

## Conclusions for Dispatch
- Duplicate = copy the folder, then change `profile_name` only. **No other field needs to change**: there is no
  unique ID, and the copied `creation_time` caused no problems, so Dispatch keeps it as-is.
- Staging under a non-hex name plus a single final move works. The game never saw a half-copied profile.
- Rename (M3) uses the same mechanism.
