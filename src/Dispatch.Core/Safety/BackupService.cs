using System.Globalization;
using System.Text.Json;
using Dispatch.Core.Profiles;

namespace Dispatch.Core.Safety;

public enum BackupReason
{
    LoadOrderSave,
    Rename,
    Duplicate,
    PreRestore,
    PresetApply,
    Manual,

    // Append new values only: backup.json stores these as numbers.
    Delete,
}

/// <summary>One backup folder. <see cref="IsFullProfile"/> backups hold a whole profile folder (from rename).</summary>
public sealed record Backup(
    string Id,
    DateTime CreatedUtc,
    ProfileKind Kind,
    string ProfileFolderName,
    string ProfileName,
    BackupReason Reason,
    bool IsFullProfile,
    string OriginalDir,
    IReadOnlyList<string> Files,
    string Path,
    long SizeBytes);

/// <summary>Retention per profile (PLAN.md decision: 20 small, 5 full).</summary>
public sealed record BackupRetention(int Small = 20, int Full = 5);

/// <summary>
/// Timestamped backups under %LOCALAPPDATA%\Dispatch\backups\&lt;Local|SteamCloud&gt;\&lt;profile hex&gt;\&lt;stamp&gt;_&lt;reason&gt;\.
/// Each backup folder carries its own backup.json, so the folder alone is enough to restore it.
/// </summary>
public sealed class BackupService(string backupsRoot, BackupRetention? retention = null, Func<DateTime>? clock = null)
{
    private const string ManifestName = "backup.json";
    private const string ContentFolder = "files";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly BackupRetention _retention = retention ?? new BackupRetention();
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);

    public string Root => backupsRoot;

    /// <summary>Copies the given files (relative to <paramref name="profileDir"/>) into a new backup. Missing files are skipped.</summary>
    public Backup BackupFiles(ProfileInfo profile, BackupReason reason, IEnumerable<string> relativeFiles)
    {
        string dir = NewBackupDir(profile, reason);
        string content = System.IO.Path.Combine(dir, ContentFolder);
        Directory.CreateDirectory(content);
        var copied = new List<string>();
        foreach (var rel in relativeFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string src = System.IO.Path.Combine(profile.MainDir, rel);
            if (!File.Exists(src))
            {
                continue;
            }

            string dest = System.IO.Path.Combine(content, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            File.Copy(src, dest);
            copied.Add(rel);
        }

        var backup = Finish(dir, profile, reason, isFull: false, copied);
        Prune(profile.Kind, profile.FolderName);
        return backup;
    }

    /// <summary>
    /// Moves (same drive) or copies a whole profile folder into a new full backup. Used by rename: the original
    /// leaves profiles\ but stays restorable.
    /// </summary>
    public Backup BackupWholeFolder(ProfileInfo profile, BackupReason reason, bool move)
    {
        string dir = NewBackupDir(profile, reason);
        string content = System.IO.Path.Combine(dir, ContentFolder);
        if (move && SameVolume(profile.MainDir, dir))
        {
            Directory.Move(profile.MainDir, content);
        }
        else
        {
            CopyDirectory(profile.MainDir, content);
            if (move)
            {
                Directory.Delete(profile.MainDir, recursive: true);
            }
        }

        var files = Directory.EnumerateFiles(content, "*", SearchOption.AllDirectories)
                             .Select(f => System.IO.Path.GetRelativePath(content, f)).ToList();
        var backup = Finish(dir, profile, reason, isFull: true, files);
        Prune(profile.Kind, profile.FolderName);
        return backup;
    }

    /// <summary>Backups of one profile, newest first.</summary>
    public IReadOnlyList<Backup> List(ProfileKind kind, string profileFolderName)
    {
        string dir = System.IO.Path.Combine(backupsRoot, kind.ToString(), profileFolderName);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var list = new List<Backup>();
        foreach (var b in Directory.EnumerateDirectories(dir))
        {
            if (TryRead(b) is { } backup)
            {
                list.Add(backup);
            }
        }

        return list.OrderByDescending(b => b.CreatedUtc).ThenByDescending(b => b.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>All backups of all profiles of a kind, newest first (also finds backups of renamed/deleted profiles).</summary>
    public IReadOnlyList<Backup> ListAll()
    {
        if (!Directory.Exists(backupsRoot))
        {
            return [];
        }

        return Directory.EnumerateDirectories(backupsRoot)
            .SelectMany(Directory.EnumerateDirectories)
            .SelectMany(Directory.EnumerateDirectories)
            .Select(TryRead).OfType<Backup>()
            .OrderByDescending(b => b.CreatedUtc).ToList();
    }

    public static string ContentDir(Backup backup) => System.IO.Path.Combine(backup.Path, ContentFolder);

    /// <summary>Keeps the newest N small and M full backups of a profile; older ones are deleted.</summary>
    public void Prune(ProfileKind kind, string profileFolderName)
    {
        var all = List(kind, profileFolderName);
        foreach (var old in all.Where(b => !b.IsFullProfile).Skip(_retention.Small)
                               .Concat(all.Where(b => b.IsFullProfile).Skip(_retention.Full)))
        {
            try
            {
                Directory.Delete(old.Path, recursive: true);
            }
            catch (IOException)
            {
                // Locked by something (e.g. Explorer); try again next time.
            }
        }
    }

    private string NewBackupDir(ProfileInfo profile, BackupReason reason)
    {
        string parent = System.IO.Path.Combine(backupsRoot, profile.Kind.ToString(), profile.FolderName);
        string stamp = _now().ToLocalTime().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string dir = System.IO.Path.Combine(parent, $"{stamp}_{reason}");
        for (int i = 2; Directory.Exists(dir); i++)
        {
            dir = System.IO.Path.Combine(parent, $"{stamp}-{i}_{reason}");
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    private Backup Finish(string dir, ProfileInfo profile, BackupReason reason, bool isFull, List<string> files)
    {
        long size = Directory.EnumerateFiles(System.IO.Path.Combine(dir, ContentFolder), "*", SearchOption.AllDirectories)
                             .Sum(f => new FileInfo(f).Length);
        var backup = new Backup(System.IO.Path.GetFileName(dir), _now(), profile.Kind, profile.FolderName, profile.Name, reason,
                                isFull, profile.MainDir, files, dir, size);
        File.WriteAllText(System.IO.Path.Combine(dir, ManifestName), JsonSerializer.Serialize(backup with { Path = "" }, Json));
        return backup;
    }

    private static Backup? TryRead(string dir)
    {
        try
        {
            string manifest = System.IO.Path.Combine(dir, ManifestName);
            return File.Exists(manifest) && JsonSerializer.Deserialize<Backup>(File.ReadAllText(manifest)) is { } b
                ? b with { Path = dir }
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    internal static bool SameVolume(string a, string b) =>
        string.Equals(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(a)),
                      System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    internal static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var d in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(System.IO.Path.Combine(dest, System.IO.Path.GetRelativePath(source, d)));
        }

        foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(f, System.IO.Path.Combine(dest, System.IO.Path.GetRelativePath(source, f)));
        }
    }
}
