using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Dispatch.App.Resources;
using Dispatch.Core.Games;
using Dispatch.Core.Maps;
using Dispatch.Core.Mods.Packages;
using Dispatch.Core.Profiles;
using Dispatch.Core.Sii;
using Serilog;

namespace Dispatch.App.ViewModels;

/// <summary>What the Stats window reads: the profile, the game (for levels, currency and city names) and its mods.</summary>
public sealed record StatsRequest(ProfileInfo Profile, GameId Game, string? InstallDir, IReadOnlyList<MapSource> Mods, string MapName, string CacheDir);

public sealed record StatTile(string Label, string Value, string Detail = "");

public sealed record StatsDayBar(string Label, double RevenueHeight, double ProfitHeight, string Tooltip);

public sealed record StatsSkillRow(string Name, int Value);

public sealed record StatsTruckRow(string Model, string Plate, long Odometer, string OdometerText, double Wear, string WearText, string Garage, string Driver);

public sealed record StatsGarageRow(string City, int Slots, int Trucks, int Drivers, long Revenue, string RevenueText, long Profit, string ProfitText,
                                    double Productivity, string ProductivityText);

/// <summary>A hired driver. The save keeps no names (only "driver.342"), so a driver is shown by garage and record.</summary>
public sealed record StatsDriverRow(string Garage, int Level, long Experience, string ExperienceText, string Skills);

/// <summary>One finished job. Numbers come twice: as values (for sorting) and as text (for showing).</summary>
public sealed record StatsJobRow(long Finished, string FinishedText, string From, string To, string Cargo, double Mass, string MassText,
                                 int Km, string KmText, long Payment, string PaymentText, double PerKm, string PerKmText,
                                 double Damage, string DamageText, long Xp, string XpText, string Truck, string Kind);

/// <summary>The Stats window: company, money, fleet, garages and drivers from the profile's newest save. Read-only.</summary>
public sealed partial class StatsViewModel(StatsRequest request) : ObservableObject
{
    private const double ChartHeight = 150;

    public StatsRequest Request { get; } = request;

    public string Title => string.Format(CultureInfo.CurrentCulture, Strings.StatsTitle, Request.Profile.Name);

    [ObservableProperty]
    private string _header = request.Profile.Name;

    [ObservableProperty]
    private string _subtitle = Strings.StatsLoading;

    [ObservableProperty]
    private bool _isLoading = true;

    /// <summary>The save was read (the figures are shown).</summary>
    [ObservableProperty]
    private bool _hasStats;

    [ObservableProperty]
    private string _message = "";

    public ProfileStats? Stats { get; private set; }

    public ObservableCollection<StatTile> Tiles { get; } = [];

    public ObservableCollection<StatTile> Income { get; } = [];

    [ObservableProperty]
    private string _incomeHeader = "";

    public ObservableCollection<StatsDayBar> Days { get; } = [];

    public ObservableCollection<StatsSkillRow> Skills { get; } = [];

    public ObservableCollection<StatsTruckRow> Trucks { get; } = [];

    public ObservableCollection<StatsGarageRow> Garages { get; } = [];

    public ObservableCollection<StatsDriverRow> Drivers { get; } = [];

    /// <summary>Every job the player finished, newest first.</summary>
    public ObservableCollection<StatsJobRow> Jobs { get; } = [];

    /// <summary>Totals over the jobs (count, distance, earnings, favourite cargo and route…).</summary>
    public ObservableCollection<StatTile> JobTiles { get; } = [];

    [ObservableProperty]
    private string _jobsTab = "";

    [ObservableProperty]
    private bool _hasJobs;

    [ObservableProperty]
    private string _trucksTab = "";

    [ObservableProperty]
    private string _garagesTab = "";

    [ObservableProperty]
    private string _driversTab = "";

