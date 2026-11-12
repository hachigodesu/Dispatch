namespace Dispatch.Core.Games;

public enum GameId
{
    Ets2,
    Ats,
}

/// <summary>Everything that differs between the SCS games Dispatch supports.</summary>
/// <param name="Key">Stable lower-case ID used in settings and preset files ("ets2", "ats").</param>
public sealed record GameDefinition(
    GameId Id,
    string DisplayName,
    int SteamAppId,
    string InstallFolderName,
    string ExeRelativePath,
    string ProcessName,
    string DocumentsFolderName,
    string Key,
    string ShortName)
{
    public static readonly GameDefinition Ets2 = new(
        GameId.Ets2, "Euro Truck Simulator 2", 227300, "Euro Truck Simulator 2",
        @"bin\win_x64\eurotrucks2.exe", "eurotrucks2", "Euro Truck Simulator 2", "ets2", "ETS2");

    public static readonly GameDefinition Ats = new(
        GameId.Ats, "American Truck Simulator", 270880, "American Truck Simulator",
        @"bin\win_x64\amtrucks.exe", "amtrucks", "American Truck Simulator", "ats", "ATS");

    public static IReadOnlyList<GameDefinition> All { get; } = [Ets2, Ats];

    /// <summary>The game with this key; ETS2 for anything unknown (older settings files have no key).</summary>
    public static GameDefinition FromKey(string? key) =>
        All.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Ets2;
}
