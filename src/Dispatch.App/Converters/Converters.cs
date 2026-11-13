using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dispatch.Core.Mods;

namespace Dispatch.App.Converters;

/// <summary>Compatibility status → badge colour.</summary>
public sealed class CompatToBrushConverter : IValueConverter
{
    private static readonly Brush Ok = Freeze(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly Brush NoInfo = Freeze(Color.FromRgb(0x60, 0x6B, 0x78));
    private static readonly Brush Mismatch = Freeze(Color.FromRgb(0xB2, 0x5E, 0x00));
    private static readonly Brush MissingDlc = Freeze(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly Brush Unreadable = Freeze(Color.FromRgb(0x6A, 0x4C, 0x93));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        CompatStatus.Ok => Ok,
        CompatStatus.NoVersionInfo => NoInfo,
        CompatStatus.VersionMismatch => Mismatch,
        CompatStatus.MissingDlc => MissingDlc,
        CompatStatus.Unreadable => Unreadable,
        _ => MissingDlc, // null = not installed
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>Issue severity → small text colour.</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        IssueSeverity.Error => Brushes.IndianRed,
        IssueSeverity.Warning => Brushes.DarkOrange,
        _ => Brushes.Gray,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Icon file path → image. Loads fully into memory (OnLoad) so the cache file isn't kept locked, and decodes at
/// display size to save memory with hundreds of mods.
/// </summary>
public sealed class PathToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            if (parameter is string width && int.TryParse(width, out int w))
            {
                image.DecodePixelWidth = w;
            }

            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or FileFormatException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true ⇄ false, both ways (used to bind the second radio button of a pair to the same bool).</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>A brush for true or false: parameter "#RRGGBB|#RRGGBB" (true colour first).</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string[] colours = (parameter as string ?? "#2E7D32|#6B6B6B").Split('|');
        var brush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value is true ? colours[0] : colours[^1]));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the value is true / non-null / a non-empty string. Parameter "invert" flips it.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value switch
        {
            bool b => b,
            string s => s.Length > 0,
            int i => i > 0,
            null => false,
            _ => true,
        };
        if (parameter as string == "invert")
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
