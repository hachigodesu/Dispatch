using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Dispatch.Core.Mods;
using Dispatch.Core.Mods.Packages;

namespace Dispatch.Core.Tests.Mods;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("dispatch-tests-").FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal static class Fixtures
{
    public const string Manifest = """
        SiiNunit
        {
        # comment with "quotes" and : colons
        mod_package : .package_name
        {
            package_version: "1.2"
            display_name: "Test \"Mod\" 日本"
            author: "Tester" // trailing comment
            category[]: "map"
            category[]: "ui"
            icon: "icon.jpg"
            description_file: "desc.txt"
            compatible_versions[]: "1.62.*"
            compatible_version[]: "1.61.*"
            dlc_dependencies[]: "dlc_north"
            mp_mod_optional: true
        }
        }
        """;

    /// <summary>Minimal JPEG: SOI, an APP0 segment, then SOF0 declaring 276×162.</summary>
    public static byte[] Jpeg276x162 =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00,
        0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0xA2, 0x01, 0x14, 0x01, 0x01, 0x11, 0x00,
        0xFF, 0xD9,
    ];

    public static Dictionary<string, byte[]> SampleFiles() => new()
    {
        ["manifest.sii"] = Encoding.UTF8.GetBytes(Manifest),
        ["icon.jpg"] = Jpeg276x162,
        ["desc.txt"] = Encoding.UTF8.GetBytes("[orange]Hello"),
        ["def/world/thing.sii"] = Encoding.UTF8.GetBytes("SiiNunit { }"),
    };

    public static void WriteZip(string path, Dictionary<string, byte[]> files)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in files)
        {
            using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            s.Write(data);
        }
    }

    /// <summary>Zeroes every local file header signature, like "protected" mods do.</summary>
    public static void CorruptLocalHeaders(string path)
    {
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        for (int i = 0; i + 4 <= bytes.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == 0x04034b50)
            {
                bytes.AsSpan(i, 4).Clear();
            }
        }

        System.IO.File.WriteAllBytes(path, bytes);
    }

    /// <summary>Test-only HashFS v1 writer (zlib-compressed files, text directory listings).</summary>
    public static void WriteHashFsV1(string path, Dictionary<string, byte[]> files, bool fakeTableOffset = false)
    {
        var dirs = new Dictionary<string, SortedSet<string>> { [""] = [] };
        foreach (var name in files.Keys)
        {
            string child = name;
            while (true)
            {
                int slash = child.LastIndexOf('/');
                string parent = slash < 0 ? "" : child[..slash];
                string leaf = slash < 0 ? child : child[(slash + 1)..];
                bool isDir = child != name;
                if (!dirs.TryGetValue(parent, out var set))
                {
                    dirs[parent] = set = [];
                }

                set.Add(isDir ? "*" + leaf : leaf);
                if (parent.Length == 0)
                {
                    break;
                }

                child = parent;
            }
        }

        var entries = new List<(ulong Hash, byte[] Stored, uint Size, uint Flags)>();
        foreach (var (name, data) in files)
        {
            entries.Add((CityHash.Hash64(name), ZlibCompress(data), (uint)data.Length, 2));
        }

        foreach (var (dir, names) in dirs)
        {
            byte[] listing = Encoding.UTF8.GetBytes(string.Join("\n", names));
            entries.Add((CityHash.Hash64(dir), ZlibCompress(listing), (uint)listing.Length, 3));
        }

        using var fs = System.IO.File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write(0x23534353u); w.Write((ushort)1); w.Write((ushort)0); w.Write("CITY"u8.ToArray());
        w.Write((uint)entries.Count);
        long tableOffsetPos = fs.Position;
        w.Write(0UL);
        var offsets = new List<long>();
        foreach (var e in entries)
        {
            offsets.Add(fs.Position);
            w.Write(e.Stored);
        }

        long tableOffset = fs.Position;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            w.Write(e.Hash); w.Write((ulong)offsets[i]); w.Write(e.Flags); w.Write(0u);
            w.Write(e.Size); w.Write((uint)e.Stored.Length);
        }

        fs.Position = tableOffsetPos;
        w.Write(fakeTableOffset ? 0xFFFF_FFFFUL : (ulong)tableOffset);
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }
}

public class CityHashTests
{
    [Fact]
    public void Empty_input_returns_k2() => CityHash.Hash64("").ShouldBe(0x9AE16A3B2F90404FUL);

    [Fact]
    public void Matches_hash_found_in_real_game_archives() =>
        CityHash.Hash64("manifest.sii").ShouldBe(0xB97FFF7CE7377C95UL);

    [Theory]
    [InlineData("def")]
    [InlineData("def/world/a_path_between_17_and_32")]
    [InlineData("def/vehicle/truck/scania.s_2016/engine/a_path_that_is_between_33_and_64")]
    [InlineData("material/ui/radio/offline_station/a_long_path_that_exceeds_sixty_four_bytes_easily.mat")]
    public void Is_deterministic_for_all_length_classes(string path) =>
        CityHash.Hash64(path).ShouldBe(CityHash.Hash64(Encoding.UTF8.GetBytes(path)));
}

public class PackageReaderTests
{
    private static void AssertSamplePackage(IModPackage pkg)
    {
        var manifest = ModManifest.Parse(pkg.TryReadFile("manifest.sii")!);
        manifest.DisplayName.ShouldBe("Test \"Mod\" 日本");
        pkg.TryReadFile("/def/world/thing.sii").ShouldNotBeNull();
        pkg.TryReadFile("missing.txt").ShouldBeNull();
        pkg.ListDirectory("")!.ShouldBe(["def/", "desc.txt", "icon.jpg", "manifest.sii"], ignoreOrder: true);
        pkg.ListDirectory("def")!.ShouldBe(["world/"]);
    }

