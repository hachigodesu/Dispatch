# Dispatch: Plan

> A mod manager for Euro Truck Simulator 2
> **Status:** approved 2026-11-12. Implementation goes milestone by milestone, starting with Milestone 0 (spikes). No git commands are run without asking first.

**Your decisions:** C# + WPF · new to the stack (so the plan explains patterns and keeps the architecture simple) · installer · leave room for ATS · read-only access to your real ETS2 files for verification · mostly local profiles · keep the last 20 small backups and 5 full-profile backups per profile · English UI, with strings in resx files so it can be translated later · **The Unlicense** · local git · unsigned builds for now. All decisions are listed in §10.

**Sources:** facts marked ✅ were checked read-only on your PC (2026-11-12, game **1.62.2.1**). Facts marked 🌐 come from modding.scssoft.com, SCS/Steam forums and open-source tools; links are in §11. Facts marked ❓ are still open and get a spike in §7.

---

## 1. Summary: problem and goals

ETS2's built-in Mod Manager only works in-game and per profile. It has no presets, no backups, no way to copy a profile together with its load order, and no early warning about incompatible mods. Large mod setups (your PC has **228 local mods and 3 Workshop items**, many of them multi-part maps such as ProMods, RusMap and RPM) make rebuilding load orders slow and error-prone.

**Dispatch** is a Windows desktop app that:
- finds the game, its user-data folder, Steam libraries and Steam accounts automatically;
- reads every mod, whatever its package format, and shows its name, icon, description, version and compatibility;
- edits each profile's active mods and load order outside the game;
- duplicates and renames profiles;
- saves, exports and imports load-order presets;
- backs up before every write, and never writes while the game is running.

**Safety rules (built into the architecture, not left to each feature)**
1. Nothing is written to game or profile files without a timestamped backup first.
2. Nothing is written while `eurotrucks2.exe` is running. This is checked before an operation starts *and* again right before each commit.
3. Duplicate and rename never overwrite an existing profile folder.
4. Writes are atomic (temp file → verify → swap), so a crash can't leave half a `profile.sii`.
5. Dispatch never modifies the game's own update backups (`profiles(1.62.2.1s).bak` …) or Workshop content.

**Non-goals (for now):** Linux, macOS, Steam Deck/Proton · downloading mods · editing mod contents · TruckersMP sync.

---

## 2. Tech stack

| Concern | Choice | Why |
|---|---|---|
| Runtime | **.NET 10 LTS** (`net10.0-windows`, supported until Nov 2028) | .NET 8 and 9 reached end of support on 2026-11-10. .NET 11 is a short-term release. |
| UI | **WPF** | The most mature Windows UI: solid drag & drop and lots of learning material. A modern look comes from the built-in Fluent theme (.NET 9+) or the WPF-UI library. |
| MVVM | **CommunityToolkit.Mvvm 8.4** (MIT) | Source generators (`[ObservableProperty]`, `[RelayCommand]`) remove most of the MVVM boilerplate. |
| DI, settings, logging | **Microsoft.Extensions.Hosting** + **Serilog** (rolling log file) | Standard and lightweight. Logs make it possible to debug other people's setups. |
| Drag & drop reordering | **gong-wpf-dragdrop 4.x** (BSD-3) | De-facto standard. Not explicitly tested on .NET 10, so this is checked in M1. |
| VDF/ACF parsing | **ValveKeyValue** (MIT, ValveResourceFormat org) | Maintained. Reads `libraryfolders.vdf`, `appmanifest_*.acf`, `loginusers.vdf`. |
| SII text, ScsC decrypt, BSII decode | **Own implementation** in Dispatch.Core, written from the published format docs. zlib via `System.IO.Compression.ZLibStream`. | No permissively licensed C# library both reads and writes these formats (see licence notes below). The formats are small and well documented. |
| ZIP mods | **Own tolerant ZIP reader**: reads the central directory, ignores corrupted local headers, supports ZIP64, inflates with `DeflateStream` | "Locked" mods deliberately corrupt their ZIP headers. `ZipArchive` and SharpZipLib reject them; the game doesn't. |
| HashFS v1/v2 | **Own reader** written from the documented layout (§5.3), plus **CityHash64 (2011 variant)** | Same licence reason. Only manifests, icons, descriptions and the file list are needed, not textures (GDeflate). |
| File-system abstraction | **System.IO.Abstractions** (MIT) | Tests run against fake Documents/Steam trees in memory. |
| Tests | **xUnit v3 + Shouldly** | FluentAssertions 8+ is no longer free for commercial use. |
| Installer | **Inno Setup 6**: per-user install to `%LOCALAPPDATA%\Programs\Dispatch`, no admin rights needed, self-contained .NET publish | The uninstaller **keeps `%LOCALAPPDATA%\Dispatch` (backups!)** unless you tick a box. |

