using System.Windows;

namespace Dispatch.App.Views;

/// <summary>Asks before launching with mods that need DLC the player doesn't own (DataContext: MissingDlcViewModel).</summary>
public partial class MissingDlcWindow : Window
{
    public MissingDlcWindow() => InitializeComponent();

    private void OnLaunch(object sender, RoutedEventArgs e) => DialogResult = true;
}
