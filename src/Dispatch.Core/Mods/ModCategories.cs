namespace Dispatch.Core.Mods;

/// <summary>The game's mod categories (manifest <c>category[]</c>) and tolerant matching of common author mistakes.</summary>
public static class ModCategories
{
    /// <summary>Valid values from the SCS modding wiki.</summary>
    public static readonly IReadOnlySet<string> Valid = new HashSet<string>(StringComparer.Ordinal)
    {
        "truck", "trailer", "interior", "tuning_parts", "ai_traffic", "sound", "paint_job", "cargo_pack", "map",
        "ui", "weather_setup", "physics", "graphics", "models", "movers", "walkers", "prefabs", "other",
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tuning"] = "tuning_parts",
        ["tuning_part"] = "tuning_parts",
        ["traffic"] = "ai_traffic",
        ["paintjob"] = "paint_job",
        ["paint"] = "paint_job",
        ["cargo"] = "cargo_pack",
        ["weather"] = "weather_setup",
        ["model"] = "models",
        ["prefab"] = "prefabs",
        ["mover"] = "movers",
        ["walker"] = "walkers",
    };

    /// <summary>
    /// The valid category a declared value most likely means, or null if it can't be matched. Exact values are returned
    /// as-is. Case, a plural/singular slip ("maps" → "map", "sounds" → "sound") and spaces/dashes are forgiven.
    /// The game itself ignores invalid values, so <see cref="ModLinter"/> still reports them.
    /// </summary>
    public static string? Canonical(string declared)
    {
        if (Valid.Contains(declared))
        {
            return declared;
        }

        string key = declared.Trim().Replace(' ', '_').Replace('-', '_').ToLowerInvariant();
        if (key.Length == 0)
        {
            return null;
        }

        if (Valid.Contains(key))
        {
            return key;
        }

        if (Aliases.TryGetValue(key, out var alias))
        {
            return alias;
        }

        if (key.EndsWith('s') && Valid.Contains(key[..^1]))
        {
            return key[..^1]; // maps → map, sounds → sound, trucks → truck
        }

        return Valid.Contains(key + "s") ? key + "s" : null; // model → models
    }
}
