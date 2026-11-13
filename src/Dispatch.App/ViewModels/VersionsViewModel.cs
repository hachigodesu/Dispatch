using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Resources;
using Dispatch.App.Services;
using Dispatch.Core.Detection;
using Dispatch.Core.Games;
using Dispatch.Core.Platform;
using Microsoft.Win32;
using Serilog;

namespace Dispatch.App.ViewModels;

/// <summary>One row in Settings → Game versions.</summary>
public sealed class InstallationItemViewModel(GameInstallation installation, bool exists, CopyHealth? health = null)
{
    public GameInstallation Installation { get; } = installation;

    public bool Exists { get; } = exists;

    /// <summary>For copies: shared files still intact, and the space the copy really uses.</summary>
    public CopyHealth? Health { get; } = health;

    /// <summary>A copy whose shared files were changed by a Steam update; never launched.</summary>
    public bool IsBroken => Health?.IsBroken == true;

    public bool CanLaunch => Exists && !IsBroken;

    public string Label => VersionsViewModel.Describe(Installation);

    public string Folder => Installation.Folder;

    public string Detail => !Exists
        ? Strings.VersionsMissing
        : IsBroken && Health!.IsPacked
            ? string.Format(CultureInfo.CurrentCulture, Strings.VersionsPackDamaged, Health.Changed[0])
            : IsBroken
                ? string.Format(CultureInfo.CurrentCulture, Strings.VersionsBroken, Health!.Changed.Count,
                                Installation.Version?.ToString() ?? Strings.VersionUnknown)
                : Installation.SteamBranch is not null ? Strings.VersionsBranchHint : "";

    public bool HasDetail => Detail.Length > 0;

    /// <summary>Explorer counts shared files fully; this says what the copy really takes.</summary>
    public string Space => Health switch
    {
        null => "",
        { Pack: { StaleBaseBytes: > 0 } p } h => string.Format(CultureInfo.CurrentCulture, Strings.VersionsSpacePackedStale,
                                                                 VersionsViewModel.FormatGb(h.OwnBytes), VersionsViewModel.FormatGb(p.StaleBaseBytes)),
        { Pack: { SharedWithCopiesBytes: > 0 } p } h => string.Format(CultureInfo.CurrentCulture, Strings.VersionsSpacePackedShared,
                                                                        VersionsViewModel.FormatGb(h.OwnBytes),
                                                                        VersionsViewModel.FormatGb(p.SharedWithCopiesBytes)),
        { IsPacked: true } h => string.Format(CultureInfo.CurrentCulture, Strings.VersionsSpacePacked, VersionsViewModel.FormatGb(h.OwnBytes)),
        { StillShared: > 0 } h => string.Format(CultureInfo.CurrentCulture, Strings.VersionsSpaceShared, VersionsViewModel.FormatGb(h.OwnBytes),
                                                VersionsViewModel.FormatGb(h.TotalBytes - h.OwnBytes)),
        var h => string.Format(CultureInfo.CurrentCulture, Strings.VersionsSpaceFull, VersionsViewModel.FormatGb(h.OwnBytes)),
    };

    public bool HasSpace => Space.Length > 0;

    /// <summary>A full copy, or a packed copy that holds on to Steam files Steam has replaced since.</summary>
    public bool CanPack => Exists && !IsBroken && !Installation.IsSteam && Health is { } h && (!h.IsPacked || h.Pack!.StaleBaseBytes > 0);

    /// <summary>A packed copy Steam has updated past: packing it again frees the old Steam files it holds on to.</summary>
    public bool NeedsRepack => CanLaunch && !Installation.IsSteam && Health is { IsPacked: true, Pack.StaleBaseBytes: > 0 };

    /// <summary>A packed copy that's up to date with the Steam install: other copies may use its data.</summary>
    public bool CanShareData => CanLaunch && !Installation.IsSteam && Health is { IsPacked: true, Pack.StaleBaseBytes: 0 };
}

