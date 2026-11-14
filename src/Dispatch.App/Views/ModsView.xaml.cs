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
        Fill(MapInProfileMenu, vm.Selected is null ? [] : vm.MapTargets, vm.ShowOnMapInProfileCommand, Strings.NoReadableProfiles);
    }

    /// <summary>Shows a "copy" cursor only when files are dragged in.</summary>
    private void OnFileDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Dropping .scs/.zip files onto the list installs them (each is checked first).</summary>
    private async void OnFileDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (DataContext is ModsViewModel vm && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
        {
            e.Handled = true;
            await vm.InstallFilesAsync(files);
        }
    }

    private static void Fill(MenuItem menu, IEnumerable<ProfileItemViewModel> targets, System.Windows.Input.ICommand command, string? empty = null)
    {
        menu.Items.Clear();
        foreach (var profile in targets)
        {
            menu.Items.Add(new MenuItem { Header = profile.Name, Command = command, CommandParameter = profile });
        }

        menu.IsEnabled = menu.Items.Count > 0;
        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = empty ?? Strings.NoEditableProfiles, IsEnabled = false });
        }
    }
}
