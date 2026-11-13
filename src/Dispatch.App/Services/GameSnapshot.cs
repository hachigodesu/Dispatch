using System.IO;
using Dispatch.Core.Detection;
using Dispatch.Core.Mods;
using Dispatch.Core.Platform;
using Dispatch.Core.Profiles;
using Serilog;

namespace Dispatch.App.Services;

/// <summary>One profile and its decoded load order (or the error that prevented reading it).</summary>
public sealed record ProfileSnapshot(ProfileInfo Profile, LoadOrderRead? LoadOrder, string? Error);

/// <summary>Everything the UI shows, loaded in one go on a background thread. Immutable.</summary>
public sealed record GameSnapshot(
    GameEnvironment Environment,
    ModScanResult Scan,
    GameLogInfo? GameLog,
    IReadOnlyList<ProfileSnapshot> Profiles,
    IReadOnlyDictionary<string, Compatibility> Compatibility,
    IReadOnlyDictionary<string, string> NamesFromProfiles,
    IReadOnlyList<Dispatch.Core.Games.GameDefinition> InstalledGames)
{
    /// <summary>Package IDs in any profile's load order (local and Steam Cloud).</summary>
    public HashSet<string> UsedPackageIds { get; } =
        Profiles.SelectMany(p => p.LoadOrder?.TopFirst ?? []).Select(e => e.PackageId).ToHashSet(StringComparer.Ordinal);

    /// <summary>False when a profile couldn't be read: then nobody can say which mods are unused.</summary>
    public bool AllProfilesRead => Profiles.All(p => p.LoadOrder is not null);

    /// <summary>Reads the whole picture. Read-only: nothing on disk is modified (except Dispatch's own cache).</summary>
    public static GameSnapshot Load(IPlatform platform, AppPaths paths, AppSettings settings, Dispatch.Core.Games.GameDefinition game,
                                    IProgress<(int Done, int Total)>? progress, CancellationToken cancel)
    {
        var env = GameDetector.Detect(platform, game, settings.ToOverrides(game));
        Log.Information("Detected game {Version} ({Source}), user data {UserData}, install {Install}, {Problems} problem(s)",
                        env.Version, env.VersionSource, env.UserDataDir, env.InstallDir, env.Problems.Count);

        var scan = new ModScanner(paths.ForGame(game).Cache).Scan(env, progress, cancel);
        Log.Information("Scanned {Count} mods in {Seconds:F1}s ({Cached} from cache)", scan.Mods.Count, scan.Elapsed.TotalSeconds, scan.FromCache);

        var log = env.UserDataDir is null ? null : Dispatch.Core.Detection.GameLog.Read(env.UserDataDir);
        var profiles = new List<ProfileSnapshot>();
        foreach (var p in ProfileRepository.List(env))
        {
            try
            {
                profiles.Add(new ProfileSnapshot(p, ProfileRepository.ReadLoadOrder(p), null));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Log.Warning(ex, "Profile {Folder} couldn't be read", p.FolderName);
                profiles.Add(new ProfileSnapshot(p, null, ex.Message));
            }
        }

        // Display names stored in profiles ("…|Name") are the best names for Workshop and unreadable mods.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in profiles.SelectMany(p => p.LoadOrder?.TopFirst ?? []))
        {
            names.TryAdd(entry.PackageId, entry.DisplayName);
        }

        // Two packages can share an ID (e.g. "x.scs" and a folder "x"); the first one wins here,
        // duplicate detection proper is a v1 feature.
        var compat = new Dictionary<string, Compatibility>(StringComparer.Ordinal);
        foreach (var m in scan.Mods)
        {
            compat.TryAdd(m.PackageId, CompatibilityChecker.Evaluate(m, env.Version, env.OwnedDlc, log));
        }
        return new GameSnapshot(env, scan, log, profiles, compat, names, FindInstalledGames(platform, settings, env));
    }

    /// <summary>The games that look installed (install folder or Documents folder found). The shown game is always listed.</summary>
    private static List<Dispatch.Core.Games.GameDefinition> FindInstalledGames(IPlatform platform, AppSettings settings, GameEnvironment current) =>
        Dispatch.Core.Games.GameDefinition.All.Where(g =>
        {
            if (g == current.Game)
            {
                return true;
            }

            var env = GameDetector.Detect(platform, g, settings.ToOverrides(g));
            return env.InstallDir is not null || (env.UserDataDir is not null && Directory.Exists(Path.Combine(env.UserDataDir, "profiles")));
        }).ToList();
}
