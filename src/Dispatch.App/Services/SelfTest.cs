using System.IO;
using System.Text;
using Dispatch.App.ViewModels;
using Dispatch.Core.Profiles;
using Dispatch.Core.Sii;

namespace Dispatch.App.Services;

/// <summary>Answers every dialog with "yes" (self-test only).</summary>
internal sealed class AutoYesDialogs : IDialogs
{
    public List<string> Errors { get; } = [];

    public bool Confirm(string title, string message) => true;

    public bool? AskSaveChanges(string profileName) => true;

    public void ShowError(string message) => Errors.Add(message);

    public List<string> Infos { get; } = [];

    public void ShowInfo(string message) => Infos.Add(message);

    /// <summary>Answers for the next Duplicate/Rename dialogs: new name, include saves.</summary>
    public Queue<(string Name, bool IncludeSaves)> ProfileDialogAnswers { get; } = new();

    public Queue<string> TextAnswers { get; } = new();

    public Queue<IReadOnlyList<string>> FilesAnswers { get; } = new();

    public Queue<string[]> ProfilePicks { get; } = new();

    public bool? YesNoCancelAnswer { get; set; } = true;

    public IReadOnlyList<string> PickOpenFiles(string filter) => FilesAnswers.Count > 0 ? FilesAnswers.Dequeue() : [];

    public bool? AskYesNoCancel(string title, string message) => YesNoCancelAnswer;

    public IReadOnlyList<ViewModels.ProfileItemViewModel> PickProfiles(string title, string prompt,
                                                                        IReadOnlyList<ViewModels.ProfileItemViewModel> profiles)
    {
        var names = ProfilePicks.Count > 0 ? ProfilePicks.Dequeue() : [];
        return profiles.Where(p => names.Contains(p.Name)).ToList();
    }

    public Queue<string> FileAnswers { get; } = new();

    public string? AskText(string title, string prompt, string initial) => TextAnswers.Count > 0 ? TextAnswers.Dequeue() : null;

    public string? PickOpenFile(string filter) => FileAnswers.Count > 0 ? FileAnswers.Dequeue() : null;

    public string? PickSaveFile(string filter, string defaultName) => FileAnswers.Count > 0 ? FileAnswers.Dequeue() : null;

    public bool ShowProfileDialog(ViewModels.ProfileDialogViewModel dialog)
    {
        var (name, includeSaves) = ProfileDialogAnswers.Dequeue();
        dialog.NewName = name;
        dialog.IncludeSaves = includeSaves;
        return dialog.CanAccept;
    }
}

