using System.Diagnostics;
using System.Globalization;
using Dispatch.Core.Platform;

namespace Dispatch.Core.Games;

/// <summary>
/// Keeps a game version in another folder so it can still be played after Steam moves on.
/// <para>
/// On the same NTFS drive as the install, files are <b>shared</b> (hard links) instead of copied: the copy takes almost
/// no space at first. Steam updates a game by building each changed file anew and moving it into place, so the copy
/// keeps the old file and only from then on uses space for it. Should Steam ever change a shared file in place
/// instead, the copy would change too; <see cref="GameCopyRecord"/> notices that, and the copy is then marked broken.
/// On another drive every file is copied.
/// </para>
/// <para>
/// Only reads the source and never writes to a shared file. Never overwrites: the destination must not exist. Files
/// go to a staging folder first (&lt;destination&gt;.dispatch-partial), which becomes the destination only when it's
/// complete; a failed or cancelled copy removes the staging folder again.
/// </para>
/// </summary>
public static class GameCopier
{
    public const string PartialSuffix = ".dispatch-partial";

    /// <summary>Space to leave free on the destination drive besides what the copy writes.</summary>
    public const long Headroom = 1L << 30;

    /// <param name="Share">True: files become hard links to the install's files (same NTFS drive).</param>
    public sealed record Plan(string Source, string Destination, long TotalBytes, int FileCount, bool Share)
    {
        /// <summary>Space the copy needs now (shared files need none).</summary>
        public long BytesToWrite => Share ? 0 : TotalBytes;
    }

    /// <summary>Checks everything that can be checked before copying; throws InvalidOperationException with the reason.</summary>
    /// <param name="allowSharing">False forces a full copy even on the same drive.</param>
    public static Plan Prepare(IPlatform platform, GameDefinition game, string source, string destination, bool allowSharing = true)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (!File.Exists(Path.Combine(source, game.ExeRelativePath)))
        {
            throw new InvalidOperationException($"{source} doesn't contain {game.ExeRelativePath}.");
        }

