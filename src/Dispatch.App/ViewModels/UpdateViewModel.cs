using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Resources;
using Dispatch.App.Services;
using Dispatch.Core.Updates;
using Serilog;

namespace Dispatch.App.ViewModels;

/// <summary>
/// "A new version is available": checks GitHub at most once a day (can be turned off in Settings), shows a banner, and
/// installs the update. Installed copies download the verified installer, run it silently and restart; portable
/// copies open the release page instead.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly AppSettings _settings;
    private readonly AppPaths _paths;
    private readonly IDialogs _dialogs;
    private readonly Func<bool> _confirmLeave;
    private readonly Action _shutdown;
    private readonly UpdateChecker _checker = new(Http);

    public UpdateViewModel(AppSettings settings, AppPaths paths, IDialogs dialogs, Func<bool> confirmLeave, Action shutdown)
    {
        _settings = settings;
        _paths = paths;
        _dialogs = dialogs;
        _confirmLeave = confirmLeave;
        _shutdown = shutdown;
    }

    public static Version CurrentVersion => typeof(UpdateViewModel).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>An installer-made copy has Inno Setup's uninstaller next to it; a portable copy doesn't.</summary>
    public static bool IsInstalledCopy => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(BannerText))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(SkipCommand), nameof(OpenReleasePageCommand))]
    private UpdateInfo? _available;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(CheckNowCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _status = "";

    public bool HasUpdate => Available is not null;

    public string BannerText => Available is { } u
        ? string.Format(CultureInfo.CurrentCulture, Strings.UpdateAvailable, u.Version.ToString(3), CurrentVersion.ToString(3))
        : "";

    public string InstallLabel => IsInstalledCopy ? Strings.UpdateInstall : Strings.UpdateDownload;

    public bool AutoCheck
    {
        get => _settings.CheckForUpdates;
        set
        {
            if (_settings.CheckForUpdates != value)
            {
                _settings.CheckForUpdates = value;
                SaveSettings();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Quietly checks if it's on and the last check was more than a day ago. Errors only go to the log.</summary>
    public async Task CheckOnStartupAsync()
    {
        if (AutoCheck && (_settings.LastUpdateCheckUtc is not { } last || DateTime.UtcNow - last > TimeSpan.FromHours(20)))
        {
            await CheckCoreAsync(manual: false);
        }
    }

    private bool CanCheck() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNowAsync() => CheckCoreAsync(manual: true);

    private async Task CheckCoreAsync(bool manual)
    {
        IsBusy = true;
        Status = Strings.UpdateChecking;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var update = await _checker.CheckAsync(CurrentVersion, cts.Token);
            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            SaveSettings();
            if (update is not null && !manual && _settings.SkippedUpdateVersion == update.Version.ToString(3))
            {
                Log.Information("Update {Version} available but skipped by the user", update.Version);
                update = null;
            }

            Available = update;
            Status = update is null
                ? string.Format(CultureInfo.CurrentCulture, Strings.UpdateNone, CurrentVersion.ToString(3))
                : BannerText;
            Log.Information("Update check: {Result}", update?.Tag ?? "up to date");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException)
        {
            Log.Warning(ex, "Update check failed");
            if (!manual)
            {
                // Don't retry on every start while GitHub is unreachable (or the repository isn't public).
                _settings.LastUpdateCheckUtc = DateTime.UtcNow;
                SaveSettings();
            }

            Status = string.Format(CultureInfo.CurrentCulture, Strings.UpdateCheckFailed,
                                   ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } ? Strings.UpdateNoReleases : ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanInstall() => HasUpdate && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var update = Available!;
        if (!IsInstalledCopy || update.Setup is null)
        {
            OpenReleasePage();
            return;
        }

        if (!_confirmLeave() ||
            !_dialogs.Confirm(Strings.UpdateInstall, string.Format(CultureInfo.CurrentCulture, Strings.UpdateConfirm,
                                                                   update.Version.ToString(3), UpdateChecker.FormatSize(update.Setup.Size))))
        {
            return;
        }

        IsBusy = true;
        try
        {
            Status = Strings.UpdateDownloading;
            var progress = new Progress<double>(p => Progress = p * 100);
            string file = await _checker.DownloadAsync(update.Setup, Path.Combine(_paths.Root, "updates"), progress, CancellationToken.None);
            Log.Information("Downloaded and verified {File}; starting it", file);

            // /SILENT: progress window only, same folder and options as before. /RELAUNCH=1 starts Dispatch again afterwards.
            Process.Start(new ProcessStartInfo(file, "/SILENT /RELAUNCH=1") { UseShellExecute = true })?.Dispose();
            _shutdown();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException
                                       or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            Log.Warning(ex, "Installing the update failed");
            Status = "";
            _dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.UpdateFailed, ex.Message));
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    [RelayCommand(CanExecute = nameof(HasUpdate))]
    private void OpenReleasePage()
    {
        if (Available is { } u && u.PageUrl.Scheme == Uri.UriSchemeHttps && u.PageUrl.Host == "github.com")
        {
            Process.Start(new ProcessStartInfo(u.PageUrl.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
    }

    /// <summary>Don't show this version again (a later one will still be offered).</summary>
    [RelayCommand(CanExecute = nameof(HasUpdate))]
    private void Skip()
    {
        _settings.SkippedUpdateVersion = Available!.Version.ToString(3);
        SaveSettings();
        Available = null;
    }

    [RelayCommand]
    private void Dismiss() => Available = null;

    private void SaveSettings()
    {
        try
        {
            _settings.Save(_paths.SettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Couldn't save settings");
        }
    }
}
