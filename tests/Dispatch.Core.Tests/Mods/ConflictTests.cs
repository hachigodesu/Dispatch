using System.Text;
using Dispatch.Core.Mods;
using Dispatch.Core.Mods.Packages;

namespace Dispatch.Core.Tests.Mods;

public class ConflictTests
{
    private static Dictionary<string, byte[]> Files(params string[] paths)
    {
        var files = paths.ToDictionary(p => p, p => Encoding.UTF8.GetBytes("data of " + p));
        files["manifest.sii"] = "SiiNunit { mod_package : .x {\n display_name: \"X\"\n} }"u8.ToArray();
        files["icon.jpg"] = Fixtures.Jpeg276x162;
        return files;
    }

    private static ModInfo Info(string id, string path) =>
        new(id, ModSource.Local, null, path, path, PackageFormat.Zip, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path),
            null, null, 0, null, null, null, []);

    [Fact]
    public void Hashes_match_across_zip_and_hashfs_and_metadata_is_excluded()
    {
        using var tmp = new TempDir();
        Fixtures.WriteZip(tmp.File("a.scs"), Files("def/world/city.sii", "material/x.mat"));
        Fixtures.WriteHashFsV1(tmp.File("b.scs"), Files("def/world/city.sii", "map/europe.mbd"));

        var a = ModFileIndex.Build(Info("a", tmp.File("a.scs")) with { Format = PackageFormat.Zip });
        var b = ModFileIndex.Build(Info("b", tmp.File("b.scs")) with { Format = PackageFormat.HashFsV1 });

        a.Hashes.Length.ShouldBe(2); // manifest.sii and icon.jpg excluded
        b.Hashes.Length.ShouldBe(2);
        a.Hashes.Intersect(b.Hashes).ShouldBe([ModPackage.PathHash("def/world/city.sii")]);
    }

    [Fact]
    public void Higher_mod_wins_and_pairs_are_counted()
    {
        static ModFileSet Set(string id, params string[] paths) =>
            new(id, paths.Select(ModPackage.PathHash).Order().ToArray());

        var top = Set("patch", "def/a.sii", "def/b.sii");
        var mid = Set("map", "def/a.sii", "def/b.sii", "def/c.sii", "map/x.mbd");
        var bottom = Set("base", "def/c.sii", "def/a.sii", "other/y.sii");
        var report = ConflictAnalyzer.Analyze([top, mid, bottom]);

        report.Pairs.Select(p => (p.WinnerId, p.LoserId, p.FileCount))
              .ShouldBe([("patch", "map", 2), ("map", "base", 1), ("patch", "base", 1)], ignoreOrder: true);
        report.Overrides("patch").Sum(p => p.FileCount).ShouldBe(3);
        report.OverriddenBy("base").Select(p => p.WinnerId).ShouldBe(["map", "patch"], ignoreOrder: true);
        report.OverriddenBy("patch").ShouldBeEmpty();

        // Reversing the order flips who wins: patch now loses a.sii to base and b.sii to map.
        var reversed = ConflictAnalyzer.Analyze([bottom, mid, top]);
        reversed.OverriddenBy("patch").Sum(p => p.FileCount).ShouldBe(2);
        reversed.Overrides("patch").ShouldBeEmpty();
        reversed.Pairs.Select(p => (p.WinnerId, p.LoserId, p.FileCount))
                .ShouldBe([("base", "map", 2), ("base", "patch", 1), ("map", "patch", 1)], ignoreOrder: true);

        // Whatever the order, a file in k mods hides k-1 copies.
        reversed.Pairs.Sum(p => p.FileCount).ShouldBe(report.Pairs.Sum(p => p.FileCount));
    }

    [Fact]
    public void Resolves_example_names()
    {
        using var tmp = new TempDir();
        Fixtures.WriteHashFsV1(tmp.File("b.scs"), Files("def/world/city.sii", "map/europe.mbd"));
        var mod = Info("b", tmp.File("b.scs")) with { Format = PackageFormat.HashFsV1 };
        var names = ModFileIndex.ResolveNames(mod, [ModPackage.PathHash("def/world/city.sii")]);
        names.Values.ShouldBe(["def/world/city.sii"]);
    }

    [Fact]
    public void Opaque_mods_have_no_known_files() =>
        ModFileIndex.Build(new ModInfo("x", ModSource.Local, null, "x", "x", PackageFormat.Opaque, 1, DateTime.UtcNow,
                                       null, null, 0, null, null, null, [])).Hashes.ShouldBeEmpty();
}
