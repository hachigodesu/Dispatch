using System.Windows;

namespace Dispatch.App.Views;

public partial class ProfileDialog : Window
{
    public ProfileDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
