using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Resources;
using Dispatch.Core.Presets;
using Dispatch.Core.Profiles;
using Serilog;

namespace Dispatch.App.ViewModels;

/// <summary>Presets panel of the Profiles tab (PLAN.md M4). Presets live in %LOCALAPPDATA%\Dispatch\presets.</summary>
public sealed partial class ProfilesViewModel
{
    private string _presetsDir = "";
    private string? _gameVersion;
    private Dispatch.Core.Games.GameDefinition _presetGame = Dispatch.Core.Games.GameDefinition.Ets2;

    public ObservableCollection<PresetItemViewModel> Presets { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyPresetCommand), nameof(ExportPresetCommand), nameof(DeletePresetCommand))]
    private PresetItemViewModel? _selectedPreset;

    public void ConfigurePresets(string presetsDir, string? gameVersion, Dispatch.Core.Games.GameDefinition game)
    {
        _presetsDir = presetsDir;
        _presetGame = game;
        _gameVersion = gameVersion;
        RefreshPresets();
    }

    private void RefreshPresets()
    {
        string? keep = SelectedPreset?.Path;
        Presets.Clear();
        if (!Directory.Exists(_presetsDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_presetsDir, "*" + Preset.FileExtension))
        {
            try
            {
                Presets.Add(new PresetItemViewModel(Preset.Load(file, _presetGame), file));
            }
            catch (InvalidDataException ex)
            {
                Log.Warning(ex, "Skipping invalid preset {File}", file);
            }
        }

        var sorted = Presets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Presets.Clear();
        foreach (var p in sorted)
        {
            Presets.Add(p);
        }

        SelectedPreset = Presets.FirstOrDefault(p => p.Path == keep);
    }

    [RelayCommand]
    private void SavePreset()
    {
        if (Selected is null)
        {
            return;
        }

        string? name = _dialogs.AskText(Strings.PresetNameTitle, Strings.PresetNamePrompt, Selected.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var installed = _mods.Values.ToDictionary(m => m.PackageId, m => m.Mod, StringComparer.Ordinal);
        var preset = Preset.Create(name.Trim(), LoadOrder.Select(l => l.Entry), installed, _gameVersion, game: _presetGame);
        string path = UniquePresetPath(name);
        preset.Save(path);
        Log.Information("Saved preset {Name} ({Count} mods) to {Path}", preset.Name, preset.Mods.Count, path);
        RefreshPresets();
        SelectedPreset = Presets.FirstOrDefault(p => p.Path == path);
        Message = string.Format(CultureInfo.CurrentCulture, Strings.PresetSavedStatus, preset.Name);
    }

    /// <summary>
    /// Puts the preset's mods into the editor as unsaved changes. Nothing is written until you press Save,
    /// which goes through the normal safe save (backup first, game closed).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyPreset))]
    private void ApplyPreset()
    {
        var preset = SelectedPreset!.Preset;
        var resolved = PresetResolver.Resolve(preset, _mods.Values.Select(m => m.Mod));
        _suppressDirty = true;
        LoadOrder.Clear();
        foreach (var r in resolved.Where(r => r.Mod is not null))
        {
            _mods.TryGetValue(r.Mod!.PackageId, out var mod);
            LoadOrder.Add(new LoadOrderItemViewModel(r.ToActiveMod()!, mod));
        }

        _suppressDirty = false;
        Renumber();
        IsDirty = true;
        RefreshAvailable();
        NotifyMoveCommands();

        var missing = resolved.Where(r => r.Match == PresetMatch.Missing).ToList();
        int renamed = resolved.Count(r => r.Match == PresetMatch.ByFileName);
        Message = string.Format(CultureInfo.CurrentCulture, Strings.PresetAppliedStatus, preset.Name, LoadOrder.Count)
                  + (renamed > 0 ? " " + string.Format(CultureInfo.CurrentCulture, Strings.PresetRenamedNote, renamed) : "");
        if (missing.Count > 0)
        {
            string list = string.Join(Environment.NewLine,
                missing.Select(m => "• " + m.Entry.DisplayName + (m.WorkshopUrl is { } url ? $"  ({url})" : "")));
            _dialogs.ShowInfo(string.Format(CultureInfo.CurrentCulture, Strings.PresetMissing, missing.Count, list));
        }
    }

    private bool CanApplyPreset() => CanEdit && SelectedPreset is not null;

    [RelayCommand]
    private void ImportPreset()
    {
        if (_dialogs.PickOpenFile(Strings.PresetFileFilter) is not { } file)
        {
            return;
        }

        try
        {
            var preset = Preset.Load(file, _presetGame); // validates before anything is copied
            string path = UniquePresetPath(preset.Name);
            preset.Save(path);
            RefreshPresets();
            SelectedPreset = Presets.FirstOrDefault(p => p.Path == path);
            Message = string.Format(CultureInfo.CurrentCulture, Strings.PresetImportedStatus, preset.Name);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasPreset))]
    private void ExportPreset()
    {
        var item = SelectedPreset!;
        if (_dialogs.PickSaveFile(Strings.PresetFileFilter, SafeFileName(item.Name) + Preset.FileExtension) is not { } file)
        {
            return;
        }

        try
        {
            item.Preset.Save(file);
            Message = string.Format(CultureInfo.CurrentCulture, Strings.PresetExportedStatus, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasPreset))]
    private void DeletePreset()
    {
        var item = SelectedPreset!;
        if (!_dialogs.Confirm(Strings.DeletePreset, string.Format(CultureInfo.CurrentCulture, Strings.ConfirmDeletePreset, item.Name)))
        {
            return;
        }

        File.Delete(item.Path);
        RefreshPresets();
    }

    private bool HasPreset() => SelectedPreset is not null;

    private string UniquePresetPath(string name)
    {
        Directory.CreateDirectory(_presetsDir);
        string baseName = SafeFileName(name);
        string path = Path.Combine(_presetsDir, baseName + Preset.FileExtension);
        for (int i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(_presetsDir, $"{baseName} ({i}){Preset.FileExtension}");
        }

        return path;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string s = new(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return s.Length == 0 ? "preset" : s[..Math.Min(s.Length, 80)];
    }
}

public sealed class PresetItemViewModel(Preset preset, string path)
{
    public Preset Preset { get; } = preset;

    public string Path { get; } = path;

    public string Name => Preset.Name;

    public string Summary =>
        string.Format(CultureInfo.CurrentCulture, Strings.PresetSummaryLine, Preset.Mods.Count,
                      Preset.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)) +
        (Preset.GameVersion is { } v ? " · " + string.Format(CultureInfo.CurrentCulture, Strings.PresetMadeWith, v) : "");
}