/// <summary>
/// Settings → Game versions: the Steam install plus kept copies of other versions (made by Dispatch from the Steam
/// install, or added from a folder). Profiles pick one of these to launch with (see ProfilesViewModel.Versions).
/// Only copies are written to, never the Steam install; removing a copy only takes it off the list.
/// </summary>
public sealed partial class VersionsViewModel(IPlatform platform, AppSettings settings, AppPaths paths, IDialogs dialogs) : ObservableObject
{
    private GameDefinition _game = GameDefinition.FromKey(settings.Game);
    private GameEnvironment? _env;
    private CancellationTokenSource? _copyCancel;

    /// <summary>Raised when the list changes, so profiles can re-pick their version.</summary>
    public event Action? Changed;

    public GameDefinition Game => _game;

    public ObservableCollection<InstallationItemViewModel> Items { get; } = [];

    /// <summary>The installations that can be launched (Steam first, then copies whose folder exists).</summary>
    public IReadOnlyList<GameInstallation> Available => Items.Where(i => i.CanLaunch).Select(i => i.Installation).ToList();

    public GameInstallation? Steam => Items.FirstOrDefault(i => i.Installation.IsSteam)?.Installation;

    [ObservableProperty]
    private string _header = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(OpenFolderCommand), nameof(PackCommand))]
    private InstallationItemViewModel? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(KeepCopyCommand), nameof(AddCopyCommand), nameof(RemoveCommand), nameof(PackCommand))]
    private bool _isCopying;

    /// <summary>A packed copy is being rebuilt for playing (shown in a banner, as it's started from the header).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PackCommand))]
    [NotifyPropertyChangedFor(nameof(IsBannerVisible))]
    private bool _isPreparing;

    /// <summary>Packed copies are being packed again in the background after a Steam update (shown in the same banner).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(KeepCopyCommand), nameof(AddCopyCommand), nameof(RemoveCommand), nameof(PackCommand))]
    [NotifyPropertyChangedFor(nameof(IsBannerVisible))]
    private bool _isAutoPacking;

    public bool IsBannerVisible => IsPreparing || IsAutoPacking;

    /// <summary>
    /// Self-test only: copies inside <c>Root</c> are judged against the small fake install <c>Steam</c>, and only they
    /// are packed again automatically, so the user's real copies are never touched by the test.
    /// </summary>
    internal (string Steam, string Root)? TestOverride { get; set; }

    internal Task AutoRepackTask => _autoTask;

    private bool InTestRoot(string folder) =>
        TestOverride is { } t && folder.StartsWith(t.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Steam install a copy is compared with (the self-test's fake one for its own copies).</summary>
    private string? SteamFolderFor(string folder) => InTestRoot(folder) ? TestOverride!.Value.Steam : _env?.InstallDir;

    private CancellationTokenSource? _autoCancel;
    private Task _autoTask = Task.CompletedTask;
    private bool _autoStoppedByUser;
    private readonly HashSet<string> _autoFailed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pack kept versions again by themselves after Steam has updated the game (Settings, Game versions).</summary>
    public bool AutoRepack
    {
        get => settings.AutoRepackCopies;
        set
        {
            if (settings.AutoRepackCopies != value)
            {
                settings.AutoRepackCopies = value;
                Save();
                OnPropertyChanged();
                if (value)
                {
                    _autoStoppedByUser = false;
                    StartAutoRepack();
                }
            }
        }
    }

    [ObservableProperty]
    private double _prepareProgress;

    [ObservableProperty]
    private string _prepareStatus = "";

    private CancellationTokenSource? _prepareCancel;
    private DateTime _preparedUtc = DateTime.MinValue;

    [ObservableProperty]
    private double _copyProgress;

    [ObservableProperty]
    private string _copyStatus = "";

    public static string Describe(GameInstallation i)
    {
        string version = i.Version?.ToString() ?? Strings.VersionUnknown;
        if (!i.IsSteam)
        {
            return string.Format(CultureInfo.CurrentCulture, i.IsPacked ? Strings.VersionsPacked : Strings.VersionsCopy, version,
                                 Path.GetFileName(Path.TrimEndingDirectorySeparator(i.Folder)));
        }

        return i.SteamBranch is { } branch
            ? string.Format(CultureInfo.CurrentCulture, Strings.VersionsSteamBranch, version, branch)
            : string.Format(CultureInfo.CurrentCulture, Strings.VersionsSteam, version);
    }

    public static string FormatGb(long bytes) => bytes < 1073741824
        ? (bytes / 1048576.0).ToString("F0", CultureInfo.CurrentCulture) + " MB"
        : (bytes / 1073741824.0).ToString("F1", CultureInfo.CurrentCulture) + " GB";

    /// <summary>Rebuilds the list for the detected game (after every refresh or game switch).</summary>
    public void Load(GameEnvironment env)
    {
        _env = env;
        _game = env.Game;
        Header = string.Format(CultureInfo.CurrentCulture, Strings.VersionsHeader, env.Game.DisplayName);
        Rebuild();
        CleanUpRebuilt();
        StartAutoRepack();
    }

    /// <summary>Looks at the copies again (Steam may have updated the game meanwhile) and repacks if needed.</summary>
    public void CheckCopies()
    {
        if (_env is null || IsCopying || IsPreparing || IsAutoPacking)
        {
            return;
        }

        Rebuild();
        StartAutoRepack();
    }

    private void Rebuild()
    {
        string? keep = Selected?.Folder;
        Items.Clear();
        if (_env?.InstallDir is { } install)
        {
            var state = GameInstallations.ReadSteamState(_game, install);
            Items.Add(new InstallationItemViewModel(new GameInstallation(install, _env.Version, IsSteam: true, state?.Branch), exists: true));
        }

        foreach (string folder in settings.CopiesFor(_game))
        {
            try
            {
                GamePacker.Recover(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Couldn't finish an interrupted pack of {Folder}", folder);
            }

            var copy = Directory.Exists(folder) ? GameInstallations.ReadCopy(_game, folder) : null;
            Items.Add(copy is null
                ? new InstallationItemViewModel(new GameInstallation(folder, null, IsSteam: false), exists: false)
                : new InstallationItemViewModel(copy, exists: true, Inspect(folder, SteamFolderFor(folder))));
        }

        Selected = Items.FirstOrDefault(i => i.Folder == keep);
        KeepCopyCommand.NotifyCanExecuteChanged();
        PackCommand.NotifyCanExecuteChanged();
        Changed?.Invoke();
    }

    /// <summary>Reads only file metadata (a few hundred files), so it's quick enough for every refresh.</summary>
    internal static CopyHealth? Inspect(string folder, string? steamFolder = null)
    {
        try
        {
            var health = GameCopyRecord.Inspect(folder, steamFolder);
            if (health.IsBroken)
            {
                Log.Warning("Copy {Folder} is broken: {Count} shared file(s) changed, e.g. {Example}", folder, health.Changed.Count, health.Changed[0]);
            }

            return health;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warning(ex, "Couldn't inspect the copy {Folder}", folder);
            return null;
        }
    }

    private bool CanKeepCopy() => !IsCopying && !IsAutoPacking && Steam is not null;

    /// <summary>Copies the Steam install (whatever version Steam currently has) to a new folder and lists it.</summary>
    [RelayCommand(CanExecute = nameof(CanKeepCopy))]
    private async Task KeepCopyAsync()
    {
        var steam = Steam!;
        var game = _game;
        var state = GameInstallations.ReadSteamState(game, steam.Folder);
        if (state is { FullyInstalled: false })
        {
            dialogs.ShowError(Strings.VersionsSteamBusy);
            return;
        }

        if (platform.IsProcessRunning(game.ProcessName))
        {
            dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.VersionsCloseGame, game.DisplayName));
            return;
        }

        if (steam.Version is { } sv && Items.FirstOrDefault(i => !i.Installation.IsSteam && i.CanLaunch && i.Installation.Version == sv) is { } same)
        {
            dialogs.ShowInfo(string.Format(CultureInfo.CurrentCulture, Strings.VersionsAlreadyKept, sv, same.Folder));
            return;
        }

        string name = $"{game.ShortName} {steam.Version?.ToString() ?? "copy"}";
        var picker = new OpenFolderDialog
        {
            Title = string.Format(CultureInfo.CurrentCulture, Strings.VersionsPickParent, name),
            InitialDirectory = SteamLocator.LibraryOf(steam.Folder) ?? "",
        };
        if (picker.ShowDialog() != true)
        {
            return;
        }

        await CopyToAsync(game, steam, Path.Combine(picker.FolderName, name), state?.SizeOnDisk ?? 0);
    }

    /// <summary>The copy itself (also used by the self-test with a small fake install).</summary>
    internal async Task<bool> CopyToAsync(GameDefinition game, GameInstallation steam, string destination, long expectedSize)
    {
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.VersionsExists, destination));
            return false;
        }

        string full = Path.GetFullPath(destination);
        string install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(steam.Folder)) + Path.DirectorySeparatorChar;
        if (full.StartsWith(install, StringComparison.OrdinalIgnoreCase))
        {
            dialogs.ShowError(Strings.VersionsInsideInstall);
            return false;
        }

        IsCopying = true;
        CopyProgress = 0;
        CopyStatus = Strings.Working;
        try
        {
            GameCopier.Plan plan;
            try
            {
                plan = await Task.Run(() => GameCopier.Prepare(platform, game, steam.Folder, destination));
            }
            catch (InsufficientSpaceException ex)
            {
                dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.VersionsNoSpace, ex.Drive, FormatGb(ex.Needed), FormatGb(ex.Free)));
                return false;
            }

            Log.Information("Copy plan: {Files} files, {Bytes} bytes (Steam says {Expected}) from {Source} to {Destination}, shared: {Share}",
                            plan.FileCount, plan.TotalBytes, expectedSize, plan.Source, plan.Destination, plan.Share);
            string version = steam.Version?.ToString() ?? Strings.VersionUnknown;
            string question = plan.Share
                ? string.Format(CultureInfo.CurrentCulture, Strings.VersionsConfirmShare, game.DisplayName, version, FormatGb(plan.TotalBytes), plan.Destination)
                : string.Format(CultureInfo.CurrentCulture, Strings.VersionsConfirmCopy, game.DisplayName, version, FormatGb(plan.TotalBytes), plan.Destination)
                  + Environment.NewLine + Environment.NewLine + Strings.VersionsFullCopyTip;
            if (!dialogs.Confirm(Strings.VersionsKeepCopy, question))
            {
                return false;
            }

            _copyCancel = new CancellationTokenSource();
            var cancel = _copyCancel.Token;
            string total = FormatGb(plan.TotalBytes);
            var progress = new Progress<long>(done =>
            {
                CopyProgress = plan.TotalBytes == 0 ? 100 : 100.0 * done / plan.TotalBytes;
                CopyStatus = string.Format(CultureInfo.CurrentCulture, Strings.VersionsCopying, FormatGb(done), total);
            });
            var took = await Task.Run(() => GameCopier.Copy(platform, game, plan, progress, cancel, steam.Version?.ToString()), cancel);
            Log.Information("Kept {Game} {Version} in {Folder} ({Seconds:F0}s, shared: {Share})", game.ShortName, steam.Version, plan.Destination,
                            took.TotalSeconds, plan.Share);

            settings.GameCopies.Add(new GameCopySetting { Game = game.Key, Folder = plan.Destination });
            Save();
            if (game == _game)
            {
                Rebuild();
            }

            CopyStatus = string.Format(CultureInfo.CurrentCulture, Strings.VersionsCopyDone, steam.Version?.ToString() ?? "", plan.Destination);
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Information("Copy to {Destination} cancelled", destination);
            CopyStatus = Strings.VersionsCopyCancelled;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warning(ex, "Copy to {Destination} failed", destination);
            CopyStatus = "";
            dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.VersionsCopyFailed, ex.Message));
            return false;
        }
        finally
        {
            _copyCancel?.Dispose();
            _copyCancel = null;
            IsCopying = false;
            CopyProgress = 0;
        }
    }

    [RelayCommand]
    private void CancelCopy() => _copyCancel?.Cancel();

    private bool CanPack() => !IsCopying && !IsPreparing && !IsAutoPacking && Steam is not null && Selected is { CanPack: true };

    /// <summary>Other packed copies whose data a copy may use: up to date with the Steam install, so nothing old is kept alive.</summary>
    private List<string> ShareCandidates(string folder) =>
        Items.Where(i => i.CanShareData && !string.Equals(i.Folder, folder, StringComparison.OrdinalIgnoreCase)).Select(i => i.Folder).ToList();

    /// <summary>
    /// Packs the selected copy (or packs a packed copy again after a Steam update): only what differs from the Steam
    /// install is kept. See <see cref="GamePacker"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPack))]
    private async Task PackAsync()
    {
        var item = Selected!;
        var steam = Steam!;
        if (GameInstallations.ReadSteamState(_game, steam.Folder) is { FullyInstalled: false })
        {
            dialogs.ShowError(Strings.VersionsSteamBusy);
            return;
        }

        if (!GameCopier.CanShare(steam.Folder, item.Folder))
        {
            dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.VersionsPackSameDrive, Path.GetPathRoot(steam.Folder)));
            return;
        }

        var health = item.Health!;
        if (!health.IsPacked && health.OwnBytes < (1L << 30))
        {
            dialogs.ShowInfo(Strings.VersionsPackNotYet);
            return;
        }

        string steamVersion = steam.Version?.ToString() ?? Strings.VersionUnknown;
        string question = health.IsPacked
            ? string.Format(CultureInfo.CurrentCulture, Strings.VersionsConfirmRepack, item.Label, FormatGb(health.Pack!.StaleBaseBytes), steamVersion)
            : string.Format(CultureInfo.CurrentCulture, Strings.VersionsConfirmPack, item.Label, steamVersion, FormatGb(health.TotalBytes), item.Folder);
        if (!dialogs.Confirm(Strings.VersionsPack.TrimEnd('…'), question))
        {
            return;
        }

        await PackFolderAsync(_game, item.Folder, steam, item.Installation.Version?.ToString(), health.OwnBytes);
    }

    /// <summary>The packing itself (also used by the self-test with a small fake install).</summary>
    internal async Task<bool> PackFolderAsync(GameDefinition game, string folder, GameInstallation steam, string? version, long ownBefore)
    {
        if (platform.IsProcessRunning(game.ProcessName))
        {
            dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.VersionsCloseGame, game.DisplayName));
            return false;
        }

        IsCopying = true;
        CopyProgress = 0;
        CopyStatus = Strings.Working;
        _copyCancel = new CancellationTokenSource();
        var cancel = _copyCancel.Token;
        var progress = new Progress<PackProgress>(p =>
        {
            CopyProgress = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
            string format = p.Phase switch
            {
                PackPhase.Indexing => Strings.VersionsPackIndexing,
                PackPhase.Packing => Strings.VersionsPackPacking,
                _ => Strings.VersionsPackChecking,
            };
            CopyStatus = string.Format(CultureInfo.CurrentCulture, format, FormatGb(p.Done), FormatGb(p.Total));
        });
        try
        {
            var share = ShareCandidates(folder);
            var result = await Task.Run(() => GamePacker.Pack(platform, game, folder, steam.Folder, version, progress, cancel, shareWith: share), cancel);
            Log.Information("Packed {Folder}: {Data} bytes of its own data, {Files} files, {Bases} files used ({Shared} other copies), {Seconds:F0}s",
                            folder, result.DataBytes, result.Files, result.BaseFiles, result.SharedCopies, result.Took.TotalSeconds);
            long ownAfter = Inspect(folder, steam.Folder)?.OwnBytes ?? result.DataBytes;
            CopyStatus = string.Format(CultureInfo.CurrentCulture, Strings.VersionsPackDone, version ?? Path.GetFileName(folder), FormatGb(ownAfter),
                                       FormatGb(ownBefore));
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Information("Packing {Folder} cancelled", folder);
            CopyStatus = Strings.VersionsPackCancelled;
            return false;
        }
        catch (InsufficientSpaceException ex)
        {
            Log.Warning(ex, "Packing {Folder} ran out of space", folder);
            CopyStatus = "";
            dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.VersionsNoSpace, ex.Drive, FormatGb(ex.Needed), FormatGb(ex.Free)));
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            Log.Warning(ex, "Packing {Folder} failed", folder);
            CopyStatus = "";
            dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.VersionsPackFailed, ex.Message));
            return false;
        }
        finally
        {
            _copyCancel?.Dispose();
            _copyCancel = null;
            IsCopying = false;
            CopyProgress = 0;
            if (game == _game)
            {
                Rebuild();
            }
        }
    }

    /// <summary>
    /// Rebuilds a packed copy's game folder before it's started (or reuses a complete one). Returns false when it
    /// couldn't (the reason was shown) or was cancelled.
    /// </summary>
    public async Task<bool> PrepareAsync(GameInstallation install)
    {
        if (!install.IsPacked)
        {
            return true;
        }

        if (IsPreparing || IsCopying)
        {
            return false;
        }

        string label = Describe(install);
        var game = _game;
        IsPreparing = true;
        PrepareProgress = 0;
        PrepareStatus = string.Format(CultureInfo.CurrentCulture, Strings.VersionsPreparing, label, FormatGb(0), "…");
        _prepareCancel = new CancellationTokenSource();
        var cancel = _prepareCancel.Token;
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            PrepareProgress = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
            PrepareStatus = string.Format(CultureInfo.CurrentCulture, Strings.VersionsPreparing, label, FormatGb(p.Done), FormatGb(p.Total));
        });
        try
        {
            var watch = Stopwatch.StartNew();
            string folder = await Task.Run(() => GamePacker.Rebuild(platform, game, install.Folder, progress, cancel), cancel);
            _preparedUtc = DateTime.UtcNow;
            Log.Information("Rebuilt {Folder} for playing in {Seconds:F0}s", folder, watch.Elapsed.TotalSeconds);
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Information("Preparing {Folder} cancelled", install.Folder);
            return false;
        }
        catch (InsufficientSpaceException ex)
        {
            dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.VersionsPrepareNoSpace, label, FormatGb(ex.Needed), ex.Drive, FormatGb(ex.Free)));
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            Log.Warning(ex, "Preparing {Folder} failed", install.Folder);
            dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.VersionsPrepareFailed, label, ex.Message));
            Rebuild();
            return false;
        }
        finally
        {
            _prepareCancel?.Dispose();
            _prepareCancel = null;
            IsPreparing = false;
            PrepareProgress = 0;
        }
    }

    /// <summary>The banner's Cancel: stops preparing, or the automatic repacking (for the rest of this session).</summary>
    [RelayCommand]
    private void CancelPrepare()
    {
        _prepareCancel?.Cancel();
        if (_autoCancel is { } auto)
        {
            _autoStoppedByUser = true;
            auto.Cancel();
        }
    }

    /// <summary>Starts packing outdated packed copies again in the background, if there are any and nothing else runs.</summary>
    public void StartAutoRepack()
    {
        if (_autoTask.IsCompleted)
        {
            _autoTask = AutoRepackAsync();
        }
    }

    /// <summary>Stops the background repacking (e.g. to launch the game) and waits until it has cleaned up.</summary>
    public async Task StopAutoRepackAsync()
    {
        _autoCancel?.Cancel();
        await _autoTask;
    }

    /// <summary>
    /// After a Steam update, packed copies hold on to the old Steam files (nearly the whole game, as SCS rebuilds every
    /// archive). This packs them again against the new Steam version, newest first so older ones can use its data.
    /// Runs only while Steam isn't busy and the game isn't running; a copy that fails isn't tried again this session.
    /// </summary>
    internal async Task AutoRepackAsync()
    {
        if (!settings.AutoRepackCopies || _autoStoppedByUser || IsCopying || IsPreparing || IsAutoPacking || Steam is not { } steam ||
            !Items.Any(i => i.NeedsRepack) || platform.IsProcessRunning(_game.ProcessName) ||
            GameInstallations.ReadSteamState(_game, steam.Folder) is { FullyInstalled: false })
        {
            return;
        }

        var game = _game;
        IsAutoPacking = true;
        _autoCancel = new CancellationTokenSource();
        var cancel = _autoCancel.Token;
        try
        {
            while (!cancel.IsCancellationRequested && game == _game)
            {
                var next = Items.Where(i => i.NeedsRepack && !_autoFailed.Contains(i.Folder) && (TestOverride is null || InTestRoot(i.Folder)) &&
                                            GameCopier.CanShare(steam.Folder, i.Folder))
                                .OrderByDescending(i => i.Installation.Version!, VersionComparer.Instance)
                                .FirstOrDefault();
                if (next is null)
                {
                    break;
                }

                string label = next.Label;
                string folder = next.Folder;
                string steamFolder = SteamFolderFor(folder) ?? steam.Folder;
                string? version = next.Installation.Version?.ToString();
                long before = next.Health!.OwnBytes;
                var share = ShareCandidates(folder);
                PrepareProgress = 0;
                PrepareStatus = string.Format(CultureInfo.CurrentCulture, Strings.VersionsAutoRepacking, label, Strings.Working);
                var progress = new Progress<PackProgress>(p =>
                {
                    PrepareProgress = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
                    string phase = p.Phase switch
                    {
                        PackPhase.Indexing => Strings.VersionsPackIndexing,
                        PackPhase.Packing => Strings.VersionsPackPacking,
                        _ => Strings.VersionsPackChecking,
                    };
                    PrepareStatus = string.Format(CultureInfo.CurrentCulture, Strings.VersionsAutoRepacking, label,
                                                  string.Format(CultureInfo.CurrentCulture, phase, FormatGb(p.Done), FormatGb(p.Total)));
                });
                try
                {
                    Log.Information("Packing {Folder} again after a Steam update ({Stale} bytes of old Steam files)", folder, next.Health.Pack!.StaleBaseBytes);
                    var result = await Task.Run(() => GamePacker.Pack(platform, game, folder, steamFolder, version, progress, cancel, shareWith: share), cancel);
                    Log.Information("Packed {Folder} again: {Data} bytes of its own data, {Shared} other copies used, {Seconds:F0}s", folder,
                                    result.DataBytes, result.SharedCopies, result.Took.TotalSeconds);
                    long after = Inspect(folder, steamFolder)?.OwnBytes ?? result.DataBytes;
                    CopyStatus = string.Format(CultureInfo.CurrentCulture, Strings.VersionsAutoRepackDone, label, FormatGb(after), FormatGb(before));
                }
                catch (OperationCanceledException)
                {
                    Log.Information("Packing {Folder} again was stopped", folder);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
                {
                    // Not again this session (e.g. no space, or Steam started updating); the copy is as it was.
                    Log.Warning(ex, "Packing {Folder} again after a Steam update failed", folder);
                    _autoFailed.Add(folder);
                    CopyStatus = string.Format(CultureInfo.CurrentCulture, Strings.VersionsAutoRepackFailed, label, ex.Message);
                }

                if (game == _game)
                {
                    Rebuild();
                }
            }
        }
        finally
        {
            _autoCancel.Dispose();
            _autoCancel = null;
            IsAutoPacking = false;
            PrepareProgress = 0;
        }
    }

    /// <summary>
    /// Removes packed copies' rebuilt game folders once the game isn't running (after playing, or left over from a
    /// session Dispatch was closed during). Not right after preparing unless the game has run and closed since: it may
    /// still be starting.
    /// </summary>
    public void CleanUpRebuilt(bool afterGameClosed = false)
    {
        if (IsPreparing || IsCopying || (!afterGameClosed && DateTime.UtcNow - _preparedUtc < TimeSpan.FromMinutes(2)) ||
            platform.IsProcessRunning(_game.ProcessName))
        {
            return;
        }

        bool removed = false;
        foreach (string folder in settings.CopiesFor(_game).Where(f => PackedCopy.IsPacked(f)))
        {
            string game = Path.Combine(folder, PackedCopy.GameFolder);
            if (!Directory.Exists(game) && !Directory.Exists(game + ".dispatch-partial"))
            {
                continue;
            }

            try
            {
                GamePacker.RemoveRebuilt(folder);
                Log.Information("Removed the rebuilt game folder of {Folder}", folder);
                removed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Couldn't remove the rebuilt game folder of {Folder} yet", folder);
            }
        }

        if (removed)
        {
            Rebuild();
        }
    }

    private bool CanAddCopy() => !IsCopying && !IsAutoPacking;

    /// <summary>Lists a copy the user made themselves (any folder with the game's executable).</summary>
    [RelayCommand(CanExecute = nameof(CanAddCopy))]
    private void AddCopy()
    {
        var picker = new OpenFolderDialog { Title = string.Format(CultureInfo.CurrentCulture, Strings.VersionsPickCopy, _game.DisplayName) };
        if (picker.ShowDialog() == true)
        {
            AddCopyFrom(picker.FolderName);
        }
    }

    internal bool AddCopyFrom(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (Steam is { } steam && string.Equals(folder, Path.TrimEndingDirectorySeparator(Path.GetFullPath(steam.Folder)), StringComparison.OrdinalIgnoreCase))
        {
            dialogs.ShowError(Strings.VersionsIsSteamInstall);
            return false;
        }

        if (Items.Any(i => string.Equals(Path.TrimEndingDirectorySeparator(i.Folder), folder, StringComparison.OrdinalIgnoreCase)))
        {
            dialogs.ShowError(Strings.VersionsAlreadyListed);
            return false;
        }

        if (GameInstallations.ReadCopy(_game, folder) is not { } copy)
        {
            dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.VersionsNotAGame, _game.DisplayName, _game.ExeRelativePath));
            return false;
        }

        Log.Information("Added {Game} copy {Version} at {Folder}", _game.ShortName, copy.Version, folder);
        settings.GameCopies.Add(new GameCopySetting { Game = _game.Key, Folder = folder });
        Save();
        Rebuild();
        Selected = Items.FirstOrDefault(i => i.Folder == folder);
        return true;
    }

    private bool CanRemove() => Selected is { Installation.IsSteam: false } && !IsCopying && !IsAutoPacking;

    /// <summary>Takes a copy off the list. The folder stays on disk: Dispatch doesn't delete game files.</summary>
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        var item = Selected!;
        if (!dialogs.Confirm(Strings.VersionsRemove, string.Format(CultureInfo.CurrentCulture, Strings.VersionsConfirmRemove, item.Label, item.Folder)))
        {
            return;
        }

        RemoveCopy(item.Folder);
    }

    internal void RemoveCopy(string folder)
    {
        settings.GameCopies.RemoveAll(c => string.Equals(c.Game, _game.Key, StringComparison.OrdinalIgnoreCase) &&
                                           string.Equals(c.Folder, folder, StringComparison.OrdinalIgnoreCase));
        Save();
        Log.Information("Removed {Game} copy {Folder} from the list", _game.ShortName, folder);
        Rebuild();
    }

    private bool CanOpenFolder() => Selected is { Exists: true };

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Selected!.Folder}\"") { UseShellExecute = true })?.Dispose();

    private void Save()
    {
        try
        {
            settings.Save(paths.SettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Couldn't save settings");
        }
    }
}
