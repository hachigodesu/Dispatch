using System.Windows;
using System.Windows.Controls;

namespace Dispatch.App.Services;

/// <summary>
/// Remembers the widths of areas the user resizes with a splitter (profiles list, side panel, map legend…) in
/// settings.json. Widths are restored when a view is built and saved when a drag ends. The self-test and screenshot
/// modes set <see cref="AppSettings.ReadOnly"/>, so they never write.
/// </summary>
public static class PaneLayout
{
    private static AppSettings? _settings;
    private static string? _file;

    public static void Use(AppSettings settings, string file)
    {
        _settings = settings;
        _file = file;
    }

    /// <summary>
    /// Restores each column's saved width (when it still fits its minimum) and saves the widths after every drag of
    /// the splitter. Only fixed-width columns are remembered; the star column takes the rest.
    /// </summary>
    public static void Remember(GridSplitter splitter, params (ColumnDefinition Column, string Key)[] columns)
    {
        foreach (var (column, key) in columns)
        {
            if (_settings?.PaneWidths.TryGetValue(key, out double width) == true && width >= column.MinWidth && width <= column.MaxWidth)
            {
                column.Width = new GridLength(width);
            }
        }

        splitter.DragCompleted += (_, _) =>
        {
            if (_settings is null || _file is null)
            {
                return;
            }

            foreach (var (column, key) in columns.Where(c => c.Column.Width.IsAbsolute))
            {
                _settings.PaneWidths[key] = Math.Round(column.Width.Value);
            }

            try
            {
                _settings.Save(_file);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                Serilog.Log.Warning(ex, "Pane widths couldn't be saved");
            }
        };
    }
}
