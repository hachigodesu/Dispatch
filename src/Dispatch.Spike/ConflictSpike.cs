using System.Diagnostics;
using Dispatch.Core.Detection;
using Dispatch.Core.Games;
using Dispatch.Core.Mods;
using Dispatch.Core.Mods.Packages;
using Dispatch.Core.Platform;
using Dispatch.Core.Profiles;

namespace Dispatch.Spike;

/// <summary>v2 spike (read-only): can file-path hashes be compared across formats, and how fast is indexing?</summary>
internal static class ConflictSpike
{
    public static int Run(string profileName)
    {
        var platform = new WindowsPlatform();
        var env = GameDetector.Detect(platform, GameDefinition.Ets2);
        var scan = new ModScanner(Path.Combine(platform.LocalAppDataFolder, "Dispatch", "cache")).Scan(env);
        var byId = scan.Mods.GroupBy(m => m.PackageId).ToDictionary(g => g.Key, g => g.First());
        var profile = ProfileRepository.List(env).First(p => p.Name == profileName);
        var order = ProfileRepository.ReadLoadOrder(profile).TopFirst;

        // 1) Hash compatibility: names from HashFS listings must hash to the stored entry hashes.
        foreach (var mod in scan.Mods.Where(m => m.Format is PackageFormat.HashFsV1 or PackageFormat.HashFsV2).Take(4))
        {
            using var pkg = ModPackage.Open(mod.PackagePath);
            var hashes = pkg.FileHashes().ToHashSet();
            var paths = pkg.FilePaths().Take(5000).ToList();
            int matched = paths.Count(p => hashes.Contains(ModPackage.PathHash(p)));
            Console.WriteLine($"{mod.Format} {mod.PackageId}: {hashes.Count} files, {paths.Count} names checked, {matched} hash-match");
        }

        // 2) Index all active mods of the profile.
        long before = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();
        var index = new List<(ActiveModEntry Entry, HashSet<ulong> Hashes, long Ms)>();
        foreach (var e in order)
        {
            if (!byId.TryGetValue(e.PackageId, out var mod))
            {
                continue;
            }

            var t = Stopwatch.StartNew();
            try
            {
                using var pkg = ModPackage.Open(mod.PackagePath);
                index.Add((e, pkg.FileHashes().ToHashSet(), t.ElapsedMilliseconds));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                Console.WriteLine($"  {e.PackageId}: {ex.Message}");
            }
        }

        sw.Stop();
        long total = index.Sum(i => (long)i.Hashes.Count);
        Console.WriteLine($"Indexed {index.Count} mods, {total:N0} files in {sw.Elapsed.TotalSeconds:F1} s; " +
                          $"memory ~{(GC.GetTotalMemory(true) - before) / 1048576.0:F0} MB");
        foreach (var slow in index.OrderByDescending(i => i.Ms).Take(3))
        {
            Console.WriteLine($"  slowest: {slow.Entry.PackageId} {slow.Hashes.Count:N0} files {slow.Ms} ms");
        }

        // 3) Overlaps (top-first: earlier = higher priority = wins).
        var owner = new Dictionary<ulong, int>();
        var pairs = new Dictionary<(int Winner, int Loser), int>();
        for (int i = 0; i < index.Count; i++)
        {
            foreach (var h in index[i].Hashes)
            {
                if (owner.TryGetValue(h, out int w))
                {
                    pairs[(w, i)] = pairs.GetValueOrDefault((w, i)) + 1;
                }
                else
                {
                    owner[h] = i;
                }
            }
        }

        Console.WriteLine($"Conflicting pairs: {pairs.Count}");
        foreach (var ((w, l), n) in pairs.OrderByDescending(p => p.Value).Take(12))
        {
            Console.WriteLine($"  {index[w].Entry.DisplayName}  overrides  {index[l].Entry.DisplayName}: {n} files");
        }

        return 0;
    }
}
