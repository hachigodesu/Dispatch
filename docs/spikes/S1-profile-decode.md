# S1: Decoding profile.sii (ScsC / BSII)

**Date:** 2026-11-12 · **Game:** 1.62.2.1 · **Result:** ✅ pass

## What was run
`Dispatch.Spike profiles` copied every `profile.sii` (7 local profiles and 1 Steam Cloud profile) and
164 save files (`info.sii` of every save, plus the newest `game.sii`) into `spike-output/`, then decoded the copies.

## Results
| Input | Count | Layers found | Decoded |
|---|---:|---|---:|
| `profile.sii` (local) | 7 | ScsC → **text** | 7/7 |
| `profile.sii` (Steam Cloud, `userdata\<id>\227300\remote\profiles`) | 1 | ScsC → **text** | 1/1 |
| `save\*\info.sii` | 163 | ScsC → text | 163/163 |
| newest `game.sii` (690 KB) | 1 | ScsC → **BSII v3** | 1/1: 19,677 units, 8.0 M chars, 0 trailing bytes |

- AES key and ScsC header layout confirmed: every file decrypted and inflated.
- The BSII decoder handled 32 distinct value types on real data (0x01–0x3D, including placements 0x19/0x1A with the v2+ bias), with no unknown types.
- For every profile, `profile_name` decodes to exactly the name stored in the hex folder name, including CJK names.
- `active_mods: N` always equals the number of `active_mods[i]` entries. Workshop entries appear as `mod_workshop_package.<16 hex>|Name`.
- `user_profile` fields seen: face, brand, map_path, logo, company_name, male, cached_experience, cached_distance,
  user_data, active_mods, customization, cached_stats, cached_discovery, version, online_user_name, online_password,
  profile_name, creation_time, save_time. No unique ID field exists, which supports "only change `profile_name`" for S4.

## How the game writes text SII
These are the conventions Dispatch's writer must follow:
- **CRLF** line endings; unit header `class : id {`; attributes indented by one space.
- **Non-ASCII characters are written as UTF-8 byte escapes**: `profile_name: "\xe6\x97\xa5\xe6\x9c\xac"` (日本).
  Raw UTF-8 is tolerated when reading, but writing uses escapes like the game.
- Nameless IDs: `_nameless.221.4e89.3458` (hex in 16-bit groups).
- `g_save_format "0"` (default) gives encrypted **text** profiles and encrypted **BSII** for `game.sii`.

## Load-order direction
`active_mods[0]` in the ProMods profile is `promods-eu-dlcsupport`, and index 7 is `promods-eu-def`. ProMods'
recommended order (as I understand it; not re-checked against their site) puts the definition package at the
**top** and DLC support at the **bottom**. The Middle-East add-on, which must sit above ProMods Europe, also has
*higher* indices (8–9).

**Conclusion (strong evidence):** `active_mods[0]` = bottom of the in-game list (lowest priority),
and the last index = top (highest priority). Dispatch's codec maps UI index *i* to file index *N−1−i*.
This still gets a final confirmation in S3 (in-game).

## Pending
- Oracle comparison with TheLazyTomcat's SII_Decrypt. It needs downloading SII_Decrypt.exe (MPL-2.0) and running it locally on
  the same copies. Self-consistency checks above already pass; the oracle run is optional.
