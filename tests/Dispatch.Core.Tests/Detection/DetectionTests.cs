using Dispatch.Core.Detection;
using Dispatch.Core.Games;
using Dispatch.Core.Platform;
using Dispatch.Core.Tests.Mods;

namespace Dispatch.Core.Tests.Detection;

internal sealed class FakePlatform(string documents, string? steam, uint? activeAccount = null) : IPlatform
{
    public HashSet<string> RunningProcesses { get; } = [];

    public string DocumentsFolder { get; } = documents;

    public string LocalAppDataFolder { get; set; } = Path.Combine(documents, "..", "LocalAppData");

    public string? SteamPath { get; } = steam;

    public uint? ActiveSteamAccountId { get; } = activeAccount;

    public bool IsProcessRunning(string processName) => RunningProcesses.Contains(processName);
}

/// <summary>Builds a fake PC: Documents, a Steam install and a second Steam library holding the game.</summary>
internal sealed class FakePc : IDisposable
{
    private readonly TempDir _tmp = new();

    public FakePc(bool withGame = true)
    {
        Documents = Dir("Documents");
        UserData = Dir(@"Documents\Euro Truck Simulator 2");
        Dir(@"Documents\Euro Truck Simulator 2\mod");
        Dir(@"Documents\Euro Truck Simulator 2\profiles");
        Steam = Dir("Steam");
        Library2 = Dir("SteamLibrary2");
        File(@"Steam\steamapps\libraryfolders.vdf", $$"""
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"{{Steam.Replace(@"\", @"\\", StringComparison.Ordinal)}}"
            		"apps"		{ "730" "1" }
            	}
            	"1"
            	{
            		"path"		"{{Library2.Replace(@"\", @"\\", StringComparison.Ordinal)}}"
            		"apps"		{ "227300" "41265535367" }
            	}
            }
            """);
        File(@"Steam\config\loginusers.vdf", """
            "users"
            {
            	"76561197960265849"  // account 121
            	{
            		"AccountName"	"alice_login"
            		"PersonaName"	"Alice"
            		"MostRecent"	"1"
            	}
            	"76561197960265850"
            	{
            		"PersonaName"	"Bob"
            		"MostRecent"	"0"
            	}
            }
            """);
        Dir(@"Steam\userdata\121\227300\remote\profiles");
        Dir(@"Steam\userdata\122\227300");
        Dir(@"Steam\userdata\999\730"); // has no ETS2 data → not listed
        if (withGame)
        {
            File(@"SteamLibrary2\steamapps\appmanifest_227300.acf", """
                "AppState"
                {
                	"appid"		"227300"
                	"installdir"		"Euro Truck Simulator 2"
                	"buildid"		"26552251"
                }
                """);
            Install = Dir(@"SteamLibrary2\steamapps\common\Euro Truck Simulator 2");
            File(@"SteamLibrary2\steamapps\common\Euro Truck Simulator 2\bin\win_x64\eurotrucks2.exe", "not a real exe");
            File(@"SteamLibrary2\steamapps\common\Euro Truck Simulator 2\dlc_north.scs", "");
            File(@"SteamLibrary2\steamapps\common\Euro Truck Simulator 2\dlc_east.scs", "");
            File(@"SteamLibrary2\steamapps\common\Euro Truck Simulator 2\base.scs", "");
            Dir(@"SteamLibrary2\steamapps\workshop\content\227300\3284953293");
        }

        File(@"Documents\Euro Truck Simulator 2\game.log.txt", """
            00:00:00.799 : [ufs] Loaded pack set version 1.62.2.1 created at 1794263405
            00:00:27.537 : <WARNING> [mods] Incompatible mod - old_map
            00:00:27.556 : <WARNING> [mods] Incompatible mod - Name With Spaces 1.0
            """);
    }

    public string Root => _tmp.Path;

    public string Documents { get; }

    public string UserData { get; }

    public string Steam { get; }

    public string Library2 { get; }

    public string? Install { get; }

    public FakePlatform Platform(uint? activeAccount = null) => new(Documents, Steam, activeAccount);

    public string Dir(string relative) => Directory.CreateDirectory(Path.Combine(_tmp.Path, relative)).FullName;

