using System.Text.RegularExpressions;
using Dispatch.Core.Mods;

namespace Dispatch.Core.Profiles;

/// <summary>The kinds of mods, in the order the suggestion puts them (top of the list = highest priority first).</summary>
public enum SuggestedGroup
{
    /// <summary>Interface, graphics, weather and sound: small overrides that should win over everything else.</summary>
    Look,

    /// <summary>AI traffic, physics, movers and walkers.</summary>
    TrafficAndPhysics,

    /// <summary>Trucks, trailers, interiors, tuning, paint jobs, cargo, models and other mods.</summary>
    Vehicles,

    /// <summary>Maps, their parts, patches and connectors: at the bottom, in the user's own order.</summary>
    Maps,
}

public enum SuggestedReason
{
    /// <summary>Not moved by a rule (it may still shift because others move past it).</summary>
    Kept,

    /// <summary>Moved above mods of a kind that goes lower (e.g. a sound mod that was below a map).</summary>
    ByKind,

    /// <summary>A patch or connector moved up to just above the map it names (<see cref="SuggestedEntry.Above"/>).</summary>
    PatchAboveMap,
}

/// <summary>One mod in the suggested order.</summary>
/// <param name="From">Its place in the current order (0 = top).</param>
/// <param name="Reason">Why a rule moved it; Kept for mods that only shift because others move past them.</param>
/// <param name="Above">PatchAboveMap: the map it now sits just above.</param>
public sealed record SuggestedEntry(ActiveModEntry Entry, int From, SuggestedGroup Group, SuggestedReason Reason = SuggestedReason.Kept,
                                    ActiveModEntry? Above = null)
{
    public bool IsMoved => Reason != SuggestedReason.Kept;
}

/// <summary>
/// Suggests a load order by the usual community rules, carefully: interface, graphics, weather and sound mods at the
/// top, then traffic and physics, then trucks, trailers and other mods, and the maps at the bottom. Mods of the same
/// kind keep their order. The map section keeps the user's order (map combos are ordered by hand, by their own
/// instructions), except that a patch or connector below a map it names moves up to just above that map. Parts of a
/// map pack ("Poland Rebuilding - Def", "RPM - Models - 1") stay with the map even when they aren't marked as map, and
/// a mod Dispatch can't classify stays with the mod below it.
/// </summary>
public static partial class LoadOrderSuggester
{
    public static List<SuggestedEntry> Suggest(IReadOnlyList<ActiveModEntry> topFirst, IReadOnlyDictionary<string, ModInfo> installed)
    {
        int n = topFirst.Count;
        var mods = topFirst.Select(e => installed.GetValueOrDefault(e.PackageId)).ToArray();
        var families = topFirst.Select(e => Family(e.DisplayName)).ToArray();
        var groups = topFirst.Select((e, i) => Classify(e, mods[i])).ToArray();

        // Unknown mods take the kind of the next known mod below them (or the middle kind at the very bottom).
        SuggestedGroup below = SuggestedGroup.Vehicles;
        for (int i = n - 1; i >= 0; i--)
        {
            below = groups[i] ??= below;
        }

        // Parts of a map pack belong to the map section, whatever their own category says.
        var mapFamilies = Enumerable.Range(0, n).Where(i => groups[i] == SuggestedGroup.Maps && families[i] is not null)
                                    .Select(i => families[i]!).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            if (families[i] is { } f && mapFamilies.Contains(f))
            {
                groups[i] = SuggestedGroup.Maps;
            }
        }

        var others = Enumerable.Range(0, n).Where(i => groups[i] != SuggestedGroup.Maps).OrderBy(i => groups[i]!.Value).ToList(); // stable
        var maps = Enumerable.Range(0, n).Where(i => groups[i] == SuggestedGroup.Maps).ToList();
        var patchMoves = MovePatchesAboveTheirMaps(maps, topFirst, mods, families);

