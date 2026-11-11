using System.Windows.Controls;
using Dispatch.App.Resources;
using Dispatch.App.ViewModels;

namespace Dispatch.App.Views;

public partial class ModsView : UserControl
{
    public ModsView() => InitializeComponent();

    /// <summary>Fills "Add to profile ▸" and "Remove from profile ▸" with the current profiles each time the menu opens.</summary>
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (DataContext is not ModsViewModel vm)
        {
            return;
        }

        Fill(AddMenu, vm.AddTargets, vm.AddToProfileCommand);
        Fill(RemoveMenu, vm.RemoveTargets, vm.RemoveFromProfileCommand);
    }

    private static void Fill(MenuItem menu, IEnumerable<ProfileItemViewModel> targets, System.Windows.Input.ICommand command)
    {
        menu.Items.Clear();
        foreach (var profile in targets)
        {
            menu.Items.Add(new MenuItem { Header = profile.Name, Command = command, CommandParameter = profile });
        }

        menu.IsEnabled = menu.Items.Count > 0;
        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = Strings.NoEditableProfiles, IsEnabled = false });
        }
    }
}
