# Manual test checklist (MVP)

Automated coverage: 178 unit tests (`dotnet test`) plus the end-to-end self-test, which drives the real view models on
the throwaway profile `DispatchTest`:

```
Dispatch.exe --selftest DispatchTest result.txt
```

It covers save, restore, duplicate, rename, undoing the rename, presets, bulk moves, adding/removing mods from the Mods tab,
and deleting/restoring a profile, and leaves `DispatchTest` unchanged.

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

## Release
- [ ] `powershell -ExecutionPolicy Bypass -File installer\build.ps1` (needs Inno Setup 6) → `artifacts\Dispatch-0.5.0-setup.exe`.
- [ ] Install: no admin prompt, Start-menu entry, the app starts. Uninstall: asks about deleting data; **No** keeps
  `%LOCALAPPDATA%\Dispatch\backups`.
- [ ] Windows SmartScreen warns on first run (the build is unsigned, as decided).