**Licences of reference code.** This decides what we may copy.
- sk-zk/**TruckLib** (incl. `TruckLib.HashFs`, on NuGet) and sk-zk/**Extractor**: **GPL-2.0**. Using them as a library or porting their code makes Dispatch GPL-2.0.
- TheLazyTomcat/**SII_Decrypt**: **MPL-2.0**. Ported files stay MPL, the rest of the app is unaffected. Its `Binary SII - Format.txt` spec is the main BSII reference.
- **SIIDecryptSharp**: "MIT (Modified)", which requires modifications to be published. Also P/Invokes a native zlib and marks several BSII types as experimental. **Not used.**
- **decrypt_truck** (Rust): MIT. A clean reference for ScsC/BSII.
- Easy SCSModManager: GPL-3.0. Studied for behaviour only.

→ **Dispatch is released under The Unlicense (public domain).** So **no code is copied or ported** from GPL *or* MPL projects, because neither can be relicensed into the public domain. Everything in `Dispatch.Core` is our own implementation, written from format descriptions (facts about a file format aren't copyrightable). GPL/MPL tools are used only as **black-box oracles**: we run them externally in tests and compare outputs, and never vendor them into the repo. NuGet dependencies (MIT/BSD) remain separately licensed and are listed in `THIRD-PARTY-NOTICES.md`.

**Alternatives rejected**
- **WinUI 3**: unpackaged deployment, the designer and drag & drop are rougher, and there are far fewer beginner resources.
- **Avalonia**: cross-platform isn't needed, and its ecosystem is smaller.
- **Rust + Tauri**: a second language plus a web front-end, and none of the C# references can be reused.
- **Velopack** (installer + auto-update, MIT, good): it installs into `%LOCALAPPDATA%\<packId>` and removes that folder on uninstall. That clashes with keeping backups in `%LOCALAPPDATA%\Dispatch`, and you didn't ask for auto-update. It's easy to switch later (with a different pack ID).
- **MSIX**: needs signing, and the container makes access to Documents and the Steam folder awkward.

**Prior art studied (features and pitfalls, no code copied)**
- **Easy SCSModManager** (Python, GPL-3, active in 2026): writes plain-text `profile.sii`, backs up before every write, never deletes Workshop content, detects conflicts on overlapping `def/` files. Its history records the HMAC pitfall (§5.7).
- **ETS2ATS.ModlistManager** (C# WinForms, MPL-2.0): modlists per profile, bundles `SII_Decrypt.exe`.
- **Truck Tools** (Rust/Tauri): profile clone/rename, which copies everything except `album/` and changes only `profile_name`; it limits names to 20 characters.
- **Zoult/ets2-profile-renamer**, **TS SE Tool**: profile and save editing.

---

## 3. Architecture

Three layers. Only the top layer knows about WPF.

```
┌──────────────────────────────────────────────────────────────────┐
│ Dispatch.App  (WPF · MVVM)                                       │
│  Views (XAML)  ⇄  ViewModels  ⇄  app services (dialogs, theme)  │
│  Shell · Mods · ProfileEditor · DuplicateDialog · Presets ·     │
│  Backups · Settings · FirstRunWizard                            │
└────────────────▲─────────────────────────────────────────────────┘
                 │ async calls → domain objects + Result<T> (ok / warnings / errors)
┌────────────────┴─────────────────────────────────────────────────┐
│ Dispatch.Core  (no UI · unit-tested)                             │
│  Games/        GameDefinition (ETS2 now, ATS later)              │
│  Environment/  GameLocator · SteamLocator · SteamAccounts ·      │
│                GameVersionReader · DlcDetector                   │
│  Sii/          Text (lexer, parser, round-trip document) ·       │
│                Crypto (ScsC) · Binary (BSII decoder) · SiiCodec  │
│  Mods/         Packages (Folder · Zip · HashFsV1 · HashFsV2 ·    │
│                Opaque, picked by header) · Manifest · VersionsSii│
│                · ModScanner (+ cache) · Compatibility            │
│  Profiles/     ProfileRepository · ProfileDocument (surgical     │
│                edits) · LoadOrderService · ProfileCloner ·       │
│                ProfileNameCodec / Validator                      │
│  Presets/      PresetService (JSON)                              │
│  Safety/       GameProcessGuard · BackupService · SafeWriter     │
│  Platform/     IFileSystem · IRegistry · IProcessList ·          │
│                IKnownFolders · IRecycleBin · IClock              │
│                + Windows implementations                         │
└──────────────────────────────────────────────────────────────────┘
```

**How the parts talk to each other**
- ViewModels get Core services injected through interfaces, registered at startup.
- Expected problems (an unreadable mod, a missing DLC) come back as `Result` warnings and errors, not exceptions.
- **There is exactly one write path: `SafeWriter.Commit(plan)`.** A "plan" is a list of file operations prepared in memory. Commit then:
  1. asks `GameProcessGuard` and refuses if `eurotrucks2` is running;
  2. checks that the targets haven't changed since they were loaded (size, mtime and hash), and asks you to reload if they have;
  3. runs `BackupService.Backup(…)` (timestamped, recorded in the backup index);
  4. writes to temp files next to the targets, then re-reads and re-parses them to verify;
  5. **re-checks the game process**, then swaps files in (`File.Replace`) or moves folders into place;
  6. logs the result. On any failure it rolls back from the temp files and the backup.
- Mod scanning runs in the background with progress. Results are cached in `%LOCALAPPDATA%\Dispatch\cache\mods.json`, keyed by path, size and mtime, and the cache is invalidated when the Steam `buildid` changes. Readers only seek to headers and tables; your largest mod is 4.8 GB.
- `GameDefinition` holds everything that differs between games: app ID 227300 or 270880, exe `eurotrucks2` or `amtrucks`, Documents folder name, Workshop app ID.

**MVVM, briefly:** a *View* is XAML that binds to a *ViewModel*, a C# class with properties and commands. ViewModels call *services* in Core, which do the real work. Core never references WPF, so almost all the logic can be tested without opening a window. Each milestone starts with a short walk-through of the new patterns it uses.

---

## 4. Data model (Core)

```csharp
enum GameId { Ets2, Ats }
record GameDefinition(GameId Id, int SteamAppId, string ProcessName,
                      string DocumentsFolderName, string DisplayName);

record GameInstall(GameDefinition Game, string InstallDir, string ExePath,
                   GameVersion Version, string SteamLibraryPath, string SteamBuildId,
                   string UserDataDir,                       // …\Documents\Euro Truck Simulator 2
                   IReadOnlyList<SteamAccount> Accounts,
                   IReadOnlySet<string> OwnedDlc,            // e.g. "dlc_north" (detection: §5.2)
                   bool IsManualOverride);

record SteamAccount(uint AccountId, ulong SteamId64, string PersonaName,
                    bool IsActive, string RemoteDir);        // Steam\userdata\<id>\227300\remote

record GameVersion(int Major, int Minor, int Patch, int Build);  // 1.62.2.1

enum PackageFormat { Folder, Zip, HashFsV1, HashFsV2, Opaque }
enum ModSource { Local, Workshop }

record Mod(string PackageId,          // the part before '|' in active_mods:
                                      //  "promods-eu-map-v283" | "mod_workshop_package.00000000C3CC68CD"
           ModSource Source, ulong? WorkshopId,
           string Path, PackageFormat Format, long SizeBytes, DateTime LastWriteUtc,
           ModManifest? Manifest,     // null = missing or unreadable
           IReadOnlyList<ModSubPackage> SubPackages,   // Workshop versions.sii entries
           ModSubPackage? SelectedSubPackage,          // the one the game would use
           IReadOnlyList<ModIssue> Issues);            // e.g. typo'd field, bad icon size

record ModManifest(string? PackageVersion, string? DisplayName, string? Author,
                   IReadOnlyList<string> Categories, string? IconFile, string? DescriptionFile,
                   IReadOnlyList<string> CompatibleVersions, IReadOnlyList<string> DlcDependencies,
                   bool? MpModOptional, IReadOnlyDictionary<string,string> UnknownFields);

record ModSubPackage(string PackageName, string Path, IReadOnlyList<string> CompatibleVersions,
                     bool Informational, bool IsUniversal);

enum ProfileKind { Local, SteamCloud }
record Profile(string Name, string FolderName,     // UPPERCASE hex of UTF-8 name
               ProfileKind Kind,
               string MainDir,                     // where profile.sii + save\ live
               string? LocalPartDir,               // Cloud only: Documents\…\steam_profiles\<hex>
               SteamAccount? Owner, DateTime LastWriteUtc, int SaveCount, long SizeBytes);

record LoadOrder(IReadOnlyList<LoadOrderEntry> Entries);   // index 0 = TOP of the UI = highest priority
record LoadOrderEntry(string PackageId, string DisplayName, Mod? Resolved /* null = not installed */);

