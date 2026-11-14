using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Resources;

namespace Dispatch.App.ViewModels;

/// <summary>
/// Cut and paste in the load order editor: Ctrl+X marks the selected mods (faded), Ctrl+V moves them, in their order,
/// above the selected mod (Ctrl+Shift+V: below it), Esc cancels. Like any other move, the result is an unsaved edit.
/// </summary>
public sealed partial class ProfilesViewModel
{
    private readonly List<LoadOrderItemViewModel> _cut = [];

    /// <summary>Mods are cut and waiting to be pasted.</summary>
    public bool HasCut => _cut.Count > 0;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Cut()
    {
        var sel = Selection();
        ClearCut();
        foreach (var item in LoadOrder.Where(sel.Contains))
        {
            item.IsCut = true;
            _cut.Add(item);
        }

        Message = string.Format(CultureInfo.CurrentCulture, Strings.CutDone, _cut.Count);
        NotifyCutCommands();
    }

    /// <summary>Pasting needs one row to paste next to that isn't itself cut.</summary>
    private bool CanPaste() => CanEdit && _cut.Count > 0 && SelectedEntry is { IsCut: false } target && LoadOrder.Contains(target);

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void PasteAbove() => PasteAt(LoadOrder.IndexOf(SelectedEntry!));

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void PasteBelow() => PasteAt(LoadOrder.IndexOf(SelectedEntry!) + 1);

    [RelayCommand(CanExecute = nameof(HasCut))]
    private void CancelCut()
    {
        ClearCut();
        Message = "";
    }

    private void PasteAt(int index)
    {
        var items = LoadOrder.Where(_cut.Contains).ToList(); // in their current order
        ClearCut();
        if (items.Count == 0)
        {
            return;
        }

        MoveBlock(items, index);
        SelectedEntry = items[0];
        Message = string.Format(CultureInfo.CurrentCulture, Strings.PasteDone, items.Count);
    }

    /// <summary>Forgets the cut mods (also when the editor is reloaded: those rows no longer exist).</summary>
    private void ClearCut()
    {
        foreach (var item in _cut)
        {
            item.IsCut = false;
        }

        _cut.Clear();
        NotifyCutCommands();
    }

    private void NotifyCutCommands()
    {
        OnPropertyChanged(nameof(HasCut));
        CutCommand.NotifyCanExecuteChanged();
        PasteAboveCommand.NotifyCanExecuteChanged();
        PasteBelowCommand.NotifyCanExecuteChanged();
        CancelCutCommand.NotifyCanExecuteChanged();
    }
}
