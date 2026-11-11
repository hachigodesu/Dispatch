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
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(MoveTopCommand),
                                nameof(MoveBottomCommand), nameof(RemoveCommand))]
    private LoadOrderItemViewModel? _selectedEntry;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedCommand))]
    private ModItemViewModel? _selectedAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreBackupCommand))]
    private BackupItemViewModel? _selectedBackup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand), nameof(DuplicateCommand), nameof(RenameCommand))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RestoreBackupCommand), nameof(DuplicateCommand),
                                nameof(RenameCommand), nameof(RestoreFolderCommand))]
    private bool _isGameRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreFolderCommand))]
    private BackupItemViewModel? _selectedFullBackup;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DuplicateCommand), nameof(RenameCommand), nameof(RestoreFolderCommand))]
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

    // ---- editing commands -----------------------------------------------------------------------

    private bool HasEntry => CanEdit && SelectedEntry is not null;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(SelectedEntry!, LoadOrder.IndexOf(SelectedEntry!) - 1);

    private bool CanMoveUp() => HasEntry && LoadOrder.IndexOf(SelectedEntry!) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(SelectedEntry!, LoadOrder.IndexOf(SelectedEntry!) + 1);

    private bool CanMoveDown() => HasEntry && LoadOrder.IndexOf(SelectedEntry!) < LoadOrder.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveTop() => Move(SelectedEntry!, 0);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveBottom() => Move(SelectedEntry!, LoadOrder.Count - 1);

    [RelayCommand(CanExecute = nameof(HasEntry))]
    private void Remove()
    {
        int index = LoadOrder.IndexOf(SelectedEntry!);
        LoadOrder.RemoveAt(index);
        SelectedEntry = LoadOrder.Count == 0 ? null : LoadOrder[Math.Min(index, LoadOrder.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddSelected() => Insert(SelectedAvailable!, 0);

    private bool CanAdd() => CanEdit && SelectedAvailable is not null;

    internal void Move(LoadOrderItemViewModel item, int newIndex)
    {
        int old = LoadOrder.IndexOf(item);
        newIndex = Math.Clamp(newIndex, 0, LoadOrder.Count - 1);
        if (old < 0 || old == newIndex)
        {
            return;
        }

        LoadOrder.Move(old, newIndex);
        SelectedEntry = item;
        NotifyMoveCommands();
    }

    internal void Insert(ModItemViewModel mod, int index)
    {
        if (LoadOrder.Any(l => l.PackageId == mod.PackageId))
        {
            return;
        }

        var item = new LoadOrderItemViewModel(new ActiveModEntry(mod.PackageId, mod.DisplayName), mod);
        LoadOrder.Insert(Math.Clamp(index, 0, LoadOrder.Count), item);
        SelectedEntry = item;
        NotifyMoveCommands();
    }

    private void NotifyMoveCommands()
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        MoveTopCommand.NotifyCanExecuteChanged();
        MoveBottomCommand.NotifyCanExecuteChanged();
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
    public override void DragOver(IDropInfo dropInfo)
    {
        if (!owner.CanEdit)
        {
            dropInfo.Effects = DragDropEffects.None;
            return;
        }

        if (dropInfo.Data is LoadOrderItemViewModel or ModItemViewModel)
        {
            dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
            dropInfo.Effects = dropInfo.Data is ModItemViewModel ? DragDropEffects.Copy : DragDropEffects.Move;
        }
    }

    public override void Drop(IDropInfo dropInfo)
    {
        if (!owner.CanEdit)
        {
            return;
        }

        int index = dropInfo.InsertIndex;
        switch (dropInfo.Data)
        {
            case LoadOrderItemViewModel item:
                int old = owner.LoadOrder.IndexOf(item);
                owner.Move(item, index > old ? index - 1 : index);
                break;
            case ModItemViewModel mod:
                owner.Insert(mod, index);
                break;
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
        _ => "Manual",
    };

    public string FilesLabel => Backup.IsFullProfile
        ? $"{Backup.Files.Count} files · {Backup.SizeBytes / 1048576.0:F1} MB"
        : string.Join(", ", Backup.Files);
}
