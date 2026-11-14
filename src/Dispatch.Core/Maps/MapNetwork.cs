namespace Dispatch.Core.Maps;

/// <summary>What one layer (the game, a DLC, a mod) contributes to a profile's road network.</summary>
/// <param name="Index">Position in the stack, 0 = bottom.</param>
/// <param name="Roads">Roads on the map that this layer added (first had).</param>
/// <param name="Sectors">Sector files the layer has.</param>
/// <param name="SectorsShown">Of those, how many the game uses (the rest are replaced by layers above).</param>
/// <param name="Bounds">Where its roads are, or null when none are shown.</param>
public sealed record MapNetworkLayer(int Index, string Name, int Roads, double Km, int Sectors, int SectorsShown, int UnreadableSectors,
                                     bool IsUnreadable, MapRect? Bounds);

/// <summary>
/// A profile's whole road network: every road the game loads (each sector from the topmost layer that has it), each
/// credited to the layer that added it, i.e. the lowest layer whose sectors have that road's UID. A mod that copies a
/// sector unchanged therefore doesn't take credit for the roads it kept.
/// </summary>
public sealed class MapNetwork
{
    private MapNetwork(IReadOnlyList<MapNetworkLayer> layers, IReadOnlyList<IReadOnlyList<MapRoad>> roads,
                       IReadOnlyList<IReadOnlyList<MapPrefab>> prefabs, IReadOnlyList<(string Name, MapPoint Position, int Owner)> cities, MapRect? extent)
    {
        Layers = layers;
        RoadsByLayer = roads;
        PrefabsByLayer = prefabs;
        Cities = cities;
        Extent = extent;
    }

    /// <summary>One entry per input layer, bottom first.</summary>
    public IReadOnlyList<MapNetworkLayer> Layers { get; }

    /// <summary>The roads each layer added, indexed like <see cref="Layers"/>.</summary>
    public IReadOnlyList<IReadOnlyList<MapRoad>> RoadsByLayer { get; }

    public IReadOnlyList<IReadOnlyList<MapPrefab>> PrefabsByLayer { get; }

    /// <summary>One label per city, with the layer that added it.</summary>
    public IReadOnlyList<(string Name, MapPoint Position, int Owner)> Cities { get; }

    public MapRect? Extent { get; }

    /// <param name="bottomFirst">The game's archives, then the profile's mods from the bottom of the load order up.</param>
    public static MapNetwork Build(IReadOnlyList<MapLayer> bottomFirst)
    {
        // Who had each item first.
        var roadOwner = new Dictionary<ulong, int>();
        var prefabOwner = new Dictionary<ulong, int>();
        var cityOwner = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var effective = new Dictionary<string, (int Layer, MapSector Sector)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < bottomFirst.Count; i++)
        {
            foreach (var s in bottomFirst[i].Map.Sectors.Values)
            {
                foreach (var r in s.Roads)
                {
                    roadOwner.TryAdd(r.Uid, i);
                }

                foreach (var p in s.Prefabs)
                {
                    prefabOwner.TryAdd(p.Uid, i);
                }

                foreach (var c in s.Cities)
                {
                    cityOwner.TryAdd(c.Token, i);
                }

                effective[s.Name] = (i, s);
            }

            foreach (var (token, name) in bottomFirst[i].Map.CityNames)
            {
                names[token] = name;
            }
        }

        var roads = bottomFirst.Select(_ => new List<MapRoad>()).ToList();
        var prefabs = bottomFirst.Select(_ => new List<MapPrefab>()).ToList();
        var shown = new int[bottomFirst.Count];
        var cityPoints = new Dictionary<string, List<MapPoint>>(StringComparer.Ordinal);
        foreach (var (layer, sector) in effective.Values)
        {
            shown[layer]++;
            foreach (var r in sector.Roads)
            {
                roads[roadOwner[r.Uid]].Add(r);
            }

            foreach (var p in sector.Prefabs)
            {
                prefabs[prefabOwner[p.Uid]].Add(p);
            }

            foreach (var c in sector.Cities)
            {
                if (!cityPoints.TryGetValue(c.Token, out var list))
                {
                    cityPoints[c.Token] = list = [];
                }

                list.Add(c.Position);
            }
        }

        var cities = cityPoints
            .Where(kv => names.ContainsKey(kv.Key) || !kv.Key.Any(char.IsAsciiDigit)) // see MapComparison
            .Select(kv => (Name: names.GetValueOrDefault(kv.Key) ?? MapComparison.Readable(kv.Key),
                           Position: new MapPoint(kv.Value.Average(p => p.X), kv.Value.Average(p => p.Z)),
                           Owner: cityOwner[kv.Key]))
            .ToList();

        var layers = bottomFirst.Select((l, i) => new MapNetworkLayer(
            i, l.Name, roads[i].Count, roads[i].Sum(r => (double)r.Length) / 1000, l.Map.Sectors.Count, shown[i],
            l.Map.UnreadableSectors, l.Map.IsUnreadable, Bounds(roads[i]))).ToList();
        MapRect? extent = null;
        foreach (var b in layers.Select(l => l.Bounds).OfType<MapRect>())
        {
            extent = extent is { } x ? x.Union(b) : b;
        }

        return new MapNetwork(layers, roads, prefabs, cities, extent);
    }

    private static MapRect? Bounds(List<MapRoad> roads) => roads.Count == 0 ? null : roads.Select(r => r.Bounds).Aggregate((a, b) => a.Union(b));
}
