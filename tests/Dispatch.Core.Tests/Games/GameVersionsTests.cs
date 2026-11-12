using Dispatch.Core.Detection;
using Dispatch.Core.Games;
using Dispatch.Core.Mods;
using Dispatch.Core.Mods.Packages;
using Dispatch.Core.Tests.Detection;
using Dispatch.Core.Tests.Mods;
using Shouldly;

namespace Dispatch.Core.Tests.Versions;

public sealed class VersionPickerTests
{
    private static readonly GameInstallation Steam162 = new(@"C:\Steam\ETS2", GameVersion.TryParse("1.62.2.1"), IsSteam: true);
    private static readonly GameInstallation Copy160 = new(@"D:\ETS2 1.60", GameVersion.TryParse("1.60.3.1"), IsSteam: false);
    private static readonly GameInstallation Copy161 = new(@"D:\ETS2 1.61", GameVersion.TryParse("1.61.1.5"), IsSteam: false);

    internal static ModInfo Mod(string id, params string[] versions) => Mod(id, ModSource.Local, versions);

    internal static ModInfo Mod(string id, ModSource source, params string[] versions) =>
        new(id, source, null, id, id, PackageFormat.Zip, 1, DateTime.UtcNow,
            new ModManifest(".package_name", "1", id, null, [], null, null, versions, [], null, []),
            null, 0, null, null, null, []);

    [Fact]
    public void Picks_the_copy_all_mods_are_made_for()
    {
        var choice = VersionPicker.Pick([Steam162, Copy160, Copy161], [Mod("promods", "1.60.*"), Mod("rusmap", "1.60")]);
        choice!.Installation.ShouldBe(Copy160);
        choice.Mismatches.ShouldBe(0);
        choice.Checked.ShouldBe(2);
    }

    [Fact]
    public void Picks_the_newest_when_every_version_fits()
    {
        VersionPicker.Pick([Copy160, Steam162], [Mod("a", "1.60.*", "1.62.*")])!.Installation.ShouldBe(Steam162);
        VersionPicker.Pick([Copy160, Steam162], [Mod("no versions")])!.Installation.ShouldBe(Steam162);
        VersionPicker.Pick([Copy160, Steam162], [])!.Installation.ShouldBe(Steam162);
    }

    [Fact]
    public void Picks_the_fewest_mismatches_otherwise()
    {
        var mods = new[] { Mod("a", "1.60.*"), Mod("b", "1.60.*"), Mod("c", "1.62.*") };
        var choice = VersionPicker.Pick([Steam162, Copy160], mods)!;
        choice.Installation.ShouldBe(Copy160);
        choice.Mismatches.ShouldBe(1);
    }

    [Fact]
    public void Workshop_items_and_undeclared_mods_dont_count()
    {
        var mods = new[] { Mod("ws", ModSource.Workshop, "1.60.*"), Mod("none") };
        var choice = VersionPicker.Pick([Copy160, Steam162], mods)!;
        choice.Installation.ShouldBe(Steam162);
        choice.Checked.ShouldBe(0);
    }

    [Fact]
    public void Same_version_prefers_steam_and_unknown_versions_are_a_last_resort()
    {
        var copyOfSame = Steam162 with { Folder = @"D:\copy", IsSteam = false };
        VersionPicker.Pick([copyOfSame, Steam162], [])!.Installation.ShouldBe(Steam162);

        var unknown = new GameInstallation(@"D:\unknown", null, IsSteam: false);
        VersionPicker.Pick([unknown, Copy160], [Mod("a", "1.62.*")])!.Installation.ShouldBe(Copy160);
        VersionPicker.Pick([unknown], [])!.Installation.ShouldBe(unknown);
        VersionPicker.Pick([], []).ShouldBeNull();
    }

    [Fact]
    public void Versions_compare_numerically()
    {
        VersionComparer.Instance.Compare(GameVersion.TryParse("1.9"), GameVersion.TryParse("1.10")).ShouldBeLessThan(0);
        VersionComparer.Instance.Compare(GameVersion.TryParse("1.62.2.1"), GameVersion.TryParse("1.62.2.1")).ShouldBe(0);
        VersionComparer.Instance.Compare(GameVersion.TryParse("1.62.10"), GameVersion.TryParse("1.62.9.9")).ShouldBeGreaterThan(0);
    }
}

public sealed class GameInstallationsTests
{
    [Fact]
    public void Reads_a_copy_and_rejects_folders_without_the_game()
    {
        using var tmp = new TempDir();
        string copy = Path.Combine(tmp.Path, "ETS2 1.60");
        Directory.CreateDirectory(Path.Combine(copy, @"bin\win_x64"));
        File.WriteAllText(Path.Combine(copy, @"bin\win_x64\eurotrucks2.exe"), "");

        var read = GameInstallations.ReadCopy(GameDefinition.Ets2, copy, _ => GameVersion.TryParse("1.60.3.1"));
        read.ShouldNotBeNull();
        read.IsSteam.ShouldBeFalse();
        read.Version.ShouldBe(GameVersion.TryParse("1.60.3.1"));
        read.ExePath(GameDefinition.Ets2).ShouldBe(Path.Combine(copy, @"bin\win_x64\eurotrucks2.exe"));

        GameInstallations.ReadCopy(GameDefinition.Ats, copy).ShouldBeNull();
        GameInstallations.ReadCopy(GameDefinition.Ets2, tmp.Path).ShouldBeNull();
    }

    [Theory]
    [InlineData("public", "4", null, true)]
    [InlineData("temporary_1_60", "4", "temporary_1_60", true)]
    [InlineData("temporary_1_60", "1026", "temporary_1_60", false)]
    [InlineData("public", "6", null, true)] // an update is queued but hasn't started: the files are complete
    [InlineData("public", "1048582", null, false)] // downloading
    [InlineData("public", "38", null, false)] // files missing
    [InlineData("public", "2", null, false)] // not installed
    public void Reads_branch_and_state_from_the_app_manifest(string betaKey, string flags, string? branch, bool full)
    {
        using var pc = new FakePc();
        pc.File(@"SteamLibrary2\steamapps\appmanifest_227300.acf", $$"""
            "AppState"
            {
            	"appid"		"227300"
            	"StateFlags"		"{{flags}}"
            	"installdir"		"Euro Truck Simulator 2"
            	"SizeOnDisk"		"41265535367"
            	"UserConfig" { "BetaKey" "{{betaKey}}" }
            	"MountedConfig" { "BetaKey" "{{betaKey}}" }
            }
            """);

        var state = GameInstallations.ReadSteamState(GameDefinition.Ets2, pc.Install!);
        state.ShouldNotBeNull();
        state.Branch.ShouldBe(branch);
        state.FullyInstalled.ShouldBe(full);
        state.SizeOnDisk.ShouldBe(41265535367);
    }

    [Fact]
    public void Launching_a_copy_starts_its_exe_with_the_steam_app_id()
    {
        var copy = new GameInstallation(@"D:\ETS2 1.60", null, IsSteam: false);
        var start = GameLauncher.Directly(GameDefinition.Ets2, copy);
        start.FileName.ShouldBe(@"D:\ETS2 1.60\bin\win_x64\eurotrucks2.exe");
        start.WorkingDirectory.ShouldBe(@"D:\ETS2 1.60\bin\win_x64");
        start.UseShellExecute.ShouldBeFalse();
        start.Environment["SteamAppId"].ShouldBe("227300");

        GameLauncher.ThroughSteam(GameDefinition.Ats).FileName.ShouldBe("steam://rungameid/270880");
    }
}
