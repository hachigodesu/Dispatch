using Dispatch.Core.Detection;
using Dispatch.Core.Mods;

namespace Dispatch.Core.Profiles;

public enum HealthIssueKind
{
    /// <summary>In the load order but not installed: the game skips them.</summary>
    MissingMods,

    /// <summary>Made for other game versions than the one this profile launches with.</summary>
    WrongVersion,

    /// <summary>Need a DLC that isn't owned: the game won't activate them.</summary>
    MissingDlc,

    /// <summary>An older copy is active while a newer copy of the same mod is installed.</summary>
    OlderCopyActive,

    /// <summary>The game logged warnings about them at its last start.</summary>
    GameWarnings,

    /// <summary>The newest save was made with mods that aren't in the load order now.</summary>
    SaveNeedsMods,
}

/// <summary>A mod an issue is about. <paramref name="Replacement"/>: the newer copy to use instead (OlderCopyActive).</summary>
public sealed record HealthMod(string PackageId, string DisplayName, ModInfo? Mod, ModInfo? Replacement = null);

/// <param name="Detail">SaveNeedsMods: the save's name.</param>
public sealed record HealthIssue(HealthIssueKind Kind, IReadOnlyList<HealthMod> Mods, string? Detail = null);

/// <summary>Everything a profile's health is judged by. <paramref name="Installed"/> covers mod\, the library and the Workshop.</summary>
public sealed record HealthInput(
    IReadOnlyList<ActiveModEntry> TopFirst,
    IReadOnlyDictionary<string, ModInfo> Installed,
    GameVersion? LaunchVersion,
    IReadOnlySet<string> OwnedDlc,
    IReadOnlyDictionary<ModInfo, DuplicateInfo> Duplicates,
    GameLogInfo? GameLog,
    SaveInfo? NewestSave);

/// <summary>
/// The profile health report: problems with a profile's load order, each with what can be done about it (the app
/// offers the fixes; this only finds the problems). Pure: reads nothing from disk.
/// </summary>
public static class ProfileHealthCheck
{
    public static List<HealthIssue> Check(HealthInput input)
    {
        var issues = new List<HealthIssue>();
        var missing = new List<HealthMod>();
        var wrongVersion = new List<HealthMod>();
        var missingDlc = new List<HealthMod>();
        var older = new List<HealthMod>();
        var warned = new List<HealthMod>();
        var active = input.TopFirst.Select(e => e.PackageId).ToHashSet(StringComparer.Ordinal);

        foreach (var entry in input.TopFirst)
        {
            if (!input.Installed.TryGetValue(entry.PackageId, out var mod))
            {
                missing.Add(new HealthMod(entry.PackageId, entry.DisplayName, null));
                continue;
            }

            var item = new HealthMod(entry.PackageId, entry.DisplayName, mod);
            var compat = CompatibilityChecker.Evaluate(mod, input.LaunchVersion, input.OwnedDlc, input.GameLog);
            if (compat.Status == CompatStatus.VersionMismatch)
            {
                wrongVersion.Add(item);
            }
            else if (compat.Status == CompatStatus.MissingDlc)
            {
                missingDlc.Add(item);
            }

            if (input.Duplicates.TryGetValue(mod, out var dup) && dup.IsOlder && !active.Contains(dup.Newest.PackageId))
            {
                older.Add(item with { Replacement = dup.Newest });
            }

            if (input.GameLog?.MessagesFor(entry.PackageId).Count > 0)
            {
                warned.Add(item);
            }
        }

        Add(issues, HealthIssueKind.MissingMods, missing);
        Add(issues, HealthIssueKind.WrongVersion, wrongVersion);
        Add(issues, HealthIssueKind.MissingDlc, missingDlc);
        Add(issues, HealthIssueKind.OlderCopyActive, older);

        if (input.NewestSave is { } save)
        {
            var needed = save.Mods.Where(d => !active.Contains(d.PackageId))
                              .Select(d => new HealthMod(d.PackageId, d.DisplayName, input.Installed.GetValueOrDefault(d.PackageId)))
                              .ToList();
            if (needed.Count > 0)
            {
                issues.Add(new HealthIssue(HealthIssueKind.SaveNeedsMods, needed, save.Label));
            }
        }

        Add(issues, HealthIssueKind.GameWarnings, warned);
        return issues;
    }

    private static void Add(List<HealthIssue> issues, HealthIssueKind kind, List<HealthMod> mods)
    {
        if (mods.Count > 0)
        {
            issues.Add(new HealthIssue(kind, mods));
        }
    }
}
