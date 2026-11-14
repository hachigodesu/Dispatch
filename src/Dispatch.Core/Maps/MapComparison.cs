namespace Dispatch.Core.Maps;

/// <summary>One archive in the stack the game loads: the base game, a DLC or a mod.</summary>
public sealed record MapLayer(string Name, ArchiveMap Map);

/// <summary>How many items a mod's sectors take away from one layer below it, or how many of its sectors one layer above hides.</summary>
public sealed record MapLayerCount(string Name, int Count);

/// <summary>
/// What one map mod changes, compared with what the game loads below it. A mod's sector file replaces the whole file
/// of the same name below it (no merging), so the comparison is per sector, by item UID.
/// </summary>
public sealed class MapComparison
{
    /// <summary>Roads drawn in grey: the map with the mod applied, minus <see cref="AddedRoads"/>.</summary>
    public required IReadOnlyList<MapRoad> BackgroundRoads { get; init; }

    public required IReadOnlyList<MapPrefab> BackgroundPrefabs { get; init; }

    /// <summary>Roads in the mod's sectors that the sector below didn't have (all roads of a new sector).</summary>
    public required IReadOnlyList<MapRoad> AddedRoads { get; init; }

    public required IReadOnlyList<MapPrefab> AddedPrefabs { get; init; }

    /// <summary>Roads below that disappear because the mod's version of their sector doesn't have them.</summary>
    public required IReadOnlyList<MapRoad> RemovedRoads { get; init; }

    /// <summary>Which layers the removed roads came from (most first).</summary>
    public required IReadOnlyList<MapLayerCount> RemovedFrom { get; init; }

    /// <summary>Mods above this one that replace some of its sectors (most first), and the cells they hide.</summary>
    public required IReadOnlyList<MapLayerCount> HiddenBy { get; init; }

    public required IReadOnlyList<MapRect> HiddenCells { get; init; }

    /// <summary>Cities with their display name, one label per city, from the map with the mod applied.</summary>
    public required IReadOnlyList<(string Name, MapPoint Position, bool Added)> Cities { get; init; }

    public required int NewSectors { get; init; }

    public required int ReplacedSectors { get; init; }

    public required int UnreadableSectors { get; init; }

    public required bool ModUnreadable { get; init; }

    /// <summary>Where to look first: the mod's changes, or all its sectors when nothing changed.</summary>
    public required MapRect? Focus { get; init; }

    /// <summary>The whole map with the mod applied.</summary>
    public required MapRect? Extent { get; init; }

    public double AddedKm => AddedRoads.Sum(r => (double)r.Length) / 1000;

    public int AddedCities => Cities.Count(c => c.Added);

    /// <param name="mod">The mod to show.</param>
    /// <param name="below">Everything loaded below it, bottom first (base game and DLCs, then lower mods).</param>
    /// <param name="above">Mods loaded above it, top first (only used to report the sectors they hide).</param>
    public static MapComparison Compare(MapLayer mod, IReadOnlyList<MapLayer> below, IReadOnlyList<MapLayer> above)
    {
        // What the game has without the mod: per sector name, the topmost layer that provides it.
        var under = new Dictionary<string, (MapLayer Layer, MapSector Sector)>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in below)
        {
            foreach (var s in layer.Map.Sectors.Values)
            {
                under[s.Name] = (layer, s);
            }
        }

        var added = new List<MapRoad>();
        var addedPrefabs = new List<MapPrefab>();
        var removed = new List<MapRoad>();
        var removedFrom = new Dictionary<string, int>(StringComparer.Ordinal);
        int fresh = 0, replaced = 0;
        foreach (var s in mod.Map.Sectors.Values)
        {
            if (under.TryGetValue(s.Name, out var old))
            {
                replaced++;
                var oldRoads = old.Sector.Roads.Select(r => r.Uid).ToHashSet();
                var newRoads = s.Roads.Select(r => r.Uid).ToHashSet();
                var oldPrefabs = old.Sector.Prefabs.Select(p => p.Uid).ToHashSet();
                added.AddRange(s.Roads.Where(r => !oldRoads.Contains(r.Uid)));
                addedPrefabs.AddRange(s.Prefabs.Where(p => !oldPrefabs.Contains(p.Uid)));
                int before = removed.Count;
                removed.AddRange(old.Sector.Roads.Where(r => !newRoads.Contains(r.Uid)));
                if (removed.Count > before)
                {
                    removedFrom[old.Layer.Name] = removedFrom.GetValueOrDefault(old.Layer.Name) + removed.Count - before;
                }
            }
            else
            {
                fresh++;
                added.AddRange(s.Roads);
                addedPrefabs.AddRange(s.Prefabs);
            }
        }

