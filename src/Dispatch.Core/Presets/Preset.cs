using System.Text.Json;
using System.Text.Json.Serialization;
using Dispatch.Core.Mods;
using Dispatch.Core.Profiles;

namespace Dispatch.Core.Presets;

/// <summary>One mod in a preset. Enough to find it again on another PC (PLAN.md §5.11).</summary>
public sealed record PresetEntry(
    string PackageId,
    string DisplayName,
    ulong? WorkshopId = null,
    string? FileName = null,
    string? PackageVersion = null);

/// <summary>A shareable load order: <c>*.dispatch-preset.json</c>. <see cref="Mods"/> is top-first (highest priority first).</summary>
public sealed record Preset(
    int Schema,
    string App,
    string Game,
    string Name,
    string? GameVersion,
    DateTime CreatedUtc,
    string? Notes,
    IReadOnlyList<PresetEntry> Mods)
{
    public const int CurrentSchema = 1;
    public const string FileExtension = ".dispatch-preset.json";

    /// <summary>Presets are small; anything bigger than this isn't a preset (protects against odd files).</summary>
    public const long MaxFileBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Preset Create(string name, IEnumerable<ActiveModEntry> topFirst, IReadOnlyDictionary<string, ModInfo> installed,
                                string? gameVersion, string? notes = null, Games.GameDefinition? game = null) =>
        new(CurrentSchema, "Dispatch", (game ?? Games.GameDefinition.Ets2).Key, name, gameVersion, DateTime.UtcNow, notes,
            topFirst.Select(e =>
            {
                installed.TryGetValue(e.PackageId, out var mod);
                return new PresetEntry(
                    e.PackageId,
                    e.DisplayName,
                    ModInfo.TryParseWorkshopId(e.PackageId),
                    mod is { Source: ModSource.Local } ? System.IO.Path.GetFileName(mod.Path) : null,
                    mod?.Manifest?.PackageVersion);
            }).ToList());

    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Loads and validates a preset file. Unknown fields are ignored; newer schemas are refused.</summary>
    /// <exception cref="InvalidDataException">Not a valid Dispatch preset.</exception>
    /// <param name="game">The game the preset must be for (default ETS2). Presets for the other game are refused.</param>
    public static Preset Load(string path, Games.GameDefinition? game = null)
    {
        game ??= Games.GameDefinition.Ets2;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxFileBytes)
        {
            throw new InvalidDataException("This file isn't a Dispatch preset (missing or too large).");
        }

        Preset? preset;
        try
        {
            preset = JsonSerializer.Deserialize<Preset>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"This file isn't a valid Dispatch preset: {ex.Message}", ex);
        }

        if (preset is null || preset.Mods is null || string.IsNullOrWhiteSpace(preset.Name))
        {
            throw new InvalidDataException("This file isn't a valid Dispatch preset.");
        }

        if (preset.Schema > CurrentSchema)
        {
            throw new InvalidDataException("This preset was made by a newer version of Dispatch. Update Dispatch to open it.");
        }

        if (!string.Equals(preset.Game, game.Key, StringComparison.OrdinalIgnoreCase))
        {
            string other = Games.GameDefinition.All.FirstOrDefault(g => string.Equals(g.Key, preset.Game, StringComparison.OrdinalIgnoreCase))
                               ?.DisplayName ?? preset.Game;
            throw new InvalidDataException($"This preset is for another game ({other}).");
        }

        if (preset.Mods.Any(m => string.IsNullOrWhiteSpace(m.PackageId) || m.PackageId.Contains('|', StringComparison.Ordinal) ||
                                 m.PackageId.Any(char.IsControl)))
        {
            throw new InvalidDataException("This preset contains an invalid mod entry.");
        }

        return preset with { Mods = preset.Mods.Select(m => m with { DisplayName = m.DisplayName ?? m.PackageId }).ToList() };
    }
}

public enum PresetMatch
{
    /// <summary>Installed under the same package ID.</summary>
    Exact,

    /// <summary>A local mod with the preset's file name, but another package ID (e.g. renamed in-game).</summary>
    ByFileName,

    Missing,
}

public sealed record ResolvedPresetEntry(PresetEntry Entry, PresetMatch Match, ModInfo? Mod)
{
    /// <summary>The entry to write into a profile ("id|Name"), using the installed mod's ID when it differs.</summary>
    public ActiveModEntry? ToActiveMod() => Mod is null ? null : new ActiveModEntry(Mod.PackageId, Entry.DisplayName);

    public string? WorkshopUrl => Entry.WorkshopId is { } id
        ? $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}"
        : null;
}

public static class PresetResolver
{
    public static IReadOnlyList<ResolvedPresetEntry> Resolve(Preset preset, IEnumerable<ModInfo> installed)
    {
        var byId = new Dictionary<string, ModInfo>(StringComparer.Ordinal);
        var byFile = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in installed)
        {
            byId.TryAdd(m.PackageId, m);
            if (m.Source == ModSource.Local)
            {
                byFile.TryAdd(System.IO.Path.GetFileName(m.Path), m);
            }
        }

        return preset.Mods.Select(e =>
            byId.TryGetValue(e.PackageId, out var exact) ? new ResolvedPresetEntry(e, PresetMatch.Exact, exact)
            : e.FileName is { } f && byFile.TryGetValue(f, out var file) ? new ResolvedPresetEntry(e, PresetMatch.ByFileName, file)
            : new ResolvedPresetEntry(e, PresetMatch.Missing, null)).ToList();
    }
}
