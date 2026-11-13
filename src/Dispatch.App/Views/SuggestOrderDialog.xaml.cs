using System.Windows;

namespace Dispatch.App.Views;

/// <summary>Reviews a suggested load order before it goes into the editor (DataContext: SuggestionViewModel).</summary>
public partial class SuggestOrderDialog : Window
{
    public SuggestOrderDialog() => InitializeComponent();

    private void OnApply(object sender, RoutedEventArgs e) => DialogResult = true;
}
