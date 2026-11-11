using System.IO;
using System.Text.Json;
using Dispatch.Core.Detection;

namespace Dispatch.App.Services;

/// <summary>Where Dispatch keeps its own data: %LOCALAPPDATA%\Dispatch\.</summary>
public sealed class AppPaths(string localAppData)
{
    public string Root { get; } = Path.Combine(localAppData, "Dispatch");

    public string Cache => Path.Combine(Root, "cache");

    public string Logs => Path.Combine(Root, "logs");

    public string Backups => Path.Combine(Root, "backups");

    public string SettingsFile => Path.Combine(Root, "settings.json");
}

/// <summary>User settings, saved as JSON. Unknown or broken files fall back to defaults.</summary>
public sealed class AppSettings
{
    public string? InstallDirOverride { get; set; }

    public string? UserDataDirOverride { get; set; }

    public int BackupRetentionSmall { get; set; } = 20;

    public int BackupRetentionFull { get; set; } = 5;

    public DetectionOverrides ToOverrides() =>
        new(string.IsNullOrWhiteSpace(InstallDirOverride) ? null : InstallDirOverride.Trim(),
            string.IsNullOrWhiteSpace(UserDataDirOverride) ? null : UserDataDirOverride.Trim());

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
