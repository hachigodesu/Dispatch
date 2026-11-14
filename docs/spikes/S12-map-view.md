# S12: Map view ("which roads does this map mod add?")

**Date:** 2026-11-14 · **Game:** ETS2 1.62, map format 907 · **Result:** ✅ feasible, read-only, no new dependencies ·
**Built** the same day (PLAN.md "After 0.8.0"); the numbers below are from the spike, see "Built" at the end for the
final reader's.

## Question
Can Dispatch show, for a map mod, the roads it adds to the game, drawn against the map the player already has?

## What was run
A throwaway console spike (outside the repo, referencing `Dispatch.Core` for the existing ZIP/HashFS readers) opened
the base game's `base_map.scs`, every `dlc_*.scs` and all 229 local mods **read-only**, parsed every sector `.base`
file it could find, and rendered PNGs: the map below the mod in grey, roads the mod adds in red, and roads that exist
below but disappear once the mod's sector files replace them in blue (dashed).

## How the map is stored (public description: [sk-zk/map-docs wiki](https://github.com/sk-zk/map-docs/wiki))
- `map/europe.mbd` is the map root; sectors are 4000 × 4000 m cells in `map/europe/sec±XXXX±ZZZZ.{base,aux,snd,data,desc,layer}`.
  ATS uses the same format (`game_id` is `euro2` in both) with map `usa`; not tested here.
- **A mod's sector file replaces the whole file of the same name** from anything below it. There is no merge. So
  "what a mod adds" is a diff, per sector, against what the game would load without it.
- `.base` = header (u32 map version, u64 token `euro2`, u32 `3`), `u32 item_count` + items, `u32 node_count` + nodes,
  `u32 count` + u64 UIDs. Roads, prefabs (junctions), cities, companies, ferries are all in `.base`.
- Items have **no length prefix**, and each of the 36 item types has its own, version-dependent layout. A full parser
  must know all of them for every map version.

## The trick that avoids per-version item layouts
1. **Nodes from the end of the file.** Node records are a fixed 56 bytes (u64 uid, 3 × s32 position / 256,
   quaternion, u64 backward item, u64 forward item, u32 flags). Working back from the end (`count` of trailing UIDs,
   then `node_count` + nodes) finds the node table without reading a single item.
2. **Items by their header.** Every item starts with `u32 type, u64 uid`. Scanning for `type = 3` (road),
   `4` (prefab) or `12` (city) followed by a UID that the nodes refer to finds them with essentially no false positives.
3. **A road's ends come from the nodes**, not the road: the node whose forward item is the road is its start, the
   node whose backward item is the road is its end. The curve is a cubic Hermite between them along each node's
   direction. Confirmed on real data: road items are 269 bytes with the node UIDs at +249/+257, cities have their
   name token at +57 and their node at +73.

Only the node record and the item header have to stay stable across game versions; both have been unchanged for
years (all sectors from map version 895 to 907 parse).

## Results
| Source | Archives with sectors | Sectors parsed | Roads | Time |
|---|---:|---:|---:|---:|
| `base_map.scs` + 13 DLCs with map data | 14 | 1,174 / 1,174 | 190,539 | 11 s |
| Local mods (229 files) | 139 | 8,742 / 8,908 | 677,640 | ProMods map: 13 s, typical mod < 0.3 s |

- **Not readable, by design:** 5 opaque/protected packages (`AEM!`, e.g. Altai Map, M12 Vostok, NNMap) and 1 with a
  forged ZIP directory (vanemap). Same rule as S9: Dispatch doesn't circumvent protection; the map view would say
  "this mod is protected, its roads can't be shown".
- **Not parsed (166 sectors):** sectors in map format ≤ 889 (Australia Tasmania 5.3 map, Canada 0.2.5 map, Ireland 0.1.1, 15 old
  ProMods sectors). The node record was different back then. All of these mods also ship a newer "FIX" part.
