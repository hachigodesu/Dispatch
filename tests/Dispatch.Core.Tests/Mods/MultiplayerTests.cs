using Dispatch.Core.Mods;
using Dispatch.Core.Mods.Packages;

namespace Dispatch.Core.Tests.Mods;

public class MultiplayerTests
{
    private static ModInfo Mod(string id, bool? optional, string category = "other", string? author = null, string? name = null) =>
        new(id, ModSource.Local, null, id, id, PackageFormat.Zip, 1, DateTime.UtcNow,
            new ModManifest(".package_name", "1", name ?? id, author, [category], null, null, [], [], optional, []),
            null, 0, null, null, null, []);

    [Fact]
    public void Convoy_needs_every_mod_except_optional_ones_and_maps_are_always_needed()
    {
        var opaque = Mod("protected", null) with { Manifest = null, Format = PackageFormat.Opaque };
        var r = MultiplayerCheck.Evaluate([
            ("sound", "Sound", Mod("sound", optional: true, "sound")),
            ("truck", "Truck", Mod("truck", optional: false, "truck")),
            ("plain", "Plain", Mod("plain", optional: null)),
            ("rusmap", "RusMap", Mod("rusmap", optional: true, "maps")),
            ("protected", "Protected", opaque),
            ("gone", "Gone", null),
        ]);

        r.Select(e => (e.Convoy, e.ConvoyReason)).ShouldBe([
            (ConvoyNeed.Optional, ConvoyReason.MarkedOptional),
            (ConvoyNeed.Required, ConvoyReason.Required),
            (ConvoyNeed.Required, ConvoyReason.Required),
            (ConvoyNeed.Required, ConvoyReason.MapAlwaysRequired),
            (ConvoyNeed.Required, ConvoyReason.Unreadable),
            (ConvoyNeed.Required, ConvoyReason.NotInstalled),
        ]);
    }

    [Fact]
    public void TruckersMP_supports_only_ProMods_and_the_seasonal_weather_mods()
    {
        var r = MultiplayerCheck.Evaluate([
            ("promods-eu-map-v283", "ProMods Europe Map", Mod("promods-eu-map-v283", null, "map")),
            ("winter", "Frosty Winter Weather Mod", Mod("winter", null, "weather_setup", author: "Grimes")),
            ("mod_workshop_package.00000000C3CC68CD", "Some Truck", null),
        ]);

        r.Select(e => e.TruckersMpReason).ShouldBe([TruckersMpReason.ProMods, TruckersMpReason.SeasonalWeather, TruckersMpReason.Other]);
        r[2].TruckersMp.ShouldBe(TruckersMpSupport.NotSupported);
        r[2].WorkshopId.ShouldBe(3284953293UL);
    }
}
