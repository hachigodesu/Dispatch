namespace Dispatch.Core.Games;

public enum GameId
{
    Ets2,
    Ats,
}

/// <summary>Everything that differs between the SCS games Dispatch supports. Only ETS2 is enabled for now.</summary>
public sealed record GameDefinition(
    GameId Id,
    string DisplayName,
    int SteamAppId,
    string InstallFolderName,
    string ExeRelativePath,
    string ProcessName,
    string DocumentsFolderName)
{
    public static readonly GameDefinition Ets2 = new(
        GameId.Ets2, "Euro Truck Simulator 2", 227300, "Euro Truck Simulator 2",
        @"bin\win_x64\eurotrucks2.exe", "eurotrucks2", "Euro Truck Simulator 2");

    /// <summary>Prepared for later (PLAN.md: leave room for ATS). Not yet exposed in the UI.</summary>
    public static readonly GameDefinition Ats = new(
        GameId.Ats, "American Truck Simulator", 270880, "American Truck Simulator",
        @"bin\win_x64\amtrucks.exe", "amtrucks", "American Truck Simulator");
}