    public async Task LoadAsync(CancellationToken cancel = default)
    {
        try
        {
            var (stats, cities, names) = await Task.Run(() => (ProfileStatsReader.Read(Request.Profile, ReadXp()), CityNames(cancel), GameNames(cancel)), cancel);
            if (stats is null)
            {
                Subtitle = "";
                Message = Strings.StatsNoSave;
                return;
            }

            Stats = stats;
            Func<string, string> city = t => t is { Length: > 0 } ? cities.GetValueOrDefault(t) ?? MapComparison.Readable(t) : "";
            Fill(stats, city);
            FillJobs(stats, city, names);
            HasStats = true;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            Log.Warning(ex, "Stats: the newest save couldn't be read");
            Subtitle = "";
            Message = string.Format(CultureInfo.CurrentCulture, Strings.StatsError, ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Experience per level from the game's own definitions (so ETS2 and ATS are both right).</summary>
    private XpLevels? ReadXp()
    {
        if (Request.InstallDir is not { } dir || !File.Exists(Path.Combine(dir, "def.scs")))
        {
            return null;
        }

        try
        {
            using var def = ModPackage.Open(Path.Combine(dir, "def.scs"));
            return def.TryReadFile("def/economy_data_xp.sii") is { } data ? XpLevels.Parse(SiiFileCodec.Decode(data).Text) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>City display names from the game and the profile's mods (the mods win), through the map cache.</summary>
    private Dictionary<string, string> CityNames(CancellationToken cancel)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var cache = new MapCache(Request.CacheDir);
        foreach (var source in MapLoading.Game(Request.InstallDir).Concat(Request.Mods))
        {
            foreach (var (token, name) in cache.Get(source.Path, Request.MapName, cancel).CityNames)
            {
                names[token] = name;
            }
        }

        return names;
    }

    /// <summary>Cargo and company names in the UI language, from the game and the profile's mods.</summary>
    private GameNames GameNames(CancellationToken cancel) =>
        Request.InstallDir is { } dir
            ? Dispatch.Core.Profiles.GameNames.Read(Dispatch.Core.Profiles.GameNames.GameArchives(dir).Concat(Request.Mods.Select(m => m.Path)),
                                                    Dispatch.Core.Profiles.GameNames.LocaleFolder(CultureInfo.CurrentUICulture.Name), cancel)
            : Dispatch.Core.Profiles.GameNames.Empty;

    private void FillJobs(ProfileStats s, Func<string, string> city, GameNames names)
    {
        var c = CultureInfo.CurrentCulture;
        string Company(string token) => token.Length == 0 ? "" : names.Company(token) ?? MapComparison.Readable(token);
        string Place(string company, string town) => company.Length == 0 ? city(town) : $"{Company(company)}, {city(town)}";
        string Cargo(string token) => names.Cargo(token) is { } n && !n.StartsWith("cn_", StringComparison.Ordinal) ? n : MapComparison.Readable(token);
        string Kind(JobStats j) => j.Kind switch
        {
            JobKind.CargoMarket => Strings.JobKindCargo,
            JobKind.QuickJob => Strings.JobKindQuick,
            JobKind.SpecialTransport => Strings.JobKindSpecial,
            _ => j.Market,
        };

        Jobs.Clear();
        foreach (var j in s.Jobs.Reverse())
        {
            double perKm = j.PlannedKm > 0 ? (double)j.Revenue / j.PlannedKm : 0;
            var time = TimeSpan.FromMinutes(j.EndMinute % 1440);
            Jobs.Add(new StatsJobRow(j.EndMinute, string.Format(c, Strings.JobsDay, j.Day, time.ToString(@"hh\:mm", c)),
                                     Place(j.SourceCompany, j.SourceCity), Place(j.TargetCompany, j.TargetCity), Cargo(j.Cargo),
                                     j.MassKg / 1000, j.MassKg > 0 ? string.Format(c, "{0:0.0}", j.MassKg / 1000) : "",
                                     j.PlannedKm, j.PlannedKm.ToString("N0", c), j.Revenue, Money(j.Revenue), perKm, Money((long)Math.Round(perKm)),
                                     j.Damage, j.Damage > 0 ? string.Format(c, "{0:0.0} %", j.Damage * 100) : "–",
                                     j.Xp, j.Xp.ToString("N0", c), TruckName(j.Truck), Kind(j)));
        }

        JobTiles.Clear();
        HasJobs = s.Jobs.Count > 0;
        JobsTab = string.Format(c, Strings.JobsTab, s.Jobs.Count);
        if (!HasJobs)
        {
            return;
        }

        long km = s.Jobs.Sum(j => (long)j.PlannedKm), earned = s.Jobs.Sum(j => j.Revenue);
        JobTiles.Add(new StatTile(Strings.JobsCount, s.Jobs.Count.ToString("N0", c)));
        JobTiles.Add(new StatTile(Strings.JobsDistance, string.Format(c, "{0:N0} km", km)));
        JobTiles.Add(new StatTile(Strings.JobsEarned, Money(earned), string.Format(c, Strings.JobsPerKm, Money(km > 0 ? earned / km : 0))));
        JobTiles.Add(new StatTile(Strings.JobsXp, s.Jobs.Sum(j => j.Xp).ToString("N0", c)));
        JobTiles.Add(new StatTile(Strings.JobsClean, string.Format(c, "{0:P0}", (double)s.Jobs.Count(j => j.Damage <= 0) / s.Jobs.Count)));
        var longest = s.Jobs.MaxBy(j => j.PlannedKm)!;
        JobTiles.Add(new StatTile(Strings.JobsLongest, string.Format(c, "{0:N0} km", longest.PlannedKm), $"{city(longest.SourceCity)} → {city(longest.TargetCity)}"));
        var cargo = s.Jobs.GroupBy(j => j.Cargo).MaxBy(g => g.Count())!;
        JobTiles.Add(new StatTile(Strings.JobsTopCargo, Cargo(cargo.Key), string.Format(c, Strings.JobsTimes, cargo.Count())));
        var route = s.Jobs.Where(j => j.SourceCity.Length > 0).GroupBy(j => (j.SourceCity, j.TargetCity)).MaxBy(g => g.Count());
        if (route is not null)
        {
            JobTiles.Add(new StatTile(Strings.JobsTopRoute, $"{city(route.Key.SourceCity)} → {city(route.Key.TargetCity)}", string.Format(c, Strings.JobsTimes, route.Count())));
        }
    }

    private string Money(long amount) => Request.Game == GameId.Ats
        ? string.Format(CultureInfo.CurrentCulture, amount < 0 ? "−${0:N0}" : "${0:N0}", Math.Abs(amount))
        : string.Format(CultureInfo.CurrentCulture, "{0:N0} €", amount);

    private void Fill(ProfileStats s, Func<string, string> city)
    {
        var c = CultureInfo.CurrentCulture;
        Header = s.CompanyName is { Length: > 0 } company ? company : Request.Profile.Name;
        Subtitle = string.Format(c, Strings.StatsFromSave, s.SaveLabel, s.SaveWriteUtc.ToLocalTime().ToString("g", c));

        Tiles.Clear();
        Tiles.Add(new StatTile(Strings.StatMoney, Money(s.Money), s.Loans > 0 ? string.Format(c, Strings.StatLoansDetail, s.Loans, Money(s.LoanTotal)) : ""));
        Tiles.Add(new StatTile(Strings.StatLevel, s.Level.ToString(c), string.Format(c, Strings.StatXp, s.Experience)));
        Tiles.Add(new StatTile(Strings.StatDistance, string.Format(c, "{0:N0} km", s.DistanceKm)));
        Tiles.Add(new StatTile(Strings.StatFleetKm, string.Format(c, "{0:N0} km", s.FleetKm)));
        Tiles.Add(new StatTile(Strings.StatTrucks, s.Trucks.Count.ToString("N0", c), string.Format(c, Strings.StatTrailersDetail, s.Trailers)));
        Tiles.Add(new StatTile(Strings.StatGarages, s.Garages.Count.ToString("N0", c), string.Format(c, Strings.StatGaragesDetail, s.Garages.Sum(g => g.Slots))));
        Tiles.Add(new StatTile(Strings.StatDrivers, s.Drivers.Count.ToString("N0", c)));
        if (s.HqCity is { Length: > 0 } hq)
        {
            Tiles.Add(new StatTile(Strings.StatHq, city(hq)));
        }

        Tiles.Add(new StatTile(Strings.StatDays, (s.GameMinutes / 1440).ToString("N0", c)));
        Tiles.Add(new StatTile(Strings.StatCities, s.VisitedCities.ToString("N0", c), string.Format(c, Strings.StatExploredDetail, s.ExploredRatio)));
        Tiles.Add(new StatTile(Strings.StatDealers, s.UnlockedDealers.ToString("N0", c), string.Format(c, Strings.StatRecruitDetail, s.UnlockedRecruitments)));
        Tiles.Add(new StatTile(Strings.StatFines, Money(s.FinesTotal), string.Format(c, Strings.StatFinesDetail, s.Fines)));
        Tiles.Add(new StatTile(Strings.StatFerries, s.FerryTrips.ToString("N0", c)));
        if (s.CreatedUtc is { } created)
        {
            Tiles.Add(new StatTile(Strings.StatCreated, created.ToLocalTime().ToString("d", c)));
        }

        // The company's books for the days the game keeps.
        IncomeHeader = string.Format(c, Strings.StatsIncomeHeader, s.Days.Count);
        Income.Clear();
        Income.Add(new StatTile(Strings.StatRevenue, Money(s.Revenue)));
        Income.Add(new StatTile(Strings.StatCosts, Money(s.Revenue - s.Profit),
                                string.Format(c, Strings.StatCostsDetail, Money(s.Days.Sum(d => d.Wages)), Money(s.Days.Sum(d => d.Maintenance)), Money(s.Days.Sum(d => d.Fuel)))));
        Income.Add(new StatTile(Strings.StatProfit, Money(s.Profit)));
        Days.Clear();
        long max = Math.Max(1, s.Days.Count == 0 ? 1 : s.Days.Max(d => d.Revenue));
        foreach (var d in s.Days)
        {
            Days.Add(new StatsDayBar(d.Day.ToString(c), ChartHeight * d.Revenue / max, ChartHeight * Math.Max(0, d.Profit) / max,
                                     string.Format(c, Strings.StatsDayTooltip, d.Day, Money(d.Revenue), Money(d.Profit), d.DistanceKm)));
        }

        Skills.Clear();
        Skills.Add(new StatsSkillRow(Strings.SkillAdr, s.Skills.Adr));
        Skills.Add(new StatsSkillRow(Strings.SkillLongDistance, s.Skills.LongDistance));
        Skills.Add(new StatsSkillRow(Strings.SkillHighValue, s.Skills.HighValue));
        Skills.Add(new StatsSkillRow(Strings.SkillFragile, s.Skills.Fragile));
        Skills.Add(new StatsSkillRow(Strings.SkillJustInTime, s.Skills.JustInTime));
        Skills.Add(new StatsSkillRow(Strings.SkillEco, s.Skills.EcoDriving));

        Trucks.Clear();
        foreach (var t in s.Trucks.OrderByDescending(t => t.OdometerKm))
        {
            Trucks.Add(new StatsTruckRow(TruckName(t.Model), t.Plate, t.OdometerKm, t.OdometerKm.ToString("N0", c), t.WearPercent,
                                         string.Format(c, "{0:0} %", t.WearPercent), city(t.GarageCity ?? ""), city(t.DriverHometown ?? "")));
        }

        Garages.Clear();
        foreach (var g in s.Garages)
        {
            Garages.Add(new StatsGarageRow(city(g.City), g.Slots, g.Trucks, g.Drivers, g.Revenue, Money(g.Revenue), g.Profit, Money(g.Profit),
                                           g.Productivity, string.Format(c, "{0:0} %", g.Productivity * 100)));
        }

        Drivers.Clear();
        foreach (var d in s.Drivers)
        {
            var k = d.Skills;
            Drivers.Add(new StatsDriverRow(city(d.GarageCity ?? ""), d.Level, d.Experience, d.Experience.ToString("N0", c),
                                           string.Format(c, "{0} · {1} · {2} · {3} · {4} · {5}", k.Adr, k.LongDistance, k.HighValue, k.Fragile, k.JustInTime, k.EcoDriving)));
        }

        TrucksTab = string.Format(c, Strings.StatsTrucksTab, Trucks.Count);
        GaragesTab = string.Format(c, Strings.StatsGaragesTab, Garages.Count);
        DriversTab = string.Format(c, Strings.StatsDriversTab, Drivers.Count);
    }

    private static readonly Dictionary<string, string> Brands = new(StringComparer.Ordinal)
    {
        ["daf"] = "DAF", ["man"] = "MAN", ["mercedes"] = "Mercedes-Benz", ["scania"] = "Scania", ["volvo"] = "Volvo",
        ["renault"] = "Renault", ["iveco"] = "Iveco", ["kenworth"] = "Kenworth", ["peterbilt"] = "Peterbilt",
        ["westernstar"] = "Western Star", ["freightliner"] = "Freightliner", ["intnational"] = "International", ["mack"] = "Mack",
    };

    /// <summary>"scania.s_2016" → "Scania S 2016", "man.tgx_2020" → "MAN TGX 2020" (the game's own names are spread over many archives).</summary>
    public static string TruckName(string token)
    {
        if (token.Length == 0)
        {
            return "?";
        }

        string[] parts = token.Split('.', 2);
        string brand = Brands.GetValueOrDefault(parts[0]) ?? MapComparison.Readable(parts[0]);
        if (parts.Length == 1)
        {
            return brand;
        }

        // "actros2014" → "actros_2014": a model year stuck to the name gets its own word.
        string model = System.Text.RegularExpressions.Regex.Replace(parts[1], "(?<=[a-z])(?=\\d{4})", "_");
        var words = model.Split('_', StringSplitOptions.RemoveEmptyEntries)
                            .Select(w => w.Length <= 3 && w.Any(char.IsAsciiLetter) ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..]);
        return brand + " " + string.Join(' ', words);
    }
}
