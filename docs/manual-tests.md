# Manual test checklist (MVP)

Automated coverage: 242 unit tests (`dotnet test`) plus the end-to-end self-test, which drives the real view models on
the throwaway profile `DispatchTest`:

```
Dispatch.exe --selftest DispatchTest result.txt
```

It covers save, restore, duplicate, rename, undoing the rename, presets, bulk moves, adding/removing mods from the Mods tab,
deleting/restoring a profile, conflicts, ATS switching, game versions (keeping, sharing, packing, automatic repacking,
on small fake installs), the mod library (with its own two test mods), the Multiplayer panel, a Health fix and the
suggested order (discarded), and leaves `DispatchTest` unchanged.

The items below need a person: real mouse and keyboard, real dialogs, the running game. Use **`DispatchTest` only**,
and close the game unless a step says otherwise.

## M2: load order
- [ ] **Drag & drop (S11):** drag a mod up or down in DispatchTest's list. An insert line shows and the row moves. "Unsaved changes" appears.
- [ ] Drag a mod from **Add mods** into the list at a chosen position.
- [ ] **Alt+↑ / Alt+↓** move the selected mod; **Delete** removes it; **Enter** in Add mods adds it to the top.
- [ ] **Save**, then start the game: the Mod Manager shows the same order. Exit the game.
- [ ] Start the game and leave it running: the amber banner appears and **Save** is disabled with "Close the game to save".
- [ ] Change something, then click another profile → "Save changes?" Check that **Cancel** keeps you on DispatchTest with your edits.
- [ ] Change something, then close the window → the same question; Cancel keeps the window open.
- [ ] Open the **Backups** panel → select the newest backup → **Restore** → the old order is back. A "Before restoring a backup" entry appears.
- [ ] Select a **Steam Cloud** profile → the blue read-only note shows; toolbar, Add mods and Save are unavailable.

## Multi-select and bulk actions
- [ ] In DispatchTest's list, **Ctrl+click** two mods, then **Up/Down/To top/To bottom**: they move together and stay selected.
- [ ] **Shift+click** a range and press **Remove** (or Delete): all of them go. Discard.
- [ ] Select several rows and **drag** them: they move as a block to where you drop them.
- [ ] **Add mods** panel: select several and press **Add to top**, or drag them in together.

## Mods tab
- [ ] Every mod shows its **type** (Map, Sound, UI, …). The **All types** dropdown filters, e.g. only Sound.
- [ ] Select several mods → "N selected" shows → **right-click → Add to profile → DispatchTest**: they appear at the top
  of DispatchTest (a backup is taken first). The game shows them there too.
- [ ] Right-click → **Remove from profile** lists only profiles where a selected mod is active; removing works the same way.
- [ ] With unsaved edits open for DispatchTest in the Profiles tab, add a mod to it from the Mods tab: it's added to the
  unsaved list instead (nothing is lost), with a message saying so.
- [ ] With the game running, the right-click actions are disabled.

## Delete profiles
- [ ] Duplicate DispatchTest to a throwaway name, select it, **Delete…** → the confirmation names the profile and its saves → it
  disappears from Dispatch and from the game.
- [ ] Settings → **Removed and renamed profiles** shows it ("Whole profile, deleted") → **Restore profile** brings it back.
- [ ] Delete it again, then **Move to Recycle Bin** on its backup → it's gone from the list and is in the Windows Recycle Bin.
- [ ] A Steam Cloud profile can't be deleted (Delete is disabled).

## M3: duplicate and rename
- [ ] **Duplicate…** on DispatchTest: the suggested name is "DispatchTest 2". Type an existing name → red error, OK disabled.
  Type 21 characters → error. Type an emoji → error. Type "dispatchtest" → amber warning only.
- [ ] Duplicate as **Fresh start** with **Keep World of Trucks link** unticked → the new profile appears with 0 saves.
  In the game it loads and has the same mods.
- [ ] Duplicate with destination **Steam Cloud** → an info box explains the in-game step. In the game, edit the copy and tick
  "Use Steam Cloud" → it moves to the cloud; in Dispatch (Refresh) it now shows as Steam Cloud, read-only.
- [ ] **Rename…** the copy → the old name disappears. Settings → **Removed and renamed profiles** lists the original → **Restore profile**
  brings it back under its old name.
- [ ] Delete the test copies in the game afterwards.

## M4: presets
- [ ] **Presets → Save current load order…** → name it → it appears in the list.
- [ ] **Export…** → save a file → **Import…** the same file → a second copy "(2)" appears.
- [ ] Open an exported file in a text editor and remove a mod's line, or rename a packageId to something not installed →
  import → **Apply** → an info box lists the missing mod; the rest is applied as unsaved changes; **Discard** undoes it.
