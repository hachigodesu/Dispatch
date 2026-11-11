using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Dispatch.App.Resources;
using Dispatch.Core.Profiles;

namespace Dispatch.App.ViewModels;

public enum ProfileDialogMode
{
    Duplicate,
    Rename,
}

/// <summary>The Duplicate / Rename dialog. Validates the name as you type (rules from spike S7).</summary>
public sealed partial class ProfileDialogViewModel : ObservableObject
{
    private readonly IReadOnlyList<string> _names;
    private readonly IReadOnlyList<string> _folders;
    private readonly string _profilesRoot;
    private readonly string _longestPath;

    public ProfileDialogViewModel(ProfileDialogMode mode, ProfileInfo source, IReadOnlyList<string> names,
                                  IReadOnlyList<string> folders, string profilesRoot, bool cloudAvailable)
    {
        Mode = mode;
        Source = source;
        // When renaming, the profile's own name doesn't count as a clash (its folder still does: same name = no-op).
        _names = mode == ProfileDialogMode.Rename ? names.Where(n => n != source.Name).ToList() : names;
        _folders = folders;
        _profilesRoot = profilesRoot;
        _longestPath = Directory.Exists(source.MainDir)
            ? Directory.EnumerateFiles(source.MainDir, "*", SearchOption.AllDirectories)
                       .Select(f => Path.GetRelativePath(source.MainDir, f)).DefaultIfEmpty("profile.sii").MaxBy(r => r.Length)!
            : "profile.sii";
        CloudAvailable = cloudAvailable;
        _newName = mode == ProfileDialogMode.Duplicate ? SuggestName(source.Name) : source.Name;
        Validate();
    }

    public ProfileDialogMode Mode { get; }

    public ProfileInfo Source { get; }

    public bool IsDuplicate => Mode == ProfileDialogMode.Duplicate;

    public bool CloudAvailable { get; }

    public string Title => IsDuplicate ? Strings.DuplicateTitle : Strings.RenameTitle;

    public string Intro => string.Format(CultureInfo.CurrentCulture, IsDuplicate ? Strings.DuplicateIntro : Strings.RenameIntro, Source.Name);

    public ObservableCollection<string> Errors { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    private string _newName;

    [ObservableProperty]
    private bool _includeSaves = true;

    [ObservableProperty]
    private bool _keepWorldOfTrucksLink = true;

    [ObservableProperty]
    private bool _destinationCloud;

    public bool CanAccept => Errors.Count == 0;

    partial void OnNewNameChanged(string value) => Validate();

    private void Validate()
    {
        var result = ProfileNameValidator.Validate(NewName, _names, _folders, _profilesRoot,
                                                   IncludeSaves || !IsDuplicate ? _longestPath : "profile.bak.sii");
        Errors.Clear();
        foreach (var e in result.Errors)
        {
            Errors.Add(e);
        }

        Warnings.Clear();
        foreach (var w in result.Warnings)
        {
            Warnings.Add(w);
        }

        OnPropertyChanged(nameof(CanAccept));
    }

    /// <summary>"Name 2", "Name 3"… trimmed to the 20-character limit.</summary>
    private string SuggestName(string name)
    {
        for (int i = 2; i < 100; i++)
        {
            string suffix = " " + i.ToString(CultureInfo.InvariantCulture);
            string baseName = name.Length + suffix.Length > ProfileNameValidator.MaxLength
                ? name[..Math.Max(1, ProfileNameValidator.MaxLength - suffix.Length)].TrimEnd()
                : name;
            string candidate = baseName + suffix;
            if (ProfileNameValidator.Validate(candidate, _names, _folders).IsValid)
            {
                return candidate;
            }
        }

        return name;
    }
}