record Preset(int Schema, string Name, GameId Game, string? GameVersion, DateTime CreatedUtc,
              string? Notes, IReadOnlyList<PresetEntry> Mods);
record PresetEntry(string PackageId, string DisplayName, ulong? WorkshopId,
                   string? FileName, string? PackageVersion);

enum BackupReason { LoadOrderSave, Rename, Duplicate, PreRestore, Manual }
record Backup(string Id, DateTime CreatedUtc, string ProfileFolderName, ProfileKind Kind,
              BackupReason Reason, bool IsFullProfile, string Path, long SizeBytes,
              IReadOnlyList<string> Files);

enum CompatStatus { Ok, VersionMismatch, MissingDlc, NoVersionInfo, Unreadable }
record Compatibility(string PackageId, CompatStatus Status, string? Detail);

// later
record Conflict(ulong PathHash, string? Path, IReadOnlyList<string> PackageIds, string WinnerPackageId);
record ModNote(string PackageId, IReadOnlyList<string> Tags, string? Note);
```

**Dispatch's own data** in `%LOCALAPPDATA%\Dispatch\`:
```
settings.json              path overrides, chosen Steam account, UI prefs, retention (20 small / 5 full)
cache\mods.json, cache\icons\
backups\index.json
backups\<Local|Cloud>\<ProfileHex>\<yyyyMMdd-HHmmss-fff>_<reason>\…
presets\*.dispatch-preset.json
notes.json                 (v1)
logs\dispatch-<date>.log
```

---

## 5. File-format notes

### 5.0 Corrections and additions to the brief
| Brief said | Finding |
|---|---|
| "Steam Cloud profiles possibly in `steam_profiles\`" | ✅ **Only the machine-local part** (`controls.sii`, `config_local.cfg`, gearbox layouts) is in `Documents\…\steam_profiles\<hex>\`. **`profile.sii`, `config.cfg` and `save\`** are in `Steam\userdata\<accountId>\227300\remote\profiles\<hex>\`, which Steam syncs. A cloud profile belongs to one Steam account, and **your PC has several accounts**. |
| profile.sii is usually encrypted | ✅ All 7 local profiles are `ScsC`, **even with `g_save_format "0"`**. |
| "Find out whether the game accepts plain text back" | 🌐 Yes, according to several independent tools. 🌐 **Re-encrypting does not work**: the game checks the ScsC HMAC (its key is unknown), and on failure it **silently falls back to `profile.bak.sii`**, discarding the edit. To be confirmed in spike S2. |
| `g_save_format` | 🌐 Official values: 0 = default, 1 = binary, 2 = text, 3 = both (debug). It affects saves. Dispatch doesn't need to change it and never does. |
| Folder = "UTF-8 → lowercase hex"? | ✅ **UPPERCASE** hex of UTF-8 (`日本` → `E697A5E69CAC`). Accept either case when reading. |
| Workshop identifier "different format" | ✅ `mod_workshop_package.` + Workshop ID as **16-digit uppercase hex**: 3284953293 → `mod_workshop_package.00000000C3CC68CD`. |
| Load order: top = highest priority? | 🌐 In the game UI, yes. 🟡 **S1 evidence: `active_mods[0]` is the *bottom*** (lowest priority) and the last index is the top: your ProMods profile stores DLC support at index 0 and Definition at index 7. Final in-game confirmation in S3. |
| HashFS v2 since 1.50 | 🌐 Correct. The game still accepts ZIP and folders. ✅ Your mods: 184 ZIP, 28 HashFS v1, 10 HashFS v2, and **6 in an unknown format** (`AEM!` ×5, `MZ` ×1). |
| Game version from files or logs | ✅ **`eurotrucks2.exe` FileVersion = `1.62.2.1`**, which is the best source. |
| — | ✅ The game makes its own versioned backups (`profiles(1.62.2.1s).bak`, listed in `backups.txt`). Dispatch shows them read-only as a recovery source. |
| — | ✅ `game.log.txt` contains the game's own verdicts (`[mods] Incompatible mod - <name>`, `Ignored unknown attribute compatible_version[]`). Used to cross-check and later as "game says" hints. |

### 5.1 Paths and detection
| What | How |
|---|---|
| Documents | `SHGetKnownFolderPath(FOLDERID_Documents)`, which follows OneDrive redirection ✅. Watch out for OneDrive *online-only* placeholder files: check attributes and warn before reading. |
| User data | `<Documents>\Euro Truck Simulator 2\`: `mod\`, `profiles\`, `steam_profiles\`, `config.cfg`, `game.log.txt`, `mods_info.sii`, `backups.txt` ✅. 🌐 The game's `-homedir "<parent>"` launch option moves it; detect it from Steam's `userdata\<id>\config\localconfig.vdf` (LaunchOptions for 227300), with manual override. |
| Steam | `HKCU\Software\Valve\Steam\SteamPath` (forward slashes, lowercase, so normalize) ✅ → fallback `HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath` ✅ |
| Libraries | `<Steam>\steamapps\libraryfolders.vdf` ("libraryfolders" → `path` + `apps`) ✅. The `apps` list can be stale, so confirm with `appmanifest_227300.acf` 🌐. Fall back to `config\libraryfolders.vdf`. |
| Install | `appmanifest_227300.acf` → `installdir`, `buildid` ✅ → `…\common\Euro Truck Simulator 2\bin\win_x64\eurotrucks2.exe` ✅ |
| Workshop | `<lib>\steamapps\workshop\content\227300\<id>\` ✅ + `workshop\appworkshop_227300.acf` (installed items) |
| Steam account | `HKCU\Software\Valve\Steam\ActiveProcess\ActiveUser` (0 = Steam not running) ✅ → fallback `config\loginusers.vdf` `MostRecent`. Account ID = SteamID64 − 76561197960265728. An account picker is shown when there is more than one. |
| Game running | `Process.GetProcessesByName("eurotrucks2")` (+ `amtrucks` for ATS) |
| Non-Steam install | 🌐 Still exists (SCS standalone). Manual install path in Settings. |

### 5.2 Game version and DLC
1. ✅ **`eurotrucks2.exe` FileVersion** `1.62.2.1 (4c8b313…)`: primary source, works with the game closed.
2. ✅ `game.log.txt`: `[ufs] Loaded pack set version 1.62.2.1` (🌐 older builds: `init ver.1.48.1.6s`; strip the `s`). Fallback only; it may be stale.
3. ✅ `buildid` in the ACF: change detection only.

**`compatible_versions` matching** 🌐: a `*` is allowed only at the end. `"1.62.*"` matches 1.62.x and 1.62.x.y. Without `*`, the prefix must match exactly ❓ (checked in S8 against the game's own log verdicts). Workshop mods use `versions.sii`, not the manifest's `compatible_versions`.

**Owned DLC** ✅ (S8): list `dlc_*.scs` in the install directory (`dlc_north.scs` → `dlc_north`; 106 on your PC). Steam only downloads owned DLC. Mods declare these package names in `dlc_dependencies[]`.

### 5.3 Mod packages: detect by header, not extension
Every `.scs`, `.zip` or folder **directly** in `mod\` is one mod. Subfolders aren't searched further 🌐. `PackageId` = file name without extension, or the folder name ✅.

| First bytes | Format | Reader |
|---|---|---|
| (directory) | Folder mod | direct file access |
| `PK 03 04` | ZIP | Tolerant: find the EOCD, read the central directory (ZIP64), use the central-directory sizes and offsets, ignore broken local headers. |
| `SCS#` + `01 00` | HashFS v1 🌐 | Header 24 B (magic, ver, salt u16, `"CITY"`, count u32, table offset u64). Entries 32 B (hash, offset, flags, crc, size, csize). Flags: dir, compressed (zlib), verify, encrypted. Locked files may give a fake table offset, so fall back to "table at end". Dir listing = text lines, `*` = subdir. |
| `SCS#` + `02 00` | HashFS v2 🌐 (1.50+) | Header 49 B. Entry table and metadata table are each zlib-compressed. Entries 16 B (hash, meta index, meta count, flags). Metadata chunks (plain, directory, image/mip…). Sizes are 28-bit, offset = block × 16. Dir listing = count + length bytes + names, `/` = subdir. Textures (GDeflate) are skipped. |
| `AEM!`, `MZ…`, other | **Opaque** ❓ | Listed by file name with an "unreadable package" badge. No manifest, icon or conflict data. Spike S9 investigates. |

