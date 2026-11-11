using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Resources;
using Dispatch.App.Services;
using Dispatch.Core.Mods;

namespace Dispatch.App.ViewModels;

/// <summary>
/// Mods tab: every installed mod with search, a type filter, multi-selection and a right-click menu to add the selected
/// mods to (or remove them from) a profile.
/// </summary>
public sealed partial class ModsViewModel : ObservableObject
{
    private readonly ProfilesViewModel _profiles;
    private List<ModItemViewModel> _all = [];

    public ModsViewModel(ProfilesViewModel profiles)
    {
        _profiles = profiles;
        SelectedMods.CollectionChanged += (_, _) => OnSelectionChanged();
    }

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
    private bool _isWorking;

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

        var keepIds = SelectedMods.Select(m => m.PackageId).ToHashSet(StringComparer.Ordinal);
        var byId = new Dictionary<string, ModItemViewModel>(StringComparer.Ordinal);
        _all = [];
        foreach (var mod in snapshot.Scan.Mods)
        {
            var vm = new ModItemViewModel(mod, snapshot.Compatibility.GetValueOrDefault(mod.PackageId)
                                               ?? CompatibilityChecker.Evaluate(mod, snapshot.Environment.Version, snapshot.Environment.OwnedDlc, snapshot.GameLog),
                                          snapshot.NamesFromProfiles.GetValueOrDefault(mod.PackageId),
                                          usedIn.GetValueOrDefault(mod.PackageId) ?? []);
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
        foreach (var m in _all.Where(m => m.Matches(f) && MatchesType(m, type)))
        {
            Items.Add(m);
        }

        Selected = keep is not null && Items.Contains(keep) ? keep : null;
    }

    private static bool MatchesType(ModItemViewModel m, string type) =>
        type == Strings.AllTypes || (type == Strings.NoType ? m.TypeLabels.Count == 0 : m.TypeLabels.Contains(type));

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
