# Manual test checklist (MVP)

Automated coverage: 135 unit tests (`dotnet test`) plus the end-to-end self-test, which drives the real view models on
the throwaway profile `DispatchTest`:

```
Dispatch.exe --selftest DispatchTest result.txt
```

It covers save, restore, duplicate, rename, undoing the rename and presets, and leaves `DispatchTest` unchanged.

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

## Release
- [ ] `powershell -ExecutionPolicy Bypass -File installer\build.ps1` (needs Inno Setup 6) → `artifacts\Dispatch-0.4.0-setup.exe`.
- [ ] Install: no admin prompt, Start-menu entry, the app starts. Uninstall: asks about deleting data; **No** keeps
  `%LOCALAPPDATA%\Dispatch\backups`.
- [ ] Windows SmartScreen warns on first run (the build is unsigned, as decided).
