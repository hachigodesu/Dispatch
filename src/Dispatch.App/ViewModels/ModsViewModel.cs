using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Resources;
using Dispatch.App.Services;
using Dispatch.Core.Mods;
using Dispatch.Core.Safety;
using Serilog;

namespace Dispatch.App.ViewModels;

/// <summary>
/// Mods tab: every installed mod with search, a type filter, multi-selection and a right-click menu to add the selected
/// mods to (or remove them from) a profile.
/// </summary>
public sealed partial class ModsViewModel : ObservableObject
{
    private readonly ProfilesViewModel _profiles;
    private readonly IDialogs _dialogs;
    private readonly Func<ModInstaller> _installer;
    private ModNotesStore _notes;
    private readonly Func<Task> _refresh;
    private readonly Func<IReadOnlyList<ModInfo>, string, IReadOnlyList<LibraryMove>> _move;
    private List<ModItemViewModel> _all = [];
    private string? _modDir;
    private string? _libraryDir;
    private HashSet<string> _used = [];
    private bool _allProfilesRead;

    /// <param name="move">Moves mods into a folder (the library, or back to mod\); see <see cref="ModLibrary.Move"/>.</param>
    public ModsViewModel(ProfilesViewModel profiles, IDialogs dialogs, Func<ModInstaller> installer, ModNotesStore notes, Func<Task> refresh,
                         Func<IReadOnlyList<ModInfo>, string, IReadOnlyList<LibraryMove>> move)
    {
        _profiles = profiles;
        _dialogs = dialogs;
        _installer = installer;
        _notes = notes;
        _refresh = refresh;
        _move = move;
        SelectedMods.CollectionChanged += (_, _) => OnSelectionChanged();
        ShowOptions = [Strings.ShowAll, Strings.ShowDuplicates, Strings.ShowUnused, Strings.ShowInLibrary, Strings.ShowProblems, Strings.ShowGameWarnings];
        _show = Strings.ShowAll;
    }

    /// <summary>Switches to another game's notes (takes effect on the next Load).</summary>
    public void UseNotes(ModNotesStore notes) => _notes = notes;

    /// <summary>"All mods", "Duplicates and older copies", "Not used in any profile", "Incompatible or missing DLC".</summary>
    public IReadOnlyList<string> ShowOptions { get; }

    [ObservableProperty]
    private string _show;

    partial void OnShowChanged(string value) => ApplyFilter();

    public ObservableCollection<ModItemViewModel> Items { get; } = [];

    /// <summary>All selected mods (multi-select; kept in sync by Behaviors.MultiSelect).</summary>
    public ObservableCollection<ModItemViewModel> SelectedMods { get; } = [];

    /// <summary>Profiles offered under "Add to profile".</summary>
    public ObservableCollection<ProfileItemViewModel> AddTargets { get; } = [];

    /// <summary>Profiles offered under "Remove from profile": those where at least one selected mod is active.</summary>
    public ObservableCollection<ProfileItemViewModel> RemoveTargets { get; } = [];

    /// <summary>"All types" plus every type present, e.g. "Map", "Sound".</summary>
    public ObservableCollection<string> TypeOptions { get; } = [];

    public IReadOnlyDictionary<string, ModItemViewModel> ById { get; private set; } = new Dictionary<string, ModItemViewModel>();

