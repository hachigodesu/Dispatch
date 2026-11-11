using System.Security.Cryptography;
using Dispatch.Core.Platform;
using Dispatch.Core.Profiles;

namespace Dispatch.Core.Safety;

/// <summary>The game is running; nothing was written.</summary>
public sealed class GameRunningException(string message) : InvalidOperationException(message);

/// <summary>A target file changed after Dispatch read it (usually: the game saved). Nothing was written.</summary>
public sealed class ConcurrentChangeException(string message) : InvalidOperationException(message);

/// <param name="RelativePath">Path inside the profile folder, e.g. "profile.sii".</param>
/// <param name="Content">The complete new file content.</param>
/// <param name="ExpectedHash">SHA-256 of the file as it was when Dispatch read it; the write is refused if it differs.</param>
public sealed record FileWrite(string RelativePath, byte[] Content, byte[] ExpectedHash);

/// <param name="Verify">Checks the written temp file (e.g. decodes it and compares the mod list); throws if wrong.</param>
public sealed record WritePlan(
    ProfileInfo Profile,
    BackupReason Reason,
    IReadOnlyList<FileWrite> Writes,
    IReadOnlyList<string> AlsoBackUp,
    Action<string, byte[]>? Verify = null);

/// <summary>
/// The single write path for game files (PLAN.md §3). Order: game closed? → unchanged since read? → backup →
/// temp file → verify → game still closed? → atomic replace. On failure after the swap began, the backup is restored.
/// </summary>
public sealed class SafeWriter(IPlatform platform, BackupService backups, string processName)
{
    public static byte[] Hash(byte[] content) => SHA256.HashData(content);

    public static byte[] HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(fs);
    }

    public void ThrowIfGameRunning()
    {
        if (platform.IsProcessRunning(processName))
        {
            throw new GameRunningException("The game is running. Close it completely, then try again. Nothing was changed.");
        }
    }

    public Backup Commit(WritePlan plan)
    {
        if (plan.Profile.IsReadOnly)
        {
            throw new InvalidOperationException("Steam Cloud profiles are read-only in Dispatch.");
        }

        ThrowIfGameRunning();
        foreach (var w in plan.Writes)
        {
            string target = Path.Combine(plan.Profile.MainDir, w.RelativePath);
            if (!File.Exists(target) || !HashFile(target).AsSpan().SequenceEqual(w.ExpectedHash))
            {
                throw new ConcurrentChangeException(
                    $"{w.RelativePath} changed since Dispatch read it (did the game save?). Reload the profile and try again. Nothing was changed.");
            }
        }

        var backup = backups.BackupFiles(plan.Profile, plan.Reason, plan.Writes.Select(w => w.RelativePath).Concat(plan.AlsoBackUp));

        var temps = new List<(string Temp, string Target)>();
        try
        {
            foreach (var w in plan.Writes)
            {
                string target = Path.Combine(plan.Profile.MainDir, w.RelativePath);
                string temp = target + ".dispatch-tmp";
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(w.Content);
                    fs.Flush(flushToDisk: true);
                }

                byte[] readBack = File.ReadAllBytes(temp);
                if (!readBack.AsSpan().SequenceEqual(w.Content))
                {
                    throw new IOException($"Verifying {w.RelativePath} failed: the written file differs.");
                }

                plan.Verify?.Invoke(w.RelativePath, readBack);
                temps.Add((temp, target));
            }
        }
        catch
        {
            DeleteQuietly(temps.Select(t => t.Temp).Concat(plan.Writes.Select(w => Path.Combine(plan.Profile.MainDir, w.RelativePath) + ".dispatch-tmp")));
            throw;
        }

        // Last check right before touching the real files: the game may have been started meanwhile.
        try
        {
            ThrowIfGameRunning();
        }
        catch
        {
            DeleteQuietly(temps.Select(t => t.Temp));
            throw;
        }

        var replaced = new List<string>();
        try
        {
            foreach (var (temp, target) in temps)
            {
                File.Replace(temp, target, destinationBackupFileName: null);
                replaced.Add(target);
            }
        }
        catch
        {
            // Put back what was already replaced, from the backup taken above.
            string content = BackupService.ContentDir(backup);
            foreach (var target in replaced)
            {
                File.Copy(Path.Combine(content, Path.GetRelativePath(plan.Profile.MainDir, target)), target, overwrite: true);
            }

            DeleteQuietly(temps.Select(t => t.Temp));
            throw;
        }

        return backup;
    }

    /// <summary>
    /// Restores a small backup into its profile folder. The current files are backed up first (PreRestore),
    /// so a restore can itself be undone.
    /// </summary>
    public Backup Restore(Backup backup, ProfileInfo profile)
    {
        if (backup.IsFullProfile)
        {
            throw new InvalidOperationException("Full-profile backups are restored with RestoreFolder.");
        }

        if (profile.IsReadOnly)
        {
            throw new InvalidOperationException("Steam Cloud profiles are read-only in Dispatch.");
        }

        ThrowIfGameRunning();
        var pre = backups.BackupFiles(profile, BackupReason.PreRestore, backup.Files);
        string content = BackupService.ContentDir(backup);
        ThrowIfGameRunning();
        foreach (var rel in backup.Files)
        {
            string target = Path.Combine(profile.MainDir, rel);
            string temp = target + ".dispatch-tmp";
            File.Copy(Path.Combine(content, rel), temp, overwrite: true);
            if (File.Exists(target))
            {
                File.Replace(temp, target, null);
            }
            else
            {
                File.Move(temp, target);
            }
        }

        return pre;
    }

    /// <summary>
    /// Restores a full-profile backup (e.g. undoing a rename) to its original folder. Refuses if that folder
    /// exists again, so nothing is ever overwritten. The backup itself is kept (copied, not moved).
    /// </summary>
    public string RestoreFolder(Backup backup)
    {
        if (!backup.IsFullProfile)
        {
            throw new InvalidOperationException("Not a full-profile backup.");
        }

        ThrowIfGameRunning();
        if (Directory.Exists(backup.OriginalDir))
        {
            throw new InvalidOperationException($"A profile folder already exists at {backup.OriginalDir}; nothing was changed.");
        }

        string parent = Path.GetDirectoryName(backup.OriginalDir)!;
        string staging = Path.Combine(parent, $".dispatch-staging-{Guid.NewGuid():N}");
        try
        {
            BackupService.CopyDirectory(BackupService.ContentDir(backup), staging);
            ThrowIfGameRunning();
            Directory.Move(staging, backup.OriginalDir);
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            throw;
        }

        return backup.OriginalDir;
    }

    private static void DeleteQuietly(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            try
            {
                File.Delete(f);
            }
            catch (IOException)
            {
                // Best effort; a stray .dispatch-tmp file is harmless.
            }
        }
    }
}
