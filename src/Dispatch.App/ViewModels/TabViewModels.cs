using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Resources;
using Dispatch.App.Services;
using Dispatch.Core.Detection;
using Dispatch.Core.Mods;
using Microsoft.Win32;

namespace Dispatch.App.ViewModels;

/// <summary>Mods tab: every installed mod with a search box and a details panel.</summary>
public sealed partial class ModsViewModel : ObservableObject
{
    private List<ModItemViewModel> _all = [];

    public ObservableCollection<ModItemViewModel> Items { get; } = [];

    public IReadOnlyDictionary<string, ModItemViewModel> ById { get; private set; } = new Dictionary<string, ModItemViewModel>();

    [ObservableProperty]
    private ModItemViewModel? _selected;

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private string _summary = "";

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
        ApplyFilter();
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedChanged(ModItemViewModel? value) => value?.EnsureDescriptionLoaded();

    private void ApplyFilter()
    {
        string f = Filter.Trim();
        var keep = Selected;
        Items.Clear();
        foreach (var m in _all.Where(m => m.Matches(f)))
        {
            Items.Add(m);
        }

        Selected = keep is not null && Items.Contains(keep) ? keep : null;
    }
}

/// <summary>Settings tab: what was detected, plus manual path overrides.</summary>
public sealed partial class SettingsViewModel(AppSettings settings, AppPaths paths, Func<Task> redetect) : ObservableObject
{
    public AppSettings Current => settings;

    [ObservableProperty]
    private string _installOverride = settings.InstallDirOverride ?? "";

    [ObservableProperty]
    private string _userDataOverride = settings.UserDataDirOverride ?? "";

    [ObservableProperty]
    private string _detectedUserData = "";

    [ObservableProperty]
    private string _detectedInstall = "";

    [ObservableProperty]
    private string _detectedVersion = "";

    [ObservableProperty]
    private string _libraries = "";

    [ObservableProperty]
    private string _accounts = "";

    [ObservableProperty]
    private string _dlc = "";

    public string DataFolder => paths.Root;

    public string AppVersion => string.Format(CultureInfo.CurrentCulture, Strings.AboutVersion,
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");

    public void Load(GameEnvironment env)
    {
        DetectedUserData = env.UserDataDir ?? "—";
        DetectedInstall = env.InstallDir ?? "—";
        DetectedVersion = env.Version is { } v ? $"{v} (from {env.VersionSource})" : "—";
        Libraries = env.SteamLibraries.Count == 0 ? "—" : string.Join(Environment.NewLine, env.SteamLibraries);
        Accounts = env.Accounts.Count == 0
            ? "—"
            : string.Join(Environment.NewLine, env.Accounts.Select(a => a.PersonaName + (a.IsActive ? " (signed in)" : "")));
        Dlc = env.OwnedDlc.Count.ToString(CultureInfo.CurrentCulture);
    }

    [RelayCommand]
    private void BrowseInstall()
    {
        if (PickFolder(InstallOverride) is { } p)
        {
            InstallOverride = p;
        }
    }

    [RelayCommand]
    private void BrowseUserData()
    {
        if (PickFolder(UserDataOverride) is { } p)
        {
            UserDataOverride = p;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        settings.InstallDirOverride = string.IsNullOrWhiteSpace(InstallOverride) ? null : InstallOverride.Trim();
        settings.UserDataDirOverride = string.IsNullOrWhiteSpace(UserDataOverride) ? null : UserDataOverride.Trim();
        settings.Save(paths.SettingsFile);
        await redetect();
    }

    private static string? PickFolder(string start)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(start) ? start : "" };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
