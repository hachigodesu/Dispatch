using System.Diagnostics;
using System.Globalization;
using Dispatch.Core.Detection;
using Dispatch.Core.Mods;

namespace Dispatch.Core.Games;

/// <summary>
/// One runnable copy of the game: the Steam install (started through Steam) or a copy of an older version kept in
/// another folder (started directly). Steam only keeps one version installed, so copies are how a profile can stay on
/// e.g. 1.60 while the Steam install moves on.
/// </summary>
/// <param name="SteamBranch">The Steam install's beta branch ("temporary_1_60"); null for the normal (public) branch and for copies.</param>
/// <param name="IsPacked">A packed copy (see <see cref="PackedCopy"/>): rebuilt into its game\ folder before playing.</param>
public sealed record GameInstallation(string Folder, GameVersion? Version, bool IsSteam, string? SteamBranch = null, bool IsPacked = false)
{
    /// <summary>The folder the game runs from: for a packed copy, its rebuilt game\ folder.</summary>
    public string GameFolder => IsPacked ? Path.Combine(Folder, PackedCopy.GameFolder) : Folder;

    public string ExePath(GameDefinition game) => Path.Combine(GameFolder, game.ExeRelativePath);
}

/// <summary>What Steam's app manifest says about the installed game.</summary>
/// <param name="Branch">The beta branch, or null for the public one.</param>
/// <param name="FullyInstalled">False while Steam downloads, updates or verifies the game, or files are missing (a queued update that hasn't started is fine).</param>
public sealed record SteamInstallState(string? Branch, bool FullyInstalled, long SizeOnDisk);

