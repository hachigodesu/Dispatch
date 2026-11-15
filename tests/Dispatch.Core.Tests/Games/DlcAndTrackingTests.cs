using Dispatch.Core.Games;
using Dispatch.Core.Tests.Mods;

namespace Dispatch.Core.Tests.Dlc;

public sealed class DlcCatalogTests
{
    private const string PolarManifest = """
        SiiNunit
        {
        dlc_package : _nameless.dlc_polar {
        	id: eut2_nordic
        	display_name: "DLC - Nordic Horizons"
        	steam_app_id: 2780810
        	features[]: map
        }
        }
        """;

    [Fact]
    public void Names_come_from_manifests_then_known_names_then_the_id()
    {
        var polar = DlcCatalog.Parse("dlc_polar", PolarManifest, GameId.Ets2);
        polar.ShouldBe(polar with { Name = "Nordic Horizons", SteamAppId = 2780810 });
        polar.AddsMap.ShouldBeTrue();

        var catalog = new DlcCatalog([polar, DlcCatalog.Parse("dlc_flags_de", "display_name: \"DLC – German Paint Jobs\"", GameId.Ets2)], GameId.Ets2);
        catalog.Owns("dlc_polar").ShouldBeTrue();
        catalog.Owns("DLC_POLAR").ShouldBeTrue();
        catalog.Owns("dlc_iberia").ShouldBeFalse();
        catalog.NameOf("dlc_polar").ShouldBe("Nordic Horizons");
        catalog.NameOf("dlc_flags_de").ShouldBe("German Paint Jobs");
        catalog.NameOf("dlc_iberia").ShouldBe("Iberia");          // not owned: known name
        catalog.NameOf("dlc_some_new_pack").ShouldBe("Some New Pack"); // unknown: tidied ID
        catalog.Owned.Select(d => d.Name).ShouldBe(["German Paint Jobs", "Nordic Horizons"]);

        catalog.IsMapExpansion(polar).ShouldBeTrue();
        catalog.IsMapExpansion(new DlcInfo("dlc_balt", "Beyond the Baltic Sea", ["map", "cargo"], null)).ShouldBeTrue(); // known map
        catalog.IsMapExpansion(new DlcInfo("dlc_krone", "Krone Trailer Pack", ["map", "cargo"], null)).ShouldBeFalse(); // trailers
        catalog.IsMapExpansion(new DlcInfo("dlc_flags_de", "German Paint Jobs", [], null)).ShouldBeFalse();

        var ats = DlcCatalog.Empty(GameId.Ats);
        ats.IsMapExpansion(new DlcInfo("dlc_tx", "Texas", [], null)).ShouldBeTrue();
        ats.NameOf("dlc_nm").ShouldBe("New Mexico");
        ats.NameOf("dlc_heavy_cargo").ShouldBe("Heavy Cargo");
    }

    [Fact]
    public void A_manifest_without_a_name_or_with_a_translation_key_falls_back()
    {
        DlcCatalog.Parse("dlc_east", "display_name: \"@@dlc_east_name@@\"", GameId.Ets2).Name.ShouldBe("Going East!");
        DlcCatalog.Parse("dlc_unknown_one", null, GameId.Ets2).Name.ShouldBe("Unknown One");
        DlcCatalog.Parse("dlc_unknown_one", null, GameId.Ets2).Features.ShouldBeEmpty();
    }

    [Fact]
    public void Reads_owned_dlcs_from_the_game_folder()
    {
        using var tmp = new TempDir();
        Fixtures.WriteZip(tmp.File("dlc_polar.scs"), new() { ["dlc_polar.manifest.sii"] = System.Text.Encoding.UTF8.GetBytes(PolarManifest) });
        File.WriteAllBytes(tmp.File("dlc_broken.scs"), [1, 2, 3]); // unreadable, still owned
        File.WriteAllBytes(tmp.File("base.scs"), [1, 2, 3]);

        var catalog = DlcCatalog.Read(tmp.Path, GameId.Ets2, TestContext.Current.CancellationToken);

        catalog.Owned.Select(d => d.Id).Order().ShouldBe(["dlc_broken", "dlc_polar"]);
        catalog.NameOf("dlc_polar").ShouldBe("Nordic Horizons");
        DlcCatalog.Read(tmp.File("missing"), GameId.Ets2, TestContext.Current.CancellationToken).Owned.ShouldBeEmpty();
    }
}

public sealed class HeadTrackingTests
{
    [Fact]
    public void Starts_opentrack_first_and_skips_running_or_missing_programs()
    {
        using var tmp = new TempDir();
        string open = tmp.File("opentrack.exe"), ai = tmp.File("AITrack.exe");
        File.WriteAllBytes(open, []);
        File.WriteAllBytes(ai, []);

        HeadTracking.ToStart(open, ai, _ => false).ShouldBe([open, ai]);
        HeadTracking.ToStart(open, ai, name => name == "opentrack").ShouldBe([ai]);
        HeadTracking.ToStart(null, ai, _ => false).ShouldBe([ai]);
        HeadTracking.ToStart(tmp.File("gone.exe"), "  ", _ => false).ShouldBeEmpty();
    }
}
