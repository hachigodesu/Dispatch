using System.Globalization;

namespace Dispatch.Core.Detection;

public static class SteamLocator
{
    /// <summary>All Steam library roots: the Steam folder itself plus every "path" in libraryfolders.vdf.</summary>
    public static IReadOnlyList<string> FindLibraries(string steamPath)
    {
        var result = new List<string> { Normalize(steamPath) };
        foreach (var vdfPath in new[]
                 {
                     Path.Combine(steamPath, "steamapps", "libraryfolders.vdf"),
                     Path.Combine(steamPath, "config", "libraryfolders.vdf"),
                 })
        {
            if (!File.Exists(vdfPath))
            {
                continue;
            }

            VdfNode root;
            try
            {
                root = Vdf.ParseFile(vdfPath);
            }
            catch (FormatException)
            {
                continue;
            }

            var folders = root["libraryfolders"] ?? root["LibraryFolders"];
            foreach (var (_, node) in folders?.Children ?? [])
            {
                // New format: { "path" "D:\\SteamLibrary" ... }. Old format: "1" "D:\\SteamLibrary".
                string? path = node.Value ?? node.GetString("path");
                if (path is not null && Directory.Exists(path))
                {
                    string normalized = Normalize(path);
                    if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Add(normalized);
                    }
                }
            }

            break; // steamapps\ wins over config\ when both exist
        }

        return result;
    }

    /// <summary>Finds an installed app by its appmanifest (the "apps" list in libraryfolders.vdf can be stale).</summary>
    public static (string Library, string InstallDir, string? BuildId)? FindApp(IEnumerable<string> libraries, int appId)
    {
        foreach (var lib in libraries)
        {
            string acf = Path.Combine(lib, "steamapps", $"appmanifest_{appId.ToString(CultureInfo.InvariantCulture)}.acf");
            if (!File.Exists(acf))
            {
                continue;
            }

            try
            {
                var state = Vdf.ParseFile(acf)["AppState"];
                if (state?.GetString("installdir") is { } dir)
                {
                    return (lib, dir, state.GetString("buildid"));
                }
            }
            catch (FormatException)
            {
                // A broken manifest: try the next library.
            }
        }

        return null;
    }

    /// <summary>For "…\steamapps\common\Game", returns the library root; otherwise null.</summary>
    public static string? LibraryOf(string installDir)
    {
        var common = Directory.GetParent(Normalize(installDir));
        var steamapps = common?.Parent;
        return common?.Name.Equals("common", StringComparison.OrdinalIgnoreCase) == true &&
               steamapps?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) == true
            ? steamapps.Parent?.FullName
            : null;
    }

    /// <summary>Steam accounts on this PC that have data for the app (userdata\&lt;id&gt;\&lt;appid&gt;).</summary>
    public static IReadOnlyList<SteamAccount> FindAccounts(string steamPath, int appId, uint? activeAccountId)
    {
        string userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata))
        {
            return [];
        }

        var logins = new Dictionary<uint, (string Persona, bool MostRecent, ulong Id64)>();
        string loginFile = Path.Combine(steamPath, "config", "loginusers.vdf");
        if (File.Exists(loginFile))
        {
            try
            {
                foreach (var (key, node) in Vdf.ParseFile(loginFile)["users"]?.Children ?? [])
                {
                    if (ulong.TryParse(key, out ulong id64) && id64 > SteamAccount.SteamId64Base)
                    {
                        logins[(uint)(id64 - SteamAccount.SteamId64Base)] =
                            (node.GetString("PersonaName") ?? "", node.GetString("MostRecent") == "1", id64);
                    }
                }
            }
            catch (FormatException)
            {
                // Names are cosmetic; continue without them.
            }
        }

        // Logged-in user (registry) first; when Steam isn't running, the most recent login.
        uint? active = activeAccountId;
        if (active is null && logins.FirstOrDefault(l => l.Value.MostRecent) is { Key: not 0 } recent)
        {
            active = recent.Key;
        }
        var accounts = new List<SteamAccount>();
        foreach (var dir in Directory.EnumerateDirectories(userdata))
        {
            if (!uint.TryParse(Path.GetFileName(dir), out uint accountId) || accountId == 0 ||
                !Directory.Exists(Path.Combine(dir, appId.ToString(CultureInfo.InvariantCulture))))
            {
                continue;
            }

            logins.TryGetValue(accountId, out var login);
            accounts.Add(new SteamAccount(accountId, login.Id64 != 0 ? login.Id64 : SteamAccount.SteamId64Base + accountId,
                                          string.IsNullOrEmpty(login.Persona) ? $"Steam account {accountId}" : login.Persona,
                                          accountId == active, dir));
        }

        return accounts.OrderByDescending(a => a.IsActive).ThenBy(a => a.PersonaName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string Normalize(string path) => Path.GetFullPath(path.Replace('/', '\\')).TrimEnd('\\');
}