    [Fact]
    public void Reads_zip()
    {
        using var tmp = new TempDir();
        Fixtures.WriteZip(tmp.File("a.scs"), Fixtures.SampleFiles());
        using var pkg = ModPackage.Open(tmp.File("a.scs"));
        pkg.Format.ShouldBe(PackageFormat.Zip);
        AssertSamplePackage(pkg);
    }

    [Fact]
    public void Reads_zip_with_corrupted_local_headers()
    {
        using var tmp = new TempDir();
        Fixtures.WriteZip(tmp.File("locked.scs"), Fixtures.SampleFiles());
        Fixtures.CorruptLocalHeaders(tmp.File("locked.scs"));
        using var pkg = ModPackage.Open(tmp.File("locked.scs"));
        AssertSamplePackage(pkg);
    }

    [Fact]
    public void Detects_format_by_header_not_extension()
    {
        using var tmp = new TempDir();
        Fixtures.WriteZip(tmp.File("really_a_zip.scs"), Fixtures.SampleFiles());
        Fixtures.WriteHashFsV1(tmp.File("really_hashfs.zip"), Fixtures.SampleFiles());
        File.WriteAllBytes(tmp.File("protected.scs"), "AEM!\0\0garbage"u8.ToArray());

        using (var a = ModPackage.Open(tmp.File("really_a_zip.scs"))) { a.Format.ShouldBe(PackageFormat.Zip); }
        using (var b = ModPackage.Open(tmp.File("really_hashfs.zip"))) { b.Format.ShouldBe(PackageFormat.HashFsV1); }
        using (var c = ModPackage.Open(tmp.File("protected.scs"))) { c.Format.ShouldBe(PackageFormat.Opaque); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reads_hashfs_v1(bool fakeTableOffset)
    {
        using var tmp = new TempDir();
        Fixtures.WriteHashFsV1(tmp.File("a.scs"), Fixtures.SampleFiles(), fakeTableOffset);
        using var pkg = ModPackage.Open(tmp.File("a.scs"));
        pkg.Format.ShouldBe(PackageFormat.HashFsV1);
        AssertSamplePackage(pkg);
    }

    [Fact]
    public void Reads_folder_mod()
    {
        using var tmp = new TempDir();
        foreach (var (name, data) in Fixtures.SampleFiles())
        {
            string full = Path.Combine(tmp.Path, name);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, data);
        }

        using var pkg = ModPackage.Open(tmp.Path);
        pkg.Format.ShouldBe(PackageFormat.Folder);
        AssertSamplePackage(pkg);
    }
}

public class ManifestTests
{
    [Fact]
    public void Parses_all_fields_and_flags_unknown_ones()
    {
        var m = ModManifest.Parse(Encoding.UTF8.GetBytes(Fixtures.Manifest));
        m.UnitName.ShouldBe(".package_name");
        m.PackageVersion.ShouldBe("1.2");
        m.Author.ShouldBe("Tester");
        m.Categories.ShouldBe(["map", "ui"]);
        m.IconFile.ShouldBe("icon.jpg");
        m.DescriptionFile.ShouldBe("desc.txt");
        m.CompatibleVersions.ShouldBe(["1.62.*"]);
        m.DlcDependencies.ShouldBe(["dlc_north"]);
        m.MpModOptional.ShouldBe(true);
        m.UnknownAttributes.ShouldBe(["compatible_version[]"]);
    }

    [Fact]
    public void Display_name_is_optional()
    {
        var m = ModManifest.Parse("SiiNunit { mod_package : .x { author: \"a\" } }"u8.ToArray());
        m.DisplayName.ShouldBeNull();
    }

    [Fact]
    public void Reads_jpeg_dimensions() =>
        ImageInfo.TryGetJpegSize(Fixtures.Jpeg276x162).ShouldBe((276, 162));
}

public class VersionMatchTests
{
    [Theory]
    [InlineData("1.62.*", "1.62.2.1", true)]
    [InlineData("1.62.*", "1.62.0", true)]
    [InlineData("1.62.2.*", "1.62.2.1", true)]
    [InlineData("1.61.*", "1.62.2.1", false)]
    [InlineData("1.6*", "1.62.2.1", true)]
    [InlineData("*", "1.62.2.1", false)]
    [InlineData("1.*.2", "1.62.2.1", false)]
    [InlineData("1.62", "1.62.2.1", true)]
    [InlineData("1.6", "1.62.2.1", false)]
    [InlineData("1.62.2.1s", "1.62.2.1", true)]
    public void Matches_game_rules(string pattern, string version, bool expected) =>
        GameVersionMatch.Matches(pattern, version).ShouldBe(expected);

    [Fact]
    public void Versions_sii_selects_matching_entry_then_universal()
    {
        const string text = """
            SiiNunit
            {
            package_version_info : .universal { package_name: "universal" }
            package_version_info : .v160 { package_name: "160" compatible_versions[]: "1.60.*" }
            }
            """;
        // Single-line units are not supported by the line-based reader; use the multi-line form the game writes.
        const string multiLine = """
            SiiNunit
            {
            package_version_info : .universal
            {
                package_name: "universal"
            }
            package_version_info : .v160
            {
                package_name: "160"
                compatible_versions[]: "1.60.*"
            }
            }
            """;
        _ = text;
        var entries = VersionsEntry.Parse(multiLine);
        entries.Count.ShouldBe(2);
        VersionsEntry.Select(entries, "1.60.3.1")!.PackageName.ShouldBe("160");
        VersionsEntry.Select(entries, "1.62.2.1")!.PackageName.ShouldBe("universal");
    }
}
