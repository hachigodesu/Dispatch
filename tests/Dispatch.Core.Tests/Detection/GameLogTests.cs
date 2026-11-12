using Dispatch.Core.Detection;
using Dispatch.Core.Mods;

namespace Dispatch.Core.Tests.Detection;

public class GameLogTests
{
    // Shapes copied from a real game.log.txt (names changed).
    private static readonly string[] Lines =
    [
        "00:00:01.000 : [ufs] Loaded pack set version 1.62.2.1",
        "00:00:06.537 : [zipfs] C:/Users/x/Documents/Euro Truck Simulator 2/mod/Locked Map 1.0.scs: AEM PK",
        "00:00:06.537 : [zipfs] Locked Map 1.0.scs: Created, 6334 entries",
        "00:00:06.615 : <WARNING> [unit] Ignored unknown attribute compatible_version[] = 1.55.*",
        "00:00:06.700 : [hashfs] Big Map [DEF].scs: Created, 1273 entries",
        "00:00:06.700 : [hashfs] Big Map [DEF].scs: Created, requested subdirectory: base, 1273 entries",
        "00:00:06.701 : <WARNING> [mods] Unknown category 'maps' defined for Big Map 2.0. Ignoring.",
        "00:00:06.701 : <WARNING> [mods] No valid categories defined for Big Map 2.0. Setting defaults.",
        "00:00:06.702 : <WARNING> [mods] Incompatible mod - Big Map [DEF]",
        "00:00:06.800 : [zipfs] quiet.zip: Created, 5 entries",
        "00:00:06.900 : [mod_package_manager] Requesting Steam Workshop content..",
        // World loading later: must not be pinned on quiet.zip.
        "00:00:20.000 : <WARNING> [unit] Ignored unknown attribute dependencies[] = europe",
        "00:00:20.001 : <ERROR> [mods] Cannot find '/mod_description.txt' description file for mod 'Nobody'.",
    ];

    [Fact]
    public void Messages_are_attributed_to_the_mod_mounted_just_before()
    {
        var log = GameLog.Parse(Lines, DateTime.UtcNow);

        log.Version.ShouldBe(GameVersion.TryParse("1.62.2.1"));
        log.IncompatibleMods.ShouldBe(["Big Map [DEF]"]);
        log.MessagesFor("Locked Map 1.0").Select(m => m.Message).ShouldBe(["Ignored unknown attribute compatible_version[] = 1.55.*"]);

        var big = log.MessagesFor("Big Map [DEF]");
        big.Count.ShouldBe(3);
        big[0].Message.ShouldBe("Unknown category 'maps' defined for Big Map 2.0. Ignoring.");
        big[2].Message.ShouldStartWith("Incompatible mod");
        big.ShouldAllBe(m => m.Severity == IssueSeverity.Warning);

        log.MessagesFor("quiet").ShouldBeEmpty();
        log.ModMessages.Keys.Order().ShouldBe(["Big Map [DEF]", "Locked Map 1.0"]);
    }
}
