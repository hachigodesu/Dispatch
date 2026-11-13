using System.Globalization;
using Dispatch.Core.Platform;
using Dispatch.Core.Safety;

namespace Dispatch.Core.Mods;

public enum LibraryMoveOutcome
{
    Moved,
    Skipped,
    Failed,
}

public sealed record LibraryMove(ModInfo Mod, string Target, LibraryMoveOutcome Outcome, string Message);

/// <summary>
/// The mod library: a folder next to mod\ for local mods no profile uses. The game reads every package in mod\ when it
/// starts and lists them all in its mod manager, so keeping unused ones aside makes it start faster and keeps that
/// list short. Mods are only moved (same drive: instant, nothing is copied or changed) and never overwrite anything;
/// every move is written to a journal in the library folder. Workshop items are never touched.
/// </summary>
public static class ModLibrary
{
    public const string FolderName = "mod library (Dispatch)";

    /// <summary>One line per move: UTC time, from, to. Kept in the library folder.</summary>
    public const string JournalFile = "dispatch-moves.txt";

    /// <summary>Local mods in mod\ that no profile uses (by package ID): these can go to the library.</summary>
    public static List<ModInfo> Unused(IEnumerable<ModInfo> mods, IReadOnlySet<string> usedPackageIds) =>
        mods.Where(m => m.Source == ModSource.Local && !m.InLibrary && !usedPackageIds.Contains(m.PackageId)).ToList();

    /// <summary>Mods in the library that a profile uses: these must go back to mod\ for the game to load them.</summary>
    public static List<ModInfo> NeededBack(IEnumerable<ModInfo> mods, IReadOnlySet<string> usedPackageIds) =>
        mods.Where(m => m.InLibrary && usedPackageIds.Contains(m.PackageId)).ToList();

    /// <summary>
    /// Moves each mod into <paramref name="targetDir"/> (the library, or mod\ to bring them back). A mod whose name is
    /// already taken there is skipped, never overwritten. Stops as soon as the game is running.
    /// </summary>
    /// <exception cref="GameRunningException">The game is running before anything was moved.</exception>
    public static IReadOnlyList<LibraryMove> Move(IPlatform platform, string processName, IEnumerable<ModInfo> mods, string targetDir)
    {
        if (platform.IsProcessRunning(processName))
        {
            throw new GameRunningException("The game is running. Close it before moving mods. Nothing was moved.");
        }

        var results = new List<LibraryMove>();
        string root = Path.GetPathRoot(Path.GetFullPath(targetDir))!;
        foreach (var mod in mods)
        {
            string target = Path.Combine(targetDir, Path.GetFileName(Path.TrimEndingDirectorySeparator(mod.Path)));
            if (platform.IsProcessRunning(processName))
            {
                results.Add(new(mod, target, LibraryMoveOutcome.Skipped, "The game was started, so the rest wasn't moved."));
                continue;
            }

            if (mod.Source == ModSource.Workshop)
            {
                results.Add(new(mod, target, LibraryMoveOutcome.Skipped, "Workshop items are managed by Steam and never moved."));
                continue;
            }

            bool isFolder = Directory.Exists(mod.Path);
            if (!isFolder && !File.Exists(mod.Path))
            {
                results.Add(new(mod, target, LibraryMoveOutcome.Failed, "It's no longer where it was."));
                continue;
            }

            if (File.Exists(target) || Directory.Exists(target))
            {
                results.Add(new(mod, target, LibraryMoveOutcome.Skipped, "A mod with this name is already there; nothing was overwritten."));
                continue;
            }

            if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(mod.Path)), root, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new(mod, target, LibraryMoveOutcome.Failed, "It's on another drive than the library."));
                continue;
            }

            try
            {
                Directory.CreateDirectory(targetDir);
                if (isFolder)
                {
                    Directory.Move(mod.Path, target);
                }
                else
                {
                    File.Move(mod.Path, target, overwrite: false);
                }

                Journal(targetDir, mod.Path, target);
                results.Add(new(mod, target, LibraryMoveOutcome.Moved, "Moved."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new(mod, target, LibraryMoveOutcome.Failed, ex.Message));
            }
        }

        return results;
    }

    private static void Journal(string targetDir, string from, string to)
    {
        // The library's journal, whichever way the mod went (the library is the target or the source's folder).
        string library = Path.GetFileName(Path.TrimEndingDirectorySeparator(targetDir)) == FolderName
            ? targetDir
            : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(from))!;
        try
        {
            File.AppendAllText(Path.Combine(library, JournalFile),
                               string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:O}\t{from}\t{to}{Environment.NewLine}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The move itself happened; the journal is only for finding things later.
        }
    }
}
