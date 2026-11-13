using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Resources;
using Dispatch.Core.Mods;
using Serilog;

namespace Dispatch.App.ViewModels;

/// <summary>One active mod in the Multiplayer panel.</summary>
public sealed class MultiplayerItemViewModel(int position, MultiplayerEntry entry)
{
    public MultiplayerEntry Entry { get; } = entry;

    public string Name => $"{position}. {Entry.DisplayName}";

    public bool IsConvoyOptional => Entry.Convoy == ConvoyNeed.Optional;

    public string ConvoyLabel => IsConvoyOptional ? Strings.ConvoyOptional : Strings.ConvoyRequired;

    public string ConvoyDetail => Entry.ConvoyReason switch
    {
        ConvoyReason.MarkedOptional => Strings.ConvoyReasonOptional,
        ConvoyReason.MapAlwaysRequired => Strings.ConvoyReasonMap,
        ConvoyReason.NotInstalled => Strings.ConvoyReasonMissing,
        ConvoyReason.Unreadable => Strings.ConvoyReasonUnreadable,
        _ => Strings.ConvoyReasonRequired,
    };

    /// <summary>Shown under the name when it isn't simply "every player needs it".</summary>
    public bool HasConvoyNote => Entry.ConvoyReason != ConvoyReason.Required;

    public bool IsTruckersMpSupported => Entry.TruckersMp == TruckersMpSupport.Supported;
}

/// <summary>
/// v3: the Multiplayer panel. Convoy (the game's own multiplayer): which active mods everyone else needs too, from
/// the mods' manifests, with a list to send to friends. TruckersMP: a hint which active mods it supports at all.
/// Read-only; updated whenever the (unsaved) load order changes.
/// </summary>
public sealed partial class ProfilesViewModel
{
    public ObservableCollection<MultiplayerItemViewModel> Multiplayer { get; } = [];

    [ObservableProperty]
    private string _convoySummary = "";

    [ObservableProperty]
    private string _truckersMpSummary = "";

    private void RefreshMultiplayer()
    {
        var entries = MultiplayerCheck.Evaluate(LoadOrder.Select(o => (o.PackageId, o.DisplayName, o.Mod?.Mod)));
        Multiplayer.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            Multiplayer.Add(new MultiplayerItemViewModel(i + 1, entries[i]));
        }

        int optional = entries.Count(e => e.Convoy == ConvoyNeed.Optional);
        int supported = entries.Count(e => e.TruckersMp == TruckersMpSupport.Supported);
        ConvoySummary = entries.Count == 0
            ? Strings.MultiplayerNoMods
            : string.Format(CultureInfo.CurrentCulture, Strings.ConvoySummary, entries.Count - optional, optional);
        // "ProMods (10 files, on its ProMods servers)" rather than every part's name.
        var parts = new List<string>();
        int proMods = entries.Count(e => e.TruckersMpReason == TruckersMpReason.ProMods);
        int weather = entries.Count(e => e.TruckersMpReason == TruckersMpReason.SeasonalWeather);
        if (proMods > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.TruckersMpProModsPart, proMods));
        }

        if (weather > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.TruckersMpWeatherPart, weather));
        }

        TruckersMpSummary = entries.Count == 0
            ? Strings.TruckersMpNoMods
            : supported == 0
                ? Strings.TruckersMpNone
                : supported == entries.Count
                    ? Strings.TruckersMpAll
                    : string.Format(CultureInfo.CurrentCulture, Strings.TruckersMpSome, string.Join(", ", parts), entries.Count - supported);
        CopyConvoyListCommand.NotifyCanExecuteChanged();
    }

    private bool CanCopyConvoyList() => Multiplayer.Count > 0;

    /// <summary>The load order as text for the people you drive with: what they need, in which order, and where to get it.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyConvoyList))]
    private void CopyConvoyList()
    {
        var text = new StringBuilder();
        text.AppendLine(string.Format(CultureInfo.CurrentCulture, Strings.ConvoyListHeader, Selected?.Name ?? ""));
        foreach (var m in Multiplayer)
        {
            text.Append(m.Name).Append(" — ").Append(m.ConvoyLabel);
            if (m.Entry.WorkshopId is { } id)
            {
                text.Append(" — https://steamcommunity.com/sharedfiles/filedetails/?id=").Append(id.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                text.Append(" (").Append(m.Entry.PackageId).Append(')');
            }

            text.AppendLine();
        }

        try
        {
            System.Windows.Clipboard.SetText(text.ToString());
            Message = Strings.ConvoyListCopied;
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Warning(ex, "Couldn't copy the convoy list");
            Message = ex.Message;
        }
    }
}