- [ ] **Delete** a preset → it asks first.

## v1
- [ ] **Launch game** (top right) starts ETS2 through Steam. With unsaved changes it asks first. While the game runs the
  button is disabled.
- [ ] **Copy load order to…** on DispatchTest → pick another test profile → its load order now matches; a backup was taken
  (its Backups panel).
- [ ] Mods tab → **Install mods…** → pick an `.scs`/`.zip` → it appears in the list. **Drag** a mod file from Explorer onto the
  list → it's installed. Install the same file again → it asks whether to replace it (the old one goes to the Recycle Bin).
- [ ] Try to install a `.txt` renamed to `.scs` → reported as "protected or unknown format" but installed; a real `.txt` is skipped.
- [ ] Select a mod active in a test profile → **Uninstall…** → it asks whether to remove it from that profile as well → the
  file is in the Recycle Bin.
- [ ] Select a Workshop item → Uninstall says to unsubscribe in Steam and leaves it alone.
- [ ] **Show → Duplicates and older copies**: lists mods installed twice; the older one has an orange "Older copy" badge and
  the details panel names the other copy.
- [ ] **Your tags** / **Your note** in the details panel: type something, restart Dispatch → it's still there; searching a
  tag finds the mod.

## v2: conflicts
- [ ] Profiles tab → select a profile with map mods (e.g. ProMods plus an add-on) → **Conflicts (n)** tab: "Checking for
  conflicts…" shows briefly, then a list of "#a X replaces files of #b Y". The higher mod (smaller #) is always first.
- [ ] Click a pair → example file names appear below (or a note that the archive hides its names).
- [ ] Rows involved show a purple "n conflicts" badge; hovering it says how many mods it replaces / is replaced by.
- [ ] Drag a mod that loses a conflict above the winner → within a second the pair flips. **Discard changes** → back as before.
- [ ] Tick **Only the selected mod** → only pairs involving the selected row are listed.
- [ ] In-game sanity check (optional, DispatchTest): put two mods that change the same thing in both orders; the one higher
  in the list is the one you see in-game.

