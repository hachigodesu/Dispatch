using System.Buffers.Binary;
using System.Text;

namespace Dispatch.Core.Maps;

/// <summary>SCS tokens: up to 12 characters of [0-9a-z_] packed base-38 into a u64.</summary>
public static class MapToken
{
    private const string Chars = "\00123456789abcdefghijklmnopqrstuvwxyz_";

    /// <summary>The token's text, or null when the value isn't a valid token.</summary>
    public static string? Decode(ulong value)
    {
        var sb = new StringBuilder(12);
        while (value > 0)
        {
            int c = (int)(value % 38);
            if (c == 0)
            {
                return null;
            }

            sb.Append(Chars[c]);
            value /= 38;
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    public static ulong Encode(string text)
    {
        ulong value = 0, power = 1;
        foreach (char ch in text)
        {
            int c = Chars.IndexOf(ch, 1);
            if (c <= 0)
            {
                throw new ArgumentException($"'{ch}' can't be part of a token.", nameof(text));
            }

            value += (ulong)c * power;
            power *= 38;
        }

        return value;
    }
}

/// <summary>
/// Reads roads, prefabs and cities from a sector's .base file without knowing every item type's layout (items have
/// no length prefix and their layouts change between game versions). Format: docs/spikes/S12-map-view.md.
/// <list type="number">
/// <item>The node table is found from the end of the file: <c>u32 count</c> + fixed 56-byte nodes, followed by
/// <c>u32 count</c> + u64 UIDs up to the end.</item>
/// <item>Items are found by their header (<c>u32 type, u64 uid</c>) where the UID is one the nodes refer to.</item>
/// <item>A road's ends are the node whose forward item is the road and the node whose backward item is the road.</item>
/// </list>
/// </summary>
public static class SectorReader
{
    public const int NodeSize = 56;
    private const int HeaderSize = 20; // u32 core map version, u64 game id token, u32 game map version, u32 item count
    private const uint RoadType = 3;
    private const uint PrefabType = 4;
    private const uint CityType = 12;
    private const int CityNameOffset = 57; // after the k-DOP item header (type, uid, 40-byte box, flags, view distance)
    private const int CityNodeOffset = 73; // after the name token and two floats (width, height)
    private static readonly ulong GameIdToken = MapToken.Encode("euro2");

    private readonly record struct Node(MapPoint Position, MapPoint Direction, ulong Backward, ulong Forward);

    /// <summary>The sector's contents, or null when the data isn't a sector this reader understands.</summary>
    public static MapSector? Read(string name, ReadOnlySpan<byte> d)
    {
        if (d.Length < HeaderSize + 8 || U64(d, 4) != GameIdToken)
        {
            return null;
        }

        if (!FindNodeTable(d, out int nodesStart, out int nodeCount))
        {
            return null;
        }

        var nodes = new Dictionary<ulong, Node>(nodeCount);
        var forwardOf = new Dictionary<ulong, ulong>(); // item uid → node uid whose forward item it is
        var backwardOf = new Dictionary<ulong, ulong>();
        var byItem = new Dictionary<ulong, List<ulong>>();
        for (int i = 0; i < nodeCount; i++)
        {
            int o = nodesStart + 4 + (i * NodeSize);
            ulong uid = U64(d, o);
            var pos = new MapPoint(I32(d, o + 8) / 256f, I32(d, o + 16) / 256f);
            var dir = Forward(F32(d, o + 20), F32(d, o + 24), F32(d, o + 28), F32(d, o + 32));
            var node = new Node(pos, dir, U64(d, o + 36), U64(d, o + 44));
            nodes[uid] = node;
            if (node.Forward != 0)
            {
                forwardOf[node.Forward] = uid;
                Add(byItem, node.Forward, uid);
            }

            if (node.Backward != 0)
            {
                backwardOf[node.Backward] = uid;
                Add(byItem, node.Backward, uid);
            }
        }

        var sector = new MapSector(name, U32(d, 0));
        var seen = new HashSet<ulong>();
        for (int o = HeaderSize; o + 12 <= nodesStart; o++)
        {
            uint type = U32(d, o);
            if (type is not (RoadType or PrefabType or CityType))
            {
                continue;
            }

            ulong uid = U64(d, o + 4);
            if (type == RoadType && forwardOf.TryGetValue(uid, out ulong startUid) && backwardOf.TryGetValue(uid, out ulong endUid) && seen.Add(uid))
            {
                var a = nodes[startUid];
                var b = nodes[endUid];
                var chord = b.Position - a.Position;
                sector.Roads.Add(new MapRoad(uid, a.Position, b.Position, Along(a.Direction, chord), Along(b.Direction, chord)));
                o += 11; // past the header only: the road's own size differs between map versions
            }
            else if (type == PrefabType && byItem.TryGetValue(uid, out var prefabNodes) && seen.Add(uid))
            {
                sector.Prefabs.Add(new MapPrefab(uid, prefabNodes.Distinct().Select(n => nodes[n].Position).ToList()));
            }
            else if (type == CityType && o + CityNodeOffset + 8 <= nodesStart &&
                     nodes.TryGetValue(U64(d, o + CityNodeOffset), out var cityNode) &&
                     MapToken.Decode(U64(d, o + CityNameOffset)) is { } token && seen.Add(uid))
            {
                sector.Cities.Add(new MapCity(token, cityNode.Position));
            }
        }

        return sector;
    }

    /// <summary>Works back from the end: trailing UID list, then the node table before it.</summary>
    private static bool FindNodeTable(ReadOnlySpan<byte> d, out int nodesStart, out int nodeCount)
    {
        for (long v = 0; ; v++)
        {
            long tailStart = d.Length - 4L - (8L * v);
            if (tailStart < HeaderSize)
            {
                break;
            }

            if (U32(d, (int)tailStart) != v)
            {
                continue;
            }

            // Largest count first: a small one is easy to fake (the last node's flags are often 0, which looks like
            // "0 nodes"), a large one needs that many records that all look like nodes.
            for (long n = (tailStart - HeaderSize - 4) / NodeSize; n >= 0; n--)
            {
                long start = tailStart - (NodeSize * n) - 4;
                if (U32(d, (int)start) == n && PlausibleNodes(d, (int)start + 4, (int)n))
                {
                    nodesStart = (int)start;
                    nodeCount = (int)n;
                    return true;
                }
            }
        }

        nodesStart = nodeCount = 0;
        return false;
    }

    /// <summary>Real nodes have a UID and a unit quaternion; random data almost never does.</summary>
    private static bool PlausibleNodes(ReadOnlySpan<byte> d, int start, int count)
    {
        for (int k = 0; k < Math.Min(count, 9); k++)
        {
            int i = k < 8 ? k : count - 1; // the first eight and the last
            int o = start + (i * NodeSize);
            float w = F32(d, o + 20), x = F32(d, o + 24), y = F32(d, o + 28), z = F32(d, o + 32);
            double m = (w * w) + (x * x) + (y * y) + (z * z);
            if (U64(d, o) == 0 || !(Math.Abs(m - 1) < 0.05))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The node's (0, 0, −1) axis rotated by its quaternion (w, x, y, z), flattened onto the map.</summary>
    private static MapPoint Forward(float w, float x, float y, float z)
    {
        // Third column of the rotation matrix, negated.
        float fx = -2 * ((x * z) + (w * y));
        float fz = -(1 - (2 * ((x * x) + (y * y))));
        float len = MathF.Sqrt((fx * fx) + (fz * fz));
        return len < 1e-6f ? default : new MapPoint(fx / len, fz / len);
    }

    /// <summary>A node's direction turned to point the way the road runs (nodes can face either way).</summary>
    private static MapPoint Along(MapPoint dir, MapPoint chord)
    {
        if (dir == default)
        {
            float len = chord.Length;
            return len < 1e-6f ? default : chord * (1 / len);
        }

        return dir.Dot(chord) < 0 ? dir * -1 : dir;
    }

    private static void Add(Dictionary<ulong, List<ulong>> map, ulong key, ulong value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        list.Add(value);
    }

    private static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);

    private static int I32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d[o..]);

    private static ulong U64(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt64LittleEndian(d[o..]);

    private static float F32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadSingleLittleEndian(d[o..]);
}
