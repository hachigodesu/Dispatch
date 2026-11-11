using Dispatch.Core.Detection;
using Dispatch.Core.Sii;

namespace Dispatch.Core.Profiles;

public enum ProfileKind
{
    Local,
    SteamCloud,
}

/// <param name="MainDir">Folder holding profile.sii and save\ (for cloud profiles: Steam\userdata\…\remote\profiles\&lt;hex&gt;).</param>
/// <param name="LocalPartDir">Cloud profiles only: the machine-local part in Documents\…\steam_profiles\&lt;hex&gt;.</param>
public sealed record ProfileInfo(
    string Name,
    string FolderName,
    ProfileKind Kind,
    string MainDir,
    string? LocalPartDir,
    SteamAccount? Owner,
    DateTime LastWriteUtc,
    int SaveCount)
{
    public string ProfileSiiPath => Path.Combine(MainDir, "profile.sii");

    /// <summary>Steam Cloud profiles are read-only in the MVP (PLAN.md §5.10).</summary>
    public bool IsReadOnly => Kind == ProfileKind.SteamCloud;
}

/// <summary>A profile's load order, top of the in-game list (highest priority) first.</summary>
public sealed record LoadOrderRead(IReadOnlyList<ActiveModEntry> TopFirst, IReadOnlyList<SiiFormat> Layers, DateTime FileWriteUtc);

public static class ProfileRepository
{
    public static IReadOnlyList<ProfileInfo> List(GameEnvironment env)
    {
        var profiles = new List<ProfileInfo>();
        if (env.UserDataDir is { } userData)
        {
            foreach (var dir in ProfileDirs(Path.Combine(userData, "profiles")))
            {
                profiles.Add(Describe(dir, ProfileKind.Local, null, null));
            }
        }

        foreach (var account in env.Accounts)
        {
            string remote = Path.Combine(account.RemoteDir(env.Game.SteamAppId), "profiles");
            foreach (var dir in ProfileDirs(remote))
            {
                string? localPart = env.UserDataDir is null
                    ? null
                    : Path.Combine(env.UserDataDir, "steam_profiles", Path.GetFileName(dir));
                profiles.Add(Describe(dir, ProfileKind.SteamCloud, Directory.Exists(localPart) ? localPart : null, account));
            }
        }

        return profiles.OrderBy(p => p.Kind).ThenByDescending(p => p.LastWriteUtc).ToList();
    }

    /// <summary>Decodes profile.sii (read-only) and returns its active mods top-first.</summary>
    /// <exception cref="InvalidDataException">The file can't be decoded.</exception>
    public static LoadOrderRead ReadLoadOrder(ProfileInfo profile)
    {
        byte[] data = ReadShared(profile.ProfileSiiPath);
        var decoded = SiiFileCodec.Decode(data);
        var fileOrder = ProfileText.ReadActiveMods(decoded.Text);

        // active_mods[0] is the BOTTOM of the in-game list (spike S3), so reverse for top-first.
        return new LoadOrderRead(fileOrder.Reverse().ToList(), decoded.Layers, File.GetLastWriteTimeUtc(profile.ProfileSiiPath));
    }

    /// <summary>Only folders whose name is valid hex-encoded UTF-8 and that contain profile.sii are profiles.</summary>
    private static IEnumerable<string> ProfileDirs(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (ProfileNameCodec.TryFromFolderName(Path.GetFileName(dir), out _) && File.Exists(Path.Combine(dir, "profile.sii")))
            {
                yield return dir;
            }
        }
    }

    private static ProfileInfo Describe(string dir, ProfileKind kind, string? localPart, SteamAccount? owner)
    {
        string folder = Path.GetFileName(dir);
        ProfileNameCodec.TryFromFolderName(folder, out string name);
        string saveDir = Path.Combine(dir, "save");
        int saves = Directory.Exists(saveDir) ? Directory.EnumerateDirectories(saveDir).Count() : 0;
        return new ProfileInfo(name, folder, kind, dir, localPart, owner,
                               File.GetLastWriteTimeUtc(Path.Combine(dir, "profile.sii")), saves);
    }

    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[fs.Length];
        fs.ReadExactly(buffer);
        return buffer;
    }
}
