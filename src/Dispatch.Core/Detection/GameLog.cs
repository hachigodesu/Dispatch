using System.Text.RegularExpressions;
using Dispatch.Core.Mods;

namespace Dispatch.Core.Detection;

/// <summary>What the game itself wrote about the last launch (Documents\…\game.log.txt).</summary>
/// <param name="ModMessages">
/// The game's own warnings and errors about each mod, keyed by package ID (file name without extension). Only
/// messages the game writes while reading a mod's package (right after mounting it) are kept, so world-loading noise
/// isn't pinned on whichever mod happened to be mounted last.
/// </param>
public sealed record GameLogInfo(GameVersion? Version, IReadOnlySet<string> IncompatibleMods, DateTime LastWriteUtc,
                                 IReadOnlyDictionary<string, IReadOnlyList<ModIssue>> ModMessages)
{
    public IReadOnlyList<ModIssue> MessagesFor(string packageId) =>
        ModMessages.TryGetValue(packageId, out var list) ? list : [];
}

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

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return Parse(ReadLines(reader), File.GetLastWriteTimeUtc(path));
    }

    private static IEnumerable<string> ReadLines(TextReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    public static GameLogInfo Parse(IEnumerable<string> lines, DateTime lastWriteUtc)
    {
        GameVersion? version = null;
        var incompatible = new HashSet<string>(StringComparer.Ordinal);
        var messages = new Dictionary<string, List<ModIssue>>(StringComparer.Ordinal);
        string? current = null; // the mod mounted on the previous line(s), while its own messages follow

        void Add(string id, ModIssue issue)
        {
            if (!messages.TryGetValue(id, out var list))
            {
                messages[id] = list = [];
            }

            if (!list.Contains(issue))
            {
                list.Add(issue);
            }
        }

        foreach (string line in lines)
        {
            if (version is null && VersionRegex().Match(line) is { Success: true } v)
            {
                version = GameVersion.TryParse(v.Groups[1].Value);
                continue;
            }

            if (MountRegex().Match(line) is { Success: true } mount)
            {
                current = Path.GetFileNameWithoutExtension(mount.Groups[1].Value);
                continue;
            }

            if (IncompatibleRegex().Match(line) is { Success: true } m)
            {
                string id = m.Groups[1].Value.Trim();
                incompatible.Add(id);
                Add(id, new ModIssue(IssueSeverity.Warning, "Incompatible mod (the game won't load it with this game version)."));
                continue;
            }

            if (current is not null && ModMessageRegex().Match(line) is { Success: true } msg)
            {
                var severity = msg.Groups[1].Value == "ERROR" ? IssueSeverity.Error : IssueSeverity.Warning;
                Add(current, new ModIssue(severity, msg.Groups[2].Value.Replace("[mods] ", "", StringComparison.Ordinal)
                                                         .Replace("[unit] ", "", StringComparison.Ordinal).Trim()));
                continue;
            }

            // A "[zipfs] <full path>: <header bytes>" line (protected archives) keeps the attribution going; anything
            // else ends it.
            if (!line.Contains("[zipfs]", StringComparison.Ordinal) && !line.Contains("[hashfs]", StringComparison.Ordinal))
            {
                current = null;
            }
        }

        return new GameLogInfo(version, incompatible, lastWriteUtc,
                               messages.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ModIssue>)kv.Value, StringComparer.Ordinal));
    }

    [GeneratedRegex(@"(?:Loaded pack set version|init ver\.)\s*([\d.]+)")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\[mods\] Incompatible mod - (.+)$")]
    private static partial Regex IncompatibleRegex();

    /// <summary>"[zipfs] RPM [DEF].scs: Created, 1273 entries" (also hashfs). Names with '/' are full paths, not mounts.</summary>
    [GeneratedRegex(@"\[(?:zipfs|hashfs)\] ([^/\\:]+\.(?:scs|zip)): Created")]
    private static partial Regex MountRegex();

    /// <summary>The kinds of messages the game writes while reading a mod's manifest.</summary>
    [GeneratedRegex(@"<(WARNING|ERROR)> ((?:\[mods\]|\[unit\] Ignored unknown attribute).*)$")]
    private static partial Regex ModMessageRegex();
}
