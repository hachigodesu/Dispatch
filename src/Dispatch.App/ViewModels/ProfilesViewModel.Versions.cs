using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Dispatch.App.Resources;
using Dispatch.App.Services;
using Dispatch.Core.Games;
using Dispatch.Core.Mods;
using Dispatch.Core.Profiles;
using Serilog;

namespace Dispatch.App.ViewModels;

/// <summary>An entry in a profile's "Game version" list.</summary>
/// <param name="Key">"" = automatic, "steam", or a copy's folder (as stored in settings).</param>
public sealed record LaunchChoice(string Key, string Label, GameInstallation? Installation);

/// <summary>
/// Which game version a profile launches with: automatic (the version its active mods fit best) or pinned to the
/// Steam version or a kept copy. Stored in Dispatch's settings, never in the profile.
/// </summary>
public sealed partial class ProfilesViewModel
{
    private VersionsViewModel? _versions;
    private AppSettings? _versionSettings;
    private string? _settingsFile;
    private bool _fillingChoices;

    public void ConfigureVersions(VersionsViewModel versions, AppSettings settings, string settingsFile)
    {
        _versions = versions;
        _versionSettings = settings;
        _settingsFile = settingsFile;
        versions.Changed += RefreshLaunchChoices;
    }

    public ObservableCollection<LaunchChoice> LaunchChoices { get; } = [];

    [ObservableProperty]
    private LaunchChoice? _selectedLaunchChoice;

    [ObservableProperty]
    private string _launchHint = "";

    [ObservableProperty]
    private bool _launchHintIsWarning;

    [ObservableProperty]
    private bool _hasVersionChoice;

    /// <summary>What Launch starts for the selected profile; null = the Steam version (no choice to make).</summary>
    [ObservableProperty]
    private GameInstallation? _launchInstallation;

    private string? VersionKey(ProfileItemViewModel? p) =>
        p is null || _versions is null ? null : AppSettings.ProfileVersionKey(_versions.Game, p.IsCloud, p.Profile.FolderName);

    /// <summary>Rebuilds the choices and the automatic pick for the selected profile (cheap: no file access).</summary>
    private void RefreshLaunchChoices()
    {
        if (_versions is null || _versionSettings is null)
        {
            return;
        }

        var available = _versions.Available;
        var steam = _versions.Steam;
        var mods = LoadOrder.Select(e => e.Mod?.Mod).OfType<ModInfo>().ToList();
        var auto = VersionPicker.Pick(available, mods);

        _fillingChoices = true;
        try
        {
            LaunchChoices.Clear();
            HasVersionChoice = available.Count > 1 && _shown is not null;
            if (!HasVersionChoice)
            {
                SelectedLaunchChoice = null;
                LaunchInstallation = null;
                LaunchHint = "";
                return;
            }

            LaunchChoices.Add(new LaunchChoice("", string.Format(CultureInfo.CurrentCulture, Strings.LaunchAuto,
                                                                 VersionsViewModel.Describe(auto!.Installation)), null));
            foreach (var i in available)
            {
                LaunchChoices.Add(new LaunchChoice(i.IsSteam ? AppSettings.SteamVersion : i.Folder, VersionsViewModel.Describe(i), i));
            }

            string? saved = VersionKey(_shown) is { } key && _versionSettings.ProfileVersions.TryGetValue(key, out var v) ? v : null;
            // A pin to a copy that's gone (removed or folder missing) falls back to automatic, but stays saved.
            SelectedLaunchChoice = LaunchChoices.FirstOrDefault(c => saved is not null && string.Equals(c.Key, saved, StringComparison.OrdinalIgnoreCase))
                                   ?? LaunchChoices[0];
        }
        finally
        {
            _fillingChoices = false;
        }

        var chosen = SelectedLaunchChoice!.Installation ?? auto.Installation;
        LaunchInstallation = chosen;
        string hint = SelectedLaunchChoice.Installation is not null
            ? ""
            : auto.Checked == 0
                ? Strings.LaunchAutoNoInfo
                : auto.Mismatches == 0
                    ? string.Format(CultureInfo.CurrentCulture, Strings.LaunchAutoAllMatch, chosen.Version)
                    : string.Format(CultureInfo.CurrentCulture, Strings.LaunchAutoSome, chosen.Version, auto.Mismatches);
        bool older = steam?.Version is { } sv && chosen.Version is { } cv && VersionComparer.Instance.Compare(cv, sv) < 0;
        if (older)
        {
            hint = (hint.Length > 0 ? hint + " " : "") + Strings.LaunchOlderWarning;
        }

        LaunchHint = hint;
        LaunchHintIsWarning = older;
    }

