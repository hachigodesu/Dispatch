using System.Windows;
using Dispatch.App.ViewModels;

namespace Dispatch.App.Views;

/// <summary>A profile's stats from its newest save (DataContext: StatsViewModel). Not modal.</summary>
public partial class StatsWindow : Window
{
    private readonly CancellationTokenSource _cancel = new();

    public StatsWindow()
    {
        InitializeComponent();
        Services.PaneLayout.Remember(SkillsSplitter, (SkillsColumn, "stats.skills"));
        Page.SizeChanged += (_, _) => FitTables();
        Summary.SizeChanged += (_, _) => FitTables();
    }

    /// <summary>The tables take the rest of a tall window; in a short one they keep 440 px and the page scrolls.</summary>
    private void FitTables() => Tables.Height = Math.Max(440, Page.ViewportHeight - Summary.ActualHeight - 12);

    /// <summary>Starts reading the save; closing the window stops it.</summary>
    public Task StartLoading() => ((StatsViewModel)DataContext).LoadAsync(_cancel.Token);

    protected override void OnClosed(EventArgs e)
    {
        _cancel.Cancel();
        _cancel.Dispose();
        base.OnClosed(e);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