    [ObservableProperty]
    private ModItemViewModel? _selected;

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private string _typeFilter = Strings.AllTypes;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(MoveUnusedToLibraryCommand), nameof(MoveAllBackCommand))]
    private bool _isWorking;

    /// <summary>"12 mods in the library (3.4 GB)", or empty while it's empty.</summary>
    [ObservableProperty]
    private string _librarySummary = "";

    public string SelectionLabel => SelectedMods.Count > 1
        ? string.Format(CultureInfo.CurrentCulture, Strings.SelectedCount, SelectedMods.Count)
        : "";

    public bool HasAddTargets => AddTargets.Count > 0;

    public bool HasRemoveTargets => RemoveTargets.Count > 0;

    public void Load(GameSnapshot snapshot)
    {
        // Which profiles use each mod (shown in the details panel).
        var usedIn = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var p in snapshot.Profiles)
        {
            foreach (var e in p.LoadOrder?.TopFirst ?? [])
            {
                if (!usedIn.TryGetValue(e.PackageId, out var list))
                {
                    usedIn[e.PackageId] = list = [];
                }

                list.Add(p.Profile.Name);
            }
        }

        _modDir = snapshot.Environment.ModDir;
        _libraryDir = snapshot.Environment.ModLibraryDir;
        _used = snapshot.UsedPackageIds;
        _allProfilesRead = snapshot.AllProfilesRead;
        var duplicates = ModDuplicates.Find(snapshot.Scan.Mods);
        var keepIds = SelectedMods.Select(m => m.PackageId).ToHashSet(StringComparer.Ordinal);
        var byId = new Dictionary<string, ModItemViewModel>(StringComparer.Ordinal);
        _all = [];
        foreach (var mod in snapshot.Scan.Mods)
        {
            var vm = new ModItemViewModel(mod, snapshot.Compatibility.GetValueOrDefault(mod.PackageId)
                                               ?? CompatibilityChecker.Evaluate(mod, snapshot.Environment.Version, snapshot.Environment.OwnedDlc, snapshot.GameLog),
                                          snapshot.NamesFromProfiles.GetValueOrDefault(mod.PackageId),
                                          usedIn.GetValueOrDefault(mod.PackageId) ?? [],
                                          _notes.Get(mod.PackageId), SaveNote, duplicates.GetValueOrDefault(mod),
                                          snapshot.GameLog?.MessagesFor(mod.PackageId) ?? [], snapshot.GameLog?.LastWriteUtc);
            _all.Add(vm);
            byId.TryAdd(mod.PackageId, vm);
        }

        _all.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        ById = byId;
        Summary = string.Format(CultureInfo.CurrentCulture, Strings.ModsSummary, _all.Count,
                                _all.Count(m => m.Mod.Source == ModSource.Local), _all.Count(m => m.Mod.Source == ModSource.Workshop));

        string keepType = TypeFilter;
        TypeOptions.Clear();
        TypeOptions.Add(Strings.AllTypes);
        foreach (var t in _all.SelectMany(m => m.TypeLabels).Distinct().Order(StringComparer.CurrentCultureIgnoreCase))
        {
            TypeOptions.Add(t);
        }

        if (_all.Any(m => m.TypeLabels.Count == 0))
        {
            TypeOptions.Add(Strings.NoType);
        }

        TypeFilter = TypeOptions.Contains(keepType) ? keepType : Strings.AllTypes;
        ApplyFilter();
        InstallCommand.NotifyCanExecuteChanged(); // the mod folder is known now
        var inLibrary = _all.Where(m => m.InLibrary).ToList();
        LibrarySummary = inLibrary.Count == 0
            ? ""
            : string.Format(CultureInfo.CurrentCulture, Strings.LibrarySummary, inLibrary.Count, FormatSize(inLibrary.Sum(m => m.Mod.SizeBytes)));
        MoveUnusedToLibraryCommand.NotifyCanExecuteChanged();
        MoveAllBackCommand.NotifyCanExecuteChanged();
        OpenLibraryCommand.NotifyCanExecuteChanged();

        // Keep the selection across refreshes (e.g. after "Add to profile").
        SelectedMods.Clear();
        foreach (var m in Items.Where(m => keepIds.Contains(m.PackageId)))
        {
            SelectedMods.Add(m);
        }

        OnSelectionChanged();
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnTypeFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedChanged(ModItemViewModel? value) => value?.EnsureDescriptionLoaded();

    private void ApplyFilter()
    {
        string f = Filter.Trim();
        string type = TypeFilter;
        var keep = Selected;
        Items.Clear();
        foreach (var m in _all.Where(m => m.Matches(f) && MatchesType(m, type) && MatchesShow(m)))
        {
            Items.Add(m);
        }

        Selected = keep is not null && Items.Contains(keep) ? keep : null;
    }

    private bool MatchesShow(ModItemViewModel m) =>
        Show == Strings.ShowDuplicates ? m.IsDuplicate
        : Show == Strings.ShowUnused ? m.UsedIn.Count == 0
        : Show == Strings.ShowGameWarnings ? m.HasGameMessages
        : Show == Strings.ShowInLibrary ? m.InLibrary
        : Show != Strings.ShowProblems || m.CompatStatus is CompatStatus.VersionMismatch or CompatStatus.MissingDlc or CompatStatus.Unreadable;

    private void SaveNote(ModItemViewModel mod)
    {
        try
        {
            _notes.Set(mod.PackageId, mod.Note);
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Saving the note for {Mod} failed", mod.PackageId);
        }
    }

    private static bool MatchesType(ModItemViewModel m, string type) =>
        type == Strings.AllTypes || (type == Strings.NoType ? m.TypeLabels.Count == 0 : m.TypeLabels.Contains(type));

    public void OnGameRunningChanged()
    {
        InstallCommand.NotifyCanExecuteChanged();
        MoveUnusedToLibraryCommand.NotifyCanExecuteChanged();
        MoveAllBackCommand.NotifyCanExecuteChanged();
        OnSelectionChanged();
    }

    /// <summary>Called after profiles reload, so the right-click menu lists the current profiles.</summary>
    public void RefreshTargets() => OnSelectionChanged();

    private void OnSelectionChanged()
    {
        var selected = CurrentSelection();
        var ids = selected.Select(m => m.PackageId).ToHashSet(StringComparer.Ordinal);
        AddTargets.Clear();
        RemoveTargets.Clear();
        foreach (var p in _profiles.EditableProfiles)
        {
            AddTargets.Add(p);
            if (p.LoadOrder!.TopFirst.Any(e => ids.Contains(e.PackageId)))
            {
                RemoveTargets.Add(p);
            }
        }

        OnPropertyChanged(nameof(SelectionLabel));
        OnPropertyChanged(nameof(HasAddTargets));
        OnPropertyChanged(nameof(HasRemoveTargets));
        AddToProfileCommand.NotifyCanExecuteChanged();
        RemoveFromProfileCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        MoveToLibraryCommand.NotifyCanExecuteChanged();
        MoveBackCommand.NotifyCanExecuteChanged();
    }

    // ---- mod library -----------------------------------------------------------------------------

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("F1", CultureInfo.CurrentCulture) + " GB",
        _ => (bytes / (double)(1L << 20)).ToString("F0", CultureInfo.CurrentCulture) + " MB",
    };

    private bool CanMoveUnused() => !IsWorking && !_profiles.IsGameRunning && _libraryDir is not null &&
                                    _all.Any(m => m.Mod.Source == ModSource.Local && !m.InLibrary && !_used.Contains(m.PackageId));

    /// <summary>Moves every local mod no profile uses into the library (they come back by themselves when a profile needs them).</summary>
    [RelayCommand(CanExecute = nameof(CanMoveUnused))]
    private async Task MoveUnusedToLibraryAsync()
    {
        if (!_allProfilesRead)
        {
            _dialogs.ShowError(Strings.LibraryProfilesUnreadable);
            return;
        }

        var unused = ModLibrary.Unused(_all.Select(m => m.Mod), _used);
        if (unused.Count == 0 || !_dialogs.Confirm(Strings.LibraryMoveTitle, string.Format(CultureInfo.CurrentCulture, Strings.LibraryMoveUnusedQuestion,
                unused.Count, FormatSize(unused.Sum(m => m.SizeBytes)), _libraryDir)))
        {
            return;
        }

        await MoveAsync(unused, _libraryDir!, Strings.LibraryMovedIn);
    }

    private bool CanMoveToLibrary() => !IsWorking && !_profiles.IsGameRunning && _libraryDir is not null &&
                                       CurrentSelection().Any(m => m.Mod.Source == ModSource.Local && !m.InLibrary);

    /// <summary>Moves the selected local mods into the library; mods a profile uses stay where they are.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveToLibrary))]
    private async Task MoveToLibraryAsync()
    {
        if (!_allProfilesRead)
        {
            _dialogs.ShowError(Strings.LibraryProfilesUnreadable);
            return;
        }

        var chosen = CurrentSelection().Where(m => m.Mod.Source == ModSource.Local && !m.InLibrary).ToList();
        var used = chosen.Where(m => _used.Contains(m.PackageId)).ToList();
        if (used.Count > 0)
        {
            _dialogs.ShowInfo(string.Format(CultureInfo.CurrentCulture, Strings.LibraryStillUsed, string.Join(", ", used.Select(m => m.DisplayName))));
        }

        var mods = chosen.Except(used).Select(m => m.Mod).ToList();
        if (mods.Count > 0)
        {
            await MoveAsync(mods, _libraryDir!, Strings.LibraryMovedIn);
        }
    }

    private bool CanMoveBack() => !IsWorking && !_profiles.IsGameRunning && _modDir is not null && CurrentSelection().Any(m => m.InLibrary);

    [RelayCommand(CanExecute = nameof(CanMoveBack))]
    private async Task MoveBackAsync() =>
        await MoveAsync(CurrentSelection().Where(m => m.InLibrary).Select(m => m.Mod).ToList(), _modDir!, Strings.LibraryMovedBack);

    private bool CanMoveAllBack() => !IsWorking && !_profiles.IsGameRunning && _modDir is not null && _all.Any(m => m.InLibrary);

    /// <summary>Empties the library: every mod in it goes back to mod\ (where the game sees it again).</summary>
    [RelayCommand(CanExecute = nameof(CanMoveAllBack))]
    private async Task MoveAllBackAsync()
    {
        var all = _all.Where(m => m.InLibrary).Select(m => m.Mod).ToList();
        if (_dialogs.Confirm(Strings.LibraryMoveTitle, string.Format(CultureInfo.CurrentCulture, Strings.LibraryMoveAllBackQuestion, all.Count, _modDir)))
        {
            await MoveAsync(all, _modDir!, Strings.LibraryMovedBack);
        }
    }

    private bool CanOpenLibrary() => _libraryDir is not null && Directory.Exists(_libraryDir);

    [RelayCommand(CanExecute = nameof(CanOpenLibrary))]
    private void OpenLibrary() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_libraryDir}\"") { UseShellExecute = true })?.Dispose();

    private async Task MoveAsync(IReadOnlyList<ModInfo> mods, string target, string doneFormat)
    {
        IsWorking = true;
        try
        {
            var results = await Task.Run(() => _move(mods, target));
            foreach (var r in results)
            {
                Log.Information("Library: {Mod} {From} -> {To}: {Outcome} ({Message})", r.Mod.PackageId, r.Mod.Path, r.Target, r.Outcome, r.Message);
            }

            var problems = results.Where(r => r.Outcome != LibraryMoveOutcome.Moved).Select(r => $"• {r.Mod.DisplayName}: {r.Message}").ToList();
            if (problems.Count > 0)
            {
                _dialogs.ShowInfo(string.Join(Environment.NewLine, problems));
            }

            SelectedMods.Clear();
            await _refresh();
            Message = string.Format(CultureInfo.CurrentCulture, doneFormat, results.Count(r => r.Outcome == LibraryMoveOutcome.Moved));
        }
        catch (GameRunningException ex)
        {
            _dialogs.ShowError(ex.Message);
        }
        finally
        {
            IsWorking = false;
        }
    }

    // ---- install / uninstall ---------------------------------------------------------------------

    private bool CanInstall() => !IsWorking && !_profiles.IsGameRunning && _modDir is not null;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var files = _dialogs.PickOpenFiles(Strings.ModFileFilter);
        if (files.Count > 0)
        {
            await InstallFilesAsync(files);
        }
    }

    /// <summary>Installs dropped or picked files (only .scs/.zip; each is checked by its content first).</summary>
    public async Task InstallFilesAsync(IReadOnlyList<string> files)
    {
        if (!CanInstall())
        {
            return;
        }

        IsWorking = true;
        try
        {
            var results = _installer().Install(_modDir!, files, name =>
                _dialogs.Confirm(Strings.ReplaceTitle, string.Format(CultureInfo.CurrentCulture, Strings.ReplaceQuestion, name)), _libraryDir);
            foreach (var r in results)
            {
                Log.Information("Install {Source}: {Outcome} ({Message})", r.Source, r.Outcome, r.Message);
            }

            string summary = string.Format(CultureInfo.CurrentCulture, Strings.InstallSummary,
                results.Count(r => r.Outcome == InstallOutcome.Installed), results.Count(r => r.Outcome == InstallOutcome.Replaced),
                results.Count(r => r.Outcome == InstallOutcome.Skipped), results.Count(r => r.Outcome == InstallOutcome.Failed));
            var notes = results.Where(r => r.Outcome is not InstallOutcome.Installed || r.Message != "Installed.")
                               .Select(r => $"• {Path.GetFileName(r.Source)}: {r.Message}").ToList();
            if (notes.Count > 0)
            {
                _dialogs.ShowInfo(summary + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, notes));
            }

            await _refresh();
            Message = summary;
        }
        catch (Exception ex) when (ex is GameRunningException or IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(ex.Message);
        }
        finally
        {
            IsWorking = false;
        }
    }

    private bool CanUninstall() => !IsWorking && !_profiles.IsGameRunning && CurrentSelection().Count > 0;

    /// <summary>Sends the selected local mods to the Recycle Bin; optionally removes them from the profiles that use them.</summary>
    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        var selection = CurrentSelection();
        var workshop = selection.Where(m => m.Mod.Source == ModSource.Workshop).ToList();
        var local = selection.Where(m => m.Mod.Source == ModSource.Local).ToList();
        if (workshop.Count > 0)
        {
            _dialogs.ShowInfo(string.Format(CultureInfo.CurrentCulture, Strings.UninstallWorkshopNote,
                                            string.Join(", ", workshop.Select(m => m.DisplayName))));
        }

        if (local.Count == 0 ||
            !_dialogs.Confirm(Strings.UninstallTitle, string.Format(CultureInfo.CurrentCulture, Strings.UninstallQuestion, local.Count,
                                                                    string.Join(Environment.NewLine, local.Select(m => "• " + m.DisplayName)))))
        {
            return;
        }

        var ids = local.Select(m => m.PackageId).ToHashSet(StringComparer.Ordinal);
        var usedBy = _profiles.EditableProfiles.Where(p => p.LoadOrder!.TopFirst.Any(e => ids.Contains(e.PackageId))).ToList();
        bool removeFromProfiles = false;
        if (usedBy.Count > 0)
        {
            var answer = _dialogs.AskYesNoCancel(Strings.UninstallTitle, string.Format(CultureInfo.CurrentCulture,
                Strings.UninstallActiveQuestion, string.Join(", ", usedBy.Select(p => p.Name))));
            if (answer is null)
            {
                return;
            }

            removeFromProfiles = answer.Value;
        }

        IsWorking = true;
        try
        {
            if (removeFromProfiles)
            {
                foreach (var profile in usedBy)
                {
                    await _profiles.RemoveModsAsync(local, profile);
                }
            }

            var installer = _installer();
            foreach (var m in local)
            {
                installer.Uninstall(m.Mod);
                Log.Information("Uninstalled {Mod} ({Path}) to the Recycle Bin", m.PackageId, m.Mod.Path);
            }

            SelectedMods.Clear();
            await _refresh();
            Message = string.Format(CultureInfo.CurrentCulture, Strings.UninstalledStatus, local.Count);
        }
        catch (Exception ex) when (ex is GameRunningException or InvalidOperationException or IOException or UnauthorizedAccessException
                                       or OperationCanceledException)
        {
            _dialogs.ShowError(ex.Message);
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>The selected mods in list order, or the focused one when nothing is multi-selected.</summary>
    private List<ModItemViewModel> CurrentSelection()
    {
        var list = Items.Where(SelectedMods.Contains).ToList();
        if (list.Count == 0 && Selected is { } one)
        {
            list.Add(one);
        }

        return list;
    }

    private bool CanChange(ProfileItemViewModel? target) =>
        target is not null && !IsWorking && !_profiles.IsGameRunning && CurrentSelection().Count > 0;

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task AddToProfileAsync(ProfileItemViewModel? target) =>
        await Run(() => _profiles.AddModsAsync(CurrentSelection(), target!));

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task RemoveFromProfileAsync(ProfileItemViewModel? target) =>
        await Run(() => _profiles.RemoveModsAsync(CurrentSelection(), target!));

    private async Task Run(Func<Task<string?>> action)
    {
        IsWorking = true;
        try
        {
            if (await action() is { } message)
            {
                Message = message;
            }
        }
        finally
        {
            IsWorking = false;
            OnSelectionChanged();
        }
    }
}
