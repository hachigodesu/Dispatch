using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Resources;
using Dispatch.App.Services;
using Dispatch.Core.Games;
using Dispatch.Core.Mods;
using Dispatch.Core.Mods.Packages;
using Dispatch.Core.Profiles;
using Serilog;

namespace Dispatch.App.ViewModels;

/// <summary>One problem in the health report, with up to two fixes.</summary>
public sealed class HealthIssueViewModel(string title, string text)
{
    public string Title { get; } = title;

    public string Text { get; } = text;

    public bool HasText => Text.Length > 0;

    public string? PrimaryLabel { get; init; }

    public IAsyncRelayCommand? Primary { get; init; }

    public bool HasPrimary => Primary is not null;

    public string? SecondaryLabel { get; init; }

    public IAsyncRelayCommand? Secondary { get; init; }

    public bool HasSecondary => Secondary is not null;

    /// <summary>Worth knowing but often fine (the game's log warnings, file conflicts): shown, not counted as a problem.</summary>
    public bool IsNote { get; init; }
}

/// <summary>A profile in the health report.</summary>
public sealed partial class ProfileHealthItemViewModel(ProfileItemViewModel profile) : ObservableObject
{
    public ProfileItemViewModel Profile { get; } = profile;

    public string Name => Profile.Name;

    public string KindLabel => Profile.KindLabel;

    public ObservableCollection<HealthIssueViewModel> Issues { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHealthy))]
    private string _status = "";

    public bool IsHealthy => Problems == 0 && Profile.LoadOrder is not null;

    private int Problems => Issues.Count(i => !i.IsNote);

    public bool IsReadOnly => Profile.IsCloud;

    internal void UpdateStatus()
    {
        Status = Profile.LoadOrder is null
            ? string.Format(CultureInfo.CurrentCulture, Strings.HealthUnreadable, Profile.Error)
            : Problems switch
            {
                0 => Strings.HealthNoProblems,
                1 => Strings.HealthProblemOne,
                var n => string.Format(CultureInfo.CurrentCulture, Strings.HealthProblemCount, n),
            };
        OnPropertyChanged(nameof(IsHealthy));
    }
}

/// <summary>
/// v3: the Health tab. For every profile: mods that aren't installed, made for another game version than the one it
/// launches with, needing a DLC you don't have, older copies of installed mods, mods the newest save needs, mods the
/// game complained about, and conflicts. Each comes with a fix where there is one; fixes are normal profile changes
/// (backed up first, refused while the game runs). Steam Cloud profiles are only shown.
/// </summary>
public sealed partial class HealthViewModel(ProfilesViewModel profiles, Action<ProfileItemViewModel, int> openProfile) : ObservableObject
{
    private const int NamesShown = 8;
    private GameSnapshot? _snapshot;
    private CancellationTokenSource? _cts;

    public ObservableCollection<ProfileHealthItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _isChecking;

    /// <summary>The latest check, conflicts included (the self-test awaits it).</summary>
    internal Task Checked { get; private set; } = Task.CompletedTask;

    /// <summary>Called after every refresh (after the profiles are loaded).</summary>
    public void Load(GameSnapshot snapshot)
    {
        _snapshot = snapshot;
        Checked = RunAsync();
    }

    [RelayCommand]
    private async Task RecheckAsync()
    {
        Message = "";
        await (Checked = RunAsync());
    }

