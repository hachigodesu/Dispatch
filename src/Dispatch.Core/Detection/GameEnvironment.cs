using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Dispatch.Core.Games;
using Dispatch.Core.Platform;

namespace Dispatch.Core.Detection;

public sealed record GameVersion(int Major, int Minor, int Patch, int Build)
{
    /// <summary>Parses "1.62.2.1", "1.62.2.1s" or "1.62.2.1 (4c8b31…)".</summary>
    public static GameVersion? TryParse(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var m = Regex.Match(text, @"(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        if (!m.Success)
        {
            return null;
        }

        static int Part(Group g) => g.Success ? int.Parse(g.Value, CultureInfo.InvariantCulture) : 0;
        return new GameVersion(Part(m.Groups[1]), Part(m.Groups[2]), Part(m.Groups[3]), Part(m.Groups[4]));
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}.{Build}";
}

public sealed record SteamAccount(uint AccountId, ulong SteamId64, string PersonaName, bool IsActive, string UserDataDir)
{
    public const ulong SteamId64Base = 76561197960265728UL;

    /// <summary>Steam Cloud folder for a game: userdata\&lt;id&gt;\&lt;appid&gt;\remote.</summary>
    public string RemoteDir(int appId) => Path.Combine(UserDataDir, appId.ToString(CultureInfo.InvariantCulture), "remote");
}

/// <summary>User-chosen paths that replace auto-detection (Settings → manual override).</summary>
public sealed record DetectionOverrides(string? InstallDir = null, string? UserDataDir = null);

/// <summary>What Dispatch found. Every part is optional; <see cref="Problems"/> says what's missing and why.</summary>
public sealed record GameEnvironment(
    GameDefinition Game,
    string? UserDataDir,
    bool UserDataOverridden,
    string? SteamPath,
    IReadOnlyList<string> SteamLibraries,
    string? InstallDir,
    bool InstallOverridden,
    string? ExePath,
    GameVersion? Version,
    string? VersionSource,
    string? SteamBuildId,
    IReadOnlyList<SteamAccount> Accounts,
    IReadOnlySet<string> OwnedDlc,
    IReadOnlyList<string> Problems)
{
    public string? ModDir => UserDataDir is null ? null : Path.Combine(UserDataDir, "mod");

    /// <summary>Workshop content folders for this game in every known Steam library.</summary>
    public IEnumerable<string> WorkshopContentDirs =>
        SteamLibraries.Select(l => Path.Combine(l, "steamapps", "workshop", "content",
                                                Game.SteamAppId.ToString(CultureInfo.InvariantCulture)))
                      .Where(Directory.Exists)
                      .Distinct(StringComparer.OrdinalIgnoreCase);
}

public static class GameDetector
{
    public static GameEnvironment Detect(IPlatform platform, GameDefinition game, DetectionOverrides? overrides = null)
    {
        overrides ??= new DetectionOverrides();
        var problems = new List<string>();

        // --- user data (Documents\Euro Truck Simulator 2) ---
        string? userData = overrides.UserDataDir ?? Path.Combine(platform.DocumentsFolder, game.DocumentsFolderName);
        if (!Directory.Exists(userData))
        {
            problems.Add($"The game's user folder wasn't found at {userData}. Start the game once, or choose the folder in Settings.");
            userData = null;
        }

        // --- Steam, libraries, install ---
        string? steam = platform.SteamPath;
        var libraries = steam is null ? [] : SteamLocator.FindLibraries(steam);
        if (steam is null)
        {
            problems.Add("Steam wasn't found in the registry.");
        }

        string? install = overrides.InstallDir;
        string? buildId = null;
        if (install is null)
        {
            var app = SteamLocator.FindApp(libraries, game.SteamAppId);
            if (app is not null)
            {
                install = Path.Combine(app.Value.Library, "steamapps", "common", app.Value.InstallDir);
                buildId = app.Value.BuildId;
            }
        }
        else if (SteamLocator.LibraryOf(install) is { } lib && !libraries.Contains(lib, StringComparer.OrdinalIgnoreCase))
        {
            libraries = [.. libraries, lib]; // so Workshop items of a manually chosen install are found too
        }

        string? exe = install is null ? null : Path.Combine(install, game.ExeRelativePath);
        if (install is null || !Directory.Exists(install))
        {
            problems.Add($"{game.DisplayName} wasn't found in any Steam library. Choose the install folder in Settings.");
            install = null;
            exe = null;
        }
        else if (!File.Exists(exe))
        {
            problems.Add($"The game executable wasn't found at {exe}.");
            exe = null;
        }

        // --- version: exe first, game.log.txt as fallback ---
        GameVersion? version = null;
        string? versionSource = null;
        if (exe is not null)
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            version = GameVersion.TryParse(info.ProductVersion) ?? GameVersion.TryParse(info.FileVersion);
            versionSource = version is null ? null : "executable";
        }

        if (version is null && userData is not null && GameLog.Read(userData)?.Version is { } logVersion)
        {
            version = logVersion;
            versionSource = "game.log.txt (last launch)";
        }

        if (version is null)
        {
            problems.Add("The game version couldn't be determined, so compatibility can't be checked.");
        }

        var accounts = steam is null ? [] : SteamLocator.FindAccounts(steam, game.SteamAppId, platform.ActiveSteamAccountId);
        var dlc = install is null
            ? new HashSet<string>()
            : Directory.EnumerateFiles(install, "dlc_*.scs").Select(Path.GetFileNameWithoutExtension)
                       .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new GameEnvironment(game, userData, overrides.UserDataDir is not null, steam, libraries,
                                   install, overrides.InstallDir is not null, exe, version, versionSource, buildId,
                                   accounts, dlc, problems);
    }
}
