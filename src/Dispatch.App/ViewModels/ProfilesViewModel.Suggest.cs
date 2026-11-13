using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Resources;
using Dispatch.Core.Mods;
using Dispatch.Core.Profiles;
using Serilog;

namespace Dispatch.App.ViewModels;

/// <summary>
/// "Suggest order…": sorts the editor's load order by the usual rules (see <see cref="LoadOrderSuggester"/>) after
/// asking, as an unsaved change: the user checks it and saves or discards it like any other edit.
/// </summary>
public sealed partial class ProfilesViewModel
{
    private bool CanSuggestOrder() => CanEdit;

    [RelayCommand(CanExecute = nameof(CanSuggestOrder))]
    private void SuggestOrder()
    {
        if (BuildSuggestion() is not { } review)
        {
            Message = Strings.SuggestNoChange;
            return;
        }

        if (_dialogs.ReviewSuggestion(review))
        {
            ApplySuggestion(review.Suggestion);
        }
    }

    /// <summary>The suggestion for the editor's current order, or null when it's already in that order.</summary>
    internal SuggestionViewModel? BuildSuggestion()
    {
        var installed = LoadOrder.Where(l => l.Mod is not null).GroupBy(l => l.PackageId).ToDictionary(g => g.Key, g => g.First().Mod!.Mod);
        var suggestion = LoadOrderSuggester.Suggest(LoadOrder.Select(l => l.Entry).ToList(), installed);
        return LoadOrderSuggester.IsUnchanged(suggestion) ? null : new SuggestionViewModel(Selected?.Name ?? "", suggestion, id => _mods.GetValueOrDefault(id));
    }

    internal void ApplySuggestion(IReadOnlyList<SuggestedEntry> suggestion)
    {
        var rows = LoadOrder.ToList();
        _suppressDirty = true;
        LoadOrder.Clear();
        foreach (var s in suggestion)
        {
            LoadOrder.Add(rows[s.From]);
        }

        _suppressDirty = false;
        OnLoadOrderChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)); // dirty, re-checked
        Log.Information("Applied the suggested load order to {Folder} (unsaved)", Selected?.Profile.FolderName);
        Message = Strings.SuggestApplied;
    }
}
