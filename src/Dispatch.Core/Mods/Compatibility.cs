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

/// <summary>Why a mod got its status; lets the UI explain it in its own language (<see cref="Compatibility.Detail"/> is English).</summary>
public enum CompatReason
{
    GameSaysIncompatible,
    NoManifest,
    Unreadable,
    MissingDlc,
    UnknownGameVersion,
    Workshop,
    NoVersions,
    Matches,
    Mismatch,
}

/// <summary>Whether the game's own log agrees with Dispatch's verdict.</summary>
public enum LogNote
{
    None,
    LogSaysIncompatible,
    LogSaysCompatible,
}

public sealed record Compatibility(CompatStatus Status, string Detail, IReadOnlyList<string> MissingDlc)
{
    public CompatReason Reason { get; init; }

    /// <summary>The mod's compatible_versions, comma-separated (for Matches / Mismatch).</summary>
    public string? Declared { get; init; }

    /// <summary>"1.62" (for Mismatch).</summary>
    public string? YourVersion { get; init; }

    public LogNote LogNote { get; init; }
}

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
                { Reason = CompatReason.GameSaysIncompatible }
                : new Compatibility(mod.IsReadable ? CompatStatus.NoVersionInfo : CompatStatus.Unreadable,
                                    mod.IsReadable ? "This mod has no manifest.sii, so it declares no compatible versions."
                                                   : "Dispatch can't read this package (protected or unknown format).", [])
                { Reason = mod.IsReadable ? CompatReason.NoManifest : CompatReason.Unreadable };
        }

        if (missingDlc.Count > 0)
        {
            return new Compatibility(CompatStatus.MissingDlc, $"Requires DLC you don't have: {string.Join(", ", missingDlc)}.", missingDlc)
            { Reason = CompatReason.MissingDlc };
        }

        if (version is null)
        {
            return new Compatibility(CompatStatus.NoVersionInfo, "The game version is unknown, so compatibility can't be checked.", [])
            { Reason = CompatReason.UnknownGameVersion };
        }

        string v = version.ToString();
        if (mod.Source == ModSource.Workshop)
        {
            // Workshop items pick a sub-package via versions.sii; the manifest's compatible_versions isn't used.
            return new Compatibility(CompatStatus.Ok, "Workshop item: the game picks the matching version automatically.", [])
            { Reason = CompatReason.Workshop };
        }

        if (manifest.CompatibleVersions.Count == 0)
        {
            return new Compatibility(CompatStatus.NoVersionInfo, "The mod doesn't declare which game versions it supports.", [])
            { Reason = CompatReason.NoVersions };
        }

        bool matches = manifest.CompatibleVersions.Any(p => GameVersionMatch.Matches(p, v));
        string declared = string.Join(", ", manifest.CompatibleVersions);
        string disagreement = gameLog is null || matches != gameSaysIncompatible
            ? ""
            : matches
                ? " Note: the game's log disagrees and reported it as incompatible."
                : " Note: the game's log didn't report it as incompatible.";
        var note = disagreement.Length == 0 ? LogNote.None : matches ? LogNote.LogSaysIncompatible : LogNote.LogSaysCompatible;
        return matches
            ? new Compatibility(CompatStatus.Ok, $"Made for {declared}.{disagreement}", [])
            { Reason = CompatReason.Matches, Declared = declared, LogNote = note }
            : new Compatibility(CompatStatus.VersionMismatch, $"Made for {declared}; you have {version.Major}.{version.Minor}.{disagreement}", [])
            { Reason = CompatReason.Mismatch, Declared = declared, YourVersion = $"{version.Major}.{version.Minor}", LogNote = note };
    }
}
