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
                                 Dispatch.Core.Mods.ModNotesStore.Load(paths.ForGame(_game).NotesFile), () => RefreshCommand.ExecuteAsync(null));
        _dialogs = dialogs;
        Updates = new UpdateViewModel(settings, paths, dialogs, () => Profiles.ConfirmLeave(),
                                      () => System.Windows.Application.Current?.Shutdown());
        Games.Add(_game);
        _selectedGame = _game;

        // Poll for the game process every 2 seconds (cheap) so the banner is always current.
        _gameWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _gameWatch.Tick += (_, _) => IsGameRunning = _platform.IsProcessRunning(_game.ProcessName);
        _gameWatch.Start();
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
    }

    private bool CanLaunch() => !IsGameRunning;

    /// <summary>Starts the game through Steam (with your Steam launch options). Asks about unsaved changes first.</summary>
    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private void LaunchGame()
    {
        if (!Profiles.ConfirmLeave())
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("steam://rungameid/" + _game.SteamAppId.ToString(CultureInfo.InvariantCulture))
            { UseShellExecute = true })?.Dispose();
            Log.Information("Launched {Game} through Steam", _game.ShortName);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.LaunchFailed, ex.Message));
        }
    }

    public ObservableCollection<string> Problems { get; } = [];

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

            Mods.Load(snapshot);
            Profiles.ConfigurePresets(_paths.ForGame(game).Presets, snapshot.Environment.Version?.ToString(), game);
            Profiles.Load(snapshot, Mods.ById);
            Mods.RefreshTargets();
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
