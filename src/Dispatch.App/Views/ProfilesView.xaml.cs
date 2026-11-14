using System.Windows.Controls;

namespace Dispatch.App.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView()
    {
        InitializeComponent();
        Services.PaneLayout.Remember(ListSplitter, (ListColumn, "profiles.list"));
        Services.PaneLayout.Remember(SideSplitter, (SideColumn, "profiles.side"));

        // A mod selected from elsewhere (a map's "Show in load order", a paste) is brought into view.
        LoadOrderList.SelectionChanged += (_, _) =>
        {
            if (LoadOrderList.SelectedItem is { } item)
            {
                LoadOrderList.ScrollIntoView(item);
            }
        };
    }
}
