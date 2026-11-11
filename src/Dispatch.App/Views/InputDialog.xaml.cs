using System.Windows;

namespace Dispatch.App.Views;

/// <summary>A minimal "enter a name" dialog.</summary>
public partial class InputDialog : Window
{
    public InputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        Prompt.Text = prompt;
        Input.Text = initial;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public string Value => Input.Text;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
