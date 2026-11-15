using System.Globalization;
using System.Text.RegularExpressions;
using Dispatch.Core.Mods.Packages;
using Dispatch.Core.Sii;

namespace Dispatch.Core.Games;

/// <summary>One DLC: its ID as mods name it ("dlc_polar"), its name ("Nordic Horizons") and what it adds.</summary>
/// <param name="Features">The manifest's <c>features[]</c> ("map", "cargo", "bus"…); empty for paint jobs and parts.</param>
public sealed record DlcInfo(string Id, string Name, IReadOnlyList<string> Features, uint? SteamAppId)
{
    public bool AddsMap => Features.Contains("map", StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The DLCs the player owns (each installed <c>dlc_*.scs</c>, named from its <c>dlc_*.manifest.sii</c>) and names for
/// DLCs they don't own, which mods may still ask for. Steam installs only owned DLCs, so an archive in the game folder
/// means the DLC is owned.
/// </summary>
public sealed partial class DlcCatalog
{
    private readonly Dictionary<string, DlcInfo> _owned;
    private readonly GameId _game;

    public DlcCatalog(IEnumerable<DlcInfo> owned, GameId game)
    {
        _owned = owned.GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _game = game;
    }

    public static DlcCatalog Empty(GameId game) => new([], game);

    /// <summary>Owned DLCs, sorted by name.</summary>
    public IReadOnlyList<DlcInfo> Owned => _owned.Values.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public bool Owns(string id) => _owned.ContainsKey(id);

    /// <summary>
    /// A map expansion (Iberia, Nordic Horizons, Texas…), not a cargo or trailer pack that also adds a few map items:
    /// a known map DLC, an ATS state, or one whose only feature is "map".
    /// </summary>
    public bool IsMapExpansion(DlcInfo dlc)
    {
        string key = dlc.Id.ToLowerInvariant();
        return KnownEts2Maps.Contains(key) ||
               (_game == GameId.Ats && key.StartsWith("dlc_", StringComparison.Ordinal) && key.Length == 6 && UsStates.ContainsKey(key[4..])) ||
               (dlc.Features.Count == 1 && dlc.AddsMap);
    }

    private static readonly HashSet<string> KnownEts2Maps =
        ["dlc_east", "dlc_north", "dlc_fr", "dlc_it", "dlc_balt", "dlc_balkan_e", "dlc_iberia", "dlc_balkan_w", "dlc_greece", "dlc_polar", "dlc_iceland"];

    /// <summary>
    /// The DLC's name: from its manifest when owned, else from the list of known DLCs, else tidied ("dlc_foo_bar" →
    /// "Foo Bar"). Unknown IDs are kept recognisable, never dropped.
    /// </summary>
    public string NameOf(string id)
    {
        if (_owned.TryGetValue(id, out var info))
        {
            return info.Name;
        }

        string key = id.ToLowerInvariant();
        if (_game == GameId.Ets2 && KnownEts2.TryGetValue(key, out var known))
        {
            return known;
        }

        // ATS map DLCs are named after US states ("dlc_nm" → New Mexico).
        if (_game == GameId.Ats && key.StartsWith("dlc_", StringComparison.Ordinal) && key.Length == 6 && UsStates.TryGetValue(key[4..], out var state))
        {
            return state;
        }

        string stem = key.StartsWith("dlc_", StringComparison.Ordinal) ? key[4..] : key;
        return string.Join(' ', stem.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..]));
    }

