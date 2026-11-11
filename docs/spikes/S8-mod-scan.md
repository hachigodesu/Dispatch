# S8: Package readers on real data

**Date:** 2026-11-12 · **Game:** 1.62.2.1 · **Result:** ✅ pass

## What was run
`Dispatch.Spike mods` opened every mod in `Documents\Euro Truck Simulator 2\mod` and every Workshop item
read-only (shared read, no copies needed). For each it parsed `manifest.sii` (and `versions.sii` for Workshop),
checked the icon and description file, and computed a compatibility verdict. Full table: `spike-output/mods-report.md`.

## Results
| Source | Format (by header) | Mods | Manifest parsed |
|---|---|---:|---:|
| local | ZIP | 184 | 184 |
| local | HashFS v1 | 28 | 28 |
| local | HashFS v2 | 10 | 10 |
| local | Opaque `AEM!` | 5 | — |
| local | Opaque `MZ…` (forged ZIP structures) | 1 | — |
| Workshop | Folder (`versions.sii` + `universal\`) | 3 | 3 |

- **225/225 readable mods parsed**, 0 reader errors.
- **Speed:** full scan of 231 mods (~50 GB) in **1.0–1.9 s** (median 0.6 ms per mod, max 201 ms). This beats the < 15 s cold target without a cache.
- **Compatibility vs the game's own log** (`[mods] Incompatible mod - …`): **225/225 agree** for readable mods.
  The only 3 disagreements are opaque mods, which Dispatch can't read but the game can.
- `compatible_versions`: all 131 patterns in this data set use a trailing `*`. The rule for patterns
  **without** a wildcard (`"1.62"`) is still unverified by real data, so it stays ❓.

## Data-quality findings (lint rules for M1)
- Icons: 212 are exactly 276×162 JPEG, 7 aren't JPEG, 1 is missing, 11 mods declare none.
- 1 manifest without `display_name` (allowed).
- Invalid category value `maps` (5 mods); valid is `map`.
- Typo'd/unknown attributes: `compatible_version[]` (4 mods, ignored by the game, which the log confirms), `dependencies[]` (1).
- `dlc_dependencies` in use (e.g. `dlc_north, dlc_east, …`).

## DLC detection
`<install>\dlc_*.scs` (106 files here, e.g. `dlc_north.scs`) gives the DLC package names. Steam only downloads
owned DLC depots, so this list equals owned DLC. ✅ Used for the "missing DLC" warning.

## HashFS v2
Confirmed on all 10 real v2 archives:
- Header: magic, version 2, salt, `CITY`, entry count, entry-table length, metadata count, metadata-table length,
  entry-table offset (u64), metadata-table offset (u64). Both tables are zlib streams near the end of the file.
- Entry: hash u64, metadata index u32, metadata count u16, flags u16 (bit 0 = directory).
- Metadata header word: `type << 24 | absoluteWordIndex`. Type 0x80 = plain file, 0x81 = directory.
- Main metadata, 4 words: `[compressedSize:28 | flags:4]`, `[size:28 | ?:4]`, `[unknown]`, `[offset / 16]`.
- Directory listing: u32 count, `count` length bytes, then the names; a `/` prefix marks a subdirectory.
- CityHash64 (2011) of `""` = `9AE16A3B2F90404F` and of `manifest.sii` = `B97FFF7CE7377C95`. Both were found in real archives.

Pending: oracle comparison with sk-zk/Extractor file lists (optional; reads already succeed).

## Opaque packages (S9 preview)
- **The game can read them**: it flags 3 of the 6 as incompatible in `game.log.txt`, so it reads their manifests.
- `vanemap.scs` (`MZ…` header) is deliberately booby-trapped: high-entropy data, 20,073 fake `PK\x03\x04`
  signatures in the first 4 MB, a forged end-of-central-directory and ZIP64 trailer, and a forged central
  directory that even lists a `manifest.sii` at an impossible offset. Dispatch now rejects this safely.
- `AEM!` files (5) have an unknown header and look encrypted.
- These are **protection schemes chosen by the mod authors**. Decrypting them would mean circumventing that
  protection, so S9 continues only with non-circumventing sources of metadata (see PLAN.md §7 S9).
