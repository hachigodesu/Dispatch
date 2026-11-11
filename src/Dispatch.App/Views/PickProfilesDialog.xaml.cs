using System.Windows;
using Dispatch.App.ViewModels;

namespace Dispatch.App.Views;

/// <summary>Pick one or more profiles (click to select several).</summary>
public partial class PickProfilesDialog : Window
{
    public PickProfilesDialog(string title, string prompt, IEnumerable<ProfileItemViewModel> profiles)
    {
        InitializeComponent();
        Title = title;
        Prompt.Text = prompt;
        List.ItemsSource = profiles.ToList();
        List.SelectionChanged += (_, _) => OkButton.IsEnabled = List.SelectedItems.Count > 0;
    }

    public IReadOnlyList<ProfileItemViewModel> Picked => List.SelectedItems.Cast<ProfileItemViewModel>().ToList();

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
