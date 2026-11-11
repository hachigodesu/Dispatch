using System.Text;
using Dispatch.Core.Safety;
using Dispatch.Core.Sii;

namespace Dispatch.Core.Profiles;

/// <param name="IncludeSaves">False = "fresh start": everything except the save\ folder.</param>
/// <param name="KeepWorldOfTrucksLink">False clears online_user_name / online_password in the copy.</param>
public sealed record DuplicateOptions(string NewName, bool IncludeSaves = true, bool KeepWorldOfTrucksLink = true);

/// <summary>
/// Duplicate and rename (PLAN.md M3, spike S4). A copy is staged under a hidden, non-hex folder name that the game
/// ignores, its profile_name is changed, it's verified, and then it appears with a single folder move.
/// Nothing is ever overwritten.
/// </summary>
public static class ProfileCloner
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly string[] ProfileFiles = ["profile.sii", "profile.bak.sii"];

    /// <param name="profilesRoot">Destination: the game's Documents\…\profiles folder (copies are always local).</param>
    /// <param name="existingNames">All profile names, local and Steam Cloud.</param>
    /// <param name="existingFolders">All profile folder names, local and Steam Cloud.</param>
    public static ProfileInfo Duplicate(SafeWriter writer, ProfileInfo source, string profilesRoot, DuplicateOptions options,
                                        IEnumerable<string> existingNames, IEnumerable<string> existingFolders)
    {
        string staging = Stage(writer, source, profilesRoot, options, existingNames, existingFolders, out string target);
        try
        {
            writer.ThrowIfGameRunning();
            Directory.Move(staging, target);
        }
        catch
        {
            DeleteQuietly(staging);
            throw;
        }

        return Describe(source, target, options.NewName);
    }

    /// <summary>
    /// Renames a local profile: stage a renamed copy → move the original into a full backup → move the copy into place.
    /// If the last step fails, the original is put back.
    /// </summary>
    public static (ProfileInfo Renamed, Backup Original) Rename(SafeWriter writer, BackupService backups, ProfileInfo source,
                                                                 string newName, IEnumerable<string> existingNames,
                                                                 IEnumerable<string> existingFolders)
    {
        if (source.IsReadOnly)
        {
            throw new InvalidOperationException("Steam Cloud profiles are read-only in Dispatch.");
        }

        string profilesRoot = Path.GetDirectoryName(source.MainDir)!;
        var options = new DuplicateOptions(newName, IncludeSaves: true, KeepWorldOfTrucksLink: true);
        string staging = Stage(writer, source, profilesRoot, options,
                               existingNames.Where(n => n != source.Name), existingFolders, out string target);
        Backup original;
        try
        {
            writer.ThrowIfGameRunning();
            original = backups.BackupWholeFolder(source, BackupReason.Rename, move: true);
        }
        catch
        {
            DeleteQuietly(staging);
            throw;
        }

        try
        {
            Directory.Move(staging, target);
        }
        catch
        {
            // Put the original back where it was, then clean up.
            writer.RestoreFolder(original);
            DeleteQuietly(staging);
            throw;
        }

        return (Describe(source, target, newName), original);
    }

    private static string Stage(SafeWriter writer, ProfileInfo source, string profilesRoot, DuplicateOptions options,
                                IEnumerable<string> existingNames, IEnumerable<string> existingFolders, out string target)
    {
        string longest = LongestRelativePath(source, options.IncludeSaves);
        var validation = ProfileNameValidator.Validate(options.NewName, existingNames, existingFolders, profilesRoot, longest);
        if (!validation.IsValid)
        {
            throw new ArgumentException(string.Join(" ", validation.Errors));
        }

        writer.ThrowIfGameRunning();
        target = Path.Combine(profilesRoot, ProfileNameCodec.ToFolderName(options.NewName));
        if (Directory.Exists(target))
        {
            throw new InvalidOperationException($"A profile folder for '{options.NewName}' already exists; nothing was changed.");
        }

        long needed = DirectorySize(source.MainDir, options.IncludeSaves) + (source.LocalPartDir is { } lp ? DirectorySize(lp, true) : 0);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(profilesRoot))!);
        if (drive.IsReady && drive.AvailableFreeSpace < needed + (64L << 20))
        {
            throw new IOException($"Not enough free space on {drive.Name}: the copy needs {needed / 1048576.0:F0} MB.");
        }

        string staging = Path.Combine(profilesRoot, $".dispatch-staging-{Guid.NewGuid():N}");
        try
        {
            CopyProfile(source.MainDir, staging, options.IncludeSaves);

            // A Steam Cloud profile keeps controls etc. in Documents\…\steam_profiles\<hex>; bring those along.
            if (source.LocalPartDir is { } localPart && Directory.Exists(localPart))
            {
                CopyProfile(localPart, staging, includeSaves: false, overwrite: false);
            }

            byte[]? renamedProfile = null;
            foreach (var file in ProfileFiles)
            {
                string path = Path.Combine(staging, file);
                if (!File.Exists(path))
                {
                    continue;
                }

                string text;
                try
                {
                    text = SiiFileCodec.Decode(File.ReadAllBytes(path)).Text;
                }
                catch (Exception ex) when (file == "profile.bak.sii" && renamedProfile is not null &&
                                           ex is InvalidDataException or NotSupportedException)
                {
                    // A damaged fallback copy: replace it with the renamed profile (the game refreshes it anyway, S2).
                    File.WriteAllBytes(path, renamedProfile);
                    continue;
                }

                text = ProfileText.WriteProfileName(text, options.NewName);
                if (!options.KeepWorldOfTrucksLink)
                {
                    text = ProfileText.WriteField(text, "online_user_name", "");
                    text = ProfileText.WriteField(text, "online_password", "");
                }

                byte[] bytes = Utf8NoBom.GetBytes(text);
                renamedProfile ??= bytes;
                File.WriteAllBytes(path, bytes);
                if (ProfileText.ReadProfileName(SiiFileCodec.Decode(File.ReadAllBytes(path)).Text) != options.NewName)
                {
                    throw new InvalidDataException($"Verification of {file} in the copy failed.");
                }
            }
        }
        catch
        {
            DeleteQuietly(staging);
            throw;
        }

        return staging;
    }

    private static void CopyProfile(string source, string dest, bool includeSaves, bool overwrite = false)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, file);
            if ((!includeSaves && IsInSaves(rel)) || rel.EndsWith(".dispatch-tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string to = Path.Combine(dest, rel);
            if (!overwrite && File.Exists(to))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite);
        }

        // Keep empty folders (e.g. an empty save\ in a fresh start is created by the game anyway).
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, dir);
            if (includeSaves || !IsInSaves(rel) || rel.Equals("save", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.Combine(dest, rel));
            }
        }
    }

    private static bool IsInSaves(string rel) =>
        rel.Equals("save", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("save" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string LongestRelativePath(ProfileInfo source, bool includeSaves) =>
        Directory.EnumerateFiles(source.MainDir, "*", SearchOption.AllDirectories)
                 .Select(f => Path.GetRelativePath(source.MainDir, f))
                 .Where(r => includeSaves || !IsInSaves(r))
                 .DefaultIfEmpty("profile.sii")
                 .MaxBy(r => r.Length)!;

    private static long DirectorySize(string dir, bool includeSaves) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                 .Where(f => includeSaves || !IsInSaves(Path.GetRelativePath(dir, f)))
                 .Sum(f => new FileInfo(f).Length);

    private static ProfileInfo Describe(ProfileInfo source, string dir, string name) =>
        new(name, Path.GetFileName(dir), ProfileKind.Local, dir, null, null, File.GetLastWriteTimeUtc(Path.Combine(dir, "profile.sii")),
            Directory.Exists(Path.Combine(dir, "save")) ? Directory.EnumerateDirectories(Path.Combine(dir, "save")).Count() : 0);

    private static void DeleteQuietly(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover .dispatch-staging-* folder is ignored by the game and by Dispatch.
        }
    }
}