## v2: game warnings, ATS, updates, languages
- [ ] Mods tab → **Show → The game reported problems** lists mods the game complained about at its last start; the
  details panel shows them under **The game says** (in English, with the log's date).
- [ ] With ATS installed, the header shows a game switcher. Pick **American Truck Simulator** → its profiles and mods
  appear, Settings shows ATS paths, **Launch game** starts ATS. Restart Dispatch → it opens on ATS again. Switch back.
- [ ] With unsaved load-order changes, switching the game first asks about them (Cancel keeps you where you were).
- [ ] Settings → **Updates → Check now**: while the repository is private it says no public releases were found. Once a
  newer public release exists: the blue banner appears; **Install update** downloads, closes Dispatch, installs and
  starts it again; settings and backups are still there.
- [ ] Settings → **Language** → pick a language → **Restart now** → the whole UI is in that language. Check a few dialogs
  (Duplicate, Delete, Uninstall) for cut-off text. Set it back to **Same as Windows**.

## v2: game versions per profile
- [ ] Settings → **Game versions** lists the Steam install with its version (and the beta branch, if Steam is on one).
- [ ] Steam → ETS2 Properties → Game Versions & Betas → an older version (e.g. 1.60); wait for the download. In Dispatch
  press Refresh: the Steam entry shows that version and the beta-branch hint.
- [ ] **Keep a copy of the Steam version…** → choose a folder on the game's drive → the confirm dialog says files are
  shared → it finishes in seconds → the copy shows "0 MB of its own, ~41 GB shared". The new folder `ETS2 <version>`
  appears in the list. Choosing the same place again is refused (never overwrites).
- [ ] (Optional) Keep a copy on another drive: the dialog says it's a full copy; progress bar; **Cancel** leaves no
  folder behind.
- [ ] Switch Steam back to the latest version (Betas → None) and let it update. Refresh: Steam entry = latest, copy = old
  and **not** marked broken; its "of its own" size grew to roughly what the update changed. Free space on the drive
  dropped by about that much.
- [ ] Profiles → a profile whose mods are made for the old version: **Game version: Automatic: <old> · copy** with an
  orange "older than the Steam version" note. A profile with up-to-date or no-version mods: Automatic picks Steam.
- [ ] The header button reads **Launch <version>** for the selected profile. Launch the copy (Steam running): the game
  starts, shows the old version in the main menu, owned DLC and Workshop mods work, the profile loads. Quit.
- [ ] Launch with Steam closed: Dispatch starts Steam and asks you to click Launch again.
- [ ] Pin a profile to a version, restart Dispatch: still pinned. Rename the profile: still pinned. Set it back to Automatic.
- [ ] **Remove from list** on the copy: gone from the list, the folder is still on disk.
- [ ] **Pack to save space…** on the old copy (after Steam has moved to the latest version; on the game's drive): the
  dialog explains packing → three phases with progress (reading Steam, packing, checking; ~4 minutes) → the copy reads
  "<version> · packed copy" with "~3.6 GB of its own". Its folder holds `dispatch-pack.bin`, `dispatch-pack.data`
  and `base\`. The drive's free space grew by ~34 GB.
- [ ] Launch a profile pinned to the packed copy: a blue banner "Preparing <version>: x of ~41 GB" with Cancel (a few
  minutes); then the game starts with the old version. **Cancel** during preparing leaves no `game` folder.
- [ ] Quit the game: within a few seconds `<copy>\game` is gone again.
- [ ] After a later Steam update: the packed copy says "… of it old Steam files: pack it again" → **Pack** frees them.

## After 0.7.0: shared packed copies, automatic repacking
- [ ] Keep and pack a second old version on the same drive: it reads "… of its own, plus … shared with other kept
  versions" and is much smaller than the first one. Launch it once: it plays normally.
- [ ] After a Steam update (or with the setting just switched on): a blue banner "Packing <version> again after a Steam
  update" appears by itself; **Cancel** stops it until the next start. Launching while it runs stops it, and it carries
  on after the game closes. Afterwards both copies show no old Steam files.

## After 0.7.0: mod library
- [ ] Mods → **Move unused mods to the library…**: the dialog shows how many and how much; afterwards they carry a
  "Library" badge, the game's own mod manager no longer lists them, and the game starts faster.
- [ ] Add a library mod to a profile (right-click → Add to profile): a message says it was moved back; the game sees it.
- [ ] Right-click → Move to / back from the library; **Move all back** empties it. With the game running, nothing moves.

## After 0.7.0: multiplayer and health
- [ ] Profiles → **Multiplayer**: a map mod marked optional still says "Needed"; ProMods says "TruckersMP". **Copy the list
  for your convoy** and paste it somewhere: names in order, Workshop links.
- [ ] **Health** tab: each profile shows its problems; a fix (e.g. remove a missing mod) changes the profile, a backup
  appears in Backups, and the problem is gone after the automatic check. A profile with unsaved edits refuses fixes.

## After 0.7.0: suggested load order
- [ ] Profiles → **Suggest order…** on a profile with a truck mod below a map: the dialog lists the order with ↑/↓, the
  truck moves above the maps, the maps keep their order. Apply → the editor is unsaved; **Discard** restores the order.

## After 0.8.0: map view
- [ ] Profiles → select a map mod in a load order → details → **Show on map**: a window opens, shows "Reading the
  map…" with progress the first time (about 20 s for a big combo), then the map. Opening it again is about a second.
- [ ] Scroll to zoom (around the mouse), drag to move, double-click to zoom in: smooth, and the roads sharpen a moment
  after you stop. **Zoom to changes** and **Whole map** work. City labels don't overlap.
- [ ] A connector or patch: red roads where it adds, blue dashed where roads from the map below disappear; the orange
  text names the mods they came from. Move a mod above it that replaces some of its sectors → purple cells, and the
  summary says which mod hides them.
- [ ] Mods tab → right-click any mod → **Show on map**: compared with the game and DLCs only (the subtitle says so).
  A truck mod says it has no map sectors; a protected map (e.g. one with an `AEM!` header) says it can't be read.
- [ ] A separate map (Grand Utopia): the note says it's a separate map, and only its own roads are shown.
- [ ] Close a Map window while it's still reading: no error, and Dispatch stays responsive. Two Map windows at once work.
- [ ] Profiles → **Road map…**: the whole network, each mod in its own colour, the game grey; the legend lists the map
  mods top of the load order first. Untick a mod → its roads disappear a moment later; **Mods only** hides the game;
  **Show all** brings everything back; clicking a mod's name zooms to it. A mod whose sectors are all replaced by mods
  above is greyed and says so. The Grand Utopia profile shows its own map.
- [ ] Mods tab → right-click a map mod that isn't in a profile → **Show on map in profile ▸** → that profile: the
  subtitle says "as if it were added at the top"; with one that is in it, it's compared at its real place. A Europe
  mod in the Grand Utopia profile says it doesn't change that profile's roads.

## Release
- [ ] `powershell -ExecutionPolicy Bypass -File installer\build.ps1` (needs Inno Setup 6) → `artifacts\Dispatch-<version>-setup.exe`.
- [ ] Install: no admin prompt, Start-menu entry, the app starts. Uninstall: asks about deleting data; **No** keeps
  `%LOCALAPPDATA%\Dispatch\backups`.
- [ ] Windows SmartScreen warns on first run (the build is unsigned, as decided).
