using System.Globalization;
using Dispatch.Core.Mods.Packages;

namespace Dispatch.Core.Mods;

public enum ModSource
{
    Local,
    Workshop,
}

public enum IssueSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record ModIssue(IssueSeverity Severity, string Message);

/// <summary>Everything Dispatch knows about one installed mod. Immutable; cached between runs.</summary>
public sealed record ModInfo(
    string PackageId,
    ModSource Source,
    ulong? WorkshopId,
    string Path,
    string PackagePath,
    PackageFormat Format,
    long SizeBytes,
    DateTime LastWriteUtc,
    ModManifest? Manifest,
    string? SubPackage,
    int SubPackageCount,
    string? IconFile,
    int? IconWidth,
    int? IconHeight,
    IReadOnlyList<ModIssue> Issues)
{
    /// <summary>The <c>active_mods</c> identifier of a Workshop item: "mod_workshop_package." + 16 uppercase hex digits.</summary>
    public static string WorkshopPackageId(ulong workshopId) =>
        "mod_workshop_package." + workshopId.ToString("X16", CultureInfo.InvariantCulture);

    public static ulong? TryParseWorkshopId(string packageId) =>
        packageId.StartsWith("mod_workshop_package.", StringComparison.Ordinal) &&
        ulong.TryParse(packageId.AsSpan("mod_workshop_package.".Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong id)
            ? id
            : null;

    /// <summary>
    /// Name to show. The game ignores a Workshop item's display_name (it uses the Workshop title, which isn't
    /// stored locally), so profiles' own "…|Name" entries are preferred over this where available.
    /// </summary>
    public string DisplayName => Manifest?.DisplayName ??
                                 (WorkshopId is { } id ? $"Workshop item {id}" : PackageId);

    public bool IsReadable => Format != PackageFormat.Opaque && Issues.All(i => i.Severity != IssueSeverity.Error);
}
