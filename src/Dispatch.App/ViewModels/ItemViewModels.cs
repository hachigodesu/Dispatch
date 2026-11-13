using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Dispatch.App.Resources;
using Dispatch.Core.Mods;
using Dispatch.Core.Mods.Packages;
using Dispatch.Core.Profiles;

namespace Dispatch.App.ViewModels;

/// <summary>One installed mod, shaped for display. Wraps the immutable Core <see cref="ModInfo"/>.</summary>
public sealed partial class ModItemViewModel : ObservableObject
{
    private readonly Action<ModItemViewModel>? _saveNote;
    private bool _loadingNote;

    public ModItemViewModel(ModInfo mod, Compatibility compatibility, string? nameFromProfiles, IReadOnlyList<string> usedIn,
                            ModNote? note = null, Action<ModItemViewModel>? saveNote = null, DuplicateInfo? duplicate = null,
                            IReadOnlyList<ModIssue>? gameMessages = null, DateTime? gameLogUtc = null)
    {
        GameMessages = gameMessages ?? [];
        GameMessagesHeader = gameLogUtc is { } when
            ? string.Format(CultureInfo.CurrentCulture, Strings.GameSaysHeader, when.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
            : Strings.GameSaysHeaderNoDate;
        Mod = mod;
        Compatibility = compatibility;
        UsedIn = usedIn;
        Duplicate = duplicate;
        _saveNote = saveNote;
        _loadingNote = true;
        TagsText = string.Join(", ", note?.Tags ?? []);
        NoteText = note?.Note ?? "";
        _loadingNote = false;

        // Prefer the manifest's name for local mods; for Workshop and unreadable mods the name the game stored in a
        // profile is better (the game uses the Workshop title, which isn't on disk).
        DisplayName = mod.Source == ModSource.Local && mod.Manifest?.DisplayName is { } manifestName
            ? manifestName
            : nameFromProfiles ?? mod.DisplayName;
    }

    public ModInfo Mod { get; }

    public Compatibility Compatibility { get; }

    public IReadOnlyList<string> UsedIn { get; }

    public string DisplayName { get; }

    public string PackageId => Mod.PackageId;

    public string? IconFile => Mod.IconFile;

    public string Version => Mod.Manifest?.PackageVersion ?? "";

    public string Author => Mod.Manifest?.Author ?? "";

    public string Categories => string.Join(", ", Mod.Manifest?.Categories ?? []);

    /// <summary>The official categories, named in the UI language (resource keys "Type_&lt;category&gt;").</summary>
    private static readonly Dictionary<string, string> TypeNames = new[]
        {
            "truck", "trailer", "interior", "tuning_parts", "ai_traffic", "sound", "paint_job", "cargo_pack", "map", "ui",
            "weather_setup", "physics", "graphics", "models", "movers", "walkers", "prefabs", "other",
        }
        .ToDictionary(k => k, k => Strings.ByName("Type_" + k), StringComparer.Ordinal);

    /// <summary>
    /// The mod's types (manifest categories) as display names, e.g. ["Map"] or ["Sound", "Truck"]. Common author
    /// mistakes are matched to the real category first ("maps" → Map), so the type filter doesn't split them.
    /// </summary>
    public IReadOnlyList<string> TypeLabels => _typeLabels ??= (Mod.Manifest?.Categories ?? [])
        .Select(c => ModCategories.Canonical(c) is { } key && TypeNames.TryGetValue(key, out var name) ? name
                     : c.Trim().Length == 0 ? "" : char.ToUpperInvariant(c.Trim()[0]) + c.Trim()[1..].Replace('_', ' '))
        .Where(c => c.Length > 0).Distinct().ToList();

    private IReadOnlyList<string>? _typeLabels;

    public string TypeLabel => TypeLabels.Count == 0 ? "" : string.Join(" · ", TypeLabels);

    public bool HasType => TypeLabels.Count > 0;

    public string CompatibleVersions => string.Join(", ", Mod.Manifest?.CompatibleVersions ?? []);

    public string DlcDependencies => string.Join(", ", Mod.Manifest?.DlcDependencies ?? []);

    public CompatStatus CompatStatus => Compatibility.Status;

    public string CompatLabel => CompatStatus switch
    {
        CompatStatus.Ok => Strings.CompatOk,
        CompatStatus.NoVersionInfo => Strings.CompatNoInfo,
        CompatStatus.VersionMismatch => Strings.CompatMismatch,
        CompatStatus.MissingDlc => Strings.CompatMissingDlc,
        _ => Strings.CompatUnreadable,
    };

    /// <summary>The compatibility explanation in the UI language.</summary>
    public string CompatDetail
    {
        get
        {
            var c = Compatibility;
            string text = c.Reason switch
            {
                CompatReason.GameSaysIncompatible => Strings.CompatDetailGameSaysIncompatible,
                CompatReason.NoManifest => Strings.CompatDetailNoManifest,
                CompatReason.Unreadable => Strings.CompatDetailUnreadable,
                CompatReason.MissingDlc => string.Format(CultureInfo.CurrentCulture, Strings.CompatDetailMissingDlc, string.Join(", ", c.MissingDlc)),
                CompatReason.UnknownGameVersion => Strings.CompatDetailUnknownGameVersion,
                CompatReason.Workshop => Strings.CompatDetailWorkshop,
                CompatReason.NoVersions => Strings.CompatDetailNoVersions,
                CompatReason.Matches => string.Format(CultureInfo.CurrentCulture, Strings.CompatDetailMatches, c.Declared),
                CompatReason.Mismatch => string.Format(CultureInfo.CurrentCulture, Strings.CompatDetailMismatch, c.Declared, c.YourVersion),
                _ => c.Detail,
            };
            return c.LogNote switch
            {
                LogNote.LogSaysIncompatible => text + " " + Strings.CompatNoteLogSaysIncompatible,
                LogNote.LogSaysCompatible => text + " " + Strings.CompatNoteLogSaysCompatible,
                _ => text,
            };
        }
    }

    public string SourceLabel => Mod.Source == ModSource.Workshop ? Strings.SourceWorkshop : Strings.SourceLocal;

    public string FormatLabel => Mod.Format switch
    {
        PackageFormat.Folder => Strings.FormatFolder,
        PackageFormat.Zip => "ZIP",
        PackageFormat.HashFsV1 => "HashFS v1",
        PackageFormat.HashFsV2 => "HashFS v2",
        _ => Strings.FormatUnknown,
    } + (Mod.SubPackage is { } sub
        ? " · " + string.Format(CultureInfo.CurrentCulture, Strings.FormatSubPackage, sub, Mod.SubPackageCount)
        : "");

    public string SizeLabel => Mod.SizeBytes switch
    {
        >= 1L << 30 => (Mod.SizeBytes / (double)(1L << 30)).ToString("F1", CultureInfo.CurrentCulture) + " GB",
        >= 1L << 20 => (Mod.SizeBytes / (double)(1L << 20)).ToString("F1", CultureInfo.CurrentCulture) + " MB",
        _ => (Mod.SizeBytes / 1024.0).ToString("F0", CultureInfo.CurrentCulture) + " KB",
    };

    /// <summary>"Local · 1.2 · 5.3 MB", skipping parts that are unknown.</summary>
    public string ListSubtitle => string.Join(" · ", new[] { SourceLabel, Version, SizeLabel }.Where(s => s.Length > 0));

    public string UsedInLabel => UsedIn.Count == 0 ? "—" : string.Join(", ", UsedIn);

    public IReadOnlyList<ModIssue> Issues => Mod.Issues;

    public bool HasIssues => Mod.Issues.Count > 0;

    /// <summary>What the game itself logged about this mod at its last start (game.log.txt).</summary>
    public IReadOnlyList<ModIssue> GameMessages { get; }

    public bool HasGameMessages => GameMessages.Count > 0;

    public string GameMessagesHeader { get; }

    /// <summary>Loaded the first time the mod is selected (reading the package takes a moment).</summary>
    [ObservableProperty]
    private string? _description;

    private bool _descriptionLoaded;

    public void EnsureDescriptionLoaded()
    {
        if (_descriptionLoaded)
        {
            return;
        }

        _descriptionLoaded = true;
        Task.Run(() => ModScanner.ReadDescription(Mod)).ContinueWith(
            t => Description = t.Result is { } text ? DescriptionText.StripColorTags(text).Trim() : null,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    public bool Matches(string filter) =>
        filter.Length == 0 ||
        DisplayName.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        PackageId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        Author.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        TagsText.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        NoteText.Contains(filter, StringComparison.CurrentCultureIgnoreCase);

    // ---- your tags and notes (saved as you type) -------------------------------------------------

    [ObservableProperty]
    private string _tagsText = "";

    [ObservableProperty]
    private string _noteText = "";

    public ModNote Note => new(ModNote.ParseTags(TagsText), NoteText.Trim());

    partial void OnTagsTextChanged(string value)
    {
        if (!_loadingNote)
        {
            _saveNote?.Invoke(this);
        }
    }

    partial void OnNoteTextChanged(string value)
    {
        if (!_loadingNote)
        {
            _saveNote?.Invoke(this);
        }
    }

    // ---- duplicates --------------------------------------------------------------------------------

    public DuplicateInfo? Duplicate { get; }

    public bool IsDuplicate => Duplicate is not null;

    /// <summary>Kept in Dispatch's mod library: the game doesn't see it until a profile uses it again.</summary>
    public bool InLibrary => Mod.InLibrary;

    public string DuplicateBadge => Duplicate is null ? "" : Duplicate.IsOlder ? Strings.OlderBadge : Strings.DuplicateBadge;

    public string DuplicateText
    {
        get
        {
            if (Duplicate is null)
            {
                return "";
            }

            string others = string.Join(", ", Duplicate.Others.Select(o =>
                $"{System.IO.Path.GetFileName(o.Path)}{(o.Manifest?.PackageVersion is { } v ? " (" + v + ")" : "")}"));
            string text = Duplicate.Kind == DuplicateKind.SameId
                ? Strings.DuplicateSameId + " " + string.Format(CultureInfo.CurrentCulture, Strings.DuplicateSameName, others)
                : string.Format(CultureInfo.CurrentCulture, Strings.DuplicateSameName, others);
            return Duplicate.IsOlder
                ? text + " " + string.Format(CultureInfo.CurrentCulture, Strings.DuplicateOlder, System.IO.Path.GetFileName(Duplicate.Newest.Path))
                : text;
        }
    }
}

public sealed class ProfileItemViewModel(ProfileInfo profile, LoadOrderRead? loadOrder, string? error)
{
    public ProfileInfo Profile { get; } = profile;

    public LoadOrderRead? LoadOrder { get; } = loadOrder;

    public string? Error { get; } = error;

    public string Name => Profile.Name;

    public bool IsCloud => Profile.Kind == ProfileKind.SteamCloud;

    public string KindLabel => IsCloud
        ? $"{Strings.KindCloud} · {Profile.Owner?.PersonaName}"
        : Strings.KindLocal;

    public string Summary => string.Format(CultureInfo.CurrentCulture, Strings.ProfileSummaryLine, LoadOrder?.TopFirst.Count ?? 0,
                                           Profile.SaveCount, Profile.LastWriteUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
}

/// <summary>One row of a profile's load order. Position 1 = top of the in-game list = highest priority.</summary>
public sealed partial class LoadOrderItemViewModel(ActiveModEntry entry, ModItemViewModel? mod) : ObservableObject
{
    /// <summary>Kept as read from the profile, so saving writes back the exact same "id|Name" entry.</summary>
    public ActiveModEntry Entry { get; } = entry;

    [ObservableProperty]
    private int _position;

    public string DisplayName { get; } = entry.DisplayName;

    public string PackageId { get; } = entry.PackageId;

    public ModItemViewModel? Mod { get; } = mod;

    public bool IsInstalled => Mod is not null;

    public string? IconFile => Mod?.IconFile;

    public string StatusLabel => Mod?.CompatLabel ?? Strings.NotInstalled;

    public CompatStatus? CompatStatus => Mod?.CompatStatus;

    // ---- conflicts (v2) ---------------------------------------------------------------------------

    /// <summary>"3 conflicts" when this mod shares files with other active mods; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConflicts))]
    private string _conflictBadge = "";

    [ObservableProperty]
    private string _conflictTooltip = "";

    public bool HasConflicts => ConflictBadge.Length > 0;

    /// <param name="wins">Mods lower in the list whose files this one replaces.</param>
    /// <param name="losses">Mods higher in the list that replace some of this one's files.</param>
    public void SetConflicts(int wins, int losses)
    {
        int total = wins + losses;
        ConflictBadge = total == 0 ? "" : string.Format(CultureInfo.CurrentCulture, total == 1 ? Strings.ConflictBadgeOne : Strings.ConflictBadge, total);
        var parts = new List<string>();
        if (wins > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.ConflictWins, wins));
        }

        if (losses > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.ConflictLosses, losses));
        }

        ConflictTooltip = string.Join("\n", parts);
    }
}
