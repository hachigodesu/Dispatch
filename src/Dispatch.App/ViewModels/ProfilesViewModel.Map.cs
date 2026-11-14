using CommunityToolkit.Mvvm.Input;

namespace Dispatch.App.ViewModels;

/// <summary>"Road map…": the selected profile's whole road network (see <see cref="ProfileMapViewModel"/>). Read-only.</summary>
public sealed partial class ProfilesViewModel
{
    /// <summary>Set by the main window: opens the Profile map window for a profile.</summary>
    public Action<ProfileItemViewModel>? ProfileMapRequested { get; set; }

    private bool CanShowProfileMap() => Selected?.LoadOrder is not null && ProfileMapRequested is not null;

    [RelayCommand(CanExecute = nameof(CanShowProfileMap))]
    private void ShowProfileMap()
    {
        if (Selected is { } profile)
        {
            ProfileMapRequested?.Invoke(profile);
        }
    }
}
