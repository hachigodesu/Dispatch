namespace Dispatch.Core.Mods;

/// <summary>
/// Checks for manifest mistakes the game silently ignores or works around (found on real data in spike S8).
/// </summary>
public static class ModLinter
{
    /// <summary>Valid <c>category[]</c> values from the SCS modding wiki.</summary>
    public static readonly IReadOnlySet<string> Categories = new HashSet<string>(StringComparer.Ordinal)
    {
        "truck", "trailer", "interior", "tuning_parts", "ai_traffic", "sound", "paint_job", "cargo_pack", "map",
        "ui", "weather_setup", "physics", "graphics", "models", "movers", "walkers", "prefabs", "other",
    };

    private static readonly Dictionary<string, string> CommonTypos = new(StringComparer.Ordinal)
    {
        ["compatible_version[]"] = "compatible_versions[]",
        ["compatible_versions"] = "compatible_versions[]",
        ["categories[]"] = "category[]",
        ["dlc_dependency[]"] = "dlc_dependencies[]",
        ["dependencies[]"] = "dlc_dependencies[]",
        ["description"] = "description_file",
        ["version"] = "package_version",
        ["name"] = "display_name",
    };

    public static List<ModIssue> Check(ModManifest m, bool iconFound, (int Width, int Height)? iconSize, bool iconIsJpeg,
                                       bool descriptionFound)
    {
        var issues = new List<ModIssue>();
        foreach (var unknown in m.UnknownAttributes)
        {
            issues.Add(CommonTypos.TryGetValue(unknown, out var fix)
                ? new ModIssue(IssueSeverity.Warning, $"manifest.sii uses '{unknown}', which the game ignores. Did the author mean '{fix}'?")
                : new ModIssue(IssueSeverity.Info, $"manifest.sii has an unknown field '{unknown}' (ignored by the game)."));
        }

        foreach (var c in m.Categories.Where(c => !Categories.Contains(c)))
        {
            issues.Add(new ModIssue(IssueSeverity.Info,
                c == "maps" ? "Category 'maps' isn't valid (the game expects 'map')." : $"Unknown category '{c}' (ignored by the game)."));
        }

        if (m.Categories.Count > 2)
        {
            issues.Add(new ModIssue(IssueSeverity.Info, "The game only uses the first two categories."));
        }

        if (m.IconFile is not null)
        {
            if (!iconFound)
            {
                issues.Add(new ModIssue(IssueSeverity.Warning, $"The icon file '{m.IconFile}' is missing from the package."));
            }
            else if (!iconIsJpeg)
            {
                issues.Add(new ModIssue(IssueSeverity.Info, "The icon isn't a JPEG; the game expects a 276×162 .jpg."));
            }
            else if (iconSize is { } s && (s.Width != 276 || s.Height != 162))
            {
                issues.Add(new ModIssue(IssueSeverity.Info, $"The icon is {s.Width}×{s.Height}; the game expects 276×162."));
            }
        }

        if (m.DescriptionFile is not null && !descriptionFound)
        {
            issues.Add(new ModIssue(IssueSeverity.Warning, $"The description file '{m.DescriptionFile}' is missing from the package."));
        }

        return issues;
    }
}
