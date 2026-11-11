using System.Text;
using Dispatch.Core.Detection;
using Dispatch.Core.Games;
using Dispatch.Core.Mods;
using Dispatch.Core.Mods.Packages;
using Dispatch.Core.Profiles;
using Dispatch.Core.Tests.Mods;

namespace Dispatch.Core.Tests.Detection;

public class ModScannerTests
{
    private static void AddMods(FakePc pc)
    {
        string mod = @"Documents\Euro Truck Simulator 2\mod\";
        Fixtures.WriteZip(Path.Combine(pc.Root, mod + "zipped.scs"), Fixtures.SampleFiles());
        Fixtures.WriteHashFsV1(Path.Combine(pc.Root, mod + "hashed.scs"), Fixtures.SampleFiles());
        pc.File(mod + "protected.scs", "AEM!\0\0 not readable"u8.ToArray());
        pc.File(mod + "readme.txt", "ignored: not a mod extension");
        pc.File(mod + @"folder_mod\manifest.sii", "SiiNunit { mod_package : .x {\n display_name: \"Folder Mod\"\n category[]: \"maps\"\n } }");

        string ws = @"SteamLibrary2\steamapps\workshop\content\227300\3284953293\";
        pc.File(ws + "versions.sii", "SiiNunit\n{\npackage_version_info : .universal\n{\n package_name: \"universal\"\n}\n}\n");
        foreach (var (name, data) in Fixtures.SampleFiles())
        {
            pc.File(ws + @"universal\" + name.Replace('/', '\\'), data);
        }
    }

    [Fact]
    public void Scans_local_and_workshop_mods_of_every_format()
    {
        using var pc = new FakePc();
        AddMods(pc);
        var env = GameDetector.Detect(pc.Platform(), GameDefinition.Ets2);
        var result = new ModScanner(Path.Combine(pc.Root, "cache")).Scan(env, cancel: TestContext.Current.CancellationToken);

        var byId = result.Mods.ToDictionary(m => m.PackageId);
        byId.Keys.ShouldBe(["zipped", "hashed", "protected", "folder_mod", "mod_workshop_package.00000000C3CC68CD"], ignoreOrder: true);
        byId["zipped"].Format.ShouldBe(PackageFormat.Zip);
        byId["hashed"].Format.ShouldBe(PackageFormat.HashFsV1);
        byId["folder_mod"].Format.ShouldBe(PackageFormat.Folder);
        byId["protected"].Format.ShouldBe(PackageFormat.Opaque);
        byId["protected"].Issues.ShouldContain(i => i.Message.Contains("Protected", StringComparison.Ordinal));

        var zipped = byId["zipped"];
        zipped.DisplayName.ShouldBe("Test \"Mod\" 日本");
        zipped.IconWidth.ShouldBe(276);
        File.Exists(zipped.IconFile).ShouldBeTrue();
        zipped.Issues.ShouldContain(i => i.Message.Contains("compatible_versions[]", StringComparison.Ordinal));
        ModScanner.ReadDescription(zipped).ShouldBe("[orange]Hello");

        byId["folder_mod"].Issues.ShouldContain(i => i.Message.Contains("'maps'", StringComparison.Ordinal));

        var ws = byId["mod_workshop_package.00000000C3CC68CD"];
        ws.Source.ShouldBe(ModSource.Workshop);
        ws.WorkshopId.ShouldBe(3284953293UL);
        ws.SubPackage.ShouldBe("universal");
        ws.Manifest.ShouldNotBeNull();
    }

    [Fact]
    public void Second_scan_comes_from_cache_until_a_file_changes()
    {
        using var pc = new FakePc();
        AddMods(pc);
        var env = GameDetector.Detect(pc.Platform(), GameDefinition.Ets2);
        var scanner = new ModScanner(Path.Combine(pc.Root, "cache"));
        scanner.Scan(env, cancel: TestContext.Current.CancellationToken).FromCache.ShouldBe(0);
        scanner.Scan(env, cancel: TestContext.Current.CancellationToken).FromCache.ShouldBe(5);

        File.SetLastWriteTimeUtc(Path.Combine(pc.UserData, "mod", "zipped.scs"), DateTime.UtcNow.AddMinutes(1));
        scanner.Scan(env, cancel: TestContext.Current.CancellationToken).FromCache.ShouldBe(4);
    }

    [Fact]
    public void Workshop_package_ids_round_trip() =>
        ModInfo.TryParseWorkshopId(ModInfo.WorkshopPackageId(3284953293)).ShouldBe(3284953293UL);
}

public class CompatibilityTests
{
    private static ModInfo Mod(string[] versions, string[]? dlc = null, ModSource source = ModSource.Local, bool manifest = true) =>
        new("m", source, null, "p", "p", PackageFormat.Zip, 1, DateTime.UtcNow,
            manifest ? new ModManifest(".x", null, "M", null, [], null, null, versions, dlc ?? [], null, []) : null,
            null, 0, null, null, null, []);

    private static readonly GameVersion V162 = new(1, 62, 2, 1);
    private static readonly HashSet<string> Dlc = ["dlc_north", "dlc_east"];

    [Fact]
    public void Matching_version_is_ok() =>
        CompatibilityChecker.Evaluate(Mod(["1.62.*"]), V162, Dlc, null).Status.ShouldBe(CompatStatus.Ok);

    [Fact]
    public void Older_version_is_a_mismatch()
    {
        var c = CompatibilityChecker.Evaluate(Mod(["1.60.*", "1.61.*"]), V162, Dlc, null);
        c.Status.ShouldBe(CompatStatus.VersionMismatch);
        c.Detail.ShouldBe("Made for 1.60.*, 1.61.*; you have 1.62.");
    }

