using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Dispatch.App.Resources;
using Dispatch.Core.Profiles;

namespace Dispatch.App.ViewModels;

/// <summary>One row of the suggested order.</summary>
public sealed class SuggestionRowViewModel(SuggestedEntry entry, int position, ModItemViewModel? mod)
{
    public SuggestedEntry Entry { get; } = entry;

    public int Position { get; } = position;

    public string DisplayName => Entry.Entry.DisplayName;

    public string? IconFile => mod?.IconFile;

    public bool IsMoved => Entry.IsMoved;

    public bool MovedUp => IsMoved && Entry.From + 1 > Position;

    /// <summary>"9 → 4" for a mod a rule moves; empty otherwise.</summary>
    public string MoveLabel => IsMoved ? string.Format(CultureInfo.CurrentCulture, Strings.SuggestMove, Entry.From + 1, Position) : "";

    public string KindLabel => Entry.Group switch
    {
        SuggestedGroup.Look => Strings.SuggestGroupLook,
        SuggestedGroup.TrafficAndPhysics => Strings.SuggestGroupTraffic,
        SuggestedGroup.Vehicles => Strings.SuggestGroupVehicles,
        _ => Strings.SuggestGroupMaps,
    };

    /// <summary>Why it moves, or what kind it is.</summary>
    public string Detail => Entry.Reason switch
    {
        SuggestedReason.PatchAboveMap => string.Format(CultureInfo.CurrentCulture, Strings.SuggestReasonPatch, Entry.Above!.DisplayName),
        SuggestedReason.ByKind => Entry.Group switch
        {
            SuggestedGroup.Look => Strings.SuggestReasonLook,
            SuggestedGroup.TrafficAndPhysics => Strings.SuggestReasonTraffic,
            _ => Strings.SuggestReasonVehicles,
        },
        _ => Entry.Entry.PackageId,
    };
}

/// <summary>The "Suggest order" review window: the whole suggested order, the mods that move highlighted, and why.</summary>
public sealed partial class SuggestionViewModel : ObservableObject
{
    private readonly List<SuggestionRowViewModel> _all;

    public SuggestionViewModel(string profileName, IReadOnlyList<SuggestedEntry> suggestion, Func<string, ModItemViewModel?> modFor)
    {
        Suggestion = suggestion;
        _all = suggestion.Select((s, i) => new SuggestionRowViewModel(s, i + 1, modFor(s.Entry.PackageId))).ToList();
        int moved = _all.Count(r => r.IsMoved);
        Header = string.Format(CultureInfo.CurrentCulture, Strings.SuggestHeader, profileName);
        Summary = moved == 1
            ? Strings.SuggestSummaryOne
            : string.Format(CultureInfo.CurrentCulture, Strings.SuggestSummary, moved, _all.Count);
        _onlyMoved = moved > 0;
        Refresh();
    }

    public IReadOnlyList<SuggestedEntry> Suggestion { get; }

    public string Header { get; }

    public string Summary { get; }

    public ObservableCollection<SuggestionRowViewModel> Rows { get; } = [];

    /// <summary>Show only the mods a rule moves (on by default), or the whole suggested order.</summary>
    [ObservableProperty]
    private bool _onlyMoved;

    partial void OnOnlyMovedChanged(bool value) => Refresh();

    private void Refresh()
    {
        Rows.Clear();
        foreach (var r in _all.Where(r => !OnlyMoved || r.IsMoved))
        {
            Rows.Add(r);
        }
    }
}
