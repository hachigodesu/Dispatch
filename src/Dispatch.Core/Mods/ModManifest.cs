using System.Text;
using Dispatch.Core.Sii;
using Dispatch.Core.Sii.Text;

namespace Dispatch.Core.Mods;

public sealed record ModManifest(
    string UnitName,
    string? PackageVersion,
    string? DisplayName,
    string? Author,
    IReadOnlyList<string> Categories,
    string? IconFile,
    string? DescriptionFile,
    IReadOnlyList<string> CompatibleVersions,
    IReadOnlyList<string> DlcDependencies,
    bool? MpModOptional,
    IReadOnlyList<string> UnknownAttributes)
{
    private static readonly HashSet<string> KnownKeys =
    [
        "package_version", "display_name", "author", "category", "icon", "description_file",
        "compatible_versions", "dlc_dependencies", "mp_mod_optional",
    ];

    /// <summary>Parses manifest.sii bytes (text, or encrypted/binary via <see cref="SiiFileCodec"/>).</summary>
    public static ModManifest Parse(byte[] data)
    {
        string text = SiiFormatDetector.Detect(data) == SiiFormat.Text
            ? Encoding.UTF8.GetString(data)
            : SiiFileCodec.Decode(data).Text;
        var unit = SiiUnitReader.Read(text).FirstOrDefault(u => u.ClassName == "mod_package")
                   ?? throw new InvalidDataException("manifest.sii has no 'mod_package' unit.");
        string? mp = unit.Get("mp_mod_optional");
        return new ModManifest(
            unit.Name,
            unit.Get("package_version"),
            NullIfEmpty(unit.Get("display_name")),
            NullIfEmpty(unit.Get("author")),
            unit.GetArray("category"),
            NullIfEmpty(unit.Get("icon")),
            NullIfEmpty(unit.Get("description_file")),
            unit.GetArray("compatible_versions").Where(v => v.Length > 0).ToList(),
            unit.GetArray("dlc_dependencies").Where(v => v.Length > 0).ToList(),
            mp is null ? null : mp.Equals("true", StringComparison.OrdinalIgnoreCase),
            unit.Attributes.Select(a => a.IsArrayItem ? a.Key + "[]" : a.Key)
                .Where(k => !KnownKeys.Contains(k.TrimEnd('[', ']'))).Distinct().ToList());
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>One entry of a Workshop item's versions.sii.</summary>
public sealed record VersionsEntry(string PackageName, IReadOnlyList<string> CompatibleVersions, bool Informational)
{
    public bool IsUniversal => CompatibleVersions.Count == 0;

    public static IReadOnlyList<VersionsEntry> Parse(string text) =>
        SiiUnitReader.Read(text).Where(u => u.ClassName == "package_version_info")
            .Select(u => new VersionsEntry(
                u.Get("package_name") ?? u.Name.TrimStart('.'),
                u.GetArray("compatible_versions").Where(v => v.Length > 0).ToList(),
                string.Equals(u.Get("informational"), "true", StringComparison.OrdinalIgnoreCase)))
            .ToList();

    /// <summary>The game's rule: first entry matching the version, otherwise the universal one.</summary>
    public static VersionsEntry? Select(IReadOnlyList<VersionsEntry> entries, string gameVersion) =>
        entries.FirstOrDefault(e => !e.IsUniversal && e.CompatibleVersions.Any(p => GameVersionMatch.Matches(p, gameVersion)))
        ?? entries.FirstOrDefault(e => e.IsUniversal);
}

public static class GameVersionMatch
{
    /// <summary>
    /// "1.62.*" matches 1.62.x and 1.62.x.y; a wildcard is only valid at the end.
    /// Without a wildcard the pattern must equal the version or be a whole-component prefix of it (to verify, S8).
    /// </summary>
    public static bool Matches(string pattern, string version)
    {
        pattern = pattern.Trim().TrimEnd('s');
        version = version.Trim().TrimEnd('s');
        if (pattern.EndsWith('*'))
        {
            string prefix = pattern[..^1];
            return prefix.Length > 0 && !prefix.Contains('*', StringComparison.Ordinal) &&
                   version.StartsWith(prefix, StringComparison.Ordinal);
        }

        return version == pattern || version.StartsWith(pattern + ".", StringComparison.Ordinal);
    }
}