    [Fact]
    public void Missing_dlc_wins_over_version() =>
        CompatibilityChecker.Evaluate(Mod(["1.62.*"], ["dlc_north", "dlc_iberia"]), V162, Dlc, null)
            .MissingDlc.ShouldBe(["dlc_iberia"]);

    [Fact]
    public void No_versions_declared() =>
        CompatibilityChecker.Evaluate(Mod([]), V162, Dlc, null).Status.ShouldBe(CompatStatus.NoVersionInfo);

    [Fact]
    public void Workshop_items_are_handled_by_versions_sii() =>
        CompatibilityChecker.Evaluate(Mod(["1.40.*"], source: ModSource.Workshop), V162, Dlc, null).Status.ShouldBe(CompatStatus.Ok);

    [Fact]
    public void Unreadable_mod_uses_the_games_own_verdict()
    {
        var log = new GameLogInfo(V162, new HashSet<string> { "m" }, DateTime.UtcNow);
        CompatibilityChecker.Evaluate(Mod([], manifest: false) with { Format = PackageFormat.Opaque }, V162, Dlc, log)
            .Status.ShouldBe(CompatStatus.VersionMismatch);
        CompatibilityChecker.Evaluate(Mod([], manifest: false) with { Format = PackageFormat.Opaque }, V162, Dlc, null)
            .Status.ShouldBe(CompatStatus.Unreadable);
    }

    [Fact]
    public void Disagreement_with_the_game_log_is_noted()
    {
        var log = new GameLogInfo(V162, new HashSet<string> { "m" }, DateTime.UtcNow);
        CompatibilityChecker.Evaluate(Mod(["1.62.*"]), V162, Dlc, log).Detail.ShouldContain("log disagrees");
    }
}

public class ProfileRepositoryTests
{
    private static string ProfileText(string name, params string[] fileOrder)
    {
        var sb = new StringBuilder("SiiNunit\r\n{\r\nuser_profile : _nameless.1.2 {\r\n");
        sb.Append(" active_mods: ").Append(fileOrder.Length).Append("\r\n");
        for (int i = 0; i < fileOrder.Length; i++)
        {
            sb.Append(" active_mods[").Append(i).Append("]: \"").Append(fileOrder[i]).Append("\"\r\n");
        }

        sb.Append(" profile_name: ").Append(Dispatch.Core.Sii.Text.SiiString.Format(name)).Append("\r\n}\r\n\r\n}\r\n");
        return sb.ToString();
    }

    [Fact]
    public void Lists_local_and_cloud_profiles_and_ignores_everything_else()
    {
        using var pc = new FakePc();
        string local = ProfileNameCodec.ToFolderName("Promods");
        pc.File($@"Documents\Euro Truck Simulator 2\profiles\{local}\profile.sii", ProfileText("Promods"));
        pc.Dir($@"Documents\Euro Truck Simulator 2\profiles\{local}\save\1");
        pc.Dir($@"Documents\Euro Truck Simulator 2\profiles\{local}\save\autosave");
        pc.File(@"Documents\Euro Truck Simulator 2\profiles\.dispatch-staging-abc\profile.sii", "x");
        pc.File(@"Documents\Euro Truck Simulator 2\profiles\NOTHEX\profile.sii", "x");
        pc.Dir($@"Documents\Euro Truck Simulator 2\profiles\{ProfileNameCodec.ToFolderName("Empty")}"); // no profile.sii
        pc.File($@"Documents\Euro Truck Simulator 2\profiles(1.62.2.1s).bak\{local}\profile.sii", "old");

        string cloud = ProfileNameCodec.ToFolderName("日本");
        pc.File($@"Steam\userdata\121\227300\remote\profiles\{cloud}\profile.sii", ProfileText("日本"));
        pc.Dir($@"Documents\Euro Truck Simulator 2\steam_profiles\{cloud}");

        var env = GameDetector.Detect(pc.Platform(), GameDefinition.Ets2);
        var profiles = ProfileRepository.List(env);

        profiles.Select(p => (p.Name, p.Kind)).ShouldBe([("Promods", ProfileKind.Local), ("日本", ProfileKind.SteamCloud)]);
        profiles[0].SaveCount.ShouldBe(2);
        profiles[0].IsReadOnly.ShouldBeFalse();
        profiles[1].IsReadOnly.ShouldBeTrue();
        profiles[1].Owner!.PersonaName.ShouldBe("Alice");
        profiles[1].LocalPartDir.ShouldEndWith($@"steam_profiles\{cloud}");
    }

    [Fact]
    public void Load_order_is_returned_top_first()
    {
        using var pc = new FakePc();
        string folder = ProfileNameCodec.ToFolderName("Test");
        // File order: [0] = bottom of the in-game list.
        pc.File($@"Documents\Euro Truck Simulator 2\profiles\{folder}\profile.sii",
                ProfileText("Test", "bottom|Bottom Mod", "middle|Middle Mod", "top|Top Mod"));
        var env = GameDetector.Detect(pc.Platform(), GameDefinition.Ets2);
        var read = ProfileRepository.ReadLoadOrder(ProfileRepository.List(env).Single());

        read.TopFirst.Select(e => e.PackageId).ShouldBe(["top", "middle", "bottom"]);
        read.Layers.ShouldBe([Dispatch.Core.Sii.SiiFormat.Text]);
    }
}
