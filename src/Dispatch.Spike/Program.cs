using System.Text.RegularExpressions;
using Dispatch.Core.Profiles;
using Dispatch.Core.Sii;
using Dispatch.Core.Sii.Text;
using Microsoft.Win32;

namespace Dispatch.Spike;

/// <summary>
/// Milestone 0 spike tool. The S1/S8 commands read the real game folders READ-ONLY (copies go to
/// ./spike-output, git-ignored). Only the in-game spike commands write, and only to "DispatchTest*" profiles.
/// </summary>
internal static partial class Program
{
    private static int Main(string[] args)
    {
        string command = args.Length > 0 ? args[0] : "help";
        string OutDir() => Path.GetFullPath(args.Length > 1 ? args[1] : "spike-output");
        return command switch
        {
            "profiles" => ProfilesSpike.Run(OutDir()),
            "mods" => ModsSpike.Run(OutDir()),
            "probe" => ProbeSpike.Run(args[1], args.Skip(2)),
            "s3-read" or "s2-write" or "s2-check" or "s4-duplicate" or "restore" or "roundtrip" or "decode"
                => InGameSpikes.Run(command, args[1..]),
            _ => Help(),
        };
    }

    private static int Help()
    {
        Console.WriteLine("""
            Dispatch.Spike <command> [args]
              profiles [outDir]           S1: copy every profile.sii and decode the copies (read-only)
              mods [outDir]               S8: scan mod packages (read-only)
              probe <archive> [path...]   HashFS diagnostics (read-only)
              roundtrip [outDir]          re-encode decoded profile copies, expect byte-identical (read-only)

            In-game spikes — only for local profiles named DispatchTest…; refuse while the game runs:
              s3-read <name>              print active_mods in file order (read-only)
              s2-write <name>             back up, then write plain-text profile.sii with the order reversed
              s2-check <name>             show format/order of profile.sii and profile.bak.sii (read-only)
              s4-duplicate <name> <new>   copy the profile to a new name (never overwrites)
              restore <name>              restore the latest Dispatch backup of the profile
            """);
        return 1;
    }
}

internal static class GamePaths
{
    public static string UserDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Euro Truck Simulator 2");

    public static string? SteamDir
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") is string p ? Path.GetFullPath(p) : null;
        }
    }

    /// <summary>All (kind, folder) pairs that hold a profile.sii: local profiles + Steam Cloud remote folders.</summary>
    public static IEnumerable<(string Kind, string Dir)> ProfileDirs()
    {
        string local = Path.Combine(UserDataDir, "profiles");
        if (Directory.Exists(local))
        {
            foreach (var d in Directory.EnumerateDirectories(local))
            {
                yield return ("local", d);
            }
        }

        if (SteamDir is { } steam && Directory.Exists(Path.Combine(steam, "userdata")))
        {
            foreach (var account in Directory.EnumerateDirectories(Path.Combine(steam, "userdata")))
            {
                string remote = Path.Combine(account, "227300", "remote", "profiles");
                if (!Directory.Exists(remote))
                {
                    continue;
                }

                foreach (var d in Directory.EnumerateDirectories(remote))
                {
                    yield return ($"cloud-{Path.GetFileName(account)}", d);
                }
            }
        }
    }
}

internal static partial class ProfilesSpike
{
    public static int Run(string outDir)
    {
        Console.WriteLine($"S1 profile decode — output: {outDir}");
        int ok = 0, failed = 0;
        foreach (var (kind, dir) in GamePaths.ProfileDirs())
        {
            string folder = Path.GetFileName(dir);
            string source = Path.Combine(dir, "profile.sii");
            if (!File.Exists(source))
            {
                Console.WriteLine($"  [{kind}] {folder}: no profile.sii");
                continue;
            }

            string workDir = Path.Combine(outDir, "profiles", $"{kind}_{folder}");
            Directory.CreateDirectory(workDir);
            string copy = Path.Combine(workDir, "profile.sii");
            File.Copy(source, copy, overwrite: true); // read-only use of the original

            bool nameOk = ProfileNameCodec.TryFromFolderName(folder, out string folderName);
            try
            {
                var result = SiiFileCodec.Decode(File.ReadAllBytes(copy));
                File.WriteAllText(Path.Combine(workDir, "profile.decoded.sii"), result.Text);

                string profileName = Match(ProfileNameRegex(), result.Text) is { } raw ? SiiString.Unquote(raw) : "?";
                int declaredMods = int.TryParse(Match(ActiveModsCountRegex(), result.Text), out int n) ? n : -1;
                var mods = ActiveModRegex().Matches(result.Text).Select(m => m.Groups[1].Value).ToList();
                int workshop = mods.Count(m => m.StartsWith("\"mod_workshop_package.", StringComparison.Ordinal));
                var fields = UserProfileFieldNames(result.Text);

                Console.WriteLine($"  [{kind}] {folder}");
                Console.WriteLine($"      layers: {string.Join(" > ", result.Layers)}" +
                                  (result.Binary is { } b
                                      ? $"  (BSII v{b.Version}, {b.StructureCount} structures, {b.UnitCount} units, trailing bytes {b.TrailingBytes}, types {string.Join(",", b.ValueTypesSeen.Order().Select(t => $"0x{t:X2}"))})"
                                      : ""));
                Console.WriteLine($"      profile_name matches folder: {(nameOk && folderName == profileName ? "yes" : "NO")}");
                Console.WriteLine($"      active_mods: declared {declaredMods}, entries {mods.Count}, workshop {workshop}");
                foreach (var m in mods.Take(3))
                {
                    Console.WriteLine($"        {m}");
                }

                Console.WriteLine($"      user_profile fields: {string.Join(", ", fields)}");
                ok++;
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                Console.WriteLine($"  [{kind}] {folder}: FAILED — {ex.Message}");
                failed++;
            }
        }

        Console.WriteLine($"Done: {ok} decoded, {failed} failed.");
        int saveFailures = DecodeSaves(outDir);
        return failed == 0 && saveFailures == 0 ? 0 : 2;
    }

