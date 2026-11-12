using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Dispatch.App.Resources;
using Dispatch.Core.Mods;
using Dispatch.Core.Mods.Packages;
using Serilog;

namespace Dispatch.App.ViewModels;

/// <summary>
/// v2: which mods in the load order replace each other's files. Re-checked shortly after every change to the list
/// (so dragging rows around doesn't start one check per step), off the UI thread. File tables are cached, so only the
/// first check of a big profile takes a moment.
/// </summary>
public sealed partial class ProfilesViewModel
{
    private CancellationTokenSource? _conflictCts;
    private ConflictReport _conflictReport = ConflictReport.Empty;

    /// <summary>The conflicting pairs shown in the Conflicts panel (all, or only the selected mod's).</summary>
    public ObservableCollection<ConflictItemViewModel> Conflicts { get; } = [];

    [ObservableProperty]
    private string _conflictSummary = "";

    [ObservableProperty]
    private string _conflictsHeader = Strings.PanelConflicts;

    [ObservableProperty]
    private bool _isCheckingConflicts;

    [ObservableProperty]
    private bool _conflictsOnlySelected;

    [ObservableProperty]
    private ConflictItemViewModel? _selectedConflict;

    /// <summary>The latest check (the self-test awaits it).</summary>
    internal Task ConflictCheck { get; private set; } = Task.CompletedTask;

    private void ScheduleConflictCheck(int delayMs = 400)
    {
        _conflictCts?.Cancel();
        var cts = _conflictCts = new CancellationTokenSource();
        ConflictCheck = CheckConflictsAsync(LoadOrder.ToList(), delayMs, cts.Token);
    }

    private async Task CheckConflictsAsync(List<LoadOrderItemViewModel> order, int delayMs, CancellationToken ct)
    {
        try
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, ct);
            }

            IsCheckingConflicts = true;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var mods = order.Select(o => o.Mod?.Mod).ToList();
            var ids = order.Select(o => o.PackageId).ToList();
            var (report, unchecked_) = await Task.Run(() =>
            {
                var sets = new List<ModFileSet>(mods.Count);
                int notChecked = 0;
                for (int i = 0; i < mods.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (mods[i] is not { } mod || mod.Format == PackageFormat.Opaque)
                    {
                        notChecked += mods[i] is null ? 0 : 1; // missing mods aren't loaded by the game anyway
                        sets.Add(new ModFileSet(ids[i], []));
                        continue;
                    }

                    sets.Add(ModFileIndex.Get(mod));
                }

                return (ConflictAnalyzer.Analyze(sets), notChecked);
            }, ct);

            ct.ThrowIfCancellationRequested();
            Log.Information("Conflict check: {Mods} mods, {Pairs} pairs, {Unchecked} protected, {Ms} ms",
                            order.Count, report.Pairs.Count, unchecked_, timer.ElapsedMilliseconds);
            ApplyConflicts(order, report, unchecked_);
        }
        catch (OperationCanceledException)
        {
            // A newer check replaced this one.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warning(ex, "Conflict check failed");
            ConflictSummary = Strings.ConflictsFailed;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsCheckingConflicts = false;
            }
        }
    }

    private void ApplyConflicts(List<LoadOrderItemViewModel> order, ConflictReport report, int notChecked)
    {
        _conflictReport = report;
        var byId = order.GroupBy(o => o.PackageId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var item in order)
        {
            int wins = report.Overrides(item.PackageId).Count();
            int losses = report.OverriddenBy(item.PackageId).Count();
            item.SetConflicts(wins, losses);
        }

        int involved = report.Pairs.SelectMany(p => new[] { p.WinnerId, p.LoserId }).Distinct().Count();
        ConflictSummary = (report.Pairs.Count switch
                          {
                              0 => Strings.ConflictsNone,
                              1 => Strings.ConflictsSummaryOne,
                              _ => string.Format(CultureInfo.CurrentCulture, Strings.ConflictsSummary, report.Pairs.Count, involved),
                          })
                          + (notChecked == 0 ? "" : " " + string.Format(CultureInfo.CurrentCulture, Strings.ConflictsNotChecked, notChecked));
        ConflictsHeader = report.Pairs.Count == 0
            ? Strings.PanelConflicts
            : string.Format(CultureInfo.CurrentCulture, Strings.PanelConflictsCount, report.Pairs.Count);
        _conflictItems = report.Pairs
            .Where(p => byId.ContainsKey(p.WinnerId) && byId.ContainsKey(p.LoserId))
            .Select(p => new ConflictItemViewModel(p, byId[p.WinnerId], byId[p.LoserId]))
            .ToList();
        RefreshConflictList();
    }

    private List<ConflictItemViewModel> _conflictItems = [];

    private void RefreshConflictList()
    {
        string? only = ConflictsOnlySelected ? SelectedEntry?.PackageId : null;
        var keep = SelectedConflict?.Conflict;
        Conflicts.Clear();
        foreach (var c in _conflictItems.Where(c => only is null || c.Conflict.WinnerId == only || c.Conflict.LoserId == only)
                                        .OrderBy(c => c.Winner.Position).ThenBy(c => c.Loser.Position))
        {
            Conflicts.Add(c);
        }

        SelectedConflict = Conflicts.FirstOrDefault(c => keep is not null &&
                                                         c.Conflict.WinnerId == keep.WinnerId && c.Conflict.LoserId == keep.LoserId);
    }

    private void ClearConflicts()
    {
        _conflictCts?.Cancel();
        _conflictReport = ConflictReport.Empty;
        _conflictItems = [];
        Conflicts.Clear();
        ConflictSummary = "";
        ConflictsHeader = Strings.PanelConflicts;
        IsCheckingConflicts = false;
    }

    partial void OnConflictsOnlySelectedChanged(bool value) => RefreshConflictList();

    partial void OnSelectedConflictChanged(ConflictItemViewModel? value) => value?.EnsureExamplesLoaded();

    /// <summary>Report of the latest finished check (for the self-test).</summary>
    internal ConflictReport LastConflictReport => _conflictReport;
}