- **Examples rendered** (images not committed: they're of third-party mod content):
  - *Abkh Map 1.8*: 8 new sectors, 1,380 roads / 81 km, cities Sochi, Gagra, Pitsunda, Gudauta, Poti, Kutaisi.
    Standalone against base + DLCs: it floats in empty space, because it connects to other map mods.
  - *Skandinavien Södra 1.3.2*: replaces 12 sectors: +223 roads, −230 roads. The removed ones sit in Göteborg's
    harbour: the mod was built against an older base map and silently reverts it.
  - *Portugal Rebuild 0.1.8* on top of ProMods 2.83: replaces 30 sectors, +2,393 roads (206 km), −1,276 roads. The
    removals around Vigo and the Algarve are ProMods roads that disappear when this add-on is loaded above ProMods.

## What this means for a feature
The **diff** is the valuable part, and it's something Dispatch can do that the game and existing map tools don't:
it knows the profile's load order, so it can draw a mod against exactly what is loaded below it (base + the DLCs the
player has + lower map mods) and show both what the mod adds and **what it removes**. Removed roads are a strong
signal of a version mismatch or a missing connector, which is what breaks big map combos.

Proposal (not started):
- **Core** `Maps/`: `SectorReader` (the heuristic above), `MapStack` (base + DLCs + mods in load order, later wins
  per sector path), `MapDiff` (added/removed roads, prefabs and cities per mod, plus sectors replaced by more than one
  active map mod). Cache per archive, keyed by path + size + mtime + map version, as compact endpoints + tangents
  (ProMods ≈ 13 MB), so a second view is instant.
- **App:** a "Map" window (not a message box) opened from a map mod in the Mods tab: pan/zoom, base layer in grey
  (pre-rendered to bitmap tiles for speed), the mod's added roads in colour, removed roads dashed, city labels, and a
  short summary ("adds 206 km of roads and 12 cities; removes 1,276 roads from ProMods in 30 sectors"). Later: a
  whole-profile view colouring each map mod, and sector clashes between map mods on the Conflicts panel.
- **Tests:** generated sectors in `Fixtures` (nodes + road/prefab/city items, plus junk items between them), a
  self-test step and a new screenshot tab.

## Open points
- **Junction shapes:** prefabs are drawn as spokes from their centre to their nodes, which closes the gaps but isn't
  pretty. Proper shapes need the prefab descriptors (`.ppd`) from `base.scs`; their format is a separate small spike.
- **Road classes** (motorway / main / local, for line width): the road's `road_look` field is a token, but at the
  offset the wiki's field order implies it decoded to nonsense on 1.62 data. Needs a closer look, or classify from
  `road_look.template.sii` via another field.
- **City display names:** sectors hold tokens (`goteborg`); the localized names are in `def/city.*.sii`, which
  Dispatch's SII reader can already parse.
- **No landmass/coastline:** the game's own UI map is a texture, which Dispatch can't read (v2 texture chunks).
  Roads alone are readable enough, as the renders show.
- **DLC ownership:** `dlc_*.scs` files are present only for owned DLCs, so the base layer is naturally the player's.
- **Licences:** TruckLib (GPL-2) and truckermudgeon/maps (GPL-3) parse this format but must not be copied (PLAN.md
  §7, licence contamination). The spike was written from the wiki's description only, like the HashFS reader.
- **Format churn:** map version bumps roughly once a year (907 covers 1.59–1.62). The heuristic depends only on the
  node record and the item header, so it should survive most bumps; the self-test would catch one that doesn't.

## Built (2026-11-14)
`Core/Maps`: `SectorReader`, `MapArchiveReader` (sectors + city names from `def/city/*.sui` and `def/city*.sii`),
`MapCache`, `MapComparison`; App: `MapViewModel`, `MapWindow`, `MapCanvas`. Differences from the spike:
- **Node table search, largest count first.** The spike tried the smallest count first and accepted "0 nodes" whenever
  the last node's flags were 0, silently dropping roads. The final reader finds **257,974** roads in the base game and
  DLCs (spike: 190,539) and the same counts as the spike everywhere else. 510 mod sectors are unreadable: the 166
  old-format ones above plus 344 in a package with a forged ZIP directory (protected; refused like S9).
- **Separate maps.** Some mods are a map of their own (`map/grandutopia.mbd`, RIW's `very_africa`), played instead of
  the game's map. A mod without sectors for the game's map is shown against mods for its own map only.
- **City labels** use the def files' `city_name` (any layer; the mod's own wins); unnamed tokens with digits are false
  positives and are dropped.
- **Speed** (110 game archives + 229 mods): cold 5.2 s + 10.8 s, cached 0.75 s + 1.7 s, 49 MB of cache. Rendering
  runs on a background STA thread; while it runs, the last bitmap is moved/scaled with the mouse.
