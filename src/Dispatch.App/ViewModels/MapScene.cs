using System.Windows.Media;
using Dispatch.Core.Maps;

namespace Dispatch.App.ViewModels;

/// <summary>Roads drawn in one style.</summary>
/// <param name="Width">Line width zoomed out; <paramref name="ZoomWidth"/> is added when zoomed in to single roads.</param>
/// <param name="AlwaysShow">Draw even roads shorter than a pixel (changes must never disappear when zoomed out).</param>
public sealed record MapSceneLayer(IReadOnlyList<MapRoad> Roads, IReadOnlyList<MapPrefab> Prefabs, Color Color, double Width,
                                   double ZoomWidth, bool Dashed = false, bool AlwaysShow = false);

/// <summary>A city label. Strong labels are bold, placed first and drawn in their colour.</summary>
public readonly record struct MapLabel(string Name, MapPoint Position, bool Strong, Color Color);

/// <summary>
/// What the map control draws: layers bottom first, outlined cells, labels, and where to look first. A new scene with
/// the same <paramref name="Source"/> (e.g. a layer hidden) keeps the current view.
/// </summary>
public sealed record MapScene(object Source, IReadOnlyList<MapSceneLayer> Layers, IReadOnlyList<MapRect> HiddenCells,
                              IReadOnlyList<MapLabel> Labels, MapRect? Focus, MapRect? Extent)
{
    public static readonly Color Paper = Color.FromRgb(0xF4, 0xF2, 0xEE);
    public static readonly Color OtherRoad = Color.FromRgb(0x9C, 0x9C, 0x9C);
    public static readonly Color AddedRoad = Color.FromRgb(0xD9, 0x48, 0x1E);
    public static readonly Color RemovedRoad = Color.FromRgb(0x2A, 0x6F, 0xDB);
    public static readonly Color Hidden = Color.FromRgb(0x8E, 0x44, 0xAD);
    public static readonly Color Label = Color.FromRgb(0x11, 0x11, 0x11);
    public static readonly Color AddedLabel = Color.FromRgb(0xA3, 0x2E, 0x0C);

    /// <summary>
    /// The i-th mod colour: the hand-picked ones first, then hues spaced by the golden angle with alternating
    /// lightness, so even a combo of 40 map mods gets no repeats (and all stay readable on the paper background).
    /// </summary>
    public static Color ModColor(int i)
    {
        if (i < ModColors.Count)
        {
            return ModColors[i];
        }

        double hue = (i * 137.508) % 360, light = i % 2 == 0 ? 0.40 : 0.55, sat = 0.70;
        double c = (1 - Math.Abs((2 * light) - 1)) * sat, x = c * (1 - Math.Abs((hue / 60 % 2) - 1)), m = light - (c / 2);
        var (r, g, b) = (hue / 60) switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    /// <summary>Hand-picked, clearly different colours for the mods that add the most roads.</summary>
    private static readonly IReadOnlyList<Color> ModColors =
    [
        Color.FromRgb(0xD9, 0x48, 0x1E), Color.FromRgb(0x2A, 0x6F, 0xDB), Color.FromRgb(0x2E, 0x9E, 0x44), Color.FromRgb(0x8E, 0x44, 0xAD),
        Color.FromRgb(0xD4, 0xA0, 0x00), Color.FromRgb(0x00, 0x99, 0x9A), Color.FromRgb(0xC2, 0x18, 0x5B), Color.FromRgb(0x7A, 0x52, 0x30),
        Color.FromRgb(0x5C, 0x6B, 0xC0), Color.FromRgb(0x7C, 0xB3, 0x42), Color.FromRgb(0xF5, 0x7C, 0x00), Color.FromRgb(0x00, 0x83, 0x8F),
        Color.FromRgb(0xAD, 0x14, 0x57), Color.FromRgb(0x45, 0x5A, 0x64), Color.FromRgb(0x9E, 0x9D, 0x24), Color.FromRgb(0x6A, 0x1B, 0x9A),
    ];

    /// <summary>One mod against what's below it: others grey, removed blue dashed, added red, hidden cells purple.</summary>
    public static MapScene FromComparison(MapComparison c) => new(
        c,
        [
            new MapSceneLayer(c.BackgroundRoads, c.BackgroundPrefabs, OtherRoad, 1.0, 1.2),
            new MapSceneLayer(c.RemovedRoads, [], RemovedRoad, 2.0, 1.0, Dashed: true, AlwaysShow: true),
            new MapSceneLayer(c.AddedRoads, c.AddedPrefabs, AddedRoad, 2.2, 1.6, AlwaysShow: true),
        ],
        c.HiddenCells,
        c.Cities.Select(x => new MapLabel(x.Name, x.Position, x.Added, x.Added ? AddedLabel : Label)).ToList(),
        c.Focus ?? c.Extent,
        c.Extent);
}
