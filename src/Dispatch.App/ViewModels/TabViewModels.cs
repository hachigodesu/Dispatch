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
