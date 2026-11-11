# S2: Does the game accept a plain-text profile.sii?

**Date:** 2026-11-12 · **Game:** 1.62.2.1 · **Profile:** `DispatchTest` (local) · **Result:** ✅ accepted

## Setup
1. `DispatchTest` was created in-game. Its three mods were ordered top → bottom: High_Detail_Vegetation,
   Man TGX blinkers, Iveco Sway sound. That gave file order `[0]` Iveco, `[1]` Man TGX, `[2]` Vegetation (see S3).
2. The profile had been created with Steam Cloud enabled. Unticking "Use Steam Cloud" in-game **moved the
   whole profile** into `profiles\` (saves, album, controls, everything) and removed both the
   `steam_profiles\` part and Steam's `remote` copy.
3. With the game closed, `s2-write DispatchTest`:
   - backed up `profile.sii` + `profile.bak.sii` to `%LOCALAPPDATA%\Dispatch\backups\Local\<hex>\…_spike-s2\`;
   - wrote `profile.sii` as **plain text** (`SiiNunit`, CRLF, game-style escaping) with the file order reversed;
   - left `profile.bak.sii` untouched (still the game's encrypted copy with the *old* order).

## Result
- In the game's Mod Manager, the order was **Iveco, Man TGX, Vegetation** (top → bottom), which is exactly
  the reversed order. The game read our plain-text file.
- No warning or "damaged profile" message, and the game didn't fall back to the stale `profile.bak.sii`.

## Observation: selecting the profile without saving
The first session only selected the profile, opened the Mod Manager without changing it, and quit.
The game wrote **only `config.cfg` and `config_local.cfg`**. `profile.sii` (still our plain text) and
`profile.bak.sii` were left untouched, and so were all save files. So the game doesn't rewrite `profile.sii` just
because the profile was opened.

## After a real save
Second session: loaded into the world, saved to slot 1, then quit.

| Time | File | What the game did |
|---|---|---|
| 03:48:47 | `profile.bak.sii` | Refreshed at world load: encrypted, **with our new order** |
| 03:49:29 | `save/1/*`, `tutorial_hint_data.sii` | Manual save |
| 03:49:33 | `profile.sii`, `save/autosave/*`, `config*.cfg` | Written on quit: `profile.sii` **re-encrypted** (ScsC → text), **our order kept** |

## Conclusions for Dispatch
1. **Write plain text.** The game accepts it, adopts the edit fully, and re-encrypts it on its next save.
   Re-detect the format on every read; never assume the file is still plain text.
2. **Leave `profile.bak.sii` alone** (back it up, don't modify it). The game refreshes it from the live profile
   when the world loads, so a stale `.bak` caused no problems.
3. Opening a profile without playing doesn't rewrite `profile.sii`. Playing does, on quit. So the
   concurrent-change check in `SafeWriter` (mtime + hash since load) is needed: the game can replace the
   file any time it was running in between.
