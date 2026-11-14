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

        bool selfTest = e.Args.Length == 3 && e.Args[0] == "--selftest";
        var autoDialogs = new AutoYesDialogs();
        var settings = AppSettings.Load(paths.SettingsFile);
        // "--screenshot out.png tab profile game lang" can preview a language without saving it.
        SettingsViewModel.ApplyLanguage(e.Args.Length >= 6 && e.Args[0] == "--screenshot" ? e.Args[5] : settings.Language);
        if (selfTest)
        {
            settings.ReadOnly = true; // nothing the self-test does may change the user's settings.json
            settings.AutoRepackCopies = false; // and it never packs the user's real copies (the packing test turns it on for its own)
            settings.Game = Dispatch.Core.Games.GameDefinition.Ets2.Key; // the self-test profile is an ETS2 one
        }
        else if (e.Args.Length >= 5 && e.Args[0] == "--screenshot")
        {
            settings.ReadOnly = true;
            settings.AutoRepackCopies = false;
            settings.Game = e.Args[4]; // "--screenshot out.png tab profile ats": show that game (not saved)
        }

        PaneLayout.Use(settings, paths.SettingsFile);
        var viewModel = new MainViewModel(platform, paths, settings, selfTest ? autoDialogs : new Dialogs());
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();

        // Development aid: "Dispatch.exe --screenshot out.png [tab] [profile]" renders the loaded window to a PNG
        // and exits. tab: 0 Profiles, 1 Mods, 2 Settings, 3 Duplicate dialog, 4 Profiles with the Conflicts panel, 5 end of Settings; an optional
        // 5th argument picks the game (ets2/ats) for this run only. Lets the UI be checked without clicking through it.
        string? screenshot = e.Args.Length >= 2 && e.Args[0] == "--screenshot" ? e.Args[1] : null;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        if (selfTest)
        {
            bool passed = await SelfTest.RunAsync(viewModel, autoDialogs, e.Args[1], e.Args[2]);
            Shutdown(passed ? 0 : 1);
            return;
        }

        if (screenshot is null)
        {
            _ = viewModel.Updates.CheckOnStartupAsync(); // background; never blocks or interrupts
        }

        if (screenshot is not null)
        {
            int tab = e.Args.Length >= 3 && int.TryParse(e.Args[2], out int t) ? t : 0;
            if (e.Args.Length >= 7 && tab is not (10 or 11 or 12 or 13)) // for these the 7th argument is a mod name or a window size
            {
                viewModel.Versions.AddCopyFrom(e.Args[6]); // a 7th argument lists a game copy for this run only (not saved)
            }

            if (e.Args.Length >= 4)
            {
                viewModel.Profiles.Selected = viewModel.Profiles.Items.FirstOrDefault(p => p.Name == e.Args[3]) ?? viewModel.Profiles.Selected;
                viewModel.Profiles.SelectedEntry = viewModel.Profiles.LoadOrder.FirstOrDefault(l => l.IconFile is not null);
            }

            viewModel.Mods.Selected = viewModel.Mods.Items.FirstOrDefault(m => m.HasGameMessages && m.HasIssues && m.IconFile is not null)
                                     ?? viewModel.Mods.Items.FirstOrDefault(m => m.HasIssues && m.IconFile is not null);
            foreach (var m in viewModel.Mods.Items.Where(m => m.HasType).Take(3))
            {
                viewModel.Mods.SelectedMods.Add(m); // shows the multi-selection in screenshots
            }
            await Task.Delay(1500); // let the selected mod's description load
            if (tab == 3 && viewModel.Profiles.Selected is { } sel)
            {
                // The Duplicate dialog, rendered without showing it modally.
                var dlgVm = new ProfileDialogViewModel(ProfileDialogMode.Duplicate, sel.Profile,
                                                       viewModel.Profiles.Items.Select(i => i.Name).ToList(),
                                                       viewModel.Profiles.Items.Select(i => i.Profile.FolderName).ToList(),
                                                       Path.GetDirectoryName(sel.Profile.MainDir)!, cloudAvailable: true)
                { DestinationCloud = true };
                var dlg = new ProfileDialog { DataContext = dlgVm, Owner = window, WindowStartupLocation = WindowStartupLocation.Manual };
                dlg.Show();
                await Task.Delay(300);
                Views.MainWindow.SaveScreenshot(dlg, screenshot);
                dlg.Close();
            }
            else if (tab == 8 && viewModel.Profiles.BuildSuggestion() is { } review)
            {
                // The "Suggest order" review window, rendered without showing it modally.
                var dlg = new SuggestOrderDialog { DataContext = review, Owner = window, WindowStartupLocation = WindowStartupLocation.Manual };
                dlg.Show();
                await Task.Delay(800); // icons
                Views.MainWindow.SaveScreenshot(dlg, screenshot);
                dlg.Close();
            }
            else if (tab == 10)
            {
                // The Map window for a mod in the profile's load order: the 7th argument picks it by name (part of
                // the display name or package ID), otherwise the first one that looks like a map.
                string? pick = e.Args.Length >= 7 ? e.Args[6] : null;
                var entry = viewModel.Profiles.LoadOrder.FirstOrDefault(l => l.Mod is not null && pick is not null &&
                                (l.DisplayName.Contains(pick, StringComparison.OrdinalIgnoreCase) || l.PackageId.Contains(pick, StringComparison.OrdinalIgnoreCase)))
                            ?? viewModel.Profiles.LoadOrder.FirstOrDefault(l => l.Mod?.LooksLikeMap == true);
                if (entry?.Mod is { } mod)
                {
                    // First the main window with that mod selected (its details show the "Show on map" button).
                    viewModel.SelectedTab = 0;
                    viewModel.Profiles.SelectedEntry = entry;
                    await Task.Delay(500);
                    window.SaveScreenshot(Path.ChangeExtension(screenshot, null) + "-details.png", 0);
                    var map = new MapWindow { DataContext = viewModel.CreateMap(mod), Owner = window, WindowStartupLocation = WindowStartupLocation.Manual };
                    map.Show();
                    await map.StartLoading();
                    for (int i = 0; i < 600 && !map.MapControl.HasRendered && ((MapViewModel)map.DataContext).HasMap; i++)
                    {
                        await Task.Delay(100);
                    }

                    Views.MainWindow.SaveScreenshot(map, screenshot);
                    map.Close();
                }
            }
            else if (tab is 11 or 12 && viewModel.Profiles.Selected is { } shown)
            {
                // 11: the profile's road map. 12: a mod from the Mods tab (7th argument: part of its name) shown in that profile.
                Window map;
                Func<MapCanvas> canvas;
                Func<bool> hasMap;
                Task loading;
                if (tab == 11)
                {
                    var w = new ProfileMapWindow { DataContext = viewModel.CreateProfileMap(shown), Owner = window, WindowStartupLocation = WindowStartupLocation.Manual };
                    w.Show();
                    loading = w.StartLoading();
                    (map, canvas, hasMap) = (w, () => w.MapControl, () => ((ProfileMapViewModel)w.DataContext).HasMap);
                }
                else
                {
                    string pick = e.Args.Length >= 7 ? e.Args[6] : "map";
                    var mod = viewModel.Mods.Items.FirstOrDefault(m => m.DisplayName.Contains(pick, StringComparison.OrdinalIgnoreCase) ||
                                                                       m.PackageId.Contains(pick, StringComparison.OrdinalIgnoreCase));
                    if (mod is null)
                    {
                        Log.Warning("Screenshot: no mod matches {Pick}", pick);
                        Shutdown(1);
                        return;
                    }

                    var w = new MapWindow { DataContext = viewModel.CreateMapInProfile(mod, shown), Owner = window, WindowStartupLocation = WindowStartupLocation.Manual };
                    w.Show();
                    loading = w.StartLoading();
                    (map, canvas, hasMap) = (w, () => w.MapControl, () => ((MapViewModel)w.DataContext).HasMap);
                }

                await loading;
                for (int i = 0; i < 600 && !canvas().HasRendered && hasMap(); i++)
                {
                    await Task.Delay(100);
                }

                // 11 with a 7th argument: click a road of that mod (as a user would), then zoom to it.
                if (tab == 11 && e.Args.Length >= 7 && map.DataContext is ProfileMapViewModel pm &&
                    pm.Legend.FirstOrDefault(l => l.HasRoads && l.Name.Contains(e.Args[6], StringComparison.OrdinalIgnoreCase)) is { } item &&
                    pm.Network?.RoadsByLayer[item.Layers[0]] is { Count: > 0 } roads)
                {
                    var road = roads.OrderByDescending(r => r.Length).First();
                    canvas().FitTo(road.Bounds.Inflate(6000));
                    await Task.Delay(300);
                    canvas().HighlightedRoad = road;
                    pm.OnRoadClicked(pm.Scene!.Layers.First(l => l.Tag == item));
                    await Task.Delay(1500);
                }

                Views.MainWindow.SaveScreenshot(map, screenshot);
                map.Close();
            }
            else if (tab == 13 && viewModel.Profiles.Selected is { } statsProfile)
            {
                // The Stats window for the profile's newest save.
                var w = new StatsWindow { DataContext = viewModel.CreateStats(statsProfile), Owner = window, WindowStartupLocation = WindowStartupLocation.Manual };
                if (e.Args.Length >= 7 && e.Args[6].Split('x') is [var sw, var sh] && double.TryParse(sw, out double ww) && double.TryParse(sh, out double wh))
                {
                    (w.Width, w.Height) = (ww, wh); // a 7th argument "1920x1040" checks wide windows
                }

                w.Show();
                await w.StartLoading();
                if (e.Args.Length >= 8 && int.TryParse(e.Args[7], out int tableTab))
                {
                    w.Tables.SelectedIndex = tableTab; // an 8th argument picks the table: 0 jobs, 1 trucks, 2 garages, 3 drivers
                }

                await Task.Delay(800);
                Views.MainWindow.SaveScreenshot(w, screenshot);
                w.Close();
            }
            else if (tab is 15 or 16)
            {
                // Narrow areas (nothing saved): 15 = profiles list at its minimum, a wide side panel squeezing the load
                // order to its minimum; 16 = a wide details panel squeezing the mod list.
                var profiles = window.ProfilesPage;
                profiles.ListColumn.Width = new GridLength(profiles.ListColumn.MinWidth);
                profiles.SideColumn.Width = new GridLength(620);
                window.ModsPage.DetailsColumn.Width = new GridLength(860);
                await Task.Delay(500);
                window.SaveScreenshot(screenshot, tab == 15 ? 0 : 1);
                var list = profiles.LoadOrderList;
                var item = list.ItemContainerGenerator.ContainerFromIndex(1) as FrameworkElement;
                Log.Information("Screenshot: load order list {List:F0} wide, column {Column:F0}, row {Row:F0}",
                                list.ActualWidth, profiles.ListColumn.ActualWidth, item?.ActualWidth ?? -1);
            }
            else if (tab == 14 && viewModel.Profiles.LoadOrder.Count >= 4)
            {
                // Cut and paste: rows 2 and 3 cut (faded), row 1 selected as the paste target. Nothing is saved.
                var p = viewModel.Profiles;
                p.SelectedEntries.Clear();
                p.SelectedEntries.Add(p.LoadOrder[1]);
                p.SelectedEntries.Add(p.LoadOrder[2]);
                p.CutCommand.Execute(null);
                p.SelectedEntries.Clear();
                p.SelectedEntry = p.LoadOrder[0];
                await Task.Delay(500);
                window.SaveScreenshot(screenshot, 0);
            }
            else if (tab == 9)
            {
                window.SaveScreenshot(screenshot, 0, sidePanel: 3); // the Multiplayer panel
            }
            else if (tab == 4)
            {
                await viewModel.Profiles.ConflictCheck;
                viewModel.Profiles.SelectedConflict = viewModel.Profiles.Conflicts.FirstOrDefault();
                await Task.Delay(1500); // example file names
                window.SaveScreenshot(screenshot, 0, sidePanel: 2);
            }
            else
            {
                window.SaveScreenshot(screenshot, tab);
            }

            // Long translations can make the side panel's tab headers wrap onto a second row; report it.
            var panel = window.ProfilesPage.SidePanel;
            int rows = panel.Items.OfType<System.Windows.Controls.TabItem>()
                            .Select(t => Math.Round(t.TranslatePoint(new Point(0, 0), panel).Y)).Distinct().Count();
            Log.Information("Screenshot: side panel tab header rows {Rows} ({Culture})", rows, Dispatch.App.Resources.Strings.Culture?.Name ?? "default");
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
        MessageBox.Show(string.Format(System.Globalization.CultureInfo.CurrentCulture, Dispatch.App.Resources.Strings.CrashMessage, e.Exception.Message),
                        "Dispatch", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
