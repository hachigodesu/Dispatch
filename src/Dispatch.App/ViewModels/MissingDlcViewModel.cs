using System.Globalization;
using Dispatch.App.Resources;

namespace Dispatch.App.ViewModels;

/// <summary>One missing DLC and the active mods that need it.</summary>
public sealed record MissingDlcRow(string Dlc, string Mods);

/// <summary>
/// Shown before launching when active mods of the profile need DLC the player doesn't own: the game won't activate
/// those mods, which can break a map combo. "Launch anyway" or "Cancel".
/// </summary>
public sealed class MissingDlcViewModel(string profileName, IReadOnlyList<MissingDlcRow> rows)
{
    public string Header => string.Format(CultureInfo.CurrentCulture, Strings.MissingDlcHeader, profileName);

    public IReadOnlyList<MissingDlcRow> Rows { get; } = rows;
}