    public string File(string relative, string content)
    {
        string full = Path.Combine(_tmp.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public string File(string relative, byte[] content)
    {
        string full = Path.Combine(_tmp.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, content);
        return full;
    }

    public void Dispose() => _tmp.Dispose();
}

public class VdfTests
{
    [Fact]
    public void Parses_nested_quoted_bare_comments_and_escapes()
    {
        var root = Vdf.Parse("""
            // comment
            "root"
            {
                "path"    "C:\\Program Files (x86)\\Steam"
                bare      value
                "Nested" { "a" "1" "b" "with \"quote\"" }
                "cond"    "x" [$WIN32]
            }
            """);
        var r = root["root"]!;
        r.GetString("path").ShouldBe(@"C:\Program Files (x86)\Steam");
        r.GetString("BARE").ShouldBe("value");
        r["nested"]!.GetString("b").ShouldBe("with \"quote\"");
        r.GetString("cond").ShouldBe("x");
    }

    [Fact]
    public void Rejects_unbalanced_braces() => Should.Throw<FormatException>(() => Vdf.Parse("\"a\" { \"b\" \"c\""));
}

public class DetectionTests
{
    [Fact]
    public void Finds_everything_on_a_normal_pc()
    {
        using var pc = new FakePc();
        var env = GameDetector.Detect(pc.Platform(), GameDefinition.Ets2);

        env.Problems.ShouldBeEmpty();
        env.UserDataDir.ShouldBe(pc.UserData);
        env.SteamLibraries.ShouldBe([pc.Steam, pc.Library2]);
        env.InstallDir.ShouldBe(pc.Install);
        env.SteamBuildId.ShouldBe("26552251");
        env.Version.ShouldBe(new GameVersion(1, 62, 2, 1)); // the fake exe has no version info → game.log.txt
        env.VersionSource.ShouldStartWith("game.log.txt");
        env.OwnedDlc.ShouldBe(["dlc_north", "dlc_east"], ignoreOrder: true);
        env.WorkshopContentDirs.Single().ShouldEndWith(@"workshop\content\227300");
    }

    [Fact]
    public void Lists_steam_accounts_with_game_data_and_marks_most_recent_as_active()
    {
        using var pc = new FakePc();
        var env = GameDetector.Detect(pc.Platform(), GameDefinition.Ets2);
        env.Accounts.Select(a => (a.AccountId, a.PersonaName, a.IsActive))
           .ShouldBe([(121u, "Alice", true), (122u, "Bob", false)]);
        env.Accounts[0].SteamId64.ShouldBe(76561197960265849UL);
        env.Accounts[0].RemoteDir(227300).ShouldEndWith(@"userdata\121\227300\remote");
    }

    [Fact]
    public void Running_steam_user_wins_over_most_recent_login()
    {
        using var pc = new FakePc();
        var env = GameDetector.Detect(pc.Platform(activeAccount: 122), GameDefinition.Ets2);
        env.Accounts.Single(a => a.IsActive).PersonaName.ShouldBe("Bob");
    }

    [Fact]
    public void Missing_game_is_a_problem_not_an_exception()
    {
        using var pc = new FakePc(withGame: false);
        var env = GameDetector.Detect(pc.Platform(), GameDefinition.Ets2);
        env.InstallDir.ShouldBeNull();
        env.Problems.ShouldContain(p => p.Contains("wasn't found in any Steam library", StringComparison.Ordinal));
        env.Version.ShouldBe(new GameVersion(1, 62, 2, 1)); // still known from the log
    }

    [Fact]
    public void Manual_install_override_also_adds_its_library()
    {
        using var pc = new FakePc();
        var env = GameDetector.Detect(new FakePlatform(pc.Documents, steam: null), GameDefinition.Ets2,
                                      new DetectionOverrides(InstallDir: pc.Install));
        env.InstallOverridden.ShouldBeTrue();
        env.InstallDir.ShouldBe(pc.Install);
        env.SteamLibraries.ShouldBe([pc.Library2]);
        env.Problems.ShouldBe(["Steam wasn't found in the registry."]);
    }

    [Fact]
    public void Missing_user_folder_is_reported()
    {
        using var pc = new FakePc();
        var env = GameDetector.Detect(new FakePlatform(Path.Combine(pc.Root, "Nowhere"), pc.Steam), GameDefinition.Ets2);
        env.UserDataDir.ShouldBeNull();
        env.Problems.ShouldContain(p => p.Contains("user folder wasn't found", StringComparison.Ordinal));
    }

    [Fact]
    public void Game_log_gives_version_and_incompatible_mods()
    {
        using var pc = new FakePc();
        var log = GameLog.Read(pc.UserData)!;
        log.Version.ShouldBe(new GameVersion(1, 62, 2, 1));
        log.IncompatibleMods.ShouldBe(["old_map", "Name With Spaces 1.0"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("1.62.2.1 (4c8b31398d9e)", "1.62.2.1")]
    [InlineData("1.48.1.6s", "1.48.1.6")]
    [InlineData("1.50", "1.50.0.0")]
    public void Parses_version_strings(string text, string expected) =>
        GameVersion.TryParse(text)!.ToString().ShouldBe(expected);
}

public class AtsDetectionTests
{
    [Fact]
    public void Ats_is_found_beside_ets2_with_its_own_folders_and_accounts()
    {
        using var pc = new FakePc();
        string userData = pc.Dir(@"Documents\American Truck Simulator");
        pc.Dir(@"Documents\American Truck Simulator\profiles");
        pc.Dir(@"Documents\American Truck Simulator\mod");
        pc.File(@"Steam\steamapps\appmanifest_270880.acf", """
            "AppState"
            {
            	"appid"		"270880"
            	"installdir"		"American Truck Simulator"
            	"buildid"		"1"
            }
            """);
        string install = pc.Dir(@"Steam\steamapps\common\American Truck Simulator");
        pc.File(@"Steam\steamapps\common\American Truck Simulator\bin\win_x64\amtrucks.exe", "not a real exe");
        pc.File(@"Steam\steamapps\common\American Truck Simulator\dlc_arizona.scs", "");
        pc.Dir(@"Steam\userdata\122\270880");

        var env = GameDetector.Detect(pc.Platform(), GameDefinition.Ats);

        env.Game.ShouldBe(GameDefinition.Ats);
        env.UserDataDir.ShouldBe(userData);
        env.InstallDir.ShouldBe(install);
        env.OwnedDlc.ShouldBe(["dlc_arizona"]);
        env.Accounts.Select(a => a.PersonaName).ShouldBe(["Bob"]); // only Bob has ATS data
        env.Accounts[0].RemoteDir(270880).ShouldEndWith(@"userdata\122\270880\remote");

        // ETS2 on the same PC is unaffected.
        GameDetector.Detect(pc.Platform(), GameDefinition.Ets2).InstallDir.ShouldBe(pc.Install);
    }

    [Fact]
    public void Game_keys_round_trip_and_unknown_means_ets2()
    {
        GameDefinition.FromKey("ats").ShouldBe(GameDefinition.Ats);
        GameDefinition.FromKey("ETS2").ShouldBe(GameDefinition.Ets2);
        GameDefinition.FromKey(null).ShouldBe(GameDefinition.Ets2);
        GameDefinition.FromKey("farming").ShouldBe(GameDefinition.Ets2);
    }
}
