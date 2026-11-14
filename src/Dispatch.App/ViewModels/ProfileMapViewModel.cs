using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Resources;
using Dispatch.Core.Maps;
using Serilog;

namespace Dispatch.App.ViewModels;

/// <summary>A profile's whole road network.</summary>
/// <param name="Mods">The installed mods of the load order, bottom first.</param>
/// <param name="MapName">The map the profile plays; <paramref name="DefaultMapName"/> is the game's own.</param>
/// <param name="NotInstalled">Mods in the load order that aren't installed (left out).</param>
public sealed record ProfileMapRequest(string ProfileName, IReadOnlyList<MapSource> Mods, string? InstallDir, string MapName,
                                       string DefaultMapName, string CacheDir, int NotInstalled);

/// <summary>One row of the legend: a mod (or the game and its DLCs) with its colour.</summary>
public sealed partial class ProfileMapLegendItem(string name, Color color, string detail, MapRect? bounds, bool isGame,
                                                 IReadOnlyList<int> layers, bool hasRoads) : ObservableObject
{
    public string Name { get; } = name;

    public Color Color { get; } = color;

    public Brush Swatch { get; } = Frozen(color);

    public string Detail { get; } = detail;

    /// <summary>Where its roads are (null: none shown, so it can't be zoomed to).</summary>
    public MapRect? Bounds { get; } = bounds;

    public bool CanZoom => Bounds is not null;

    public bool IsGame { get; } = isGame;

    /// <summary>Its roads are on the map (otherwise the row is dimmed).</summary>
    public bool HasRoads { get; } = hasRoads;

    /// <summary>The network layers this row stands for (the game's archives share one row).</summary>
    public IReadOnlyList<int> Layers { get; } = layers;

    [ObservableProperty]
    private bool _isVisible = true;

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>The Profile map window: every road the profile's game and mods give, coloured by the mod that added it.</summary>
public sealed partial class ProfileMapViewModel(ProfileMapRequest request) : ObservableObject
{
    private static readonly Color GameColor = Color.FromRgb(0xA4, 0xA4, 0xA4);
    private MapNetwork? _network;

    public ProfileMapRequest Request { get; } = request;

    public string Title => string.Format(CultureInfo.CurrentCulture, Strings.ProfileMapTitle, Request.ProfileName);

    public string Header => Request.ProfileName;

    public string Subtitle => string.Format(CultureInfo.CurrentCulture, Strings.ProfileMapSubtitle, Request.Mods.Count);

    public ObservableCollection<MapNote> Notes { get; } = [];

    /// <summary>Top of the load order first, the game last; only mods with map sectors.</summary>
    public ObservableCollection<ProfileMapLegendItem> Legend { get; } = [];

    public MapNetwork? Network => _network;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _failed;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMax = 1;

    [ObservableProperty]
    private MapScene? _scene;

    public bool HasMap => Scene is { Extent: not null };

    partial void OnSceneChanged(MapScene? value) => OnPropertyChanged(nameof(HasMap));

    public async Task LoadAsync(CancellationToken cancel = default)
    {
        var game = MapLoading.Game(Request.InstallDir);
        ProgressMax = game.Count + Request.Mods.Count;
        var progress = new Progress<int>(done =>
        {
            ProgressValue = done;
            ProgressText = string.Format(CultureInfo.CurrentCulture, Strings.MapLoading, done, (int)ProgressMax);
        });
        ((IProgress<int>)progress).Report(0);

        try
        {
            var network = await Task.Run(() =>
            {
                var cache = new MapCache(Request.CacheDir);
                int done = 0;
                var started = DateTime.UtcNow;
                var layers = MapLoading.Read(cache, Request.MapName, game.Concat(Request.Mods), () => ((IProgress<int>)progress).Report(++done), cancel);
                var n = MapNetwork.Build(layers);
                Log.Information("Profile map {Map}: {Layers} layers read in {Seconds:F1}s; {Roads} roads",
                                Request.MapName, layers.Count, (DateTime.UtcNow - started).TotalSeconds, n.RoadsByLayer.Sum(l => l.Count));
                return n;
            }, cancel);
            _network = network;
            Fill(network, game.Count);
            Scene = BuildScene();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warning(ex, "The profile map couldn't be read");
            Failed = true;
            Notes.Add(new MapNote(string.Format(CultureInfo.CurrentCulture, Strings.MapError, ex.Message), true));
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ShowAll()
    {
        foreach (var item in Legend)
        {
            item.IsVisible = true;
        }
    }

    [RelayCommand]
    private void ModsOnly()
    {
        foreach (var item in Legend)
        {
            item.IsVisible = !item.IsGame;
        }
    }

    private void Fill(MapNetwork n, int gameLayers)
    {
        var culture = CultureInfo.CurrentCulture;
        Notes.Clear();
        Legend.Clear();
        if (!string.Equals(Request.MapName, Request.DefaultMapName, StringComparison.OrdinalIgnoreCase))
        {
            Notes.Add(new MapNote(string.Format(culture, Strings.ProfileMapSeparate, Request.MapName), false));
        }

        var game = n.Layers.Take(gameLayers).ToList();
        var mods = n.Layers.Skip(gameLayers).ToList();
        double totalKm = n.Layers.Sum(l => l.Km);
        double modKm = mods.Sum(l => l.Km);
        Notes.Add(new MapNote(string.Format(culture, Strings.ProfileMapTotals, n.Layers.Sum(l => l.Roads), totalKm,
                                            mods.Sum(l => l.Roads), modKm), false));

        int noMap = mods.Count(l => l.Sectors == 0 && l.UnreadableSectors == 0 && !l.IsUnreadable);
        if (noMap > 0)
        {
            Notes.Add(new MapNote(string.Format(culture, Strings.ProfileMapNoMapMods, noMap), false));
        }

        if (Request.NotInstalled > 0)
        {
            Notes.Add(new MapNote(string.Format(culture, Strings.ProfileMapMissing, Request.NotInstalled), true));
        }

        if (Request.InstallDir is null)
        {
            Notes.Add(new MapNote(Strings.MapNoGame, true));
        }

        // Colours: the mods that add the most roads get the most distinct ones.
        var colours = mods.Where(l => l.Roads > 0).OrderByDescending(l => l.Roads)
                          .Select((l, i) => (l.Index, Color: MapScene.ModColor(i))).ToDictionary(x => x.Index, x => x.Color);

        // Mods top first (as in the load order).
        foreach (var l in Enumerable.Reverse(mods))
        {
            if (l.Sectors == 0 && l.UnreadableSectors == 0 && !l.IsUnreadable)
            {
                continue; // doesn't touch the map
            }

            string detail = l switch
            {
                { IsUnreadable: true } => Strings.LegendProtected,
                { Roads: 0, Sectors: > 0, SectorsShown: 0 } => Strings.LegendCovered,
                { SectorsShown: var shown, Sectors: var all } when shown < all =>
                    string.Format(culture, Strings.LegendRoadsCovered, l.Roads, l.Km, all - shown, all),
                _ => string.Format(culture, Strings.LegendRoads, l.Roads, l.Km),
            };
            var color = colours.TryGetValue(l.Index, out var c) ? c : Color.FromRgb(0xB0, 0xB0, 0xB0);
            Legend.Add(Watch(new ProfileMapLegendItem(l.Name, color, detail, l.Bounds, false, [l.Index], l.Roads > 0)));
        }

        MapRect? gameBounds = null;
        foreach (var b in game.Select(l => l.Bounds).OfType<MapRect>())
        {
            gameBounds = gameBounds is { } x ? x.Union(b) : b;
        }

        if (game.Count > 0)
        {
            Legend.Add(Watch(new ProfileMapLegendItem(Strings.LegendGame, GameColor,
                                                      string.Format(culture, Strings.LegendRoads, game.Sum(l => l.Roads), game.Sum(l => l.Km)),
                                                      gameBounds, true, game.Select(l => l.Index).ToList(), game.Any(l => l.Roads > 0))));
        }
    }

    private ProfileMapLegendItem Watch(ProfileMapLegendItem item)
    {
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileMapLegendItem.IsVisible))
            {
                Scene = BuildScene();
            }
        };
        return item;
    }

    /// <summary>The game's roads first (grey), then each visible mod from the bottom of the load order up.</summary>
    private MapScene? BuildScene()
    {
        if (_network is not { } n)
        {
            return null;
        }

        var layers = new List<MapSceneLayer>();
        var owners = new Dictionary<int, ProfileMapLegendItem>();
        foreach (var item in Legend.Reverse())
        {
            foreach (int i in item.Layers)
            {
                owners[i] = item;
            }

            if (!item.IsVisible || !item.HasRoads)
            {
                continue;
            }

            var roads = item.Layers.SelectMany(i => n.RoadsByLayer[i]).ToList();
            var prefabs = item.Layers.SelectMany(i => n.PrefabsByLayer[i]).ToList();
            layers.Add(item.IsGame
                ? new MapSceneLayer(roads, prefabs, item.Color, 1.0, 1.2)
                : new MapSceneLayer(roads, prefabs, item.Color, 1.7, 1.6));
        }

        var labels = n.Cities.Select(c => owners.TryGetValue(c.Owner, out var o) && !o.IsGame && o.IsVisible
            ? new MapLabel(c.Name, c.Position, true, Darker(o.Color))
            : new MapLabel(c.Name, c.Position, false, MapScene.Label)).ToList();

        MapRect? modBounds = null;
        foreach (var b in Legend.Where(l => !l.IsGame).Select(l => l.Bounds).OfType<MapRect>())
        {
            modBounds = modBounds is { } x ? x.Union(b) : b;
        }

        return new MapScene(n, layers, [], labels, n.Extent, n.Extent ?? modBounds);
    }

    private static Color Darker(Color c) => Color.FromRgb((byte)(c.R * 0.72), (byte)(c.G * 0.72), (byte)(c.B * 0.72));
}
