using System.Buffers.Binary;
using System.Text;
using Dispatch.Core.Maps;
using Dispatch.Core.Tests.Mods;

namespace Dispatch.Core.Tests.Maps;

/// <summary>Builds .base sector files in the layout described in docs/spikes/S12-map-view.md.</summary>
internal sealed class SectorBuilder
{
    private readonly List<byte[]> _items = [];
    private readonly List<(ulong Uid, float X, float Z, float Angle, ulong Back, ulong Fwd)> _nodes = [];
    private readonly List<ulong> _trailing = [];
    private ulong _next = 0x1000;

    public uint Version { get; init; } = 907;

    public ulong Uid() => _next++;

    /// <summary>A node; angle in radians around the vertical axis (0 = facing north, −π/2 = east).</summary>
    public ulong Node(float x, float z, float angle = 0, ulong back = 0, ulong fwd = 0)
    {
        ulong uid = Uid();
        _nodes.Add((uid, x, z, angle, back, fwd));
        return uid;
    }

    public void SetItems(ulong node, ulong back, ulong fwd)
    {
        int i = _nodes.FindIndex(n => n.Uid == node);
        _nodes[i] = _nodes[i] with { Back = back, Fwd = fwd };
    }

    /// <summary>A road from (x0,z0) to (x1,z1), with the real 269-byte layout (node UIDs at +249/+257).</summary>
    public ulong Road(float x0, float z0, float x1, float z1, float angle = 0)
    {
        ulong uid = Uid();
        ulong a = Node(x0, z0, angle, fwd: uid);
        ulong b = Node(x1, z1, angle, back: uid);
        var item = Header(3, uid, 269);
        BinaryPrimitives.WriteUInt64LittleEndian(item.AsSpan(61), MapToken.Encode("template17"));
        BinaryPrimitives.WriteUInt64LittleEndian(item.AsSpan(249), a);
        BinaryPrimitives.WriteUInt64LittleEndian(item.AsSpan(257), b);
        _items.Add(item);
        return uid;
    }

    public ulong Prefab(params (float X, float Z)[] at)
    {
        ulong uid = Uid();
        var item = Header(4, uid, 120);
        foreach (var (x, z) in at)
        {
            Node(x, z, back: uid);
        }

        _items.Add(item);
        return uid;
    }

    public void City(string token, float x, float z)
    {
        ulong uid = Uid();
        ulong node = Node(x, z, fwd: uid);
        var item = Header(12, uid, 90);
        BinaryPrimitives.WriteUInt64LittleEndian(item.AsSpan(57), MapToken.Encode(token));
        BinaryPrimitives.WriteSingleLittleEndian(item.AsSpan(65), 300f);
        BinaryPrimitives.WriteSingleLittleEndian(item.AsSpan(69), 200f);
        BinaryPrimitives.WriteUInt64LittleEndian(item.AsSpan(73), node);
        _items.Add(item);
    }

    /// <summary>An item of a type the reader doesn't know, with bytes that look a bit like a road header inside.</summary>
    public void Junk()
    {
        var item = Header(6, Uid(), 77);
        BinaryPrimitives.WriteUInt32LittleEndian(item.AsSpan(30), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(item.AsSpan(34), 0xDEADBEEF);
        _items.Add(item);
    }

    public void TrailingUid() => _trailing.Add(Uid());

    public byte[] Build()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Version);
        w.Write(MapToken.Encode("euro2"));
        w.Write(3u);
        w.Write((uint)_items.Count);
        foreach (var item in _items)
        {
            w.Write(item);
        }

        w.Write((uint)_nodes.Count);
        foreach (var n in _nodes)
        {
            w.Write(n.Uid);
            w.Write((int)(n.X * 256));
            w.Write(0);
            w.Write((int)(n.Z * 256));
            w.Write(MathF.Cos(n.Angle / 2)); // w
            w.Write(0f);                     // x
            w.Write(MathF.Sin(n.Angle / 2)); // y
            w.Write(0f);                     // z
            w.Write(n.Back);
            w.Write(n.Fwd);
            w.Write(0u);
        }

        w.Write((uint)_trailing.Count);
        foreach (ulong u in _trailing)
        {
            w.Write(u);
        }

        return ms.ToArray();
    }

    private static byte[] Header(uint type, ulong uid, int size)
    {
        var item = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(item, type);
        BinaryPrimitives.WriteUInt64LittleEndian(item.AsSpan(4), uid);
        for (int i = 12; i < 52; i += 4)
        {
            BinaryPrimitives.WriteSingleLittleEndian(item.AsSpan(i), 1234.5f); // k-DOP box
        }

        item[56] = 40; // view distance
        return item;
    }
}

