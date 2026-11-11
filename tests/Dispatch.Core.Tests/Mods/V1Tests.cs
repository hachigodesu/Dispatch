using Dispatch.Core.Mods;
using Dispatch.Core.Mods.Packages;
using Dispatch.Core.Safety;
using Dispatch.Core.Tests.Detection;

namespace Dispatch.Core.Tests.Mods;

public class ModInstallerTests
{
    private sealed class Setup : IDisposable
    {
        public TempDir Tmp { get; } = new();

        public List<string> Recycled { get; } = [];

        public FakePlatform Platform { get; }

        public ModInstaller Installer { get; }

        public string ModDir => Tmp.File("mod");

        public Setup()
        {
            Platform = new FakePlatform(Tmp.Path, null);
            Installer = new ModInstaller(Platform, "eurotrucks2", p =>
            {
                Recycled.Add(p);
                if (File.Exists(p)) { File.Delete(p); } else { Directory.Delete(p, true); }
            });
            Directory.CreateDirectory(ModDir);
        }

        public void Dispose() => Tmp.Dispose();
    }

    [Fact]
    public void Installs_mod_files_checked_by_content()
    {
        using var s = new Setup();
        string zip = s.Tmp.File("Cool Mod.scs");
        Fixtures.WriteZip(zip, Fixtures.SampleFiles());
        string hashfs = s.Tmp.File("Map.scs");
        Fixtures.WriteHashFsV1(hashfs, Fixtures.SampleFiles());
        string text = s.Tmp.File("readme.txt");
        File.WriteAllText(text, "hi");
        string fake = s.Tmp.File("fake.scs");
        File.WriteAllText(fake, "this is not a mod");

        var r = s.Installer.Install(s.ModDir, [zip, hashfs, text, fake], _ => false);

        r.Select(x => x.Outcome).ShouldBe([InstallOutcome.Installed, InstallOutcome.Installed, InstallOutcome.Skipped, InstallOutcome.Installed]);
        r[3].Format.ShouldBe(PackageFormat.Opaque);       // unknown content: installed, with a warning
        r[3].Message.ShouldContain("protected or unknown");
        File.Exists(Path.Combine(s.ModDir, "Cool Mod.scs")).ShouldBeTrue();
        Directory.EnumerateFiles(s.ModDir, ".dispatch-install-*").ShouldBeEmpty();
    }

    [Fact]
    public void Replaces_only_when_asked_and_recycles_the_old_copy()
    {
        using var s = new Setup();
        File.WriteAllText(Path.Combine(s.ModDir, "Cool Mod.scs"), "old version");
        Directory.CreateDirectory(Path.Combine(s.ModDir, "Cool Mod"));
        string zip = s.Tmp.File("Cool Mod.scs");
        Fixtures.WriteZip(zip, Fixtures.SampleFiles());

        s.Installer.Install(s.ModDir, [zip], _ => false)[0].Outcome.ShouldBe(InstallOutcome.Skipped);
        File.ReadAllText(Path.Combine(s.ModDir, "Cool Mod.scs")).ShouldBe("old version");

        s.Installer.Install(s.ModDir, [zip], _ => true)[0].Outcome.ShouldBe(InstallOutcome.Replaced);
        s.Recycled.Select(Path.GetFileName).ShouldBe(["Cool Mod.scs", "Cool Mod"]);
        File.ReadAllBytes(Path.Combine(s.ModDir, "Cool Mod.scs")).ShouldBe(File.ReadAllBytes(zip));
    }

    [Fact]
    public void Refuses_while_the_game_runs()
    {
        using var s = new Setup();
        s.Platform.RunningProcesses.Add("eurotrucks2");
        Should.Throw<GameRunningException>(() => s.Installer.Install(s.ModDir, [s.Tmp.File("x.scs")], _ => true));
    }

