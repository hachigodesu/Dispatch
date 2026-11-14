using System.Security.Cryptography;
using System.Text;

namespace Dispatch.Core.Maps;

/// <summary>
/// Keeps what <see cref="MapArchiveReader"/> found per archive in Dispatch's own cache folder, keyed by path, size and
/// last-write time, so an unchanged archive is read once. A broken or outdated cache file is simply read again.
/// </summary>
public sealed class MapCache(string cacheDir)
{
    private const uint Magic = 0x50414D44; // "DMAP"
    private const int Schema = 1; // bump when MapArchiveReader's output changes

    private string Dir => Path.Combine(cacheDir, "maps");

    /// <summary>The archive's map, from the cache when it's unchanged, otherwise read (and cached).</summary>
    public ArchiveMap Get(string archivePath, string mapName, CancellationToken cancel = default)
    {
        var (size, mtime) = Stamp(archivePath);
        string file = Path.Combine(Dir, Key(archivePath, mapName) + ".bin");
        if (TryLoad(file, archivePath, mapName, size, mtime) is { } cached)
        {
            return cached;
        }

        var map = MapArchiveReader.Read(archivePath, mapName, cancel);
        TrySave(file, archivePath, mapName, size, mtime, map);
        return map;
    }

    private static (long Size, long Mtime) Stamp(string path)
    {
        if (Directory.Exists(path))
        {
            // A folder mod (Workshop): newest file wins.
            var files = new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            return (files.Sum(f => f.Length), files.Count == 0 ? 0 : files.Max(f => f.LastWriteTimeUtc.Ticks));
        }

        var fi = new FileInfo(path);
        return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc.Ticks) : (-1, 0);
    }

    private static string Key(string path, string mapName) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant() + "|" + mapName)))[..24];

    private static ArchiveMap? TryLoad(string file, string path, string mapName, long size, long mtime)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            using var r = new BinaryReader(File.OpenRead(file), Encoding.UTF8);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Schema || r.ReadString() != Path.GetFullPath(path) ||
                r.ReadString() != mapName || r.ReadInt64() != size || r.ReadInt64() != mtime)
            {
                return null;
            }

            return Deserialize(r);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    private void TrySave(string file, string path, string mapName, long size, long mtime, ArchiveMap map)
    {
        string temp = file + ".tmp";
        try
        {
            Directory.CreateDirectory(Dir);
            using (var w = new BinaryWriter(File.Create(temp), Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(Schema);
                w.Write(Path.GetFullPath(path));
                w.Write(mapName);
                w.Write(size);
                w.Write(mtime);
                Serialize(w, map);
            }

            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that can't be written only costs time next run.
            try
            {
                File.Delete(temp);
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    internal static void Serialize(BinaryWriter w, ArchiveMap map)
    {
        w.Write(map.IsUnreadable);
        w.Write(map.UnreadableSectors);
        w.Write(map.CityNames.Count);
        foreach (var (token, name) in map.CityNames)
        {
            w.Write(token);
            w.Write(name);
        }

        w.Write(map.Sectors.Count);
        foreach (var s in map.Sectors.Values)
        {
            w.Write(s.Name);
            w.Write(s.MapVersion);
            w.Write(s.Roads.Count);
            foreach (var r in s.Roads)
            {
                w.Write(r.Uid);
                Write(w, r.A);
                Write(w, r.B);
                Write(w, r.DirA);
                Write(w, r.DirB);
            }

            w.Write(s.Prefabs.Count);
            foreach (var p in s.Prefabs)
            {
                w.Write(p.Uid);
                w.Write(p.Nodes.Count);
                foreach (var n in p.Nodes)
                {
                    Write(w, n);
                }
            }

            w.Write(s.Cities.Count);
            foreach (var c in s.Cities)
            {
                w.Write(c.Token);
                Write(w, c.Position);
            }
        }
    }

    internal static ArchiveMap Deserialize(BinaryReader r)
    {
        var map = new ArchiveMap { IsUnreadable = r.ReadBoolean(), UnreadableSectors = r.ReadInt32() };
        int cities = r.ReadInt32();
        for (int i = 0; i < cities; i++)
        {
            map.CityNames[r.ReadString()] = r.ReadString();
        }

        int sectors = r.ReadInt32();
        for (int i = 0; i < sectors; i++)
        {
            var s = new MapSector(r.ReadString(), r.ReadUInt32());
            int roads = r.ReadInt32();
            s.Roads.Capacity = roads;
            for (int j = 0; j < roads; j++)
            {
                s.Roads.Add(new MapRoad(r.ReadUInt64(), Point(r), Point(r), Point(r), Point(r)));
            }

            int prefabs = r.ReadInt32();
            for (int j = 0; j < prefabs; j++)
            {
                ulong uid = r.ReadUInt64();
                var nodes = new MapPoint[r.ReadInt32()];
                for (int k = 0; k < nodes.Length; k++)
                {
                    nodes[k] = Point(r);
                }

                s.Prefabs.Add(new MapPrefab(uid, nodes));
            }

            int cityItems = r.ReadInt32();
            for (int j = 0; j < cityItems; j++)
            {
                s.Cities.Add(new MapCity(r.ReadString(), Point(r)));
            }

            map.Sectors[s.Name] = s;
        }

        return map;
    }

    private static void Write(BinaryWriter w, MapPoint p)
    {
        w.Write(p.X);
        w.Write(p.Z);
    }

    private static MapPoint Point(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle());
}