        if (IsInside(destination, source) || IsInside(source, destination))
        {
            throw new InvalidOperationException("The copy must go to a folder outside the game's install folder.");
        }

        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new InvalidOperationException($"{destination} already exists. Dispatch never overwrites a folder; choose another one.");
        }

        if (platform.IsProcessRunning(game.ProcessName))
        {
            throw new InvalidOperationException($"{game.DisplayName} is running. Close it before copying.");
        }

        var (total, count) = Measure(source);
        string root = Path.GetPathRoot(destination) ?? destination;
        bool share = allowSharing && CanShare(source, destination) && ProbeLink(Path.Combine(source, game.ExeRelativePath), destination);
        long free = new DriveInfo(root).AvailableFreeSpace;
        string partial = destination + PartialSuffix;
        if (Directory.Exists(partial) && !share)
        {
            free += Measure(partial).Bytes; // a leftover from an interrupted copy is removed first
        }

        long needed = (share ? 0 : total) + Headroom;
        if (free < needed)
        {
            throw new InsufficientSpaceException(root, needed, free);
        }

        return new Plan(source, destination, total, count, share);
    }

    /// <summary>Hard links need both folders on the same NTFS volume.</summary>
    public static bool CanShare(string source, string destination)
    {
        string a = Path.GetPathRoot(Path.GetFullPath(source)) ?? "";
        string b = Path.GetPathRoot(Path.GetFullPath(destination)) ?? "";
        if (a.Length == 0 || !string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return string.Equals(new DriveInfo(a).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Tries one link next to the destination (and removes it again): the drive may allow links in general but not
    /// for these files or this folder. Removing a link only removes that name; the game's file stays as it is.
    /// </summary>
    internal static bool ProbeLink(string existing, string destination)
    {
        string parent = Path.GetDirectoryName(destination)!;
        string probe = Path.Combine(parent, ".dispatch-link-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(parent);
            return NativeFiles.TryCreateHardLink(probe, existing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A stray zero-cost link name; harmless.
            }
        }
    }

    /// <summary>Makes the copy and returns how long it took. Progress reports bytes done so far (shared files count fully).</summary>
    public static TimeSpan Copy(IPlatform platform, GameDefinition game, Plan plan, IProgress<long>? progress, CancellationToken cancel,
                                string? version = null)
    {
        string partial = plan.Destination + PartialSuffix;
        if (Directory.Exists(partial))
        {
            // Only Dispatch makes folders with this suffix: what's left of an earlier, interrupted copy. Deleting a
            // shared file only removes this name for it; the install's file isn't touched.
            Directory.Delete(partial, recursive: true);
        }

        var watch = Stopwatch.StartNew();
        long done = 0;
        var shared = new List<GameCopyRecord.SharedFile>();
        try
        {
            Directory.CreateDirectory(partial);
            var buffer = plan.Share ? [] : new byte[4 << 20];
            foreach (var file in new DirectoryInfo(plan.Source).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cancel.ThrowIfCancellationRequested();
                if (platform.IsProcessRunning(game.ProcessName))
                {
                    throw new InvalidOperationException($"{game.DisplayName} was started during the copy. The copy was stopped.");
                }

                string relative = Path.GetRelativePath(plan.Source, file.FullName);
                string target = Path.Combine(partial, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (plan.Share && NativeFiles.TryCreateHardLink(target, file.FullName))
                {
                    file.Refresh();
                    shared.Add(new GameCopyRecord.SharedFile(relative, file.Length, file.LastWriteTimeUtc.Ticks));
                    done += file.Length;
                    progress?.Report(done);
                    continue;
                }

                if (plan.Share)
                {
                    // Linking can fail for single files (e.g. too many links); copy those instead.
                    buffer = buffer.Length == 0 ? new byte[4 << 20] : buffer;
                }

                CopyFile(file, target, buffer, ref done, progress, cancel);
            }

            // The copy must still match what was planned: if Steam touched the source meanwhile, the copy may be a mix.
            var (bytes, files) = Measure(partial);
            if (files != plan.FileCount || bytes != plan.TotalBytes)
            {
                throw new IOException(string.Create(CultureInfo.InvariantCulture,
                    $"The game folder changed during the copy ({files} files / {bytes} bytes instead of {plan.FileCount} / {plan.TotalBytes}). Is Steam updating the game?"));
            }

            GameCopyRecord.Write(partial, new GameCopyRecord(1, game.Key, version, DateTime.UtcNow, shared));
            Directory.Move(partial, plan.Destination);
            return watch.Elapsed;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static void CopyFile(FileInfo file, string target, byte[] buffer, ref long done, IProgress<long>? progress, CancellationToken cancel)
    {
        using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
        {
            output.SetLength(input.Length);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                done += read;
                progress?.Report(done);
            }

            if (output.Length != file.Length)
            {
                throw new IOException($"{file.FullName} changed while it was copied. Is Steam updating the game?");
            }
        }

        File.SetLastWriteTimeUtc(target, file.LastWriteTimeUtc);
    }

    /// <summary>Total size and number of files, not counting Dispatch's own record file.</summary>
    internal static (long Bytes, int Files) Measure(string folder)
    {
        long bytes = 0;
        int files = 0;
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (string.Equals(f.FullName, Path.Combine(folder, GameCopyRecord.FileName), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bytes += f.Length;
            files++;
        }

        return (bytes, files);
    }

    internal static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; the next copy to the same place removes it first.
        }
    }

    internal static bool IsInside(string path, string folder)
    {
        string f = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        return string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class InsufficientSpaceException(string drive, long needed, long free)
    : InvalidOperationException($"Not enough free space on {drive}: {needed} bytes needed, {free} free.")
{
    public string Drive { get; } = drive;

    public long Needed { get; } = needed;

    public long Free { get; } = free;
}
