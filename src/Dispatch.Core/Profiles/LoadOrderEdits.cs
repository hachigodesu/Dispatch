namespace Dispatch.Core.Profiles;

/// <summary>
/// Pure list operations on a top-first load order (index 0 = highest priority). Used for bulk edits from the UI and for
/// "add to / remove from profile". No I/O; saving goes through <see cref="LoadOrderService"/>.
/// </summary>
public static class LoadOrderEdits
{
    /// <summary>Adds mods at the top (like the game does for newly enabled mods), keeping their given order. Mods already active are left where they are.</summary>
    public static List<ActiveModEntry> AddToTop(IReadOnlyList<ActiveModEntry> topFirst, IEnumerable<ActiveModEntry> mods)
    {
        var active = topFirst.Select(e => e.PackageId).ToHashSet(StringComparer.Ordinal);
        var added = mods.Where(m => active.Add(m.PackageId)).ToList();
        return [.. added, .. topFirst];
    }

    public static List<ActiveModEntry> Remove(IReadOnlyList<ActiveModEntry> topFirst, IEnumerable<string> packageIds)
    {
        var remove = packageIds.ToHashSet(StringComparer.Ordinal);
        return topFirst.Where(e => !remove.Contains(e.PackageId)).ToList();
    }

    /// <summary>Puts another mod in a mod's place (e.g. the newer copy for an older one). A mod that ends up twice is kept once, at its first place.</summary>
    public static List<ActiveModEntry> Replace(IReadOnlyList<ActiveModEntry> topFirst, IReadOnlyDictionary<string, ActiveModEntry> replacements) =>
        topFirst.Select(e => replacements.GetValueOrDefault(e.PackageId) ?? e).DistinctBy(e => e.PackageId, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Moves the selected items one step up (towards index 0) as a block. Items already at the top stay, and the
    /// selected items keep their relative order. Returns the new order.
    /// </summary>
    public static List<T> MoveUp<T>(IReadOnlyList<T> items, ISet<T> selected)
    {
        var list = items.ToList();
        for (int i = 1; i < list.Count; i++)
        {
            if (selected.Contains(list[i]) && !selected.Contains(list[i - 1]))
            {
                (list[i - 1], list[i]) = (list[i], list[i - 1]);
            }
        }

        return list;
    }

    public static List<T> MoveDown<T>(IReadOnlyList<T> items, ISet<T> selected)
    {
        var list = items.ToList();
        for (int i = list.Count - 2; i >= 0; i--)
        {
            if (selected.Contains(list[i]) && !selected.Contains(list[i + 1]))
            {
                (list[i + 1], list[i]) = (list[i], list[i + 1]);
            }
        }

        return list;
    }

    public static List<T> MoveToTop<T>(IReadOnlyList<T> items, ISet<T> selected) =>
        [.. items.Where(selected.Contains), .. items.Where(i => !selected.Contains(i))];

    public static List<T> MoveToBottom<T>(IReadOnlyList<T> items, ISet<T> selected) =>
        [.. items.Where(i => !selected.Contains(i)), .. items.Where(selected.Contains)];

    /// <summary>
    /// Moves the selected items as a block so they start at <paramref name="insertIndex"/> (an index in the original
    /// list, as a drop target reports it). Relative order is kept.
    /// </summary>
    public static List<T> MoveTo<T>(IReadOnlyList<T> items, ISet<T> selected, int insertIndex)
    {
        int before = items.Take(Math.Clamp(insertIndex, 0, items.Count)).Count(selected.Contains);
        var rest = items.Where(i => !selected.Contains(i)).ToList();
        int at = Math.Clamp(insertIndex - before, 0, rest.Count);
        rest.InsertRange(at, items.Where(selected.Contains));
        return rest;
    }
}

/// <summary>Deleting a profile = moving its whole folder into Dispatch's backups (restorable).</summary>
public static class ProfileRemover
{
    public static Safety.Backup Delete(Safety.SafeWriter writer, Safety.BackupService backups, ProfileInfo profile)
    {
        if (profile.IsReadOnly)
        {
            throw new InvalidOperationException(
                "Steam Cloud profiles can't be deleted in Dispatch. Delete it in the game, or turn off \"Use Steam Cloud\" for it first.");
        }

        writer.ThrowIfGameRunning();
        if (!Directory.Exists(profile.MainDir))
        {
            throw new InvalidOperationException("The profile folder no longer exists.");
        }

        return backups.BackupWholeFolder(profile, Safety.BackupReason.Delete, move: true);
    }
}
