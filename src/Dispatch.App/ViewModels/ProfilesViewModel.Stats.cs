using CommunityToolkit.Mvvm.Input;

namespace Dispatch.App.ViewModels;

/// <summary>"Stats…": the selected profile's figures from its newest save (see <see cref="StatsViewModel"/>). Read-only.</summary>
public sealed partial class ProfilesViewModel
{
    /// <summary>Set by the main window: opens the Stats window for a profile.</summary>
    public Action<ProfileItemViewModel>? StatsRequested { get; set; }

    private bool CanShowStats() => Selected is { Profile.SaveCount: > 0 } && StatsRequested is not null;

    [RelayCommand(CanExecute = nameof(CanShowStats))]
    private void ShowStats()
    {
        if (Selected is { } profile)
        {
            StatsRequested?.Invoke(profile);
        }
    }
}
