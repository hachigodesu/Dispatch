namespace Dispatch.Core.Profiles;

public sealed record NameValidation(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Validates a new profile name for duplicate/rename. Rules come from spike S7 (what the game's name field
/// accepts) plus Dispatch's safety rules. See docs/spikes/S7-profile-name-rules.md.
/// </summary>
public static class ProfileNameValidator
{
    /// <summary>The game's name field stops at 20 characters (S7).</summary>
    public const int MaxLength = 20;

    /// <summary>Windows MAX_PATH minus the terminating null.</summary>
    public const int MaxPath = 259;

    /// <param name="name">The proposed name.</param>
    /// <param name="existingNames">Names of all existing profiles, local and Steam Cloud.</param>
    /// <param name="existingFolders">Existing profile folder names (hex), local and Steam Cloud.</param>
    /// <param name="profilesRoot">Destination root (e.g. ...\profiles) for the path-length check, or null to skip it.</param>
    /// <param name="longestRelativePath">Longest file path inside the profile being copied, relative to its folder.</param>
    public static NameValidation Validate(
        string name,
        IEnumerable<string> existingNames,
        IEnumerable<string> existingFolders,
        string? profilesRoot = null,
        string? longestRelativePath = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("Enter a name.");
            return new NameValidation(errors, warnings);
        }

        if (char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[^1]))
        {
            errors.Add("The name can't start or end with a space.");
        }

        if (name.Any(char.IsSurrogate))
        {
            errors.Add("The game doesn't accept emoji or similar symbols in profile names.");
        }

        if (name.Any(char.IsControl))
        {
            errors.Add("The name contains an invisible control character.");
        }

        int length = name.Count(c => !char.IsLowSurrogate(c));
        if (length > MaxLength)
        {
            errors.Add($"The game allows at most {MaxLength} characters; this name has {length}.");
        }

        string folder = ProfileNameCodec.ToFolderName(name);
        var names = existingNames.ToList();
        if (names.Contains(name, StringComparer.Ordinal) ||
            existingFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"A profile named '{name}' already exists.");
        }
        else if (names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) is { } similar)
        {
            warnings.Add($"A profile named '{similar}' already exists; names that differ only in capitals are easy to confuse.");
        }

        if (profilesRoot is not null)
        {
            string longest = Path.Combine(profilesRoot, folder, longestRelativePath ?? "profile.bak.sii");
            if (longest.Length > MaxPath)
            {
                errors.Add($"The name is too long for this folder location: a copied file path would be {longest.Length} characters (Windows allows {MaxPath}).");
            }
        }

        return new NameValidation(errors, warnings);
    }
}
