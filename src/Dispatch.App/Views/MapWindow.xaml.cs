using System.Windows;

namespace Dispatch.App.Views;

/// <summary>Shows the roads a map mod adds and removes (DataContext: MapViewModel). Not modal: several can be open.</summary>
public partial class MapWindow : Window
{
    private readonly CancellationTokenSource _cancel = new();

    public MapWindow() => InitializeComponent();

    /// <summary>The map control (screenshots wait for its first render).</summary>
    public MapCanvas MapControl => Map;

    /// <summary>Starts loading the view model's map; closing the window stops it.</summary>
    public Task StartLoading() => ((ViewModels.MapViewModel)DataContext).LoadAsync(_cancel.Token);

    protected override void OnClosed(EventArgs e)
    {
        _cancel.Cancel();
        _cancel.Dispose();
        base.OnClosed(e);
    }

    private void OnZoomChanges(object sender, RoutedEventArgs e) => Map.ZoomToChanges();

    private void OnZoomAll(object sender, RoutedEventArgs e) => Map.ZoomToAll();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