        // A mod "moves" when a rule lifts it over something that was above it: a mod of a lower kind, or (patches) its map.
        // Mods that only shift down because others pass them keep their place among their own kind.
        return [.. others.Concat(maps).Select(i =>
        {
            var g = groups[i]!.Value;
            if (patchMoves.TryGetValue(i, out int above))
            {
                return new SuggestedEntry(topFirst[i], i, g, SuggestedReason.PatchAboveMap, topFirst[above]);
            }

            bool passed = Enumerable.Range(0, i).Any(j => groups[j]!.Value > g);
            return new SuggestedEntry(topFirst[i], i, g, passed ? SuggestedReason.ByKind : SuggestedReason.Kept);
        })];
    }

    /// <summary>True when the suggestion is the order the mods are in already.</summary>
    public static bool IsUnchanged(IReadOnlyList<SuggestedEntry> suggestion) => suggestion.Select((s, i) => s.From == i).All(x => x);

    /// <summary>The kind of a mod, or null when it can't be told (not installed, no manifest, no usable category).</summary>
    public static SuggestedGroup? Classify(ActiveModEntry entry, ModInfo? mod)
    {
        var categories = (mod?.Manifest?.Categories ?? []).Select(ModCategories.Canonical).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (categories.Contains("map") || categories.Contains("prefabs"))
        {
            return SuggestedGroup.Maps;
        }

        if (categories.Overlaps(["ui", "graphics", "weather_setup", "sound"]))
        {
            return SuggestedGroup.Look;
        }

        if (categories.Overlaps(["ai_traffic", "physics", "movers", "walkers"]))
        {
            return SuggestedGroup.TrafficAndPhysics;
        }

        if (categories.Count > 0)
        {
            // Other mods named as a map fix or connector (e.g. "ProMods + RusMap connector") belong with the maps.
            return IsPatch(entry, mod) && Words(Name(entry, mod)).Overlaps(["map", "maps", "promods", "rusmap"]) ? SuggestedGroup.Maps : SuggestedGroup.Vehicles;
        }

        return null;
    }

    /// <summary>
    /// A patch below a map it names ("…ProMods Fix", "…Poland Rebuilding Connector") moves to just above that map.
    /// Returns the moved patches with the map each now sits above.
    /// </summary>
    private static Dictionary<int, int> MovePatchesAboveTheirMaps(List<int> maps, IReadOnlyList<ActiveModEntry> topFirst, ModInfo?[] mods, string?[] families)
    {
        var moved = new Dictionary<int, int>();
        var patches = maps.Where(i => IsPatch(topFirst[i], mods[i])).ToList();
        foreach (int p in patches)
        {
            var named = Words(Name(topFirst[p], mods[p]));
            int at = maps.IndexOf(p);
            int target = maps.FindIndex(i => i != p && !patches.Contains(i) && families[i] is { } f && named.Contains(f));
            if (target >= 0 && target < at)
            {
                moved[p] = maps[target];
                maps.RemoveAt(at);
                maps.Insert(target, p);
            }
        }

        return moved;
    }

    private static bool IsPatch(ActiveModEntry entry, ModInfo? mod) => PatchWords().IsMatch(Name(entry, mod));

    private static string Name(ActiveModEntry entry, ModInfo? mod) => $"{entry.DisplayName} {mod?.Manifest?.DisplayName}";

    /// <summary>What a mod's name starts with, which its other parts share: "Poland Rebuilding - Def" → "poland", "52FRXF3" → "52frxf".</summary>
    internal static string? Family(string displayName) =>
        WordPattern().Match(displayName) is { Success: true } m && Stem(m.Value) is { Length: >= 3 } stem ? stem : null;

    private static HashSet<string> Words(string text) =>
        WordPattern().Matches(text).Select(m => Stem(m.Value)).Where(w => w.Length > 0).ToHashSet(StringComparer.Ordinal);

    private static string Stem(string word) => word.ToLowerInvariant().TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"\b(patch|fix|fixes|compat\w*|connector|addon|add-on)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PatchWords();
}
