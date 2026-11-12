using System.Collections.Concurrent;
using Dispatch.Core.Mods.Packages;

namespace Dispatch.Core.Mods;

/// <summary>The game files a mod provides, as path hashes (see <see cref="ModPackage.PathHash"/>). Sorted, no duplicates.</summary>
public sealed record ModFileSet(string PackageId, ulong[] Hashes);

/// <summary>
/// Builds and caches <see cref="ModFileSet"/>s. Reading the file tables is fast (all 54 mods of a big profile, 700k files,
/// in about a second), so sets are built on demand and kept in memory until the package changes.
/// Mod metadata at the package root (manifest.sii, icon, description) is excluded: it never overrides game files.
/// </summary>
public static class ModFileIndex
{
    private static readonly ConcurrentDictionary<(string Path, long Size, DateTime Mtime), ModFileSet> Cache = new();

    /// <summary>Root-level metadata names that every mod has; excluded even when a protected archive hides its names.</summary>
    private static readonly string[] MetadataNames =
    [
        "manifest.sii", "versions.sii", "description.txt", "mod_description.txt", "readme.txt", "icon.jpg", "mod_icon.jpg",
    ];

    private static readonly HashSet<ulong> MetadataHashes = MetadataNames.Select(ModPackage.PathHash).ToHashSet();

    public static ModFileSet Get(ModInfo mod)
    {
        var key = (mod.PackagePath, mod.SizeBytes, mod.LastWriteUtc);
        return Cache.GetOrAdd(key, _ => Build(mod));
    }

    public static ModFileSet Build(ModInfo mod)
    {
        if (mod.Format == PackageFormat.Opaque)
        {
            return new ModFileSet(mod.PackageId, []);
        }

        try
        {
            using var pkg = ModPackage.Open(mod.PackagePath);
            var exclude = new HashSet<ulong>(MetadataHashes);
            foreach (var name in new[] { mod.Manifest?.IconFile, mod.Manifest?.DescriptionFile }.OfType<string>())
            {
                exclude.Add(ModPackage.PathHash(name));
            }

            foreach (var root in pkg.ListDirectory("") ?? [])
            {
                if (!root.EndsWith('/'))
                {
                    exclude.Add(ModPackage.PathHash(root)); // root-level files are mod metadata, not game files
                }
            }

            var hashes = pkg.FileHashes().Where(h => !exclude.Contains(h)).Distinct().ToArray();
            Array.Sort(hashes);
            return new ModFileSet(mod.PackageId, hashes);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ModFileSet(mod.PackageId, []);
        }
    }

    /// <summary>
    /// Finds names for some hashes (for showing examples). HashFS archives may hide names, so some stay unknown.
    /// Stops early once all are found.
    /// </summary>
    public static IReadOnlyDictionary<ulong, string> ResolveNames(ModInfo mod, IEnumerable<ulong> hashes)
    {
        var wanted = hashes.ToHashSet();
        var found = new Dictionary<ulong, string>();
        if (wanted.Count == 0 || mod.Format == PackageFormat.Opaque)
        {
            return found;
        }

        try
        {
            using var pkg = ModPackage.Open(mod.PackagePath);
            foreach (var path in pkg.FilePaths())
            {
                ulong h = ModPackage.PathHash(path);
                if (wanted.Remove(h) || wanted.Remove(Packages.CityHash.Hash64(path)))
                {
                    found[h] = path;
                    if (wanted.Count == 0)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Names are a nicety; the counts are still right.
        }

        return found;
    }

    public static void ClearCache() => Cache.Clear();
}

/// <summary>Files of <see cref="LoserId"/> that <see cref="WinnerId"/> replaces (the winner is higher in the load order).</summary>
public sealed record ModConflict(string WinnerId, string LoserId, int FileCount, IReadOnlyList<ulong> Sample);

public sealed record ConflictReport(IReadOnlyList<ModConflict> Pairs)
{
    public static readonly ConflictReport Empty = new([]);

    /// <summary>Conflicts where this mod wins (it replaces other mods' files).</summary>
    public IEnumerable<ModConflict> Overrides(string packageId) => Pairs.Where(p => p.WinnerId == packageId);

    /// <summary>Conflicts where this mod loses (some of its files are replaced by higher mods).</summary>
    public IEnumerable<ModConflict> OverriddenBy(string packageId) => Pairs.Where(p => p.LoserId == packageId);
}

public static class ConflictAnalyzer
{
    /// <param name="topFirst">The load order, highest priority first. A file provided by several mods comes from the highest one.</param>
    /// <param name="sampleSize">How many example hashes to keep per pair.</param>
    public static ConflictReport Analyze(IReadOnlyList<ModFileSet> topFirst, int sampleSize = 25)
    {
        var owner = new Dictionary<ulong, int>(topFirst.Sum(s => s.Hashes.Length));
        var pairs = new Dictionary<(int Winner, int Loser), (int Count, List<ulong> Sample)>();
        for (int i = 0; i < topFirst.Count; i++)
        {
            foreach (var h in topFirst[i].Hashes)
            {
                if (owner.TryGetValue(h, out int winner))
                {
                    if (winner == i)
                    {
                        continue; // the same mod twice in the list
                    }

                    var key = (winner, i);
                    var (count, sample) = pairs.TryGetValue(key, out var v) ? v : (0, new List<ulong>());
                    if (sample.Count < sampleSize)
                    {
                        sample.Add(h);
                    }

                    pairs[key] = (count + 1, sample);
                }
                else
                {
                    owner[h] = i;
                }
            }
        }

        return new ConflictReport(pairs
            .Select(kv => new ModConflict(topFirst[kv.Key.Winner].PackageId, topFirst[kv.Key.Loser].PackageId, kv.Value.Count, kv.Value.Sample))
            .OrderByDescending(c => c.FileCount).ThenBy(c => c.WinnerId, StringComparer.Ordinal).ToList());
    }
}
