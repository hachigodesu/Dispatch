using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dispatch.Core.Mods;
using Dispatch.Core.Mods.Packages;

namespace Dispatch.Spike;

/// <summary>
/// S8: scan every local and Workshop mod read-only, parse manifests, and compare our compatibility
/// verdicts with the game's own "[mods] Incompatible mod" log lines.
/// </summary>
internal static partial class ModsSpike
{
    private sealed record Row(string PackageId, string Source, string Format, long Size, string Status,
                              ModManifest? Manifest, string? Icon, bool HasDescription, string? SubPackage,
                              double Ms, string? Error, string Verdict);

    private static readonly string[] SubPackageExtensions = ["", ".zip", ".scs"];

    public static int Run(string outDir)
    {
        string? gameVersion = FindGameVersion(out string? exePath);
        Console.WriteLine($"S8 mod scan — game version {gameVersion ?? "?"} ({exePath ?? "exe not found"})");
        var rows = new List<Row>();
        var total = Stopwatch.StartNew();

        string modDir = Path.Combine(GamePaths.UserDataDir, "mod");
        foreach (var entry in Directory.EnumerateFileSystemEntries(modDir).Order(StringComparer.OrdinalIgnoreCase))
        {
            bool isDir = Directory.Exists(entry);
            string ext = Path.GetExtension(entry).ToLowerInvariant();
            if (!isDir && ext is not (".scs" or ".zip"))
            {
                continue;
            }

            string id = isDir ? Path.GetFileName(entry) : Path.GetFileNameWithoutExtension(entry);
            rows.Add(Scan(id, "local", entry, entry, null, gameVersion));
        }

        foreach (var (workshopId, root) in WorkshopItems())
        {
            string id = $"mod_workshop_package.{workshopId:X16}";
            string versionsPath = Path.Combine(root, "versions.sii");
            string target = root;
            string? sub = null;
            if (File.Exists(versionsPath))
            {
                var entries = VersionsEntry.Parse(File.ReadAllText(versionsPath));
                var chosen = VersionsEntry.Select(entries, gameVersion ?? "");
                sub = chosen is null ? "(none)" : $"{chosen.PackageName} of {entries.Count}";
                if (chosen is not null)
                {
                    target = SubPackageExtensions.Select(e => Path.Combine(root, chosen.PackageName + e))
                        .FirstOrDefault(p => Directory.Exists(p) || File.Exists(p)) ?? root;
                }
            }

            rows.Add(Scan(id, "workshop", root, target, sub, gameVersion));
        }

        total.Stop();
        CompareWithGameLog(rows);
        WriteReport(outDir, rows, gameVersion, total.Elapsed);
        PrintSummary(rows, total.Elapsed);
        return rows.Any(r => r.Status == "error") ? 2 : 0;
    }

    private static Row Scan(string id, string source, string displayPath, string packagePath, string? sub, string? gameVersion)
    {
        var sw = Stopwatch.StartNew();
        long size = File.Exists(displayPath) ? new FileInfo(displayPath).Length : DirectorySize(displayPath);
        try
        {
            using var pkg = ModPackage.Open(packagePath);
            if (pkg.Format == PackageFormat.Opaque)
            {
                return new Row(id, source, $"Opaque ({((OpaquePackage)pkg).Reason})", size, "opaque", null, null, false, sub,
                               sw.Elapsed.TotalMilliseconds, null, "unknown");
            }

            byte[]? manifestBytes = pkg.TryReadFile("manifest.sii");
            if (manifestBytes is null)
            {
                return new Row(id, source, pkg.Format.ToString(), size, "no manifest", null, null, false, sub,
                               sw.Elapsed.TotalMilliseconds, null, "unknown");
            }

            var manifest = ModManifest.Parse(manifestBytes);
            string? icon = null;
            if (manifest.IconFile is { } iconFile && pkg.TryReadFile(iconFile) is { } iconBytes)
            {
                icon = ImageInfo.TryGetJpegSize(iconBytes) is { } dim ? $"{dim.Width}x{dim.Height}" : "not jpeg";
            }
            else if (manifest.IconFile is not null)
            {
                icon = "missing";
            }

            bool hasDescription = manifest.DescriptionFile is { } d && pkg.TryReadFile(d) is not null;
            string verdict = source == "workshop" || manifest.CompatibleVersions.Count == 0 || gameVersion is null
                ? "unknown"
                : manifest.CompatibleVersions.Any(p => GameVersionMatch.Matches(p, gameVersion)) ? "ok" : "incompatible";
            return new Row(id, source, pkg.Format.ToString(), size, "ok", manifest, icon, hasDescription, sub,
                           sw.Elapsed.TotalMilliseconds, null, verdict);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return new Row(id, source, "?", size, "error", null, null, false, sub, sw.Elapsed.TotalMilliseconds,
                           $"{ex.GetType().Name}: {ex.Message}", "unknown");
        }
    }

    private static readonly Dictionary<string, string> GameVerdicts = new(StringComparer.Ordinal);

    private static void CompareWithGameLog(List<Row> rows)
    {
        string log = Path.Combine(GamePaths.UserDataDir, "game.log.txt");
        if (!File.Exists(log))
        {
            return;
        }

        string tempCopy = Path.GetTempFileName();
        File.Copy(log, tempCopy, overwrite: true);
        foreach (var line in File.ReadLines(tempCopy))
        {
            var m = IncompatibleRegex().Match(line);
            if (m.Success)
            {
                GameVerdicts[m.Groups[1].Value.Trim()] = "incompatible";
            }
        }

        File.Delete(tempCopy);
        _ = rows;
    }

