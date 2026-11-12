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

public sealed record LanguageOption(string Code, string Name);

/// <summary>Settings tab: what was detected, plus manual path overrides.</summary>
public sealed partial class SettingsViewModel(AppSettings settings, AppPaths paths, Func<Task> redetect) : ObservableObject
{
    public AppSettings Current => settings;

    /// <summary>The game whose paths are shown (follows the game switcher).</summary>
    private Dispatch.Core.Games.GameDefinition _game = Dispatch.Core.Games.GameDefinition.FromKey(settings.Game);

    [ObservableProperty]
    private string _installOverride = settings.PathsFor(Dispatch.Core.Games.GameDefinition.FromKey(settings.Game)).Install ?? "";

    [ObservableProperty]
    private string _userDataOverride = settings.PathsFor(Dispatch.Core.Games.GameDefinition.FromKey(settings.Game)).UserData ?? "";

    [ObservableProperty]
    private string _detectedHeader = "";

    [ObservableProperty]
    private string _overridesHeader = "";

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

    /// <summary>The UI languages Dispatch ships ("" = follow Windows), each named in its own language.</summary>
    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("", Strings.LanguageSystem), new("en", "English"), new("de", "Deutsch"), new("es", "Español"), new("fr", "Français"),
        new("pl", "Polski"), new("pt-BR", "Português (Brasil)"), new("ru", "Русский"),
    ];

    [ObservableProperty]
    private LanguageOption _selectedLanguage = Languages.FirstOrDefault(l => l.Code == (settings.Language ?? "")) ?? Languages[0];

    [ObservableProperty]
    private bool _restartNeeded;

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        settings.Language = value.Code.Length == 0 ? null : value.Code;
        settings.Save(paths.SettingsFile);
        RestartNeeded = true;
    }

    public string AppVersion => string.Format(CultureInfo.CurrentCulture, Strings.AboutVersion,
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");

    public void Load(GameEnvironment env)
    {
        _game = env.Game;
        DetectedHeader = string.Format(CultureInfo.CurrentCulture, Strings.SettingsDetected, env.Game.DisplayName);
        OverridesHeader = string.Format(CultureInfo.CurrentCulture, Strings.Overrides, env.Game.DisplayName);
        InstallOverride = settings.PathsFor(env.Game).Install ?? "";
        UserDataOverride = settings.PathsFor(env.Game).UserData ?? "";
        DetectedUserData = env.UserDataDir ?? "—";
        DetectedInstall = env.InstallDir ?? "—";
        DetectedVersion = env.Version is { } v ? string.Format(CultureInfo.CurrentCulture, Strings.VersionFrom, v, env.VersionSource) : "—";
        Libraries = env.SteamLibraries.Count == 0 ? "—" : string.Join(Environment.NewLine, env.SteamLibraries);
        Accounts = env.Accounts.Count == 0
            ? "—"
            : string.Join(Environment.NewLine, env.Accounts.Select(a => a.PersonaName + (a.IsActive ? " " + Strings.SignedIn : "")));
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
        settings.SetPathsFor(_game, InstallOverride, UserDataOverride);
        settings.Save(paths.SettingsFile);
        await redetect();
    }

    /// <summary>Applies the saved language before any window is created (the UI reads its strings once).</summary>
    public static void ApplyLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
            // Set on the strings themselves too: OnStartup is async, and .NET puts a thread's culture back when an
            // async method yields, so bindings evaluated later would otherwise fall back to Windows' language.
            Strings.Culture = culture;
        }
        catch (CultureNotFoundException)
        {
            Serilog.Log.Warning("Unknown language {Code} in settings; using Windows' language", code);
        }
    }

    private static string? PickFolder(string start)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(start) ? start : "" };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
