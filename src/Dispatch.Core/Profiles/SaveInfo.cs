using Dispatch.Core.Sii;
using Dispatch.Core.Sii.Text;

namespace Dispatch.Core.Profiles;

/// <summary>One entry of a save's <c>dependencies[]</c>: "mod|&lt;package id&gt;|&lt;name&gt;", "dlc|…" or "rdlc|…".</summary>
public sealed record SaveDependency(string Kind, string PackageId, string DisplayName);

/// <summary>What a save's info.sii says (read-only).</summary>
/// <param name="Slot">The save's folder name ("1", "autosave", "autosave_drive_2"…).</param>
/// <param name="Name">The name the player gave it; empty for autosaves.</param>
public sealed record SaveInfo(string Slot, string Name, DateTime WriteUtc, IReadOnlyList<SaveDependency> Dependencies)
{
    /// <summary>The mods the game loaded when it wrote this save (in its own order).</summary>
    public IEnumerable<SaveDependency> Mods => Dependencies.Where(d => d.Kind == "mod");

    public string Label => Name.Length > 0 ? Name : Slot;
}

public static class SaveReader
{
    /// <summary>
    /// The profile's newest save (the one "Continue" loads), or null when it has none or it can't be read. Only
    /// info.sii is read (small); the save itself isn't touched.
    /// </summary>
    public static SaveInfo? Newest(ProfileInfo profile)
    {
        string saves = Path.Combine(profile.MainDir, "save");
        if (!Directory.Exists(saves))
        {
            return null;
        }

        var newest = new DirectoryInfo(saves).EnumerateDirectories()
            .Select(d => new FileInfo(Path.Combine(d.FullName, "info.sii")))
            .Where(f => f.Exists)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        return newest is null ? null : TryRead(newest.DirectoryName!);
    }

    public static SaveInfo? TryRead(string saveFolder)
    {
        try
        {
            string file = Path.Combine(saveFolder, "info.sii");
            string text = SiiFileCodec.Decode(File.ReadAllBytes(file)).Text;
            var unit = SiiUnitReader.Read(text).FirstOrDefault(u => u.ClassName == "save_container");
            if (unit is null)
            {
                return null;
            }

            var dependencies = new List<SaveDependency>();
            foreach (string value in unit.GetArray("dependencies"))
            {
                var parts = value.Split('|', 3);
                if (parts.Length >= 2)
                {
                    dependencies.Add(new SaveDependency(parts[0], parts[1], parts.Length > 2 ? parts[2] : parts[1]));
                }
            }

            return new SaveInfo(Path.GetFileName(saveFolder), unit.Get("name") ?? "", File.GetLastWriteTimeUtc(file), dependencies);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or FormatException)
        {
            return null;
        }
    }
}
