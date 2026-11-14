# Dispatch

**A mod manager for Euro Truck Simulator 2 and American Truck Simulator, for Windows.**

The game's own mod manager only works inside the game, one profile at a time. Dispatch works outside the game. It
lets you see all your mods, sort load orders by drag and drop, copy profiles, save load orders as presets, and keep
older game versions for mods that haven't been updated yet. It backs up every profile before changing it.

**[Download the latest release](https://github.com/hachigodesu/Dispatch/releases/latest)**

## Install

| Download | What it is |
|---|---|
| `Dispatch-<version>-setup.exe` | **Installer** (recommended). It installs for your user only, needs no admin rights, and can update itself. |
| `Dispatch-<version>-portable.zip` | **Portable**: a single `Dispatch.exe` with nothing to install. |

- **Requirements:** Windows 10 or 11 (64-bit). .NET is included, so nothing else needs to be installed.
- **SmartScreen:** Dispatch isn't code-signed, so Windows SmartScreen warns the first time you run it. Click
  **More info**, then **Run anyway**. Each release lists SHA-256 checksums if you want to check the download.

On first start Dispatch finds the game, your Steam libraries and accounts, your profiles and your mods by itself. If
something is installed somewhere unusual, you can set the paths in **Settings**.

## Features

### See all your mods
- Reads every mod format the game uses: folders, ZIP (including "protected" ZIPs), and HashFS v1 and v2 `.scs`
  archives. It covers local mods and Steam Workshop items.
- Shows each mod's icon, description, version, author, type and required DLC.
- **Compatibility warnings:** made for a different game version, needs a DLC you don't own, or has no version
  information.
- **The game's own verdicts:** the warnings the game wrote about each mod in its log the last time you played.
- **Duplicates and outdated copies:** the same mod in several files or versions, or installed both locally and from
  the Workshop.
- Search and filter by type. You can also show only duplicates, mods no profile uses, incompatible mods, or mods
  the game reported problems with. Add your own tags and notes to any mod.
- **Install** mods by dropping `.scs` or `.zip` files onto Dispatch. **Uninstall** sends them to the Recycle Bin.
  Workshop items are never deleted; unsubscribe from those in Steam.
- **Mod library:** move the mods no profile uses out of the game's mod folder in one click. The game starts faster and
  its own mod list gets shorter. When you add one of them to a profile, Dispatch moves it back by itself.

### Edit load orders
- Turn mods on and off and **drag and drop** them into order. Select several mods to move or remove them together.
  The order is shown the same way as in the game: the mod at the top wins.
- **Conflicts:** shows which active mods contain the same files, and which one the game will use. That's often
  intended (patches, compatibility add-ons), but it shows you why a mod seems to do nothing.
- **Suggest order:** sorts a load order by the usual rules (looks and sounds on top, maps at the bottom, patches above
  the maps they fix) and shows you every move first. It goes into the editor, so you check it before saving.
- **Show on map:** see the roads a map mod adds, and the roads it takes away, drawn over the map you already have. Opened
  from a profile, it's compared with the mods below it there, so you can see where a connector or an outdated add-on
  replaces another map's roads, and which of its parts a mod above it covers up. From the Mods tab you can also see
  how a mod would change any profile's roads before you add it.
- **Road map:** a profile's whole road network, each road coloured by the mod that adds it, with a legend to hide mods
  or jump to them.
- **Copy a load order** from one profile to others.
- **Presets:** save a load order under a name. You can apply it to any profile, or export it as a file to share. When
  you import a preset, Dispatch lists the mods you don't have, with Workshop links where there are any.

### Check your profiles
- The **Health** tab lists each profile's problems with a fix for each: mods that aren't installed, mods made for
  another game version, mods needing a DLC you don't own, an older copy of a mod you have a newer one of, and mods your
  newest save was made with that aren't active any more. It also notes file conflicts and the game's own warnings.
- The **Multiplayer** panel shows which active mods everyone in your **Convoy** needs too, and which are optional.
  Copy the list to send to the people you drive with. It also hints which mods **TruckersMP** supports (only ProMods
  and the seasonal weather mods).

### Manage profiles
- **Duplicate** a profile as a full copy, or as a fresh start that has its settings and mods but no saves. You can
  choose whether the copy keeps the World of Trucks link.
- **Rename** and **delete** profiles. Deleted profiles go to Dispatch's backups and can be restored.
- Steam Cloud profiles are shown with their load order, and you can duplicate them as local profiles.

### Keep older game versions
Steam keeps only one version of the game installed. When a big update comes out, many mods (especially maps) take a
while to catch up. Dispatch can keep a copy of the version you have now:

1. In **Settings → Game versions**, click **Keep a copy of the Steam version…**. If you need an older version, first
   switch the game to that beta branch in Steam (game Properties → Betas).
2. Let Steam update the game as usual.
3. Each profile launches with the version its active mods are made for. Dispatch picks it automatically, or you can
   choose a version per profile.

Copies don't need to take the space of a whole game:
- A copy on the same drive as Steam **shares every unchanged file** with the Steam install.
- **Pack to save space** stores only what differs from the Steam install and from your other packed versions, which is
  about a tenth of the game. Dispatch rebuilds the game when you launch that version and removes the rebuilt files
  after you quit.
- After Steam updates the game, Dispatch packs your kept versions again by itself in the background, so they stay
  small. You can turn that off in Settings.

Copies are started directly, not through Steam, so Steam must be running and Steam's launch options don't apply to
them.

### Also
- **Launch the game** from Dispatch, with the right version for the selected profile.
- **Euro Truck Simulator 2 and American Truck Simulator.** Switch between them in Settings.
- **Languages:** English, German, Spanish, French, Polish, Portuguese (Brazil) and Russian.
- **Updates:** Dispatch checks this page for new releases at most once a day. You can turn that off in Settings. The
  installed version downloads updates only from this repository's releases and checks them against the checksum.

## Safety

Dispatch edits files the game depends on, so it's built to be careful:

- **Every change is backed up first.** You can restore any backup in one click, and restoring backs up the current
  state first. Dispatch keeps the last 20 backups per profile, and the last 5 whole-profile backups (from renaming and
  deleting).
- **Nothing is written while the game is running.** This is checked before a change starts and again right before it
  is saved.
- **Nothing is overwritten.** Duplicating or renaming never replaces an existing profile.
- **Changes are all-or-nothing.** Files are written to a temporary copy, checked, and only then swapped in, so a crash
  can't leave a half-written profile.
- **Some things are never touched:** Workshop content, the game's own profile backups, and the Steam install.
  Steam Cloud profiles are read-only.

## Your data

Dispatch keeps its settings, presets, notes, logs and **backups** in `%LOCALAPPDATA%\Dispatch`. Uninstalling keeps
that folder unless you answer yes when the uninstaller asks whether to delete it, so your backups survive a
reinstall.

Dispatch doesn't collect anything. The only network request is the optional update check to GitHub.

## Limitations
- Windows only. Linux, Steam Deck and macOS aren't supported.
- A few mods use packaging Dispatch can't read. They're listed by file name, but without details, and conflicts
  can't be checked for them.
- The map shows roads and city names only, with junctions simplified. Protected map mods can't be shown.
- Steam Cloud profiles can't be edited directly. To edit one, turn off Steam Cloud for it in the game, edit it, then
  turn Steam Cloud back on.
- A profile that has been saved in a newer game version can't be loaded in an older one. Dispatch warns you before
  launching an older version.

## Building from source

Requires the .NET 10 SDK.

```
dotnet build
dotnet test
dotnet run --project src/Dispatch.App
```

Release build: runs the tests, makes a self-contained single-file exe, then builds the installer if Inno Setup 6 is
installed.

```
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

For developers:
- [PLAN.md](PLAN.md): design and file-format notes.
- [docs/walkthrough-m1.md](docs/walkthrough-m1.md): how the code is organised.
- [docs/manual-tests.md](docs/manual-tests.md): the checks that need a person.

## Licence

Public domain ([The Unlicense](LICENSE)). Third-party packages keep their own licences, listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Dispatch is not affiliated with or endorsed by SCS Software. "Euro Truck Simulator 2" and "American Truck Simulator"
are trademarks of SCS Software.