    /// <summary>Reads the owned DLCs from a game folder (about half a second for a hundred DLCs). Read-only.</summary>
    public static DlcCatalog Read(string? installDir, GameId game, CancellationToken cancel = default)
    {
        if (installDir is null || !Directory.Exists(installDir))
        {
            return Empty(game);
        }

        var owned = new List<DlcInfo>();
        foreach (string archive in Directory.EnumerateFiles(installDir, "dlc_*.scs"))
        {
            cancel.ThrowIfCancellationRequested();
            string id = Path.GetFileNameWithoutExtension(archive);
            string? manifest = null;
            try
            {
                using var pkg = ModPackage.Open(archive);
                if (pkg.TryReadFile(id + ".manifest.sii") is { } data)
                {
                    manifest = SiiFileCodec.Decode(data).Text;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            {
                // Owned, but its manifest can't be read: it still counts, with a tidied name.
            }

            owned.Add(Parse(id, manifest, game));
        }

        return new DlcCatalog(owned, game);
    }

    /// <summary>A DLC from its manifest text ("display_name: \"DLC - Nordic Horizons\"", "features[]: map").</summary>
    public static DlcInfo Parse(string id, string? manifest, GameId game)
    {
        string? display = manifest is null ? null : DisplayName().Match(manifest) is { Success: true } m ? m.Groups[1].Value : null;
        string name = display is { Length: > 0 } && !display.StartsWith("@@", StringComparison.Ordinal)
            ? DlcPrefix().Replace(display, "").Trim()
            : Empty(game).NameOf(id);
        var features = manifest is null ? [] : Feature().Matches(manifest).Select(f => f.Groups[1].Value).ToList();
        uint? app = manifest is not null && SteamApp().Match(manifest) is { Success: true } a && uint.TryParse(a.Groups[1].Value, CultureInfo.InvariantCulture, out uint v) ? v : null;
        return new DlcInfo(id, name.Length > 0 ? name : id, features, app);
    }

    [GeneratedRegex("""display_name\s*:\s*"([^"]*)"\s*""")]
    private static partial Regex DisplayName();

    [GeneratedRegex(@"features\[\]\s*:\s*""?(\w+)""?")]
    private static partial Regex Feature();

    [GeneratedRegex(@"steam_app_id\s*:\s*(\d+)")]
    private static partial Regex SteamApp();

    /// <summary>"DLC - ", "DLC – " at the start of a manifest name.</summary>
    [GeneratedRegex(@"^DLC\s*[-–—:]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex DlcPrefix();

    /// <summary>ETS2 DLC names as their manifests give them (ETS2 1.62), for DLCs the player doesn't own.</summary>
    private static readonly Dictionary<string, string> KnownEts2 = new(StringComparer.Ordinal)
    {
        ["dlc_east"] = "Going East!", ["dlc_north"] = "Scandinavia", ["dlc_fr"] = "Vive la France !", ["dlc_it"] = "Italia",
        ["dlc_balt"] = "Beyond the Baltic Sea", ["dlc_balkan_e"] = "Road to the Black Sea", ["dlc_iberia"] = "Iberia",
        ["dlc_balkan_w"] = "West Balkans", ["dlc_greece"] = "Greece", ["dlc_polar"] = "Nordic Horizons", ["dlc_iceland"] = "Iceland",
        ["dlc_oversize"] = "Special Transport", ["dlc_heavy_cargo"] = "Heavy Cargo Pack", ["dlc_trailers"] = "High Power Cargo Pack",
        ["dlc_bus"] = "Coaches", ["dlc_krone"] = "Krone Trailer Pack", ["dlc_feldbinder"] = "Feldbinder Trailer Pack",
        ["dlc_schmitz"] = "Schmitz Trailer Pack", ["dlc_kogel"] = "Kögel Trailer Pack", ["dlc_wielton"] = "Wielton Trailer Pack",
        ["dlc_tirsan"] = "Tirsan Trailer Pack", ["dlc_farm_machinery"] = "Farm Machinery", ["dlc_forest_harvesting"] = "Forest Machinery",
        ["dlc_krone_agriculture"] = "Krone Agriculture", ["dlc_jcb"] = "JCB Equipment Pack", ["dlc_bobcat"] = "Bobcat Cargo Pack",
        ["dlc_volvo_construction"] = "Volvo Construction", ["dlc_daf_2021"] = "DAF NGD", ["dlc_daf_xd"] = "DAF XD",
        ["dlc_toys"] = "Cabin Accessories", ["dlc_rims"] = "Wheels Tuning Pack", ["dlc_goodyear"] = "Goodyear Tyres Pack",
        ["dlc_michelin"] = "Michelin Fan Pack", ["dlc_hs_schoch"] = "HS-Schoch Tuning Pack",
    };

    private static readonly Dictionary<string, string> UsStates = new(StringComparer.Ordinal)
    {
        ["al"] = "Alabama", ["ak"] = "Alaska", ["az"] = "Arizona", ["ar"] = "Arkansas", ["ca"] = "California", ["co"] = "Colorado",
        ["ct"] = "Connecticut", ["de"] = "Delaware", ["fl"] = "Florida", ["ga"] = "Georgia", ["hi"] = "Hawaii", ["id"] = "Idaho",
        ["il"] = "Illinois", ["in"] = "Indiana", ["ia"] = "Iowa", ["ks"] = "Kansas", ["ky"] = "Kentucky", ["la"] = "Louisiana",
        ["me"] = "Maine", ["md"] = "Maryland", ["ma"] = "Massachusetts", ["mi"] = "Michigan", ["mn"] = "Minnesota", ["ms"] = "Mississippi",
        ["mo"] = "Missouri", ["mt"] = "Montana", ["ne"] = "Nebraska", ["nv"] = "Nevada", ["nh"] = "New Hampshire", ["nj"] = "New Jersey",
        ["nm"] = "New Mexico", ["ny"] = "New York", ["nc"] = "North Carolina", ["nd"] = "North Dakota", ["oh"] = "Ohio", ["ok"] = "Oklahoma",
        ["or"] = "Oregon", ["pa"] = "Pennsylvania", ["ri"] = "Rhode Island", ["sc"] = "South Carolina", ["sd"] = "South Dakota",
        ["tn"] = "Tennessee", ["tx"] = "Texas", ["ut"] = "Utah", ["vt"] = "Vermont", ["va"] = "Virginia", ["wa"] = "Washington",
        ["wv"] = "West Virginia", ["wi"] = "Wisconsin", ["wy"] = "Wyoming",
    };
}
