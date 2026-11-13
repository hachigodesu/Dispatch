using System.Windows;
using Dispatch.App.Resources;

namespace Dispatch.App.Services;

/// <summary>
/// Message boxes behind an interface, so view models don't talk to WPF windows directly (and tests could fake it).
/// </summary>
public interface IDialogs
{
    bool Confirm(string title, string message);

    /// <summary>Yes = save, No = discard, null = cancel.</summary>
    bool? AskSaveChanges(string profileName);

    void ShowError(string message);

    void ShowInfo(string message);

    /// <summary>Shows the Duplicate / Rename dialog; true when the user pressed OK.</summary>
    bool ShowProfileDialog(ViewModels.ProfileDialogViewModel dialog);

    /// <summary>Asks for a line of text; null when cancelled.</summary>
    string? AskText(string title, string prompt, string initial);

    string? PickOpenFile(string filter);

    string? PickSaveFile(string filter, string defaultName);

    IReadOnlyList<string> PickOpenFiles(string filter);

    /// <summary>Yes = true, No = false, Cancel = null.</summary>
    bool? AskYesNoCancel(string title, string message);

    IReadOnlyList<ViewModels.ProfileItemViewModel> PickProfiles(string title, string prompt,
                                                                 IReadOnlyList<ViewModels.ProfileItemViewModel> profiles);

    /// <summary>Shows a suggested load order for review; true = put it in the editor.</summary>
    bool ReviewSuggestion(ViewModels.SuggestionViewModel suggestion);
}

public sealed class Dialogs : IDialogs
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
        == MessageBoxResult.OK;

    public bool? AskSaveChanges(string profileName) =>
        MessageBox.Show(Application.Current.MainWindow,
                        string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.AskSave, profileName),
                        Strings.AskSaveTitle, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

    public void ShowError(string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, Strings.ErrorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowInfo(string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, Strings.AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);

    public bool ShowProfileDialog(ViewModels.ProfileDialogViewModel dialog) =>
        new Views.ProfileDialog { Owner = Application.Current.MainWindow, DataContext = dialog }.ShowDialog() == true;

    public string? AskText(string title, string prompt, string initial)
    {
        var d = new Views.InputDialog(title, prompt, initial) { Owner = Application.Current.MainWindow };
        return d.ShowDialog() == true ? d.Value : null;
    }

    public string? PickOpenFile(string filter)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = filter, CheckFileExists = true };
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FileName : null;
    }

    public string? PickSaveFile(string filter, string defaultName)
    {
        var d = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = defaultName, OverwritePrompt = true };
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FileName : null;
    }

    public IReadOnlyList<string> PickOpenFiles(string filter)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = filter, CheckFileExists = true, Multiselect = true };
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FileNames : [];
    }

    public bool? AskYesNoCancel(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

    public IReadOnlyList<ViewModels.ProfileItemViewModel> PickProfiles(string title, string prompt,
                                                                        IReadOnlyList<ViewModels.ProfileItemViewModel> profiles)
    {
        var d = new Views.PickProfilesDialog(title, prompt, profiles) { Owner = Application.Current.MainWindow };
        return d.ShowDialog() == true ? d.Picked : [];
    }

    public bool ReviewSuggestion(ViewModels.SuggestionViewModel suggestion) =>
        new Views.SuggestOrderDialog { Owner = Application.Current.MainWindow, DataContext = suggestion }.ShowDialog() == true;
}