public sealed class SectorReaderTests
{
    [Fact]
    public void Reads_roads_prefabs_and_cities_between_unknown_items()
    {
        var b = new SectorBuilder();
        b.Junk();
        ulong road = b.Road(100, 200, 900, 200, angle: -MathF.PI / 2);
        b.Junk();
        ulong prefab = b.Prefab((1000, 0), (1100, 0), (1050, 80));
        b.City("goteborg", 1500, -300);
        b.TrailingUid();
        b.TrailingUid();

        var s = SectorReader.Read("sec+0000+0000", b.Build());

        s.ShouldNotBeNull();
        s.MapVersion.ShouldBe(907u);
        var r = s.Roads.ShouldHaveSingleItem();
        r.Uid.ShouldBe(road);
        r.A.ShouldBe(new MapPoint(100, 200));
        r.B.ShouldBe(new MapPoint(900, 200));
        r.DirA.X.ShouldBe(1f, 1e-4);
        r.DirA.Z.ShouldBe(0f, 1e-4);
        s.Prefabs.ShouldHaveSingleItem().Uid.ShouldBe(prefab);
        s.Prefabs[0].Nodes.Count.ShouldBe(3);
        s.Cities.ShouldHaveSingleItem().ShouldBe(new MapCity("goteborg", new MapPoint(1500, -300)));
    }

    [Fact]
    public void Node_facing_against_the_road_is_turned_around()
    {
        var b = new SectorBuilder();
        b.Road(0, 0, 0, -500, angle: MathF.PI); // nodes face south, road runs north
        var r = SectorReader.Read("s", b.Build())!.Roads.Single();
        r.DirA.Z.ShouldBe(-1f, 1e-4);
        r.DirB.Z.ShouldBe(-1f, 1e-4);
        r.At(0).ShouldBe(r.A);
        r.At(1).ShouldBe(r.B);
        r.At(0.5f).Z.ShouldBe(-250f, 1e-2);
        r.Bounds.MinZ.ShouldBe(-500f, 1e-3);
    }

    [Fact]
    public void Empty_sector_and_rubbish()
    {
        SectorReader.Read("s", new SectorBuilder().Build()).ShouldNotBeNull().Roads.ShouldBeEmpty();
        SectorReader.Read("s", new byte[200]).ShouldBeNull();
        SectorReader.Read("s", Encoding.UTF8.GetBytes(new string('x', 500))).ShouldBeNull();
    }

    [Fact]
    public void Tokens_round_trip()
    {
        MapToken.Decode(MapToken.Encode("euro2")).ShouldBe("euro2");
        MapToken.Decode(MapToken.Encode("gagra_abh")).ShouldBe("gagra_abh");
        MapToken.Decode(0).ShouldBeNull();
        Should.Throw<ArgumentException>(() => MapToken.Encode("Upper"));
    }

    [Fact]
    public void Sector_names_are_4000_metre_cells()
    {
        MapSector.CellOf("sec+0000-0007").ShouldBe(new MapRect(0, -28000, 4000, -24000));
        MapSector.CellOf("sec-0012+0003").ShouldBe(new MapRect(-48000, 12000, -44000, 16000));
        MapSector.CellOf("europe").ShouldBeNull();
    }
}

public sealed class MapArchiveTests
{
    private static Dictionary<string, byte[]> MapMod()
    {
        var a = new SectorBuilder();
        a.Road(0, 0, 100, 0);
        a.City("newtown", 50, 50);
        return new Dictionary<string, byte[]>
        {
            ["manifest.sii"] = Encoding.UTF8.GetBytes(Fixtures.Manifest),
            ["map/europe/sec+0000+0000.base"] = a.Build(),
            ["map/europe/sec+0000+0000.aux"] = [1, 2, 3],
            ["map/europe/sec+0001+0000.base"] = new byte[64], // unreadable
            ["map/other/sec+0000+0000.base"] = a.Build(),     // a different map: ignored
            ["def/city/newtown.mymod.sui"] = Encoding.UTF8.GetBytes("city_data : city.newtown\n{\n\tcity_name: \"Neustadt\"\n\tcity_name_localized: \"@@newtown@@\"\n}\n"),
            ["def/city.mymod.sii"] = Encoding.UTF8.GetBytes("SiiNunit\n{\ncity_data : city.other_town\n{\n city_name: \"Other\"\n}\n}\n"),
        };
    }

    [Fact]
    public void Reads_sectors_and_city_names_from_a_zip()
    {
        using var tmp = new TempDir();
        string zip = tmp.File("map.scs");
        Fixtures.WriteZip(zip, MapMod());

        var map = MapArchiveReader.Read(zip, "europe", TestContext.Current.CancellationToken);

        map.IsUnreadable.ShouldBeFalse();
        map.Sectors.Keys.ShouldBe(["sec+0000+0000"]);
        map.UnreadableSectors.ShouldBe(1);
        map.CityNames["newtown"].ShouldBe("Neustadt");
        map.CityNames["other_town"].ShouldBe("Other");
    }

