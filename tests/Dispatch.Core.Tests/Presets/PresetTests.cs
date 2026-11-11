using Dispatch.Core.Mods;
using Dispatch.Core.Mods.Packages;
using Dispatch.Core.Presets;
using Dispatch.Core.Profiles;
using Dispatch.Core.Tests.Mods;

namespace Dispatch.Core.Tests.Presets;

public class PresetTests
{
    private static ModInfo Mod(string id, string path, ModSource source = ModSource.Local) =>
        new(id, source, ModInfo.TryParseWorkshopId(id), path, path, PackageFormat.Zip, 1, DateTime.UtcNow,
            new ModManifest(".x", "2.83", id, null, [], null, null, [], [], null, []), null, 0, null, null, null, []);

    private static readonly ActiveModEntry[] TopFirst =
    [
        new("promods-eu-def-v283", "ProMods Definition Package"),
        new("mod_workshop_package.00000000C3CC68CD", "RPM - QuickStart"),
        new("gone", "A mod that isn't installed"),
    ];

    private static Dictionary<string, ModInfo> Installed() => new()
    {
        ["promods-eu-def-v283"] = Mod("promods-eu-def-v283", @"C:\mod\promods-eu-def-v283.scs"),
        ["mod_workshop_package.00000000C3CC68CD"] = Mod("mod_workshop_package.00000000C3CC68CD", @"C:\ws\3284953293", ModSource.Workshop),
    };

    [Fact]
    public void Create_save_load_round_trip()
    {
        using var tmp = new TempDir();
        var preset = Preset.Create("ProMods 2.83", TopFirst, Installed(), "1.62.2.1", "notes");
        string file = tmp.File("p" + Preset.FileExtension);
        preset.Save(file);

        var loaded = Preset.Load(file);
        loaded.Name.ShouldBe("ProMods 2.83");
        loaded.Mods.Select(m => m.PackageId).ShouldBe(TopFirst.Select(e => e.PackageId));
        loaded.Mods[0].FileName.ShouldBe("promods-eu-def-v283.scs");
        loaded.Mods[0].PackageVersion.ShouldBe("2.83");
        loaded.Mods[1].WorkshopId.ShouldBe(3284953293UL);
        File.ReadAllText(file).ShouldContain("\"schema\": 1");
        File.ReadAllText(file).ShouldNotContain("\"fileName\": null"); // nulls are left out
    }

    [Fact]
    public void Resolves_exact_by_file_name_and_missing()
    {
        var preset = Preset.Create("p", TopFirst, Installed(), null);
        var installed = new[]
        {
            Mod("promods-def-renamed", @"C:\mod\promods-eu-def-v283.scs"), // same file, other ID
            Mod("mod_workshop_package.00000000C3CC68CD", @"C:\ws\3284953293", ModSource.Workshop),
        };

        var r = PresetResolver.Resolve(preset, installed);
        r.Select(x => x.Match).ShouldBe([PresetMatch.ByFileName, PresetMatch.Exact, PresetMatch.Missing]);
        r[0].ToActiveMod()!.PackageId.ShouldBe("promods-def-renamed");
        r[1].WorkshopUrl.ShouldBe("https://steamcommunity.com/sharedfiles/filedetails/?id=3284953293");
        r[2].ToActiveMod().ShouldBeNull();
    }

    [Theory]
    [InlineData("""{ "schema": 1, "game": "ets2", "name": "x", "mods": [ { "packageId": "a|b" } ] }""", "invalid mod entry")]
    [InlineData("""{ "schema": 99, "game": "ets2", "name": "x", "mods": [] }""", "newer version")]
    [InlineData("""{ "schema": 1, "game": "ats", "name": "x", "mods": [] }""", "another game")]
    [InlineData("""not json""", "isn't a valid")]
    [InlineData("""{ "schema": 1, "game": "ets2", "mods": [] }""", "isn't a valid")]
    public void Rejects_bad_files(string json, string reason)
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("bad.json"), json);
        Should.Throw<InvalidDataException>(() => Preset.Load(tmp.File("bad.json"))).Message.ShouldContain(reason);
    }

    [Fact]
    public void Ignores_unknown_fields_from_later_minor_versions()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("p.json"), """
            { "schema": 1, "app": "Dispatch", "game": "ets2", "name": "x", "future": true,
              "mods": [ { "packageId": "a", "displayName": "A", "extra": 1 } ] }
            """);
        Preset.Load(tmp.File("p.json")).Mods.Single().DisplayName.ShouldBe("A");
    }
}
