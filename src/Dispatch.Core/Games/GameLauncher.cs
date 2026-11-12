using System.Diagnostics;
using System.Globalization;

namespace Dispatch.Core.Games;

/// <summary>How to start an installation of the game.</summary>
public static class GameLauncher
{
    /// <summary>The Steam install starts through Steam, so Steam's launch options and overlay apply.</summary>
    public static ProcessStartInfo ThroughSteam(GameDefinition game) =>
        new("steam://rungameid/" + game.SteamAppId.ToString(CultureInfo.InvariantCulture)) { UseShellExecute = true };

    /// <summary>
    /// A copy starts directly from its own folder. The game doesn't insist on being started by Steam, but it does talk
    /// to the running Steam client (ownership, DLC, Workshop): <c>SteamAppId</c> tells the Steam API which game this is,
    /// the same thing Steam sets when it launches a game. Steam must be running.
    /// </summary>
    public static ProcessStartInfo Directly(GameDefinition game, GameInstallation copy)
    {
        string exe = copy.ExePath(game);
        string id = game.SteamAppId.ToString(CultureInfo.InvariantCulture);
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        start.Environment["SteamAppId"] = id;
        start.Environment["SteamGameId"] = id;
        return start;
    }
}