    private async Task RunAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        IsChecking = true;
        try
        {
            var installed = new Dictionary<string, ModInfo>(StringComparer.Ordinal);
            foreach (var m in snapshot.Scan.Mods.OrderBy(m => m.InLibrary))
            {
                installed.TryAdd(m.PackageId, m);
            }

            var duplicates = ModDuplicates.Find(snapshot.Scan.Mods);
            var list = profiles.Items.ToList();
            var launch = list.Select(p => profiles.LaunchFor(p)).ToList();
            var env = snapshot.Environment;
            var results = await Task.Run(() => list.Select((p, i) => p.LoadOrder is null
                ? null
                : ProfileHealthCheck.Check(new HealthInput(p.LoadOrder.TopFirst, installed, launch[i].Installation?.Version ?? env.Version, env.OwnedDlc,
                                                           duplicates, snapshot.GameLog, SaveReader.Newest(p.Profile)))).ToList(), cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Items.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                var item = new ProfileHealthItemViewModel(list[i]);
                foreach (var issue in results[i] ?? [])
                {
                    item.Issues.Add(Describe(item, issue, launch[i]));
                }

                item.UpdateStatus();
                Items.Add(item);
            }

            UpdateSummary();
            await CheckConflictsAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer check took over.
        }
        finally
        {
            if (_cts == cts)
            {
                IsChecking = false;
            }
        }
    }

    private void UpdateSummary()
    {
        int sick = Items.Count(i => !i.IsHealthy);
        Summary = sick == 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.HealthSummaryAllGood, Items.Count)
            : string.Format(CultureInfo.CurrentCulture, Strings.HealthSummary, sick, Items.Count);
    }

    /// <summary>Conflicts last: they need every active mod's file list (cached after the first time).</summary>
    private async Task CheckConflictsAsync(CancellationToken ct)
    {
        foreach (var item in Items.ToList())
        {
            if (item.Profile.LoadOrder is not { } order)
            {
                continue;
            }

            var mods = order.TopFirst.Select(e => profiles.ModFor(e.PackageId)).ToList();
            var ids = order.TopFirst.Select(e => e.PackageId).ToList();
            int pairs = await Task.Run(() =>
            {
                var sets = mods.Select((m, i) => m is null || m.Format == PackageFormat.Opaque ? new ModFileSet(ids[i], []) : ModFileIndex.Get(m)).ToList();
                return ConflictAnalyzer.Analyze(sets, sampleSize: 0).Pairs.Count;
            }, ct);
            if (pairs > 0)
            {
                item.Issues.Add(new HealthIssueViewModel(string.Format(CultureInfo.CurrentCulture, Strings.HealthConflictsTitle, pairs), Strings.HealthConflictsText)
                {
                    IsNote = true,
                    PrimaryLabel = Strings.HealthFixShowConflicts,
                    Primary = new AsyncRelayCommand(() => { openProfile(item.Profile, 2); return Task.CompletedTask; }),
                });
                item.UpdateStatus();
                UpdateSummary();
            }
        }
    }

    private HealthIssueViewModel Describe(ProfileHealthItemViewModel item, HealthIssue issue,
                                          (GameInstallation? Installation, bool Pinned, VersionChoice? Auto) launch)
    {
        var p = item.Profile;
        bool editable = !p.IsCloud;
        string names = Names(issue.Mods);
        var ids = issue.Mods.Select(m => m.PackageId).ToList();
        IAsyncRelayCommand Remove() => new AsyncRelayCommand(() => Fix(p, current => LoadOrderEdits.Remove(current, ids)));
        switch (issue.Kind)
        {
            case HealthIssueKind.MissingMods:
                var workshop = issue.Mods.Select(m => ModInfo.TryParseWorkshopId(m.PackageId)).OfType<ulong>().ToList();
                return new HealthIssueViewModel(string.Format(CultureInfo.CurrentCulture, Strings.HealthMissingTitle, issue.Mods.Count), names)
                {
                    PrimaryLabel = editable ? Strings.HealthFixRemove : null,
                    Primary = editable ? Remove() : null,
                    SecondaryLabel = workshop.Count > 0 ? Strings.HealthFixWorkshop : null,
                    Secondary = workshop.Count > 0 ? new AsyncRelayCommand(() => OpenWorkshop(workshop)) : null,
                };

            case HealthIssueKind.WrongVersion:
                string version = launch.Installation?.Version?.ToString() ?? _snapshot?.Environment.Version?.ToString() ?? Strings.VersionUnknown;
                bool autoBetter = launch.Pinned && launch.Auto is { } auto && auto.Installation != launch.Installation &&
                                  auto.Installation.Version is { } av && VersionPicker.CountMismatches(issue.Mods.Select(m => m.Mod!), av) < issue.Mods.Count;
                return new HealthIssueViewModel(string.Format(CultureInfo.CurrentCulture, Strings.HealthWrongVersionTitle, issue.Mods.Count, version), names)
                {
                    PrimaryLabel = autoBetter ? string.Format(CultureInfo.CurrentCulture, Strings.HealthFixAutomatic, VersionsViewModel.Describe(launch.Auto!.Installation))
                                 : editable ? Strings.HealthFixRemove : null,
                    Primary = autoBetter ? new AsyncRelayCommand(() => UseAutomatic(p)) : editable ? Remove() : null,
                    SecondaryLabel = autoBetter && editable ? Strings.HealthFixRemove : null,
                    Secondary = autoBetter && editable ? Remove() : null,
                };

            case HealthIssueKind.MissingDlc:
                return new HealthIssueViewModel(string.Format(CultureInfo.CurrentCulture, Strings.HealthMissingDlcTitle, issue.Mods.Count), names)
                {
                    PrimaryLabel = editable ? Strings.HealthFixRemove : null,
                    Primary = editable ? Remove() : null,
                };

            case HealthIssueKind.OlderCopyActive:
                var swap = issue.Mods.Where(m => m.Replacement is not null)
                                .ToDictionary(m => m.PackageId, m => new ActiveModEntry(m.Replacement!.PackageId, m.Replacement.DisplayName), StringComparer.Ordinal);
                string text = string.Join(Environment.NewLine, issue.Mods.Select(m =>
                    string.Format(CultureInfo.CurrentCulture, Strings.HealthOlderLine, m.DisplayName, m.Mod?.Manifest?.PackageVersion ?? "?",
                                  m.Replacement!.Manifest?.PackageVersion ?? "?")));
                return new HealthIssueViewModel(string.Format(CultureInfo.CurrentCulture, Strings.HealthOlderTitle, issue.Mods.Count), text)
                {
                    PrimaryLabel = editable ? Strings.HealthFixNewer : null,
                    Primary = editable ? new AsyncRelayCommand(() => Fix(p, current => LoadOrderEdits.Replace(current, swap))) : null,
                };

            case HealthIssueKind.SaveNeedsMods:
                var have = issue.Mods.Where(m => m.Mod is not null).ToList();
                var lacking = issue.Mods.Where(m => m.Mod is null).ToList();
                var lines = new List<string>();
                if (have.Count > 0)
                {
                    lines.Add(Names(have));
                }

                if (lacking.Count > 0)
                {
                    lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.HealthSaveNotInstalled, Names(lacking)));
                }

                var lackingWorkshop = lacking.Select(m => ModInfo.TryParseWorkshopId(m.PackageId)).OfType<ulong>().ToList();
                return new HealthIssueViewModel(string.Format(CultureInfo.CurrentCulture, Strings.HealthSaveTitle, issue.Detail, issue.Mods.Count),
                                                string.Join(Environment.NewLine, lines))
                {
                    PrimaryLabel = editable && have.Count > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.HealthFixAddSaveMods, have.Count) : null,
                    Primary = editable && have.Count > 0
                        ? new AsyncRelayCommand(() => Fix(p, current => LoadOrderEdits.AddToTop(current, have.Select(m => new ActiveModEntry(m.PackageId, m.DisplayName)))))
                        : null,
                    SecondaryLabel = lackingWorkshop.Count > 0 ? Strings.HealthFixWorkshop : null,
                    Secondary = lackingWorkshop.Count > 0 ? new AsyncRelayCommand(() => OpenWorkshop(lackingWorkshop)) : null,
                };

            default: // GameWarnings
                return new HealthIssueViewModel(string.Format(CultureInfo.CurrentCulture, Strings.HealthGameWarningsTitle, issue.Mods.Count), names)
                {
                    IsNote = true,
                    PrimaryLabel = Strings.HealthFixOpenProfile,
                    Primary = new AsyncRelayCommand(() => { openProfile(p, 0); return Task.CompletedTask; }),
                };
        }
    }

    private static string Names(IReadOnlyList<HealthMod> mods)
    {
        string shown = string.Join(", ", mods.Take(NamesShown).Select(m => m.DisplayName));
        return mods.Count > NamesShown ? shown + " " + string.Format(CultureInfo.CurrentCulture, Strings.HealthMore, mods.Count - NamesShown) : shown;
    }

    private async Task Fix(ProfileItemViewModel profile, Func<IReadOnlyList<ActiveModEntry>, List<ActiveModEntry>> change)
    {
        if (await profiles.FixProfileAsync(profile, change, string.Format(CultureInfo.CurrentCulture, Strings.HealthFixed, profile.Name)) is { } message)
        {
            Message = message; // the save refreshed everything, which also checked again
        }
    }

    private async Task UseAutomatic(ProfileItemViewModel profile)
    {
        profiles.UseAutomaticVersion(profile);
        await RecheckAsync();
        Message = string.Format(CultureInfo.CurrentCulture, Strings.HealthFixedVersion, profile.Name);
    }

    private static Task OpenWorkshop(IEnumerable<ulong> ids)
    {
        foreach (ulong id in ids.Take(5))
        {
            try
            {
                Process.Start(new ProcessStartInfo($"steam://url/CommunityFilePage/{id.ToString(CultureInfo.InvariantCulture)}") { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Warning(ex, "Couldn't open Workshop item {Id}", id);
            }
        }

        return Task.CompletedTask;
    }
}
