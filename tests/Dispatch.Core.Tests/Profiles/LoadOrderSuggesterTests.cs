using Dispatch.Core.Mods;
using Dispatch.Core.Mods.Packages;
using Dispatch.Core.Profiles;

namespace Dispatch.Core.Tests.Profiles;

public class LoadOrderSuggesterTests
{
    private static ModInfo Mod(string id, params string[] categories) =>
        new(id, ModSource.Local, null, id, id, PackageFormat.Zip, 1, DateTime.UtcNow,
            new ModManifest(".package_name", "1", id, null, categories, null, null, [], [], null, []), null, 0, null, null, null, []);

    private static List<SuggestedEntry> Suggest(params (string Name, string[] Categories)[] mods)
    {
        var installed = mods.Where(m => m.Categories.Length > 0).ToDictionary(m => m.Name, m => Mod(m.Name, m.Categories));
        return LoadOrderSuggester.Suggest(mods.Select(m => new ActiveModEntry(m.Name, m.Name)).ToList(), installed);
    }

    [Fact]
    public void Look_traffic_and_vehicles_go_above_the_maps_which_keep_their_order()
    {
        var suggestion = Suggest(
            ("ProMods Map", ["map"]), ("Truck A", ["truck"]), ("ProMods Definition", ["maps"]), ("Sound A", ["sound"]),
            ("Traffic", ["ai_traffic"]), ("RusMap Map", ["map"]), ("Trailer B", ["trailer"]), ("UI A", ["ui"]));

        suggestion.Select(s => s.Entry.PackageId).ShouldBe([
            "Sound A", "UI A", "Traffic", "Truck A", "Trailer B", "ProMods Map", "ProMods Definition", "RusMap Map"]);
        LoadOrderSuggester.IsUnchanged(suggestion).ShouldBeFalse();

        // Only the mods a rule lifts count as moved; the maps just shift down.
        suggestion.Where(e => e.IsMoved).Select(e => e.Entry.PackageId).ShouldBe(["Sound A", "UI A", "Traffic", "Truck A", "Trailer B"]);
        suggestion.Single(e => e.Entry.PackageId == "ProMods Map").Reason.ShouldBe(SuggestedReason.Kept);
    }

    [Fact]
    public void Parts_of_a_map_pack_stay_with_it_and_unknown_mods_stay_with_the_mod_below()
    {
        var suggestion = Suggest(
            ("Poland Rebuilding - Def", ["other"]), ("Truck", ["truck"]), ("Poland Rebuilding - Map", ["map"]),
            ("Protected part", []), ("RPM - Map", ["map"]), ("Sound", ["sound"]));

        suggestion.Select(s => s.Entry.PackageId).ShouldBe([
            "Sound", "Truck", "Poland Rebuilding - Def", "Poland Rebuilding - Map", "Protected part", "RPM - Map"]);
    }

    [Fact]
    public void A_patch_below_the_map_it_names_moves_just_above_it()
    {
        var suggestion = Suggest(
            ("Sud de France", ["map"]), ("ProMods Definition", ["map"]), ("ProMods Map", ["map"]),
            ("Sud de France & ProMods Fix", ["map"]), ("VolgaMap", ["map"]), ("VolgaMap Fix", ["map"]), ("Ultimate Map Combo Fix", ["map"]));

        suggestion.Select(s => s.Entry.PackageId).ShouldBe([
            "Sud de France & ProMods Fix", "Sud de France", "ProMods Definition", "ProMods Map", "VolgaMap Fix", "VolgaMap", "Ultimate Map Combo Fix"]);
        var fix = suggestion[0];
        (fix.Reason, fix.Above!.PackageId).ShouldBe((SuggestedReason.PatchAboveMap, "Sud de France"));
        suggestion.Count(e => e.IsMoved).ShouldBe(2);
        LoadOrderSuggester.IsUnchanged(LoadOrderSuggester.Suggest(suggestion.Select(s => s.Entry).ToList(),
            suggestion.ToDictionary(s => s.Entry.PackageId, s => Mod(s.Entry.PackageId, "map")))).ShouldBeTrue();
    }
}