    private static void PrintSummary(List<Row> rows, TimeSpan elapsed)
    {
        Console.WriteLine($"Scanned {rows.Count} mods in {elapsed.TotalSeconds:F1} s");
        foreach (var g in rows.GroupBy(r => (r.Source, r.Format)).OrderBy(g => g.Key.Source).ThenBy(g => g.Key.Format))
        {
            Console.WriteLine($"  {g.Key.Source,-9} {g.Key.Format,-40} {g.Count(),4}  manifest ok {g.Count(r => r.Status == "ok")}");
        }

        foreach (var g in rows.GroupBy(r => r.Status))
        {
            Console.WriteLine($"  status {g.Key,-12} {g.Count()}");
        }

        foreach (var r in rows.Where(r => r.Status == "error"))
        {
            Console.WriteLine($"  ERROR {r.PackageId}: {r.Error}");
        }

        var local = rows.Where(r => r.Source == "local").ToList();
        int agree = 0, weSayIncompatGameOk = 0, gameSaysIncompatWeDont = 0;
        var disagreements = new List<string>();
        foreach (var r in local)
        {
            bool game = GameVerdicts.ContainsKey(r.PackageId);
            bool ours = r.Verdict == "incompatible";
            if (game == ours)
            {
                agree++;
            }
            else if (ours)
            {
                weSayIncompatGameOk++;
                disagreements.Add($"{r.PackageId}: we=incompatible game=ok  versions=[{string.Join(", ", r.Manifest?.CompatibleVersions ?? [])}]");
            }
            else
            {
                gameSaysIncompatWeDont++;
                disagreements.Add($"{r.PackageId}: we={r.Verdict} game=incompatible  status={r.Status} versions=[{string.Join(", ", r.Manifest?.CompatibleVersions ?? [])}] unknown=[{string.Join(", ", r.Manifest?.UnknownAttributes ?? [])}]");
            }
        }

        Console.WriteLine($"Compatibility vs game.log: {agree}/{local.Count} agree; we-only {weSayIncompatGameOk}, game-only {gameSaysIncompatWeDont}");
        foreach (var d in disagreements.Take(25))
        {
            Console.WriteLine($"  {d}");
        }

        var times = rows.Select(r => r.Ms).Order().ToList();
        Console.WriteLine($"Per-mod time: median {times[times.Count / 2]:F1} ms, max {times[^1]:F0} ms ({rows.MaxBy(r => r.Ms)!.PackageId})");
    }

    private static void WriteReport(string outDir, List<Row> rows, string? gameVersion, TimeSpan elapsed)
    {
        Directory.CreateDirectory(outDir);
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# S8 mod scan — game {gameVersion}, {rows.Count} mods, {elapsed.TotalSeconds:F1} s");
        sb.AppendLine();
        sb.AppendLine("| Package ID | Src | Format | Size MB | Status | Display name | Version | Categories | compatible_versions | DLC deps | Icon | Desc | Sub-package | Ours | Game | Unknown attrs | ms |");
        sb.AppendLine("|---|---|---|---:|---|---|---|---|---|---|---|---|---|---|---|---|---:|");
        foreach (var r in rows)
        {
            var m = r.Manifest;
            string game = r.Source == "local" ? (GameVerdicts.ContainsKey(r.PackageId) ? "incompatible" : "ok/unknown") : "";
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {Esc(r.PackageId)} | {r.Source} | {Esc(r.Format)} | {r.Size / 1048576.0:F1} | {r.Status}{(r.Error is null ? "" : ": " + Esc(r.Error))} | {Esc(m?.DisplayName)} | {Esc(m?.PackageVersion)} | {Esc(string.Join(", ", m?.Categories ?? []))} | {Esc(string.Join(", ", m?.CompatibleVersions ?? []))} | {Esc(string.Join(", ", m?.DlcDependencies ?? []))} | {r.Icon} | {(r.HasDescription ? "yes" : "")} | {Esc(r.SubPackage)} | {r.Verdict} | {game} | {Esc(string.Join(", ", m?.UnknownAttributes ?? []))} | {r.Ms:F0} |");
        }

        string path = Path.Combine(outDir, "mods-report.md");
        File.WriteAllText(path, sb.ToString());
        Console.WriteLine($"Report: {path}");
    }

    private static string Esc(string? s) => (s ?? "").Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");

    private static long DirectorySize(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;

    private static IEnumerable<(ulong Id, string Root)> WorkshopItems()
    {
        foreach (var lib in SteamLibraries())
        {
            string ws = Path.Combine(lib, "steamapps", "workshop", "content", "227300");
            if (!Directory.Exists(ws))
            {
                continue;
            }

            foreach (var d in Directory.EnumerateDirectories(ws))
            {
                if (ulong.TryParse(Path.GetFileName(d), out ulong id))
                {
                    yield return (id, d);
                }
            }
        }
    }

    internal static IEnumerable<string> SteamLibraries()
    {
        if (GamePaths.SteamDir is not { } steam)
        {
            yield break;
        }

        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steam };
        yield return steam;
        if (!File.Exists(vdf))
        {
            yield break;
        }

        foreach (Match m in VdfPathRegex().Matches(File.ReadAllText(vdf)))
        {
            string p = Path.GetFullPath(m.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal));
            if (seen.Add(p))
            {
                yield return p;
            }
        }
    }

    private static string? FindGameVersion(out string? exePath)
    {
        exePath = SteamLibraries()
            .Select(l => Path.Combine(l, "steamapps", "common", "Euro Truck Simulator 2", "bin", "win_x64", "eurotrucks2.exe"))
            .FirstOrDefault(File.Exists);
        string? v = exePath is null ? null : FileVersionInfo.GetVersionInfo(exePath).ProductVersion;
        return v?.Split(' ')[0];
    }

    [GeneratedRegex(@"\[mods\] Incompatible mod - (.+)$")]
    private static partial Regex IncompatibleRegex();

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfPathRegex();
}
