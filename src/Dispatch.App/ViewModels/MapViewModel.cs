using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Dispatch.App.Resources;
using Dispatch.Core.Maps;
using Serilog;

namespace Dispatch.App.ViewModels;

/// <summary>An archive to stack under or over the mod: a mod's name and package path.</summary>
public sealed record MapSource(string Name, string Path);

/// <summary>What to show: one mod, compared with the game and (from a profile) the mods below and above it.</summary>
/// <param name="Below">Mods loaded below it, bottom first.</param>
/// <param name="Above">Mods loaded above it, top first.</param>
/// <param name="ProfileMapName">The map the profile plays ("europe", "grandutopia"); null: the mod's own map.</param>
/// <param name="AddedOnTop">The mod isn't in the profile: it's shown as if added at the top of the load order.</param>
public sealed record MapRequest(MapSource Mod, string? ProfileName, IReadOnlyList<MapSource> Below, IReadOnlyList<MapSource> Above,
                                string? InstallDir, string MapName, string CacheDir, string? ProfileMapName = null, bool AddedOnTop = false);

/// <summary>A line of the summary above the map.</summary>
public sealed record MapNote(string Text, bool IsWarning);

/// <summary>Reading map layers through the cache; shared by the Map and Profile map windows.</summary>
internal static class MapLoading
{
    /// <summary>The game's archives as layers: "Base game" for base/def/base_map, "DLC north" for dlc_north.scs.</summary>
    public static List<MapSource> Game(string? installDir) => installDir is null
        ? []
        : MapArchiveReader.GameArchives(installDir).Select(p => new MapSource(GameLayerName(p), p)).ToList();

    private static string GameLayerName(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        return stem.StartsWith("dlc_", StringComparison.OrdinalIgnoreCase)
            ? string.Format(CultureInfo.CurrentCulture, Strings.MapDlc, stem[4..])
            : Strings.MapBaseGame;
    }

    /// <summary>Reads each source (cached), reporting one step per archive.</summary>
    public static List<MapLayer> Read(MapCache cache, string mapName, IEnumerable<MapSource> sources, Action step, CancellationToken cancel) =>
        sources.Select(s =>
        {
            var layer = new MapLayer(s.Name, cache.Get(s.Path, mapName, cancel));
            step();
            return layer;
        }).ToList();
}

/// <summary>The Map window: the roads one map mod adds and removes (Core: <see cref="MapComparison"/>).</summary>
public sealed partial class MapViewModel(MapRequest request) : ObservableObject
{
    public MapRequest Request { get; } = request;

    public string Title => string.Format(CultureInfo.CurrentCulture, Strings.MapTitle, Request.Mod.Name);

    public string Header => Request.Mod.Name;

    public string ComparedWith => Request switch
    {
        { ProfileName: { } profile, AddedOnTop: true } => string.Format(CultureInfo.CurrentCulture, Strings.MapComparedProfileTop, Request.Below.Count, profile),
        { ProfileName: { } profile } => string.Format(CultureInfo.CurrentCulture, Strings.MapComparedProfile, Request.Below.Count, profile),
        _ => Strings.MapComparedBase,
    };

    public ObservableCollection<MapNote> Notes { get; } = [];

    [ObservableProperty]
    private bool _isLoading = true;

    /// <summary>Reading failed (the reason is in <see cref="Notes"/>).</summary>
    [ObservableProperty]
    private bool _failed;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMax = 1;

    /// <summary>Set once loading finished.</summary>
    public MapComparison? Comparison { get; private set; }

    /// <summary>What the map control draws.</summary>
    [ObservableProperty]
    private MapScene? _scene;

    /// <summary>True when there's something to draw (the zoom buttons and legend are shown).</summary>
    public bool HasMap => Scene is { Extent: not null };

    partial void OnSceneChanged(MapScene? value) => OnPropertyChanged(nameof(HasMap));