    [Fact]
    public void Reads_hashfs_too_and_opaque_files_are_unreadable()
    {
        using var tmp = new TempDir();
        string hfs = tmp.File("map_hashfs.scs");
        Fixtures.WriteHashFsV1(hfs, MapMod());
        MapArchiveReader.Read(hfs, "europe", TestContext.Current.CancellationToken).Sectors.Count.ShouldBe(1);

        string opaque = tmp.File("protected.scs");
        File.WriteAllBytes(opaque, [.. "AEM!"u8, .. new byte[100]]);
        MapArchiveReader.Read(opaque, "europe", TestContext.Current.CancellationToken).IsUnreadable.ShouldBeTrue();
    }

    [Fact]
    public void Cache_returns_the_same_map_and_notices_changes()
    {
        using var tmp = new TempDir();
        string zip = tmp.File("map.scs");
        Fixtures.WriteZip(zip, MapMod());
        var cache = new MapCache(tmp.File("cache"));

        var first = cache.Get(zip, "europe", TestContext.Current.CancellationToken);
        Directory.GetFiles(tmp.File("cache/maps")).Length.ShouldBe(1);
        var second = cache.Get(zip, "europe", TestContext.Current.CancellationToken);
        second.ShouldNotBeSameAs(first);
        second.Sectors["sec+0000+0000"].Roads.ShouldBe(first.Sectors["sec+0000+0000"].Roads);
        second.Sectors["sec+0000+0000"].Cities.ShouldBe(first.Sectors["sec+0000+0000"].Cities);
        second.CityNames.ShouldBe(first.CityNames);
        second.UnreadableSectors.ShouldBe(1);

        // A changed archive is read again.
        var files = MapMod();
        files.Remove("map/europe/sec+0000+0000.base");
        File.Delete(zip);
        Fixtures.WriteZip(zip, files);
        File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddMinutes(1));
        cache.Get(zip, "europe", TestContext.Current.CancellationToken).Sectors.ShouldBeEmpty();
    }

    [Fact]
    public void A_separate_map_is_found_by_its_own_folder()
    {
        using var tmp = new TempDir();
        var sector = new SectorBuilder();
        sector.Road(0, 0, 10, 0);

        string standalone = tmp.File("utopia.scs");
        Fixtures.WriteZip(standalone, new() { ["map/utopia.mbd"] = [1], ["map/utopia/sec+0000+0000.base"] = sector.Build() });
        MapArchiveReader.FindMapName(standalone, "europe").ShouldBe("utopia");
        MapArchiveReader.Read(standalone, "utopia", TestContext.Current.CancellationToken).Sectors.Count.ShouldBe(1);

        string both = tmp.File("both.scs");
        Fixtures.WriteZip(both, new()
        {
            ["map/africa/sec+0000+0000.base"] = sector.Build(),
            ["map/africa/sec+0001+0000.base"] = sector.Build(),
            ["map/europe/sec+0000+0000.base"] = sector.Build(),
        });
        MapArchiveReader.FindMapName(both, "europe").ShouldBe("europe");

        string truck = tmp.File("truck.scs");
        Fixtures.WriteZip(truck, Fixtures.SampleFiles());
        MapArchiveReader.FindMapName(truck, "europe").ShouldBe("europe");
    }

    [Fact]
    public void Game_archives_are_base_then_dlcs()
    {
        using var tmp = new TempDir();
        foreach (string f in new[] { "dlc_north.scs", "base_map.scs", "def.scs", "dlc_east.scs", "core.scs" })
        {
            File.WriteAllBytes(tmp.File(f), []);
        }

        MapArchiveReader.GameArchives(tmp.Path).Select(Path.GetFileName).ShouldBe(["def.scs", "base_map.scs", "dlc_east.scs", "dlc_north.scs"]);
    }
}

public sealed class MapComparisonTests
{
    private static ArchiveMap Map(params (string Sector, ulong[] Roads, string[] Cities)[] sectors)
    {
        var map = new ArchiveMap();
        foreach (var (name, roads, cities) in sectors)
        {
            var s = new MapSector(name, 907);
            foreach (ulong r in roads)
            {
                s.Roads.Add(new MapRoad(r, new MapPoint(r * 10, 0), new MapPoint((r * 10) + 5, 0), new MapPoint(1, 0), new MapPoint(1, 0)));
            }

            foreach (string c in cities)
            {
                s.Cities.Add(new MapCity(c, new MapPoint(1, 1)));
            }

            map.Sectors[name] = s;
        }

        return map;
    }