        // The map as the game shows it with the mod on top (ignoring mods above, which are reported separately).
        var effective = new Dictionary<string, MapSector>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entry) in under)
        {
            effective[name] = entry.Sector;
        }

        foreach (var s in mod.Map.Sectors.Values)
        {
            effective[s.Name] = s;
        }

        var addedUids = added.Select(r => r.Uid).ToHashSet();
        var addedPrefabUids = addedPrefabs.Select(p => p.Uid).ToHashSet();
        var background = effective.Values.SelectMany(s => s.Roads).Where(r => !addedUids.Contains(r.Uid)).ToList();
        var backgroundPrefabs = effective.Values.SelectMany(s => s.Prefabs).Where(p => !addedPrefabUids.Contains(p.Uid)).ToList();

        // Sectors of this mod that a mod above replaces: the game shows that mod's version instead.
        var hiddenBy = new Dictionary<string, int>(StringComparer.Ordinal);
        var hiddenCells = new List<MapRect>();
        foreach (var s in mod.Map.Sectors.Values)
        {
            if (above.FirstOrDefault(l => l.Map.Sectors.ContainsKey(s.Name)) is { } hider)
            {
                hiddenBy[hider.Name] = hiddenBy.GetValueOrDefault(hider.Name) + 1;
                if (MapSector.CellOf(s.Name) is { } cell)
                {
                    hiddenCells.Add(cell);
                }
            }
        }

        // Cities: names from any layer (the mod's own win), one label per token.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var layer in below.Append(mod))
        {
            foreach (var (token, name) in layer.Map.CityNames)
            {
                names[token] = name;
            }
        }

        var tokensBelow = under.Values.SelectMany(e => e.Sector.Cities).Select(c => c.Token).ToHashSet(StringComparer.Ordinal);
        var tokensInMod = mod.Map.Sectors.Values.SelectMany(s => s.Cities).Select(c => c.Token).ToHashSet(StringComparer.Ordinal);
        // A city without a name in any def file whose token has digits ("g66lt30zu12") isn't a label worth showing.
        var cities = effective.Values.SelectMany(s => s.Cities)
            .Where(c => names.ContainsKey(c.Token) || !c.Token.Any(char.IsAsciiDigit))
            .GroupBy(c => c.Token, StringComparer.Ordinal)
            .Select(g => (Name: names.GetValueOrDefault(g.Key) ?? Readable(g.Key),
                          Position: new MapPoint(g.Average(c => c.Position.X), g.Average(c => c.Position.Z)),
                          Added: tokensInMod.Contains(g.Key) && !tokensBelow.Contains(g.Key)))
            .ToList();

        var changed = added.Concat(removed).Select(r => r.Bounds).ToList();
        if (changed.Count == 0)
        {
            changed = mod.Map.Sectors.Keys.Select(MapSector.CellOf).OfType<MapRect>().ToList();
        }

        MapRect? focus = changed.Count == 0 ? null : changed.Aggregate((a, b) => a.Union(b));
        MapRect? extent = MapRect.Around(background.Concat(added).SelectMany(r => new[] { r.A, r.B }));

        return new MapComparison
        {
            BackgroundRoads = background,
            BackgroundPrefabs = backgroundPrefabs,
            AddedRoads = added,
            AddedPrefabs = addedPrefabs,
            RemovedRoads = removed,
            RemovedFrom = removedFrom.OrderByDescending(kv => kv.Value).Select(kv => new MapLayerCount(kv.Key, kv.Value)).ToList(),
            HiddenBy = hiddenBy.OrderByDescending(kv => kv.Value).Select(kv => new MapLayerCount(kv.Key, kv.Value)).ToList(),
            HiddenCells = hiddenCells,
            Cities = cities,
            NewSectors = fresh,
            ReplacedSectors = replaced,
            UnreadableSectors = mod.Map.UnreadableSectors,
            ModUnreadable = mod.Map.IsUnreadable,
            Focus = focus,
            Extent = extent,
        };
    }

    /// <summary>"gagra_abh" → "Gagra Abh", for cities without a name in any def file.</summary>
    public static string Readable(string token) =>
        string.Join(' ', token.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
