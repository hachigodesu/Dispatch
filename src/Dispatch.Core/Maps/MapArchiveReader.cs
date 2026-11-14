using System.Text;
using System.Text.RegularExpressions;
using Dispatch.Core.Games;
using Dispatch.Core.Mods.Packages;
using Dispatch.Core.Sii;

namespace Dispatch.Core.Maps;

/// <summary>Reads the map sectors and city names an archive contains. Read-only.</summary>
public static partial class MapArchiveReader
{
    /// <summary>The map every mod extends: ETS2 "europe", ATS "usa".</summary>
    public static string MapName(GameId game) => game == GameId.Ats ? "usa" : "europe";

    /// <summary>
    /// The game's own archives that carry map sectors or city definitions, bottom first: base, def, base_map, then the
    /// DLCs (only owned DLCs are installed). Missing files are left out.
    /// </summary>
    public static IReadOnlyList<string> GameArchives(string installDir)
    {
        var list = new List<string>();
        foreach (string name in new[] { "base.scs", "def.scs", "base_map.scs" })
        {
            string path = Path.Combine(installDir, name);
            if (File.Exists(path))
            {
                list.Add(path);
            }
        }

        if (Directory.Exists(installDir))
        {
            list.AddRange(Directory.GetFiles(installDir, "dlc_*.scs").Order(StringComparer.OrdinalIgnoreCase));
        }

        return list;
    }

    /// <summary>
    /// Which map a mod belongs to: the game's own (<paramref name="defaultName"/>) when it has sectors for it, otherwise
    /// the map folder with the most sectors: a separate map such as Grand Utopia (map/grandutopia.mbd), which a profile
    /// plays instead of the game's map. Protected or map-less packages give the default.
    /// </summary>
    public static string FindMapName(string path, string defaultName)
    {
        try
        {
            using var pkg = ModPackage.Open(path);
            var counts = List(pkg, "map", dirs: true)
                .Select(d => (Name: d.TrimEnd('/'), Sectors: List(pkg, "map/" + d.TrimEnd('/')).Count(f => f.EndsWith(".base", StringComparison.OrdinalIgnoreCase))))
                .Where(d => d.Sectors > 0)
                .ToList();
            return counts.Count == 0 || counts.Any(d => string.Equals(d.Name, defaultName, StringComparison.OrdinalIgnoreCase))
                ? defaultName
                : counts.MaxBy(d => d.Sectors).Name;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return defaultName;
        }
    }

    /// <summary>Opens and reads one archive (file or folder). A protected or broken package gives an unreadable result.</summary>
    public static ArchiveMap Read(string path, string mapName, CancellationToken cancel = default)
    {
        IModPackage pkg;
        try
        {
            pkg = ModPackage.Open(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new ArchiveMap { IsUnreadable = true };
        }

        using (pkg)
        {
            return pkg.Format == PackageFormat.Opaque ? new ArchiveMap { IsUnreadable = true } : Read(pkg, mapName, cancel);
        }
    }

    public static ArchiveMap Read(IModPackage pkg, string mapName, CancellationToken cancel = default)
    {
        var result = new ArchiveMap();
        string dir = "map/" + mapName;
        foreach (string file in List(pkg, dir).Where(f => f.EndsWith(".base", StringComparison.OrdinalIgnoreCase)))
        {
            cancel.ThrowIfCancellationRequested();
            string name = file[..^".base".Length];
            try
            {
                if (pkg.TryReadFile(dir + "/" + file) is { } data && SectorReader.Read(name, data) is { } sector)
                {
                    result.Sectors[name] = sector;
                    continue;
                }
            }
            catch (InvalidDataException)
            {
                // counted below
            }

            result.UnreadableSectors++;
        }

        ReadCityNames(pkg, result.CityNames);
        return result;
    }

    /// <summary>
    /// City display names from <c>def/city/*.sui</c> and <c>def/city*.sii</c>:
    /// <c>city_data : city.&lt;token&gt; { city_name: "…" }</c>.
    /// </summary>
    private static void ReadCityNames(IModPackage pkg, Dictionary<string, string> names)
    {
        var files = List(pkg, "def/city").Where(f => f.EndsWith(".sui", StringComparison.OrdinalIgnoreCase)).Select(f => "def/city/" + f)
            .Concat(List(pkg, "def").Where(f => f.StartsWith("city", StringComparison.OrdinalIgnoreCase) &&
                                                f.EndsWith(".sii", StringComparison.OrdinalIgnoreCase)).Select(f => "def/" + f));
        foreach (string file in files)
        {
            string? text = ReadText(pkg, file);
            if (text is null)
            {
                continue;
            }

            foreach (Match m in CityUnit().Matches(text))
            {
                string name = m.Groups["name"].Value.Trim();
                if (name.Length > 0 && !name.StartsWith("@@", StringComparison.Ordinal))
                {
                    names[m.Groups["token"].Value] = name;
                }
            }
        }
    }

    private static string? ReadText(IModPackage pkg, string file)
    {
        try
        {
            if (pkg.TryReadFile(file) is not { } data)
            {
                return null;
            }

            try
            {
                return SiiFileCodec.Decode(data).Text;
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                return Encoding.UTF8.GetString(data); // .sui fragments have no SiiNunit header
            }
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>File names in a directory (or, with <paramref name="dirs"/>, subdirectory names ending in '/').</summary>
    private static List<string> List(IModPackage pkg, string dir, bool dirs = false)
    {
        try
        {
            return pkg.ListDirectory(dir)?.Where(n => n.EndsWith('/') == dirs).ToList() ?? [];
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    [GeneratedRegex("""city_data\s*:\s*city\.(?<token>[0-9a-z_]+)\s*\{[^}]*?\bcity_name\s*:\s*"(?<name>[^"]*)"[^}]*\}""", RegexOptions.CultureInvariant)]
    private static partial Regex CityUnit();
}
