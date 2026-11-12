using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dispatch.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>Asks about unsaved load-order changes before the window closes.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm && !vm.Profiles.ConfirmLeave())
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    /// <summary>Renders the window to a PNG (used by the --screenshot development option).</summary>
    public void SaveScreenshot(string path, int tab, int sidePanel = 0)
    {
        bool settingsEnd = tab == 5; // 5 = Settings, scrolled to the bottom (language, updates, about)
        bool settingsVersions = tab == 6; // 6 = Settings, scrolled to Game versions
        Tabs.SelectedIndex = settingsEnd || settingsVersions ? 2 : tab;
        ProfilesPage.SidePanel.SelectedIndex = sidePanel;
        if (settingsEnd || settingsVersions)
        {
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (SettingsPage.Content is System.Windows.Controls.ScrollViewer scroll)
            {
                if (settingsEnd)
                {
                    scroll.ScrollToEnd();
                }
                else
                {
                    var top = SettingsPage.VersionsSection.TransformToAncestor(scroll).Transform(new Point(0, 0));
                    scroll.ScrollToVerticalOffset(scroll.VerticalOffset + top.Y - 8);
                }
            }
        }

        SaveScreenshot(this, path);
    }

    /// <summary>Renders any window's content to a PNG.</summary>
    public static void SaveScreenshot(Window window, string path)
    {
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var content = (FrameworkElement)window.Content;
        var background = window.Background;
        content.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
                                            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }

        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