**Path lookup:** `CityHash64` (2011 variant, no seed) of the UTF-8 path, without a leading `/` (root = `""`). With a non-zero salt, the salt (decimal) is prepended ❓ (unconfirmed for v2, so check in S10). Some locked archives have no directory listings. That's fine for us, because we look up known paths (`manifest.sii`, icon, description) by hash.

### 5.4 manifest.sii 🌐 ✅
```
SiiNunit
{
mod_package : .package_name
{
	package_version: "1.3.2"
	display_name: "RPM - QuickStart"
	author: "RPM - Modding"
	category[]: "other"
	icon: "Mod_Manager_Image.jpg"
	description_file: "mod_description.txt"
	mp_mod_optional: false
}
}
```
- `display_name` is optional; the default is the package name ✅ (one of your Workshop items has none). For Workshop mods the game ignores it and uses the Workshop title. Offline, Dispatch shows the name stored in `profile.sii` (`…|Display Name`), then the manifest name, then the ID.
- `category[]`: only the first two count. Valid values: truck, trailer, interior, tuning_parts, ai_traffic, sound, paint_job, cargo_pack, map, ui, weather_setup, physics, graphics, models, movers, walkers, prefabs, other.
- `icon`: JPG, **276×162**. Other sizes get a lint warning. `description_file`: UTF-8 with colour tags `[red] [green] [blue] [orange] [white] [normal]` (rendered, not stripped). Localised variants: `description.<locale>.txt`.
- `dlc_dependencies[]`: DLC package names (`dlc_north`). A missing one blocks activation in the game, so Dispatch warns.
- Lint: unknown or typo'd fields such as `compatible_version[]` ✅ (seen in your log), more than two categories, and a wrong icon size.

