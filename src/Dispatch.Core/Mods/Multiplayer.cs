namespace Dispatch.Core.Mods;

/// <summary>Whether everybody in an SCS Convoy session needs a mod too.</summary>
public enum ConvoyNeed
{
    Required,
    Optional,
}

public enum ConvoyReason
{
    /// <summary>A normal mod: every player needs it, in the same order.</summary>
    Required,

    /// <summary>The author marked it <c>mp_mod_optional</c>: players may leave it out if the host allows optional mods.</summary>
    MarkedOptional,

    /// <summary>Map mods are always required, even when marked optional.</summary>
    MapAlwaysRequired,

    /// <summary>Not installed here, so it can't be checked (and the game skips it).</summary>
    NotInstalled,

    /// <summary>A protected or unreadable package: its manifest can't be read, so it's treated as required.</summary>
    Unreadable,
}

public enum TruckersMpSupport
{
    Supported,
    NotSupported,
}

public enum TruckersMpReason
{
    /// <summary>ProMods: supported on TruckersMP's ProMods servers, in the version those servers run.</summary>
    ProMods,

    /// <summary>The seasonal weather mods by Grimes, which TruckersMP supports.</summary>
    SeasonalWeather,

    /// <summary>Anything else: TruckersMP doesn't support it.</summary>
    Other,
}

public sealed record MultiplayerEntry(
    string PackageId,
    string DisplayName,
    ulong? WorkshopId,
    ConvoyNeed Convoy,
    ConvoyReason ConvoyReason,
    TruckersMpSupport TruckersMp,
    TruckersMpReason TruckersMpReason);

/// <summary>
/// Multiplayer readiness of a load order.
/// <para>
/// SCS Convoy (the game's own multiplayer): every player needs the host's mods, unless a mod's manifest says
/// <c>mp_mod_optional: true</c> and the host allows optional mods. Map mods are always required.
/// </para>
/// <para>
/// TruckersMP (a separate multiplayer mod) has no manifest field to read. As of November 2026 it supports only
/// ProMods (on its ProMods servers) and the seasonal weather mods by Grimes; this list is kept by hand and matched by
/// name, so it's a hint, not a verdict.
/// </para>
/// </summary>
public static class MultiplayerCheck
{
    public static List<MultiplayerEntry> Evaluate(IEnumerable<(string PackageId, string DisplayName, ModInfo? Mod)> topFirst) =>
        topFirst.Select(e => Evaluate(e.PackageId, e.DisplayName, e.Mod)).ToList();

    public static MultiplayerEntry Evaluate(string packageId, string displayName, ModInfo? mod)
    {
        var (need, reason) = mod switch
        {
            null => (ConvoyNeed.Required, ConvoyReason.NotInstalled),
            { Manifest: null } => (ConvoyNeed.Required, ConvoyReason.Unreadable),
            { Manifest: var m } when IsMap(m) => (ConvoyNeed.Required, m.MpModOptional == true ? ConvoyReason.MapAlwaysRequired : ConvoyReason.Required),
            { Manifest.MpModOptional: true } => (ConvoyNeed.Optional, ConvoyReason.MarkedOptional),
            _ => (ConvoyNeed.Required, ConvoyReason.Required),
        };

        var tmp = TruckersMpFor(packageId, displayName, mod);
        return new MultiplayerEntry(packageId, displayName, mod?.WorkshopId ?? ModInfo.TryParseWorkshopId(packageId), need, reason,
                                    tmp == TruckersMpReason.Other ? TruckersMpSupport.NotSupported : TruckersMpSupport.Supported, tmp);
    }

    private static bool IsMap(ModManifest m) => m.Categories.Any(c => ModCategories.Canonical(c) == "map");

    private static TruckersMpReason TruckersMpFor(string packageId, string displayName, ModInfo? mod)
    {
        string name = $"{packageId} {displayName} {mod?.Manifest?.DisplayName}";
        string author = mod?.Manifest?.Author ?? "";
        if (Contains(name, "promods") || Contains(author, "promods"))
        {
            return TruckersMpReason.ProMods;
        }

        return Contains(author, "grimes") || Contains(name, "grimes") || Contains(name, "frosty winter")
            ? TruckersMpReason.SeasonalWeather
            : TruckersMpReason.Other;
    }

    private static bool Contains(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
}