    /// <summary>
    /// What any profile launches with (its pinned version, or the automatic pick), without selecting it. Pinned is
    /// true when the profile is set to a specific version.
    /// </summary>
    internal (GameInstallation? Installation, bool Pinned, VersionChoice? Auto) LaunchFor(ProfileItemViewModel profile)
    {
        if (_versions is null || _versionSettings is null)
        {
            return (null, false, null);
        }

        var available = _versions.Available;
        var mods = (profile.LoadOrder?.TopFirst ?? []).Select(e => _mods.GetValueOrDefault(e.PackageId)?.Mod).OfType<ModInfo>().ToList();
        var auto = VersionPicker.Pick(available, mods);
        string? saved = VersionKey(profile) is { } key && _versionSettings.ProfileVersions.TryGetValue(key, out var v) ? v : null;
        var pinned = saved is null
            ? null
            : available.FirstOrDefault(i => string.Equals(i.IsSteam ? AppSettings.SteamVersion : i.Folder, saved, StringComparison.OrdinalIgnoreCase));
        return (pinned ?? auto?.Installation, pinned is not null, auto);
    }

    /// <summary>Sets a profile back to the automatic version (the health report's fix for a pinned version that doesn't fit).</summary>
    internal void UseAutomaticVersion(ProfileItemViewModel profile)
    {
        if (VersionKey(profile) is { } key && _versionSettings is not null && _versionSettings.ProfileVersions.Remove(key))
        {
            Log.Information("Profile {Folder} launches with the automatic version again", profile.Profile.FolderName);
            SaveVersionSettings();
            RefreshLaunchChoices();
        }
    }

    partial void OnSelectedLaunchChoiceChanged(LaunchChoice? value)
    {
        if (_fillingChoices || value is null || VersionKey(_shown) is not { } key || _versionSettings is null)
        {
            return;
        }

        if (value.Key.Length == 0)
        {
            _versionSettings.ProfileVersions.Remove(key);
        }
        else
        {
            _versionSettings.ProfileVersions[key] = value.Key;
        }

        Log.Information("Profile {Folder} launches with {Choice}", _shown!.Profile.FolderName, value.Key.Length == 0 ? "automatic" : value.Key);
        SaveVersionSettings();
        RefreshLaunchChoices();
    }

    /// <summary>A renamed profile keeps its version; a duplicate starts with the same one.</summary>
    private void CarryVersionChoice(ProfileInfo from, ProfileInfo to, bool move)
    {
        if (_versions is null || _versionSettings is null)
        {
            return;
        }

        string oldKey = AppSettings.ProfileVersionKey(_versions.Game, from.Kind == ProfileKind.SteamCloud, from.FolderName);
        if (_versionSettings.ProfileVersions.TryGetValue(oldKey, out var value))
        {
            _versionSettings.ProfileVersions[AppSettings.ProfileVersionKey(_versions.Game, to.Kind == ProfileKind.SteamCloud, to.FolderName)] = value;
            if (move)
            {
                _versionSettings.ProfileVersions.Remove(oldKey);
            }

            SaveVersionSettings();
        }
    }

    private void SaveVersionSettings()
    {
        try
        {
            _versionSettings?.Save(_settingsFile!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Couldn't save the profile's game version");
        }
    }
}