    /// <summary>Reads everything (through the cache) on a background thread, then fills the summary.</summary>
    public async Task LoadAsync(CancellationToken cancel = default)
    {
        var game = MapLoading.Game(Request.InstallDir);
        ProgressMax = game.Count + Request.Below.Count + Request.Above.Count + 1;
        var progress = new Progress<int>(done =>
        {
            ProgressValue = done;
            ProgressText = string.Format(CultureInfo.CurrentCulture, Strings.MapLoading, done, (int)ProgressMax);
        });
        ((IProgress<int>)progress).Report(0);

        try
        {
            var (comparison, unreadableOthers, mapName, modMap) = await Task.Run(() => Build(game, progress, cancel), cancel);
            Comparison = comparison;
            Fill(comparison, unreadableOthers, mapName, modMap);
            Scene = MapScene.FromComparison(comparison);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warning(ex, "The map for {Mod} couldn't be read", Request.Mod.Path);
            Failed = true;
            Notes.Add(new MapNote(string.Format(CultureInfo.CurrentCulture, Strings.MapError, ex.Message), true));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private (MapComparison Comparison, int UnreadableOthers, string MapName, string ModMap) Build(
        List<MapSource> game, IProgress<int> progress, CancellationToken cancel)
    {
        var cache = new MapCache(Request.CacheDir);
        // The profile's map when there is one; otherwise the mod's own (the game's, or a separate map like Grand Utopia).
        string modMap = MapArchiveReader.FindMapName(Request.Mod.Path, Request.MapName);
        string mapName = Request.ProfileMapName ?? modMap;
        int done = 0;
        void Step() => progress.Report(++done);

        var started = DateTime.UtcNow;
        var below = MapLoading.Read(cache, mapName, game, Step, cancel);
        var mods = MapLoading.Read(cache, mapName, Request.Below, Step, cancel);
        below.AddRange(mods);
        var above = MapLoading.Read(cache, mapName, Request.Above, Step, cancel);
        var mod = MapLoading.Read(cache, mapName, [Request.Mod], Step, cancel)[0];
        var comparison = MapComparison.Compare(mod, below, above);
        Log.Information("Map {Map} for {Mod}: {Layers} layers read in {Seconds:F1}s; {Added} roads added, {Removed} removed",
                        mapName, Request.Mod.Path, done, (DateTime.UtcNow - started).TotalSeconds, comparison.AddedRoads.Count, comparison.RemovedRoads.Count);
        int unreadable = mods.Concat(above).Count(l => l.Map.IsUnreadable);
        return (comparison, unreadable, mapName, modMap);
    }

    private void Fill(MapComparison c, int unreadableOthers, string mapName, string modMap)
    {
        var culture = CultureInfo.CurrentCulture;
        Notes.Clear();
        bool otherMap = !string.Equals(mapName, modMap, StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(mapName, Request.MapName, StringComparison.OrdinalIgnoreCase) && !otherMap)
        {
            Notes.Add(new MapNote(string.Format(culture, Strings.MapSeparate, mapName), false));
        }

        if (c.ModUnreadable)
        {
            Notes.Add(new MapNote(Strings.MapModUnreadable, true));
        }
        else if (c.NewSectors + c.ReplacedSectors == 0 && c.UnreadableSectors == 0)
        {
            Notes.Add(otherMap
                ? new MapNote(string.Format(culture, Strings.MapOtherMap, modMap, Request.ProfileName, mapName), true)
                : new MapNote(Strings.MapNoMap, false));
        }
        else
        {
            Notes.Add(new MapNote(c.AddedRoads.Count + c.RemovedRoads.Count == 0
                ? Strings.MapNoChanges
                : string.Format(culture, Strings.MapAdds, c.AddedRoads.Count, c.AddedKm, c.AddedCities), false));
            Notes.Add(new MapNote(string.Format(culture, Strings.MapSectors, c.NewSectors + c.ReplacedSectors, c.NewSectors, c.ReplacedSectors), false));
        }

        if (c.RemovedRoads.Count > 0)
        {
            Notes.Add(new MapNote(string.Format(culture, Strings.MapRemoves, c.RemovedRoads.Count, List(c.RemovedFrom)), true));
        }

        if (c.HiddenBy.Count > 0)
        {
            Notes.Add(new MapNote(string.Format(culture, Strings.MapHidden, c.HiddenCells.Count, List(c.HiddenBy)), true));
        }

        if (c.UnreadableSectors > 0 && !c.ModUnreadable)
        {
            Notes.Add(new MapNote(string.Format(culture, Strings.MapUnreadableSectors, c.UnreadableSectors), false));
        }

        if (unreadableOthers > 0)
        {
            Notes.Add(new MapNote(string.Format(culture, Strings.MapUnreadableOthers, unreadableOthers), false));
        }

        if (Request.InstallDir is null)
        {
            Notes.Add(new MapNote(Strings.MapNoGame, true));
        }
    }

    /// <summary>"ProMods (1,276), Base game (12)"; at most four names, then "3 more".</summary>
    internal static string List(IReadOnlyList<MapLayerCount> counts)
    {
        var shown = counts.Take(4).Select(c => string.Format(CultureInfo.CurrentCulture, "{0} ({1:N0})", c.Name, c.Count));
        return string.Join(", ", counts.Count > 4 ? shown.Append(string.Format(CultureInfo.CurrentCulture, Strings.MapMore, counts.Count - 4)) : shown);
    }
}