### 5.5 Workshop items 🌐 ✅
- Item root = `versions.sii` + packages (folders or ZIPs) only. Each package is a complete mod with its own `manifest.sii`.
- `versions.sii` = `package_version_info` units: `package_name` (folder, or ZIP without extension), `compatible_versions[]`, `informational`. **Exactly one unit has no `compatible_versions`; that's the universal fallback.** The name "universal" is only a convention.
- Selection: the first unit whose versions match the game, otherwise universal.
- Packages may contain `base\`/`base.zip` (always mounted) and `dlc_<name>\`/`.zip` (mounted only if that DLC is owned).
- Items without `versions.sii` are read as a single package at the root, just in case.

### 5.6 SII text format 🌐
- `SiiNunit { <class> : <unit> { key: value … } … }`
- Comments: `#`, `//`, `/* */` (quote-aware).
- `@include "x.sui"` only at column 0.
- Arrays: `a[]: v` or `a: N` + `a[i]: v`.
- Values:
  - strings (be tolerant of `\"`, `\\`, `\x..`)
  - tokens (≤12 chars `[a-z0-9_]`)
  - numbers and `&` + 8-hex floats
  - vectors `(x, y, z)`, quaternions `(w; x, y, z)`
  - pointers and `null`
- Unit names: `.name`, `_nameless.27a.c7a9.1bf8`.
- ✅ (S1) **How the game writes text:** CRLF line endings; `class : id {`; attributes indented one space; **non-ASCII as UTF-8 byte escapes** (`"\xe6\x97\xa5\xe6\x9c\xac"` = 日本). Simple word values are written **bare** (`profile_name: Promods`), anything else quoted (`profile_name: "Tuned EU"`). Dispatch writes the same way; re-writing `active_mods` and `profile_name` is byte-identical on all 8 real profiles.
- **The document model is lossless.** Unchanged bytes, line endings and comments are written back exactly. Edits are surgical, touching only `active_mods` and `profile_name`.

### 5.7 profile.sii
- **Reading:** detect by magic.
  - `SiiN` = plain text.
  - `ScsC` = 56-byte header (magic, 32-byte HMAC, 16-byte IV, u32 size), then AES-256-CBC with the publicly known key, then zlib, which gives text or BSII. Trust the inflated length, not the size field.
  - `BSII` = binary v1–3, structure blocks and data blocks, decoded to text using the SII_Decrypt format spec.
  - `3nK` = obfuscated (rare here; supported for completeness).
- ✅ (S1) On your PC **every profile.sii is ScsC → text** (not BSII); `game.sii` saves are ScsC → BSII v3. The decoder handles both: 8/8 profiles and 164/164 save files.
- **Writing: plain-text `SiiNunit`**, the way the working tools do it (to be confirmed in S2 ❓). **Never ScsC.** The game re-checks the format on every read, because it may re-encrypt the file when it next saves.
- Fields 🌐: `user_profile : _nameless.… { face, brand, map_path, logo, company_name, male, cached_* …, version, online_user_name, online_password, profile_name, creation_time, save_time, active_mods … }`.
- `active_mods: N` then `active_mods[i]: "<PackageId>|<Display Name>"`. Split on the **first** `|` only. Escape `\` and `"`. Zero mods = `active_mods: 0`.
- **Index ↔ priority 🟡** S1 evidence: index 0 = bottom (see §5.0). Spike S3 confirms in-game. Dispatch's UI always shows top = highest priority, the same as the game's mod manager, and the codec maps that to file order.
- **`profile.bak.sii`**: the game's fallback copy. Dispatch backs it up but leaves it unchanged. The S2 spike checks that a stale `.bak` causes no problems.