/// <summary>
/// Development self-test: "Dispatch.exe --selftest DispatchTest result.txt". Drives the real view models on a
/// throwaway profile: move the top mod to the bottom, save, check the file, restore the backup, check the original
/// is back byte-for-byte. Refuses any profile not named "DispatchTest…".
/// </summary>
internal static class SelfTest
{
    public static async Task<bool> RunAsync(MainViewModel vm, AutoYesDialogs dialogs, string profileName, string resultFile)
    {
        var log = new StringBuilder();
        void Step(string s) => log.AppendLine(s);
        bool ok = false;
        try
        {
            if (!profileName.StartsWith("DispatchTest", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Self-test only runs on profiles named DispatchTest….");
            }

            var p = vm.Profiles;
            p.Selected = p.Items.First(i => i.Name == profileName && !i.IsCloud);
            string sii = p.Selected.Profile.ProfileSiiPath;
            byte[] original = File.ReadAllBytes(sii);
            var before = p.LoadOrder.Select(l => l.PackageId).ToList();
            Step($"before (top-first): {string.Join(" | ", before)}; file {SiiFormatDetector.Detect(original)}");
            Step($"mod types offered: {string.Join(", ", vm.Mods.TypeOptions)}; no separate Maps entry: {!vm.Mods.TypeOptions.Contains("Maps")}");
            if (before.Count < 2)
            {
                throw new InvalidOperationException("Needs at least 2 active mods.");
            }

            p.SelectedEntry = p.LoadOrder[0];
            p.MoveBottomCommand.Execute(null);
            Step($"dirty after move: {p.IsDirty}; can save: {p.CanSave}");
            await p.SaveCommand.ExecuteAsync(null);
            p = vm.Profiles;
            var reread = ProfileRepository.ReadLoadOrder(p.Selected!.Profile).TopFirst.Select(e => e.PackageId).ToList();
            var expected = before.Skip(1).Append(before[0]).ToList();
            Step($"after save (re-read from disk): {string.Join(" | ", reread)}; file {SiiFormatDetector.Detect(File.ReadAllBytes(sii))}");
            bool saved = reread.SequenceEqual(expected);
            Step($"save correct: {saved}; message: {p.Message}");

            p.SelectedBackup = p.Backups.First(b => b.Backup.Reason == Dispatch.Core.Safety.BackupReason.LoadOrderSave);
            await p.RestoreBackupCommand.ExecuteAsync(null);
            bool restored = File.ReadAllBytes(sii).AsSpan().SequenceEqual(original);
            Step($"restore → original bytes back: {restored}; backups now: {vm.Profiles.Backups.Count}");
            bool m3 = await DuplicateRenameAsync(vm, dialogs, profileName, Step);
            bool m4 = Presets(vm, dialogs, profileName, resultFile, Step);
            bool bulk = await BulkAndDeleteAsync(vm, dialogs, profileName, Step);
            bool v1 = await V1Async(vm, dialogs, profileName, resultFile, Step);
            m4 = m4 && bulk && v1;
            Step($"dialog errors: {(dialogs.Errors.Count == 0 ? "none" : string.Join(" / ", dialogs.Errors))}");
            ok = saved && restored && m3 && m4 && !vm.Mods.TypeOptions.Contains("Maps") && dialogs.Errors.Count == 0;
        }
        catch (Exception ex)
        {
            Step($"FAILED: {ex.GetType().Name}: {ex.Message}");
        }

        Step(ok ? "RESULT: PASS" : "RESULT: FAIL");
        await File.WriteAllTextAsync(resultFile, log.ToString());
        return ok;
    }

    /// <summary>
    /// v1: install two generated test mods (same name, different versions) → duplicate detection → notes → uninstall;
    /// copy a load order onto a throwaway copy of the test profile.
    /// </summary>
    private static async Task<bool> V1Async(MainViewModel vm, AutoYesDialogs dialogs, string profileName, string resultFile, Action<string> step)
    {
        // Two tiny generated mods with the same display name: 1.0 and 1.1.
        string dir = Path.Combine(Path.GetDirectoryName(resultFile)!, "selftest-mods");
        Directory.CreateDirectory(dir);
        string MakeMod(string file, string version)
        {
            string path = Path.Combine(dir, file);
            File.Delete(path);
            using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
            using var w = new StreamWriter(zip.CreateEntry("manifest.sii").Open());
            w.Write($"SiiNunit\n{{\nmod_package : .package_name\n{{\n package_version: \"{version}\"\n display_name: \"Dispatch Self-Test Mod\"\n category[]: \"other\"\n}}\n}}\n");
            return path;
        }

        var files = new[] { MakeMod("DispatchSelfTest_a.scs", "1.0"), MakeMod("DispatchSelfTest_b.scs", "1.1") };
        step($"Install mods… button enabled: {vm.Mods.InstallCommand.CanExecute(null)}");
        if (!vm.Mods.InstallCommand.CanExecute(null)) { return false; }
        await vm.Mods.InstallFilesAsync(files);
        var a = vm.Mods.ById.GetValueOrDefault("DispatchSelfTest_a");
        var b = vm.Mods.ById.GetValueOrDefault("DispatchSelfTest_b");
        bool installed = a is not null && b is not null;
        bool dupes = installed && a!.IsDuplicate && a.Duplicate!.IsOlder && b!.IsDuplicate && !b.Duplicate!.IsOlder;
        vm.Mods.Show = Resources.Strings.ShowDuplicates;
        bool filter = vm.Mods.Items.Contains(a!) && vm.Mods.Items.Contains(b!);
        vm.Mods.Show = Resources.Strings.ShowAll;
        step($"install 2 test mods: {installed}; duplicate/older detected: {dupes}; Duplicates filter shows them: {filter}; message: {vm.Mods.Message}");

        // Notes: set, reload from disk, clear.
        b!.TagsText = "selftest, temporary";
        b.NoteText = "made by the self-test";
        var reloaded = Dispatch.Core.Mods.ModNotesStore.Load(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dispatch", "notes.json")).Get(b.PackageId);
        bool notes = reloaded.Tags.SequenceEqual(["selftest", "temporary"]) && reloaded.Note == "made by the self-test";
        vm.Mods.Filter = "temporary";
        bool searchable = vm.Mods.Items.Contains(b);
        vm.Mods.Filter = "";
        b.TagsText = "";
        b.NoteText = "";
        step($"tags/notes saved: {notes}; found by searching a tag: {searchable}");

        // Uninstall both (to the Recycle Bin); they aren't active anywhere, so no profile question.
        vm.Mods.SelectedMods.Clear();
        vm.Mods.SelectedMods.Add(vm.Mods.ById["DispatchSelfTest_a"]);
        vm.Mods.SelectedMods.Add(vm.Mods.ById["DispatchSelfTest_b"]);
        await vm.Mods.UninstallCommand.ExecuteAsync(null);
        bool uninstalled = !vm.Mods.ById.ContainsKey("DispatchSelfTest_a") && !vm.Mods.ById.ContainsKey("DispatchSelfTest_b");
        step($"uninstall to Recycle Bin: {uninstalled}");

        // Copy load order: make a throwaway copy, change its order, copy the test profile's order onto it.
        string copyName = profileName + "Cp";
        var p = vm.Profiles;
        p.Selected = p.Items.First(i => i.Name == profileName && !i.IsCloud);
        var order = p.LoadOrder.Select(l => l.PackageId).ToList();
        dialogs.ProfileDialogAnswers.Enqueue((copyName, false));
        await p.DuplicateCommand.ExecuteAsync(null);
        p.Selected = p.Items.First(i => i.Name == copyName);
        p.SelectedEntry = p.LoadOrder[0];
        p.MoveBottomCommand.Execute(null);
        await p.SaveCommand.ExecuteAsync(null);
        p.Selected = p.Items.First(i => i.Name == profileName && !i.IsCloud);
        dialogs.ProfilePicks.Enqueue([copyName]);
        await p.CopyLoadOrderCommand.ExecuteAsync(null);
        var copy = p.Items.First(i => i.Name == copyName);
        bool copied = Dispatch.Core.Profiles.ProfileRepository.ReadLoadOrder(copy.Profile).TopFirst.Select(e => e.PackageId).SequenceEqual(order);
        step($"copy load order {profileName} → {copyName}: {copied}; message: {p.Message}");

        var tidy = new Dispatch.Core.Safety.BackupService(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dispatch", "backups"));
        tidy.BackupWholeFolder(copy.Profile, Dispatch.Core.Safety.BackupReason.Manual, move: true);
        await vm.RefreshCommand.ExecuteAsync(null);
        Directory.Delete(dir, recursive: true);

        return installed && dupes && filter && notes && searchable && uninstalled && copied;
    }

    /// <summary>
    /// Multi-select bulk move (discarded), add/remove two mods from the Mods tab (then the original file is restored),
    /// and delete + restore a temporary copy.
    /// </summary>
    private static async Task<bool> BulkAndDeleteAsync(MainViewModel vm, AutoYesDialogs dialogs, string profileName, Action<string> step)
    {
        var p = vm.Profiles;
        p.Selected = p.Items.First(i => i.Name == profileName && !i.IsCloud);
        string sii = p.Selected.Profile.ProfileSiiPath;
        byte[] originalBytes = File.ReadAllBytes(sii);
        var original = p.LoadOrder.Select(l => l.PackageId).ToList();

        // 1) bulk move: the first two rows to the bottom, as a block
        p.SelectedEntries.Clear();
        p.SelectedEntries.Add(p.LoadOrder[0]);
        p.SelectedEntries.Add(p.LoadOrder[1]);
        p.MoveBottomCommand.Execute(null);
        var moved = p.LoadOrder.Select(l => l.PackageId).ToList();
        bool bulkMove = moved.SequenceEqual(original.Skip(2).Concat(original.Take(2)));
        p.DiscardCommand.Execute(null);
        step($"bulk move of 2 selected rows to bottom: {bulkMove}; discarded: {!p.IsDirty}");

        // 2) Mods tab: add two mods that aren't active, then remove them
        var mods = vm.Mods;
        mods.TypeFilter = Resources.Strings.AllTypes;
        mods.Filter = "";
        var extra = mods.Items.Where(m => !original.Contains(m.PackageId) && m.Mod.IsReadable).Take(2).ToList();
        mods.SelectedMods.Clear();
        foreach (var m in extra)
        {
            mods.SelectedMods.Add(m);
        }

        var target = vm.Profiles.Items.First(i => i.Name == profileName && !i.IsCloud);
        await mods.AddToProfileCommand.ExecuteAsync(target);
        target = vm.Profiles.Items.First(i => i.Name == profileName && !i.IsCloud);
        var afterAdd = Dispatch.Core.Profiles.ProfileRepository.ReadLoadOrder(target.Profile).TopFirst.Select(e => e.PackageId).ToList();
        bool added = afterAdd.SequenceEqual(extra.Select(m => m.PackageId).Concat(original));
        step($"add 2 mods from Mods tab → top of {profileName}: {added}; message: {mods.Message}");

        var removeTargets = mods.RemoveTargets.Select(t => t.Name).ToList();
        await mods.RemoveFromProfileCommand.ExecuteAsync(target);
        target = vm.Profiles.Items.First(i => i.Name == profileName && !i.IsCloud);
        var afterRemove = Dispatch.Core.Profiles.ProfileRepository.ReadLoadOrder(target.Profile).TopFirst.Select(e => e.PackageId).ToList();
        bool removed = afterRemove.SequenceEqual(original) && removeTargets.Contains(profileName);
        step($"remove them again (offered for: {string.Join(", ", removeTargets)}): {removed}");

        // Put the game's own original file back (the edits above left a plain-text file with the same order).
        vm.Profiles.Selected = target;
        vm.Profiles.SelectedBackup = vm.Profiles.Backups
            .Where(b => b.Backup.Reason == Dispatch.Core.Safety.BackupReason.LoadOrderSave)
            .OrderBy(b => b.Backup.CreatedUtc).Last(b => File.ReadAllBytes(Path.Combine(
                Dispatch.Core.Safety.BackupService.ContentDir(b.Backup), "profile.sii")).AsSpan().SequenceEqual(originalBytes));
        await vm.Profiles.RestoreBackupCommand.ExecuteAsync(null);
        bool bytesBack = File.ReadAllBytes(sii).AsSpan().SequenceEqual(originalBytes);
        step($"original file restored byte-for-byte: {bytesBack}");

        // 3) delete: make a throwaway copy, delete it, restore it, tidy up
        string delName = profileName + "Del";
        vm.Profiles.Selected = vm.Profiles.Items.First(i => i.Name == profileName && !i.IsCloud);
        dialogs.ProfileDialogAnswers.Enqueue((delName, false));
        await vm.Profiles.DuplicateCommand.ExecuteAsync(null);
        vm.Profiles.Selected = vm.Profiles.Items.First(i => i.Name == delName);
        string delDir = vm.Profiles.Selected.Profile.MainDir;
        await vm.Profiles.DeleteProfileCommand.ExecuteAsync(null);
        var deletedBackup = vm.Profiles.FullBackups.FirstOrDefault(b => b.Backup.ProfileName == delName &&
                                                                         b.Backup.Reason == Dispatch.Core.Safety.BackupReason.Delete);
        bool deleted = !Directory.Exists(delDir) && vm.Profiles.Items.All(i => i.Name != delName) && deletedBackup is not null;
        step($"delete {delName}: gone from the game and kept as a backup: {deleted}");

        vm.Profiles.SelectedFullBackup = deletedBackup;
        await vm.Profiles.RestoreFolderCommand.ExecuteAsync(null);
        bool undeleted = Directory.Exists(delDir);
        var tidy = new Dispatch.Core.Safety.BackupService(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dispatch", "backups"));
        tidy.BackupWholeFolder(vm.Profiles.Items.First(i => i.Name == delName).Profile, Dispatch.Core.Safety.BackupReason.Manual, move: true);
        await vm.RefreshCommand.ExecuteAsync(null);
        step($"restore deleted profile: {undeleted}; tidied: {vm.Profiles.Items.All(i => i.Name != delName)}");

        return bulkMove && added && removed && bytesBack && deleted && undeleted;
    }

    /// <summary>M4: save a preset → export → import → apply into the editor → discard → delete. No game files are written.</summary>
    private static bool Presets(MainViewModel vm, AutoYesDialogs dialogs, string profileName, string resultFile, Action<string> step)
    {
        var p = vm.Profiles;
        p.Selected = p.Items.First(i => i.Name == profileName && !i.IsCloud);
        var original = p.LoadOrder.Select(l => l.PackageId).ToList();

        dialogs.TextAnswers.Enqueue("SelfTest preset");
        p.SavePresetCommand.Execute(null);
        var saved = p.Presets.FirstOrDefault(x => x.Name == "SelfTest preset");
        step($"preset saved: {saved is not null} ({saved?.Preset.Mods.Count} mods)");

        string exported = Path.ChangeExtension(resultFile, ".dispatch-preset.json");
        dialogs.FileAnswers.Enqueue(exported);
        p.SelectedPreset = saved;
        p.ExportPresetCommand.Execute(null);
        dialogs.FileAnswers.Enqueue(exported);
        p.ImportPresetCommand.Execute(null);
        int copies = p.Presets.Count(x => x.Name == "SelfTest preset");
        step($"exported and re-imported: {File.Exists(exported) && copies == 2}");

        p.SelectedEntry = p.LoadOrder[0];
        p.MoveBottomCommand.Execute(null); // change the order first…
        p.SelectedPreset = p.Presets.First(x => x.Name == "SelfTest preset");
        p.ApplyPresetCommand.Execute(null); // …then the preset puts it back
        bool applied = p.LoadOrder.Select(l => l.PackageId).SequenceEqual(original) && p.IsDirty;
        step($"applied into editor (unsaved): {applied}; message: {p.Message}");
        p.DiscardCommand.Execute(null);
        bool discarded = !p.IsDirty && p.LoadOrder.Select(l => l.PackageId).SequenceEqual(original);

        while (p.Presets.FirstOrDefault(x => x.Name == "SelfTest preset") is { } item)
        {
            p.SelectedPreset = item;
            p.DeletePresetCommand.Execute(null);
        }

        step($"discarded: {discarded}; test presets deleted: {p.Presets.All(x => x.Name != "SelfTest preset")}");
        return saved is not null && copies == 2 && applied && discarded;
    }

    /// <summary>M3: duplicate (fresh start) → rename the copy → undo the rename → tidy up (moved into backups).</summary>
    private static async Task<bool> DuplicateRenameAsync(MainViewModel vm, AutoYesDialogs dialogs, string profileName, Action<string> step)
    {
        string copyName = profileName + "Copy", renName = profileName + "Ren";
        var p = vm.Profiles;
        if (p.Items.Any(i => i.Name == copyName || i.Name == renName))
        {
            throw new InvalidOperationException($"{copyName} or {renName} already exists; delete it first.");
        }

        p.Selected = p.Items.First(i => i.Name == profileName && !i.IsCloud);
        dialogs.ProfileDialogAnswers.Enqueue((copyName, false));
        await p.DuplicateCommand.ExecuteAsync(null);
        var copy = vm.Profiles.Items.FirstOrDefault(i => i.Name == copyName);
        bool duplicated = copy is not null && copy.Profile.SaveCount == 0 &&
                          copy.LoadOrder!.TopFirst.Select(e => e.PackageId)
                              .SequenceEqual(vm.Profiles.Items.First(i => i.Name == profileName).LoadOrder!.TopFirst.Select(e => e.PackageId));
        step($"duplicate → {copyName}: {duplicated} (selected: {vm.Profiles.Selected?.Name}, saves: {copy?.Profile.SaveCount})");

        vm.Profiles.Selected = vm.Profiles.Items.First(i => i.Name == copyName);
        string copyDir = vm.Profiles.Selected.Profile.MainDir;
        dialogs.ProfileDialogAnswers.Enqueue((renName, true));
        await vm.Profiles.RenameCommand.ExecuteAsync(null);
        bool renamed = vm.Profiles.Items.Any(i => i.Name == renName) && vm.Profiles.Items.All(i => i.Name != copyName) &&
                       !Directory.Exists(copyDir);
        var full = vm.Profiles.FullBackups.FirstOrDefault(b => b.Backup.ProfileName == copyName);
        step($"rename → {renName}: {renamed}; original kept as full backup: {full is not null}");

        vm.Profiles.SelectedFullBackup = full;
        await vm.Profiles.RestoreFolderCommand.ExecuteAsync(null);
        bool undone = Directory.Exists(copyDir) && vm.Profiles.Items.Any(i => i.Name == copyName);
        step($"undo rename (restore folder): {undone}");

        // Tidy up: move the two test profiles into Dispatch's backups (nothing is deleted).
        var backups = new Dispatch.Core.Safety.BackupService(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dispatch", "backups"));
        foreach (var item in vm.Profiles.Items.Where(i => i.Name == copyName || i.Name == renName).ToList())
        {
            backups.BackupWholeFolder(item.Profile, Dispatch.Core.Safety.BackupReason.Manual, move: true);
        }

        await vm.RefreshCommand.ExecuteAsync(null);
        bool tidy = vm.Profiles.Items.All(i => i.Name != copyName && i.Name != renName);
        step($"tidy up (moved into backups): {tidy}; infos: {dialogs.Infos.Count}");
        return duplicated && renamed && undone && tidy;
    }
}
