using Dispatch.Core.Games;

namespace Dispatch.App.Services;

/// <summary>
/// The DLCs of the game being shown (set after every refresh), so any view can say "Nordic Horizons" instead of
/// "dlc_polar" and tell owned DLCs from missing ones.
/// </summary>
public static class DlcNames
{
    public static DlcCatalog Catalog { get; set; } = DlcCatalog.Empty(GameId.Ets2);

    public static string Name(string id) => Catalog.NameOf(id);

    /// <summary>"Iberia, Greece" for a list of IDs.</summary>
    public static string List(IEnumerable<string> ids) => string.Join(", ", ids.Select(Name));
}
