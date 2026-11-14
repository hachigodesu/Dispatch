using System.Text;
using System.Text.RegularExpressions;
using Dispatch.Core.Mods.Packages;
using Dispatch.Core.Sii;

namespace Dispatch.Core.Profiles;

/// <summary>
/// Display names the game shows for cargo and companies, in one language. Cargo definitions
/// (<c>def/cargo/&lt;cargo&gt;.sui</c>) name a translation key ("@@cn_apples@@") that the language's files
/// (<c>locale/&lt;lang&gt;/*.sui|*.sii</c>, key[]/val[] pairs) translate; companies
/// (<c>def/company/&lt;company&gt;.sui</c>) carry their name. Read-only; later archives (mods) win.
/// </summary>
public sealed partial class GameNames
{
    private static readonly string[] BaseArchives = ["def.scs", "locale.scs"];
    private readonly Dictionary<string, string> _cargo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _companies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _translations = new(StringComparer.Ordinal);

    public static GameNames Empty { get; } = new();

    /// <summary>The game's locale folder for a UI culture ("de" → "de_de", "pt-BR" → "pt_br"); English is "en_us".</summary>
    public static string LocaleFolder(string cultureName) => cultureName.ToLowerInvariant() switch
    {
        var c when c.StartsWith("de", StringComparison.Ordinal) => "de_de",
        var c when c.StartsWith("es", StringComparison.Ordinal) => "es_es",
        var c when c.StartsWith("fr", StringComparison.Ordinal) => "fr_fr",
        var c when c.StartsWith("pl", StringComparison.Ordinal) => "pl_pl",
        var c when c.StartsWith("pt", StringComparison.Ordinal) => "pt_br",
        var c when c.StartsWith("ru", StringComparison.Ordinal) => "ru_ru",
        _ => "en_us",
    };

    /// <summary>The game's archives with definitions and translations: def, locale, then the DLCs.</summary>
    public static IReadOnlyList<string> GameArchives(string installDir)
    {
        var list = BaseArchives.Select(n => Path.Combine(installDir, n)).Where(File.Exists).ToList();
        if (Directory.Exists(installDir))
        {
            list.AddRange(Directory.GetFiles(installDir, "dlc_*.scs").Order(StringComparer.OrdinalIgnoreCase));
        }

        return list;
    }

    /// <summary>Reads the names from these archives (game first, then mods bottom first) in one language.</summary>
    public static GameNames Read(IEnumerable<string> archives, string localeFolder, CancellationToken cancel = default)
    {
        var names = new GameNames();
        foreach (string archive in archives)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                using var pkg = ModPackage.Open(archive);
                if (pkg.Format != PackageFormat.Opaque)
                {
                    names.Add(pkg, localeFolder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // A protected or broken archive simply adds no names.
            }
        }

        return names;
    }

    /// <summary>The cargo's name ("Fish Fingers"), or null when no definition was found.</summary>
    public string? Cargo(string token) => _cargo.TryGetValue(token, out var name) ? Translate(name) : null;

    public string? Company(string token) => _companies.TryGetValue(token, out var name) ? Translate(name) : null;

    /// <summary>"@@cn_apples@@" → the translation (or the key when it has none); other text is returned as it is.</summary>
    public string Translate(string text)
    {
        var m = LocalizedKey().Match(text);
        return m.Success ? _translations.GetValueOrDefault(m.Groups[1].Value) ?? m.Groups[1].Value : text;
    }

    internal void Add(IModPackage pkg, string localeFolder)
    {
        foreach (var (dir, regex, target) in new[] { ("def/cargo", CargoUnit(), _cargo), ("def/company", CompanyUnit(), _companies) })
        {
            foreach (string file in Files(pkg, dir))
            {
                if (Text(pkg, dir + "/" + file) is { } text)
                {
                    foreach (Match m in regex.Matches(text))
                    {
                        target[m.Groups["token"].Value] = m.Groups["name"].Value;
                    }
                }
            }
        }

        string locale = "locale/" + localeFolder;
        foreach (string file in Files(pkg, locale))
        {
            if (Text(pkg, locale + "/" + file) is { } text)
            {
                foreach (Match m in Translation().Matches(text))
                {
                    _translations[m.Groups["key"].Value] = m.Groups["val"].Value;
                }
            }
        }
    }

    private static List<string> Files(IModPackage pkg, string dir)
    {
        try
        {
            return (pkg.ListDirectory(dir) ?? []).Where(f => !f.EndsWith('/') &&
                (f.EndsWith(".sui", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".sii", StringComparison.OrdinalIgnoreCase))).ToList();
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    private static string? Text(IModPackage pkg, string path)
    {
        try
        {
            if (pkg.TryReadFile(path) is not { } data)
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

    [GeneratedRegex("""cargo_data\s*:\s*cargo\.(?<token>[0-9a-z_]+)\s*\{[^}]*?\bname\s*:\s*"(?<name>[^"]*)"[^}]*\}""", RegexOptions.CultureInvariant)]
    private static partial Regex CargoUnit();

    [GeneratedRegex("""company_permanent\s*:\s*company\.permanent\.(?<token>[0-9a-z_]+)\s*\{[^}]*?\bname\s*:\s*"(?<name>[^"]*)"[^}]*\}""", RegexOptions.CultureInvariant)]
    private static partial Regex CompanyUnit();

    [GeneratedRegex("""key\[\]\s*:\s*"(?<key>[^"]*)"\s*val\[\]\s*:\s*"(?<val>(?:[^"\\]|\\.)*)"(?=\s)""", RegexOptions.CultureInvariant)]
    private static partial Regex Translation();

    [GeneratedRegex(@"^@@([^@]+)@@$")]
    private static partial Regex LocalizedKey();
}