### 5.8 Profile names and folders
- Folder = UPPERCASE hex of the name's UTF-8 bytes ✅, and it must match `profile_name` 🌐.
- Validation:
  - not empty or whitespace-only, no leading or trailing spaces;
  - **≤ 20 characters** 🌐 (the limit Truck Tools uses; the real in-game limit is checked in S7 ❓);
  - only characters the game's name field accepts (S7; CJK names definitely work ✅);
  - unique by decoded name and by folder name, across local *and* cloud profiles;
  - every copied path must be ≤ 260 characters (CJK characters take 6 hex characters each).
- Duplicate 🌐: copy the folder, then change **only `profile_name`** (Truck Tools does exactly this). Dispatch also resets `creation_time` to now ❓ (checked in S4). `online_user_name`/`online_password` (World of Trucks link): the duplicate dialog has a **"Keep World of Trucks link"** checkbox, checked by default. Unticked, both fields are cleared in the copy.

### 5.9 Profile folder contents
Local ✅: `profile.sii`, `profile.bak.sii`, `config.cfg`, `config_local.cfg`, `controls.sii`, `gearbox_layout_*.sii`, `tutorial_hint_data.sii`, `academy\`, `album\`, `save\<slot>\` (`game.sii`, `info.sii`, previews; slots `1`, `autosave`, `autosave_drive_N`, …).
- **Full copy** = everything.
- **Fresh start** = everything except `save\` (and `album\` optional).
- 🌐 Saves don't reference the profile name; `info.sii` holds the save's own name and mod dependencies (checked in S4).

### 5.10 Steam Cloud policy (MVP)
🌐 Community consensus is to never edit `userdata\…\remote` directly, because Steam can overwrite or remove the changes. The safe route the game supports is: turn off "Use Steam Cloud" for that profile in-game (which moves it to `profiles\`), edit it, then turn it back on.

**MVP behaviour:**
- Cloud profiles are **read-only**, and their load order can be viewed.
- Duplicating *from* cloud *to* local is allowed, since it only reads from the cloud.
- Duplicating with **"Steam Cloud" as the destination** creates the local copy and then shows the one-click in-game step ("enable Steam Cloud for this profile").
- If spike S6 shows that direct cloud writes are safe with Steam closed, direct cloud editing becomes a v1 feature.

### 5.11 Presets: `*.dispatch-preset.json`
```json
{ "schema": 1, "app": "Dispatch", "game": "ets2", "name": "ProMods 2.83",
  "gameVersion": "1.62.2.1", "createdUtc": "2026-11-12T10:00:00Z", "notes": "",
  "mods": [
    { "packageId": "promods-eu-def-v283", "displayName": "ProMods Europe Definition",
      "fileName": "promods-eu-def-v283.scs", "packageVersion": "2.83" },
    { "packageId": "mod_workshop_package.00000000C3CC68CD", "displayName": "RPM - QuickStart",
      "workshopId": 3284953293 } ] }
```
- Order in `mods`: index 0 = top = highest priority, whatever the file order inside profile.sii turns out to be.
- On import, each entry is resolved against the installed mods. Missing ones are listed, with a Workshop link where there is one.
- Unknown fields are ignored. `schema` allows the format to change later.

---

## 6. Feature milestones

### M0: Spikes (console tool `Dispatch.Spike`, no UI)
The spikes in §7, each with a written result in `docs/spikes/Sx-*.md`. **Exit:** every ❓ in §5 is resolved or has an accepted fallback.

### M1: Read-only app
- Detection with a first-run wizard and manual overrides.
- Mod scan: all formats, `versions.sii`, icons, descriptions, cache, lint issues.
- Profile list (local + cloud per account), with each profile's load order decoded and installed vs missing mods marked.
- Compatibility badges (version mismatch, missing DLC, no version info, unreadable).
- "Game is running" banner.

### M2: Editing load orders safely
- Enable/disable and drag & drop reordering. A newly enabled mod goes to the top, as in-game. Save via `SafeWriter`.
- Game-running guard, concurrent-change detection, unsaved-changes prompt.
- Automatic backups, a backup browser, and one-click restore (which takes a pre-restore backup first). Retention per profile: the last **20 small** backups (profile files only) and the last **5 full-profile** backups (from rename), both configurable. The backup browser shows total size.

### M3: Duplicating and renaming profiles
- Duplicate: full copy or fresh start. Destination local, or Steam Cloud (guided step, §5.10). Name validation, free-space check, progress, never overwrites.
- Rename (local): stage a renamed copy → verify → **move the original into Dispatch's backups** (same drive, so it's instant and fully reversible) → swap the copy in.

### M4: Presets and release = **MVP**
- Save, load, export and import presets. Apply a preset to a profile, with a missing-mods report.
- Inno Setup installer, icon, About page (with SCS trademark disclaimer), log viewer.

### v1
- Copy the load order from one profile to another existing profile.
- Search, filters (category, format, compatibility, source), tags and notes.
- Install mods by dropping `.scs`/`.zip` (header-checked, copied into `mod\`). Uninstall sends files to the Recycle Bin (`FileSystem.DeleteFile(…, RecycleOption.SendToRecycleBin)`). Workshop items are never deleted.
- Launch via Steam: `steam://rungameid/227300`. Newer Steam clients show a warning when extra launch arguments are passed, so none are passed by default.
- Duplicate and outdated detection: the same mod in several files or versions, or both locally and on the Workshop.
- Direct Steam Cloud editing, if S6 allows it.

