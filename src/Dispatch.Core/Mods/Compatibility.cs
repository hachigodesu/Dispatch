using Dispatch.Core.Detection;

namespace Dispatch.Core.Mods;

public enum CompatStatus
{
    Ok,
    NoVersionInfo,
    VersionMismatch,
    MissingDlc,
    Unreadable,
}

public sealed record Compatibility(CompatStatus Status, string Detail, IReadOnlyList<string> MissingDlc);

public static class CompatibilityChecker
{
    /// <param name="gameLog">The game's own verdicts from the last launch; used for packages Dispatch can't read
    /// and to flag disagreements.</param>
    public static Compatibility Evaluate(ModInfo mod, GameVersion? version, IReadOnlySet<string> ownedDlc, GameLogInfo? gameLog)
    {
        bool gameSaysIncompatible = gameLog?.IncompatibleMods.Contains(mod.PackageId) == true;
        var manifest = mod.Manifest;
        var missingDlc = manifest is null || ownedDlc.Count == 0
            ? []
            : manifest.DlcDependencies.Where(d => !ownedDlc.Contains(d)).ToList();

        if (manifest is null)
        {
            return gameSaysIncompatible
                ? new Compatibility(CompatStatus.VersionMismatch,
                                    "Dispatch can't read this package, but the game reported it as incompatible at its last launch.", [])
                : new Compatibility(mod.IsReadable ? CompatStatus.NoVersionInfo : CompatStatus.Unreadable,
                                    mod.IsReadable ? "This mod has no manifest.sii, so it declares no compatible versions."
                                                   : "Dispatch can't read this package (protected or unknown format).", []);
        }

        if (missingDlc.Count > 0)
        {
            return new Compatibility(CompatStatus.MissingDlc, $"Requires DLC you don't have: {string.Join(", ", missingDlc)}.", missingDlc);
        }

        if (version is null)
        {
            return new Compatibility(CompatStatus.NoVersionInfo, "The game version is unknown, so compatibility can't be checked.", []);
        }

        string v = version.ToString();
        if (mod.Source == ModSource.Workshop)
        {
            // Workshop items pick a sub-package via versions.sii; the manifest's compatible_versions isn't used.
            return new Compatibility(CompatStatus.Ok, "Workshop item: the game picks the matching version automatically.", []);
        }

        if (manifest.CompatibleVersions.Count == 0)
        {
            return new Compatibility(CompatStatus.NoVersionInfo, "The mod doesn't declare which game versions it supports.", []);
        }

        bool matches = manifest.CompatibleVersions.Any(p => GameVersionMatch.Matches(p, v));
        string declared = string.Join(", ", manifest.CompatibleVersions);
        string disagreement = gameLog is null || matches != gameSaysIncompatible
            ? ""
            : matches
                ? " Note: the game's log disagrees and reported it as incompatible."
                : " Note: the game's log didn't report it as incompatible.";
        return matches
            ? new Compatibility(CompatStatus.Ok, $"Made for {declared}.{disagreement}", [])
            : new Compatibility(CompatStatus.VersionMismatch, $"Made for {declared}; you have {version.Major}.{version.Minor}.{disagreement}", []);
    }
}