/// <summary>One pair: <see cref="Winner"/> (higher in the list) replaces some files of <see cref="Loser"/>.</summary>
public sealed partial class ConflictItemViewModel(ModConflict conflict, LoadOrderItemViewModel winner, LoadOrderItemViewModel loser)
    : ObservableObject
{
    private bool _examplesLoaded;

    public ModConflict Conflict { get; } = conflict;

    public LoadOrderItemViewModel Winner { get; } = winner;

    public LoadOrderItemViewModel Loser { get; } = loser;

    public string Title => string.Format(CultureInfo.CurrentCulture, Strings.ConflictPair, Winner.Position, Winner.DisplayName,
                                         Loser.Position, Loser.DisplayName);

    public string FilesLabel => string.Format(CultureInfo.CurrentCulture,
                                              Conflict.FileCount == 1 ? Strings.ConflictOneFile : Strings.ConflictFiles, Conflict.FileCount);

    /// <summary>Some of the shared file names, loaded when the pair is selected. Protected archives may hide them.</summary>
    [ObservableProperty]
    private string _examples = "";

    public void EnsureExamplesLoaded()
    {
        if (_examplesLoaded)
        {
            return;
        }

        _examplesLoaded = true;
        Examples = Strings.Loading;
        var sample = Conflict.Sample;
        var winnerMod = Winner.Mod?.Mod;
        var loserMod = Loser.Mod?.Mod;
        Task.Run(() =>
        {
            var names = new Dictionary<ulong, string>();
            foreach (var mod in new[] { winnerMod, loserMod }.OfType<ModInfo>())
            {
                var missing = sample.Where(h => !names.ContainsKey(h)).ToList();
                if (missing.Count == 0)
                {
                    break;
                }

                foreach (var (h, n) in ModFileIndex.ResolveNames(mod, missing))
                {
                    names[h] = n;
                }
            }

            return names;
        }).ContinueWith(t =>
        {
            var names = t.IsCompletedSuccessfully ? t.Result.Values.Order(StringComparer.Ordinal).ToList() : [];
            int hidden = Conflict.FileCount - names.Count;
            Examples = names.Count == 0
                ? Strings.ConflictNamesHidden
                : string.Join("\n", names) + (hidden > 0
                    ? "\n" + string.Format(CultureInfo.CurrentCulture, Strings.ConflictMoreFiles, hidden)
                    : "");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