    /// <summary>Exercises the BSII decoder on real data: info.sii of every save, plus the newest game.sii.</summary>
    private static int DecodeSaves(string outDir)
    {
        Console.WriteLine("Save files (BSII decoder on real data):");
        var files = new List<string>();
        foreach (var (_, dir) in GamePaths.ProfileDirs())
        {
            string saveDir = Path.Combine(dir, "save");
            if (Directory.Exists(saveDir))
            {
                files.AddRange(Directory.EnumerateFiles(saveDir, "info.sii", SearchOption.AllDirectories));
            }
        }

        var newestGame = files.Select(f => Path.Combine(Path.GetDirectoryName(f)!, "game.sii"))
                              .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (newestGame is not null)
        {
            files.Add(newestGame);
        }

        string work = Path.Combine(outDir, "saves");
        Directory.CreateDirectory(work);
        var layerCounts = new Dictionary<string, int>();
        var types = new SortedSet<uint>();
        int failures = 0, index = 0;
        foreach (var source in files)
        {
            string copy = Path.Combine(work, $"{index++:D4}_{Path.GetFileName(source)}");
            File.Copy(source, copy, overwrite: true);
            try
            {
                var result = SiiFileCodec.Decode(File.ReadAllBytes(copy));
                string key = string.Join(" > ", result.Layers) + (result.Binary is { } b ? $" v{b.Version}" : "");
                layerCounts[key] = layerCounts.GetValueOrDefault(key) + 1;
                if (result.Binary is { } bin)
                {
                    types.UnionWith(bin.ValueTypesSeen);
                    if (bin.TrailingBytes != 0)
                    {
                        Console.WriteLine($"  {Path.GetFileName(copy)}: {bin.TrailingBytes} trailing bytes");
                    }
                }

                if (source == newestGame)
                {
                    File.WriteAllText(copy + ".decoded.sii", result.Text);
                    Console.WriteLine($"  newest game.sii: {new FileInfo(copy).Length:N0} bytes → {result.Text.Length:N0} chars, {key}, {result.Binary?.UnitCount} units");
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                Console.WriteLine($"  {Path.GetFileName(copy)}: FAILED — {ex.Message}");
                failures++;
            }
        }

        foreach (var (key, count) in layerCounts)
        {
            Console.WriteLine($"  {count,4} × {key}");
        }

        Console.WriteLine($"  BSII value types seen: {string.Join(",", types.Select(t => $"0x{t:X2}"))}");
        Console.WriteLine($"  {files.Count - failures}/{files.Count} save files decoded.");
        return failures;
    }

    private static string? Match(Regex regex, string text) => regex.Match(text) is { Success: true } m ? m.Groups[1].Value : null;

    private static List<string> UserProfileFieldNames(string text)
    {
        int start = text.IndexOf("user_profile :", StringComparison.Ordinal);
        if (start < 0)
        {
            return [];
        }

        int end = text.IndexOf("\n}", start, StringComparison.Ordinal);
        return FieldNameRegex().Matches(text[start..end]).Select(m => m.Groups[1].Value).Distinct().ToList();
    }

    [GeneratedRegex(@"^\s*profile_name:\s*(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex ProfileNameRegex();

    [GeneratedRegex(@"^\s*active_mods:\s*(\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ActiveModsCountRegex();

    [GeneratedRegex(@"^\s*active_mods\[\d*\]:\s*(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex ActiveModRegex();

    [GeneratedRegex(@"^\s([a-z0-9_]+)(?:\[\d*\])?:", RegexOptions.Multiline)]
    private static partial Regex FieldNameRegex();
}
