using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Resources;
using Dispatch.App.Services;
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

    public MainViewModel(IPlatform platform, AppPaths paths, AppSettings settings, IDialogs dialogs)
    {
        _platform = platform;
        _paths = paths;
        Settings = new SettingsViewModel(settings, paths, () => RefreshCommand.ExecuteAsync(null));
        var game = Dispatch.Core.Games.GameDefinition.Ets2;
        BackupService Backups() => new(paths.Backups, new BackupRetention(settings.BackupRetentionSmall, settings.BackupRetentionFull));
        Profiles = new ProfilesViewModel(dialogs, () => new SafeWriter(platform, Backups(), game.ProcessName), Backups,
                                         () => RefreshCommand.ExecuteAsync(null));

        // Poll for the game process every 2 seconds (cheap) so the banner is always current.
        _gameWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _gameWatch.Tick += (_, _) => IsGameRunning = _platform.IsProcessRunning(Dispatch.Core.Games.GameDefinition.Ets2.ProcessName);
        _gameWatch.Start();
        IsGameRunning = _platform.IsProcessRunning(Dispatch.Core.Games.GameDefinition.Ets2.ProcessName);
        Profiles.IsGameRunning = IsGameRunning;
    }

    public ProfilesViewModel Profiles { get; }

    public ModsViewModel Mods { get; } = new();

    public SettingsViewModel Settings { get; }

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

    partial void OnIsGameRunningChanged(bool value) => Profiles.IsGameRunning = value;

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
            var snapshot = await Task.Run(() => GameSnapshot.Load(_platform, _paths, Settings.Current, progress, CancellationToken.None));

            Mods.Load(snapshot);
            Profiles.ConfigurePresets(Path.Combine(_paths.Root, "presets"), snapshot.Environment.Version?.ToString());
            Profiles.Load(snapshot, Mods.ById);
            Settings.Load(snapshot.Environment);

            Problems.Clear();
            foreach (var p in snapshot.Environment.Problems)
            {
                Problems.Add(p);
            }

            HasProblems = Problems.Count > 0;
            GameVersion = snapshot.Environment.Version is { } v ? $"ETS2 {v}" : "";
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

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(_paths.Logs);

    internal static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
    }
}
