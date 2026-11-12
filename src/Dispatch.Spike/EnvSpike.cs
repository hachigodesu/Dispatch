using Dispatch.Core.Detection;
using Dispatch.Core.Games;
using Dispatch.Core.Mods;
using Dispatch.Core.Platform;
using Dispatch.Core.Profiles;

namespace Dispatch.Spike;

/// <summary>M1 smoke test: run the Core services the app uses against the real PC (read-only).</summary>
internal static class EnvSpike
{
    public static int Run()
    {
        var platform = new WindowsPlatform();
        var env = GameDetector.Detect(platform, GameDefinition.Ets2);
        Console.WriteLine($"User data:   {env.UserDataDir}");
        Console.WriteLine($"Install:     {env.InstallDir}");
        Console.WriteLine($"Version:     {env.Version} (from {env.VersionSource}), build {env.SteamBuildId}");
        Console.WriteLine($"Libraries:   {string.Join(" | ", env.SteamLibraries)}");
        Console.WriteLine($"Accounts:    {env.Accounts.Count} ({env.Accounts.Count(a => a.IsActive)} active)");
        Console.WriteLine($"DLC:         {env.OwnedDlc.Count}");
        Console.WriteLine($"Problems:    {(env.Problems.Count == 0 ? "none" : string.Join(" / ", env.Problems))}");

        var scanner = new ModScanner(Path.Combine(platform.LocalAppDataFolder, "Dispatch", "cache"));
        var first = scanner.Scan(env);
        var second = scanner.Scan(env);
        Console.WriteLine($"Mods:        {first.Mods.Count} in {first.Elapsed.TotalSeconds:F1} s ({first.FromCache} cached); " +
                          $"rescan {second.Elapsed.TotalMilliseconds:F0} ms ({second.FromCache} cached)");

        var log = env.UserDataDir is null ? null : GameLog.Read(env.UserDataDir);
        var compat = first.Mods.Select(m => CompatibilityChecker.Evaluate(m, env.Version, env.OwnedDlc, log)).ToList();
        foreach (var g in compat.GroupBy(c => c.Status))
        {
            Console.WriteLine($"  {g.Key,-16} {g.Count()}");
        }

        Console.WriteLine($"  issues: {first.Mods.Sum(m => m.Issues.Count)} " +
                          $"({first.Mods.SelectMany(m => m.Issues).Count(i => i.Severity == IssueSeverity.Warning)} warnings)");

        if (log is not null)
        {
            var installed = first.Mods.Select(m => m.PackageId).ToHashSet();
            var withMessages = log.ModMessages.Where(kv => installed.Contains(kv.Key)).ToList();
            Console.WriteLine($"  game.log: messages for {withMessages.Count} installed mods " +
                              $"({log.ModMessages.Count - withMessages.Count} keys not matching an installed mod)");
            foreach (var g in withMessages.SelectMany(kv => kv.Value).GroupBy(m => System.Text.RegularExpressions.Regex.Replace(m.Message, "'[^']*'|for .*|= .*", "…")).OrderByDescending(g => g.Count()))
            {
                Console.WriteLine($"    {g.Count(),4}  {g.Key}");
            }
        }

        var byId = first.Mods.ToDictionary(m => m.PackageId);
        foreach (var p in ProfileRepository.List(env))
        {
            var order = ProfileRepository.ReadLoadOrder(p);
            int missing = order.TopFirst.Count(e => !byId.ContainsKey(e.PackageId));
            Console.WriteLine($"Profile [{p.Kind}] {p.FolderName}: {order.TopFirst.Count} mods ({missing} not installed), " +
                              $"{p.SaveCount} saves, top = {(order.TopFirst.Count > 0 ? order.TopFirst[0].DisplayName : "-")}");
        }

        return 0;
    }
}
