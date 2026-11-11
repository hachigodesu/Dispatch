using System.Globalization;
using System.Text.RegularExpressions;

namespace Dispatch.Core.Mods;

public enum DuplicateKind
{
    /// <summary>Two packages with the same ID (e.g. "x.scs" and a folder "x"): the game can only use one.</summary>
    SameId,

    /// <summary>Different packages with the same manifest display_name: usually two versions of one mod.</summary>
    SameName,
}

/// <param name="IsOlder">This copy has a lower version (or, without versions, older files) than another copy in the group.</param>
public sealed record DuplicateInfo(DuplicateKind Kind, IReadOnlyList<ModInfo> Others, bool IsOlder, ModInfo Newest);

/// <summary>Finds mods installed more than once and marks the older copies (PLAN.md v1).</summary>
public static partial class ModDuplicates
{
    public static IReadOnlyDictionary<ModInfo, DuplicateInfo> Find(IReadOnlyList<ModInfo> mods)
    {
        var result = new Dictionary<ModInfo, DuplicateInfo>(ReferenceEqualityComparer.Instance);

        foreach (var group in mods.GroupBy(m => m.PackageId, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            Mark(group.ToList(), DuplicateKind.SameId, result);
        }

        // Same name: only manifests with a real display name; ignore very generic names.
        foreach (var group in mods.Where(m => m.Manifest?.DisplayName is { Length: >= 4 })
                                  .GroupBy(m => Normalize(m.Manifest!.DisplayName!), StringComparer.Ordinal)
                                  .Where(g => g.Select(m => m.PackageId).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            Mark(group.Where(m => !result.ContainsKey(m)).ToList(), DuplicateKind.SameName, result);
        }

        return result;
    }

    private static void Mark(List<ModInfo> group, DuplicateKind kind, Dictionary<ModInfo, DuplicateInfo> result)
    {
        if (group.Count < 2)
        {
            return;
        }

        var newest = group.OrderByDescending(m => m, NewerFirst.Instance).First();
        foreach (var m in group)
        {
            bool older = !ReferenceEquals(m, newest) && NewerFirst.Instance.Compare(m, newest) < 0;
            result[m] = new DuplicateInfo(kind, group.Where(o => !ReferenceEquals(o, m)).ToList(), older, newest);
        }
    }

    private static string Normalize(string name) => WhitespaceRegex().Replace(name.Trim().ToLowerInvariant(), " ");

    /// <summary>Compares versions like "1.2.10" numerically ("1.10" &gt; "1.9"); falls back to file dates.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        var pa = NumberRegex().Matches(a ?? "").Select(m => long.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        var pb = NumberRegex().Matches(b ?? "").Select(m => long.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        if (pa.Count == 0 || pb.Count == 0)
        {
            return 0;
        }

        for (int i = 0; i < Math.Max(pa.Count, pb.Count); i++)
        {
            long x = i < pa.Count ? pa[i] : 0, y = i < pb.Count ? pb[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }

    private sealed class NewerFirst : IComparer<ModInfo>
    {
        public static readonly NewerFirst Instance = new();

        public int Compare(ModInfo? x, ModInfo? y)
        {
            int v = CompareVersions(x?.Manifest?.PackageVersion, y?.Manifest?.PackageVersion);
            return v != 0 ? v : Nullable.Compare(x?.LastWriteUtc, y?.LastWriteUtc);
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberRegex();
}
