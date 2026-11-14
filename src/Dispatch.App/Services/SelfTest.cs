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

    /// <summary>The last suggested order shown (accepted).</summary>
    public ViewModels.SuggestionViewModel? LastSuggestion { get; private set; }

    public bool ReviewSuggestion(ViewModels.SuggestionViewModel suggestion)
    {
        LastSuggestion = suggestion;
        return true;
    }

    /// <summary>Map windows that would have opened (not shown; the self-test loads them itself).</summary>
    public List<ViewModels.MapViewModel> Maps { get; } = [];

    public void ShowMap(ViewModels.MapViewModel map) => Maps.Add(map);

    public List<ViewModels.ProfileMapViewModel> ProfileMaps { get; } = [];

    public void ShowProfileMap(ViewModels.ProfileMapViewModel map) => ProfileMaps.Add(map);

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
            bool v2 = await ConflictsAsync(vm, profileName, resultFile, Step) && await SwitchGameAsync(vm, profileName, Step);
            bool versions = await VersionsAsync(vm, dialogs, profileName, Step);
            bool map = await MapAsync(vm, dialogs, profileName, Step);
            m4 = m4 && bulk && v1 && v2 && versions && map;
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
    /// "Show on map" (read-only): from the profile's load order the mod is compared with the mods below and above it;
    /// from the Mods tab only with the game. Reads the real game's map (through Dispatch's own cache).
    /// </summary>
    private static async Task<bool> MapAsync(MainViewModel vm, AutoYesDialogs dialogs, string profileName, Action<string> step)
    {
        var p = vm.Profiles;
        p.Selected = p.Items.First(i => i.Name == profileName && !i.IsCloud);
        var entry = p.LoadOrder.Where(l => l.Mod is not null).OrderByDescending(l => l.Mod!.LooksLikeMap).FirstOrDefault();
        if (entry is null)
        {
            step("map: skipped (no installed mod in the profile)");
            return true;
        }

        int index = p.LoadOrder.IndexOf(entry);
        int installed = p.LoadOrder.Count(l => l.Mod is not null);
        vm.SelectedTab = 0;
        dialogs.Maps.Clear();
        entry.Mod!.ShowOnMapCommand.Execute(null);
        var fromProfile = dialogs.Maps.Single();
        await fromProfile.LoadAsync();
        var c = fromProfile.Comparison;
        bool stack = fromProfile.Request.ProfileName == profileName &&
                     fromProfile.Request.Below.Count + fromProfile.Request.Above.Count == installed - 1 &&
                     fromProfile.Request.Above.Count == p.LoadOrder.Take(index).Count(l => l.Mod is not null);
        bool read = c is not null && !fromProfile.Failed && (fromProfile.Request.InstallDir is null || c.Extent is not null);
        step($"map from profile: {entry.PackageId} (looks like a map: {entry.Mod.LooksLikeMap}), {fromProfile.Request.Below.Count} below, " +
             $"{fromProfile.Request.Above.Count} above; +{c?.AddedRoads.Count} −{c?.RemovedRoads.Count} roads, " +
             $"{c?.BackgroundRoads.Count} others; notes: {string.Join(" / ", fromProfile.Notes.Select(n => n.Text))}; stack ok: {stack}; read: {read}");

        vm.SelectedTab = 1;
        dialogs.Maps.Clear();
        entry.Mod.ShowOnMapCommand.Execute(null);
        var fromMods = dialogs.Maps.Single();
        bool gameOnly = fromMods.Request.ProfileName is null && fromMods.Request.Below.Count == 0 && fromMods.Request.Above.Count == 0;
        step($"map from Mods tab: compared with the game only: {gameOnly}");

        // Mods tab → "Show on map in profile" with a mod that isn't in the profile: as if added at the top.
        var outside = vm.Mods.Items.FirstOrDefault(m => p.LoadOrder.All(l => l.PackageId != m.PackageId));
        bool inProfile = true;
        if (outside is not null)
        {
            dialogs.Maps.Clear();
            vm.Mods.Selected = outside;
            vm.Mods.ShowOnMapInProfileCommand.Execute(p.Selected);
            var r = dialogs.Maps.Single().Request;
            inProfile = r.AddedOnTop && r.ProfileName == profileName && r.Below.Count == installed && r.Above.Count == 0;
            step($"map in profile from Mods tab ({outside.PackageId}): added on top, {r.Below.Count} below: {inProfile}");
        }

        // "Road map…": the whole network, one legend row per map mod plus the game; hiding a row redraws without it.
        vm.SelectedTab = 0;
        dialogs.ProfileMaps.Clear();
        p.ShowProfileMapCommand.Execute(null);
        var network = dialogs.ProfileMaps.Single();
        await network.LoadAsync();
        var n = network.Network;
        var game = network.Legend.LastOrDefault();
        int gameArchives = network.Request.InstallDir is { } dir ? Dispatch.Core.Maps.MapArchiveReader.GameArchives(dir).Count : 0;
        bool layered = n is not null && !network.Failed && n.Layers.Count == gameArchives + network.Request.Mods.Count &&
                       game is { IsGame: true } && (network.Request.InstallDir is null || network.HasMap);
        var before = network.Scene;
        int layersBefore = before?.Layers.Count ?? 0;
        if (game is not null)
        {
            game.IsVisible = false;
        }

        bool toggled = game is null || !game.HasRoads || (network.Scene != before && network.Scene!.Layers.Count == layersBefore - 1);
        network.ShowAllCommand.Execute(null);
        toggled = toggled && (network.Scene?.Layers.Count ?? 0) == layersBefore;
        step($"road map: {n?.RoadsByLayer.Sum(l => l.Count)} roads, legend rows {network.Legend.Count}, map {network.Request.MapName}: " +
             $"layers ok: {layered}; hiding the game and showing all again: {toggled}");
        return stack && read && gameOnly && inProfile && layered && toggled;
    }

    /// <summary>
    /// Packing on small fake "old versions" next to the fake install: pack one, rebuild it for playing, remove the
    /// rebuilt folder, pack a second one that uses the first one's data, then a "Steam update" replaces a shared file:
    /// one copy is packed again by hand, the other automatically. Only the test's own copies are ever packed (see
    /// VersionsViewModel.TestOverride). Nothing is launched.
    /// </summary>
    private static async Task<bool> PackingAsync(MainViewModel vm, string temp, string fakeSteam, Action<string> step)
    {
        var versions = vm.Versions;
        var game = vm.Game;
        var steam = versions.Steam! with { Folder = fakeSteam };
        versions.TestOverride = (fakeSteam, temp);
        try
        {
            byte[] big = new byte[3 << 20];
            new Random(7).NextBytes(big);
            File.WriteAllBytes(Path.Combine(fakeSteam, "dlc_big.scs"), big);
            byte[] oldOnly = new byte[2 << 20]; // in both old versions, not in "Steam"
            new Random(8).NextBytes(oldOnly);

            string MakeOld(string name, string baseText)
            {
                string folder = Path.Combine(temp, name);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, game.ExeRelativePath))!);
                File.Copy(Path.Combine(fakeSteam, game.ExeRelativePath), Path.Combine(folder, game.ExeRelativePath));
                File.WriteAllText(Path.Combine(folder, "base.scs"), baseText);
                File.WriteAllBytes(Path.Combine(folder, "dlc_big.scs"), big); // the same data as the "Steam" file
                File.WriteAllBytes(Path.Combine(folder, "dlc_old.scs"), oldOnly);
                return folder;
            }

            bool Rebuilt(InstallationItemViewModel i) =>
                File.Exists(i.Installation.ExePath(game)) &&
                File.ReadAllBytes(Path.Combine(i.Folder, Dispatch.Core.Games.PackedCopy.GameFolder, "dlc_big.scs")).AsSpan().SequenceEqual(big) &&
                File.ReadAllBytes(Path.Combine(i.Folder, Dispatch.Core.Games.PackedCopy.GameFolder, "dlc_old.scs")).AsSpan().SequenceEqual(oldOnly);

            InstallationItemViewModel Item(string folder) => versions.Items.First(i => i.Folder == folder);

            string old = MakeOld("old version", "base");
            bool listed = versions.AddCopyFrom(old);
            var item = Item(old);
            bool canPack = item.CanPack;
            bool packed = await versions.PackFolderAsync(game, old, steam, item.Installation.Version?.ToString(), item.Health?.OwnBytes ?? 0);
            item = Item(old);
            bool small = item.Installation.IsPacked && item.Health is { IsPacked: true, IsBroken: false } h && h.OwnBytes < (3 << 20) &&
                         !item.CanPack && item.CanShareData && versions.Available.Any(a => a.Folder == old);
            step($"versions: packed a copy ({listed}, {canPack}, {packed}, {small}): \"{item.Label}\", {item.Space}; {versions.CopyStatus}");

            bool prepared = await versions.PrepareAsync(item.Installation) && Rebuilt(item);
            versions.CleanUpRebuilt(afterGameClosed: true);
            bool cleaned = !Directory.Exists(Path.Combine(old, Dispatch.Core.Games.PackedCopy.GameFolder));

            // A second old version: its own data is only what the first one doesn't have.
            string older = MakeOld("older version", "older base");
            versions.AddCopyFrom(older);
            bool packedOlder = await versions.PackFolderAsync(game, older, steam, Item(older).Installation.Version?.ToString(), Item(older).Health?.OwnBytes ?? 0);
            var second = Item(older);
            bool sharesData = packedOlder && second.Health?.Pack is { SharedWithCopiesBytes: > 0 } && second.Health.OwnBytes < (1 << 20) &&
                              await versions.PrepareAsync(second.Installation) && Rebuilt(second);
            versions.CleanUpRebuilt(afterGameClosed: true);
            step($"versions: rebuilt for playing ({prepared}), removed after ({cleaned}), a second copy uses the first one's data ({sharesData}): {second.Space}");

            // "Steam update": both copies now hold on to the old big file.
            string staged = Path.Combine(temp, "staged-big.scs");
            File.WriteAllText(staged, "a new version of the big file");
            File.Move(staged, Path.Combine(fakeSteam, "dlc_big.scs"), overwrite: true);
            await vm.RefreshCommand.ExecuteAsync(null);
            item = Item(old);
            bool stale = item.NeedsRepack && Item(older).NeedsRepack && item.CanPack && item.CanLaunch;
            bool repacked = await versions.PackFolderAsync(game, old, steam, item.Installation.Version?.ToString(), item.Health?.OwnBytes ?? 0);
            item = Item(old);
            bool fresh = item.Health?.Pack is { StaleBaseBytes: 0 } && await versions.PrepareAsync(item.Installation) && Rebuilt(item);
            versions.CleanUpRebuilt(afterGameClosed: true);

            // The other one is packed again by itself.
            vm.Settings.Current.AutoRepackCopies = true;
            versions.StartAutoRepack();
            bool started = versions.IsAutoPacking;
            await versions.AutoRepackTask;
            vm.Settings.Current.AutoRepackCopies = false;
            second = Item(older);
            bool auto = started && !versions.IsAutoPacking && !second.NeedsRepack && second.Health?.Pack is { StaleBaseBytes: 0 } &&
                        await versions.PrepareAsync(second.Installation) && Rebuilt(second);
            versions.CleanUpRebuilt(afterGameClosed: true);
            step($"versions: Steam update noticed ({stale}), packed again by hand ({repacked}, {fresh}) and automatically ({auto}): {versions.CopyStatus}");
            versions.RemoveCopy(old);
            versions.RemoveCopy(older);
            return listed && canPack && packed && small && prepared && cleaned && sharesData && stale && repacked && fresh && auto;
        }
        finally
        {
            vm.Settings.Current.AutoRepackCopies = false;
            versions.TestOverride = null;
        }
    }

    /// <summary>
    /// Game versions: lists a fake copy (a temp folder with a copy of the game's exe, so it has the same version),
    /// pins the test profile to it and back to automatic, and copies a small fake install with the real copier.
    /// Nothing is launched, the Steam install is only read, and settings.json isn't written (settings are read-only
    /// during the self-test). The temp folders are deleted afterwards.
    /// </summary>
    private static async Task<bool> VersionsAsync(MainViewModel vm, AutoYesDialogs dialogs, string profileName, Action<string> step)
    {
        var versions = vm.Versions;
        var game = vm.Game;
        var steam = versions.Steam;
        if (steam?.Version is null)
        {
            step("versions: no Steam install with a version, skipped");
            return true;
        }

        string temp = Path.Combine(Path.GetTempPath(), "DispatchTest-versions-" + Guid.NewGuid().ToString("N")[..8]);
        int errorsBefore = dialogs.Errors.Count;
        try
        {
            string copy = Path.Combine(temp, "copy");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(copy, game.ExeRelativePath))!);
            File.Copy(steam.ExePath(game), Path.Combine(copy, game.ExeRelativePath));
            int before = versions.Items.Count;
            bool added = versions.AddCopyFrom(copy) && versions.Items.Count == before + 1 &&
                         versions.Items.Any(i => i.Folder == copy && i.Installation.Version == steam.Version);
            bool refusesTwice = !versions.AddCopyFrom(copy) && !versions.AddCopyFrom(steam.Folder);
            step($"versions: copy listed ({added}), refuses duplicates and the Steam install ({refusesTwice}); {versions.Items.Count} installations");

            var p = vm.Profiles;
            p.Selected = p.Items.First(i => i.Name == profileName && !i.IsCloud);
            bool autoSteam = p.HasVersionChoice && p.SelectedLaunchChoice?.Key == "" && p.LaunchInstallation == steam &&
                             vm.LaunchLabel.Contains(steam.Version.ToString(), StringComparison.Ordinal);
            step($"versions: automatic picks Steam for the same version ({autoSteam}): \"{p.SelectedLaunchChoice?.Label}\", button \"{vm.LaunchLabel}\", hint \"{p.LaunchHint}\"");

            string key = AppSettings.ProfileVersionKey(game, cloud: false, p.Selected.Profile.FolderName);
            p.SelectedLaunchChoice = p.LaunchChoices.First(c => c.Key == copy);
            bool pinned = p.LaunchInstallation?.Folder == copy && vm.Settings.Current.ProfileVersions.GetValueOrDefault(key) == copy;
            await vm.RefreshCommand.ExecuteAsync(null);
            p = vm.Profiles;
            p.Selected = p.Items.First(i => i.Name == profileName && !i.IsCloud);
            bool keptPin = p.SelectedLaunchChoice?.Key == copy && p.LaunchInstallation?.Folder == copy;
            p.SelectedLaunchChoice = p.LaunchChoices[0];
            bool backToAuto = !vm.Settings.Current.ProfileVersions.ContainsKey(key) && p.LaunchInstallation == steam;
            step($"versions: pin to the copy ({pinned}), kept after refresh ({keptPin}), back to automatic ({backToAuto})");

            // The real copier on a small fake install (a folder with the exe and two files).
            string fakeSteam = Path.Combine(temp, "fake install");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fakeSteam, game.ExeRelativePath))!);
            File.Copy(steam.ExePath(game), Path.Combine(fakeSteam, game.ExeRelativePath));
            File.WriteAllText(Path.Combine(fakeSteam, "base.scs"), "base");
            File.WriteAllText(Path.Combine(fakeSteam, "dlc_test.scs"), "dlc");
            string dest = Path.Combine(temp, "kept", $"{game.ShortName} {steam.Version}");
            bool copied = await versions.CopyToAsync(game, steam with { Folder = fakeSteam }, dest, 0) &&
                          Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Length == 4 && // 3 + Dispatch's record
                          File.Exists(Path.Combine(dest, Dispatch.Core.Games.GameCopyRecord.FileName)) &&
                          !Directory.Exists(dest + Dispatch.Core.Games.GameCopier.PartialSuffix) &&
                          versions.Items.Any(i => i.Folder == dest);
            bool refusesExisting = !await versions.CopyToAsync(game, steam with { Folder = fakeSteam }, dest, 0);
            step($"versions: kept a copy of a small install ({copied}), refuses an existing folder ({refusesExisting}): {versions.CopyStatus}");

            // Same drive: the files are shared. A replaced file (how Steam updates) keeps the copy intact; a file
            // written in place breaks it, and it's then no longer offered for launching.
            var kept = versions.Items.First(i => i.Folder == dest);
            bool shared = kept.Health is { MadeByDispatch: true, SharedAtCopy: 3, StillShared: 3, IsBroken: false };
            string staged = Path.Combine(temp, "staged.scs");
            File.WriteAllText(staged, "base v2");
            File.Move(staged, Path.Combine(fakeSteam, "base.scs"), overwrite: true);
            await vm.RefreshCommand.ExecuteAsync(null);
            kept = versions.Items.First(i => i.Folder == dest);
            bool survivesReplace = kept.Health is { IsBroken: false, StillShared: 2 } && kept.CanLaunch &&
                                   File.ReadAllText(Path.Combine(dest, "base.scs")) == "base";
            File.AppendAllText(Path.Combine(fakeSteam, "dlc_test.scs"), " changed in place");
            await vm.RefreshCommand.ExecuteAsync(null);
            kept = versions.Items.First(i => i.Folder == dest);
            bool brokenNoticed = kept.IsBroken && !versions.Available.Any(a => a.Folder == dest) && kept.Detail.Length > 0;
            step($"versions: shared ({shared}; {kept.Space}), survives a replaced file ({survivesReplace}), in-place change marks it broken ({brokenNoticed})");

            bool packed = await PackingAsync(vm, temp, fakeSteam, step);

            versions.RemoveCopy(dest);
            versions.RemoveCopy(copy);
            bool removed = versions.Items.Count == before && Directory.Exists(copy) && Directory.Exists(dest);
            step($"versions: removed from the list, folders left on disk ({removed}); version row shown: {vm.Profiles.HasVersionChoice}");

            // The refusals above are expected errors, not failures.
            int expected = dialogs.Errors.Count - errorsBefore;
            dialogs.Errors.RemoveRange(errorsBefore, expected);
            return added && refusesTwice && autoSteam && pinned && keptPin && backToAuto && copied && refusesExisting && shared && survivesReplace &&
                   brokenNoticed && packed && removed && expected == 3;
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
                // Temp folder; Windows cleans it up eventually.
            }
        }
    }

    /// <summary>
    /// v2: switch to ATS (if installed) and back. Read-only: ATS profiles are only listed, and the saved game choice
    /// isn't changed.
    /// </summary>
    private static async Task<bool> SwitchGameAsync(MainViewModel vm, string profileName, Action<string> step)
    {
        var ats = Dispatch.Core.Games.GameDefinition.Ats;
        if (!vm.Games.Contains(ats))
        {
            step("v2 game switch: ATS not installed, skipped");
            return true;
        }

        await vm.SwitchGameAsync(ats, remember: false);
        string atsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ats.DocumentsFolderName);
        bool atsShown = vm.Game == ats && vm.GameVersion.StartsWith("ATS", StringComparison.Ordinal) &&
                        vm.Profiles.Items.All(p => p.IsCloud || p.Profile.MainDir.StartsWith(atsRoot, StringComparison.OrdinalIgnoreCase)) &&
                        vm.Profiles.Items.All(p => p.Name != profileName || p.IsCloud);
        step($"v2 switch to ATS: {atsShown} ({vm.GameVersion}, {vm.Profiles.Items.Count} profiles, {vm.Mods.ById.Count} mods)");

        await vm.SwitchGameAsync(Dispatch.Core.Games.GameDefinition.Ets2, remember: false);
        bool back = vm.Game == Dispatch.Core.Games.GameDefinition.Ets2 && vm.Profiles.Items.Any(p => p.Name == profileName);
        step($"v2 switch back to ETS2: {back}");
        return atsShown && back;
    }

    /// <summary>
    /// v2: two generated mods that share a file are installed and added (unsaved) to the test profile. The higher one
    /// must win, rows and the panel must agree, example names must resolve, and reordering re-checks. Then everything is
    /// discarded and uninstalled; the profile file is never written.
    /// </summary>
    private static async Task<bool> ConflictsAsync(MainViewModel vm, string profileName, string resultFile, Action<string> step)
    {
        const string Shared = "def/dispatch_selftest/shared.sii";
        string dir = Path.Combine(Path.GetDirectoryName(resultFile)!, "selftest-conflicts");
        Directory.CreateDirectory(dir);
        string MakeMod(string id, string unique)
        {
            string path = Path.Combine(dir, id + ".scs");
            File.Delete(path);
            using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
            foreach (var (name, text) in new[]
                     {
                         ("manifest.sii", $"SiiNunit\n{{\nmod_package : .package_name\n{{\n display_name: \"Dispatch Self-Test {id}\"\n category[]: \"other\"\n}}\n}}\n"),
                         ("readme.txt", "root-level files are metadata, not conflicts"),
                         (Shared, "SiiNunit { }"),
                         (unique, "SiiNunit { }"),
                     })
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }

            return path;
        }

        await vm.Mods.InstallFilesAsync([MakeMod("DispatchSelfTest_c1", "def/dispatch_selftest/one.sii"),
                                         MakeMod("DispatchSelfTest_c2", "def/dispatch_selftest/two.sii")]);
        var c1 = vm.Mods.ById.GetValueOrDefault("DispatchSelfTest_c1");
        var c2 = vm.Mods.ById.GetValueOrDefault("DispatchSelfTest_c2");
        if (c1 is null || c2 is null)
        {
            step("v2: test mods weren't installed");
            return false;
        }

        var p = vm.Profiles;
        p.Selected = p.Items.First(i => i.Name == profileName && !i.IsCloud);
        string sii = p.Selected.Profile.ProfileSiiPath;
        byte[] before = File.ReadAllBytes(sii);
        p.InsertMany([c1, c2], 0); // c1 at the top, c2 right below it
        await p.ConflictCheck;
        var pair = p.LastConflictReport.Pairs.SingleOrDefault(c => c.LoserId == c2.PackageId);
        bool found = pair is { WinnerId: "DispatchSelfTest_c1", FileCount: 1 };
        p.SelectedConflict = p.Conflicts.FirstOrDefault(c => c.Conflict.LoserId == c2.PackageId);
        for (int i = 0; i < 50 && p.SelectedConflict is { } sc && (sc.Examples.Length == 0 || sc.Examples == Resources.Strings.Loading); i++)
        {
            await Task.Delay(100);
        }

        bool names = p.SelectedConflict?.Examples == Shared;
        bool badges = p.LoadOrder.First(l => l.PackageId == c1.PackageId).HasConflicts &&
                      p.LoadOrder.First(l => l.PackageId == c2.PackageId).HasConflicts;
        step($"v2 test mods sharing one file: found c1 over c2: {found}; row badges: {badges}; example: {p.SelectedConflict?.Examples}");

        bool Consistent()
        {
            var pos = p.LoadOrder.ToDictionary(l => l.PackageId, l => l.Position);
            var report = p.LastConflictReport;
            bool higherWins = report.Pairs.All(c => pos[c.WinnerId] < pos[c.LoserId]);
            var involved = report.Pairs.SelectMany(c => new[] { c.WinnerId, c.LoserId }).ToHashSet();
            bool rows = p.LoadOrder.All(l => l.HasConflicts == involved.Contains(l.PackageId));
            return higherWins && rows && p.Conflicts.Count == report.Pairs.Count && !p.IsCheckingConflicts;
        }

        bool first = Consistent();
        step($"v2 conflicts: {p.LastConflictReport.Pairs.Count} pairs; {p.ConflictSummary}; consistent: {first}");

        // Reverse the whole list. Who wins changes, but not which mods are involved or how many file copies are
        // hidden in total (a file in k mods hides k-1 copies, whatever the order).
        static (int Hidden, string Ids) Totals(Dispatch.Core.Mods.ConflictReport r) =>
            (r.Pairs.Sum(c => c.FileCount),
             string.Join(",", r.Pairs.SelectMany(c => new[] { c.WinnerId, c.LoserId }).Distinct().Order(StringComparer.Ordinal)));
        var oldTotals = Totals(p.LastConflictReport);
        for (int i = 1; i < p.LoadOrder.Count; i++)
        {
            p.LoadOrder.Move(i, 0);
        }

        await Task.Delay(50); // the check is debounced; wait for the one started by the last move
        await p.ConflictCheck;
        bool reversed = Consistent() && Totals(p.LastConflictReport) == oldTotals;
        bool flippedTest = p.LastConflictReport.Pairs.Any(c => c is { WinnerId: "DispatchSelfTest_c2", LoserId: "DispatchSelfTest_c1" });
        reversed = reversed && flippedTest;
        step($"v2 reversed order: {p.LastConflictReport.Pairs.Count} pairs, {oldTotals.Hidden} hidden copies before and after, c2 now wins: {reversed}");

        p.ConflictsOnlySelected = true;
        p.SelectedEntry = p.LoadOrder.FirstOrDefault(l => l.HasConflicts) ?? p.LoadOrder[0];
        bool filtered = p.Conflicts.All(c => c.Conflict.WinnerId == p.SelectedEntry.PackageId || c.Conflict.LoserId == p.SelectedEntry.PackageId);
        p.ConflictsOnlySelected = false;
        p.DiscardCommand.Execute(null);
        await p.ConflictCheck;
        bool untouched = File.ReadAllBytes(sii).AsSpan().SequenceEqual(before) && !p.IsDirty && Consistent();
        step($"v2 filter by selected mod: {filtered}; discarded, profile untouched: {untouched}");

        vm.Mods.SelectedMods.Clear();
        vm.Mods.SelectedMods.Add(vm.Mods.ById[c1.PackageId]);
        vm.Mods.SelectedMods.Add(vm.Mods.ById[c2.PackageId]);
        await vm.Mods.UninstallCommand.ExecuteAsync(null);
        bool gone = !vm.Mods.ById.ContainsKey(c1.PackageId) && !vm.Mods.ById.ContainsKey(c2.PackageId);
        Directory.Delete(dir, recursive: true);
        step($"v2 test mods uninstalled: {gone}");
        return found && names && badges && first && reversed && filtered && untouched && gone;
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

        // Mod library, with only the two test mods (no profile uses them): both in, one back by hand.
        string modDir = Path.GetDirectoryName(a!.Mod.Path)!;
        string library = Path.Combine(Path.GetDirectoryName(modDir)!, Dispatch.Core.Mods.ModLibrary.FolderName);
        bool libraryExisted = Directory.Exists(library);
        vm.Mods.SelectedMods.Clear();
        vm.Mods.SelectedMods.Add(vm.Mods.ById["DispatchSelfTest_a"]);
        vm.Mods.SelectedMods.Add(vm.Mods.ById["DispatchSelfTest_b"]);
        await vm.Mods.MoveToLibraryCommand.ExecuteAsync(null);
        bool inLibrary = vm.Mods.ById["DispatchSelfTest_a"].InLibrary && vm.Mods.ById["DispatchSelfTest_b"].InLibrary &&
                         File.Exists(Path.Combine(library, "DispatchSelfTest_b.scs")) && !File.Exists(Path.Combine(modDir, "DispatchSelfTest_b.scs"));
        vm.Mods.Show = Resources.Strings.ShowInLibrary;
        bool libraryFilter = vm.Mods.Items.Any(m => m.PackageId == "DispatchSelfTest_a") && vm.Mods.Items.All(m => m.InLibrary);
        vm.Mods.Show = Resources.Strings.ShowAll;
        vm.Mods.SelectedMods.Clear();
        vm.Mods.SelectedMods.Add(vm.Mods.ById["DispatchSelfTest_a"]);
        await vm.Mods.MoveBackCommand.ExecuteAsync(null);
        bool movedBack = !vm.Mods.ById["DispatchSelfTest_a"].InLibrary && File.Exists(Path.Combine(modDir, "DispatchSelfTest_a.scs"));
        step($"library: moved in ({inLibrary}), filter ({libraryFilter}), moved back by hand ({movedBack}): {vm.Mods.LibrarySummary}");

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

        // A profile starts using a mod that's in the library: it's back in mod\ by itself.
        await p.AddModsAsync([vm.Mods.ById["DispatchSelfTest_b"]], vm.Profiles.Items.First(i => i.Name == copyName));
        bool broughtBack = !vm.Mods.ById["DispatchSelfTest_b"].InLibrary && File.Exists(Path.Combine(modDir, "DispatchSelfTest_b.scs")) &&
                           !File.Exists(Path.Combine(library, "DispatchSelfTest_b.scs"));
        step($"library: brought back when a profile uses it ({broughtBack}): {vm.Mods.Message}");
        copy = vm.Profiles.Items.First(i => i.Name == copyName);

        // Multiplayer panel for the copy (the clipboard isn't touched).
        p.Selected = vm.Profiles.Items.First(i => i.Name == copyName);
        bool multiplayer = p.Multiplayer.Count == p.LoadOrder.Count && p.ConvoySummary.Length > 0 && p.CopyConvoyListCommand.CanExecute(null) &&
                           p.Multiplayer.Any(m => m.Entry.PackageId == "DispatchSelfTest_b" && !m.IsConvoyOptional && !m.IsTruckersMpSupported);
        step($"multiplayer: {multiplayer}: {p.ConvoySummary} {p.TruckersMpSummary}");

        // Health: a mod that isn't installed shows up, and its fix removes it again (a backed-up change to the copy).
        await p.FixProfileAsync(copy, current => Dispatch.Core.Profiles.LoadOrderEdits.AddToTop(current, [new("DispatchSelfTest_missing", "Missing test mod")]), "added");
        await vm.Health.Checked;
        var health = vm.Health.Items.First(i => i.Name == copyName);
        var missingIssue = health.Issues.FirstOrDefault(i => i.Text.Contains("Missing test mod", StringComparison.Ordinal) && i.HasPrimary);
        bool healthFound = missingIssue is not null && !health.IsHealthy;
        if (missingIssue is not null)
        {
            await missingIssue.Primary!.ExecuteAsync(null);
        }

        await vm.Health.Checked;
        copy = vm.Profiles.Items.First(i => i.Name == copyName);
        bool healthFixed = !copy.LoadOrder!.TopFirst.Any(e => e.PackageId == "DispatchSelfTest_missing") &&
                           !vm.Health.Items.First(i => i.Name == copyName).Issues.Any(i => i.Text.Contains("Missing test mod", StringComparison.Ordinal));
        // Suggested order: the copy has an "other" mod above its sound mods, so the sounds move up; then it's discarded.
        p.Selected = vm.Profiles.Items.First(i => i.Name == copyName);
        var before = p.LoadOrder.Select(l => l.PackageId).ToList();
        p.SuggestOrderCommand.Execute(null);
        var review = dialogs.LastSuggestion;
        bool suggested = p.IsDirty && p.LoadOrder[^1].PackageId == "DispatchSelfTest_b" && p.LoadOrder.Count == before.Count &&
                         review is { OnlyMoved: true } && review.Rows.Count > 0 && review.Rows.All(r => r.IsMoved && r.MovedUp && r.Detail.Length > 0);
        p.DiscardCommand.Execute(null);
        bool discarded = !p.IsDirty && p.LoadOrder.Select(l => l.PackageId).SequenceEqual(before);
        step($"suggested order: applied to the editor ({suggested}), discarded ({discarded}): {string.Join(", ", before)}");

        var original = vm.Health.Items.First(i => i.Name == profileName && !i.Profile.IsCloud);
        var save = Dispatch.Core.Profiles.SaveReader.Newest(original.Profile.Profile);
        step($"health: missing mod found ({healthFound}) and fixed ({healthFixed}); {vm.Health.Summary} {profileName}: {original.Status} " +
             $"[{string.Join("; ", original.Issues.Select(i => i.Title))}]; newest save {save?.Label ?? "none"} with {save?.Mods.Count() ?? 0} mods");

        var tidy = new Dispatch.Core.Safety.BackupService(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dispatch", "backups"));
        tidy.BackupWholeFolder(copy.Profile, Dispatch.Core.Safety.BackupReason.Manual, move: true);
        await vm.RefreshCommand.ExecuteAsync(null);
        Directory.Delete(dir, recursive: true);

        // Uninstall both (to the Recycle Bin); they aren't active anywhere now, so no profile question.
        vm.Mods.SelectedMods.Clear();
        vm.Mods.SelectedMods.Add(vm.Mods.ById["DispatchSelfTest_a"]);
        vm.Mods.SelectedMods.Add(vm.Mods.ById["DispatchSelfTest_b"]);
        await vm.Mods.UninstallCommand.ExecuteAsync(null);
        bool uninstalled = !vm.Mods.ById.ContainsKey("DispatchSelfTest_a") && !vm.Mods.ById.ContainsKey("DispatchSelfTest_b");
        step($"uninstall to Recycle Bin: {uninstalled}");

        // The library folder goes again if the test made it (only the journal is left in it).
        if (!libraryExisted && Directory.Exists(library) &&
            Directory.EnumerateFileSystemEntries(library).All(f => Path.GetFileName(f) == Dispatch.Core.Mods.ModLibrary.JournalFile))
        {
            Directory.Delete(library, recursive: true);
        }

        return installed && dupes && filter && notes && searchable && inLibrary && libraryFilter && movedBack && broughtBack && multiplayer &&
               healthFound && healthFixed && suggested && discarded && uninstalled && copied;
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
