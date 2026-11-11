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
    IReadOnlyDictionary<string, string> NamesFromProfiles)
{
    /// <summary>Reads the whole picture. Read-only: nothing on disk is modified (except Dispatch's own cache).</summary>
    public static GameSnapshot Load(IPlatform platform, AppPaths paths, AppSettings settings,
                                    IProgress<(int Done, int Total)>? progress, CancellationToken cancel)
    {
        var env = GameDetector.Detect(platform, Dispatch.Core.Games.GameDefinition.Ets2, settings.ToOverrides());
        Log.Information("Detected game {Version} ({Source}), user data {UserData}, install {Install}, {Problems} problem(s)",
                        env.Version, env.VersionSource, env.UserDataDir, env.InstallDir, env.Problems.Count);

        var scan = new ModScanner(paths.Cache).Scan(env, progress, cancel);
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
        return new GameSnapshot(env, scan, log, profiles, compat, names);
    }
}
