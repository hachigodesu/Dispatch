using System.Text.Json;

namespace Dispatch.Core.Mods;

public sealed record ModNote(IReadOnlyList<string> Tags, string Note)
{
    public static readonly ModNote Empty = new([], "");

    public bool IsEmpty => Tags.Count == 0 && Note.Length == 0;

    /// <summary>"map, needs promods ,  map" → ["map", "needs promods"].</summary>
    public static IReadOnlyList<string> ParseTags(string text) =>
        text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
}

/// <summary>Your own tags and notes per mod, in %LOCALAPPDATA%\Dispatch\notes.json (keyed by package ID).</summary>
public sealed class ModNotesStore(string file)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private Dictionary<string, ModNote> _notes = new(StringComparer.Ordinal);

    public static ModNotesStore Load(string file)
    {
        var store = new ModNotesStore(file);
        try
        {
            if (File.Exists(file) &&
                JsonSerializer.Deserialize<Dictionary<string, ModNote>>(File.ReadAllText(file)) is { } data)
            {
                store._notes = new Dictionary<string, ModNote>(data, StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A broken notes file shouldn't stop the app; it's kept aside so nothing is lost.
            try
            {
                File.Copy(file, file + ".broken", overwrite: true);
            }
            catch (IOException)
            {
            }
        }

        return store;
    }

    public ModNote Get(string packageId) => _notes.GetValueOrDefault(packageId) ?? ModNote.Empty;

    public void Set(string packageId, ModNote note)
    {
        if (note.IsEmpty)
        {
            _notes.Remove(packageId);
        }
        else
        {
            _notes[packageId] = note;
        }

        Save();
    }

    public IEnumerable<string> AllTags => _notes.Values.SelectMany(n => n.Tags).Distinct(StringComparer.CurrentCultureIgnoreCase);

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_notes, Json));
        File.Move(temp, file, overwrite: true);
    }
}
