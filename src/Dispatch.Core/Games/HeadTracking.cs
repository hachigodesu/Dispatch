using Microsoft.Win32;

namespace Dispatch.Core.Games;

/// <summary>
/// Head-tracking programs Dispatch can start with the game: OpenTrack (turns tracking into game input) and AITrack
/// (webcam face tracking that feeds OpenTrack). OpenTrack starts first so AITrack has something to send to.
/// </summary>
public static class HeadTracking
{
    /// <summary>OpenTrack's usual place, from its uninstall entry or Program Files; null when not found.</summary>
    public static string? FindOpenTrack()
    {
        foreach (string? folder in UninstallLocations("opentrack").Concat(ProgramFolders("opentrack")))
        {
            if (folder is not null && File.Exists(Path.Combine(folder, "opentrack.exe")))
            {
                return Path.Combine(folder, "opentrack.exe");
            }
        }

        return null;
    }

    /// <summary>AITrack's usual place: it installs into a versioned folder ("aitrack-v0.7.1"); the newest wins.</summary>
    public static string? FindAiTrack() =>
        UninstallLocations("aitrack").Concat(ProgramFolders("aitrack*"))
            .OfType<string>()
            .Select(folder => Path.Combine(folder, "AITrack.exe"))
            .Where(File.Exists)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    /// <summary>
    /// The programs to start, in start order (OpenTrack, then AITrack): the configured ones that exist and aren't
    /// running already (a second copy would fight the first for the camera or the port).
    /// </summary>
    public static IReadOnlyList<string> ToStart(string? openTrack, string? aiTrack, Func<string, bool> isRunning) =>
        new[] { openTrack, aiTrack }
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Select(p => p!)
            .Where(p => !isRunning(Path.GetFileNameWithoutExtension(p)))
            .ToList();

    private static IEnumerable<string?> ProgramFolders(string pattern)
    {
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string dir = Environment.GetFolderPath(root);
            if (dir.Length == 0 || !Directory.Exists(dir))
            {
                continue;
            }

            string[] found;
            try
            {
                found = Directory.GetDirectories(dir, pattern);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string folder in found)
            {
                yield return folder;
            }
        }

        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        if (Directory.Exists(local))
        {
            foreach (string folder in Directory.GetDirectories(local, pattern))
            {
                yield return folder;
            }
        }
    }

    /// <summary>InstallLocation of uninstall entries whose name starts with the program's name.</summary>
    private static IEnumerable<string?> UninstallLocations(string name)
    {
        var views = new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        foreach (var (hive, path) in views)
        {
            using var key = hive.OpenSubKey(path);
            foreach (string sub in key?.GetSubKeyNames() ?? [])
            {
                using var entry = key!.OpenSubKey(sub);
                if (entry?.GetValue("DisplayName") is string display && display.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    yield return entry.GetValue("InstallLocation") as string;
                }
            }
        }
    }
}
