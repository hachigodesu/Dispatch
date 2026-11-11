using System.IO;
using System.Windows;
using System.Windows.Threading;
using Dispatch.App.Services;
using Dispatch.App.ViewModels;
using Dispatch.App.Views;
using Dispatch.Core.Platform;
using Serilog;

namespace Dispatch.App;

/// <summary>
/// Entry point and "composition root": the one place where services are created and handed to view models.
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var platform = new WindowsPlatform();
        var paths = new AppPaths(platform.LocalAppDataFolder);
        Directory.CreateDirectory(paths.Logs);
        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(Path.Combine(paths.Logs, "dispatch-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                          formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();
        Log.Information("Dispatch {Version} starting", typeof(App).Assembly.GetName().Version);

        DispatcherUnhandledException += OnUnhandledException;

        var viewModel = new MainViewModel(platform, paths, AppSettings.Load(paths.SettingsFile));
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();

        // Development aid: "Dispatch.exe --screenshot out.png [tab] [profile]" renders the loaded window to a PNG
        // and exits. tab: 0 Profiles, 1 Mods, 2 Settings. Lets the UI be checked without clicking through it.
        string? screenshot = e.Args.Length >= 2 && e.Args[0] == "--screenshot" ? e.Args[1] : null;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        if (screenshot is not null)
        {
            int tab = e.Args.Length >= 3 && int.TryParse(e.Args[2], out int t) ? t : 0;
            if (e.Args.Length >= 4)
            {
                viewModel.Profiles.Selected = viewModel.Profiles.Items.FirstOrDefault(p => p.Name == e.Args[3]) ?? viewModel.Profiles.Selected;
                viewModel.Profiles.SelectedEntry = viewModel.Profiles.LoadOrder.FirstOrDefault(l => l.IconFile is not null);
            }

            viewModel.Mods.Selected = viewModel.Mods.Items.FirstOrDefault(m => m.HasIssues && m.IconFile is not null);
            await Task.Delay(1500); // let the selected mod's description load
            window.SaveScreenshot(screenshot, tab);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Dispatch exiting");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled exception");
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails are in the log folder.",
                        "Dispatch", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
