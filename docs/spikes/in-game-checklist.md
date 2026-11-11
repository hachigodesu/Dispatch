# In-game checklist: spikes S2, S3, S4, S7

Your real profiles are never touched. Dispatch's spike commands only accept local profiles whose name
starts with **DispatchTest**. They refuse to run while the game is open, and they back up to
`%LOCALAPPDATA%\Dispatch\backups\Local\<profile-hex>\` before writing.

You do the in-game steps (🎮). Claude runs the commands (💻) when you say a step is done, and you can run
them yourself too: `dotnet run --project src/Dispatch.Spike -- <command>`.

Allow about 20 minutes in total.

---

## Prep: create the test profile 🎮
1. Start ETS2 and create a **new profile** named exactly `DispatchTest`.
   - **Untick "Use Steam Cloud"**: it must be a local profile.
   - Any truck, company name and so on. Skip the tutorial.
2. Load into the world, then **save the game once** (Menu → Save & Load → Save). This gives S4 a save to copy.

## S3: Which end of the list is index 0? 🎮 then 💻
3. In the **Mod Manager** for `DispatchTest`, activate these three small mods, arranged **top → bottom** as:
   - **A** `High_Detail_Vegetation` (top)
   - **B** `2023 Man TGX Realistic blinkers sounds`
   - **C** `Iveco Sway Cursor 13 2019 engine sound.0` (bottom)

   (Any three small, non-map mods are fine. Just write down the top-to-bottom order.)
4. Confirm the changes, load into the world, save, then **exit the game completely**. Tell Claude: *"S3 ready"*.
5. 💻 `s3-read DispatchTest`
   - **Expected, if index 0 = bottom:** `[0] C`, `[1] B`, `[2] A`.

## S2: Does the game accept a plain-text profile.sii? 🎮
6. 💻 `s2-write DispatchTest`: backs up, then writes `profile.sii` as **plain text** with the order **reversed**.
7. 🎮 Start the game and select `DispatchTest`. Open the Mod Manager and note:
   - Is the order now **C, B, A** (top → bottom)? → the game read our file.
   - Is it still **A, B, C**? → the game ignored the file (probably fell back to `profile.bak.sii`).
   - Any error or "profile damaged" message?
8. Load into the world, save, and exit the game completely. Tell Claude: *"S2 done"* plus what you saw.
9. 💻 `s2-check DispatchTest` shows whether the game re-encrypted the file, kept the order, and updated `profile.bak.sii`.

## S4: Duplicating a profile 🎮
10. 💻 `s4-duplicate DispatchTest DispatchTest2`: copies the whole profile folder and changes only `profile_name`.
11. 🎮 Start the game and check:
    - Do **both** `DispatchTest` and `DispatchTest2` appear in the profile list, with the right names?
    - Load `DispatchTest2`: is the save there? Same mods and order? Same truck and money?
    - Drive for a minute and **save** in `DispatchTest2`.
    - Switch to `DispatchTest`: is it **unaffected** (no new save, same state)?
12. Exit and tell Claude: *"S4 done"* plus anything odd.

## S7: Profile name rules 🎮 (in the "create profile" screen; no need to finish creating)
13. Note the answers:
    - How many characters does the name field accept? Type `123456789012345678901234567890`.
    - Are these accepted: `日本` · `🚚` · `/ \ : * ? " < > |` · a leading or trailing space · `.` and `-`?
    - Does it refuse a name that already exists (e.g. `DispatchTest`)?
14. Cancel (or create and delete again).

## Cleanup and recovery
- Keep `DispatchTest`; later spikes (S6) reuse it. You can delete `DispatchTest2` in-game afterwards.
- If anything looks wrong: 💻 `restore DispatchTest` restores the latest Dispatch backup.
  It saves the current state first, so a restore can itself be undone.