    [Fact]
    public void Uninstall_recycles_local_mods_and_refuses_workshop_items()
    {
        using var s = new Setup();
        string path = Path.Combine(s.ModDir, "a.scs");
        File.WriteAllText(path, "x");
        ModInfo Mod(ModSource src) => new("a", src, null, path, path, PackageFormat.Zip, 1, DateTime.UtcNow, null, null, 0, null, null, null, []);

        Should.Throw<InvalidOperationException>(() => s.Installer.Uninstall(Mod(ModSource.Workshop)));
        s.Installer.Uninstall(Mod(ModSource.Local));
        s.Recycled.ShouldBe([path]);
    }
}

public class ModDuplicatesTests
{
    private static ModInfo Mod(string id, string? name, string? version, int daysOld = 0, string? path = null) =>
        new(id, ModSource.Local, null, path ?? id, path ?? id, PackageFormat.Zip, 1, DateTime.UtcNow.AddDays(-daysOld),
            name is null ? null : new ModManifest(".x", version, name, null, [], null, null, [], [], null, []),
            null, 0, null, null, null, []);

    [Fact]
    public void Same_name_groups_versions_and_marks_older_copies()
    {
        var v1 = Mod("rusmap_1_9", "RusMap", "1.9");
        var v2 = Mod("rusmap_1_10", "RusMap", "1.10"); // numerically newer than 1.9
        var other = Mod("promods", "ProMods", "2.83");
        var d = ModDuplicates.Find([v1, v2, other]);

        d.ContainsKey(other).ShouldBeFalse();
        d[v1].Kind.ShouldBe(DuplicateKind.SameName);
        d[v1].IsOlder.ShouldBeTrue();
        d[v2].IsOlder.ShouldBeFalse();
        d[v1].Newest.ShouldBeSameAs(v2);
    }

    [Fact]
    public void Same_id_is_reported_and_newer_files_win_without_versions()
    {
        var file = Mod("x", "X mod", null, daysOld: 5, path: "x.scs");
        var folder = Mod("x", "X mod", null, daysOld: 1, path: "x");
        var d = ModDuplicates.Find([file, folder]);
        d[file].Kind.ShouldBe(DuplicateKind.SameId);
        d[file].IsOlder.ShouldBeTrue();
        d[folder].IsOlder.ShouldBeFalse();
    }

    [Theory]
    [InlineData("1.10", "1.9", 1)]
    [InlineData("2.83", "2.83", 0)]
    [InlineData("v1.2.0", "1.2", 0)]
    [InlineData("1.0", "", 0)]
    [InlineData("1.60-1.0.1", "1.60-1.0.2", -1)]
    public void Compares_versions_numerically(string a, string b, int expected) =>
        Math.Sign(ModDuplicates.CompareVersions(a, b)).ShouldBe(expected);
}

public class ModNotesTests
{
    [Fact]
    public void Notes_round_trip_and_empty_notes_are_removed()
    {
        using var tmp = new TempDir();
        string file = tmp.File("notes.json");
        var store = ModNotesStore.Load(file);
        store.Set("a", new ModNote(ModNote.ParseTags("map, needs promods ; Map"), "Load above RusMap"));
        store.Set("b", new ModNote(["x"], ""));
        store.Set("b", ModNote.Empty);

        var again = ModNotesStore.Load(file);
        again.Get("a").Tags.ShouldBe(["map", "needs promods"]);
        again.Get("a").Note.ShouldBe("Load above RusMap");
        again.Get("b").IsEmpty.ShouldBeTrue();
        again.AllTags.ShouldBe(["map", "needs promods"]);
    }

    [Fact]
    public void A_broken_notes_file_is_kept_aside()
    {
        using var tmp = new TempDir();
        string file = tmp.File("notes.json");
        File.WriteAllText(file, "{ not json");
        ModNotesStore.Load(file).Get("a").IsEmpty.ShouldBeTrue();
        File.Exists(file + ".broken").ShouldBeTrue();
    }
}
