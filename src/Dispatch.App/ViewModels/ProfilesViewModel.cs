using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Resources;
using Dispatch.App.Services;
using Dispatch.Core.Profiles;
using Dispatch.Core.Safety;
using GongSolutions.Wpf.DragDrop;
using Serilog;

namespace Dispatch.App.ViewModels;

/// <summary>
/// Profiles tab: the profile list, the selected profile's editable load order, a panel of mods to add, and the
/// profile's backups. Saving goes through Core's SafeWriter (backup first, game must be closed).
/// </summary>
public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly IDialogs _dialogs;
    private readonly Func<SafeWriter> _writer;
    private readonly Func<BackupService> _backups;
    private readonly Func<Task> _refresh;
    private IReadOnlyDictionary<string, ModItemViewModel> _mods = new Dictionary<string, ModItemViewModel>();
    private bool _suppressDirty;

    public ProfilesViewModel(IDialogs dialogs, Func<SafeWriter> writer, Func<BackupService> backups, Func<Task> refresh)
    {
        _dialogs = dialogs;
        _writer = writer;
        _backups = backups;
        _refresh = refresh;
        LoadOrder.CollectionChanged += OnLoadOrderChanged;
        SelectedEntries.CollectionChanged += (_, _) => NotifyMoveCommands();
        SelectedAvailables.CollectionChanged += (_, _) => AddSelectedCommand.NotifyCanExecuteChanged();
        DropHandler = new LoadOrderDropHandler(this);
    }

    public ObservableCollection<ProfileItemViewModel> Items { get; } = [];

    public ObservableCollection<LoadOrderItemViewModel> LoadOrder { get; } = [];

    public ObservableCollection<ModItemViewModel> Available { get; } = [];

    public ObservableCollection<BackupItemViewModel> Backups { get; } = [];

    /// <summary>Whole-profile backups (originals of renamed profiles), shown in Settings.</summary>
    public ObservableCollection<BackupItemViewModel> FullBackups { get; } = [];

    private string? _userDataDir;
    private bool _cloudAvailable;

    /// <summary>Handles drops on the load order list (reorder, or add from the "Add mods" panel).</summary>
    public IDropTarget DropHandler { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand), nameof(RestoreBackupCommand))]
    private ProfileItemViewModel? _selected;

    [ObservableProperty]
    private LoadOrderItemViewModel? _selectedEntry;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedCommand))]
    private ModItemViewModel? _selectedAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreBackupCommand))]
    private BackupItemViewModel? _selectedBackup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand), nameof(DuplicateCommand), nameof(RenameCommand),
                                nameof(CopyLoadOrderCommand))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RestoreBackupCommand), nameof(DuplicateCommand),
                                nameof(RenameCommand), nameof(RestoreFolderCommand), nameof(DeleteProfileCommand),
                                nameof(CopyLoadOrderCommand))]
    private bool _isGameRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreFolderCommand), nameof(RecycleFullBackupCommand))]
    private BackupItemViewModel? _selectedFullBackup;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DuplicateCommand), nameof(RenameCommand), nameof(RestoreFolderCommand),
                                nameof(DeleteProfileCommand), nameof(RecycleFullBackupCommand), nameof(CopyLoadOrderCommand))]
    private bool _isWorking;

    [ObservableProperty]
    private string _availableFilter = "";

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _message = "";

    /// <summary>Local profiles that loaded fine can be edited; Steam Cloud profiles are read-only (PLAN.md §5.10).</summary>
    public bool CanEdit => Selected is { IsCloud: false, LoadOrder: not null };

    public bool CanSave => CanEdit && IsDirty && !IsGameRunning;

    public ModItemViewModel? SelectedEntryMod => SelectedEntry?.Mod;

    public void Load(Services.GameSnapshot snapshot, IReadOnlyDictionary<string, ModItemViewModel> mods)
    {
        _mods = mods;
        _userDataDir = snapshot.Environment.UserDataDir;
        _cloudAvailable = snapshot.Environment.Accounts.Count > 0;
        FullBackups.Clear();
        foreach (var b in _backups().ListAll().Where(b => b.IsFullProfile))
        {
            FullBackups.Add(new BackupItemViewModel(b));
        }

        string? keep = _keepSelection ?? Selected?.Profile.MainDir;
        _keepSelection = null;
        Items.Clear();
        foreach (var p in snapshot.Profiles)
        {
            Items.Add(new ProfileItemViewModel(p.Profile, p.LoadOrder, p.Error));
        }

        Summary = string.Format(CultureInfo.CurrentCulture, Strings.ProfilesSummary, Items.Count);
        _selectingProgrammatically = true;
        Selected = Items.FirstOrDefault(i => i.Profile.MainDir == keep) ?? Items.FirstOrDefault();
        _selectingProgrammatically = false;
        ShowProfile(Selected);
    }

    /// <summary>True while the window may close or the selection may change without losing edits.</summary>
    public bool ConfirmLeave()
    {
        if (!IsDirty || Selected is null)
        {
            return true;
        }

        switch (_dialogs.AskSaveChanges(Selected.Name))
        {
            case true:
                return SaveCore();
            case false:
                IsDirty = false;
                return true;
            default:
                return false;
        }
    }

    private bool _selectingProgrammatically;
    private ProfileItemViewModel? _shown;

    private string? _keepSelection;

    partial void OnSelectedChanged(ProfileItemViewModel? oldValue, ProfileItemViewModel? newValue)
    {
        DuplicateCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
        CopyLoadOrderCommand.NotifyCanExecuteChanged();
        if (_selectingProgrammatically || newValue == _shown)
        {
            return;
        }

        if (!ConfirmLeave())
        {
            // Put the selection back after the ListBox finishes its own update.
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _selectingProgrammatically = true;
                Selected = _shown;
                _selectingProgrammatically = false;
            });
            return;
        }

        ShowProfile(newValue);
    }

    private void ShowProfile(ProfileItemViewModel? profile)
    {
        _shown = profile;
        _suppressDirty = true;
        LoadOrder.Clear();
        foreach (var entry in profile?.LoadOrder?.TopFirst ?? [])
        {
            _mods.TryGetValue(entry.PackageId, out var mod);
            LoadOrder.Add(new LoadOrderItemViewModel(entry, mod));
        }

        _suppressDirty = false;
        Renumber();
        IsDirty = false;
        SelectedEntry = null;
        Message = "";
        RefreshAvailable();
        RefreshBackups();
    }

    partial void OnSelectedEntryChanged(LoadOrderItemViewModel? value)
    {
        value?.Mod?.EnsureDescriptionLoaded();
        OnPropertyChanged(nameof(SelectedEntryMod));
        NotifyMoveCommands();
    }

    partial void OnSelectedAvailableChanged(ModItemViewModel? value) => value?.EnsureDescriptionLoaded();

    partial void OnAvailableFilterChanged(string value) => RefreshAvailable();

    private void OnLoadOrderChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Renumber();
        if (!_suppressDirty)
        {
            IsDirty = true;
            RefreshAvailable();
        }
    }

    private void Renumber()
    {
        for (int i = 0; i < LoadOrder.Count; i++)
        {
            LoadOrder[i].Position = i + 1;
        }
    }

    private void RefreshAvailable()
    {
        var active = LoadOrder.Select(l => l.PackageId).ToHashSet(StringComparer.Ordinal);
        string f = AvailableFilter.Trim();
        Available.Clear();
        foreach (var m in _mods.Values.Where(m => !active.Contains(m.PackageId) && m.Matches(f))
                                      .OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            Available.Add(m);
        }
    }

    private void RefreshBackups()
    {
        Backups.Clear();
        if (Selected is null)
        {
            return;
        }

        foreach (var b in _backups().List(Selected.Profile.Kind, Selected.Profile.FolderName))
        {
            Backups.Add(new BackupItemViewModel(b));
        }
    }

    // ---- editing commands (all work on the whole selection) ---------------------------------------

    /// <summary>Selected rows of the load order (multi-select; kept in sync by Behaviors.MultiSelect).</summary>
    public ObservableCollection<LoadOrderItemViewModel> SelectedEntries { get; } = [];

    /// <summary>Selected mods in the "Add mods" panel.</summary>
    public ObservableCollection<ModItemViewModel> SelectedAvailables { get; } = [];

    /// <summary>The selection, or the focused row when nothing is multi-selected.</summary>
    private HashSet<LoadOrderItemViewModel> Selection()
    {
        var set = SelectedEntries.Where(LoadOrder.Contains).ToHashSet();
        if (set.Count == 0 && SelectedEntry is { } single)
        {
            set.Add(single);
        }

        return set;
    }

    public int SelectionCount => Selection().Count;

    private bool HasSelection() => CanEdit && Selection().Count > 0;

    private bool CanMoveUp()
    {
        var sel = Selection();
        return CanEdit && sel.Count > 0 && LoadOrder.Select((item, i) => (item, i)).Any(x => sel.Contains(x.item) && x.i > 0 && !sel.Contains(LoadOrder[x.i - 1]));
    }

    private bool CanMoveDown()
    {
        var sel = Selection();
        return CanEdit && sel.Count > 0 &&
               LoadOrder.Select((item, i) => (item, i)).Any(x => sel.Contains(x.item) && x.i < LoadOrder.Count - 1 && !sel.Contains(LoadOrder[x.i + 1]));
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Reorder(LoadOrderEdits.MoveUp(LoadOrder, Selection()));

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Reorder(LoadOrderEdits.MoveDown(LoadOrder, Selection()));

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveTop() => Reorder(LoadOrderEdits.MoveToTop(LoadOrder, Selection()));

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveBottom() => Reorder(LoadOrderEdits.MoveToBottom(LoadOrder, Selection()));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        var sel = Selection();
        int first = LoadOrder.Select((item, i) => (item, i)).First(x => sel.Contains(x.item)).i;
        foreach (var item in sel)
        {
            LoadOrder.Remove(item);
        }

        SelectedEntries.Clear();
        SelectedEntry = LoadOrder.Count == 0 ? null : LoadOrder[Math.Min(first, LoadOrder.Count - 1)];
        NotifyMoveCommands();
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddSelected() => InsertMany(SelectedMods(), 0);

    private List<ModItemViewModel> SelectedMods()
    {
        var mods = Available.Where(SelectedAvailables.Contains).ToList(); // in list order
        if (mods.Count == 0 && SelectedAvailable is { } one)
        {
            mods.Add(one);
        }

        return mods;
    }

    private bool CanAdd() => CanEdit && (SelectedAvailables.Count > 0 || SelectedAvailable is not null);

    /// <summary>Applies a new order with ObservableCollection.Move, so selected rows stay selected.</summary>
    private void Reorder(List<LoadOrderItemViewModel> target)
    {
        for (int i = 0; i < target.Count; i++)
        {
            int current = LoadOrder.IndexOf(target[i]);
            if (current != i)
            {
                LoadOrder.Move(current, i);
            }
        }

        NotifyMoveCommands();
    }

    internal void MoveBlock(IReadOnlyCollection<LoadOrderItemViewModel> items, int insertIndex)
    {
        Reorder(LoadOrderEdits.MoveTo(LoadOrder, items.ToHashSet(), insertIndex));
        SelectedEntries.Clear();
        foreach (var item in items)
        {
            SelectedEntries.Add(item);
        }
    }

    internal void InsertMany(IReadOnlyList<ModItemViewModel> mods, int index)
    {
        var added = new List<LoadOrderItemViewModel>();
        int at = Math.Clamp(index, 0, LoadOrder.Count);
        foreach (var mod in mods)
        {
            if (LoadOrder.Any(l => l.PackageId == mod.PackageId))
            {
                continue;
            }

            var item = new LoadOrderItemViewModel(new ActiveModEntry(mod.PackageId, mod.DisplayName), mod);
            LoadOrder.Insert(at++, item);
            added.Add(item);
        }

        SelectedEntries.Clear();
        foreach (var item in added)
        {
            SelectedEntries.Add(item);
        }

        SelectedEntry = added.FirstOrDefault() ?? SelectedEntry;
        NotifyMoveCommands();
    }

    private void NotifyMoveCommands()
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        MoveTopCommand.NotifyCanExecuteChanged();
        MoveBottomCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        AddSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectionCount));
    }

    // ---- save / discard / restore ---------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (SaveCore())
        {
            string message = Message;
            await _refresh(); // re-reads everything, which also clears the message…
            Message = message; // …so show it again
        }
    }

    private bool SaveCore()
    {
        var profile = Selected;
        if (profile?.LoadOrder is null)
        {
            return false;
        }

        try
        {
            var backup = LoadOrderService.Save(_writer(), profile.Profile, profile.LoadOrder,
                                               LoadOrder.Select(l => l.Entry).ToList());
            Log.Information("Saved load order of {Folder} ({Count} mods), backup {Backup}", profile.Profile.FolderName, LoadOrder.Count, backup.Id);
            IsDirty = false;
            Message = string.Format(CultureInfo.CurrentCulture, Strings.SavedStatus, profile.Name, backup.Id);
            return true;
        }
        catch (Exception ex) when (ex is GameRunningException or ConcurrentChangeException or InvalidOperationException
                                       or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Saving {Folder} failed", profile.Profile.FolderName);
            _dialogs.ShowError(ex.Message);
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Discard() => ShowProfile(Selected);

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreBackupAsync()
    {
        var backup = SelectedBackup!.Backup;
        string when = SelectedBackup.When;
        if (!_dialogs.Confirm(Strings.ConfirmRestoreTitle, string.Format(CultureInfo.CurrentCulture, Strings.ConfirmRestore, when)))
        {
            return;
        }

        try
        {
            _writer().Restore(backup, Selected!.Profile);
            Log.Information("Restored backup {Backup} into {Folder}", backup.Id, Selected.Profile.FolderName);
            IsDirty = false;
            await _refresh();
            Message = string.Format(CultureInfo.CurrentCulture, Strings.RestoredStatus, when);
        }
        catch (Exception ex) when (ex is GameRunningException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Restore of {Backup} failed", backup.Id);
            _dialogs.ShowError(ex.Message);
        }
    }

    private bool CanRestore() => CanEdit && SelectedBackup is { Backup.IsFullProfile: false } && !IsGameRunning;

    [RelayCommand]
    private void OpenBackupFolder()
    {
        if (SelectedBackup is { } b)
        {
            MainViewModel.OpenFolder(b.Backup.Path);
        }
        else if (Selected is { } p)
        {
            MainViewModel.OpenFolder(Path.Combine(_backups().Root, p.Profile.Kind.ToString(), p.Profile.FolderName));
        }
    }
}

public sealed partial class ProfilesViewModel
{
    // ---- duplicate / rename (M3) ----------------------------------------------------------------

    private bool CanDuplicate() => Selected is not null && !IsGameRunning && !IsWorking && _userDataDir is not null && !IsDirty;

    private bool CanRename() => CanEdit && !IsGameRunning && !IsWorking && !IsDirty;

    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private async Task DuplicateAsync()
    {
        var source = Selected!.Profile;
        var dialog = new ProfileDialogViewModel(ProfileDialogMode.Duplicate, source, Items.Select(i => i.Name).ToList(),
                                                Items.Select(i => i.Profile.FolderName).ToList(), ProfilesRoot, _cloudAvailable);
        if (!_dialogs.ShowProfileDialog(dialog))
        {
            return;
        }

        var options = new DuplicateOptions(dialog.NewName, dialog.IncludeSaves, dialog.KeepWorldOfTrucksLink);
        var names = Items.Select(i => i.Name).ToList();
        var folders = Items.Select(i => i.Profile.FolderName).ToList();
        bool toCloud = dialog.DestinationCloud;
        await RunProfileOperation(() =>
        {
            var copy = ProfileCloner.Duplicate(_writer(), source, ProfilesRoot, options, names, folders);
            Log.Information("Duplicated {Source} to {Target} (saves: {Saves}, WoT link: {Wot})", source.FolderName, copy.FolderName,
                            options.IncludeSaves, options.KeepWorldOfTrucksLink);
            return (copy.MainDir, string.Format(CultureInfo.CurrentCulture,
                toCloud ? Strings.DuplicatedCloudStatus : Strings.DuplicatedStatus, copy.Name), toCloud);
        });
    }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameAsync()
    {
        var source = Selected!.Profile;
        var names = Items.Select(i => i.Name).ToList();
        var folders = Items.Select(i => i.Profile.FolderName).ToList();
        var dialog = new ProfileDialogViewModel(ProfileDialogMode.Rename, source, names, folders, ProfilesRoot, cloudAvailable: false);
        if (!_dialogs.ShowProfileDialog(dialog))
        {
            return;
        }

        string newName = dialog.NewName;
        await RunProfileOperation(() =>
        {
            var (renamed, original) = ProfileCloner.Rename(_writer(), _backups(), source, newName, names, folders);
            Log.Information("Renamed {Source} to {Target}; original kept in backup {Backup}", source.FolderName, renamed.FolderName, original.Id);
            return (renamed.MainDir, string.Format(CultureInfo.CurrentCulture, Strings.RenamedStatus, renamed.Name), false);
        });
    }

    [RelayCommand(CanExecute = nameof(CanRestoreFolder))]
    private async Task RestoreFolderAsync()
    {
        var item = SelectedFullBackup!;
        if (!_dialogs.Confirm(Strings.RestoreFolder,
                              string.Format(CultureInfo.CurrentCulture, Strings.ConfirmRestoreFolder, item.Backup.ProfileName, item.When)))
        {
            return;
        }

        await RunProfileOperation(() =>
        {
            string dir = _writer().RestoreFolder(item.Backup);
            Log.Information("Restored whole-profile backup {Backup} to {Dir}", item.Backup.Id, dir);
            return (dir, string.Format(CultureInfo.CurrentCulture, Strings.RestoredFolderStatus, item.Backup.ProfileName), false);
        });
    }

    private bool CanRestoreFolder() => SelectedFullBackup is not null && !IsGameRunning && !IsWorking;

    private string ProfilesRoot => Path.Combine(_userDataDir ?? "", "profiles");

    /// <summary>Runs a (possibly slow: saves can be large) folder operation off the UI thread, then refreshes.</summary>
    private async Task RunProfileOperation(Func<(string SelectDir, string Message, bool Info)> operation)
    {
        IsWorking = true;
        Message = Strings.Working;
        try
        {
            var (dir, message, info) = await Task.Run(operation);
            _keepSelection = dir;
            await _refresh();
            Message = message;
            if (info)
            {
                _dialogs.ShowInfo(message);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException
                                       or IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Profile operation failed");
            Message = "";
            _dialogs.ShowError(ex.Message);
        }
        finally
        {
            IsWorking = false;
        }
    }
}

/// <summary>Drag & drop on the load order: reorder rows, or drop a mod from "Add mods" at a position.</summary>
internal sealed class LoadOrderDropHandler(ProfilesViewModel owner) : DefaultDropHandler
{
    /// <summary>gong passes one item, or a list when several selected rows are dragged.</summary>
    private static List<object> Items(object data) =>
        data is System.Collections.IEnumerable list and not string ? list.Cast<object>().ToList() : [data];

    public override void DragOver(IDropInfo dropInfo)
    {
        var items = Items(dropInfo.Data);
        if (!owner.CanEdit || items.Count == 0 ||
            !(items.All(i => i is LoadOrderItemViewModel) || items.All(i => i is ModItemViewModel)))
        {
            dropInfo.Effects = DragDropEffects.None;
            return;
        }

        dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
        dropInfo.Effects = items[0] is ModItemViewModel ? DragDropEffects.Copy : DragDropEffects.Move;
    }

    public override void Drop(IDropInfo dropInfo)
    {
        var items = Items(dropInfo.Data);
        if (!owner.CanEdit || items.Count == 0)
        {
            return;
        }

        if (items[0] is LoadOrderItemViewModel)
        {
            owner.MoveBlock(items.Cast<LoadOrderItemViewModel>().ToList(), dropInfo.InsertIndex);
        }
        else if (items[0] is ModItemViewModel)
        {
            owner.InsertMany(items.Cast<ModItemViewModel>().ToList(), dropInfo.InsertIndex);
        }
    }
}

public sealed class BackupItemViewModel(Backup backup)
{
    public Backup Backup { get; } = backup;

    public string When => Backup.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string ReasonLabel => Backup.Reason switch
    {
        BackupReason.LoadOrderSave => "Before saving the load order",
        BackupReason.PreRestore => "Before restoring a backup",
        BackupReason.Rename => "Whole profile, before renaming",
        BackupReason.Duplicate => "Before duplicating",
        BackupReason.PresetApply => "Before applying a preset",
        BackupReason.Delete => "Whole profile, deleted",
        _ => "Manual",
    };

    public string FilesLabel => Backup.IsFullProfile
        ? $"{Backup.Files.Count} files · {Backup.SizeBytes / 1048576.0:F1} MB"
        : string.Join(", ", Backup.Files);
}
