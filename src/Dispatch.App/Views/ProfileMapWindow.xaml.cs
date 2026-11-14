using System.Windows;
using Dispatch.App.ViewModels;

namespace Dispatch.App.Views;

/// <summary>A profile's whole road network, coloured by mod, with a legend (DataContext: ProfileMapViewModel). Not modal.</summary>
public partial class ProfileMapWindow : Window
{
    private readonly CancellationTokenSource _cancel = new();

    public ProfileMapWindow() => InitializeComponent();

    /// <summary>The map control (screenshots wait for its first render).</summary>
    public MapCanvas MapControl => Map;

    /// <summary>Starts loading the view model's map; closing the window stops it.</summary>
    public Task StartLoading() => ((ProfileMapViewModel)DataContext).LoadAsync(_cancel.Token);

    protected override void OnClosed(EventArgs e)
    {
        _cancel.Cancel();
        _cancel.Dispose();
        base.OnClosed(e);
    }

    private void OnZoomToItem(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProfileMapLegendItem item })
        {
            Map.FitTo(item.Bounds);
        }
    }

    private void OnZoomAll(object sender, RoutedEventArgs e) => Map.ZoomToAll();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
