using System.Text.RegularExpressions;

namespace Dispatch.Core.Detection;

/// <summary>What the game itself wrote about the last launch (Documents\…\game.log.txt).</summary>
public sealed record GameLogInfo(GameVersion? Version, IReadOnlySet<string> IncompatibleMods, DateTime LastWriteUtc);

public static partial class GameLog
{
    /// <summary>Reads game.log.txt without locking it (the game may be writing it). Null if it doesn't exist.</summary>
    public static GameLogInfo? Read(string userDataDir)
    {
        string path = Path.Combine(userDataDir, "game.log.txt");
        if (!File.Exists(path))
        {
            return null;
        }

        GameVersion? version = null;
        var incompatible = new HashSet<string>(StringComparer.Ordinal);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (version is null && VersionRegex().Match(line) is { Success: true } v)
            {
                version = GameVersion.TryParse(v.Groups[1].Value);
            }
            else if (IncompatibleRegex().Match(line) is { Success: true } m)
            {
                incompatible.Add(m.Groups[1].Value.Trim());
            }
        }

        return new GameLogInfo(version, incompatible, File.GetLastWriteTimeUtc(path));
    }

    [GeneratedRegex(@"(?:Loaded pack set version|init ver\.)\s*([\d.]+)")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\[mods\] Incompatible mod - (.+)$")]
    private static partial Regex IncompatibleRegex();
}