    [Fact]
    public void Diffs_per_sector_against_what_is_below()
    {
        var game = new MapLayer("Base game", Map(("sec+0000+0000", [1, 2], ["oldtown"]), ("sec+0001+0000", [3], [])));
        var dlc = new MapLayer("dlc", Map(("sec+0001+0000", [3, 6], [])));
        var mod = new MapLayer("My map", Map(("sec+0000+0000", [2, 4], ["oldtown"]), ("sec+0002+0000", [5], ["newtown"]), ("sec+0001+0000", [3], [])));
        mod.Map.CityNames["newtown"] = "Neustadt";
        var above = new MapLayer("Fix", Map(("sec+0002+0000", [5, 7], [])));

        var c = MapComparison.Compare(mod, [game, dlc], [above]);

        c.AddedRoads.Select(r => r.Uid).Order().ShouldBe([4UL, 5UL]);
        c.RemovedRoads.Select(r => r.Uid).Order().ShouldBe([1UL, 6UL]);
        c.RemovedFrom.ShouldBe([new MapLayerCount("Base game", 1), new MapLayerCount("dlc", 1)], ignoreOrder: true);
        c.BackgroundRoads.Select(r => r.Uid).Order().ShouldBe([2UL, 3UL]);
        c.NewSectors.ShouldBe(1);
        c.ReplacedSectors.ShouldBe(2);
        c.HiddenBy.ShouldBe([new MapLayerCount("Fix", 1)]);
        c.HiddenCells.ShouldBe([new MapRect(8000, 0, 12000, 4000)]);
        c.Cities.Single(x => x.Name == "Neustadt").Added.ShouldBeTrue();
        c.Cities.Single(x => x.Name == "Oldtown").Added.ShouldBeFalse();
        c.AddedCities.ShouldBe(1);
        c.Focus.ShouldNotBeNull();
        c.Focus.Value.MinX.ShouldBe(10f); // removed road 1 counts as a change too
        c.Focus.Value.MaxX.ShouldBe(65f);
    }

    [Fact]
    public void A_mod_that_changes_nothing_focuses_on_its_sectors()
    {
        var game = new MapLayer("Base game", Map(("sec+0000+0000", [1], [])));
        var mod = new MapLayer("Same", Map(("sec+0000+0000", [1], [])));
        var c = MapComparison.Compare(mod, [game], []);
        c.AddedRoads.ShouldBeEmpty();
        c.RemovedRoads.ShouldBeEmpty();
        c.Focus.ShouldBe(new MapRect(0, 0, 4000, 4000));
    }

    [Fact]
    public void Network_credits_each_road_to_the_layer_that_added_it()
    {
        var game = new MapLayer("Base game", Map(("sec+0000+0000", [1, 2], ["oldtown"]), ("sec+0001+0000", [3], [])));
        var map = new MapLayer("Big map", Map(("sec+0000+0000", [1, 2, 4], ["oldtown", "newtown"]), ("sec+0002+0000", [5, 6], ["gonetown"])));
        var patch = new MapLayer("Patch", Map(("sec+0002+0000", [5, 7], [])));   // drops road 6, adds 7
        var hidden = new MapLayer("Hidden", Map(("sec+0001+0000", [3], [])));    // replaced by the layer above
        var top = new MapLayer("Top", Map(("sec+0001+0000", [3, 8], [])));

        var n = MapNetwork.Build([game, map, patch, hidden, top]);

        n.RoadsByLayer.Select(l => l.Select(r => r.Uid).Order().ToArray()).ShouldBe([[1UL, 2UL, 3UL], [4UL, 5UL], [7UL], [], [8UL]]);
        n.Layers[1].Sectors.ShouldBe(2);
        n.Layers[1].SectorsShown.ShouldBe(1); // sec+0002 is the patch's
        n.Layers[3].SectorsShown.ShouldBe(0); // all covered by "Top"
        n.Layers[3].Roads.ShouldBe(0);
        n.Cities.Single(c => c.Name == "Newtown").Owner.ShouldBe(1);
        n.Cities.Single(c => c.Name == "Oldtown").Owner.ShouldBe(0);
        n.Cities.ShouldNotContain(c => c.Name == "Gonetown"); // its sector is the patch's, which has no city
        n.Extent.ShouldNotBeNull();
    }

    [Fact]
    public void Readable_city_tokens() => MapComparison.Readable("gagra_abh").ShouldBe("Gagra Abh");

    [Fact]
    public void Unnamed_cities_with_digits_are_left_out()
    {
        var game = new MapLayer("Base game", Map(("sec+0000+0000", [1], ["g66lt30zu12", "sochi", "a1"])));
        game.Map.CityNames["a1"] = "Named One";
        var c = MapComparison.Compare(new MapLayer("Mod", new ArchiveMap()), [game], []);
        c.Cities.Select(x => x.Name).Order().ShouldBe(["Named One", "Sochi"]);
    }
}
