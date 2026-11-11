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
    public ModItemViewModel(ModInfo mod, Compatibility compatibility, string? nameFromProfiles, IReadOnlyList<string> usedIn)
    {
        Mod = mod;
        Compatibility = compatibility;
        UsedIn = usedIn;

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

    public string SourceLabel => Mod.Source == ModSource.Workshop ? Strings.SourceWorkshop : Strings.SourceLocal;

    public string FormatLabel => Mod.Format switch
    {
        PackageFormat.Folder => "Folder",
        PackageFormat.Zip => "ZIP",
        PackageFormat.HashFsV1 => "HashFS v1",
        PackageFormat.HashFsV2 => "HashFS v2",
        _ => "Unknown / protected",
    } + (Mod.SubPackage is { } sub ? $" · {sub} ({Mod.SubPackageCount} in versions.sii)" : "");

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
        Author.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
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

    public string Summary =>
        $"{LoadOrder?.TopFirst.Count ?? 0} mods · {Profile.SaveCount} {Strings.Saves} · {Profile.LastWriteUtc.ToLocalTime():g}";
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
}