### Later
- Conflict detection: build a set of CityHash64 path hashes per mod (works for ZIP and HashFS, even without names), intersect the sets, and hint which mod wins (highest in the load order). Names are shown where available.
- ATS support (second `GameDefinition`).
- Show the game's own `game.log.txt` mod warnings in the UI.
- Auto-update (Velopack) and more languages.

---

## 7. Risks, unknowns and spikes

All spikes work on **copies** in a scratch folder, or on a throwaway profile **`DispatchTest`** that you create in-game. Never on your real profiles.

| # | Risk / unknown | Spike | Pass → / Fail → |
|---|---|---|---|
| **S1** | ScsC/BSII decoding | Decode copies of all 8 profile.sii files. Compare with SII_Decrypt's output, used as an oracle | Identical → done. Diff → fix decoder |
| **S2** | **Plain-text profile.sii accepted?** (#1 risk) | On `DispatchTest`: change `active_mods` in the decoded text, write plain text, launch and check the Mod Manager. Exit, inspect the magic and `.bak` afterwards | Accepted → plain text is the write format. Rejected → try unencrypted BSII (encoder = larger task, re-plan M2) |
| **S3** | UI order ↔ `active_mods[i]` | Order A, B, C top→bottom in-game, decode, then reverse and decode again | Rule written into the codec + tests |
| **S4** | Duplicate/rename: required field changes | Copy `DispatchTest` to a new hex folder, set `profile_name`. Do both appear, both load saves, and neither affects the other? Diff the decoded fields. Search decoded saves for the name | List of fields to change (expected: only `profile_name`) |
| **S5** | `g_save_format` | Documentation only. Dispatch reads it for diagnostics, never sets it | — |
| **S6** | Steam Cloud write safety | Throwaway cloud profile: edit with Steam closed, then start Steam and the game. Conflict dialog, or the edit lost? | Safe → v1 feature. Unsafe → stay with the §5.10 policy |
| **S7** | Name rules | Try 20/21/40 characters, CJK, emoji, `/\:*?"<>\|` in the in-game dialog | Validator rules |
| **S8** | Readers on real data + version/DLC matching | Scan your 228 + 3 mods. Report format, manifest success, failures and timings. Compare Dispatch's compatibility verdicts with the `Incompatible mod` lines in `game.log.txt` | All non-opaque mods parsed. Verdicts agree. Cold scan < 15 s, cached < 1 s |
| **S9** | `AEM!` / `MZ` packages (you chose "try harder") | **Started in M0.** Findings: the game reads them, but they are deliberately protected (encrypted-looking data, thousands of fake ZIP signatures, forged ZIP directories). **Decrypting them would circumvent the authors' protection, so Dispatch won't.** Remaining non-circumventing work (time-boxed): (a) take the display name from any profile's `active_mods` entry (`…\|Display Name`); (b) use the game's own verdicts in `game.log.txt` (`Incompatible mod - <id>`); (c) check whether the game caches mod info or icons in `Documents\…\cache` or `mods_info.sii`. | Opaque, but with the best available name and the game's compatibility verdict. Never write to these files |
| **S10** | HashFS v2 details (salt, metadata) | Read the 10 v2 mods. Compare file lists with Extractor's output (oracle) | Lists match |
| **S11** | gong-wpf-dragdrop on .NET 10 | Tiny WPF prototype list | Works → use it. Breaks → hand-rolled drag & drop |

Other risks, handled by design:
- **A game update changes a format**: readers are version-aware. Dispatch refuses to write a file it couldn't fully read, and shows clear "unknown format" messages.
- **The game starts mid-operation**: it is re-checked before each commit. Folder operations are staged and finish with one move.
- **OneDrive or antivirus locks files**: retry with back-off, then explain the failure. The atomic swap means no partial state.
- **Large profiles**: free-space check, a progress bar, and cancellation that cleans up the staging folder.
- **Licence contamination**: no GPL code in the repo unless you choose GPL. Oracles are only run as external tools in tests.
- **Trademarks**: "Dispatch" branding only. "for Euro Truck Simulator 2" appears only in descriptive text, and the About page includes a not-affiliated disclaimer.

---

## 8. Testing strategy

- **Unit tests (Core), xUnit v3 + Shouldly**
  - SII lexer/parser: comments, includes, both array forms, escapes, hex floats, tuples, `_nameless` units. **Round-trip must be byte-identical.**
  - ScsC/BSII: golden files (encrypted input → expected text).
  - Name codec and validator: ASCII, CJK, emoji, empty, 20/21 characters, collisions, path length.
  - Version glob matching and `versions.sii` selection.
  - Load-order edits: enable, disable, move, missing mods kept, `active_mods: N` count rewritten, `|` in display names, escaping.
  - Preset round-trip and import resolution.
- **Fixture mods** (`tests/Fixtures/Mods`): synthetic and a few KB each, generated by a test helper.
  - Folder mod, ZIP, ZIP with zeroed local headers, ZIP64.
  - HashFS v1 and v2 (written by a test-only writer, validated against Extractor in S10), and HashFS with a fake table offset.
  - Workshop item with `versions.sii` (universal + versioned + DLC sub-parts), and one without `versions.sii`.
  - Manifests without `display_name`, with `compatible_version[]` (typo), with 3 categories, with a 300×200 icon.
  - Opaque `AEM!` header and a garbage file.
- **Sample profiles** (`tests/Fixtures/Profiles`):
  - `profile.sii` from the **throwaway `DispatchTest`** profile, in ScsC+BSII, ScsC+text and plain-text versions. None of your real profiles go into the repo.
  - Hand-written text profiles with 0, 1, 60 mods, Workshop entries and a CJK name.
- **Integration tests** against `MockFileSystem` trees:
  - multiple Steam libraries;
  - multiple accounts;
  - OneDrive-style Documents path;
  - cloud profile split across `steam_profiles` and `remote`;
  - game's `.bak` folders present.
- **Safety tests**:
  - fake `eurotrucks2` running → every write refused;
  - process appears between backup and commit → abort;
  - file changed after load → abort;
  - failure mid-swap → original intact and backup present;
  - duplicate onto an existing name or folder → refused.
- **Manual in-game checklist** (`docs/manual-tests.md`), run on `DispatchTest` before each milestone sign-off.
- **Real-data smoke test** (local only, opt-in, read-only): scans your real mod folder and profiles, and must finish with no crashes and a report.

---

## 9. Folder and project structure

```
Dispatch\
  PLAN.md  README.md  LICENSE (The Unlicense)  THIRD-PARTY-NOTICES.md  .editorconfig  .gitignore
  Dispatch.slnx                    (.NET 10's XML solution format)
  Directory.Build.props            net10.0-windows, nullable on, warnings as errors
  src\
    Dispatch.Core\                 class library, no WPF
      Games\ Environment\ Sii\{Text,Crypto,Binary}\ Mods\{Packages,Manifest}\
      Profiles\ Presets\ Safety\ Platform\{Abstractions,Windows}\
    Dispatch.App\                  WPF exe → Dispatch.exe
      Views\ ViewModels\ Services\ Resources\Strings.resx Themes\ App.xaml
    Dispatch.Spike\                console tool (M0, later kept as a diagnostics tool)
  tests\
    Dispatch.Core.Tests\
    Fixtures\{Mods,Profiles,SteamTrees}\
  installer\Dispatch.iss
  docs\spikes\ docs\manual-tests.md docs\file-formats.md
```

---

## 10. Decisions (answered 2026-11-12)

| # | Question | Decision |
|---|---|---|
| 1 | Licence | **The Unlicense** (public domain). No GPL/MPL code copied or ported; own implementations only (§2). |
| 2 | Git | **Local git**: `git init` when implementation starts, no remote and no push. Asked before every commit or other git command. |
| 3 | Steam Cloud | **MVP policy as in §5.10**: cloud profiles read-only, duplicate cloud → local allowed, "copy to cloud" = local copy + guided in-game step. |
| 4 | World of Trucks link on duplicate | **Ask each time**: "Keep World of Trucks link" checkbox, checked by default. |
| 5 | Rename | **Move the original into Dispatch's backups** (reversible, one-click restore). |
| 6 | Backup retention | **20 small + 5 full-profile backups per profile**, both configurable. |
| 7 | Code signing | **Unsigned for now**; revisit before any public release. |
| 8 | Spike help | **You'll run the in-game checks**: a local `DispatchTest` profile, plus a throwaway Steam Cloud profile for S6. I'll provide numbered checklists. |
| 9 | Opaque mods (`AEM!`, `MZ`) | **Try harder in the MVP**: time-boxed investigation (S9), with best-available names as the fallback. |

**Still open (no answer needed yet):** the questions marked ❓ in §5, all answered by the M0 spikes.

---

## 11. Sources
- SCS modding wiki:
  - Mod manager / manifest: modding.scssoft.com/wiki/Documentation/Engine/Mod_manager
  - `package_version_info`: …/Documentation/Engine/Units/package_version_info
  - Workshop structure: …/Documentation/Tools/SCS_Workshop_Uploader/Workshop_mod_structure
  - SII units: …/Documentation/Engine/Units
  - `g_save_format`: …/Documentation/Engine/Configuration_variables/g_save_format
  - 1.33 guide (Steam Cloud): …/Games/ETS2/Modding_guides/1.33
  - 1.50 guide (HashFS v2): …/Games/ETS2/Modding_guides/1.50
  - Game Archive Packer: …/Documentation/Tools/Game_Archive_Packer
- TheLazyTomcat/SII_Decrypt (MPL-2.0), incl. `Documents/Binary SII - Format.txt`
- CoffeSiberian/DecryptTruck (`decrypt_truck`, MIT) and truck-tools
- gitlab.com/jammerxd/sii-decryptsharp
- sk-zk/TruckLib, TruckLib.HashFs, Extractor (GPL-2.0)
- Switch-Bros/easy-scsmodmanager (GPL-3.0): plain-text write commit, Workshop ID format, reader/writer
- Zoult/ets2-profile-renamer
- rohere58/ETS2ATS.ModlistManager
- Steam Community threads on cloud profiles and `active_mods`
- ValveResourceFormat/ValveKeyValue

## Verification of this plan
- Plan approved and written to `PLAN.md`. No other files created, no git commands run.
- Each milestone ends with `dotnet test` green, the manual in-game checklist run on `DispatchTest`, and a short demo of what's new.
