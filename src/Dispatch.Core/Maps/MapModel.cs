using System.Globalization;

namespace Dispatch.Core.Maps;

/// <summary>A point on the game map in metres: X grows east, Z grows south (so north is up when Z is drawn down).</summary>
public readonly record struct MapPoint(float X, float Z)
{
    public static MapPoint operator +(MapPoint a, MapPoint b) => new(a.X + b.X, a.Z + b.Z);

    public static MapPoint operator -(MapPoint a, MapPoint b) => new(a.X - b.X, a.Z - b.Z);

    public static MapPoint operator *(MapPoint a, float s) => new(a.X * s, a.Z * s);

    public float Length => MathF.Sqrt((X * X) + (Z * Z));

    public float Dot(MapPoint o) => (X * o.X) + (Z * o.Z);
}

/// <summary>An axis-aligned rectangle in map metres.</summary>
public readonly record struct MapRect(float MinX, float MinZ, float MaxX, float MaxZ)
{
    public float Width => MaxX - MinX;

    public float Height => MaxZ - MinZ;

    public bool Intersects(MapRect o) => MinX <= o.MaxX && o.MinX <= MaxX && MinZ <= o.MaxZ && o.MinZ <= MaxZ;

    public MapRect Union(MapRect o) => new(Math.Min(MinX, o.MinX), Math.Min(MinZ, o.MinZ), Math.Max(MaxX, o.MaxX), Math.Max(MaxZ, o.MaxZ));

    public MapRect Inflate(float by) => new(MinX - by, MinZ - by, MaxX + by, MaxZ + by);

    public static MapRect? Around(IEnumerable<MapPoint> points)
    {
        MapRect? r = null;
        foreach (var p in points)
        {
            var pr = new MapRect(p.X, p.Z, p.X, p.Z);
            r = r is { } cur ? cur.Union(pr) : pr;
        }

        return r;
    }
}

/// <summary>
/// One road item: a cubic Hermite curve from <see cref="A"/> to <see cref="B"/>. The directions are unit vectors that
/// point along the road from A towards B; the tangents' length is the distance between the ends (how the game draws it).
/// </summary>
public readonly record struct MapRoad(ulong Uid, MapPoint A, MapPoint B, MapPoint DirA, MapPoint DirB)
{
    public float Length => (B - A).Length;

    public MapPoint At(float t)
    {
        float len = Length, t2 = t * t, t3 = t2 * t;
        return (A * ((2 * t3) - (3 * t2) + 1)) + (DirA * (len * (t3 - (2 * t2) + t))) +
               (B * ((-2 * t3) + (3 * t2))) + (DirB * (len * (t3 - t2)));
    }

    /// <summary>A box that contains the whole curve (the Bézier control points' box).</summary>
    public MapRect Bounds
    {
        get
        {
            float third = Length / 3;
            var c1 = A + (DirA * third);
            var c2 = B - (DirB * third);
            return new MapRect(Math.Min(Math.Min(A.X, B.X), Math.Min(c1.X, c2.X)), Math.Min(Math.Min(A.Z, B.Z), Math.Min(c1.Z, c2.Z)),
                               Math.Max(Math.Max(A.X, B.X), Math.Max(c1.X, c2.X)), Math.Max(Math.Max(A.Z, B.Z), Math.Max(c1.Z, c2.Z)));
        }
    }
}

/// <summary>A prefab (junction, company yard, …), known only by where its nodes are.</summary>
public sealed record MapPrefab(ulong Uid, IReadOnlyList<MapPoint> Nodes)
{
    public MapPoint Centre => new(Nodes.Average(n => n.X), Nodes.Average(n => n.Z));
}

/// <summary>A city item: its token (e.g. "goteborg") and where it is.</summary>
public readonly record struct MapCity(string Token, MapPoint Position);

/// <summary>The roads, prefabs and cities of one sector's .base file.</summary>
public sealed class MapSector(string name, uint mapVersion)
{
    public const float Size = 4000;

    /// <summary>File name without extension, e.g. "sec+0012-0003".</summary>
    public string Name { get; } = name;

    public uint MapVersion { get; } = mapVersion;

    public List<MapRoad> Roads { get; } = [];

    public List<MapPrefab> Prefabs { get; } = [];

    public List<MapCity> Cities { get; } = [];

    /// <summary>The 4000 m cell a sector name stands for ("sec+0000-0007" covers x 0…4000, z −28000…−24000).</summary>
    public static MapRect? CellOf(string sectorName)
    {
        // sec±XXXX±ZZZZ
        if (sectorName.Length != 13 || !sectorName.StartsWith("sec", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(sectorName.AsSpan(3, 5), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(sectorName.AsSpan(8, 5), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int z))
        {
            return null;
        }

        return new MapRect(x * Size, z * Size, (x + 1) * Size, (z + 1) * Size);
    }
}

/// <summary>Everything Dispatch could read from one archive's map folder.</summary>
public sealed class ArchiveMap
{
    public static ArchiveMap Empty { get; } = new();

    /// <summary>Sectors by name (case-insensitive), e.g. "sec+0012-0003".</summary>
    public Dictionary<string, MapSector> Sectors { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>City display names by token, from the archive's def/city files.</summary>
    public Dictionary<string, string> CityNames { get; } = new(StringComparer.Ordinal);

    /// <summary>Sector files that exist but couldn't be read (an old map format, damaged data).</summary>
    public int UnreadableSectors { get; set; }

    /// <summary>The package is protected or broken, so nothing inside can be read.</summary>
    public bool IsUnreadable { get; set; }

    public bool HasMap => Sectors.Count > 0 || UnreadableSectors > 0;
}
