using System.IO;
using System.Text.Json;
using Dispatch.Core.Detection;
using Dispatch.Core.Games;

namespace Dispatch.App.Services;

/// <summary>Where Dispatch keeps its own data: %LOCALAPPDATA%\Dispatch\.</summary>
public sealed class AppPaths(string localAppData)
{
    public string Root { get; } = Path.Combine(localAppData, "Dispatch");

    public string Cache => Path.Combine(Root, "cache");

    public string Logs => Path.Combine(Root, "logs");

    public string Backups => Path.Combine(Root, "backups");

    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>
    /// Per-game data. ETS2 keeps the folders Dispatch has always used (so existing backups, notes and presets stay where
    /// they are); other games get a subfolder each, e.g. %LOCALAPPDATA%\Dispatch\ats\backups.
    /// </summary>
    public GamePaths ForGame(GameDefinition game) => game.Id == GameId.Ets2
        ? new GamePaths(Cache, Backups, Path.Combine(Root, "notes.json"), Path.Combine(Root, "presets"))
        : new GamePaths(Path.Combine(Root, game.Key, "cache"), Path.Combine(Root, game.Key, "backups"),
                        Path.Combine(Root, game.Key, "notes.json"), Path.Combine(Root, game.Key, "presets"));
}

public sealed record GamePaths(string Cache, string Backups, string NotesFile, string Presets);

/// <summary>User settings, saved as JSON. Unknown or broken files fall back to defaults.</summary>
public sealed class AppSettings
{
    public string? InstallDirOverride { get; set; }

    public string? UserDataDirOverride { get; set; }

    /// <summary>The game Dispatch shows ("ets2" or "ats").</summary>
    public string Game { get; set; } = GameDefinition.Ets2.Key;

    public string? AtsInstallDirOverride { get; set; }

    public string? AtsUserDataDirOverride { get; set; }

    /// <summary>Ask GitHub for a newer Dispatch at most once a day when the app starts.</summary>
    public bool CheckForUpdates { get; set; } = true;

    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>UI language as a culture name ("de", "pt-BR"); null = same as Windows.</summary>
    public string? Language { get; set; }

    /// <summary>A version the user chose to skip ("0.6.0").</summary>
    public string? SkippedUpdateVersion { get; set; }

    public int BackupRetentionSmall { get; set; } = 20;

    public int BackupRetentionFull { get; set; } = 5;

    /// <summary>The manual paths for one game (the original two settings are ETS2's).</summary>
    public (string? Install, string? UserData) PathsFor(GameDefinition game) =>
        game.Id == GameId.Ets2 ? (InstallDirOverride, UserDataDirOverride) : (AtsInstallDirOverride, AtsUserDataDirOverride);

    public void SetPathsFor(GameDefinition game, string? install, string? userData)
    {
        install = string.IsNullOrWhiteSpace(install) ? null : install.Trim();
        userData = string.IsNullOrWhiteSpace(userData) ? null : userData.Trim();
        if (game.Id == GameId.Ets2)
        {
            (InstallDirOverride, UserDataDirOverride) = (install, userData);
        }
        else
        {
            (AtsInstallDirOverride, AtsUserDataDirOverride) = (install, userData);
        }
    }

    public DetectionOverrides ToOverrides(GameDefinition game)
    {
        var (install, userData) = PathsFor(game);
        return new(string.IsNullOrWhiteSpace(install) ? null : install.Trim(), string.IsNullOrWhiteSpace(userData) ? null : userData.Trim());
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file)) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Settings file {File} couldn't be read; using defaults", file);
        }

        return new AppSettings();
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, file, overwrite: true);
    }
}