public static class GameInstallations
{
    /// <summary>Reads a game folder's version from its executable; null when it isn't a game folder.</summary>
    public static GameInstallation? ReadCopy(GameDefinition game, string folder, Func<string, GameVersion?>? readVersion = null)
    {
        if (PackedCopy.IsPacked(folder))
        {
            try
            {
                var recipe = PackRecipe.Read(folder, withFiles: false);
                return string.Equals(recipe.Game, game.Key, StringComparison.OrdinalIgnoreCase)
                    ? new GameInstallation(Path.GetFullPath(folder), GameVersion.TryParse(recipe.Version), IsSteam: false, IsPacked: true)
                    : null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
            {
                return null;
            }
        }

        string exe = Path.Combine(folder, game.ExeRelativePath);
        if (!File.Exists(exe))
        {
            return null;
        }

        return new GameInstallation(Path.GetFullPath(folder), (readVersion ?? ReadExeVersion)(exe), IsSteam: false);
    }

    public static GameVersion? ReadExeVersion(string exe)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            return GameVersion.TryParse(info.ProductVersion) ?? GameVersion.TryParse(info.FileVersion);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    // Steam's AppState flags (bits of StateFlags in the app manifest).
    private const long FullyInstalled = 4;
    private const long FilesMissing = 32;
    private const long FilesCorrupt = 128;
    private const long UpdateRunning = 256;
    private const long UpdateStarted = 1024;
    private const long Uninstalling = 2048;
    private const long Reconfiguring = 65536;
    private const long Validating = 131072;
    private const long AddingFiles = 262144;
    private const long Preallocating = 524288;
    private const long Downloading = 1048576;
    private const long Staging = 2097152;
    private const long Committing = 4194304;
    private const long UpdateStopping = 8388608;

    private const long Busy = FilesMissing | FilesCorrupt | UpdateRunning | UpdateStarted | Uninstalling | Reconfiguring | Validating |
                              AddingFiles | Preallocating | Downloading | Staging | Committing | UpdateStopping;

    /// <summary>
    /// Fully installed and Steam isn't touching the files. "Update required" (2) alone is fine: an update is queued
    /// (e.g. scheduled for later) but hasn't started, so the installed files are complete and unchanged.
    /// </summary>
    internal static bool IsIdle(long flags) => (flags & FullyInstalled) != 0 && (flags & Busy) == 0;

    /// <summary>Reads appmanifest_&lt;appid&gt;.acf next to a Steam install; null when the install isn't in a Steam library.</summary>
    public static SteamInstallState? ReadSteamState(GameDefinition game, string installDir)
    {
        if (SteamLocator.LibraryOf(installDir) is not { } library)
        {
            return null;
        }

        string acf = Path.Combine(library, "steamapps", $"appmanifest_{game.SteamAppId.ToString(CultureInfo.InvariantCulture)}.acf");
        if (!File.Exists(acf))
        {
            return null;
        }

        try
        {
            var state = Vdf.ParseFile(acf)["AppState"];
            if (state is null)
            {
                return null;
            }

            string? branch = state["MountedConfig"]?.GetString("BetaKey") ?? state["UserConfig"]?.GetString("BetaKey");
            if (string.IsNullOrEmpty(branch) || branch.Equals("public", StringComparison.OrdinalIgnoreCase))
            {
                branch = null;
            }

            bool full = state.GetString("StateFlags") is not { } text ||
                        (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long flags) && IsIdle(flags));
            long size = long.TryParse(state.GetString("SizeOnDisk"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) ? s : 0;
            return new SteamInstallState(branch, full, size);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>The automatic choice for a profile, and why.</summary>
/// <param name="Mismatches">Active mods that declare game versions but none matching this installation.</param>
/// <param name="Checked">Active mods that declare game versions at all.</param>
public sealed record VersionChoice(GameInstallation Installation, int Mismatches, int Checked);

public static class VersionPicker
{
    /// <summary>
    /// Mods that say which versions they're made for, but not this one. Workshop items always fit (the game picks a
    /// matching sub-package or the universal one), and mods without version information can't be judged.
    /// </summary>
    public static int CountMismatches(IEnumerable<ModInfo> mods, GameVersion version)
    {
        string v = version.ToString();
        return Declaring(mods).Count(m => !m.Manifest!.CompatibleVersions.Any(p => GameVersionMatch.Matches(p, v)));
    }

    /// <summary>
    /// The installation the profile's active mods fit best: fewest mods made for other versions, and among equals the
    /// newest (so a profile whose mods all support the latest version, or that declares nothing, gets the latest).
    /// Installations with an unknown version are only picked when nothing else is there.
    /// </summary>
    public static VersionChoice? Pick(IReadOnlyList<GameInstallation> installations, IReadOnlyCollection<ModInfo> activeMods)
    {
        if (installations.Count == 0)
        {
            return null;
        }

        int declaring = Declaring(activeMods).Count();
        var known = installations.Where(i => i.Version is not null).ToList();
        if (known.Count == 0)
        {
            return new VersionChoice(installations.FirstOrDefault(i => i.IsSteam) ?? installations[0], 0, declaring);
        }

        return known
            .Select(i => new VersionChoice(i, CountMismatches(activeMods, i.Version!), declaring))
            .OrderBy(c => c.Mismatches)
            .ThenByDescending(c => c.Installation.Version!, VersionComparer.Instance)
            .ThenByDescending(c => c.Installation.IsSteam) // same version as a copy: prefer launching through Steam
            .First();
    }

    private static IEnumerable<ModInfo> Declaring(IEnumerable<ModInfo> mods) =>
        mods.Where(m => m.Source == ModSource.Local && m.Manifest is { CompatibleVersions.Count: > 0 });
}

public sealed class VersionComparer : IComparer<GameVersion>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(GameVersion? x, GameVersion? y)
    {
        if (x is null || y is null)
        {
            return (x is null ? 0 : 1) - (y is null ? 0 : 1);
        }

        int c = x.Major.CompareTo(y.Major);
        c = c != 0 ? c : x.Minor.CompareTo(y.Minor);
        c = c != 0 ? c : x.Patch.CompareTo(y.Patch);
        return c != 0 ? c : x.Build.CompareTo(y.Build);
    }
}
