using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Resources;
using Dispatch.App.Services;
using Dispatch.Core.Games;
using Dispatch.Core.Platform;
using Dispatch.Core.Safety;
using Serilog;

namespace Dispatch.App.ViewModels;

/// <summary>
/// The window's view model: banners, status bar and refresh. The three tabs have their own view models,
/// which this one fills after every refresh.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IPlatform _platform;
    private readonly AppPaths _paths;
    private readonly DispatcherTimer _gameWatch;
    private readonly DispatcherTimer _copyWatch;
    private readonly IDialogs _dialogs;
    private readonly AppSettings _settings;

    /// <summary>The game being shown (ETS2 or ATS). Everything that writes uses this game's process name and folders.</summary>
    private GameDefinition _game;

    public MainViewModel(IPlatform platform, AppPaths paths, AppSettings settings, IDialogs dialogs)
    {
        _platform = platform;
        _paths = paths;
        _settings = settings;
        _game = GameDefinition.FromKey(settings.Game);
        Settings = new SettingsViewModel(settings, paths, () => RefreshCommand.ExecuteAsync(null));
        BackupService Backups() => new(paths.ForGame(_game).Backups,
                                       new BackupRetention(settings.BackupRetentionSmall, settings.BackupRetentionFull));
        Profiles = new ProfilesViewModel(dialogs, () => new SafeWriter(platform, Backups(), _game.ProcessName), Backups,
                                         () => RefreshCommand.ExecuteAsync(null));
        Mods = new ModsViewModel(Profiles, dialogs, () => new Dispatch.Core.Mods.ModInstaller(platform, _game.ProcessName, RecycleBin.Send),
                                 Dispatch.Core.Mods.ModNotesStore.Load(paths.ForGame(_game).NotesFile), () => RefreshCommand.ExecuteAsync(null),
                                 (mods, target) => Dispatch.Core.Mods.ModLibrary.Move(platform, _game.ProcessName, mods, target));
        _dialogs = dialogs;
        Updates = new UpdateViewModel(settings, paths, dialogs, () => Profiles.ConfirmLeave(),
                                      () => System.Windows.Application.Current?.Shutdown());
        Versions = new VersionsViewModel(platform, settings, paths, dialogs);
        Health = new HealthViewModel(Profiles, (profile, sidePanel) =>
        {
            SelectedTab = 0;
            Profiles.Selected = profile; // asks about unsaved changes first, like a click
            Profiles.SidePanelIndex = sidePanel;
        });
        Profiles.ConfigureVersions(Versions, settings, paths.SettingsFile);
        Profiles.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProfilesViewModel.LaunchInstallation) or nameof(ProfilesViewModel.HasVersionChoice))
            {
                OnPropertyChanged(nameof(LaunchLabel));
            }
        };
        Versions.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VersionsViewModel.IsPreparing))
            {
                LaunchGameCommand.NotifyCanExecuteChanged();
            }
        };
        Games.Add(_game);
        _selectedGame = _game;

        // Poll for the game process every 2 seconds (cheap) so the banner is always current.
        _gameWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _gameWatch.Tick += (_, _) => IsGameRunning = _platform.IsProcessRunning(_game.ProcessName);
        _gameWatch.Start();

        // Steam may update the game while Dispatch is open: look at the kept copies now and then (metadata only).
        _copyWatch = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _copyWatch.Tick += (_, _) =>
        {
            if (!IsBusy && !IsGameRunning)
            {
                Versions.CheckCopies();
            }
        };
        _copyWatch.Start();
        IsGameRunning = _platform.IsProcessRunning(_game.ProcessName);
        Profiles.IsGameRunning = IsGameRunning;
    }

    public GameDefinition Game => _game;

    /// <summary>Installed games to switch between; the switcher is hidden while only one is found.</summary>
    public ObservableCollection<GameDefinition> Games { get; } = [];

    public bool CanSwitchGame => Games.Count > 1;

    [ObservableProperty]
    private GameDefinition _selectedGame;

    private bool _switchingBack;

    partial void OnSelectedGameChanged(GameDefinition? oldValue, GameDefinition newValue)
    {
        if (_switchingBack || newValue is null || newValue == _game)
        {
            return;
        }

        if (IsBusy || !Profiles.ConfirmLeave())
        {
            // Put the old game back after the ComboBox finishes its own update.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _switchingBack = true;
                SelectedGame = _game;
                _switchingBack = false;
            });
            return;
        }

        _ = SwitchGameAsync(newValue);
    }

    /// <summary>Shows another game: its own profiles, mods, backups, notes and presets. Remembered for next time.</summary>
    /// <param name="remember">False for the self-test, so the user's saved choice isn't touched.</param>
    public async Task SwitchGameAsync(GameDefinition game, bool remember = true)
    {
        Log.Information("Switching to {Game}", game.ShortName);
        _game = game;
        _settings.Game = game.Key;
        try
        {
            if (remember)
            {
                _settings.Save(_paths.SettingsFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Couldn't save the chosen game");
        }

        Mods.UseNotes(Dispatch.Core.Mods.ModNotesStore.Load(_paths.ForGame(game).NotesFile));
        IsGameRunning = _platform.IsProcessRunning(game.ProcessName);
        OnPropertyChanged(nameof(Game));
        OnPropertyChanged(nameof(GameRunningText));
        _switchingBack = true;
        SelectedGame = game;
        _switchingBack = false;
        await RefreshCommand.ExecuteAsync(null);
    }

    public string GameRunningText => string.Format(CultureInfo.CurrentCulture, Strings.GameRunning, _game.DisplayName);

    public ProfilesViewModel Profiles { get; }

    public ModsViewModel Mods { get; }

    public SettingsViewModel Settings { get; }

    public UpdateViewModel Updates { get; }

    public VersionsViewModel Versions { get; }

    public HealthViewModel Health { get; }

    /// <summary>The main tab: 0 Profiles, 1 Mods, 2 Health, 3 Settings.</summary>
    [ObservableProperty]
    private int _selectedTab;

    /// <summary>"Launch game", or "Launch 1.60.3.1" when the selected profile has a version to choose from.</summary>
    public string LaunchLabel => Profiles.HasVersionChoice && Profiles.LaunchInstallation?.Version is { } v
        ? string.Format(CultureInfo.CurrentCulture, Strings.LaunchVersion, v)
        : Strings.LaunchGame;

    [ObservableProperty]
    private bool _isGameRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = Strings.Ready;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _hasProblems;

    [ObservableProperty]
    private string _gameVersion = "";

    public bool IsIdle => !IsBusy;

    partial void OnIsGameRunningChanged(bool value)
    {
        Profiles.IsGameRunning = value;
        Mods.OnGameRunningChanged();
        LaunchGameCommand.NotifyCanExecuteChanged();
        if (!value)
        {
            Versions.CleanUpRebuilt(afterGameClosed: true); // a packed copy's rebuilt files go once the game has closed
            Versions.StartAutoRepack(); // and a repack that launching stopped carries on
        }
    }

    private bool CanLaunch() => !IsGameRunning && !Versions.IsPreparing;

    /// <summary>
    /// Starts the game version the selected profile uses: the Steam install through Steam (with your Steam launch
    /// options), or a kept copy directly; a packed copy is rebuilt first. Asks about unsaved changes first.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchGameAsync()
    {
        if (!Profiles.ConfirmLeave())
        {
            return;
        }

        if (_snapshot is { } last && await Task.Run(() => BringBackUsedMods(last)) > 0)
        {
            Log.Information("Brought used mods back from the library before launching");
            await RefreshCommand.ExecuteAsync(null);
        }

        if (Versions.IsAutoPacking)
        {
            // Repacking reads the whole Steam install; it stops cleanly and carries on after the game has closed.
            await Versions.StopAutoRepackAsync();
        }

        var install = Profiles.LaunchInstallation;
        if (install is null || install.IsSteam)
        {
            try
            {
                Process.Start(GameLauncher.ThroughSteam(_game))?.Dispose();
                Log.Information("Launched {Game} through Steam", _game.ShortName);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.LaunchFailed, ex.Message));
            }

            return;
        }

        string label = VersionsViewModel.Describe(install);
        if (VersionsViewModel.Inspect(install.Folder) is { IsBroken: true })
        {
            // A Steam update changed files this copy shares since the last refresh.
            _dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.VersionsBrokenLaunch, label));
            _ = RefreshCommand.ExecuteAsync(null);
            return;
        }

        try
        {
            if (_platform.ActiveSteamAccountId is null)
            {
                // The game needs a signed-in Steam client; starting a copy without one fails or runs without DLC.
                Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true })?.Dispose();
                _dialogs.ShowInfo(Strings.VersionsNeedsSteam);
                return;
            }

            if (!await Versions.PrepareAsync(install) || IsGameRunning)
            {
                return;
            }

            Process.Start(GameLauncher.Directly(_game, install))?.Dispose();
            Log.Information("Launched {Game} {Version} from {Folder}", _game.ShortName, install.Version, install.Folder);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.LaunchCopyFailed, label, ex.Message));
        }
    }

    public ObservableCollection<string> Problems { get; } = [];

    private GameSnapshot? _snapshot;

    /// <summary>
    /// Mods in the library that a profile uses go back to mod\, so the game can load them. Runs after every refresh
    /// (every change Dispatch makes to a profile ends with one) and before launching. Never while the game runs.
    /// </summary>
    private int BringBackUsedMods(GameSnapshot snapshot)
    {
        if (snapshot.Environment.ModDir is not { } modDir || _platform.IsProcessRunning(snapshot.Environment.Game.ProcessName))
        {
            return 0;
        }

        var back = Dispatch.Core.Mods.ModLibrary.NeededBack(snapshot.Scan.Mods, snapshot.UsedPackageIds);
        if (back.Count == 0)
        {
            return 0;
        }

        try
        {
            var results = Dispatch.Core.Mods.ModLibrary.Move(_platform, snapshot.Environment.Game.ProcessName, back, modDir);
            foreach (var r in results)
            {
                Log.Information("Library: {Mod} back to the mod folder because a profile uses it: {Outcome} ({Message})", r.Mod.PackageId, r.Outcome, r.Message);
            }

            return results.Count(r => r.Outcome == Dispatch.Core.Mods.LibraryMoveOutcome.Moved);
        }
        catch (Dispatch.Core.Safety.GameRunningException)
        {
            return 0;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = Strings.Scanning;
        Progress = 0;
        try
        {
            var progress = new Progress<(int Done, int Total)>(p => Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total);
            var game = _game;
            var snapshot = await Task.Run(() => GameSnapshot.Load(_platform, _paths, Settings.Current, game, progress, CancellationToken.None));
            int broughtBack = await Task.Run(() => BringBackUsedMods(snapshot));
            if (broughtBack > 0)
            {
                // A profile uses mods that were in the library (e.g. just added to it): they're in mod\ again now.
                snapshot = await Task.Run(() => GameSnapshot.Load(_platform, _paths, Settings.Current, game, progress, CancellationToken.None));
            }

            _snapshot = snapshot;
            Mods.Load(snapshot);
            if (broughtBack > 0)
            {
                Mods.Message = string.Format(CultureInfo.CurrentCulture, Strings.LibraryBroughtBack, broughtBack);
            }

            Versions.Load(snapshot.Environment);
            Profiles.ConfigurePresets(_paths.ForGame(game).Presets, snapshot.Environment.Version?.ToString(), game);
            Profiles.Load(snapshot, Mods.ById);
            Mods.RefreshTargets();
            Health.Load(snapshot);
            Settings.Load(snapshot.Environment);

            Problems.Clear();
            foreach (var p in snapshot.Environment.Problems)
            {
                Problems.Add(p);
            }

            HasProblems = Problems.Count > 0;
            GameVersion = snapshot.Environment.Version is { } v ? $"{game.ShortName} {v}" : game.ShortName;
            foreach (var g in snapshot.InstalledGames.Where(g => !Games.Contains(g)))
            {
                Games.Add(g);
            }

            OnPropertyChanged(nameof(CanSwitchGame));
            Status = string.Format(CultureInfo.CurrentCulture, Strings.StatusScanned,
                                   snapshot.Scan.Mods.Count, snapshot.Scan.Elapsed.TotalSeconds, snapshot.Scan.FromCache);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error(ex, "Refresh failed");
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(_paths.Root);

    /// <summary>Starts a new Dispatch and closes this one (after asking about unsaved changes), e.g. to switch language.</summary>
    [RelayCommand]
    private void Restart()
    {
        if (!Profiles.ConfirmLeave() || Environment.ProcessPath is not { } exe)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })?.Dispose();
        System.Windows.Application.Current?.Shutdown();
    }

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(_paths.Logs);

    internal static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
    }
}
